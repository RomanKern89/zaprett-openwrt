// SPDX-License-Identifier: MIT
//
// Mock of the zaprett CLI (/usr/bin/zaprett ... --json) for self-checks of
// luci-app-zaprett without the real backend. Answers follow the contract
// (docs/ARCHITECTURE.md v1.1, sections 6.2, 6.3, 9, 10; v1.3 section 14: page,
// probe, monitor status, log, test status --brief, --apply-if-better, new
// fields of status) and the field names of packages/zaprett as of 2026-09-17.
// Not installed by the package.
//
// Usage: ucode mock-zaprett.uc <command> [args...] --json
// State is kept in $MOCK_STATE (default /tmp/zaprett-mock/state.json).
//
// Special ids for negative tests of the rpcd plugin:
//   strategy show mock-invalid-json   -> prints garbage, rc 1
//   strategy show mock-stderr         -> prints only to stderr, rc 2
//   strategy show mock-sleep          -> sleeps 40 s (time limit test)
//   strategy show mock-big            -> ~1.2 MiB of JSON (does not fit into ubus)
//   strategy show mock-huge           -> ~5 MiB of output (larger than the read limit)
//   test start --strategies mock-big-results -> "test status" becomes ~1.5 MiB,
//                                     "page strategies" ignores --brief then
//   probe --services <unknown>        -> error unknown_service
//   diagnose --services <unknown>     -> error unknown_service
// Contract v1.4 (section 15): dns status/setup, diagnose, status.dns,
// status.flow_offload.own, page overview/diagnostics with dns and diagnose.

'use strict';

import * as fs from 'fs';

const STATE = getenv('MOCK_STATE') ?? '/tmp/zaprett-mock/state.json';
const ID_RE = /^[A-Za-z0-9._-]{1,96}$/;

const ITEMS = [
	{ id: 'strategy-general', type: 'nfqws', name: 'General', version: '1.0.0', author: 'flowseal', description: 'Universal strategy for YouTube and Discord', source: 'bundle', entries: 0, size: 1043 },
	{ id: 'strategy-fake-tls-auto', type: 'nfqws', name: 'Fake TLS auto', version: '1.0.0', author: 'flowseal', description: 'Fake TLS with automatic TTL', source: 'bundle', entries: 0, size: 812 },
	{ id: 'strategy-alt', type: 'nfqws', name: 'strategy-alt', version: '1.0.1', author: 'flowseal', description: 'Стратегия alt из репозитория zapret для Windows', source: 'repo', entries: 0, size: 1201 },
	{ id: 'strategy2-default', type: 'nfqws2', name: 'Default (nfqws2)', version: '1.0.0', author: 'zaprett', description: 'Default strategy for nfqws2', source: 'bundle', entries: 0, size: 640 },
	{ id: 'zaprett-youtube', type: 'list', name: 'YouTube', version: '2026.09.17', author: 'zaprett', description: 'YouTube and googlevideo', source: 'bundle', entries: 13, size: 232 },
	{ id: 'zaprett-discord', type: 'list', name: 'Discord', version: '2026.09.17', author: 'zaprett', description: 'Discord web and voice', source: 'bundle', entries: 19, size: 316 },
	{ id: 'zaprett-telegram', type: 'list', name: 'Telegram', version: '2026.09.17', author: 'zaprett', description: 'Telegram web', source: 'bundle', entries: 17, size: 280 },
	{ id: 'list-extended', type: 'list', name: 'Extended <b>list</b>', version: '1.0.0', author: 'cherret', description: 'Very large list <img src=x onerror=alert(1)>', source: 'repo', entries: 675685, size: 12400000 },
	{ id: 'src-refilter_domains', type: 'list', name: 'Re:filter — заблокированные домены', version: '2026.09.17', author: '', description: '', source: 'url', entries: 512000, size: 9300000 },
	{ id: 'user-hosts', type: 'list', name: 'My domains', version: '', author: '', description: '', source: 'user', entries: 2, size: 24 },
	{ id: 'zaprett-exclude', type: 'list_exclude', name: 'Exclusions', version: '2026.09.17', author: 'zaprett', description: 'Banks, government, marketplaces', source: 'bundle', entries: 379, size: 5400 },
	{ id: 'user-hosts-exclude', type: 'list_exclude', name: 'My exclusions', version: '', author: '', description: '', source: 'user', entries: 0, size: 0 },
	{ id: 'zaprett-telegram-ipset', type: 'ipset', name: 'Telegram networks', version: '2026.09.17', author: 'zaprett', description: 'Telegram data centers', source: 'bundle', entries: 12, size: 163 },
	{ id: 'user-ipset', type: 'ipset', name: 'My networks', version: '', author: '', description: '', source: 'user', entries: 0, size: 0 },
	{ id: 'zaprett-exclude-ipset', type: 'ipset_exclude', name: 'Local networks', version: '2026.09.17', author: 'zaprett', description: 'Private address ranges', source: 'bundle', entries: 11, size: 126 },
	{ id: 'user-ipset-exclude', type: 'ipset_exclude', name: 'My excluded networks', version: '', author: '', description: '', source: 'user', entries: 0, size: 0 },
	{ id: 'quic_initial_www_google_com', type: 'bin', name: 'quic_initial_www_google_com', version: '1.0.0', author: 'bol-van', description: 'Fake QUIC Initial', source: 'bundle', entries: 0, size: 1200 },
	{ id: 'strategy-default', type: 'byedpi', name: 'strategy-default', version: '1.0.0', author: 'cherret', description: 'ByeDPI strategy (not usable on a router)', source: 'repo', entries: 0, size: 90 }
];

