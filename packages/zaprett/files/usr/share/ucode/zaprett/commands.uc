// zaprett: CLI command implementations (ARCHITECTURE §6.2). Every function returns a result object
// { ok: true, ... } or { ok: false, error, message }.
'use strict';

import * as fs from 'fs';
import { P, VERSION, run, read_json, mkdir_p, atomic_write, is_file, is_id, try_lock, wait_lock, unlock,
	log, ok, fail, tail_lines, meminfo_mib } from 'zaprett.util';
import * as V from 'zaprett.validate';
import * as C from 'zaprett.config';
import * as S from 'zaprett.store';
import * as G from 'zaprett.strategy';
import * as N from 'zaprett.nft';
import * as O from 'zaprett.offload';
import * as SV from 'zaprett.service';
import * as J from 'zaprett.job';
import * as R from 'zaprett.repo';
import * as T from 'zaprett.tester';
import * as ISO from 'zaprett.isolate';
import * as SRC from 'zaprett.sources';
import * as CR from 'zaprett.cron';
import * as H from 'zaprett.health';
import * as DNS from 'zaprett.dns';
import * as DG from 'zaprett.diagnose';

export const MAX_STDIN = 1048576;

// Item types whose files the engine reads only at start: replacing them needs a restart.
const RELOAD_TYPES = [ 'nfqws', 'nfqws2', 'bin', 'lua_lib' ];

function needs_reload(types) {
	for (let t in (types ?? []))
		if (index(RELOAD_TYPES, t) >= 0)
			return true;
	return false;
}

// UCI option holding active items of a list type (lists, exclude_lists, ipsets, exclude_ipsets).
function list_option(itype) {
	return S.TYPES[itype]?.uci;
}

export const TIER_FULL_MIN_RAM_MIB = 200;

function load_presets() {
	let p = read_json(P.presets, 1048576);
	return (type(p) == 'object' && type(p.services) == 'array') ? p : null;
}

function str_list(v) {
	return filter((type(v) == 'array') ? v : [], (x) => type(x) == 'string');
}

// Pure: a wizard argument "<service>" or "<service>:<variant>" -> { id, variant } (variant null: the core set of the
// service), or null when a part is not an id (contract v1.7 §16.4).
export function parse_service_ref(ref) {
	if (type(ref) != 'string')
		return null;
	let parts = split(ref, ':');
	if (length(parts) == 1 && is_id(parts[0]))
		return { id: parts[0], variant: null };
	if (length(parts) == 2 && is_id(parts[0]) && is_id(parts[1]))
		return { id: parts[0], variant: parts[1] };
	return null;
};

// Pure: item sets of a preset service — the core one (id null: lists/ipsets/sources of the service itself) first,
// then its variants (contract v1.5 §16.3). A variant without its own tier has the tier of the service; a variant
// with a bad or repeated id is ignored.
export function service_sets(s) {
	let out = [ { id: null, lists: str_list(s?.lists), ipsets: str_list(s?.ipsets), sources: str_list(s?.sources), tier: s?.tier } ];
	for (let v in ((type(s?.variants) == 'array') ? s.variants : [])) {
		if (type(v) != 'object' || !is_id(v.id) || length(filter(out, (x) => x.id == v.id)))
			continue;
		push(out, { id: v.id, lists: str_list(v.lists), ipsets: str_list(v.ipsets), sources: str_list(v.sources), tier: v.tier ?? s.tier,
			name: v.name, name_en: v.name_en, description: v.description, description_en: v.description_en });
	}
	return out;
};

function set_size(set) {
	return length(set.lists) + length(set.ipsets) + length(set.sources);
}

// Pure: how many items of an item set are switched on in cfg (a subscription counts when it is enabled and its
// src-<name> item is active). srcs: { name: source } from C.load_sources().
export function set_active(set, cfg, srcs) {
	let n = length(filter(set.lists, (x) => index(cfg.lists ?? [], x) >= 0)) + length(filter(set.ipsets, (x) => index(cfg.ipsets ?? [], x) >= 0));
	for (let name in set.sources) {
		let src = srcs?.[name];
		if (src && src.enabled && index(cfg[list_option(src.type)] ?? [], SRC.item_id(name)) >= 0)
			n++;
	}
	return n;
};

// Pure: id of the variant of a service whose items are all switched on (contract v1.7 §16.4: `enabled_variant`);
// of several such variants the largest wins, the first one on a tie; null when there is none.
export function enabled_variant(s, cfg, srcs) {
	let best = null, best_size = 0;
	for (let set in slice(service_sets(s), 1)) {
		let size = set_size(set);
		if (size > 0 && set_active(set, cfg, srcs) == size && size > best_size) {
			best = set.id;
			best_size = size;
		}
	}
	return best;
};

function copy_cfg(cfg) {
	return json(sprintf('%J', cfg));
}

function test_active() {
	return is_file(T.override_path());
}

// Refuses a configuration change that would make a working configuration fail.
function validate_change(old_cfg, new_cfg) {
	let g = G.generate(new_cfg, { ignore_override: true });
	if (g.ok)
		return g;
	let before = G.generate(old_cfg, { ignore_override: true });
	if (before.ok)
		return g;
	g.ok = true;
	g.warnings = [ 'config_was_invalid' ];
	return g;
}

// Applies a saved change to the running engine. A stopped engine is left stopped (the user may have
// stopped it on purpose); the new settings are used at the next start.
function reload_if_needed(cfg) {
	if (test_active())
		return { reloaded: false, reason: 'test_running' };
	if (!SV.instance_state().running)
		return { reloaded: false };
	let a = SV.init_action('reload');
	return { reloaded: a.rc == 0 };
}

// After repository/subscription changes: reload when the engine arguments changed (e.g. a list moved
// from the bundle to /etc/zaprett) or when payload files the engine reads only at start were replaced.
function reload_if_changed(force) {
	if (test_active() || !SV.instance_state().running)
		return { reloaded: false };
	if (!force) {
		let g = G.generate(C.load(), { skip_dry_run: true, no_ensure: true, ignore_override: true });
		if (!g.ok)
			return { reloaded: false, error: g.error };
		if (fs.readfile(P.run + '/args', 1048576) == join('\n', g.args) + '\n')
			return { reloaded: false };
	}
	let a = SV.init_action('reload');
	return { reloaded: a.rc == 0 };
}

function locked_call(lock, fn) {
	let r;
	try {
		r = fn();
	}
	catch (e) {
		r = fail('internal_error', 'Внутренняя ошибка: ' + e.message);
	}
	unlock(lock);
	return r;
}

function with_main_lock(fn) {
	let lock = wait_lock(P.lock_main, 60000);
	if (!lock)
		return fail('busy', 'Другая команда управления службой ещё выполняется');
	return locked_call(lock, fn);
}

// fw apply and fw remove come from init, hotplug and commands at the same time: one at a time.
function with_fw_lock(fn) {
	let lock = wait_lock(P.lock_fw, P.lock_fw_wait_ms);
	if (!lock)
		return fail('busy', 'Правила nftables сейчас меняет другая команда, повторите позже');
	return locked_call(lock, fn);
}

