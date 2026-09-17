'use strict';

import * as fs from 'fs';
import * as T from 'ztest';
import { P, uniq_name, mkdir_p } from 'zaprett.util';
import * as C from 'zaprett.config';
import * as S from 'zaprett.store';
import * as N from 'zaprett.net';
import * as SRC from 'zaprett.sources';

T.begin('sources');
T.selfcheck();
let W = T.sandbox('sources');

/* ---- normalization (contract v1.1 §4) ---- */
let n = SRC.normalize_text('hosts', chr(0xef, 0xbb, 0xbf) + 'Example.COM\r\n# comment\n\n  youtube.com  \n*.masked.org\nsp ace.com\nпример.рф\nxn--e1afmkfd.xn--p1ai\n;x\nbad..domain\n');
T.eq(n.text, 'example.com\nyoutube.com\nxn--e1afmkfd.xn--p1ai\n', 'hosts normalized: BOM/CR/space removed, lower case, punycode kept');
T.eq([ n.valid, n.invalid ], [ 3, 4 ], 'hosts: masks, inner spaces, non-punycode IDN and bad names counted invalid');
n = SRC.normalize_text('ipset', '1.2.3.4\n10.1.2.3/8\n2001:db8::/32\nnot-an-ip\n1.2.3.4/33\n');
T.eq([ n.text, n.valid, n.invalid ], [ '1.2.3.4\n10.0.0.0/8\n2001:db8::/32\n', 3, 2 ], 'ipset normalized and validated');
T.eq(SRC.normalize_text('hosts', '').text, '', 'empty text');
T.eq([ SRC.normalize_line('hosts', ' A.com\r'), SRC.normalize_line('hosts', '! adblock comment'), SRC.normalize_line('hosts', '*.a.com') ],
	[ 'a.com', '', null ], 'normalize_line: entry, skipped line, invalid line');

/* ---- streaming normalization: same result as in memory, without reading the whole file ---- */
mkdir_p(W + '/n');
let lines = [];
for (let i = 0; i < 12000; i++)
	push(lines, (i % 97 == 0) ? sprintf('Bad Domain %d', i) : ((i % 89 == 0) ? '# comment' : sprintf('Host-%d.Example.com', i)));
let big = chr(0xef, 0xbb, 0xbf) + join('\r\n', lines);
fs.writefile(W + '/n/in.txt', big);
T.ok(length(big) > 2 * SRC.READ_CHUNK, sprintf('input spans several read chunks (%d bytes)', length(big)));
let nf = SRC.normalize_file('hosts', W + '/n/in.txt', W + '/n/out.txt');
let nt = SRC.normalize_text('hosts', big);
T.eq([ nf?.valid, nf?.invalid, nf?.bytes ], [ nt.valid, nt.invalid, nt.bytes ], 'streamed counts equal in-memory normalization');
T.ok(nt.valid > 11000 && nt.invalid > 100, sprintf('the generated list has valid and invalid lines (%d/%d)', nt.valid, nt.invalid));
T.ok(fs.readfile(W + '/n/out.txt') == nt.text, 'streamed output equals in-memory normalization (no line lost at chunk borders)');
// a line longer than a read chunk (no newline for 200 KiB) counts once and does not swallow its neighbours
let longline = sprintf('%0200000d', 7);
fs.writefile(W + '/n/long.txt', 'a.com\n' + longline + '\nb.com\n' + longline);
nf = SRC.normalize_file('hosts', W + '/n/long.txt', W + '/n/long.out');
T.eq([ nf?.valid, nf?.invalid, fs.readfile(W + '/n/long.out') ], [ 2, 2, 'a.com\nb.com\n' ], 'overlong lines: one invalid entry each, neighbours kept');
T.eq(SRC.normalize_text('hosts', 'a.com\n' + longline + '\nb.com\n' + longline).invalid, 2, 'negative control: in-memory normalization agrees');
T.eq(SRC.normalize_file('hosts', W + '/n/no-such-file', W + '/n/x.out'), null, 'missing input -> null');
T.eq(SRC.normalize_file('ipset', W + '/n/in.txt', W + '/no-such-dir/x.out'), null, 'unwritable output -> null');

