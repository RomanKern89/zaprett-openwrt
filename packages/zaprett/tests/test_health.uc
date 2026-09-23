'use strict';

// Contract v1.3 §14: watchdog (ensure), live check of services (probe), availability monitor, log, page, the new
// fields of status. The engine, procd and the network are replaced by hooks: nothing here may start or stop a
// service of the test router.
import * as fs from 'fs';
import * as T from 'ztest';
import { P, set_paths, uniq_name, mkdir_p } from 'zaprett.util';
import * as C from 'zaprett.config';
import * as SV from 'zaprett.service';
import * as J from 'zaprett.job';
import * as TS from 'zaprett.tester';
import * as H from 'zaprett.health';
import * as CMD from 'zaprett.commands';

T.begin('health');
T.selfcheck();
let W = T.sandbox('health');
system([ 'cp', T.ROOT + '/files/usr/share/zaprett/presets.json', P.presets ]);

// an init script that only records its calls: proves that probe and monitor never touch the engine
let INIT_LOG = W + '/init.log';
T.write(W + '/init-rec', '#!/bin/sh\necho "$1" >> ' + INIT_LOG + '\nexit 0\n');
fs.chmod(W + '/init-rec', 493);
set_paths({ init: W + '/init-rec' });

let cfg_of = (main, monitor) => C.normalize(main, null, null, monitor);
let base_main = { enabled: '1', lists: [ 'zaprett-youtube', 'zaprett-discord' ] };

/* ---------- ensure ---------- */
let calls = [];
function hooks(running, applied, starts_ok) {
	return {
		instance_state: () => ({ running: running, pid: running ? 100 : null }),
		init_action: (a) => { push(calls, 'init:' + a); return { rc: 0, output: '' }; },
		wait_running: (want, ms) => ({ running: starts_ok, pid: starts_ok ? 101 : null }),
		is_applied: () => applied,
		fw_apply: () => { push(calls, 'fw_apply'); return { ok: true, changed: true }; }
	};
}
let cfg = cfg_of(base_main, {});
let r = H.ensure(cfg_of({ enabled: '0' }, {}), hooks(false, false, true));
T.eq([ r.ok, r.action, r.reason ], [ true, 'none', 'disabled' ], 'ensure: service disabled -> none');
calls = [];
r = H.ensure(cfg, hooks(false, false, true));
T.eq([ r.ok, r.action, r.reason, r.pid, calls ], [ true, 'started', 'not_running', 101, [ 'init:start' ] ], 'ensure: engine not running -> started');
calls = [];
r = H.ensure(cfg, hooks(false, false, false));
T.eq([ r.ok, r.error, r.action ], [ false, 'engine_not_running', 'started' ], 'ensure: start that did not bring the engine up is an error');
calls = [];
r = H.ensure(cfg, hooks(true, false, true));
T.eq([ r.ok, r.action, calls ], [ true, 'fw_applied', [ 'fw_apply' ] ], 'ensure: engine up, table missing -> fw_applied');
calls = [];
r = H.ensure(cfg, hooks(true, true, true));
T.eq([ r.ok, r.action, calls ], [ true, 'none', [] ], 'negative control: all working -> none, nothing touched');
T.write(TS.override_path(), '{"engine":"nfqws","strategy":"strategy-alt"}');
calls = [];
r = H.ensure(cfg, hooks(false, false, true));
T.eq([ r.action, r.reason, calls ], [ 'skipped', 'test_running', [] ], 'ensure: skipped while an automatic selection runs');
fs.unlink(TS.override_path());
T.write(TS.state_path(), '{"was_running":true}');
T.eq(H.ensure(cfg, hooks(false, false, true)).action, 'skipped', 'ensure: skipped while the test state exists');
fs.unlink(TS.state_path());
T.write(SV.stopped_path(), '');
calls = [];
r = H.ensure(cfg, hooks(false, false, true));
T.eq([ r.action, r.reason, calls ], [ 'none', 'stopped', [] ], 'ensure: a service stopped by the user is not started');
fs.unlink(SV.stopped_path());
T.eq(H.ensure(cfg, hooks(false, false, true)).action, 'started', 'negative control: without the mark it is started again');