// Subscription changes take the job lock so that they never race with a running update job.
// The lock is held only for UCI and file changes; reloads happen after it is released.
function with_job_lock(fn) {
	let lock = try_lock(P.lock_job);
	if (!lock)
		return J.busy_fail(J.read());
	return locked_call(lock, fn);
}

export function read_stdin(limit) {
	let data = fs.stdin.read(limit + 1) ?? '';
	if (length(data) > limit)
		return null;
	return data;
};

// A test override left behind by a test job that died (OOM, kill -9) keeps the engine on a candidate
// strategy; it is rolled back as soon as someone looks at the state.
// An isolated test (contract v1.6 §17) that died leaves its test chains, instance `test` and state instead; they are
// removed with `zaprett fw apply` in its own process (fw_apply of this module is declared further down).
function test_restore() {
	return T.restore(null, null);
}

function recover_dead_test() {
	if (!test_active() && !T.leftover())
		return false;
	let j = J.read();
	if (j && j.name == 'test' && j.state == 'running')
		return false;
	test_restore();
	log('warning', 'автоподбор прервался аварийно: исходная стратегия возвращена, проверочные правила сняты');
	return true;
}

function cleanup_job(job) {
	return (job?.name == 'test') ? test_restore() : null;
}

// Status polls finish overdue cancellations and roll back tests that died.
function settle_jobs() {
	J.enforce_cancel(cleanup_job);
	return recover_dead_test();
}

/* ---------- service ---------- */

// Pure: are the active services/subscriptions too heavy for this router (contract v1.3 §14.4)? A switched-on
// service of tier "full" with less RAM than tiers.full.min_ram_mib, or enabled subscriptions that need more than
// half of MemAvailable. A variant of tier "full" counts when all its items are switched on (contract v1.7 §16.4).
export function memory_heavy(cfg, presets, sources, ram_total, mem_available) {
	let min_full = presets?.tiers?.full?.min_ram_mib ?? TIER_FULL_MIN_RAM_MIB;
	let srcs = {};
	for (let s in sources)
		srcs[s.name] = s;
	if (ram_total != null && ram_total < min_full)
		for (let s in ((type(presets?.services) == 'array') ? presets.services : [])) {
			if (type(s) != 'object')
				continue;
			if (s.tier == 'full' && T.service_active({ lists: s.lists, ipsets: s.ipsets, sources: s.sources }, cfg))
				return true;
			for (let set in slice(service_sets(s), 1))
				if (set.tier == 'full' && set_size(set) > 0 && set_active(set, cfg, srcs) == set_size(set))
					return true;
		}
	let need = 0;
	for (let s in sources)
		if (s.enabled && s.valid)
			need += s.ram_mib ?? 0;
	return mem_available != null && need * 2 > mem_available;
};

// Pure: is a switched-on preset service waiting for encrypted DNS (contract v1.4 §15.3: `dns_plain`)?
export function dns_plain(cfg, presets, dns) {
	if (dns?.encrypted)
		return false;
	for (let s in ((type(presets?.services) == 'array') ? presets.services : []))
		if (type(s) == 'object' && s.needs_dns == true && T.service_active(s, cfg))
			return true;
	return false;
};

function job_brief(j) {
	return j ? { id: j.id, name: j.name, state: j.state, progress: j.progress } : null;
}

export function status() {
	let recovered = settle_jobs();
	let cfg = C.load();
	let st = SV.status(cfg);
	if (recovered)
		st.details.recovered_test = true;
	st.job = job_brief(J.read());
	st.monitor = H.monitor_brief(cfg);
	let add = (w) => { if (index(st.warnings, w) < 0) push(st.warnings, w); };
	if (st.monitor && (st.monitor.state == 'degraded' || st.monitor.state == 'repairing'))
		add('monitor_degraded');
	let presets = load_presets();
	if (memory_heavy(cfg, presets, C.load_sources(), meminfo_mib('MemTotal'), meminfo_mib('MemAvailable')))
		add('low_memory');
	st.dns = DNS.status();
	if (dns_plain(cfg, presets, st.dns))
		add('dns_plain');
	return st;
};

export function start() {
	return with_main_lock(() => {
		if (test_active())
			return fail('test_running', 'Идёт автоподбор стратегии. Дождитесь окончания или остановите его.');
		let cfg = C.load();
		if (!cfg.enabled) {
			if (!C.set({ enabled: '1' }))
				return fail('uci_failed', 'Не удалось сохранить настройку enabled');
			cfg = C.load();
		}
		SV.init_action('enable');
		let g = G.generate(cfg, { ignore_override: true });
		if (!g.ok) {
			G.write_outputs(g);
			return g;
		}
		let a = SV.init_action('start');
		let st = SV.wait_running(true, 6000);
		if (!st.running)
			return fail('engine_not_running', 'Движок не запустился. Подробности: logread -e zaprett', { init_output: a.output });
		return ok({ running: true, pid: st.pid, warnings: g.warnings, nft_applied: N.is_applied() });
	});
};

export function stop() {
	return with_main_lock(() => {
		if (test_active())
			return fail('test_running', 'Идёт автоподбор стратегии. Остановите его командой zaprett test stop.');
		let a = SV.init_action('stop');
		O.restore();
		let st = SV.wait_running(false, 5000);
		if (st.running)
			return fail('stop_failed', 'Не удалось остановить движок', { init_output: a.output });
		return ok({ running: false });
	});
};

export function restart() {
	return with_main_lock(() => {
		if (test_active())
			return fail('test_running', 'Идёт автоподбор стратегии.');
		let cfg = C.load();
		if (!cfg.enabled)
			return fail('disabled', 'Служба выключена. Включите и запустите её командой zaprett start.');
		let g = G.generate(cfg, { ignore_override: true });
		if (!g.ok) {
			G.write_outputs(g);
			return g;
		}
		let a = SV.init_action('restart');
		let st = SV.wait_running(true, 6000);
		if (!st.running)
			return fail('engine_not_running', 'Движок не запустился. Подробности: logread -e zaprett', { init_output: a.output });
		return ok({ running: true, pid: st.pid, warnings: g.warnings });
	});
};

export function enable() {
	if (!C.set({ enabled: '1' }))
		return fail('uci_failed', 'Не удалось сохранить настройку enabled');
	let a = SV.init_action('enable');
	CR.sync();
	return ok({ enabled: true, autostart: a.rc == 0 });
};

export function disable() {
	return with_main_lock(() => {
		if (!C.set({ enabled: '0' }))
			return fail('uci_failed', 'Не удалось сохранить настройку enabled');
		SV.init_action('disable');
		if (!test_active())
			SV.init_action('stop');
		O.restore();
		CR.sync();
		return ok({ enabled: false, autostart: false });
	});
};

/* ---------- generator / firewall ---------- */

export function check() {
	let cfg = C.load();
	let g = G.generate(cfg, { ignore_override: true, force_dry_run: true });
	if (!g.ok)
		return g;
	return ok({
		engine: g.engine, strategy: g.strategy, args: g.args, ports: g.ports, dry_run: g.dry_run,
		warnings: g.warnings, details: g.details, dropped_tokens: g.dropped_tokens
	});
};

