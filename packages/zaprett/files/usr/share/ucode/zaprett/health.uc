// zaprett: engine watchdog (`ensure`), live check of preset services (`probe`) and the availability monitor
// with automatic repair (contract v1.3 §14). None of them stops or restarts a working engine.
'use strict';

import * as fs from 'fs';
import { P, read_json, write_json, mkdir_p, is_file, log, ok, fail, NULL_CTX } from 'zaprett.util';
import * as C from 'zaprett.config';
import * as SV from 'zaprett.service';
import * as NF from 'zaprett.nft';
import * as J from 'zaprett.job';
import * as T from 'zaprett.tester';

export const HISTORY = 48;
// the monitor starts an automatic selection at most once in this many seconds
export const REPAIR_INTERVAL = 6 * 3600;
export const MONITOR_SKIP = [ 'monitor_disabled', 'service_disabled', 'stopped', 'not_running', 'job_busy' ];

export function probe_path() {
	return P.run + '/probe.json';
};

export function monitor_path() {
	return P.run + '/monitor.json';
};

// An automatic selection is running (its override or saved state exists, or its job is alive).
export function test_busy() {
	if (is_file(T.override_path()) || is_file(T.state_path()))
		return true;
	let j = J.read();
	return !!(j && j.name == 'test' && j.state == 'running');
};

// Any background job is running: the monitor does not measure while jobs change the engine or the lists.
function job_running() {
	let j = J.read();
	return !!(j && j.state == 'running');
}

/* ---------- ensure (cron watchdog) ---------- */

// hooks (unit tests): instance_state, init_action, wait_running, is_applied, fw_apply.
export function ensure(cfg, hooks) {
	hooks = hooks ?? {};
	if (!cfg.enabled)
		return ok({ action: 'none', reason: 'disabled' });
	if (is_file(SV.stopped_path()))
		return ok({ action: 'none', reason: 'stopped' });
	if (test_busy())
		return ok({ action: 'skipped', reason: 'test_running' });
	let instance_state = hooks.instance_state ?? SV.instance_state;
	let init_action = hooks.init_action ?? SV.init_action;
	let wait_running = hooks.wait_running ?? SV.wait_running;
	let is_applied = hooks.is_applied ?? NF.is_applied;
	let inst = instance_state();
	if (!inst.running) {
		let a = init_action('start');
		let st = wait_running(true, 8000);
		if (!st.running) {
			log('err', 'сторож: движок не работал, запустить его не удалось (подробности: zaprett check)');
			return fail('engine_not_running', 'Движок не работал, и запустить его не удалось. Подробности: zaprett check',
				{ action: 'started', reason: 'not_running', init_output: a.output });
		}
		log('warning', sprintf('сторож: движок не работал — запущен заново (pid %s)', st.pid ?? '?'));
		return ok({ action: 'started', reason: 'not_running', pid: st.pid });
	}
	if (!is_applied()) {
		let fw_apply = hooks.fw_apply;
		let r = fw_apply();
		if (!r.ok) {
			log('err', 'сторож: таблицы inet zaprett не было, восстановить не удалось: ' + (r.message ?? r.error));
			return fail(r.error, r.message, { action: 'fw_applied' });
		}
		log('warning', 'сторож: таблицы inet zaprett не было — правила применены заново');
		return ok({ action: 'fw_applied' });
	}
	return ok({ action: 'none' });
};

/* ---------- probe (live check of the preset services) ---------- */

function load_presets() {
	return read_json(P.presets, 1048576);
}

function target_out(t) {
	return { url: t.url, ok: t.ok, ms: t.ms, bytes: t.bytes, error: t.ok ? null : t.error };
}

