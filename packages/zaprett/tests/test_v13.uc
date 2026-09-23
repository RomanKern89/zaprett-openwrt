'use strict';

// Contract v1.3 §14.5–§14.6 and the other v1.3 changes that live in older modules: download limits and
// parallelism, https-only repository URL, the dry-run cache, --apply-if-better, IPv6 of the WAN, English
// metadata of presets and bundle items.
import * as fs from 'fs';
import * as T from 'ztest';
import { P, set_paths, uniq_name, mkdir_p, sha256_file, download_concurrency, meminfo_mib } from 'zaprett.util';
import * as V from 'zaprett.validate';
import * as C from 'zaprett.config';
import * as S from 'zaprett.store';
import * as G from 'zaprett.strategy';
import * as N from 'zaprett.nft';
import * as R from 'zaprett.repo';
import * as SRC from 'zaprett.sources';
import * as TS from 'zaprett.tester';
import * as CMD from 'zaprett.commands';

T.begin('v13');
T.selfcheck();
let W = T.sandbox('v13');

/* ---------- fake downloader: records every call, '@big' = what probe.sh leaves after `ulimit -f` ---------- */
let served = {}, calls = [], dirs = [];
function fake_probe(tasks, opts) {
	let dir = uniq_name(P.tmp + '/probe');
	mkdir_p(dir);
	push(dirs, dir);
	push(calls, { n: length(tasks), opts: opts });
	let results = {};
	for (let t in tasks) {
		let body = dir + '/' + t.key + '.body';
		let c = served[t.url];
		if (c == null) {
			results[t.key] = { rc: 4, ms: 3, bytes: 0, body: null, err: 'Connection error: Connection failed\n', summary: 'x' };
			continue;
		}
		if (c == '@big') {
			fs.writefile(body, 'x');
			results[t.key] = { rc: 153, ms: 3, bytes: opts.max_bytes + 512, body: body, err: '', summary: '' };
			continue;
		}
		fs.writefile(body, c);
		results[t.key] = { rc: 0, ms: 3, bytes: length(c), body: body, err: '', summary: '' };
	}
	return { dir: dir, results: results, rc: 0 };
}
let dirs_left = () => filter(dirs, (d) => fs.stat(d) != null);

/* ---------- repository: index, manifests, artifacts ---------- */
const IDX = 'https://repo.example/index.json';
let rcfg = C.normalize({}, { url: IDX }, null, null);
served[IDX] = '@big';
let r = R.fetch(rcfg, null, { probe: fake_probe });
T.eq([ r.ok, r.error, calls[0]?.opts?.max_bytes ], [ false, 'too_large', 2097152 ], 'index larger than 2 MiB rejected; limit passed to the download');
T.eq(dirs_left(), [], 'temporary files of the rejected index removed');
calls = [];
served[IDX] = sprintf('%J', { schema: 1, items: [ { id: 'm-big', type: 'bin', manifest: 'https://repo.example/m-big.json' },
	{ id: 'm-ok', type: 'bin', manifest: 'https://repo.example/m-ok.json' } ] });
served['https://repo.example/m-big.json'] = '@big';
let art = 'fake payload\n';
let artf = W + '/art.bin';
fs.writefile(artf, art);
let art_sha = sha256_file(artf);
served['https://repo.example/m-ok.json'] = sprintf('%J', { schema: 1, id: 'm-ok', name: 'OK', version: '1.0', author: 'a', description: 'd',
	artifact: { url: 'https://repo.example/m-ok.bin', sha256: art_sha }, dependencies: [] });
r = R.fetch(rcfg, null, { probe: fake_probe });
T.ok(r.ok && r.errors == 1, 'index fetched, one manifest failed: ' + (r.message ?? ''));
T.eq([ calls[0]?.opts?.max_bytes, calls[1]?.opts?.max_bytes ], [ R.MAX_INDEX_BYTES, R.MAX_MANIFEST_BYTES ], 'index 2 MiB, manifests 64 KiB');
let cache = R.read_cache();
T.eq(map(cache.items, (e) => [ e.id, e.error ]), [ [ 'm-big', 'too_large' ], [ 'm-ok', null ] ], 'a manifest over 64 KiB is marked too_large');
T.eq(dirs_left(), [], 'temporary files of manifests removed');