/* ---------- probe ---------- */
T.eq(H.probe_status(), { ok: true, probe: null }, 'probe status before any check');
let probed = [];
let fake_probe = (targets, c, o) => {
	push(probed, { targets: targets, opts: o });
	return { ok: length(targets) - 1, total: length(targets), avg_ms: 20,
		targets: map(targets, (t, i) => ({ url: t.url, ok: i != 0, ms: 20 + i, bytes: 1000, http_status: null,
			error: (i == 0) ? 'timeout' : null, detail: (i == 0) ? 'x' : null })) };
};
let running_hook = { probe: fake_probe, instance_state: () => ({ running: true, pid: 5 }) };
fs.unlink(INIT_LOG);
r = H.probe_run(cfg_of(base_main, {}), null, null, running_hook);
T.ok(r.ok, 'probe run: ' + (r.message ?? r.error));
let pj = json(fs.readfile(H.probe_path()) ?? 'null');
T.eq(sort(keys(pj ?? {})), [ 'engine_running', 'finished', 'ok', 'services', 'started', 'strategy', 'total' ], 'probe.json fields (contract)');
T.eq(map(pj?.services ?? [], (s) => s.id), [ 'youtube', 'discord' ], 'probe: services whose lists are active');
T.eq(sort(keys(pj?.services?.[0] ?? {})), [ 'avg_ms', 'id', 'name', 'ok', 'targets', 'total' ], 'probe: service fields');
T.eq(sort(keys(pj?.services?.[0]?.targets?.[0] ?? {})), [ 'bytes', 'error', 'ms', 'ok', 'url' ], 'probe: target fields');
T.eq([ pj?.services?.[0]?.targets?.[0]?.ok, pj?.services?.[0]?.targets?.[0]?.error, pj?.services?.[0]?.targets?.[1]?.error ],
	[ false, 'timeout', null ], 'probe: error only on failed targets');
T.eq([ pj?.engine_running, pj?.strategy, pj?.ok + 1, pj?.total ], [ true, 'strategy-general', pj?.total, 5 ], 'probe: totals and engine state');
T.eq(probed[0]?.opts, null, 'probe uses the test section timeouts');
T.ok(fs.stat(INIT_LOG) == null, 'probe never calls the init script (engine not stopped or restarted)');
r = H.probe_run(cfg_of(base_main, {}), [ 'telegram' ], null, running_hook);
T.eq(map(json(fs.readfile(H.probe_path())).services, (s) => s.id), [ 'telegram' ], 'probe --services: a named service even when switched off');
T.eq(H.probe_run(cfg_of(base_main, {}), [ 'nope' ], null, running_hook).error, 'unknown_service', 'probe: unknown service rejected');
r = H.probe_run(cfg_of({ enabled: '1', lists: [] }, {}), null, null, running_hook);
T.eq([ r.ok, r.total, json(fs.readfile(H.probe_path())).services ], [ true, 0, [] ], 'probe with nothing switched on: empty result');
T.eq(CMD.probe_status().probe?.total, 0, 'probe status reads probe.json');
// shared helpers of the tester
T.eq(TS.service_active({ sources: [ 'cloudflare_v4' ] }, cfg_of({ ipsets: [ 'src-cloudflare_v4' ] }, {})), true, 'a service is on through its subscription');
T.eq(TS.service_active({ sources: [ 'cloudflare_v4' ] }, cfg_of({ ipsets: [] }, {})), false, 'negative control: subscription item not active');

