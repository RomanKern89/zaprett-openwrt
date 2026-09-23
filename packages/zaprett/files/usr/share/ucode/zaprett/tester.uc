// zaprett: automatic strategy selection (ARCHITECTURE §10, contract v1.6 §17).
// The UCI strategy is never modified while testing. Mode isolated (default): the main engine keeps working and the
// candidates run as instance `test` on qnum+1 for the checks of user zaprett-test only (zaprett.isolate). Mode
// exclusive (fallback or --exclusive): the engine itself runs the candidates through /var/run/zaprett/test-override,
// so removing that file always restores the original setup.
'use strict';

import * as fs from 'fs';
import { P, read_json, write_json, mkdir_p, is_file, run, log, ok, fail, NULL_CTX } from 'zaprett.util';
import * as V from 'zaprett.validate';
import * as C from 'zaprett.config';
import * as S from 'zaprett.store';
import * as G from 'zaprett.strategy';
import * as N from 'zaprett.net';
import * as NF from 'zaprett.nft';
import * as SV from 'zaprett.service';
import * as ISO from 'zaprett.isolate';

export const QUICK_TOP = 10;
export const LIST_READ_LIMIT = 1048576;

export function results_path() {
	return P.run + '/test-results.json';
};

export function state_path() {
	return P.run + '/test-state.json';
};

export function override_path() {
	return P.run + '/test-override';
};

// Anything a test leaves behind while it runs: saved state, override (exclusive) or test chains and instance (isolated).
export function leftover() {
	return is_file(state_path()) || is_file(override_path()) || ISO.leftover();
};

function str_array(v) {
	return filter((type(v) == 'array') ? v : [], (x) => type(x) == 'string');
}

// Pure: is a preset service switched on — one of its lists or ipsets, or the item of one of its subscriptions
// (src-<name>), is active; the same for the items of its variants (contract v1.7 §16.4).
export function service_active(s, cfg) {
	for (let l in str_array(s.lists))
		if (index(cfg.lists, l) >= 0)
			return true;
	for (let l in str_array(s.ipsets))
		if (index(cfg.ipsets, l) >= 0)
			return true;
	for (let n in str_array(s.sources))
		if (index(cfg.lists, 'src-' + n) >= 0 || index(cfg.ipsets, 'src-' + n) >= 0)
			return true;
	for (let v in ((type(s.variants) == 'array') ? s.variants : []))
		if (type(v) == 'object' && service_active({ lists: v.lists, ipsets: v.ipsets, sources: v.sources }, cfg))
			return true;
	return false;
};

// Pure: valid test targets of one preset service: [ { url, min_bytes, service } ].
export function service_targets(s) {
	let out = [];
	for (let t in ((type(s?.test_targets) == 'array') ? s.test_targets : [])) {
		if (type(t) != 'object' || !V.url_valid(t.url))
			continue;
		let mb = V.parse_uint(t.min_bytes, 0, 1073741824);
		push(out, { url: t.url, min_bytes: mb ?? 0, service: s.id });
	}
	return out;
};

// Pure: preset services to check. ids == null -> services that are switched on; otherwise the named ones
// (unknown id -> { error, id }). Returns { services: [ { id, name, targets } ] }.
export function preset_services(presets, cfg, ids) {
	let all = filter((type(presets?.services) == 'array') ? presets.services : [], (s) => type(s) == 'object' && type(s.id) == 'string');
	let out = [];
	if (ids != null) {
		for (let id in ids) {
			let s = filter(all, (x) => x.id == id)[0];
			if (!s)
				return { error: 'unknown_service', id: id };
			push(out, { id: s.id, name: s.name ?? s.id, targets: service_targets(s) });
		}
		return { services: out };
	}
	for (let s in all)
		if (service_active(s, cfg))
			push(out, { id: s.id, name: s.name ?? s.id, targets: service_targets(s) });
	return { services: out };
};

