'use strict';

import * as fs from 'fs';
import * as T from 'ztest';
import * as R from 'zaprett.repo';
import * as N from 'zaprett.net';
import * as CMD from 'zaprett.commands';

T.begin('repo');
T.selfcheck();
let W = T.sandbox('repo');
let FX = T.ROOT + '/tests/fixtures/repo';

/* ---- index ---- */
let index_obj = json(fs.readfile(FX + '/index.json'));
let pi = R.parse_index(index_obj);
T.eq(length(pi.items), 102, 'all 102 index items accepted (duplicates only across types)');
T.eq(pi.skipped, 0, 'nothing skipped');
T.eq(length(filter(pi.items, (i) => i.id == 'strategy-default')), 2, 'same id in two types kept');
T.eq(R.parse_index({ schema: 2, items: [] }).error, 'bad_index', 'unknown schema');
T.eq(R.parse_index('x').error, 'bad_index', 'not an object');
let bad = R.parse_index({ schema: 1, items: [
	{ id: 'ok', type: 'list', manifest: 'https://x.org/a.json' },
	{ id: 'ok', type: 'list', manifest: 'https://x.org/b.json' },
	{ id: '../evil', type: 'list', manifest: 'https://x.org/c.json' },
	{ id: 'x', type: 'kernel', manifest: 'https://x.org/d.json' },
	{ id: 'y', type: 'bin', manifest: 'file:///etc/shadow' },
	'junk' ] });
T.eq([ length(bad.items), bad.skipped ], [ 1, 5 ], 'invalid and duplicate index entries skipped');

/* ---- manifests: every manifest of the real repository ---- */
let parsed = 0, by_url = {}, cache_items = [];
for (let it in pi.items) {
	let rel = split(it.manifest_url, '/refs/heads/main/')[1];
	let mo = json(fs.readfile(FX + '/' + rel));
	let m = R.parse_manifest(mo, it);
	if (m.error) {
		T.ok(false, sprintf('manifest %s/%s: %s', it.type, it.id, m.error));
		continue;
	}
	parsed++;
	let e = { id: it.id, type: it.type, manifest_url: it.manifest_url, manifest: m };
	by_url[it.manifest_url] = e;
	push(cache_items, e);
}
T.eq(parsed, 102, 'all 102 repository manifests valid');
T.eq(sort(map(filter(cache_items, (e) => e.manifest.id_note != null), (e) => e.id)), [ 'ipset-exclude-x5group', 'ipset-exclude-yandex' ],
	'manifests with a differing id (trailing dot upstream) are kept under the index id');
let gen = by_url['https://raw.githubusercontent.com/CherretGit/zaprett-repo/refs/heads/main/manifests/strategies/nfqws/strategy-general.json'];
T.eq(gen.manifest.artifact.sha256, '71b9579832e19a6fad4db267cab25f67228ad12dcf4b9b71c4a7dc38955572f6', 'sha256 parsed');
let it0 = { id: 'a', type: 'list', manifest_url: 'https://x.org/a.json' };
let good = { schema: 1, id: 'a', name: 'A', version: '1.0.0', author: 'x', description: 'd', dependencies: [],
	artifact: { url: 'https://x.org/a.txt', sha256: 'AB' + substr(gen.manifest.artifact.sha256, 2) } };
T.eq(R.parse_manifest(good, it0).artifact.sha256, 'ab' + substr(gen.manifest.artifact.sha256, 2), 'upper-case sha256 lower-cased');
let variants = [
	[ { id: 42 }, 'id_mismatch' ], [ { version: '1 0' }, 'bad_version' ], [ { artifact: { url: 'https://x.org/a', sha256: 'zz' } }, 'bad_artifact' ],
	[ { artifact: { url: 'ftp://x/a', sha256: gen.manifest.artifact.sha256 } }, 'bad_artifact' ], [ { dependencies: 'x' }, 'bad_dependencies' ],
	[ { dependencies: [ 'not a url' ] }, 'bad_dependencies' ], [ { schema: 2 }, 'bad_manifest' ] ];
for (let v in variants) {
	let o = json(sprintf('%J', good));
	for (let k, val in v[0])
		o[k] = val;
	T.eq(R.parse_manifest(o, it0).error, v[1], 'manifest rejected: ' + v[1]);
}