/* ---------- monitor: pure state machine ---------- */
let mcfg = { threshold: 3, auto_repair: true };
let st = null, states = [];
for (let i = 1; i <= 3; i++) {
	let s = H.monitor_step(st, 1, 4, 1000 * i, mcfg);
	st = s.state;
	push(states, [ st.state, st.consecutive_failures, s.repair ]);
}
T.eq(states, [ [ 'ok', 1, false ], [ 'ok', 2, false ], [ 'degraded', 3, true ] ], 'degraded exactly on the threshold-th failure, repair asked then');
T.eq(H.monitor_step(st, 2, 4, 5000, mcfg).state.consecutive_failures, 0, 'half of the targets reachable is a success (ok*2 < total fails)');
T.eq(H.monitor_step(st, 2, 4, 5000, mcfg).state.state, 'ok', 'success returns to ok');
let u = H.monitor_step(st, 0, 0, 6000, mcfg);
T.eq([ u.state.state, length(u.state.history), u.repair ], [ 'unknown', 3, false ], 'total 0 does not count: unknown, no history entry');
let rep = st;
rep.last_repair = { t: 10000, job_id: 'j1' };
T.eq(H.monitor_step(rep, 0, 4, 10000 + H.REPAIR_INTERVAL - 1, mcfg).repair, false, 'no second repair within 6 hours');
T.eq(H.monitor_step(rep, 0, 4, 10000 + H.REPAIR_INTERVAL, mcfg).repair, true, 'repair again after 6 hours');
T.eq(H.monitor_step(st, 0, 4, 9000, { threshold: 3, auto_repair: false }).repair, false, 'negative control: auto_repair off -> no repair');
let long = null;
for (let i = 0; i < 60; i++)
	long = H.monitor_step(long, 3, 4, i, mcfg).state;
T.eq([ length(long.history), long.history[0].t, long.history[47].t ], [ 48, 12, 59 ], 'history keeps the last 48 checks');

/* ---------- monitor run with hooks ---------- */
let jobs = [];
let mhooks = (okc) => ({
	instance_state: () => ({ running: true, pid: 5 }),
	busy: () => false,
	probe: (targets, c, o) => {
		push(probed, { n: length(targets), opts: o });
		return { ok: okc, total: length(targets), targets: [] };
	},
	start_job: (name, args) => { push(jobs, [ name, args ]); return { ok: true, job: { id: 'job-' + length(jobs), name: name } }; }
});
let mon = cfg_of(base_main, { threshold: '2', auto_repair: '1', max_targets: '3', timeout: '4' });
fs.unlink(H.monitor_path());
probed = [];
r = H.monitor_run(mon, mhooks(0));
T.eq([ r.ok, r.state, r.total, jobs ], [ true, 'ok', 3, [] ], 'monitor: first failure below the threshold');
T.eq(probed[0], { n: 3, opts: { timeout: 4, concurrency: 3 } }, 'monitor: max_targets and timeout from the monitor section');
fs.unlink(INIT_LOG);
r = H.monitor_run(mon, mhooks(0));
T.eq([ r.state, r.repair_started, jobs ], [ 'repairing', true, [ [ 'test', [ '--quick', '--apply-if-better' ] ] ] ],
	'monitor: degraded on the 2nd failure starts test --quick --apply-if-better');
let mj = json(fs.readfile(H.monitor_path()));
T.eq([ mj.state, mj.last_repair?.job_id, length(mj.history) ], [ 'repairing', 'job-1', 2 ], 'monitor.json after the repair start');
T.ok(fs.stat(INIT_LOG) == null, 'monitor never calls the init script itself');
r = H.monitor_run(mon, mhooks(0));
T.eq([ r.state, length(jobs) ], [ 'degraded', 1 ], 'no second repair within 6 hours');
let ms = CMD.monitor_status().monitor;
T.eq(sort(keys(ms)), [ 'auto_repair', 'checked_at', 'consecutive_failures', 'enabled', 'history', 'interval', 'last_repair', 'ok', 'state',
	'threshold', 'total' ], 'monitor status fields (contract)');
r = H.monitor_run(mon, mhooks(3));
T.eq([ r.state, r.consecutive_failures ], [ 'ok', 0 ], 'monitor: recovers after a successful check');
// repairing is shown only while the repair job runs
T.write(H.monitor_path(), sprintf('%J', { state: 'repairing', consecutive_failures: 2, history: [], last_repair: { t: 1, job_id: 'gone' } }));
T.eq(H.monitor_status(mon).monitor.state, 'degraded', 'a finished repair job no longer shows "repairing"');
let before = fs.readfile(H.monitor_path());
for (let c in [
	[ cfg_of(base_main, { enabled: '0' }), mhooks(0), 'monitor_disabled' ],
	[ cfg_of({ enabled: '0' }, {}), mhooks(0), 'service_disabled' ],
	[ mon, { instance_state: () => ({ running: false }), busy: () => false, probe: fake_probe }, 'not_running' ],
	[ mon, { instance_state: () => ({ running: true }), busy: () => true, probe: fake_probe }, 'job_busy' ] ]) {
	r = H.monitor_run(c[0], c[1]);
	T.eq([ r.ok, r.skipped ], [ true, c[2] ], 'monitor skipped: ' + c[2]);
}
T.write(SV.stopped_path(), '');
T.eq(H.monitor_run(mon, mhooks(0)).skipped, 'stopped', 'monitor skipped: stopped');
fs.unlink(SV.stopped_path());
T.eq(fs.readfile(H.monitor_path()), before, 'skipped checks write nothing');
T.ok(index(H.MONITOR_SKIP, 'job_busy') >= 0 && length(H.MONITOR_SKIP) == 5, 'closed list of skip reasons');

