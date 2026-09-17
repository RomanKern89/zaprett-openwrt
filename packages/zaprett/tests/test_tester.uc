'use strict';

import * as fs from 'fs';
import * as T from 'ztest';
import { P } from 'zaprett.util';
import * as C from 'zaprett.config';
import * as S from 'zaprett.store';
import * as TS from 'zaprett.tester';
import * as CMD from 'zaprett.commands';

T.begin('tester');
T.selfcheck();
let W = T.sandbox('tester');

let presets = {
	schema: 1,
	services: [
		{ id: 'youtube', lists: [ 'list-youtube' ], ipsets: [], test_targets: [
			{ url: 'https://www.youtube.com/', min_bytes: 65536 }, { url: 'https://i.ytimg.com/x.jpg', min_bytes: '17000' },
			{ url: 'javascript:alert(1)', min_bytes: 0 } ] },
		{ id: 'discord', lists: [ 'list-discord' ], ipsets: [], test_targets: [ { url: 'https://discord.com/', min_bytes: 1 } ] },
		{ id: 'telegram', lists: [], ipsets: [ 'ipset-roblox' ], test_targets: [ { url: 'https://telegram.org/', min_bytes: 0 } ] },
		'junk'
	],
	defaults: { strategy: 'strategy-general', quick_test_strategies: [ 'strategy-alt', 'no-such', 'strategy-alt2' ] }
};
let cfg = C.normalize({ lists: [ 'list-youtube', 'user-hosts' ], ipsets: [ 'ipset-roblox' ] }, null, null);
let texts = [ '# comment\nyoutube.com\n^exact.youtube.com\n*.bad.com\n1.2.3.4\nlocalhost\nwww.youtube.com\nyoutube.com\n_srv.example.com\n', 'b.com\nc.com\n' ];
let targets = TS.build_targets(presets, cfg, texts, 4);
T.eq(map(targets, (t) => [ t.url, t.min_bytes ]), [
	[ 'https://www.youtube.com/', 65536 ], [ 'https://i.ytimg.com/x.jpg', 17000 ], [ 'https://telegram.org/', 0 ],
	[ 'https://youtube.com/', 0 ], [ 'https://exact.youtube.com/', 0 ], [ 'https://b.com/', 0 ], [ 'https://c.com/', 0 ]
], 'targets: presets of active services, then list domains (dedup, no masks/IPs/single labels)');
T.eq(length(TS.build_targets(presets, cfg, texts, 0)), 3, 'max_domains=0 -> presets only');
T.eq(TS.build_targets(null, C.normalize({ lists: [] }, null, null), [], 20), [], 'no presets and no lists -> no targets');

let res = TS.sort_results([
	{ id: 'c', status: 'done', ratio: 0.5, avg_ms: 100 },
	{ id: 'a', status: 'invalid', ratio: 0, avg_ms: null },
	{ id: 'b', status: 'done', ratio: 1, avg_ms: 900 },
	{ id: 'd', status: 'done', ratio: 1, avg_ms: 300 },
	{ id: 'e', status: 'done', ratio: 0.5, avg_ms: null },
	{ id: 'f', status: 'engine_failed', ratio: 0, avg_ms: null }
]);
T.eq(map(res, (r) => r.id), [ 'd', 'b', 'c', 'e', 'a', 'f' ], 'results sorted by ratio desc, avg_ms asc, invalid last');

T.ok(TS.ratio(3, 4) == 0.75 && TS.ratio(0, 5) == 0 && TS.ratio(2, 2) == 1 && TS.ratio(1, 0) == 0, 'ratio is fractional (integer division trap)');
T.ok(3 / 4 == 0, 'negative control: plain integer division in ucode truncates');

let idx = S.scan();
let cfg2 =C.normalize({ strategy: 'strategy-general' }, null, null);
let all = TS.candidates(idx, cfg2, 'nfqws', {}, presets);
T.eq(length(all.ids), 65, 'all installed strategies are candidates');
T.eq(all.ids[0], 'strategy-general', 'current strategy tested first');
let quick = TS.candidates(idx, cfg2, 'nfqws', { quick: true }, presets);
T.eq(slice(quick.ids, 0, 3), [ 'strategy-general', 'strategy-alt', 'strategy-alt2' ], 'quick: preset defaults first, unknown ids skipped');
T.ok(length(quick.ids) <= TS.QUICK_TOP + 2 && length(quick.ids) >= 3, 'quick list is short');
T.eq(TS.candidates(idx, cfg2, 'nfqws', { strategies: [ 'strategy-alt', 'strategy-alt' ] }, presets).ids, [ 'strategy-alt' ], 'explicit list deduplicated');
T.eq(TS.candidates(idx, cfg2, 'nfqws', { strategies: [ 'nope' ] }, presets).error, 'strategy_not_found', 'explicit unknown strategy rejected');
T.eq(TS.candidates(idx, cfg2, 'nfqws2', {}, presets).ids, [], 'no nfqws2 strategies installed');

// restore: nothing to do after a finished test; a leftover override is removed
T.ok(TS.restore(null) == null, 'restore without state and override does nothing');
T.write(TS.override_path(), '{"engine":"nfqws","strategy":"strategy-alt"}');
let rr = TS.restore(null);
T.ok(rr != null && fs.stat(TS.override_path()) == null, 'leftover override removed (init script absent in sandbox: rc ' + rr?.rc + ')');
T.eq(rr?.output, 'нет ' + P.init, 'restore calls the init script');