// artifacts: limit and parallelism by available memory
calls = [];
served['https://repo.example/m-ok.bin'] = '@big';
r = R.install(rcfg, [ 'm-ok' ], null, { probe: fake_probe, mem_available_mib: 400 });
T.eq([ r.ok, r.error ], [ false, 'too_large' ], 'artifact over 32 MiB rejected: ' + (r.message ?? ''));
T.eq([ calls[0]?.opts?.max_bytes, calls[0]?.opts?.concurrency ], [ 33554432, 4 ], 'artifacts: 32 MiB limit, 4 in parallel');
T.eq(dirs_left(), [], 'temporary files of the rejected artifact removed');
T.ok(fs.stat(P.etc + '/files/bin/m-ok.bin') == null, 'nothing installed');
calls = [];
served['https://repo.example/m-ok.bin'] = art;
r = R.install(rcfg, [ 'm-ok' ], null, { probe: fake_probe, mem_available_mib: 60 });
T.ok(r.ok && fs.readfile(P.etc + '/files/bin/m-ok.bin') == art, 'negative control: an artifact within the limit installs: ' + (r.message ?? ''));
T.eq(calls[0]?.opts?.concurrency, 1, 'artifacts one at a time below 64 MiB available');
T.eq([ download_concurrency(4, 64, 64), download_concurrency(4, 64, 63), download_concurrency(4, 64, null) ], [ 4, 1, 4 ],
	'parallelism rule: < limit -> 1, unknown -> normal');

// subscriptions: 2 in parallel, 1 below 48 MiB
C.set_source('demo', { enabled: '1', name: 'Demo', type: 'list', url: 'https://example.org/demo.lst', min_entries: '1' });
served['https://example.org/demo.lst'] = 'a.com\n';
calls = [];
SRC.update([ 'demo' ], null, { probe: fake_probe, mem_available_mib: 200 });
SRC.update([ 'demo' ], null, { probe: fake_probe, mem_available_mib: 47 });
T.eq(map(calls, (c) => [ c.opts.concurrency, c.opts.max_bytes ]), [ [ 2, 16777216 ], [ 1, 16777216 ] ], 'subscriptions: 2 in parallel, 1 below 48 MiB');
T.write(W + '/meminfo', 'MemTotal:  65536 kB\nMemAvailable:    40960 kB\n');
set_paths({ meminfo: W + '/meminfo' });
T.eq([ meminfo_mib('MemTotal'), meminfo_mib('MemAvailable'), meminfo_mib('Nope') ], [ 64, 40, null ], 'meminfo parsed in MiB');
calls = [];
SRC.update([ 'demo' ], null, { probe: fake_probe });
T.eq(calls[0]?.opts?.concurrency, 1, 'subscriptions read MemAvailable from /proc/meminfo');

/* ---------- repo.url: https only ---------- */
T.ok(V.https_url_valid('https://a.b/i.json') && !V.https_url_valid('http://a.b/i.json') && V.url_valid('http://a.b/i.json'),
	'https_url_valid: only https; url_valid still accepts http for other users');
let hc = C.normalize({}, { url: 'http://mirror.example/index.json' }, null, null);
T.eq([ hc.repo.url, index(hc.bad_options, 'url') >= 0, index(hc.warnings, 'bad_config') >= 0 ], [ C.DEFAULTS.repo.url, true, true ],
	'http repository URL -> bad_config and the default URL');
T.eq(C.normalize({}, { url: 'https://mirror.example/index.json' }, null, null).warnings, [], 'negative control: an https mirror is fine');

/* ---------- IPv6 of the WAN ---------- */
let dump = { interface: [
	{ interface: 'wan', up: true, l3_device: 'eth1', route: [ { target: '0.0.0.0', mask: 0 } ] },
	{ interface: 'wan6', up: true, l3_device: 'eth1', route: [ { target: '::', mask: 0 } ] },
	{ interface: 'lan', up: true, l3_device: 'br-lan', route: [] } ] };
T.eq(N.wan_ipv6_default(dump, []), true, 'IPv6 default route found');
T.eq(N.wan_ipv6_default(dump, [ 'wan' ]), true, 'wan6 on the same device counts for wan');
T.eq(N.wan_ipv6_default(dump, [ 'lan' ]), false, 'negative control: another WAN');
dump.interface[1].up = false;
T.eq(N.wan_ipv6_default(dump, []), false, 'negative control: wan6 down');

