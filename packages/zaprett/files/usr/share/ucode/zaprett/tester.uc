// zaprett: automatic strategy selection (ARCHITECTURE §10).
// The UCI strategy is never modified while testing: candidates are run through
// /var/run/zaprett/test-override, so removing that file always restores the original setup.
'use strict';

import * as fs from 'fs';
import { P, read_json, write_json, mkdir_p, is_file, log, ok, fail, NULL_CTX } from 'zaprett.util';
import * as V from 'zaprett.validate';
import * as C from 'zaprett.config';
import * as S from 'zaprett.store';
import * as G from 'zaprett.strategy';
import * as N from 'zaprett.net';
import * as NF from 'zaprett.nft';
import * as SV from 'zaprett.service';

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

// Pure: preset targets of services whose lists are active + first max_domains domains of active lists.
export function build_targets(presets, cfg, list_texts, max_domains) {
	let targets = [], seen = {};
	let services = (type(presets?.services) == 'array') ? presets.services : [];
	for (let s in services) {
		if (type(s) != 'object')
			continue;
		let lists = (type(s.lists) == 'array') ? s.lists : [];
		let ipsets = (type(s.ipsets) == 'array') ? s.ipsets : [];
		let on = false;
		for (let l in lists)
			if (index(cfg.lists, l) >= 0)
				on = true;
		for (let l in ipsets)
			if (index(cfg.ipsets, l) >= 0)
				on = true;
		if (!on || type(s.test_targets) != 'array')
			continue;
		for (let t in s.test_targets) {
			if (type(t) != 'object' || !V.url_valid(t.url) || seen[t.url])
				continue;
			seen[t.url] = true;
			let mb = V.parse_uint(t.min_bytes, 0, 1073741824);
			push(targets, { url: t.url, min_bytes: mb ?? 0, service: s.id });
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

// Pure: candidate list. opts: { strategies: [ids], quick: bool }
export function candidates(idx, cfg, engine, opts, presets) {
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

function probe_targets(targets, cfg) {
	let tasks = [];
	for (let i = 0; i < length(targets); i++)
		push(tasks, { key: 't' + i, url: targets[i].url });
	let p = N.probe(tasks, { concurrency: cfg.test.concurrency, timeout: cfg.test.timeout, ipv4only: !cfg.ipv6 });
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
}

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

// Restores the service to the state saved before the test.
export function restore(state) {
	state = state ?? read_json(state_path(), 65536);
	let had_override = is_file(override_path());
	if (type(state) != 'object' && !had_override)
		return null;	// nothing left to restore (already done)
	fs.unlink(override_path());
	let r;
	if (type(state) != 'object')
		r = SV.init_action('start');	// state unknown: return to what UCI says (start_service stops when disabled)
	else if (state.was_running)
		r = SV.init_action('start');
	else
		r = SV.init_action('stop');
	fs.unlink(state_path());
	return r;
};

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

	// Точки подмены для юнит-тестов (opts.hooks): управление движком, признак «движок работает» и опрос
	// целей. По умолчанию — настоящий init-скрипт, procd и probe.sh.
	let hooks = opts.hooks ?? {};
	let engine_action = hooks.init_action ?? SV.init_action;
	let engine_running = hooks.wait_running ?? SV.wait_running;
	let probe_fn = hooks.probe ?? probe_targets;

	mkdir_p(P.run);
	let inst = SV.instance_state();
	let state = { started: time(), was_running: inst.running, engine: engine, strategy: C.current_strategy_id(cfg, engine) };
	write_json(state_path(), state);
	let results = {
		started: time(), finished: 0, state: 'running', engine: engine,
		original_strategy: state.strategy, targets: map(targets, (t) => t.url),
		baseline: null, results: []
	};
	write_json(results_path(), results);
	log('notice', sprintf('автоподбор: %d стратегий, %d целей', length(cand.ids), length(targets)));

	let exc = null;
	try {
		ctx.progress(2, sprintf('Базовая проверка без обхода: %d целей', length(targets)));
		fs.unlink(override_path());
		engine_action('stop');
		let base = probe_fn(targets, cfg);
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
			let g = G.generate(cfg, { engine: engine, strategy: sid, index: idx, ignore_override: true, test_mode: true });
			if (!g.ok) {
				r.status = 'invalid';
				r.message = g.message;
			}
			else if (!write_json(override_path(), { engine: engine, strategy: sid, started: time() })) {
				r.status = 'start_failed';
				r.message = 'Не удалось записать ' + override_path();
			}
			else {
				let a = engine_action('start');
				if (cfg.test.settle > 0)
					sleep(cfg.test.settle * 1000);
				let st = engine_running(true, 5000);
				if (!st.running) {
					r.status = 'engine_failed';
					r.message = 'Движок не запустился: ' + substr(a.output ?? '', 0, 300);
				}
				else {
					let pr = probe_fn(targets, cfg);
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
			ctx.partial({ tested: length(results.results), total: n });
		}
	}
	catch (e) {
		exc = e;
	}

	ctx.progress(97, 'Возврат исходной стратегии');
	restore(state);
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
	return ok({ tested: length(results.results), best: best, baseline_ok: results.baseline?.ok,
		message: best ? sprintf('Лучшая стратегия: %s', best) : 'Ни одна стратегия не улучшила доступность' });
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