const PRESETS = {
	schema: 1,
	services: [
		{ id: 'youtube', name: 'YouTube', description: 'Сайт и приложение YouTube', lists: [ 'zaprett-youtube' ], ipsets: [], sources: [], tier: 'light', works: 'yes', test_targets: [ { url: 'https://www.youtube.com/', min_bytes: 131072 } ], note: 'Замедление через DPI' },
		{ id: 'discord', name: 'Discord', description: 'Сайт и приложение Discord', lists: [ 'zaprett-discord' ], ipsets: [], sources: [], tier: 'light', works: 'yes', test_targets: [ { url: 'https://discord.com/', min_bytes: 65536 } ], note: '',
			variants: [
				{ id: 'full', name: 'Расширенный', name_en: 'Extended', description: 'Больше доменов', description_en: 'More domains', lists: [ 'zaprett-discord-full' ], ipsets: [], tier: 'light' },
				{ id: 'voice', name: 'С голосовыми серверами', name_en: 'With voice servers', description: 'Плюс IP-сети голоса', description_en: 'Plus voice networks', lists: [ 'zaprett-discord' ], ipsets: [ 'zaprett-discord-voice' ], tier: 'full' }
			] },
		{ id: 'telegram', name: 'Telegram', description: 'Telegram', lists: [ 'zaprett-telegram' ], ipsets: [ 'zaprett-telegram-ipset' ], sources: [], tier: 'light', works: 'partial', test_targets: [ { url: 'https://telegram.org/', min_bytes: 17000 } ], note: 'Звонки могут не работать' },
		{ id: 'rkn_full', name: 'Реестр РКН', description: 'Все заблокированные сайты', lists: [], ipsets: [], sources: [ 'refilter_domains' ], tier: 'full', works: 'partial', test_targets: [], note: 'Нужно много памяти' },
		{ id: 'chatgpt_claude', name: 'ChatGPT и Claude', description: 'Сервисы ИИ', lists: [], ipsets: [], sources: [], tier: 'light', works: 'no', test_targets: [], note: 'Гео-блок на стороне сервиса' }
	],
	always: { exclude_lists: [ 'zaprett-exclude' ], exclude_ipsets: [ 'zaprett-exclude-ipset' ] },
	tiers: { light: { min_ram_mib: 0 }, full: { min_ram_mib: 200 } },
	defaults: { services: [ 'youtube', 'discord' ], strategy: 'strategy-general', quick_test_strategies: [ 'strategy-general', 'strategy-alt' ] }
};

const REPO = [
	{ id: 'strategy-alt', type: 'nfqws', name: 'strategy-alt', version: '1.0.2', author: 'flowseal', description: 'Стратегия alt из репозитория zapret для Windows' },
	{ id: 'strategy-alt10', type: 'nfqws', name: 'strategy-alt10', version: '1.0.0', author: 'flowseal', description: 'Порт стратегии alt10 от flowseal' },
	{ id: 'strategy-general', type: 'nfqws', name: 'strategy-general', version: '1.0.0', author: 'flowseal', description: 'Входит в пакет' },
	{ id: 'list-extended', type: 'list', name: 'list-extended', version: '1.0.0', author: 'cherret', description: 'Большой список' },
	{ id: 'list-exclude-yandex', type: 'list_exclude', name: 'list-exclude-yandex', version: '1.0.0', author: 'cherret', description: 'Исключения Яндекса' },
	{ id: 'ipset-cloudflare', type: 'ipset', name: 'ipset-cloudflare', version: '1.0.0', author: 'cherret', description: 'Сети Cloudflare', error: 'sha256 mismatch in index' },
	{ id: 'tls_clienthello_4pda_to', type: 'bin', name: 'tls_clienthello_4pda_to', version: '1.0.0', author: 'bol-van', description: 'Fake TLS ClientHello' },
	{ id: 'strategy-default', type: 'byedpi', name: 'strategy-default', version: '1.0.0', author: 'cherret', description: 'ByeDPI strategy' }
];

function out(obj, rc) {
	printf('%J\n', obj);
	exit(rc ?? (obj.ok ? 0 : 1));
}

function err(code, message, extra) {
	out({ ok: false, error: code, message: message, ...(extra ?? {}) }, 1);
}

function load_state() {
	let st = null;

	try {
		st = json(fs.readfile(STATE) ?? '');
	}
	catch (e) {
		st = null;
	}

	return st ?? {
		enabled: false, running: false, engine: 'nfqws', strategy: 'strategy-general', strategy_nfqws2: '',
		list_mode: 'whitelist', lists: [ 'user-hosts' ], exclude_lists: [ 'zaprett-exclude', 'user-hosts-exclude' ],
		ipsets: [], exclude_ipsets: [ 'zaprett-exclude-ipset', 'user-ipset-exclude' ], installed: [ 'strategy-alt', 'list-extended' ],
		user: { 'user-hosts': 'youtube.com\n# comment\n' }, user_strategies: {}, fetched_at: null, job: null,
		results_mode: 'small', test_override: false,
		sources: {
			refilter_domains: { enabled: true, title: 'Re:filter — заблокированные домены', type: 'list', url: 'https://github.com/1andrevich/Re-filter-lists/releases/latest/download/domains_all.lst', interval_hours: 72, min_entries: 1000, last_update: 1758100000, entries: 512000, size: 9300000, status: 'ok', error: null, message: null, ram_mib: 9, item_id: 'src-refilter_domains' },
			cloudflare_v4: { enabled: false, title: 'Cloudflare IPv4', type: 'ipset', url: 'https://www.cloudflare.com/ips-v4', interval_hours: 168, min_entries: 10, last_update: 0, entries: null, size: null, status: 'error', error: 'too_few_entries', message: 'В файле слишком мало записей: 3 из 10 — оставлен прежний список', ram_mib: 1, item_id: 'src-cloudflare_v4' }
		},
		last_source_save: null
	};
}