// Pure: preset targets of services that are switched on + first max_domains domains of active lists.
export function build_targets(presets, cfg, list_texts, max_domains) {
	let targets = [], seen = {};
	for (let s in preset_services(presets, cfg, null).services) {
		for (let t in s.targets) {
			if (seen[t.url])
				continue;
			seen[t.url] = true;
			push(targets, t);
		}
	}
	let n = 0;
	for (let text in list_texts) {
		for (let l in split(text ?? '', '\n')) {
			if (n >= max_domains)
				break;
			l = lc(trim(l));
			let c = substr(l, 0, 1);
			if (l == '' || c == '#' || c == ';' || c == '/')
				continue;
			if (c == '^')
				l = substr(l, 1);
			if (!V.domain_valid(l) || V.ipv4_valid(l) || V.ipv6_valid(l) || index(l, '.') < 0 || index(l, '_') >= 0)
				continue;
			let url = 'https://' + l + '/';
			if (seen[url])
				continue;
			seen[url] = true;
			push(targets, { url: url, min_bytes: 0, service: null });
			n++;
		}
	}
	return targets;
};

// Pure: share of reachable targets. ucode divides integers as integers (3/4 == 0), hence the 1.0 factor.
export function ratio(okc, total) {
	return total ? (okc * 1.0 / total) : 0;
};

// Pure: result ordering — ratio desc, then avg_ms asc; tested results before invalid ones.
export function sort_results(results) {
	return sort(results, (a, b) => {
		let da = (a.status == 'done') ? 0 : 1, db = (b.status == 'done') ? 0 : 1;
		if (da != db)
			return da - db;
		if (a.ratio != b.ratio)
			return (b.ratio > a.ratio) ? 1 : -1;
		let ma = a.avg_ms ?? 1000000000, mb = b.avg_ms ?? 1000000000;
		if (ma != mb)
			return ma - mb;
		return (a.id > b.id) ? 1 : ((a.id < b.id) ? -1 : 0);
	});
};

function candidates_base(idx, cfg, engine, opts, presets) {
	let avail = idx.items[engine] ?? {};
	let out = [];
	let add = (id) => {
		if (avail[id] && index(out, id) < 0)
			push(out, id);
	};
	if (type(opts.strategies) == 'array' && length(opts.strategies)) {
		for (let id in opts.strategies) {
			if (!avail[id])
				return { error: 'strategy_not_found', id: id };
			add(id);
		}
		return { ids: out };
	}
	if (opts.quick) {
		let d = presets?.defaults;
		if (type(d?.strategy) == 'string')
			add(d.strategy);
		for (let key in [ 'quick_test_strategies', 'quick_strategies' ])
			if (type(d?.[key]) == 'array')
				for (let id in d[key])
					if (length(out) < QUICK_TOP + 2)
						add(id);
		let bundle = sort(filter(keys(avail), (id) => avail[id].source == 'bundle'));
		for (let i = 0; i < length(bundle) && length(out) < QUICK_TOP + 1; i++)
			add(bundle[i]);
		return { ids: out };
	}
	add(C.current_strategy_id(cfg, engine));
	for (let id in sort(keys(avail)))
		add(id);
	return { ids: out };
};

// Pure: candidate list. opts: { strategies: [ids], quick: bool, apply_if_better: bool }. With apply_if_better the
// current strategy is always tested first: a candidate is applied only when it is strictly better than it.
export function candidates(idx, cfg, engine, opts, presets) {
	let c = candidates_base(idx, cfg, engine, opts, presets);
	let cur = C.current_strategy_id(cfg, engine);
	if (opts.apply_if_better && c.ids && cur && idx.items[engine]?.[cur] && index(c.ids, cur) < 0)
		unshift(c.ids, cur);
	return c;
};

// Pure: the result a test with --apply-if-better applies: the best tested strategy when its share of reachable
// targets is strictly higher than that of the original one (an original that did not run counts as 0).
export function pick_better(results, original) {
	let best = filter(results ?? [], (r) => r.status == 'done' && r.ok > 0)[0];
	if (!best || best.id == original)
		return null;
	let orig = filter(results ?? [], (r) => r.id == original)[0];
	let base = (orig?.status == 'done') ? orig.ratio : 0;
	return (best.ratio > base) ? best.id : null;
};