/* ---- dependency order ---- */
let ro = R.resolve_order([ gen ], by_url);
T.eq(map(ro.order, (e) => e.id), [ 'quic_initial_www_google_com', 'strategy-general' ], 'dependencies before the strategy');
let alt11 = by_url['https://raw.githubusercontent.com/CherretGit/zaprett-repo/refs/heads/main/manifests/strategies/nfqws/strategy-alt11.json'];
ro = R.resolve_order([ alt11, gen ], by_url);
// alt11 declares quic, max_ru, www_google (in this order); strategy-general shares quic
T.eq(map(ro.order, (e) => e.id), [ 'quic_initial_www_google_com', 'tls_clienthello_max_ru', 'tls_clienthello_www_google_com', 'strategy-alt11', 'strategy-general' ],
	'shared dependency installed once');
let ca = { id: 'ca', type: 'bin', manifest_url: 'u:a', manifest: { dependencies: [ 'u:b' ] } };
let cb = { id: 'cb', type: 'bin', manifest_url: 'u:b', manifest: { dependencies: [ 'u:a' ] } };
ro = R.resolve_order([ ca ], { 'u:a': ca, 'u:b': cb });
T.eq(map(ro.order, (e) => e.id), [ 'cb', 'ca' ], 'dependency cycle terminates');
ro = R.resolve_order([ { id: 'x', type: 'nfqws', manifest: { dependencies: [ 'https://x.org/missing.json' ] } } ], by_url);
T.eq([ ro.error, ro.url ], [ 'dep_not_found', 'https://x.org/missing.json' ], 'missing dependency reported');

/* ---- update detection ---- */
let man = { version: '1.0.1', artifact: { sha256: 'aa' } };
T.ok(R.is_update({ version: '1.0.0', source: 'bundle', sha256: 'aa' }, man), 'newer version updates bundle item');
T.ok(!R.is_update({ version: '1.0.1', source: 'bundle', sha256: 'bb' }, man), 'bundle item with same version is not replaced');
T.ok(R.is_update({ version: '1.0.1', source: 'repo', sha256: 'bb' }, man), 'repo item with changed checksum updates');
T.ok(!R.is_update({ version: '1.0.1', source: 'repo', sha256: 'aa' }, man), 'same version and checksum: no update');
T.ok(!R.is_update({ version: '2.0.0', source: 'repo', sha256: 'bb' }, man), 'older repository version never downgrades');
T.ok(!R.is_update(null, man), 'not installed');

/* ---- uclient-fetch result classification (texts from uclient-fetch.c) ---- */
let cases = [
	[ 0, 'Downloading \'https://a/\'\nConnecting to 1.2.3.4:443\nWriting to \'x\'\nDownload completed (70000 bytes)\n', 70000, 65536, true, null ],
	[ 0, 'Download completed (100 bytes)\n', 100, 65536, false, 'too_small' ],
	[ 8, 'Connecting to 1.2.3.4:443\nHTTP error 403\n', 0, 0, true, null ],
	[ 8, 'HTTP error 404\n', 0, 1024, false, 'http_error' ],
	[ 4, 'Connecting to 1.2.3.4:443\nConnection error: Connection timed out\n', 0, 0, false, 'timeout' ],
	[ 4, '\nConnection reset prematurely\n', 16384, 0, false, 'reset' ],
	[ 4, 'SSL error: The connection was closed\nConnection error: Connection failed\n', 0, 0, false, 'tls_error' ],
	[ 5, 'SSL verify error: certificate is not trusted\nConnection error: Invalid SSL certificate\n', 0, 0, false, 'tls_cert' ],
	[ 5, 'Connection error: Server hostname does not match SSL certificate\n', 0, 0, false, 'tls_cert' ],
	[ 4, 'Failed to send request: Operation not permitted\n', 0, 0, false, 'connect_failed' ],
	[ 4, 'Connection error: Connection failed\n', 0, 0, false, 'connect_failed' ],
	[ -9, '', 0, 0, false, 'timeout' ],
	[ 3, 'Cannot open output file: No space left on device\n', 0, 0, false, 'local_error' ],
	[ 1, '', 0, 0, false, 'failed' ] ];
for (let c in cases) {
	let r = N.classify(c[0], c[1], c[2], c[3]);
	T.eq([ r.ok, r.error ], [ c[4], c[5] ], sprintf('classify rc=%d %s', c[0], replace(trim(c[1]), '\n', ' | ')));
}
T.eq(N.classify(8, 'HTTP error 403\n', 0, 0).http_status, 403, 'http status extracted');

/* ---- probe.sh: no network needed for invalid tasks, and its output format ---- */
let p = N.probe([ { key: 'bad key', url: 'https://example.org/' }, { key: 'k2', url: 'file:///etc/passwd' } ], { concurrency: 2, timeout: 1 });
T.eq([ p.results['bad key'].rc, p.results.k2.rc ], [ -1, -1 ], 'probe skips invalid keys and urls');
T.ok(p.dir != null && fs.stat(p.dir)?.type == 'directory', 'probe dir created');
N.cleanup(p.dir);
T.ok(fs.stat(p.dir) == null, 'probe dir removed');