/* ---------- status: job, monitor, queue, ipv6_wan, new warnings ---------- */
T.write(W + '/nfq', '  200  12345     0 2 65531     0     0      777  1\n  201  1  0 2 65531 0 0 5 1\n');
set_paths({ nfqueue: W + '/nfq', meminfo: W + '/meminfo' });
T.write(W + '/meminfo', 'MemTotal:         494244 kB\nMemAvailable:     386568 kB\n');
T.eq(SV.queue_packets(fs.readfile(W + '/nfq'), 200), 777, 'queue counter: 8th column of the qnum row');
T.eq(SV.queue_packets(fs.readfile(W + '/nfq'), 300), null, 'negative control: no row for another qnum');
T.eq(SV.queue_packets('', 200), null, 'no queue bound');
C.set({ enabled: '1' });
T.write(J.job_path(), sprintf('%J', { id: '1-1', name: 'probe', state: 'done', progress: 100, message: 'x', started: 1, finished: 2, rc: 0, result: {}, pid: 0 }));
let s1 = CMD.status();
T.ok(s1.ok && index(keys(s1), 'job') >= 0 && index(keys(s1), 'monitor') >= 0 && s1.queue?.packets == 777 && type(s1.ipv6_wan) == 'bool',
	sprintf('status has job, monitor, queue, ipv6_wan: %J %J %J', s1.job, s1.monitor, s1.queue));
T.eq(sort(keys(s1.monitor ?? {})), [ 'checked_at', 'consecutive_failures', 'state' ], 'status.monitor fields');
T.eq(sort(keys(s1.job ?? {})), [ 'id', 'name', 'progress', 'state' ], 'status.job: brief form of the last job');
T.write(H.monitor_path(), sprintf('%J', { state: 'degraded', consecutive_failures: 5, history: [] }));
T.has(CMD.status().warnings, 'monitor_degraded', 'monitor_degraded while degraded');
C.set({ enabled: '0' }, 'monitor');
let s2 = CMD.status();
T.eq([ s2.monitor, index(s2.warnings, 'monitor_degraded') ], [ null, -1 ], 'negative control: monitor off -> monitor null, no warning');
C.set({ enabled: '1' }, 'monitor');
// low_memory: enabled subscriptions need more than half of MemAvailable
C.set_source('refilter_domains', { enabled: '1' });
T.write(W + '/meminfo', 'MemTotal:         65536 kB\nMemAvailable:     16384 kB\n');
T.has(CMD.status().warnings, 'low_memory', 'low_memory: 9 MiB of subscriptions with 16 MiB available');
T.write(W + '/meminfo', 'MemTotal:         494244 kB\nMemAvailable:     386568 kB\n');
T.lacks(CMD.status().warnings, 'low_memory', 'negative control: plenty of memory');
C.set_source('refilter_domains', { enabled: '0' });
let pr = json(fs.readfile(P.presets));
let full_cfg = cfg_of({ lists: [ 'src-refilter_domains' ] }, {});
T.eq(CMD.memory_heavy(full_cfg, pr, [], 128, 100), true, 'tier full switched on with 128 MiB of RAM');
T.eq(CMD.memory_heavy(full_cfg, pr, [], 512, 100), false, 'negative control: 512 MiB of RAM');
T.eq(CMD.memory_heavy(cfg_of({ lists: [ 'zaprett-youtube' ] }, {}), pr, [], 128, 100), false, 'negative control: only light services');