// Pure: splits one probe over unique URLs into per-service results. services: [ { id, name, targets } ],
// res: result of T.probe_targets over `urls` (same order).
export function per_service(services, urls, res) {
	let by_url = {};
	for (let i = 0; i < length(urls); i++)
		by_url[urls[i]] = res.targets[i];
	let out = [], okc = 0, total = 0;
	for (let s in services) {
		let tg = [], sok = 0, ms = 0, msn = 0;
		for (let t in s.targets) {
			let r = by_url[t.url] ?? { url: t.url, ok: false, ms: null, bytes: 0, error: 'failed' };
			push(tg, target_out(r));
			if (r.ok) {
				sok++;
				if (r.ms != null) {
					ms += r.ms;
					msn++;
				}
			}
		}
		okc += sok;
		total += length(tg);
		push(out, { id: s.id, name: s.name, ok: sok, total: length(tg), avg_ms: msn ? int(ms / msn) : null, targets: tg });
	}
	return { ok: okc, total: total, services: out };
};

function unique_targets(services, limit) {
	let seen = {}, out = [];
	for (let s in services)
		for (let t in s.targets) {
			if (seen[t.url] || (limit != null && length(out) >= limit))
				continue;
			seen[t.url] = true;
			push(out, t);
		}
	return out;
}

// probe job. ids: null -> services that are switched on. hooks (unit tests): probe, instance_state.
export function probe_run(cfg, ids, ctx, hooks) {
	ctx = ctx ?? NULL_CTX;
	hooks = hooks ?? {};
	let ps = T.preset_services(load_presets(), cfg, ids);
	if (ps.error)
		return fail('unknown_service', sprintf('Сервис «%s» не найден в пресетах', ps.id));
	let instance_state = hooks.instance_state ?? SV.instance_state;
	let probe_fn = hooks.probe ?? T.probe_targets;
	let targets = unique_targets(ps.services, null);
	let res = {
		started: time(), finished: 0, engine_running: !!instance_state().running,
		strategy: C.current_strategy_id(cfg, cfg.engine) || null, ok: 0, total: 0, services: []
	};
	ctx.progress(5, sprintf('Проверка сервисов: %d, адресов: %d', length(ps.services), length(targets)));
	let pr = length(targets) ? probe_fn(targets, cfg) : { ok: 0, total: 0, targets: [] };
	let split_res = per_service(ps.services, map(targets, (t) => t.url), pr);
	res.ok = split_res.ok;
	res.total = split_res.total;
	res.services = split_res.services;
	res.finished = time();
	mkdir_p(P.run);
	if (!write_json(probe_path(), res))
		return fail('write_failed', 'Не удалось записать ' + probe_path());
	for (let s in res.services)
		ctx.log(sprintf('%s: доступно %d из %d', s.id, s.ok, s.total));
	return ok({ reachable: res.ok, total: res.total, services: length(res.services),
		message: sprintf('Доступно %d из %d адресов', res.ok, res.total) });
};

export function probe_status() {
	let p = read_json(probe_path(), 1048576);
	return ok({ probe: (type(p) == 'object') ? p : null });
};

/* ---------- monitor ---------- */

// Pure: an empty state of the monitor.
export function monitor_empty() {
	return { state: 'unknown', checked_at: null, ok: 0, total: 0, consecutive_failures: 0, history: [], last_repair: null };
};

// Pure: the state after one check. A check fails when fewer than half of the targets answered
// (ok * 2 < total); total == 0 does not count (state unknown). Returns { state, repair } where repair tells the
// caller to start an automatic selection (degraded, auto_repair on, none within REPAIR_INTERVAL).
export function monitor_step(prev, okc, total, now, mcfg) {
	let st = monitor_empty();
	for (let k in keys(st))
		if (prev?.[k] != null)
			st[k] = prev[k];
	st.history = slice((type(prev?.history) == 'array') ? prev.history : []);
	st.checked_at = now;
	st.ok = okc;
	st.total = total;
	if (!total) {
		st.state = 'unknown';
		st.consecutive_failures = 0;
		return { state: st, repair: false };
	}
	push(st.history, { t: now, ok: okc, total: total });
	if (length(st.history) > HISTORY)
		st.history = slice(st.history, length(st.history) - HISTORY);
	st.consecutive_failures = (okc * 2 < total) ? (st.consecutive_failures ?? 0) + 1 : 0;
	st.state = (st.consecutive_failures >= mcfg.threshold) ? 'degraded' : 'ok';
	let last = st.last_repair?.t;
	let repair = st.state == 'degraded' && !!mcfg.auto_repair && (type(last) != 'int' || now - last >= REPAIR_INTERVAL);
	return { state: st, repair: repair };
};