/* ---------- dry-run cache ---------- */
if (T.NFQWS) {
	let bin = W + '/libexec/nfqws';
	system([ 'mv', bin, bin + '.real' ]);
	let wrapper = (extra) => fs.writefile(bin, '#!/bin/sh\necho x >> ' + W + '/dryrun.count\n' + extra + 'exec ' + bin + '.real "$@"\n');
	wrapper('');
	fs.chmod(bin, 493);
	let count = () => length(filter(split(fs.readfile(W + '/dryrun.count') ?? '', '\n'), (l) => l != ''));
	let dcfg = C.load();
	let g1 = G.generate(dcfg, {});
	T.ok(g1.ok && !g1.dry_run.cached && count() == 1, 'first generation runs the dry-run: ' + (g1.message ?? ''));
	let g2 = G.generate(dcfg, {});
	T.ok(g2.ok && g2.dry_run.cached && count() == 1, 'same arguments: second dry-run skipped');
	let q = json(sprintf('%J', dcfg));
	q.qnum = 201;
	G.generate(q, {});
	T.eq(count(), 2, 'negative control: changed arguments (qnum) are checked again');
	G.generate(q, {});
	T.eq(count(), 2, 'and cached after that');
	CMD.user_set('user-hosts', 'changed.example\nmore.example\n');
	G.generate(q, {});
	T.eq(count(), 3, 'a list file named in the arguments changed -> checked again');
	G.generate(q, { force_dry_run: true });
	T.eq(count(), 4, 'force_dry_run (zaprett check) always runs the engine');
	sleep(1100);
	wrapper('# new build\n');
	G.generate(q, {});
	T.eq(count(), 5, 'engine binary replaced -> checked again');
	let bad = G.generate(q, { text: '--dpi-desync=bogusmode\n', item: { id: 'user-x', source: 'user', dependencies: [] } });
	T.eq([ bad.ok, fs.stat(G.dry_run_cache_path()) ], [ false, null ], 'a failed dry-run drops the cache');
	G.generate(q, {});
	T.eq(count(), 7, 'after a failure the good arguments are checked again');
	T.eq(G.dry_run_key('nfqws2', [ '--x' ]), null, 'no binary -> no cache key');
	system([ 'mv', bin + '.real', bin ]);
}
else
	print('SKIP [v13] nfqws binary not given: dry-run cache not checked\n');

/* ---------- test --apply-if-better ---------- */
let idx = S.scan();
let acfg = C.normalize({ strategy: 'strategy-alt', lists: [ 'zaprett-youtube' ] }, null, { settle: '0' }, null);
T.eq(TS.candidates(idx, acfg, 'nfqws', { strategies: [ 'strategy-general' ], apply_if_better: true }, null).ids,
	[ 'strategy-alt', 'strategy-general' ], 'apply-if-better: the original strategy is always tested (first)');
T.eq(TS.candidates(idx, acfg, 'nfqws', { strategies: [ 'strategy-general' ] }, null).ids, [ 'strategy-general' ],
	'negative control: without the flag only the named ones');
let res = (id, ratio, st) => ({ id: id, status: st ?? 'done', ok: (ratio > 0) ? 1 : 0, ratio: ratio });
T.eq(TS.pick_better([ res('b', 1), res('a', 0.5) ], 'a'), 'b', 'strictly better -> applied');
T.eq(TS.pick_better([ res('b', 1), res('a', 1) ], 'a'), null, 'equal ratio -> not applied');
T.eq(TS.pick_better([ res('a', 1), res('b', 0.5) ], 'a'), null, 'original is the best -> not applied');
T.eq(TS.pick_better([ res('b', 0.5), res('a', 0, 'invalid') ], 'a'), 'b', 'original that did not run counts as 0');
T.eq(TS.pick_better([ res('b', 0) ], 'a'), null, 'nothing reachable -> nothing applied');
if (T.NFQWS) {
	system([ 'cp', T.ROOT + '/files/usr/share/zaprett/presets.json', P.presets ]);
	C.set({ strategy: 'strategy-alt', lists: [ 'zaprett-youtube' ] });
	let good = {};
	let hooks = {
		init_action: (a) => ({ rc: 0, output: '' }),
		wait_running: (want, ms) => ({ running: want, pid: 1 }),
		probe: (targets, c) => {
			let ov = json(fs.readfile(TS.override_path()) ?? 'null');
			let n = ov ? (good[ov.strategy] ?? 0) : 0;
			return { ok: n, total: length(targets), avg_ms: 10, targets: [] };
		}
	};
	good = { 'strategy-alt': 1, 'strategy-general': 3 };
	let ar = TS.run(acfg, { strategies: [ 'strategy-general' ], apply_if_better: true, hooks: hooks }, null);
	let rj = json(fs.readfile(TS.results_path()));
	T.eq([ ar.ok, ar.applied, rj.applied, C.load().strategy ], [ true, 'strategy-general', 'strategy-general', 'strategy-general' ],
		'better strategy applied: result, test-results.json and UCI');
	C.set({ strategy: 'strategy-alt' });
	good = { 'strategy-alt': 3, 'strategy-general': 3 };
	ar = TS.run(acfg, { strategies: [ 'strategy-general' ], apply_if_better: true, hooks: hooks }, null);
	T.eq([ ar.applied, json(fs.readfile(TS.results_path())).applied, C.load().strategy ], [ null, null, 'strategy-alt' ],
		'an equal strategy is not applied');
	good = { 'strategy-alt': 1, 'strategy-general': 3 };
	ar = TS.run(acfg, { strategies: [ 'strategy-general' ], hooks: hooks }, null);
	T.eq([ ar.applied, C.load().strategy ], [ null, 'strategy-alt' ], 'negative control: without the flag nothing is applied');
}
else
	print('SKIP [v13] nfqws binary not given: --apply-if-better run not checked\n');