// test stop / test apply report `reloaded` (contract v1.2), never `restarted`
T.write(TS.override_path(), '{"engine":"nfqws","strategy":"strategy-alt"}');
let tstop = CMD.test_stop();
T.eq([ tstop.ok, tstop.state, tstop.reloaded, tstop.restarted ], [ true, 'restored', false, null ],
	'test stop without a job rolls back a leftover override (init script absent: reloaded=false)');
T.ok(fs.stat(TS.override_path()) == null, 'override removed by test stop');
T.eq(CMD.test_stop().error, 'no_job', 'negative control: nothing to stop afterwards');
T.eq(CMD.test_apply('../x').error, 'bad_id', 'test apply: bad id');
if (T.NFQWS) {
	let tapply = CMD.test_apply('strategy-alt');
	T.eq([ tapply.ok, tapply.reloaded, tapply.restarted, C.load().strategy ], [ true, false, null, 'strategy-alt' ],
		'test apply saves the strategy; reloaded=false for a stopped engine: ' + (tapply.message ?? ''));
	T.write(TS.override_path(), '{"engine":"nfqws","strategy":"strategy-alt2"}');
	T.eq(CMD.test_apply('strategy-general').error, 'test_running', 'test apply refused while a test override is active');
	fs.unlink(TS.override_path());
}

// presets of the package (made by the bundle agent) must parse
let pkg_presets = T.ROOT + '/files/usr/share/zaprett/presets.json';
if (fs.stat(pkg_presets)?.type == 'file') {
	system([ 'cp', pkg_presets, P.presets ]);
	let pr = CMD.presets();
	T.ok(pr.ok && length(pr.services) > 0, 'package presets.json parsed: ' + (pr.message ?? ''));
	for (let s in (pr.services ?? []))
		T.ok(type(s.id) == 'string' && type(s.enabled) == 'bool' && type(s.available) == 'bool', 'preset service fields: ' + s.id);
	let pt = TS.build_targets(json(fs.readfile(P.presets)), C.normalize({ lists: [ 'zaprett-youtube', 'zaprett-discord' ] }, null, null), [], 0);
	T.ok(length(pt) >= 2, sprintf('package presets give test targets for youtube+discord: %d', length(pt)));
}
else
	print('SKIP [tester] package presets.json not present\n');

/* ---- полный проход автоподбора до ветки status=done (здесь падал неверный вызов N.is_applied) ---- */
// Движок и опрос целей подменены через opts.hooks: на стенде нельзя запускать службы и ходить в сеть.
if (T.NFQWS) {
	let actions = [], probed = 0;
	let hooks = {
		init_action: (action) => {
			push(actions, action);
			return { rc: 0, output: '' };
		},
		wait_running: (want, timeout_ms) => ({ running: want, pid: 4242 }),
		probe: (targets, c) => {
			probed++;
			let okc = (probed == 1) ? 1 : length(targets);	// без обхода доступна одна цель, с обходом — все
			return { ok: okc, total: length(targets), avg_ms: 10 + probed,
				targets: map(targets, (t) => ({ url: t.url, ok: probed > 1, ms: 11, status: probed > 1 ? null : 'timeout' })) };
		}
	};
	let tcfg = C.normalize({ lists: [ 'list-youtube' ], strategy: 'strategy-general' }, null, { max_domains: '3', settle: '0' });
	let res = TS.run(tcfg, { strategies: [ 'strategy-general', 'strategy-alt' ], hooks: hooks }, null);
	T.ok(res.ok, 'автоподбор дошёл до конца: ' + (res.message ?? res.error ?? ''));
	let rres = json(fs.readfile(TS.results_path()) ?? 'null');
	T.eq(sort(map(rres?.results ?? [], (r) => [ r.id, r.status ])), [ [ 'strategy-alt', 'done' ], [ 'strategy-general', 'done' ] ],
		'обе стратегии проверены со статусом done');
	T.ok(type(rres?.results[0]?.nft_applied) == 'bool', 'поле nft_applied заполнено (при неверном импорте здесь было исключение)');
	T.eq([ rres?.state, rres?.baseline?.ok, rres?.baseline?.total ], [ 'done', 1, length(rres?.targets ?? []) ], 'базовая проверка записана');
	T.ok(res.best != null && res.tested == 2, sprintf('лучшая стратегия выбрана: %s (проверено %d)', res.best, res.tested));
	T.ok(rres?.results[0]?.ratio == 1 && rres?.results[0]?.ok > 0,
		sprintf('доля успешных целей посчитана: %J', rres?.results[0]?.ratio));
	T.ok(index(actions, 'stop') >= 0 && length(filter(actions, (a) => a == 'start')) >= 2,
		'движок остановлен для базовой проверки и запущен на каждую стратегию: ' + join(',', actions));
	T.ok(fs.stat(TS.override_path()) == null && fs.stat(TS.state_path()) == null, 'override и состояние сняты после прогона');
	// отмена между стратегиями: результатов меньше, состояние cancelled
	probed = 0;
	let cancel_ctx = { progress: () => null, log: () => null, cancelled: () => true, partial: () => null };
	res = TS.run(tcfg, { strategies: [ 'strategy-general', 'strategy-alt' ], hooks: hooks }, cancel_ctx);
	rres = json(fs.readfile(TS.results_path()) ?? 'null');
	T.eq([ res.ok, length(rres?.results ?? []), rres?.state ], [ true, 0, 'cancelled' ], 'отмена до первой стратегии');
}
else
	print('SKIP [tester] nfqws binary not given: полный проход автоподбора не проверен\n');

exit(T.finish());