function save_state(st) {
	fs.mkdir(fs.dirname(STATE));
	fs.writefile(STATE, sprintf('%J', st));
}

function find_item(st, id) {
	for (let it in ITEMS)
		if (it.id == id)
			return it;

	if (st.user_strategies[id] != null)
		return { id: id, type: 'nfqws', name: id, version: '', author: '', description: '', source: 'user', entries: 0, size: length(st.user_strategies[id]) };

	return null;
}

function active_key(type) {
	return { list: 'lists', list_exclude: 'exclude_lists', ipset: 'ipsets', ipset_exclude: 'exclude_ipsets' }[type];
}

function job_view(st) {
	const j = st.job;

	if (!j)
		return null;

	if (j.state == 'running') {
		const elapsed = time() - j.started;

		/* the backend answers "cancelling" at once and finishes the
		 * cancellation on the next poll (BACKEND.md section 8) */
		if (j.cancel_requested) {
			j.state = 'cancelled';
			j.message = 'Задача отменена';
			j.finished = time();
			j.rc = 1;
			j.cancel_done = true;
		}
		else if (elapsed >= j.duration) {
			j.state = j.fail ? 'failed' : 'done';
			j.progress = 100;
			j.finished = j.started + j.duration;
			j.rc = j.fail ? 1 : 0;
			j.message = j.fail ? 'Не удалось скачать файл: превышено время ожидания' : 'Готово';
		}
		else {
			j.progress = int(elapsed * 100 / j.duration);
			j.message = sprintf('Шаг %d из %d', int(elapsed) + 1, j.duration);
		}
	}

	return { id: j.id, name: j.name, state: j.state, progress: j.progress, message: j.message, started: j.started,
		finished: j.finished, rc: j.rc, result: j.result ?? {}, pid: 4242, pid_start: 1,
		cancel_requested: j.cancel_requested ?? false, cancel_done: j.cancel_done ?? false };
}

// job cancel / test stop: immediate answer, the job stops on the next poll
function request_cancel(st) {
	const cur = job_view(st);

	if (!cur || cur.state != 'running')
		err('no_job', 'Нет выполняющейся задачи');

	st.job.cancel_requested = true;
	save_state(st);
	out({ ok: true, state: 'cancelling', job: { id: cur.id, name: cur.name } });
}

function start_job(st, name, duration, fail) {
	const cur = job_view(st);

	if (cur && cur.state == 'running')
		err('job_busy', sprintf('Уже выполняется другая задача («%s»). Дождитесь её завершения или отмените её.', cur.name), { job: { id: cur.id, name: cur.name } });

	st.job = { id: sprintf('%d-%d', time(), 4242), name: name, state: 'running', progress: 0, message: 'Задача запускается…', started: time(), finished: 0, rc: 0, duration: duration, fail: fail };
	save_state(st);
	out({ ok: true, job: { id: st.job.id, name: name } });
}

function targets_for(ok_count, ms, n) {
	const res = [];

	for (let i = 0; i < n; i++)
		push(res, { url: sprintf('https://site%d.example.com/some/longer/path/to/make/the/answer/bigger', i), ok: i < ok_count, ms: ms, bytes: (i < ok_count) ? 70000 : 0,
			http_status: null, error: (i < ok_count) ? null : 'reset', detail: (i < ok_count) ? null : 'Connection reset prematurely' });

	return res;
}

function test_results(st) {
	const j = job_view(st);

	if (!j || j.name != 'test')
		return null;

	const big = (st.results_mode == 'big' || st.results_mode == 'trimmed');
	const n_targets = big ? 120 : 6;
	const n_strategies = big ? 64 : 3;
	const all = [];

	for (let i = 0; i < n_strategies; i++) {
		const ok_count = (i == 0) ? n_targets : ((i == 1) ? n_targets - 1 : 0);
		const status = (i == 2) ? 'invalid' : ((i == 3) ? 'engine_failed' : 'done');

		push(all, { id: (i == 0) ? 'strategy-alt' : ((i == 1) ? 'strategy-general' : sprintf('strategy-%d', i)), name: sprintf('Strategy %d', i),
			ok: (status == 'done') ? ok_count : 0, total: n_targets, ratio: (status == 'done') ? ok_count / n_targets : 0, avg_ms: (status == 'done' && ok_count) ? 400 + i : null,
			status: status, message: (status == 'invalid') ? 'Неизвестный плейсхолдер ${foo} в стратегии' : null,
			targets: (status == 'done') ? targets_for(ok_count, 400 + i, n_targets) : [] });
	}

	const n = (j.state == 'running' && !big) ? int(j.progress * length(all) / 100) : length(all);
	let results = slice(all, 0, n);
	let trimmed = false;

	/* "trimmed" reproduces the backend: details only for the first 10 results,
	 * at most 100 targets each; "big" is an untrimmed answer, which the plugin
	 * has to shrink itself */
	if (st.results_mode == 'trimmed') {
		for (let i = 0; i < length(results); i++) {
			if (i >= 10) {
				delete results[i].targets;
				trimmed = true;
			}
			else if (length(results[i].targets ?? []) > 100) {
				results[i].targets = slice(results[i].targets, 0, 100);
				trimmed = true;
			}
		}
	}

	return {
		started: j.started, finished: (j.state == 'running') ? 0 : j.finished, state: j.state, engine: 'nfqws', original_strategy: st.strategy,
		targets: map(targets_for(0, 0, n_targets), t => t.url),
		baseline: { ok: 1, total: n_targets, avg_ms: 5000, targets: targets_for(1, 5000, n_targets), note: null },
		results: results, targets_trimmed: trimmed,
		applied: (j.state == 'done' && st.test_apply_if_better) ? 'strategy-alt' : null
	};
}