export function gen_args() {
	let cfg = C.load();
	let g = G.generate(cfg);
	G.write_outputs(g);
	if (!g.ok) {
		log('err', sprintf('ошибка подготовки аргументов (%s): %s', g.error, g.message));
		return g;
	}
	return ok({ path: P.run + '/args', engine: g.engine, strategy: g.strategy, warnings: g.warnings, test_mode: g.test_mode });
};

// Pure: rulesets `fw apply` tries in turn (contract v1.4 §15.1): with the own flowtable — hardware offloading first
// when it was on, then software — and without it. plan: result of O.flowtable_plan() (null = no flowtable wanted).
// isolation: test chains of an isolated automatic selection (contract v1.6 §17) or null.
export function fw_candidates(cfg, ports, wan, test_mode, plan, isolation) {
	let out = [];
	let add = (ft, kind) => push(out, { text: N.render(cfg, ports, wan, { test_mode: test_mode, flowtable: ft, isolation: isolation }),
		flowtable: kind });
	if (plan && length(plan.devices)) {
		if (length(plan.hw_devices ?? []))
			add({ devices: plan.hw_devices, hw: true }, 'hw');
		add({ devices: plan.devices, hw: false }, 'sw');
	}
	add(null, null);
	return out;
};

function render_current(cfg) {
	let g = G.generate(cfg, { skip_dry_run: true, no_ensure: true });
	if (!g.ok)
		return g;
	let wan = N.query_wan(cfg);
	let plan = O.flowtable_plan(cfg);
	let cands = fw_candidates(cfg, g.ports, wan, g.test_mode, plan, ISO.read_state());
	return ok({ text: cands[0].text, candidates: cands, flowtable_wanted: plan != null, wan: wan, ports: g.ports,
		test_mode: g.test_mode });
}

// Applies the first candidate the kernel accepts. A ruleset already in the kernel is not applied again
// (changed: false). hooks (unit tests): applied_text, is_applied, apply_text.
export function apply_candidates(cands, hooks) {
	hooks = hooks ?? {};
	let applied_text = hooks.applied_text ?? N.applied_text;
	let is_applied = hooks.is_applied ?? N.is_applied;
	let apply_text = hooks.apply_text ?? N.apply_text;
	let cur = applied_text(), present = null, last = null;
	for (let c in cands) {
		if (cur == c.text) {
			present = present ?? is_applied();
			if (present)
				return ok({ changed: false, flowtable: c.flowtable });
		}
		let a = apply_text(c.text);
		if (a.ok)
			return ok({ changed: true, flowtable: c.flowtable });
		last = a;
		if (c.flowtable != null)
			log('warning', sprintf('правила с собственной flowtable (%s) не приняты ядром: %s', c.flowtable, a.message));
	}
	return last;
};

export function fw_apply(flags) {
	return with_fw_lock(() => {
		let cfg = C.load();
		if (flags.if_running && !SV.instance_state().running)
			return ok({ skipped: true, changed: false });
		if (flags.if_applied && !is_file(N.nft_path()))
			return ok({ skipped: true, changed: false });
		let r = render_current(cfg);
		if (!r.ok) {
			log('err', 'правила nftables не применены: ' + r.message);
			return r;
		}
		let warnings = [];
		if (!length(r.wan.v4) && !(cfg.ipv6 && length(r.wan.v6)))
			push(warnings, 'no_wan');
		let wan = { v4: r.wan.v4, v6: r.wan.v6 };
		// hotplug and init call this on every interface event: the same ruleset over a present table is not
		// applied again (contract v1.3 §14.3)
		let a = apply_candidates(r.candidates, null);
		if (!a.ok) {
			log('err', a.message);
			return a;
		}
		if (r.flowtable_wanted && a.flowtable == null)
			push(warnings, 'flowtable_failed');
		return ok({ applied: true, changed: a.changed, flowtable: a.flowtable, wan: wan, ports: r.ports, warnings: warnings });
	});
};

export function fw_remove() {
	return with_fw_lock(() => N.remove());
};

export function fw_show() {
	let r = render_current(C.load());
	if (!r.ok)
		return r;
	return ok({ text: r.text, applied: N.is_applied(), wan: r.wan, ports: r.ports });
};

/* ---------- items / lists / strategies ---------- */

export function items(opts) {
	if (opts.type != null && !S.TYPES[opts.type])
		return fail('bad_type', 'Неизвестный тип элемента: ' + opts.type);
	let idx = S.scan();
	return ok({ items: S.describe(idx, C.load(), opts.type), errors: idx.errors });
};

export function list_toggle(id, enable_it) {
	if (!is_id(id))
		return fail('bad_id', 'Недопустимый идентификатор');
	let idx = S.scan();
	let found = S.find_all(idx, id, S.LIST_TYPES);
	// a subscription can be switched on before its first download: the type comes from its UCI section
	if (!length(found) && substr(id, 0, 4) == 'src-') {
		let src = filter(C.load_sources(), (s) => s.name == substr(id, 4) && s.valid)[0];
		if (src)
			found = [ { id: id, type: src.type } ];
	}
	if (!length(found))
		return fail('not_found', sprintf('Список «%s» не найден', id));
	if (length(found) > 1)
		return fail('ambiguous_id', sprintf('Идентификатор «%s» есть в нескольких типах списков', id));
	let it = found[0], opt = S.TYPES[it.type].uci;
	let cfg = C.load(), ncfg = copy_cfg(cfg);
	let arr = filter(cfg[opt], (x) => x != id);
	if (enable_it)
		push(arr, id);
	if (length(arr) == length(cfg[opt]) && (index(cfg[opt], id) >= 0) == enable_it)
		return ok({ id: id, type: it.type, enabled: enable_it, changed: false });
	ncfg[opt] = arr;
	let v = validate_change(cfg, ncfg);
	if (!v.ok)
		return v;
	let ch = {};
	ch[opt] = arr;
	if (!C.set(ch))
		return fail('uci_failed', 'Не удалось сохранить настройки');
	let rl = reload_if_needed(ncfg);
	return ok({ id: id, type: it.type, enabled: enable_it, changed: true, reloaded: rl.reloaded, warnings: v.warnings });
};

export function strategy_set(id) {
	if (!is_id(id))
		return fail('bad_id', 'Недопустимый идентификатор');
	let cfg = C.load();
	let idx = S.scan();
	if (!idx.items[cfg.engine]?.[id])
		return fail('strategy_not_found', sprintf('Стратегия «%s» для движка %s не найдена', id, cfg.engine));
	let g = G.generate(cfg, { engine: cfg.engine, strategy: id, index: idx, ignore_override: true });
	if (!g.ok)
		return g;
	let ch = {};
	ch[C.strategy_option(cfg.engine)] = id;
	if (!C.set(ch))
		return fail('uci_failed', 'Не удалось сохранить настройки');
	let ncfg = copy_cfg(cfg);
	ncfg[C.strategy_option(cfg.engine)] = id;
	let rl = reload_if_needed(ncfg);
	return ok({ id: id, engine: cfg.engine, reloaded: rl.reloaded, warnings: g.warnings });
};