/* ---- content checks ---- */
let src = C.normalize_source('demo', { type: 'list', url: 'https://example.org/l.txt', min_entries: '3', min_valid_ratio: '0.9' });
T.ok(SRC.check_content(src, { valid: 3, invalid: 0 }) == null, 'enough valid entries accepted');
T.eq(SRC.check_content(src, { valid: 2, invalid: 0 }), 'too_few_entries', 'too few entries rejected');
T.eq(SRC.check_content(src, { valid: 9, invalid: 2 }), 'bad_content', 'valid ratio below minimum rejected (9/11 < 0.9)');
T.ok(SRC.check_content(src, { valid: 90, invalid: 10 }) == null, 'ratio exactly at minimum accepted');
T.eq(SRC.check_content(C.normalize_source('z', { type: 'list', url: 'https://a.b/', min_entries: '0' }), { valid: 0, invalid: 0 }), 'too_few_entries', 'empty download never accepted');

/* ---- schedule: interval with one hour of tolerance for the daily cron run ---- */
let s72 = C.normalize_source('d', { type: 'list', url: 'https://a.b/', interval_hours: '72' });
T.ok(SRC.is_due(s72, null, 1000), 'never updated -> due');
T.ok(!SRC.is_due(s72, { last_update: 1000 }, 1000 + 71 * 3600 - 1), 'not due more than an hour before the interval ends');
T.ok(SRC.is_due(s72, { last_update: 1000 }, 1000 + 71 * 3600), 'due one hour before the interval ends (cron started a bit earlier)');
T.ok(SRC.is_due(s72, { last_update: 1000 }, 1000 + 72 * 3600), 'due after the interval');
let s24 = C.normalize_source('d', { type: 'list', url: 'https://a.b/', interval_hours: '24' });
T.ok(SRC.is_due(s24, { last_update: 86400 * 10 + 600 }, 86400 * 11 + 60), 'daily subscription updated at 04:10 is due again at 04:01 next day');

/* ---- UCI section parsing ---- */
let d = C.normalize_source('refilter_domains', { enabled: '1', name: 'Re:filter', type: 'list',
	url: 'https://github.com/1andrevich/Re-filter-lists/releases/latest/download/domains_all.lst', interval_hours: '72', min_entries: '1000',
	min_valid_ratio: '0.99', ram_mib: '9' });
T.eq([ d.valid, d.enabled, d.title, d.interval_hours, d.min_entries, d.min_valid_ratio, d.ram_mib, d.bad_options ],
	[ true, true, 'Re:filter', 72, 1000, 0.99, 9, [] ], 'default subscription section');
let bad = C.normalize_source('Bad-Name', { type: 'kernel', url: 'http://plain.example/', interval_hours: '0', min_valid_ratio: '1.5' });
T.eq([ bad.valid, sort(bad.bad_options) ], [ false, [ 'interval_hours', 'min_valid_ratio', 'section', 'type', 'url' ] ], 'invalid subscription reported');
T.ok(!C.source_url_valid('http://example.org/') && C.source_url_valid('https://example.org/x.lst'), 'only https subscriptions');
T.ok(C.source_name_valid('cloudflare_v4') && !C.source_name_valid('src-x') && !C.source_name_valid(sprintf('%033d', 1)), 'subscription names');

/* ---- paths ---- */
T.eq(SRC.item_paths('demo', 'list'), { file: W + '/etc/files/lists/include/src-demo.txt', manifest: W + '/etc/manifests/lists/include/src-demo.json' }, 'item paths');
T.eq(SRC.item_paths('cf', 'ipset').file, W + '/etc/files/ipset/include/src-cf.txt', 'ipset item path');
T.ok(SRC.remove_item('demo') && !fs.stat(W + '/etc/files/lists/include/src-demo.txt') && !fs.stat(W + '/etc/manifests/lists/include/src-demo.json'),
	'remove_item deletes the downloaded file and manifest (fixture src-demo)');
T.ok(!SRC.remove_item('demo'), 'second removal finds nothing');