/* ---------- English metadata (contract v1.3 §14.6) ---------- */
// Manifests of the package name files under /usr/share/zaprett/bundle (not under the unpacked copy), so they are
// parsed with that root and passed through describe() — the same code path as `items` on the router.
set_paths({ presets: T.ROOT + '/files/usr/share/zaprett/presets.json' });
let pidx = { items: {}, errors: [] };
for (let t in S.TYPE_ORDER)
	pidx.items[t] = {};
for (let t in S.LIST_TYPES) {
	let mdir = T.ROOT + '/files/usr/share/zaprett/bundle/manifests/' + S.TYPES[t].dir;
	for (let n in (fs.lsdir(mdir) ?? [])) {
		let id = substr(n, 0, length(n) - 5);
		let it = S.parse_manifest(json(fs.readfile(mdir + '/' + n)), t, 'bundle', id, '/usr/share/zaprett/bundle');
		if (T.ok(!it.error, 'package manifest parses: ' + n + ' ' + (it.error ?? '')))
			pidx.items[t][id] = it;
	}
}
let lists = S.describe(pidx, C.normalize({}, null, null, null), null);
T.ok(length(lists) >= 7, sprintf('bundle lists and ipsets: %d', length(lists)));
T.eq(filter(lists, (it) => type(it.name_en) != 'string' || it.name_en == '' || type(it.description_en) != 'string' || it.description_en == ''),
	[], 'every bundle list/ipset item has name_en and description_en in `items`');
let yt = filter(lists, (it) => it.id == 'zaprett-youtube')[0];
T.eq(yt?.name_en, 'YouTube', 'items passes name_en as is');
let plain = S.parse_manifest({ schema: 1, id: 'x', file: 'x.txt', sha256: art_sha }, 'list', 'repo', 'x', '/etc/zaprett');
T.eq([ plain.name_en, plain.description_en ], [ null, null ], 'negative control: a manifest without English fields gives null');
let pr = CMD.presets();
T.eq(filter(pr.services, (s) => type(s.name_en) != 'string' || type(s.description_en) != 'string' || type(s.note_en) != 'string'), [],
	'every preset service has name_en, description_en and note_en');
// Cyrillic letters are 0xD0/0xD1 lead bytes in UTF-8 (a regex class of Cyrillic would work on bytes here)
let cyr = (s) => {
	for (let i = 0; i < length(s); i++)
		if (ord(s, i) == 0xd0 || ord(s, i) == 0xd1)
			return true;
	return false;
};
T.ok(cyr('Сайт') && !cyr('Discord – voice'), 'Cyrillic check itself works (and is not fooled by an en dash)');
T.eq(map(filter(pr.services, (s) => cyr(s.name_en + s.description_en + s.note_en)), (s) => s.id), [], 'English texts have no Cyrillic');
T.eq(map(filter(lists, (it) => cyr(it.name_en + it.description_en)), (it) => it.id), [], 'English item texts have no Cyrillic');
T.eq(filter(values(pr.tiers ?? {}), (t) => t.name != null && t.name_en == null), [], 'tiers: name_en wherever there is a name');
T.eq(S.USER_LISTS['user-hosts'].name_en, 'My domains', 'user lists have English names');

exit(T.finish());