export function strategy_show(id) {
	if (!is_id(id))
		return fail('bad_id', 'Недопустимый идентификатор');
	let cfg = C.load();
	let idx = S.scan();
	let item = idx.items[cfg.engine]?.[id] ?? S.find(idx, id, [ 'nfqws', 'nfqws2', 'byedpi' ]);
	if (!item)
		return fail('strategy_not_found', sprintf('Стратегия «%s» не найдена', id));
	let text = S.read_strategy_text(item);
	if (text == null)
		return fail('strategy_unreadable', 'Не удалось прочитать файл стратегии');
	let res = ok({ id: id, type: item.type, name: item.name, source: item.source, description: item.description,
		dependencies: item.dependencies, text: text, args: null, ports: null });
	if (item.type == 'nfqws' || item.type == 'nfqws2') {
		let b = G.generate(cfg, { engine: item.type, strategy: id, index: idx, ignore_override: true, skip_dry_run: true, no_ensure: true });
		if (b.ok) {
			res.args = b.args;
			res.ports = b.ports;
			res.warnings = b.warnings;
		}
		else {
			res.build_error = b.error;
			res.build_message = b.message;
		}
	}
	return res;
};

export function strategy_save(id, text) {
	if (!is_id(id) || substr(id, 0, 5) != 'user-' || length(id) < 6)
		return fail('bad_id', 'Имя своей стратегии должно начинаться с «user-» и содержать только латиницу, цифры, «.», «_», «-»');
	if (text == null || length(text) > S.MAX_STRATEGY_BYTES)
		return fail('too_large', 'Текст стратегии больше 64 КиБ');
	let cfg = C.load();
	let engine = cfg.engine;
	let item = { id: id, name: id, source: 'user', dependencies: [], type: engine };
	let g = G.generate(cfg, { engine: engine, text: text, item: item, ignore_override: true });
	if (!g.ok)
		return g;
	let path = S.user_strategy_path(engine, id);
	if (!mkdir_p(fs.dirname(path)) || !atomic_write(path, text))
		return fail('write_failed', 'Не удалось сохранить стратегию');
	let reloaded = false;
	if (C.current_strategy_id(cfg, engine) == id)
		reloaded = reload_if_needed(cfg).reloaded;
	return ok({ id: id, engine: engine, args: g.args, ports: g.ports, warnings: g.warnings, reloaded: reloaded });
};

export function strategy_delete(id) {
	if (!is_id(id) || substr(id, 0, 5) != 'user-')
		return fail('bad_id', 'Удалять можно только свои стратегии (с префиксом «user-»)');
	let cfg = C.load();
	let deleted = [];
	for (let eng in S.STRATEGY_TYPES) {
		let path = S.user_strategy_path(eng, id);
		if (!is_file(path))
			continue;
		if (C.current_strategy_id(cfg, eng) == id)
			return fail('item_active', sprintf('Стратегия «%s» сейчас выбрана. Сначала выберите другую.', id));
		fs.unlink(path);
		push(deleted, eng);
	}
	if (!length(deleted))
		return fail('not_found', sprintf('Своя стратегия «%s» не найдена', id));
	return ok({ id: id, engines: deleted });
};

export function user_get(id) {
	let path = S.user_list_path(id);
	if (!path)
		return fail('bad_id', 'Доступны только user-hosts, user-hosts-exclude, user-ipset, user-ipset-exclude');
	let text = fs.readfile(path, V.MAX_USER_LIST_BYTES + 1) ?? '';
	return ok({ id: id, type: S.USER_LISTS[id].type, text: text, entries: V.count_entries(text), size: length(text) });
};

export function user_set(id, text) {
	let path = S.user_list_path(id);
	if (!path)
		return fail('bad_id', 'Доступны только user-hosts, user-hosts-exclude, user-ipset, user-ipset-exclude');
	if (text == null)
		return fail('too_large', 'Список больше 1 МиБ');
	let kind = S.TYPES[S.USER_LISTS[id].type].kind;
	let v = V.validate_list_text(kind, text);
	if (!v.ok)
		return fail('invalid_entries', sprintf('Неверных строк: %d. Исправьте их и сохраните снова.', v.error_count ?? length(v.errors)),
			{ errors: v.errors });
	let before = V.count_entries(fs.readfile(path, V.MAX_USER_LIST_BYTES + 1) ?? '');
	if (!mkdir_p(P.user) || !atomic_write(path, v.text))
		return fail('write_failed', 'Не удалось сохранить список');
	let cfg = C.load();
	let opt = S.TYPES[S.USER_LISTS[id].type].uci;
	let reloaded = false;
	// nfqws re-reads list files by itself; regeneration matters only when a list turns empty/non-empty
	if (index(cfg[opt], id) >= 0 && ((before == 0) != (v.entries == 0)))
		reloaded = reload_if_needed(cfg).reloaded;
	return ok({ id: id, entries: v.entries, size: length(v.text), reloaded: reloaded });
};

export function set_mode(mode) {
	if (mode != 'whitelist' && mode != 'blacklist')
		return fail('bad_value', 'Режим должен быть whitelist или blacklist');
	let cfg = C.load(), ncfg = copy_cfg(cfg);
	ncfg.list_mode = mode;
	let v = validate_change(cfg, ncfg);
	if (!v.ok)
		return v;
	if (!C.set({ list_mode: mode }))
		return fail('uci_failed', 'Не удалось сохранить настройки');
	return ok({ list_mode: mode, reloaded: reload_if_needed(ncfg).reloaded, warnings: v.warnings });
};

// Pure: the default strategy of the presets for an engine (defaults.strategy / defaults.strategy_nfqws2), or null.
export function default_strategy(presets, engine) {
	let d = presets?.defaults?.[(engine == 'nfqws2') ? 'strategy_nfqws2' : 'strategy'];
	return is_id(d) ? d : null;
};

export function set_engine(engine) {
	if (engine != 'nfqws' && engine != 'nfqws2')
		return fail('bad_value', 'Движок должен быть nfqws или nfqws2');
	if (!is_file(SV.engine_path(engine)))
		return fail('engine_missing', sprintf('Движок %s не установлен (пакет zaprett-%s)', engine, engine));
	let cfg = C.load(), ncfg = copy_cfg(cfg);
	ncfg.engine = engine;
	let changes = { engine: engine };
	// no strategy chosen for this engine yet: the default of the presets, when it is installed
	let sopt = C.strategy_option(engine);
	let dstrat = default_strategy(load_presets(), engine);
	if (!C.current_strategy_id(cfg, engine) && dstrat && S.scan().items[engine]?.[dstrat]) {
		ncfg[sopt] = dstrat;
		changes[sopt] = dstrat;
	}
	let v = validate_change(cfg, ncfg);
	if (!v.ok)
		return v;
	if (!C.set(changes))
		return fail('uci_failed', 'Не удалось сохранить настройки');
	return ok({ engine: engine, strategy: ncfg[sopt] || null, reloaded: reload_if_needed(ncfg).reloaded, warnings: v.warnings });
};

/* ---------- cron (autoupdate of repository items and subscriptions) ---------- */

// The schedule itself lives in zaprett.cron (autoupdate, watchdog, monitor lines, contract v1.3 §14.2).
export const CRON_MARK = CR.MARK_AUTOUPDATE;

// Pure: crontab text with exactly one (or no) autoupdate line; other lines, including the watchdog and monitor
// ones, are kept.
export function cron_render(text, enabled, hour, random_minute) {
	let want = {};
	want[CR.MARK_AUTOUPDATE] = enabled ? CR.autoupdate_line(hour) : null;
	return CR.render(text, want, random_minute);
};