/* ---- update() with prepared downloads instead of the network ---- */
// served: url -> body text, '@fail' (connection refused) or '@big' (what probe.sh leaves after `ulimit -f` cut the file)
let served = {}, calls = [];
function fake_probe(tasks, opts) {
	let dir = uniq_name(P.tmp + '/probe');
	mkdir_p(dir);
	push(calls, { tasks: tasks, opts: opts });
	let results = {};
	for (let t in tasks) {
		let body = dir + '/' + t.key + '.body';
		let c = served[t.url];
		if (c == '@fail') {
			results[t.key] = { rc: 4, ms: 3, bytes: 0, body: null, err: 'Connection error: Connection failed\n', summary: 'Connection error: Connection failed' };
			continue;
		}
		if (c == '@big') {
			fs.writefile(body, 'a.com\n');
			results[t.key] = { rc: 153, ms: 3, bytes: opts.max_bytes + 512, body: body, err: '', summary: '' };
			continue;
		}
		fs.writefile(body, c);
		results[t.key] = { rc: 0, ms: 3, bytes: length(c), body: body, err: '', summary: '' };
	}
	return { dir: dir, results: results, rc: 0 };
}
const URL1 = 'https://example.org/demo.lst';
let paths = SRC.item_paths('demo', 'list');
let state = () => json(fs.readfile(SRC.state_path()) ?? '{}');
C.set_source('demo', { enabled: '1', name: 'Demo', type: 'list', url: URL1, interval_hours: '24', min_entries: '3', min_valid_ratio: '0.5' });
served[URL1] = 'A.com\nb.com\nc.com\n*.bad.com\n';
let u = SRC.update([ 'demo' ], null, { probe: fake_probe });
T.eq([ u.ok, u.updated, u.unchanged, u.failed ], [ true, [ 'demo' ], [], [] ], 'first download installs the subscription');
T.eq(calls[0].opts.max_bytes, SRC.MAX_SOURCE_BYTES, 'downloads are limited to 16 MiB');
T.eq(SRC.MAX_SOURCE_BYTES, 16 * 1024 * 1024, 'limit value');
T.eq(fs.readfile(paths.file), 'a.com\nb.com\nc.com\n', 'normalized file installed');
let man = json(fs.readfile(paths.manifest));
T.eq([ man.id, man.type, man.source, man.url, man.entries, length(man.sha256) ], [ 'src-demo', 'list', 'url', URL1, 3, 64 ], 'manifest of the subscription item');
T.eq([ state().demo?.status, state().demo?.sha256 ], [ 'ok', man.sha256 ], 'state after success');
T.ok(type(state().demo?.last_update) == 'int', 'last_update recorded');
T.eq(S.scan().items.list['src-demo']?.source, 'url', 'store sees the item as a URL subscription');
T.eq(fs.lsdir(P.tmp), [], 'downloaded bodies and temporary files removed');

let ino_file = fs.stat(paths.file).inode, ino_man = fs.stat(paths.manifest).inode;
u = SRC.update([ 'demo' ], null, { probe: fake_probe });
T.eq([ u.ok, u.updated, u.unchanged ], [ true, [], [ 'demo' ] ], 'same content -> unchanged');
T.eq([ fs.stat(paths.file).inode, fs.stat(paths.manifest).inode ], [ ino_file, ino_man ], 'unchanged content is not written to flash again');

let last_ok = state().demo.last_update;
served[URL1] = 'a.com\n';
u = SRC.update([ 'demo' ], null, { probe: fake_probe });
T.eq([ u.ok, u.error, u.failed[0]?.error ], [ false, 'source_update_failed', 'too_few_entries' ], 'too few entries -> error');
T.eq(fs.readfile(paths.file), 'a.com\nb.com\nc.com\n', 'old file kept after a short download');
T.eq([ state().demo.status, state().demo.last_update ], [ 'error', last_ok ], 'error state, last successful update kept');

served[URL1] = 'a.com\nb.com\nc.com\n#\nx y\n1 2\n* 3\n@@\n';
u = SRC.update([ 'demo' ], null, { probe: fake_probe });
T.eq(u.failed[0]?.error, 'bad_content', 'too many invalid lines (3 of 7 valid < 0.5) -> bad_content');

served[URL1] = '@big';
u = SRC.update([ 'demo' ], null, { probe: fake_probe });
T.eq(u.failed[0]?.error, 'too_large', 'download cut at the limit -> too_large');
T.ok(index(u.failed[0]?.message ?? '', '16 МиБ') >= 0 && index(u.failed[0]?.message ?? '', 'прежний файл') >= 0, 'clear message: ' + u.failed[0]?.message);
T.eq(fs.readfile(paths.file), 'a.com\nb.com\nc.com\n', 'old file kept after an oversized download');

served[URL1] = '@fail';
u = SRC.update([ 'demo' ], null, { probe: fake_probe });
T.eq(u.failed[0]?.error, 'download_failed', 'connection error -> download_failed');
T.ok(index(u.failed[0]?.message ?? '', N.ERROR_TEXT.connect_failed) == 0, 'download error explained: ' + u.failed[0]?.message);
T.eq(fs.lsdir(P.tmp), [], 'temporary files removed after failures');