/* ---- crontab line ---- */
let ct = 'x 1 * * * /bin/true\n';
let r1 = CMD.cron_render(ct, true, 4, 17);
T.eq(r1, 'x 1 * * * /bin/true\n17 4 * * * /usr/bin/zaprett repo upgrade --all --foreground --quiet # zaprett-autoupdate\n', 'cron line added with random minute');
T.eq(CMD.cron_render(r1, true, 5, 42), 'x 1 * * * /bin/true\n17 5 * * * /usr/bin/zaprett repo upgrade --all --foreground --quiet # zaprett-autoupdate\n', 'minute kept, hour updated');
T.eq(CMD.cron_render(r1, false, 5, 42), 'x 1 * * * /bin/true\n', 'cron line removed when autoupdate is off');
T.eq(CMD.cron_render('', false, 4, 1), '', 'empty crontab stays empty');
let dup = r1 + '3 4 * * * /usr/bin/zaprett repo upgrade --all --foreground --quiet # zaprett-autoupdate\n';
T.eq(CMD.cron_render(dup, true, 4, 9), 'x 1 * * * /bin/true\n3 4 * * * /usr/bin/zaprett repo upgrade --all --foreground --quiet # zaprett-autoupdate\n',
	'duplicate zaprett lines collapsed');

/* ---- real uclient-fetch on this host (local targets only): texts and exit codes match the classifier ---- */
// several batches: every task must finish and the whole run must not wait for the run() timeout
// (regression: ash `wait` hangs when ucode system() is called with a timeout)
let many = [];
for (let i = 0; i < 7; i++)
	push(many, { key: 'm' + i, url: sprintf('http://127.0.0.1:9/%d', i) });
let t0 = clock(true);
let pm = N.probe(many, { concurrency: 3, timeout: 2, ipv4only: true });
let t1 = clock(true);
let elapsed = (t1[0] - t0[0]) * 1000 + int((t1[1] - t0[1]) / 1000000);
T.eq(length(filter(keys(pm.results), (k) => pm.results[k].rc == 4)), 7, 'probe: all 7 tasks in 3 batches finished');
T.ok(elapsed < 15000, sprintf('probe: 3 batches took %d ms (no hang until the %d ms limit)', elapsed, (3 * (2 * 3 + 5) + 30) * 1000));
N.cleanup(pm.dir);

// ZTEST_BLACKHOLE: an address in an isolated local segment that nobody answers (e.g. unused LAN IP)
let blackhole = getenv('ZTEST_BLACKHOLE');
let ltasks = [ { key: 'refused', url: 'http://127.0.0.1:9/' }, { key: 'notfound', url: 'http://127.0.0.1/zaprett-test-no-such-page' } ];
if (blackhole)
	push(ltasks, { key: 'blackhole', url: 'http://' + blackhole + '/' });
let live = N.probe(ltasks, { concurrency: 3, timeout: 2, ipv4only: true });
let lr = live.results;
print(sprintf('NOTE [repo] uclient-fetch refused: rc=%d %J\n', lr.refused.rc, lr.refused.summary));
print(sprintf('NOTE [repo] uclient-fetch 404: rc=%d %J\n', lr.notfound.rc, lr.notfound.summary));
T.eq(N.classify(lr.refused.rc, lr.refused.err, lr.refused.bytes, 0).error, 'connect_failed', 'live: connection refused classified');
T.ok(lr.notfound.rc == 8 && N.classify(lr.notfound.rc, lr.notfound.err, 0, 0).ok, 'live: HTTP 404 from local web server counts as reachable');
if (blackhole) {
	print(sprintf('NOTE [repo] uclient-fetch blackhole %s: rc=%d %J ms=%d\n', blackhole, lr.blackhole.rc, lr.blackhole.summary, lr.blackhole.ms ?? -1));
	// a TCP connect that gets no answer fails inside usock with the -T timeout and uclient-fetch reports
	// "Failed to send request" (not "Connection timed out", which is only used after the connection is up)
	T.eq(N.classify(lr.blackhole.rc, lr.blackhole.err, lr.blackhole.bytes, 0).error, 'connect_failed', 'live: unanswered TCP connect classified');
	T.ok(lr.blackhole.ms != null && lr.blackhole.ms >= 1500, 'live: elapsed time measured');
}
else
	print('SKIP [repo] ZTEST_BLACKHOLE not set: timeout classification not checked live\n');
N.cleanup(live.dir);

exit(T.finish());