export function cron_needed(cfg, sources) {
	return CR.autoupdate_needed(cfg, sources);
};

export function cron_sync() {
	return CR.sync();
};

/* ---------- subscriptions (contract v1.1) ---------- */

function is_default_source(name) {
	return length(filter(C.DEFAULT_SOURCES, (d) => d.name == name)) > 0;
}

export function sources_list() {
	return SRC.list();
};

// Pure: validated UCI values of a subscription from the `sources save` JSON and the current section.
// Returns { values } or a fail object.
export function source_values(o, cur) {
	if (type(o) != 'object')
		return fail('bad_value', 'Ожидается JSON-объект {title,type,url,interval_hours,min_entries,enabled}');
	let typ = o.type ?? cur?.type;
	let url = o.url ?? cur?.url;
	if (index(C.SOURCE_TYPES, typ) < 0)
		return fail('bad_value', 'Тип подписки: list, list_exclude, ipset или ipset_exclude');
	if (!C.source_url_valid(url))
		return fail('bad_value', 'Ссылка подписки должна начинаться с https:// и не содержать пробелов');
	let values = { type: typ, url: url };
	if (o.title != null) {
		if (type(o.title) != 'string' || length(o.title) > 128 || index(o.title, '\n') >= 0)
			return fail('bad_value', 'Название — строка до 128 символов');
		values.name = o.title;
	}
	for (let k in [ [ 'interval_hours', 1, 8760 ], [ 'min_entries', 0, 100000000 ] ]) {
		if (o[k[0]] == null)
			continue;
		let n = V.parse_uint('' + o[k[0]], k[1], k[2]);
		if (n == null)
			return fail('bad_value', sprintf('%s: целое число от %d до %d', k[0], k[1], k[2]));
		values[k[0]] = n;
	}
	if (o.enabled != null)
		values.enabled = (o.enabled == true || o.enabled == 1 || o.enabled == '1') ? '1' : '0';
	return { ok: true, values: values };
};

export function sources_save(name, text) {
	if (!C.source_name_valid(name))
		return fail('bad_id', 'Имя подписки: только a-z, 0-9 и «_», до 32 символов');
	if (text == null)
		return fail('too_large', 'Слишком большие данные');
	let o = null;
	try {
		o = json(text);
	}
	catch (e) {
		o = null;
	}
	if (type(o) != 'object')
		return source_values(o, null);
	let r = with_job_lock(() => {
		let cur = filter(C.load_sources(), (s) => s.name == name)[0];
		let sv = source_values(o, cur);
		if (!sv.ok)
			return sv;
		let values = sv.values, iid = SRC.item_id(name);
		let cfg = C.load();
		let changes = {};
		let type_changed = cur != null && cur.type != values.type;
		let url_changed = cur != null && cur.url != values.url;
		// a changed type moves the id to the option of the new type: the subscription stays switched on
		let old_opt = type_changed ? list_option(cur.type) : null;
		if (old_opt && index(cfg[old_opt], iid) >= 0) {
			let new_opt = list_option(values.type);
			changes[old_opt] = filter(cfg[old_opt], (x) => x != iid);
			if (index(cfg[new_opt], iid) < 0) {
				changes[new_opt] = slice(cfg[new_opt]);
				push(changes[new_opt], iid);
			}
		}
		// a default subscription created again is no longer "deleted by the user"
		if (index(cfg.deleted_sources, name) >= 0)
			changes.deleted_sources = filter(cfg.deleted_sources, (x) => x != name);
		// UCI first: if it fails, the downloaded files are still consistent with the configuration
		if (!C.set_source(name, values))
			return fail('uci_failed', 'Не удалось сохранить подписку (имя занято секцией другого типа?)');
		if (length(keys(changes)) && !C.set(changes))
			return fail('uci_failed', 'Не удалось перенести подписку в списки нового типа');
		if (type_changed)
			SRC.remove_item(name);
		else if (url_changed)
			SRC.reset_state(name);
		return ok({ name: name, item_id: iid, type: values.type, created: cur == null, type_changed: type_changed,
			url_changed: url_changed });
	});
	if (!r.ok)
		return r;
	r.reloaded = reload_if_changed(false).reloaded;
	cron_sync();
	return r;
};

export function sources_delete(name) {
	if (!C.source_name_valid(name))
		return fail('bad_id', 'Недопустимое имя подписки');
	let r = with_job_lock(() => {
		if (!length(filter(C.load_sources(), (s) => s.name == name)))
			return fail('not_found', sprintf('Подписка «%s» не найдена', name));
		let cfg = C.load(), iid = SRC.item_id(name);
		let changes = {};
		for (let t in S.LIST_TYPES) {
			let opt = list_option(t);
			if (index(cfg[opt], iid) >= 0)
				changes[opt] = filter(cfg[opt], (x) => x != iid);
		}
		// uci-defaults must not bring a deleted default subscription back after an upgrade
		if (is_default_source(name) && index(cfg.deleted_sources, name) < 0) {
			changes.deleted_sources = slice(cfg.deleted_sources);
			push(changes.deleted_sources, name);
		}
		if ((length(keys(changes)) && !C.set(changes)) || !C.delete_source(name))
			return fail('uci_failed', 'Не удалось удалить подписку');
		return ok({ name: name, removed_item: SRC.remove_item(name) });
	});
	if (!r.ok)
		return r;
	r.reloaded = reload_if_changed(false).reloaded;
	cron_sync();
	return r;
};

// uci-defaults (first install and every upgrade): adds default subscriptions missing from the kept
// configuration, except those the user deleted (main.deleted_sources).
export function sources_defaults() {
	let cfg = C.load();
	let have = {};
	for (let s in C.load_sources())
		have[s.name] = true;
	let added = [], skipped = [];
	for (let d in C.DEFAULT_SOURCES) {
		if (have[d.name] || index(cfg.deleted_sources, d.name) >= 0)
			continue;
		if (C.set_source(d.name, d.values))
			push(added, d.name);
		else
			push(skipped, d.name);
	}
	return ok({ added: added, skipped: skipped });
};

/* ---------- presets / wizard ---------- */


function ram_total_mib() {
	return meminfo_mib('MemTotal');
}

function set_available(set, idx, srcs) {
	return length(filter(set.lists, (x) => idx.items.list[x] != null)) + length(filter(set.ipsets, (x) => idx.items.ipset[x] != null)) +
		length(filter(set.sources, (n) => srcs[n]?.valid));
}