// a new URL: state forgotten -> due at once; the same content under the new URL is written with the new URL
const URL2 = 'https://mirror.example.org/demo.lst';
served[URL1] = 'a.com\nb.com\nc.com\n';
served[URL2] = 'a.com\nb.com\nc.com\n';
SRC.update([ 'demo' ], null, { probe: fake_probe });
let sdemo = filter(C.load_sources(), (s) => s.name == 'demo')[0];
T.ok(!SRC.is_due(sdemo, state().demo, time()), 'freshly updated subscription is not due');
C.set_source('demo', { url: URL2 });
T.ok(SRC.reset_state('demo') && state().demo == null, 'reset_state forgets the download state');
sdemo = filter(C.load_sources(), (s) => s.name == 'demo')[0];
T.ok(SRC.is_due(sdemo, state().demo, time()), 'after a URL change the subscription is due at once');
calls = [];
u = SRC.update(null, null, { probe: fake_probe, due_only: true });
T.eq([ u.ok, u.updated ], [ true, [ 'demo' ] ], 'cron run downloads the changed URL and rewrites the item');
T.eq(calls[0]?.tasks, [ { key: 's0', url: URL2 } ], 'only the due subscription was downloaded');
T.eq(json(fs.readfile(paths.manifest)).url, URL2, 'manifest records the new URL');
calls = [];
u = SRC.update(null, null, { probe: fake_probe, due_only: true });
T.eq([ u.ok, length(calls), u.message ], [ true, 0, 'Нет подписок для обновления' ], 'negative control: nothing is due right after the update');

// cancellation between subscriptions
served[URL2] = 'x.com\ny.com\nz.com\n';
u = SRC.update([ 'demo' ], { progress: () => null, log: () => null, cancelled: () => true }, { probe: fake_probe });
T.eq([ u.updated, fs.readfile(paths.file) ], [ [], 'a.com\nb.com\nc.com\n' ], 'cancelled update does not install anything');
T.eq(fs.lsdir(P.tmp), [], 'cancelled update cleans temporary files');

// invalid section: reported, never downloaded
C.set_source('broken', { enabled: '1', type: 'list', url: 'http://plain.example/' });
calls = [];
u = SRC.update([ 'broken' ], null, { probe: fake_probe });
T.eq([ u.failed[0]?.error, calls[0]?.tasks ], [ 'invalid_source', [] ], 'invalid subscription is not downloaded');
T.eq(SRC.update([ 'nope' ], null, { probe: fake_probe }).error, 'not_found', 'unknown name');

let l = SRC.list();
let ld = filter(l.sources, (s) => s.name == 'demo')[0];
T.eq([ l.ok, ld?.status, ld?.entries, ld?.downloaded, ld?.item_id ], [ true, 'ok', 3, true, 'src-demo' ], 'sources list shows the downloaded subscription');
T.eq(filter(l.sources, (s) => s.name == 'broken')[0]?.status, 'invalid', 'sources list: invalid');
T.eq(filter(l.sources, (s) => s.name == 'cloudflare_v4')[0]?.status, 'never', 'sources list: never downloaded');

/* ---- probe.sh download limit with the local web server (uhttpd) ---- */
const WWW = '/www/luci-static/resources/ui.js';
let wsize = fs.stat(WWW)?.size;
if (wsize != null && wsize > 16384) {
	let url = 'http://127.0.0.1/luci-static/resources/ui.js';
	let cut = N.probe([ { key: 'cut', url: url } ], { concurrency: 1, timeout: 5, max_bytes: 4096 });
	let full = N.probe([ { key: 'full', url: url } ], { concurrency: 1, timeout: 5, max_bytes: wsize });
	let nolimit = N.probe([ { key: 'nolimit', url: url } ], { concurrency: 1, timeout: 5 });
	print(sprintf('NOTE [sources] probe limit 4096: rc=%d bytes=%d; limit=size: rc=%d bytes=%d; no limit: rc=%d bytes=%d (file %d)\n',
		cut.results.cut.rc, cut.results.cut.bytes, full.results.full.rc, full.results.full.bytes,
		nolimit.results.nolimit.rc, nolimit.results.nolimit.bytes, wsize));
	T.ok(cut.results.cut.bytes > 4096 && cut.results.cut.bytes < wsize, 'download over the limit is cut and reported longer than the limit');
	T.eq(cut.results.cut.rc, 153, 'uclient-fetch killed by SIGXFSZ (128+25)');
	T.eq([ full.results.full.rc, full.results.full.bytes ], [ 0, wsize ], 'positive control: a file of exactly max_bytes downloads completely');
	T.eq([ nolimit.results.nolimit.rc, nolimit.results.nolimit.bytes ], [ 0, wsize ], 'positive control: no limit');
	N.cleanup(cut.dir);
	N.cleanup(full.dir);
	N.cleanup(nolimit.dir);
}
else
	print('SKIP [sources] no local web server file for the download limit check\n');

exit(T.finish());