export function monitor_read() {
	let m = read_json(monitor_path(), 262144);
	return (type(m) == 'object') ? m : null;
};

// `monitor status`
export function monitor_status(cfg) {
	let m = monitor_read() ?? monitor_empty();
	let e = monitor_empty();
	for (let k in keys(e))
		if (m[k] != null)
			e[k] = m[k];
	// a repair job that ended (or died) no longer keeps the state at "repairing"
	if (e.state == 'repairing') {
		let j = J.read();
		if (!j || j.id != e.last_repair?.job_id || j.state != 'running')
			e.state = (e.consecutive_failures >= cfg.monitor.threshold) ? 'degraded' : 'ok';
	}
	e.enabled = cfg.monitor.enabled;
	e.auto_repair = cfg.monitor.auto_repair;
	e.interval = cfg.monitor.interval;
	e.threshold = cfg.monitor.threshold;
	return ok({ monitor: e });
};

// Compact state for `status`: null when the section is missing or the monitor is off.
export function monitor_brief(cfg) {
	if (!cfg.monitor.present || !cfg.monitor.enabled)
		return null;
	let m = monitor_status(cfg).monitor;
	return { state: m.state, consecutive_failures: m.consecutive_failures, checked_at: m.checked_at };
};

// Pure: why the monitor does not check now, or null.
export function monitor_skip_reason(cfg, stopped, running, busy) {
	if (!cfg.monitor.enabled)
		return 'monitor_disabled';
	if (!cfg.enabled)
		return 'service_disabled';
	if (stopped)
		return 'stopped';
	if (!running)
		return 'not_running';
	if (busy)
		return 'job_busy';
	return null;
};

// `monitor run` (cron). hooks (unit tests): instance_state, probe, busy, start_job, now.
export function monitor_run(cfg, hooks) {
	hooks = hooks ?? {};
	let instance_state = hooks.instance_state ?? SV.instance_state;
	let probe_fn = hooks.probe ?? T.probe_targets;
	let busy = hooks.busy ?? job_running;
	let start_job = hooks.start_job ?? J.start;
	let skip = monitor_skip_reason(cfg, is_file(SV.stopped_path()), instance_state().running, busy() || test_busy());
	if (skip)
		return ok({ skipped: skip });

	let ps = T.preset_services(load_presets(), cfg, null);
	let targets = unique_targets(ps.services, cfg.monitor.max_targets);
	let pr = length(targets) ? probe_fn(targets, cfg, { timeout: cfg.monitor.timeout, concurrency: cfg.monitor.max_targets }) :
		{ ok: 0, total: 0, targets: [] };
	// a job or a stop in the middle of the check (engine stopped for a test...) would give a false failure
	skip = monitor_skip_reason(cfg, is_file(SV.stopped_path()), instance_state().running, busy() || test_busy());
	if (skip)
		return ok({ skipped: skip });

	let now = hooks.now ?? time();
	let prev = monitor_read();
	let step = monitor_step(prev, pr.ok, pr.total, now, cfg.monitor);
	let st = step.state;
	if (st.state == 'degraded' && prev?.state != 'degraded' && prev?.state != 'repairing')
		log('warning', sprintf('монитор: доступно %d из %d адресов, неудачных проверок подряд: %d — состояние degraded',
			pr.ok, pr.total, st.consecutive_failures));
	if (step.repair) {
		let j = start_job('test', [ '--quick', '--apply-if-better' ]);
		if (j.ok) {
			st.state = 'repairing';
			st.last_repair = { t: now, job_id: j.job.id };
			log('warning', 'монитор: запущен быстрый автоподбор стратегии (задача ' + j.job.id + ')');
		}
		else
			log('err', 'монитор: автоподбор не запущен: ' + (j.message ?? j.error));
	}
	mkdir_p(P.run);
	if (!write_json(monitor_path(), st))
		return fail('write_failed', 'Не удалось записать ' + monitor_path());
	return ok({ state: st.state, reachable: pr.ok, total: pr.total, consecutive_failures: st.consecutive_failures,
		repair_started: st.state == 'repairing' });
};