export function presets() {
	let p = load_presets();
	if (!p)
		return fail('presets_missing', 'Файл пресетов не найден или повреждён: ' + P.presets);
	let cfg = C.load(), idx = S.scan();
	let srcs = {};
	for (let s in C.load_sources())
		srcs[s.name] = s;
	let services = [];
	for (let s in p.services) {
		if (type(s) != 'object' || !is_id(s.id))
			continue;
		let sets = service_sets(s), core = sets[0];
		let all = set_size(core), active = set_active(core, cfg, srcs), available = set_available(core, idx, srcs);
		let variants = [];
		for (let v in slice(sets, 1)) {
			let size = set_size(v);
			push(variants, {
				id: v.id, name: v.name, name_en: v.name_en, description: v.description, description_en: v.description_en,
				lists: v.lists, ipsets: v.ipsets, sources: v.sources, tier: v.tier,
				enabled: size > 0 && set_active(v, cfg, srcs) == size,
				available: size > 0 && set_available(v, idx, srcs) == size && s.works != 'no'
			});
		}
		push(services, {
			id: s.id, name: s.name, description: s.description, note: s.note, tier: s.tier, works: s.works,
			name_en: s.name_en, description_en: s.description_en, note_en: s.note_en,
			lists: core.lists, ipsets: core.ipsets, sources: core.sources, test_targets: s.test_targets ?? [],
			enabled: all > 0 && active == all,
			partially_enabled: active > 0 && active < all,
			available: all > 0 && available == all && s.works != 'no',
			variants: variants,
			enabled_variant: enabled_variant(s, cfg, srcs)
		});
	}
	let ram = ram_total_mib();
	return ok({ schema: p.schema, services: services, defaults: p.defaults ?? {}, always: p.always ?? {}, tiers: p.tiers ?? {},
		ram_total_mib: ram, recommended_tier: (ram != null && ram < TIER_FULL_MIN_RAM_MIB) ? 'light' : 'full' });
};

// The wizard owns the lists of the preset services: it never switches off a user list, even if a preset names one.
function is_user_item(x) {
	return substr(x, 0, 5) == 'user-';
}

// refs: "<service>" or "<service>:<variant>" (contract v1.7 §16.4). The chosen item set of every selected service is
// switched on; all other sets of all preset services (other variants of a selected service, every set of a service
// that is not selected) are switched off, except items that a chosen set uses too.
export function wizard_apply(refs) {
	let p = load_presets();
	if (!p)
		return fail('presets_missing', 'Файл пресетов не найден или повреждён');
	if (!length(refs))
		return fail('bad_args', 'Укажите хотя бы один сервис');
	let by_id = {};
	for (let s in p.services)
		if (type(s) == 'object' && is_id(s.id))
			by_id[s.id] = s;
	let selected = [], chosen = {}, skipped = [];
	for (let ref in refs) {
		let r = parse_service_ref(ref);
		if (!r)
			return fail('bad_args', sprintf('Неверный сервис «%s»: ожидается <сервис> или <сервис>:<вариант>', ref));
		let s = by_id[r.id];
		if (!s)
			return fail('unknown_service', sprintf('Сервис «%s» не найден в пресетах', r.id));
		let set = filter(service_sets(s), (x) => x.id == r.variant)[0];
		if (!set)
			return fail('unknown_variant', sprintf('У сервиса «%s» нет варианта «%s»', r.id, r.variant));
		if (chosen[r.id] && chosen[r.id].id != set.id)
			return fail('bad_args', sprintf('Сервис «%s» указан с разными вариантами', r.id));
		if (s.works == 'no') {
			push(skipped, { id: r.id, reason: 'works_no' });
			continue;
		}
		if (!set_size(set))
			return fail('preset_unavailable', sprintf('Для сервиса «%s» нет ни списков, ни подписок', ref));
		if (!chosen[r.id]) {
			chosen[r.id] = set;
			push(selected, r.id);
		}
	}
	if (!length(selected))
		return fail('preset_unavailable', 'Выбранные сервисы нельзя включить: обход для них не работает', { skipped: skipped });

	let cfg = C.load(), idx = S.scan();
	let srcs = {};
	for (let s in C.load_sources())
		srcs[s.name] = s;
	let opts = { lists: slice(cfg.lists), ipsets: slice(cfg.ipsets), exclude_lists: slice(cfg.exclude_lists), exclude_ipsets: slice(cfg.exclude_ipsets) };
	// items of the sets that are not chosen are switched off; a subscription stops updating (enabled=0) unless
	// a chosen set uses it
	let add = (opt, x) => { if (index(opts[opt], x) < 0) push(opts[opt], x); };
	let drop = (opt, x) => { if (!is_user_item(x)) opts[opt] = filter(opts[opt], (y) => y != x); };
	let used_sources = {}, disable_sources = [];
	for (let id in selected)
		for (let n in chosen[id].sources)
			used_sources[n] = true;
	for (let id, s in by_id) {
		for (let set in service_sets(s)) {
			if (chosen[id] != null && chosen[id].id == set.id)
				continue;
			for (let l in set.lists)
				drop('lists', l);
			for (let l in set.ipsets)
				drop('ipsets', l);
			for (let n in set.sources) {
				if (!srcs[n])
					continue;
				drop(list_option(srcs[n].type) ?? 'lists', SRC.item_id(n));
				if (!used_sources[n] && srcs[n].enabled && index(disable_sources, n) < 0)
					push(disable_sources, n);
			}
		}
	}
	let missing = [], enable_sources = [];
	for (let id in selected) {
		let set = chosen[id];
		for (let l in set.lists) {
			if (!idx.items.list[l])
				push(missing, l);
			else
				add('lists', l);
		}
		for (let l in set.ipsets) {
			if (!idx.items.ipset[l])
				push(missing, l);
			else
				add('ipsets', l);
		}
		for (let n in set.sources) {
			let src = srcs[n];
			if (!src?.valid) {
				push(missing, 'source:' + n);
				continue;
			}
			add(list_option(src.type), SRC.item_id(n));
			if (index(enable_sources, n) < 0)
				push(enable_sources, n);
		}
	}
	// "always" exclusions of the presets (government, banks...) are added and never removed here
	for (let l in str_list(p.always?.exclude_lists)) {
		if (!idx.items.list_exclude[l])
			push(missing, l);
		else
			add('exclude_lists', l);
	}
	for (let l in str_list(p.always?.exclude_ipsets)) {
		if (!idx.items.ipset_exclude[l])
			push(missing, l);
		else
			add('exclude_ipsets', l);
	}
	if (length(missing))
		return fail('preset_item_missing', 'Не найдены списки или подписки пресета: ' + join(', ', missing), { missing: missing });

	let ncfg = copy_cfg(cfg);
	let changes = { list_mode: 'whitelist' };
	for (let k, v in opts) {
		ncfg[k] = v;
		changes[k] = v;
	}
	ncfg.list_mode = 'whitelist';
	let sopt = C.strategy_option(cfg.engine);
	let sid = C.current_strategy_id(cfg, cfg.engine);
	let dstrat = p.defaults?.strategy;
	if (cfg.engine == 'nfqws' && (!sid || !idx.items.nfqws[sid]) && is_id(dstrat) && idx.items.nfqws[dstrat]) {
		ncfg[sopt] = dstrat;
		changes[sopt] = dstrat;
	}
	let v = validate_change(cfg, ncfg);
	if (!v.ok)
		return v;
	if (!C.set(changes))
		return fail('uci_failed', 'Не удалось сохранить настройки');
	for (let n in enable_sources)
		if (!srcs[n].enabled)
			C.set_source(n, { enabled: '1' });
	for (let n in disable_sources)
		C.set_source(n, { enabled: '0' });
	let variants = {};
	for (let id in selected)
		variants[id] = chosen[id].id;
	let res = ok({ services: selected, variants: variants, skipped: skipped, lists: opts.lists, ipsets: opts.ipsets,
		exclude_lists: opts.exclude_lists, exclude_ipsets: opts.exclude_ipsets, list_mode: 'whitelist', strategy: ncfg[sopt],
		sources: enable_sources, sources_disabled: disable_sources, reloaded: reload_if_needed(ncfg).reloaded, warnings: v.warnings ?? [] });
	// a chosen set of tier "full" on a small router is applied, but with a warning (contract v1.3 §14.3, v1.7 §16.4)
	let ram = ram_total_mib(), min_full = p.tiers?.full?.min_ram_mib ?? TIER_FULL_MIN_RAM_MIB;
	if (ram != null && ram < min_full && length(filter(selected, (id) => chosen[id].tier == 'full')) &&
	    index(res.warnings, 'low_memory') < 0)
		push(res.warnings, 'low_memory');
	cron_sync();
	// an error of the side job is not a configuration warning (contract v1.2 §6.2)
	if (length(enable_sources)) {
		let j = J.start('sources-update', enable_sources);
		if (j.ok)
			res.job = j.job;
		else
			res.job_error = { code: j.error, message: j.message };
	}
	return res;
};