// --- answers of single commands, also used by "page" (contract v1.3 §14.3)

// encrypted DNS is switched on by a finished "dns-setup" job
function dns_view(st) {
	const j = job_view(st);

	if (!st.dns_encrypted && j?.name == 'dns-setup' && j.state == 'done') {
		st.dns_encrypted = true;
		save_state(st);
	}

	return { encrypted: !!st.dns_encrypted, provider: st.dns_encrypted ? 'https-dns-proxy' : null };
}

// "diagnose" results appear when the diagnose job has finished
function diagnose_reply(st) {
	const j = job_view(st);

	if (j?.name == 'diagnose' && j.state == 'done' && !st.diagnose) {
		const verdicts = { youtube: 'throttle', discord: 'dns_spoof', telegram: 'ip_block' };
		const targets = [], counts = {};

		for (let id in st.diagnose_services) {
			const svc = filter(PRESETS.services, s => s.id == id)[0];
			const url = svc?.test_targets?.[0]?.url ?? 'https://example.com/';
			const verdict = verdicts[id] ?? 'ok';

			counts[verdict] = (counts[verdict] ?? 0) + 1;
			push(targets, { url: url, host: replace(replace(url, /^https:\/\//, ''), /\/.*$/, ''), verdict: verdict,
				dns: { system: [ (verdict == 'dns_spoof') ? '10.10.10.10' : '142.250.74.14' ], doh: [ '142.250.74.14' ], spoofed: verdict == 'dns_spoof' },
				detail: (verdict == 'ok') ? null : 'mock detail for ' + verdict });
		}

		let top = 'ok', best = 0;

		for (let v, n in counts)
			if (v != 'ok' && n > best) {
				top = v;
				best = n;
			}

		st.diagnose = { started: j.started, finished: j.finished, engine_running: st.running, targets: targets,
			summary: { verdict: top, counts: counts } };
		save_state(st);
	}

	return { ok: true, diagnose: st.diagnose ?? null };
}

function status_reply(st) {
	const warnings = [];

	if (st.list_mode == 'whitelist' && !length(st.lists))
		push(warnings, 'no_active_lists');

	if (st.enabled && !st.running)
		push(warnings, 'not_running');

	push(warnings, 'flow_offload_enabled', 'bad_config', 'mock_unknown_warning', 'ipv6_wan_unhandled');

	const j = job_view(st);

	return {
		dns: dns_view(st),
		ok: true, enabled: st.enabled, autostart: st.enabled, running: st.running, pid: st.running ? 2345 : null,
		engine: st.engine, engine_version: 'v72.13',
		strategy: { id: st.strategy || null, name: find_item(st, st.strategy)?.name ?? null, source: find_item(st, st.strategy)?.source ?? null },
		list_mode: st.list_mode, lists: st.lists, exclude_lists: st.exclude_lists,
		ipsets: st.ipsets, exclude_ipsets: st.exclude_ipsets,
		nft_applied: st.running, wan: [ 'eth1' ], flow_offload: { fw4: true, fw4_hw: false, mode: 'auto', own: false },
		clients_mode: 'all', test_mode: false, warnings: warnings,
		details: { generate: { ok: true }, bad_options: [ 'concurrency' ] }, version: '1.0.0',
		job: j ? { id: j.id, name: j.name, state: j.state, progress: j.progress } : null,
		monitor: { state: 'ok', consecutive_failures: 0, checked_at: 1758100000 },
		queue: st.running ? { packets: 1234 } : null,
		ipv6_wan: true
	};
}

function job_reply(st) {
	return { ok: true, job: job_view(st) };
}

// All lists and ipsets of a variant are switched on.
function set_on(st, v) {
	return length(v.lists) + length(v.ipsets) > 0 && length(filter(v.lists, l => index(st.lists, l) < 0)) == 0 &&
		length(filter(v.ipsets, l => index(st.ipsets, l) < 0)) == 0;
}

function presets_reply(st) {
	const services = [];

	for (let s in PRESETS.services) {
		const all = length(s.lists) + length(s.ipsets);
		const active = length(filter(s.lists, l => index(st.lists, l) >= 0)) + length(filter(s.ipsets, l => index(st.ipsets, l) >= 0));
		const variants = map(s.variants ?? [], v => ({ ...v, enabled: set_on(st, v), available: s.works != 'no' }));
		const on = filter(variants, v => v.enabled);
		push(services, { ...s, enabled: all > 0 && active == all, partially_enabled: active > 0 && active < all,
			available: (all > 0 || length(s.sources) > 0) && s.works != 'no', variants: variants,
			enabled_variant: length(on) ? on[length(on) - 1].id : null });
	}

	return { ok: true, schema: 1, services: services, defaults: PRESETS.defaults, always: PRESETS.always, tiers: PRESETS.tiers,
		ram_total_mib: 128, recommended_tier: 'light' };
}

function items_reply(st, want) {
	const items = [];

	for (let it in ITEMS) {
		if (want && it.type != want)
			continue;

		const key = active_key(it.type);
		push(items, { ...it, file: '/usr/share/zaprett/bundle/files/' + it.id + '.txt', active: key ? index(st[key], it.id) >= 0 : (it.id == st.strategy),
			used_by: (it.type == 'bin') ? [ 'strategy-alt' ] : [], dependencies: [], supported: true });
	}

	for (let id, text in st.user_strategies)
		if (!want || want == 'nfqws')
			push(items, { ...find_item(st, id), file: '/etc/zaprett/user/strategies/nfqws/' + id + '.txt', active: id == st.strategy, used_by: [], dependencies: [], supported: true });

	return { ok: true, items: items, errors: [] };
}

function sources_reply(st) {
	const list = [];

	for (let name, s in st.sources)
		push(list, { name: name, enabled: s.enabled, title: s.title, type: s.type, url: s.url, interval_hours: s.interval_hours,
			min_entries: s.min_entries, last_update: s.last_update, entries: s.entries, size: s.size, status: s.status,
			error: s.error, message: s.message ?? null, ram_mib: s.ram_mib, item_id: s.item_id, downloaded: !!s.last_update });

	return { ok: true, sources: list };
}

function test_reply(st, brief) {
	const j = job_view(st);
	const res = test_results(st);

	if (brief && res) {
		delete res.baseline.targets;

		for (let r in res.results)
			delete r.targets;
	}

	/* mock_flags is not a backend field: the tests use it to see the flags of "test start" */
	return { ok: true, job: (j?.name == 'test') ? j : null, running: j?.name == 'test' && j?.state == 'running', results: res,
		mock_flags: st.test_flags ?? [] };
}

function monitor_reply(st) {
	const history = [];

	for (let i = 0; i < 48; i++)
		push(history, { t: 1758100000 - (47 - i) * 1800, ok: (i % 7 == 3) ? 0 : 3, total: (i == 0) ? 0 : 3 });

	return { ok: true, monitor: { enabled: true, auto_repair: false, interval: 30, threshold: 3, state: 'ok', checked_at: 1758100000,
		ok: 3, total: 3, consecutive_failures: 0, history: history, last_repair: null } };
}

// "probe" results appear when the probe job has finished
function probe_reply(st) {
	const j = job_view(st);

	if (j?.name == 'probe' && j.state == 'done' && !st.probe) {
		const services = [];

		for (let id in st.probe_services) {
			const svc = filter(PRESETS.services, s => s.id == id)[0];
			const good = (id != 'discord');

			push(services, { id: id, name: svc?.name ?? id, ok: good ? 1 : 0, total: 1, avg_ms: good ? 420 : null,
				targets: [ { url: svc?.test_targets?.[0]?.url ?? 'https://example.com/', ok: good, ms: good ? 420 : 5000, bytes: good ? 900000 : 0,
					error: good ? null : 'reset' } ] });
		}

		st.probe = { started: j.started, finished: j.finished, engine_running: st.running, strategy: st.strategy,
			ok: length(filter(services, s => s.ok > 0)), total: length(services), services: services };
		save_state(st);
	}

	return { ok: true, probe: st.probe ?? null };
}

const argv = filter(ARGV, a => a != '--json');

if (length(argv) == length(ARGV))
	err('usage', 'mock supports only --json mode');

const st = load_state();
const cmd = argv[0], sub = argv[1];

switch (cmd) {
case 'status':
	out(status_reply(st));

case 'start':
case 'restart':
	st.running = true;
	st.enabled = true;
	save_state(st);
	out({ ok: true, running: true, pid: 2345, warnings: [] });

case 'stop':
	st.running = false;
	save_state(st);
	out({ ok: true, running: false });

case 'enable':
case 'disable':
	st.enabled = (cmd == 'enable');

	if (cmd == 'disable')
		st.running = false;

	save_state(st);
	out({ ok: true, enabled: st.enabled, autostart: st.enabled });

case 'check':
	if (st.strategy == 'user-test')
		err('dry_run_failed', 'Движок не принял параметры: неизвестный режим --dpi-desync', { args: [ '--qnum=200' ], dry_run: { rc: 1, output: 'nfqws: invalid desync mode' } });

	out({ ok: true, engine: 'nfqws', strategy: { id: st.strategy }, args: [ '--qnum=200', '--user=daemon', '--dpi-desync-fwmark=0x40000000', '--filter-tcp=80,443', '--hostlist=/etc/zaprett/user/hosts-include.txt', '--dpi-desync=fake,multisplit' ],
		ports: { tcp: [ '80', '443' ], udp: [ '443' ] }, dry_run: { rc: 0, output: 'we have 2 user defined desync profile(s)' }, warnings: [] });

case 'items':
	out(items_reply(st, (sub == '--type') ? argv[2] : null));

case 'list':
	const it = find_item(st, argv[2]);
	const key = it ? active_key(it.type) : null;

	if (!key)
		err('not_found', sprintf('Список «%s» не найден', argv[2]));

	const was = index(st[key], it.id) >= 0;

	st[key] = filter(st[key], x => x != it.id);

	if (sub == 'enable')
		push(st[key], it.id);

	save_state(st);
	out({ ok: true, id: it.id, type: it.type, enabled: sub == 'enable', changed: was != (sub == 'enable'), reloaded: st.running, warnings: [] });

case 'strategy':
	const id = argv[2];

	if (sub == 'show') {
		if (id == 'mock-invalid-json') {
			print('this is not json\n');
			exit(1);
		}

		if (id == 'mock-stderr') {
			warn('nfqws: fatal error in mock\n');
			exit(2);
		}

		if (id == 'mock-sleep') {
			system([ 'sleep', '40' ]);
			out({ ok: true });
		}

		if (id == 'mock-big')
			out({ ok: true, id: id, text: sprintf('%1200000s', 'x') });

		if (id == 'mock-huge')
			out({ ok: true, id: id, text: sprintf('%5000000s', 'x') });

		const s = find_item(st, id);

		if (!s)
			err('strategy_not_found', sprintf('Стратегия «%s» не найдена', id));

		out({ ok: true, id: id, type: s.type, name: s.name, source: s.source, description: s.description, dependencies: [],
			text: st.user_strategies[id] ?? '--filter-tcp=80,443 ${hostlists} --dpi-desync=fake,multisplit --new\n--filter-udp=443 ${hostlists} --dpi-desync=fake',
			args: [ '--filter-tcp=80,443', '--hostlist=/etc/zaprett/user/hosts-include.txt' ], ports: { tcp: [ '80', '443' ], udp: [ '443' ] }, warnings: [] });
	}

	if (sub == 'set') {
		if (!find_item(st, id))
			err('strategy_not_found', sprintf('Стратегия «%s» для движка nfqws не найдена', id));

		st.strategy = id;
		save_state(st);
		out({ ok: true, id: id, engine: 'nfqws', reloaded: st.running, warnings: [] });
	}

	if (sub == 'save') {
		const text = fs.stdin.read('all') ?? '';

		if (length(text) > 65536)
			err('too_large', 'Текст стратегии больше 64 КиБ');

		if (index(text, '--dpi-desync') < 0)
			err('dry_run_failed', 'nfqws --dry-run: не задан режим --dpi-desync', { args: [], dry_run: { rc: 1, output: 'no desync mode' } });

		st.user_strategies[id] = text;
		save_state(st);
		out({ ok: true, id: id, engine: 'nfqws', args: [], ports: { tcp: [], udp: [] }, warnings: [], reloaded: false, bytes: length(text) });
	}

	if (sub == 'delete') {
		if (st.strategy == id)
			err('item_active', sprintf('Стратегия «%s» сейчас выбрана. Сначала выберите другую.', id));

		if (st.user_strategies[id] == null)
			err('not_found', sprintf('Своя стратегия «%s» не найдена', id));

		delete st.user_strategies[id];
		save_state(st);
		out({ ok: true, id: id, engines: [ 'nfqws' ] });
	}

	err('usage', 'Неверные аргументы. Справка: zaprett help');

case 'user':
	if (sub == 'get')
		out({ ok: true, id: argv[2], type: 'list', text: st.user[argv[2]] ?? '', entries: 1, size: length(st.user[argv[2]] ?? '') });

	if (sub == 'set') {
		const text = fs.stdin.read('all') ?? '';

		if (index(text, '*.') >= 0)
			err('invalid_entries', 'Неверных строк: 1. Исправьте их и сохраните снова.', { errors: [ { line: 1, value: '*.example.com', reason: 'bad_domain' } ] });

		st.user[argv[2]] = text;
		save_state(st);
		out({ ok: true, id: argv[2], entries: length(filter(split(text, '\n'), l => length(l) && substr(l, 0, 1) != '#')), size: length(text), reloaded: false });
	}

	err('usage', 'Неверные аргументы. Справка: zaprett help');

case 'repo':
	if (sub == 'fetch') {
		st.fetched_at = time();
		start_job(st, 'repo-fetch', 6, false);
	}

	if (sub == 'list') {
		if (!st.fetched_at)
			out({ ok: true, fetched_at: null, url: 'https://raw.githubusercontent.com/CherretGit/zaprett-repo/refs/heads/main/index.json', items: [] });

		const items = [];

		for (let r in REPO) {
			const local = find_item(st, r.id);
			const installed = (local != null && local.type == r.type);
			push(items, { ...r, installed: installed, installed_version: installed ? local.version : null, installed_source: installed ? local.source : null,
				update_available: installed && local.source == 'repo' && local.version != r.version, supported: r.type != 'byedpi',
				size: installed ? local.size : null, error: r.error ?? null });
		}

		out({ ok: true, fetched_at: st.fetched_at, url: 'https://raw.githubusercontent.com/CherretGit/zaprett-repo/refs/heads/main/index.json', items: items });
	}

	if (sub == 'install') {
		for (let x in slice(argv, 2))
			push(st.installed, x);

		start_job(st, 'repo-install', 8, index(argv, 'list-extended') >= 0);
	}

	if (sub == 'remove') {
		if (argv[2] == 'quic_initial_www_google_com')
			err('item_in_use', 'Элемент «quic_initial_www_google_com» нужен стратегиям: strategy-alt', { used_by: [ 'strategy-alt' ] });

		st.installed = filter(st.installed, x => x != argv[2]);
		start_job(st, 'repo-remove', 3, false);
	}

	if (sub == 'upgrade')
		start_job(st, 'repo-upgrade', 8, false);

	err('usage', 'Неверные аргументы. Справка: zaprett help');

case 'sources':
	if (sub == 'list')
		out(sources_reply(st));

	if (sub == 'update') {
		for (let name in slice(argv, 2))
			if (!st.sources[name])
				err('not_found', sprintf('Подписка «%s» не найдена', name));

		start_job(st, 'sources-update', 5, false);
	}

	if (sub == 'save') {
		const j = job_view(st);

		if (j?.state == 'running')
			err('job_busy', sprintf('Уже выполняется другая задача («%s»). Дождитесь её завершения или отмените её.', j.name), { job: { id: j.id, name: j.name } });

		let data = null;

		try {
			data = json(fs.stdin.read('all') ?? '');
		}
		catch (e) {
			data = null;
		}

		if (type(data) != 'object')
			err('bad_value', 'Ожидается JSON-объект {title,type,url,interval_hours,min_entries,enabled}');

		const cur = st.sources[argv[2]];

		if (!cur && (data.type == null || data.url == null))
			err('bad_value', 'Тип подписки: list, list_exclude, ipset или ipset_exclude');

		st.sources[argv[2]] = { ...(cur ?? { last_update: 0, entries: null, size: null, status: 'never', error: null, message: null, ram_mib: 0, item_id: 'src-' + argv[2] }), ...data };
		st.last_source_save = data;
		save_state(st);
		// mock_received is not a backend field: the tests use it to check what went to stdin
		out({ ok: true, name: argv[2], item_id: 'src-' + argv[2], type: st.sources[argv[2]].type, created: !cur,
			type_changed: !!(cur && data.type != null && cur.type != data.type), url_changed: !!(cur && data.url != null && cur.url != data.url),
			reloaded: false, mock_received: data });
	}

	if (sub == 'delete') {
		const j = job_view(st);

		if (j?.state == 'running')
			err('job_busy', sprintf('Уже выполняется другая задача («%s»). Дождитесь её завершения или отмените её.', j.name), { job: { id: j.id, name: j.name } });

		if (!st.sources[argv[2]])
			err('not_found', sprintf('Подписка «%s» не найдена', argv[2]));

		delete st.sources[argv[2]];
		save_state(st);
		out({ ok: true, name: argv[2], removed_item: true, reloaded: false });
	}

	err('usage', 'Неверные аргументы. Справка: zaprett help');

case 'presets':
	out(presets_reply(st));

case 'wizard':
	if (sub != 'apply' || length(argv) < 3)
		err('usage', 'Неверные аргументы. Справка: zaprett help');

	const selected = [], skipped = [], enabled_sources = [], variants = {};

	for (let ref in slice(argv, 2)) {
		const parts = split(ref, ':');
		const sid = parts[0];
		const svc0 = filter(PRESETS.services, s => s.id == sid)[0];

		if (!svc0)
			err('unknown_service', sprintf('Сервис «%s» не найден в пресетах', sid));

		// "<service>:<variant>": the lists of the variant instead of the core ones; the other sets are switched off
		const variant = (length(parts) > 1) ? filter(svc0.variants ?? [], v => v.id == parts[1])[0] : null;

		if (length(parts) > 1 && !variant)
			err('unknown_variant', sprintf('У сервиса «%s» нет варианта «%s»', sid, parts[1]));

		for (let v in [ svc0, ...(svc0.variants ?? []) ]) {
			st.lists = filter(st.lists, l => index(v.lists, l) < 0);
			st.ipsets = filter(st.ipsets, l => index(v.ipsets, l) < 0);
		}

		const svc = variant ? { ...svc0, lists: variant.lists, ipsets: variant.ipsets, sources: [] } : svc0;

		variants[sid] = variant ? variant.id : null;

		if (svc.works == 'no') {
			push(skipped, { id: sid, reason: 'works_no' });
			continue;
		}

		if (!length(svc.lists) && !length(svc.ipsets) && !length(svc.sources))
			err('preset_unavailable', sprintf('Для сервиса «%s» нет ни списков, ни подписок', sid));

		push(selected, sid);

		for (let l in svc.lists)
			if (index(st.lists, l) < 0)
				push(st.lists, l);

		for (let l in svc.ipsets)
			if (index(st.ipsets, l) < 0)
				push(st.ipsets, l);

		for (let n in svc.sources)
			if (st.sources[n] && index(enabled_sources, n) < 0) {
				st.sources[n].enabled = true;
				push(enabled_sources, n);
			}
	}

	if (!length(selected))
		err('preset_unavailable', 'Выбранные сервисы нельзя включить: обход для них не работает', { skipped: skipped });

	st.list_mode = 'whitelist';

	const res = { ok: true, services: selected, variants: variants, skipped: skipped, lists: st.lists, ipsets: st.ipsets,
		exclude_lists: st.exclude_lists, exclude_ipsets: st.exclude_ipsets, list_mode: 'whitelist', strategy: st.strategy,
		sources: enabled_sources, sources_disabled: [], reloaded: st.running, warnings: [] };

	if (length(enabled_sources)) {
		const cur = job_view(st);

		if (cur && cur.state == 'running')
			res.job_error = { code: 'job_busy', message: sprintf('Уже выполняется другая задача («%s»).', cur.name) };
		else {
			st.job = { id: sprintf('%d-%d', time(), 4242), name: 'sources-update', state: 'running', progress: 0,
				message: 'Задача запускается…', started: time(), finished: 0, rc: 0, duration: 5, fail: false };
			res.job = { id: st.job.id, name: 'sources-update' };
		}
	}

	save_state(st);
	out(res);

case 'test':
	if (sub == 'start') {
		st.results_mode = (index(argv, 'mock-big-results') >= 0) ? 'big'
			: ((index(argv, 'mock-trimmed-results') >= 0) ? 'trimmed' : 'small');
		st.test_apply_if_better = index(argv, '--apply-if-better') >= 0;
		st.test_flags = filter(argv, a => substr(a, 0, 2) == '--');
		start_job(st, 'test', 24, false);
	}

	if (sub == 'status')
		out(test_reply(st, index(argv, '--brief') >= 0));

	if (sub == 'stop') {
		const cur = job_view(st);

		if (cur?.name == 'test' && cur.state == 'running')
			request_cancel(st);

		if (st.test_override) {
			st.test_override = false;
			save_state(st);
			out({ ok: true, state: 'restored', reloaded: false });
		}

		err('no_job', 'Автоподбор не выполняется');
	}

	if (sub == 'apply') {
		st.strategy = argv[2];
		save_state(st);
		out({ ok: true, strategy: argv[2], engine: 'nfqws', reloaded: st.enabled || st.running, warnings: [] });
	}

	err('no_job', 'Автоподбор не выполняется');

case 'job':
	if (sub == 'status') {
		const r = job_reply(st);
		save_state(st);
		out(r);
	}

	if (sub == 'log') {
		const j = job_view(st);
		out({ ok: true, log: j ? sprintf('[12:00:00] %s: %s\nline with <b>html</b> & "quotes"', j.name, j.message) : '' });
	}

	if (sub == 'cancel')
		request_cancel(st);

	err('usage', 'Неверные аргументы. Справка: zaprett help');

case 'diag':
	// the web interface always asks for the quick report (full: false)
	out({ ok: true, full: index(argv, '--full') >= 0,
		text: '===== zaprett =====\nzaprett 1.0.0\n<script>alert(1)</script>\n\n===== nft list table inet zaprett =====\ntable inet zaprett { }\n' });

case 'probe':
	if (sub == 'status')
		out(probe_reply(st));

	const want_services = (sub == '--services') ? split(argv[2], ',') : [];

	for (let id in want_services)
		if (!length(filter(PRESETS.services, s => s.id == id)))
			err('unknown_service', sprintf('Сервис «%s» не найден в пресетах', id));

	st.probe = null;
	/* by default the services whose lists are active */
	st.probe_services = length(want_services) ? want_services
		: map(filter(presets_reply(st).services, s => s.enabled || s.partially_enabled), s => s.id);
	start_job(st, 'probe', 2, false);

case 'dns':
	if (sub == 'status')
		out({ ok: true, dns: dns_view(st) });

	if (sub == 'setup') {
		if (dns_view(st).encrypted)
			out({ ok: true, changed: false });

		start_job(st, 'dns-setup', 2, false);
	}

	err('usage', 'Неверные аргументы. Справка: zaprett help');

case 'diagnose':
	if (sub == 'status')
		out(diagnose_reply(st));

	const diag_services = (sub == '--services') ? split(argv[2], ',') : [];

	for (let id in diag_services)
		if (!length(filter(PRESETS.services, s => s.id == id)))
			err('unknown_service', sprintf('Сервис «%s» не найден в пресетах', id));

	st.diagnose = null;
	st.diagnose_services = length(diag_services) ? diag_services
		: map(filter(presets_reply(st).services, s => s.enabled || s.partially_enabled), s => s.id);
	start_job(st, 'diagnose', 2, false);

case 'monitor':
	if (sub == 'status')
		out(monitor_reply(st));

	err('usage', 'Неверные аргументы. Справка: zaprett help');

case 'log':
	const tail = (sub == '--tail') ? int(argv[2]) : 200;
	const lines = [];

	for (let i = 1; i <= tail; i++)
		push(lines, sprintf('Sep 22 12:00:%02d router daemon.info zaprett: line %d', i % 60, i));

	lines[0] = 'Sep 22 12:00:00 router daemon.err nfqws[2345]: <b>html</b> & "quotes"';
	out({ ok: true, lines: lines });

case 'page':
	const pages = {
		overview: () => ({ status: status_reply(st), job: job_reply(st), presets: presets_reply(st), monitor: monitor_reply(st), probe: probe_reply(st),
			dns: { ok: true, dns: dns_view(st) } }),
		lists: () => ({ status: status_reply(st), job: job_reply(st), items: items_reply(st, null), sources: sources_reply(st), presets: presets_reply(st) }),
		/* "big" results imitate a backend that ignores --brief inside page */
		strategies: () => ({ status: status_reply(st), job: job_reply(st), items: items_reply(st, null), test: test_reply(st, st.results_mode != 'big') }),
		diagnostics: () => ({ status: status_reply(st), job: job_reply(st), monitor: monitor_reply(st),
			dns: { ok: true, dns: dns_view(st) }, diagnose: diagnose_reply(st) })
	};

	if (!pages[sub])
		err('usage', 'Неверные аргументы. Справка: zaprett help');

	const build = pages[sub];

	out({ ok: true, ...build() });

default:
	err('usage', 'Неверные аргументы. Справка: zaprett help');
}