// Checks targets in parallel (automatic selection, `probe`, monitor). opts: { concurrency, timeout } override
// the test section; opts.user runs the downloads as that user (mode isolated, contract v1.6 §17). Returns { ok, total, avg_ms, targets: [ { url, ok, ms, bytes, http_status, error, detail } ] }.
export function probe_targets(targets, cfg, opts) {
	opts = opts ?? {};
	let tasks = [];
	for (let i = 0; i < length(targets); i++)
		push(tasks, { key: 't' + i, url: targets[i].url });
	let p = N.probe(tasks, { concurrency: opts.concurrency ?? cfg.test.concurrency, timeout: opts.timeout ?? cfg.test.timeout,
		ipv4only: !cfg.ipv6, user: opts.user });
	let out = [], okc = 0, ms_sum = 0, ms_n = 0;
	for (let i = 0; i < length(targets); i++) {
		let t = targets[i], r = p.results['t' + i] ?? { rc: -1, err: '', bytes: 0 };
		let c = N.classify(r.rc, r.err, r.bytes, t.min_bytes);
		push(out, {
			url: t.url,
			ok: c.ok,
			ms: r.ms,
			bytes: r.bytes,
			http_status: c.http_status,
			error: c.ok ? null : c.error,
			detail: c.ok ? null : r.summary
		});
		if (c.ok) {
			okc++;
			if (r.ms != null) {
				ms_sum += r.ms;
				ms_n++;
			}
		}
	}
	N.cleanup(p.dir);
	return { ok: okc, total: length(targets), avg_ms: ms_n ? int(ms_sum / ms_n) : null, targets: out };
};

function read_list_texts(idx, cfg) {
	let texts = [];
	for (let id in cfg.lists) {
		let it = idx.items.list?.[id];
		if (!it || !is_file(it.file) || S.is_gzip(it.file))
			continue;
		let text = fs.readfile(it.file, LIST_READ_LIMIT) ?? '';
		if ((fs.stat(it.file)?.size ?? 0) > LIST_READ_LIMIT) {
			let nl = rindex(text, '\n');
			text = (nl >= 0) ? substr(text, 0, nl) : '';
		}
		push(texts, text);
	}
	return texts;
}

// Default of hook fw_apply: `zaprett fw apply` in its own process (commands pass their own fw_apply instead).
function cli_fw_apply() {
	let r = run([ P.ucode, '-S', '--', P.cli, 'fw', 'apply', '--json' ], { timeout: 60000, limit: 65536 });
	let j = null;
	try {
		j = json(r.stdout);
	}
	catch (e) {
		j = null;
	}
	return (type(j) == 'object') ? j : fail('nft_apply_failed', 'fw apply: ' + trim(r.stdout + '\n' + r.stderr));
}

// Pure: configuration of the candidate engine in mode isolated — the same, but on the test queue qnum+1.
export function isolated_cfg(cfg) {
	let c = {};
	for (let k, v in cfg)
		c[k] = v;
	c.qnum = cfg.qnum + 1;
	return c;
};