/* ---------- repository ---------- */

export function repo_list(opts) {
	if (opts.type != null && !S.TYPES[opts.type])
		return fail('bad_type', 'Неизвестный тип элемента: ' + opts.type);
	return R.list(C.load(), opts.type);
};

function reload_after_install(res) {
	if (!res.ok)
		return res;
	res.reloaded = reload_if_changed(needs_reload(res.types)).reloaded;
	return res;
}

export const JOB_HANDLERS = {
	'repo-fetch': (ctx, args, flags) => R.fetch(C.load(), ctx),
	'repo-install': (ctx, args, flags) => reload_after_install(R.install(C.load(), args, ctx, {})),
	'repo-remove': (ctx, args, flags) => reload_after_install(R.remove(C.load(), args[0])),
	'repo-upgrade': (ctx, args, flags) => reload_after_install(R.upgrade(C.load(), flags.all ? null : args, ctx, {})),
	'autoupdate': (ctx, args, flags) => {
		let cfg = C.load();
		// enabled subscriptions follow their own intervals whatever repo.autoupdate says;
		// repository items are upgraded only with repo.autoupdate=1
		let s = SRC.update(null, ctx, { due_only: true });
		let r = cfg.repo.autoupdate ? R.upgrade(cfg, null, ctx, {}) :
			ok({ skipped: true, message: 'Автообновление элементов репозитория выключено' });
		let reloaded = reload_if_changed(needs_reload(r.types)).reloaded;
		if (!r.ok || !s.ok)
			return fail(r.ok ? s.error : r.error, join('; ', filter([ r.ok ? null : r.message, s.ok ? null : s.message ], (x) => x != null)),
				{ repo: r, sources: s, reloaded: reloaded });
		return ok({ repo: r, sources: s, reloaded: reloaded, message: 'Автообновление выполнено' });
	},
	'sources-update': (ctx, args, flags) => {
		let r = SRC.update(length(args) ? args : null, ctx, {});
		r.reloaded = reload_if_changed(false).reloaded;
		return r;
	},
	'test': (ctx, args, flags) => T.run(C.load(), { quick: flags.quick, strategies: flags.strategies,
		apply_if_better: flags.apply_if_better, exclusive: flags.exclusive, hooks: { fw_apply: () => fw_apply({}) } }, ctx),
	'probe': (ctx, args, flags) => H.probe_run(C.load(), flags.services, ctx, null),
	'dns-setup': (ctx, args, flags) => DNS.setup(ctx, null),
	'diagnose': (ctx, args, flags) => DG.run_diagnose(C.load(), flags.services, ctx, null)
};

// Starts a job in background, or runs it in place with --foreground.
export function run_job(name, args, flags) {
	let handler = JOB_HANDLERS[name];
	if (flags.foreground)
		return J.foreground(name, (ctx) => handler(ctx, args, flags));
	let jargs = [];
	for (let a in args)
		push(jargs, a);
	if (flags.all)
		push(jargs, '--all');
	if (flags.quick)
		push(jargs, '--quick');
	if (flags.strategies)
		push(jargs, '--strategies', join(',', flags.strategies));
	if (flags.apply_if_better)
		push(jargs, '--apply-if-better');
	if (flags.exclusive)
		push(jargs, '--exclusive');
	if (flags.services)
		push(jargs, '--services', join(',', flags.services));
	return J.start(name, jargs);
};

/* ---------- test ---------- */

// ubus messages are limited to 1 MiB: per-target details are kept only for the best results.
export const TEST_TARGETS_RESULTS = 10;
export const TEST_TARGETS_MAX = 100;

export function trim_test_results(res) {
	if (type(res?.results) != 'array')
		return res;
	let trimmed = false;
	for (let i = 0; i < length(res.results); i++) {
		let r = res.results[i];
		if (type(r?.targets) != 'array')
			continue;
		if (i >= TEST_TARGETS_RESULTS) {
			delete r.targets;
			trimmed = true;
		}
		else if (length(r.targets) > TEST_TARGETS_MAX) {
			r.targets = slice(r.targets, 0, TEST_TARGETS_MAX);
			trimmed = true;
		}
	}
	if (type(res.baseline?.targets) == 'array' && length(res.baseline.targets) > TEST_TARGETS_MAX) {
		res.baseline.targets = slice(res.baseline.targets, 0, TEST_TARGETS_MAX);
		trimmed = true;
	}
	res.targets_trimmed = trimmed;
	return res;
};

// Pure: results without per-target details (`test status --brief`, contract v1.3 §14.3).
export function brief_test_results(res) {
	if (type(res) != 'object')
		return res;
	if (type(res.results) == 'array')
		for (let r in res.results)
			if (type(r) == 'object')
				delete r.targets;
	if (type(res.baseline) == 'object')
		delete res.baseline.targets;
	return res;
};

export function test_status(flags) {
	settle_jobs();
	let j = J.read();
	let res = read_json(T.results_path(), 4194304);
	return ok({
		job: (j && j.name == 'test') ? j : null,
		running: !!(j && j.name == 'test' && j.state == 'running'),
		mode: res?.mode ?? null,
		mode_reason: res?.mode_reason ?? null,
		results: flags?.brief ? brief_test_results(res) : trim_test_results(res)
	});
};

export function test_stop() {
	let j = J.read();
	if (!j || j.name != 'test' || j.state != 'running') {
		if (test_active() || T.leftover()) {
			let rr = test_restore();
			return ok({ state: 'restored', reloaded: rr?.rc == 0 });
		}
		return fail('no_job', 'Автоподбор не выполняется');
	}
	return J.cancel();
};

export function test_apply(id) {
	if (!is_id(id))
		return fail('bad_id', 'Недопустимый идентификатор');
	return T.apply(C.load(), id);
};

/* ---------- jobs ---------- */

export function job_status() {
	settle_jobs();
	return ok({ job: J.read() });
};

export function job_log(opts) {
	let n = V.parse_uint(opts.tail ?? '200', 1, 5000) ?? 200;
	return ok({ log: J.log_tail(n) });
};

export function job_cancel() {
	return J.cancel();
};