// wizard apply: a service of tier full on a small router is applied with low_memory
T.write(W + '/meminfo', 'MemTotal:         131072 kB\nMemAvailable:     90000 kB\n');
let wz = CMD.wizard_apply([ 'youtube', 'rkn_full' ]);
T.ok(wz.ok && index(wz.warnings, 'low_memory') >= 0, 'wizard: tier full on 128 MiB -> low_memory: ' + (wz.message ?? join(',', wz.warnings ?? [])));
T.write(W + '/meminfo', 'MemTotal:         494244 kB\nMemAvailable:     386568 kB\n');
wz = CMD.wizard_apply([ 'youtube', 'rkn_full' ]);
T.ok(wz.ok && index(wz.warnings, 'low_memory') < 0, 'negative control: 482 MiB -> no low_memory');
C.set_source('refilter_domains', { enabled: '0' });
C.set_source('antifilter_allyouneed', { enabled: '0' });

/* ---------- test status --brief ---------- */
T.write(TS.results_path(), sprintf('%J', { baseline: { ok: 1, total: 2, targets: [ { url: 'a' } ] },
	results: [ { id: 'x', ok: 1, total: 2, targets: [ { url: 'a' } ] } ] }));
let tb = CMD.test_status({ brief: true }).results;
T.eq([ tb.baseline.targets, tb.results[0].targets, tb.results[0].id ], [ null, null, 'x' ], 'test status --brief drops targets');
T.eq(length(CMD.test_status({}).results.results[0].targets), 1, 'negative control: full status keeps targets');

/* ---------- log ---------- */
let lines = [];
for (let i = 0; i < 1200; i++)
	push(lines, sprintf('Mon Sep 22 10:00:%02d 2026 daemon.info zaprett: line %d', i % 60, i));
push(lines, 'Mon Sep 22 10:01:00 2026 daemon.info dnsmasq: other');
push(lines, 'Mon Sep 22 10:01:01 2026 daemon.err nfqws[42]: engine line');
T.write(W + '/log.txt', join('\n', lines) + '\n');
T.write(W + '/logread', '#!/bin/sh\ncat ' + W + '/log.txt\n');
fs.chmod(W + '/logread', 493);
set_paths({ logread: W + '/logread' });
r = CMD.log_lines({ tail: '2' });
T.eq(r.lines, [ 'Mon Sep 22 10:00:59 2026 daemon.info zaprett: line 1199', 'Mon Sep 22 10:01:01 2026 daemon.err nfqws[42]: engine line' ],
	'log --tail 2: last lines of zaprett and nfqws, other daemons skipped');
T.eq(length(CMD.log_lines({}).lines), 200, 'log: 200 lines by default');
T.eq(length(CMD.log_lines({ tail: '5000' }).lines), 1000, 'log: at most 1000 lines');
T.eq(CMD.log_lines({ tail: 'x' }).error, 'bad_value', 'log: bad --tail');
T.eq(CMD.log_filter('a dnsmasq\nb odhcpd\n', 10), [], 'negative control: nothing of zaprett');

/* ---------- page ---------- */
// contract v1.4 §15.7: overview + dns, diagnostics + dns, diagnose
let want = { overview: [ 'status', 'job', 'presets', 'monitor', 'probe', 'dns' ], lists: [ 'status', 'job', 'items', 'sources', 'presets' ],
	strategies: [ 'status', 'job', 'items', 'test' ], diagnostics: [ 'status', 'job', 'monitor', 'dns', 'diagnose' ] };
for (let name, parts in want) {
	let pg = CMD.page(name);
	T.eq(sort(filter(keys(pg), (k) => k != 'ok' && k != 'page')), sort(slice(parts)), 'page ' + name + ': fields');
	T.eq(filter(parts, (k) => pg[k]?.ok !== true), [], 'page ' + name + ': every part is a whole answer with ok');
}
let ps = CMD.page('strategies');
T.eq(ps.test.results.results[0].targets, null, 'page strategies: test is the brief status');
T.ok(type(CMD.page('overview').status.warnings) == 'array' && type(CMD.page('lists').items.items) == 'array', 'page parts carry their data');
T.eq(CMD.page('nope').error, 'bad_value', 'unknown page');

exit(T.finish());