// Restores the service to the state saved before the test. Mode exclusive: the engine returns to the UCI strategy
// (or stays stopped). Mode isolated: the test chains, instance `test` and stray checks are removed; the main engine
// kept working and is restarted only for a strategy applied by --apply-if-better, or started when it died meanwhile
// (never when the user stopped the service). Leftovers of an isolated test are removed whatever the state says.
// hooks: { init_action, instance_state, cleanup, leftover, fw_apply }.
export function restore(state, hooks) {
	hooks = hooks ?? {};
	state = state ?? read_json(state_path(), 65536);
	let had_override = is_file(override_path());
	let leftover = hooks.leftover ?? ISO.leftover;
	let iso_left = leftover();
	if (type(state) != 'object' && !had_override && !iso_left)
		return null;	// nothing left to restore (already done)
	let init_action = hooks.init_action ?? SV.init_action;
	let instance_state = hooks.instance_state ?? SV.instance_state;
	let cleanup = hooks.cleanup ?? ISO.cleanup;
	let isolated = (type(state) == 'object' && state.mode == 'isolated');
	fs.unlink(override_path());
	if (iso_left || isolated)
		cleanup(hooks.fw_apply ?? cli_fw_apply);
	let r;
	if (isolated) {
		let stopped = is_file(SV.stopped_path());
		if (!stopped && (state.applied || (state.was_running && !instance_state().running)))
			r = init_action('start');
		else
			r = { rc: 0, output: '' };
	}
	else if (type(state) != 'object')
		r = had_override ? init_action('start') : { rc: 0, output: '' };	// state unknown: return to what UCI says
	else if (state.was_running)
		r = init_action('start');
	else
		r = init_action('stop');
	fs.unlink(state_path());
	// a test that died leaves its results "running"; run() rewrites them itself after a normal restore
	let res = read_json(results_path(), 4194304);
	if (type(res) == 'object' && res.state == 'running') {
		res.state = 'failed';
		res.finished = time();
		write_json(results_path(), res);
	}
	return r;
};

// Prepares mode isolated (contract v1.6 §17). Returns null or the reason of the fallback to mode exclusive.
function isolate_setup(cfg, state, idx, h) {
	if (!ISO.write_state(state.uid, cfg.qnum + 1, null))
		return 'write_failed';
	let fw = h.fw_apply();
	if (!fw?.ok)
		return 'nft_rejected';
	// the second instance must be able to run at all: it starts first with the strategy the main engine runs now
	let g = G.generate(isolated_cfg(cfg), { engine: state.engine, strategy: state.strategy, index: idx, ignore_override: true,
		test_mode: true, keep_dry_run_cache: true });
	if (!g.ok || !ISO.write_candidate(g.engine, g.args))
		return 'instance_failed';
	h.init_action('start');
	// a queue that is taken already makes the engine exit right after its start
	if (h.check_ms > 0)
		sleep(h.check_ms);
	return h.wait_test(true, 5000).running ? null : 'instance_failed';
}

// Starts one candidate. Returns { status, message } when it could not be started, or null.
function start_candidate(isolated, state, sid, g, h) {
	if (isolated) {
		if (!ISO.write_candidate(g.engine, g.args) || !ISO.write_state(state.uid, state.test_qnum, g.ports))
			return { status: 'start_failed', message: 'Не удалось записать ' + ISO.args_path() };
		let fw = h.fw_apply();
		if (!fw?.ok)
			return { status: 'start_failed', message: 'Правила nftables для проверки не применены: ' + (fw?.message ?? fw?.error ?? '') };
	}
	else if (!write_json(override_path(), { engine: g.engine, strategy: sid, started: time() }))
		return { status: 'start_failed', message: 'Не удалось записать ' + override_path() };
	return null;
}