/* ---------- offload / cron (used by init and uci-defaults) ---------- */

export function offload_apply() {
	return O.apply(C.load());
};

export function offload_restore() {
	return O.restore();
};

export function offload_status() {
	return ok(O.state(C.load()));
};

/* ---------- diag / version ---------- */

export function version() {
	return ok({ version: VERSION, nfqws: SV.engine_version('nfqws'), nfqws2: SV.engine_version('nfqws2') });
};

function section(title, body) {
	return '===== ' + title + ' =====\n' + trim(body ?? '') + '\n\n';
}

export const DIAG_FULL_HINT = 'полный отчёт: zaprett diag --full (по SSH)';

// Without --full the report must fit into the RPC timeout of the web interface (LuCI ждёт ответ ≤19 с):
// перечисление пакетов и журнал (десятки секунд на слабом роутере) уходят в --full, модули ядра читаются
// из /sys без внешних команд.
export function diag(flags) {
	let full = !!flags?.full;
	let out = '';
	let rel = fs.readfile('/etc/openwrt_release', 4096) ?? '';
	out += section('zaprett', sprintf('zaprett %s\nnfqws: %s\nnfqws2: %s', VERSION,
		SV.engine_version('nfqws') ?? 'не установлен', SV.engine_version('nfqws2') ?? 'не установлен'));
	out += section('Система', rel + '\n' + trim(run([ 'uname', '-a' ], { timeout: 5000 }).stdout));
	if (full) {
		let pk = run([ 'opkg', 'list-installed' ], { timeout: 60000, limit: 1048576 });
		if (pk.rc != 0)
			pk = run([ 'apk', 'list', '-I' ], { timeout: 60000, limit: 1048576 });
		let pkgs = filter(split(pk.stdout, '\n'), (l) => match(l, /^(zaprett|luci-app-zaprett|luci-i18n-zaprett|nftables|kmod-nft-queue|kmod-nfnetlink-queue|ucode|uclient-fetch|firewall4|ca-bundle)/));
		out += section('Пакеты', join('\n', pkgs));
	}
	else {
		out += section('Пакеты', sprintf('(пропущено в быстром отчёте) %s\nДвижки: nfqws — %s, nfqws2 — %s',
			DIAG_FULL_HINT,
			is_file(SV.engine_path('nfqws')) ? ('есть, ' + (SV.engine_version('nfqws') ?? 'версия не определена')) : 'нет',
			is_file(SV.engine_path('nfqws2')) ? ('есть, ' + (SV.engine_version('nfqws2') ?? 'версия не определена')) : 'нет'));
	}
	let mods = [];
	for (let m in [ 'nft_queue', 'nfnetlink_queue' ])
		push(mods, sprintf('%s: %s', m, (fs.stat('/sys/module/' + m)?.type == 'directory') ? 'загружен' : 'нет'));
	out += section('Модули ядра', join('\n', mods));
	let st = status();
	out += section('Состояние', sprintf('%.J', st));
	out += section('UCI zaprett', run([ 'uci', 'export', 'zaprett' ], { timeout: 10000, limit: 65536 }).stdout);
	out += section('Аргументы движка', fs.readfile(P.run + '/args', 65536) ?? '(нет)');
	out += section('Результат генерации', fs.readfile(P.run + '/status.json', 65536) ?? '(нет)');
	out += section('nft list table inet zaprett', N.list_table() ?? '(таблица не установлена)');
	out += section('Фоновая задача', sprintf('%.J', J.read()));
	if (full) {
		let lr = run([ 'logread', '-e', 'zaprett' ], { timeout: 15000, limit: 1048576 });
		out += section('Журнал (последние 100 строк)', tail_lines(lr.stdout, 100));
	}
	else
		out += section('Журнал', sprintf('(пропущено в быстром отчёте) %s\nили: logread -e zaprett', DIAG_FULL_HINT));
	return ok({ text: out, full: full });
};

/* ---------- watchdog, probe, monitor, log, page (contract v1.3 §14) ---------- */

export function ensure() {
	// a test killed with its job must not keep the watchdog away for good (health.test_busy)
	settle_jobs();
	return with_main_lock(() => H.ensure(C.load(), { fw_apply: () => fw_apply({}) }));
};

export function probe_status() {
	return H.probe_status();
};

export function monitor_run() {
	settle_jobs();
	return H.monitor_run(C.load(), null);
};

export function monitor_status() {
	return H.monitor_status(C.load());
};

/* ---------- encrypted DNS and blocking diagnosis (contract v1.4 §15.3–§15.4) ---------- */

export function dns_status() {
	return ok({ dns: DNS.status() });
};

// `dns setup`: a working provider needs no job — {ok, changed:false} at once; otherwise job `dns-setup`.
export function dns_setup(flags) {
	let st = DNS.status();
	if (st.encrypted)
		return ok({ changed: false, dns: st });
	return run_job('dns-setup', [], flags);
};

export function diagnose_status() {
	return DG.status();
};

export const LOG_TAIL_DEFAULT = 200;
export const LOG_TAIL_MAX = 1000;

// Pure: the last n syslog lines of zaprett or the engine.
export function log_filter(text, n) {
	let lines = filter(split(text ?? '', '\n'), (l) => index(l, 'zaprett') >= 0 || index(l, 'nfqws') >= 0);
	return (length(lines) > n) ? slice(lines, length(lines) - n) : lines;
};

export function log_lines(opts) {
	let n = (opts?.tail == null) ? LOG_TAIL_DEFAULT : V.parse_uint(opts.tail, 1, 1000000000);
	if (n == null)
		return fail('bad_value', '--tail: целое число от 1');
	if (n > LOG_TAIL_MAX)
		n = LOG_TAIL_MAX;
	let r = run([ P.logread ], { timeout: 15000, limit: 4194304 });
	if (r.rc != 0)
		return fail('logread_failed', 'Не удалось прочитать системный журнал: ' + trim(r.stderr));
	return ok({ lines: log_filter(r.stdout, n) });
};

export const PAGES = {
	overview: [ 'status', 'job', 'presets', 'monitor', 'probe', 'dns' ],
	lists: [ 'status', 'job', 'items', 'sources', 'presets' ],
	strategies: [ 'status', 'job', 'items', 'test' ],
	diagnostics: [ 'status', 'job', 'monitor', 'dns', 'diagnose' ]
};

const PAGE_PARTS = {
	status: () => status(),
	job: () => job_status(),
	presets: () => presets(),
	monitor: () => monitor_status(),
	probe: () => probe_status(),
	items: () => items({}),
	sources: () => sources_list(),
	test: () => test_status({ brief: true }),
	dns: () => dns_status(),
	diagnose: () => diagnose_status()
};

// One process instead of several for a page of the web interface: every field is the whole answer of its
// command, with its own `ok` (contract v1.3 §14.3).
export function page(name) {
	let parts = PAGES[name];
	if (!parts)
		return fail('bad_value', 'Страница: overview, lists, strategies или diagnostics');
	let res = ok({ page: name });
	for (let k in parts) {
		let fn = PAGE_PARTS[k];
		try {
			res[k] = fn();
		}
		catch (e) {
			res[k] = fail('internal_error', 'Внутренняя ошибка: ' + e.message);
		}
	}
	return res;
};