export function run(cfg, opts, ctx) {
	ctx = ctx ?? NULL_CTX;
	opts = opts ?? {};
	let engine = cfg.engine;
	if (!is_file(SV.engine_path(engine)))
		return fail('engine_missing', sprintf('Движок %s не установлен', engine));
	let idx = S.scan();
	let presets = read_json(P.presets, 1048576);
	let cand = candidates(idx, cfg, engine, opts, presets);
	if (cand.error)
		return fail(cand.error, sprintf('Стратегия «%s» не найдена для движка %s', cand.id, engine));
	if (!length(cand.ids))
		return fail('no_strategies', 'Нет установленных стратегий для проверки');
	let targets = build_targets(presets, cfg, read_list_texts(idx, cfg), cfg.test.max_domains);
	if (!length(targets))
		return fail('no_targets', 'Нет целей для проверки: включите хотя бы один список доменов или сервис в быстрой настройке');

	// Точки подмены для юнит-тестов (opts.hooks): управление движком и инстансом test, состояние procd, правила
	// nftables, пользователь проверок и опрос целей. По умолчанию — init-скрипт, procd, `zaprett fw apply`, probe.sh.
	let hooks = opts.hooks ?? {};
	let h = {
		init_action: hooks.init_action ?? SV.init_action,
		engine_running: hooks.wait_running ?? SV.wait_running,
		instance_state: hooks.instance_state ?? SV.instance_state,
		probe: hooks.probe ?? probe_targets,
		fw_apply: hooks.fw_apply ?? cli_fw_apply,
		test_user: hooks.test_user ?? ISO.test_user,
		wait_test: hooks.wait_test ?? ISO.wait_instance,
		cleanup: hooks.cleanup ?? ISO.cleanup,
		leftover: hooks.leftover ?? ISO.leftover,
		check_ms: hooks.check_ms ?? 1000
	};

	mkdir_p(P.run);
	let inst = h.instance_state();
	let user = h.test_user();
	let state = { started: time(), was_running: inst.running, engine: engine, strategy: C.current_strategy_id(cfg, engine),
		mode: 'isolated', mode_reason: null, uid: user?.uid, test_qnum: cfg.qnum + 1, applied: null };
	state.mode_reason = ISO.refusal(cfg, { enabled: cfg.enabled, running: inst.running, stopped: is_file(SV.stopped_path()),
		user: user }, opts);
	if (state.mode_reason)
		state.mode = 'exclusive';
	write_json(state_path(), state);
	let results = {
		started: time(), finished: 0, state: 'running', engine: engine,
		mode: state.mode, mode_reason: state.mode_reason,
		original_strategy: state.strategy, targets: map(targets, (t) => t.url),
		baseline: null, results: []
	};
	write_json(results_path(), results);
	log('notice', sprintf('автоподбор: %d стратегий, %d целей, режим %s%s', length(cand.ids), length(targets), state.mode,
		state.mode_reason ? ' (' + state.mode_reason + ')' : ''));

	let exc = null;
	try {
		if (state.mode == 'isolated') {
			ctx.progress(1, 'Подготовка проверки без отключения обхода');
			let why = isolate_setup(cfg, state, idx, h);
			if (why) {
				h.cleanup(h.fw_apply);
				state.mode = 'exclusive';
				state.mode_reason = why;
				write_json(state_path(), state);
				results.mode = state.mode;
				results.mode_reason = why;
				write_json(results_path(), results);
				log('warning', 'автоподбор: проверка без отключения обхода невозможна (' + why + '), движок будет остановлен на время подбора');
				ctx.log('Проверка без отключения обхода невозможна (' + why + '): обход на время подбора будет выключен');
			}
		}
		let isolated = (state.mode == 'isolated');
		let tcfg = isolated ? isolated_cfg(cfg) : cfg;
		let popts = isolated ? { user: ISO.USER } : null;

		ctx.progress(2, sprintf('Базовая проверка без обхода: %d целей', length(targets)));
		if (!isolated) {
			fs.unlink(override_path());
			h.init_action('stop');
		}
		// isolated: the test chains have no queue rules yet, the checks of the test user bypass both engines
		let base = h.probe(targets, cfg, popts);
		results.baseline = { ok: base.ok, total: base.total, avg_ms: base.avg_ms, targets: base.targets,
			note: (base.ok == base.total) ? 'no_blocking_detected' : null };
		write_json(results_path(), results);
		ctx.log(sprintf('Без обхода доступно %d из %d', base.ok, base.total));

		let n = length(cand.ids);
		for (let i = 0; i < n; i++) {
			if (ctx.cancelled())
				break;
			let sid = cand.ids[i], item = idx.items[engine][sid];
			ctx.progress(5 + int(90 * i / n), sprintf('Стратегия %d из %d: %s', i + 1, n, sid));
			let r = { id: sid, name: item.name, ok: 0, total: length(targets), ratio: 0, avg_ms: null, status: null, message: null, targets: [] };
			let g = G.generate(tcfg, { engine: engine, strategy: sid, index: idx, ignore_override: true, test_mode: true,
				keep_dry_run_cache: isolated });
			let fail_start = g.ok ? start_candidate(isolated, state, sid, g, h) : null;
			if (!g.ok) {
				r.status = 'invalid';
				r.message = g.message;
			}
			else if (fail_start) {
				r.status = fail_start.status;
				r.message = fail_start.message;
			}
			else {
				let a = h.init_action('start');
				if (cfg.test.settle > 0)
					sleep(cfg.test.settle * 1000);
				let st = isolated ? h.wait_test(true, 5000) : h.engine_running(true, 5000);
				if (!st.running) {
					r.status = 'engine_failed';
					r.message = 'Движок не запустился: ' + substr(a.output ?? '', 0, 300);
				}
				else {
					let pr = h.probe(targets, cfg, popts);
					r.ok = pr.ok;
					r.ratio = ratio(pr.ok, pr.total);
					r.avg_ms = pr.avg_ms;
					r.targets = pr.targets;
					r.status = 'done';
					r.nft_applied = NF.is_applied();
				}
			}
			ctx.log(sprintf('%s: %s %d/%d', sid, r.status, r.ok, r.total));
			push(results.results, r);
			results.results = sort_results(results.results);
			write_json(results_path(), results);
			ctx.partial({ tested: length(results.results), total: n, mode: state.mode });
		}
	}
	catch (e) {
		exc = e;
	}

	// --apply-if-better: UCI gets the winner before the restore, so the engine restarts once, already with it
	let applied = null;
	if (opts.apply_if_better && !exc && !ctx.cancelled()) {
		let win = pick_better(results.results, state.strategy);
		let ch = {};
		ch[C.strategy_option(engine)] = win;
		if (win && C.set(ch)) {
			applied = win;
			log('notice', sprintf('автоподбор: применена стратегия %s (лучше исходной %s)', win, state.strategy ?? '—'));
			ctx.log('Применена стратегия ' + win);
		}
	}
	results.applied = applied;
	state.applied = applied;

	ctx.progress(97, applied ? 'Запуск с новой стратегией' :
		((state.mode == 'isolated') ? 'Снятие проверочных правил' : 'Возврат исходной стратегии'));
	restore(state, { init_action: h.init_action, instance_state: h.instance_state, cleanup: h.cleanup, leftover: h.leftover,
		fw_apply: h.fw_apply });
	results.finished = time();
	results.state = exc ? 'failed' : (ctx.cancelled() ? 'cancelled' : 'done');
	write_json(results_path(), results);
	if (exc)
		return fail('internal_error', 'Ошибка автоподбора: ' + exc.message);
	let best = null;
	for (let r in results.results)
		if (r.status == 'done' && r.ok > 0) {
			best = r.id;
			break;
		}
	let msg = best ? sprintf('Лучшая стратегия: %s', best) : 'Ни одна стратегия не улучшила доступность';
	if (applied)
		msg += sprintf('; применена вместо %s', state.strategy ?? '—');
	return ok({ tested: length(results.results), best: best, baseline_ok: results.baseline?.ok, applied: applied,
		mode: state.mode, mode_reason: state.mode_reason, message: msg });
};

// test apply <id>
export function apply(cfg, id) {
	let idx = S.scan();
	let engine = cfg.engine;
	if (!idx.items[engine]?.[id])
		return fail('strategy_not_found', sprintf('Стратегия «%s» для движка %s не найдена', id, engine));
	if (is_file(override_path()))
		return fail('test_running', 'Идёт автоподбор. Дождитесь окончания или остановите его.');
	let g = G.generate(cfg, { engine: engine, strategy: id, index: idx, ignore_override: true });
	if (!g.ok)
		return g;
	let ch = {};
	ch[C.strategy_option(engine)] = id;
	if (!C.set(ch))
		return fail('uci_failed', 'Не удалось сохранить настройки');
	// same rule as other configuration changes: only a running engine is restarted
	let reloaded = false;
	if (SV.instance_state().running)
		reloaded = SV.init_action('reload').rc == 0;
	return ok({ strategy: id, engine: engine, reloaded: reloaded, warnings: g.warnings });
};
