// zaprett: engine argument generator (ARCHITECTURE §5).
'use strict';

import * as fs from 'fs';
import { P, run, is_file, is_id, mkdir_p, atomic_write, write_json, read_json, fail, MODE_FILE } from 'zaprett.util';
import * as V from 'zaprett.validate';
import * as S from 'zaprett.store';
import { ENGINE_OPTIONS } from 'zaprett.engine_options';

export const LEGACY_MODES = { split: 'fakedsplit', split2: 'multisplit', disorder: 'fakeddisorder', disorder2: 'multidisorder' };

// Options owned by zaprett (queue, privileges, logging, process control): removed from strategy text.
export const RESERVED_OPTIONS = [ 'qnum', 'user', 'uid', 'daemon', 'pidfile', 'debug', 'dry-run', 'version',
	'dpi-desync-fwmark', 'fwmark', 'intercept', 'chdir', 'writable', 'fuzz' ];

export const PLACEHOLDER_TYPES = {
	hostlist: 'list', hostlist_exclude: 'list_exclude', ipset: 'ipset', ipset_exclude: 'ipset_exclude',
	bin: 'bin', lua_lib: 'lua_lib'
};

export const DEFAULT_TCP_PORTS = [ [ 80, 80 ], [ 443, 443 ] ];
export const DEFAULT_UDP_PORTS = [ [ 443, 443 ] ];
export const WIDE_RANGE = 1024;

// Step 1: split text into tokens like split_whitespace(), dropping trailing '\' and '--comment' junk.
export function tokenize(text) {
	let tokens = [];
	for (let line in split(text ?? '', '\n')) {
		line = replace(replace(replace(replace(line, '\t', ' '), '\r', ' '), '\x0b', ' '), '\x0c', ' ');
		line = rtrim(line);
		while (substr(line, -1) == '\\')
			line = rtrim(substr(line, 0, length(line) - 1));
		for (let t in split(line, ' '))
			if (t != '' && t != '\\')
				push(tokens, t);
	}
	let out = [], dropped = [], skipping = false;
	for (let t in tokens) {
		if (skipping) {
			if (substr(t, 0, 2) == '--')
				skipping = false;
			else {
				push(dropped, t);
				continue;
			}
		}
		if (t == '--comment') {
			skipping = true;
			push(dropped, t);
			continue;
		}
		push(out, t);
	}
	return { tokens: out, dropped: dropped };
};

// Step 2: legacy --dpi-desync modes (nfqws only).
export function normalize_modes(tokens) {
	return map(tokens, (t) => {
		if (substr(t, 0, 13) != '--dpi-desync=')
			return t;
		let modes = map(split(substr(t, 13), ','), (m) => LEGACY_MODES[m] ?? m);
		return '--dpi-desync=' + join(',', modes);
	});
};

// Pure: the full option name as getopt_long_only resolves it — exact name, else the only option starting with it.
// { name } on success, { ambiguous: [names] } or { unknown: true } otherwise.
export function resolve_option(name, known) {
	if (name == '')
		return { unknown: true };
	if (known[name] != null)
		return { name: name };
	let hits = filter(sort(keys(known)), (k) => substr(k, 0, length(name)) == name);
	if (length(hits) == 1)
		return { name: hits[0] };
	return length(hits) ? { ambiguous: hits } : { unknown: true };
};

// Step 1b: the engines parse argv with getopt_long_only (musl): "-name" works like "--name", a name may be cut to any
// unambiguous prefix, and an option with a required argument takes the next word when there is no "=". Every option
// is rewritten to "--<full name>[=value]" by the table of the engine (engine_options.uc, generated from the engine
// sources by tools/data/gen_engine_options.py), so the checks below see exactly what the engine sees; words that are
// not options, unknown and ambiguous names are refused.
// ${hostlists} and ${ipsets} stay as they are.
export function canonicalize(tokens, engine) {
	let known = ENGINE_OPTIONS[engine];
	if (!known)
		return fail('bad_option', sprintf('Неизвестный движок «%s»', engine));
	let out = [];
	for (let i = 0; i < length(tokens); i++) {
		let t = tokens[i];
		if (t == '${hostlists}' || t == '${ipsets}') {
			push(out, t);
			continue;
		}
		let m = match(t, /^--?([^=]*)(=.*)?$/);
		if (!m)
			return fail('bad_option', sprintf('«%s» не является опцией движка %s (значение пишется через «=»: --опция=значение)', t, engine), { option: t });
		let r = resolve_option(m[1], known);
		if (r.ambiguous)
			return fail('bad_option', sprintf('Сокращённая опция «%s» подходит к нескольким опциям %s: %s', t, engine,
				join(', ', map(r.ambiguous, (n) => '--' + n))), { option: t });
		if (!r.name)
			return fail('bad_option', sprintf('Движок %s не знает опцию «%s»', engine, t), { option: t });
		let value = m[2];
		if (known[r.name] == 'no' && value != null)
			return fail('bad_option', sprintf('Опция --%s не принимает значения: «%s»', r.name, t), { option: t });
		if (known[r.name] == 'required' && value == null) {
			let next = tokens[i + 1];
			if (next == null || next == '${hostlists}' || next == '${ipsets}')
				return fail('bad_option', sprintf('Опции --%s нужно значение: --%s=значение', r.name, r.name), { option: t });
			value = '=' + next;
			i++;
		}
		push(out, '--' + r.name + (value ?? ''));
	}
	return { ok: true, tokens: out };
};

export function strip_reserved(tokens) {
	let out = [], ignored = [];
	for (let t in tokens) {
		let m = match(t, /^--?([A-Za-z0-9-]+)(=.*)?$/);
		if (m && index(RESERVED_OPTIONS, m[1]) >= 0) {
			if (index(ignored, m[1]) < 0)
				push(ignored, m[1]);
			continue;
		}
		push(out, t);
	}
	return { tokens: out, ignored: ignored };
};

// nfqws2 also starts a profile with --new=<name>: that token is kept as the first one of its profile.
function is_named_new(t) {
	return substr(t, 0, 6) == '--new=';
}

export function split_profiles(tokens) {
	let profiles = [ [] ];
	for (let t in tokens) {
		if (t == '--new')
			push(profiles, []);
		else if (is_named_new(t))
			push(profiles, [ t ]);
		else
			push(profiles[length(profiles) - 1], t);
	}
	return profiles;
};

export function join_profiles(profiles) {
	let out = [];
	for (let p in profiles) {
		if (length(out) && !(length(p) && is_named_new(p[0])))
			push(out, '--new');
		for (let t in p)
			push(out, t);
	}
	return out;
};

function list_nonempty(lists) {
	for (let l in lists)
		if (l.entries == null || l.entries > 0)
			return true;
	return false;
}

function expand_inline(t, env) {
	let err = null;
	let res = replace(t, /\$\{([^}]*)\}/g, (whole, inner) => {
		if (err)
			return whole;
		if (inner == 'hostlists' || inner == 'ipsets') {
			err = fail('placeholder_in_token', sprintf('Плейсхолдер ${%s} должен быть отдельным словом, а не частью «%s»', inner, t));
			return whole;
		}
		if (inner == 'zaprettdir')
			return env.zaprettdir;
		let m = match(inner, /^(hostlist|hostlist_exclude|ipset|ipset_exclude|bin|lua_lib):(.*)$/);
		if (!m) {
			err = fail('unknown_placeholder', sprintf('Неизвестный плейсхолдер ${%s} в стратегии', inner));
			return whole;
		}
		let id = m[2];
		if (!is_id(id)) {
			err = fail('bad_placeholder_id', sprintf('Недопустимый идентификатор в ${%s}', inner));
			return whole;
		}
		if (type(env.declared_deps) == 'array' && length(env.declared_deps) > 0 && index(env.declared_deps, id) < 0) {
			err = fail('dependency_not_declared', sprintf('Элемент «%s» не объявлен в зависимостях стратегии', id));
			return whole;
		}
		let r = env.resolve(PLACEHOLDER_TYPES[m[1]], id);
		if (!r) {
			err = fail('item_not_installed', sprintf('Стратегии нужен элемент «%s» (%s), но он не установлен', id, m[1]), { item: id });
			return whole;
		}
		return r.file;
	});
	if (err)
		return err;
	if (index(res, '${') >= 0)
		return fail('unknown_placeholder', sprintf('Незакрытый плейсхолдер в «%s»', t));
	return { ok: true, token: res };
}

// Step 3 for one profile. env: { mode, lists, exclude_lists, ipsets, exclude_ipsets,
// guard_hostlist, guard_ipset, zaprettdir, resolve(type, id), declared_deps }
export function expand_profile(tokens, env) {
	let wl = (env.mode != 'blacklist');
	let has_h = index(tokens, '${hostlists}') >= 0;
	let has_i = index(tokens, '${ipsets}') >= 0;
	let emit_h = true, emit_i = true;
	// nfqws ANDs ipset and hostlist filters of one profile, and an empty list filter passes everything.
	// When a profile uses both placeholders and only one kind has real entries, the other one is left
	// out (as in the original, where it expanded to an empty file); the guard of the used kind stays.
	if (wl && has_h && has_i) {
		let hn = list_nonempty(env.lists), inn = list_nonempty(env.ipsets);
		if (hn && !inn)
			emit_i = false;
		else if (!hn && inn)
			emit_h = false;
	}
	let out = [];
	for (let t in tokens) {
		if (t == '${hostlists}') {
			// Exclusions work in both modes (contract v1.1). They are left out together with the
			// hostlists when a dual profile matches by ipset only: any hostlist option, even an exclude,
			// makes nfqws require a known hostname (PROFILE_HOSTLISTS_EMPTY counts excludes).
			if (!emit_h)
				continue;
			if (wl) {
				for (let l in env.lists)
					push(out, '--hostlist=' + l.file);
				push(out, '--hostlist=' + env.guard_hostlist);
			}
			for (let l in env.exclude_lists)
				push(out, '--hostlist-exclude=' + l.file);
			// contract v1.2 §5: a whitelist profile with only ${hostlists} also gets the IP exclusions
			// (an empty include ipset passes, nfq/ipset.c:221-232); ${ipsets} adds them itself otherwise
			if (wl && !has_i)
				for (let l in env.exclude_ipsets)
					push(out, '--ipset-exclude=' + l.file);
			continue;
		}
		if (t == '${ipsets}') {
			if (wl && emit_i) {
				for (let l in env.ipsets)
					push(out, '--ipset=' + l.file);
				push(out, '--ipset=' + env.guard_ipset);
			}
			// ipset exclusions never need a hostname, so they are kept even when the include part is left out
			for (let l in env.exclude_ipsets)
				push(out, '--ipset-exclude=' + l.file);
			continue;
		}
		if (index(t, '${') < 0) {
			push(out, t);
			continue;
		}
		let r = expand_inline(t, env);
		if (!r.ok)
			return r;
		push(out, r.token);
	}
	return { ok: true, tokens: out };
};

// Game filter (contract v1.4 §15.2), after the Game Filter of Flowseal zapret-discord-youtube (general.bat,
// 2026-02-23…2026-08-30): profiles for the game ports, matched only by the active include ipsets. TCP — multisplit
// with seqovl 568 and a TLS ClientHello pattern, UDP — fake x12 with a QUIC Initial as the unknown-protocol fake;
// both for any protocol and only for the first packets of a connection (cutoff). nfqws2 gets the same attack
// written in its own syntax.
export const GAME_BIN_TCP = 'tls_clienthello_4pda_to';
export const GAME_BIN_UDP = 'quic_initial_www_google_com';

export function game_profiles(cfg, engine, env) {
	if (!list_nonempty(env.ipsets))
		return { ok: true, profiles: null };
	let filt = [];
	for (let l in env.ipsets)
		push(filt, '--ipset=' + l.file);
	push(filt, '--ipset=' + env.guard_ipset);
	for (let l in env.exclude_ipsets)
		push(filt, '--ipset-exclude=' + l.file);
	let bin = (id) => env.resolve('bin', id)?.file;
	let out = [];
	for (let proto in [ 'tcp', 'udp' ]) {
		let ports = cfg['game_ports_' + proto];
		if (!ports)
			continue;
		let id = (proto == 'tcp') ? GAME_BIN_TCP : GAME_BIN_UDP;
		let f = bin(id);
		if (!f)
			return fail('item_not_installed', sprintf('Игровому фильтру нужен элемент «%s» (bin), но он не установлен', id), { item: id });
		let p = [ '--filter-' + proto + '=' + ports ];
		for (let t in filt)
			push(p, t);
		if (engine == 'nfqws2') {
			if (proto == 'tcp')
				push(p, '--blob=zaprett_game_tcp:@' + f, '--out-range=<n3', '--payload=all',
					'--lua-desync=multisplit:pos=1:seqovl=568:seqovl_pattern=zaprett_game_tcp');
			else
				push(p, '--blob=zaprett_game_udp:@' + f, '--out-range=<n2', '--payload=all',
					'--lua-desync=fake:blob=zaprett_game_udp:repeats=12');
		}
		else if (proto == 'tcp')
			push(p, '--dpi-desync=multisplit', '--dpi-desync-any-protocol=1', '--dpi-desync-cutoff=n3',
				'--dpi-desync-split-seqovl=568', '--dpi-desync-split-pos=1', '--dpi-desync-split-seqovl-pattern=' + f);
		else
			push(p, '--dpi-desync=fake', '--dpi-desync-repeats=12', '--dpi-desync-any-protocol=1',
				'--dpi-desync-fake-unknown-udp=' + f, '--dpi-desync-cutoff=n2');
		push(out, p);
	}
	return { ok: true, profiles: out };
};

// Step 4.
export function base_options(cfg, engine, strategy_tokens) {
	let b = [];
	if (cfg.debug)
		push(b, '--debug=syslog');
	push(b, '--qnum=' + cfg.qnum, '--user=' + cfg.user);
	if (engine == 'nfqws2') {
		push(b, sprintf('--fwmark=0x%x', cfg.desync_mark));
		let own = filter(strategy_tokens, (t) => substr(t, 0, 11) == '--lua-init=');
		if (!length(own))
			// as zapret2 init.d/openwrt/zapret2: zapret-auto.lua holds the orchestrators (circular)
			push(b, '--lua-init=@' + P.share + '/lua/zapret-lib.lua', '--lua-init=@' + P.share + '/lua/zapret-antidpi.lua',
				'--lua-init=@' + P.share + '/lua/zapret-auto.lua');
	}
	else {
		push(b, sprintf('--dpi-desync-fwmark=0x%x', cfg.desync_mark));
	}
	return b;
};

// Step 6: ports for nftables from --filter-tcp/--filter-udp of every profile.
export function extract_ports(tokens) {
	let tcp = [], udp = [];
	for (let prof in split_profiles(tokens)) {
		if (index(prof, '--skip') >= 0)
			continue;
		let has = { tcp: false, udp: false }, neg = { tcp: false, udp: false }, rng = { tcp: [], udp: [] };
		for (let t in prof) {
			let m = match(t, /^--?filter-(tcp|udp)=(.*)$/);
			if (!m)
				continue;
			let p = V.parse_port_filter(m[2]);
			if (!p.ok)
				return fail('bad_port_filter', sprintf('Неверный фильтр портов «%s»', t));
			has[m[1]] = true;
			if (p.negated)
				neg[m[1]] = true;
			else
				for (let r in p.ranges)
					push(rng[m[1]], r);
		}
		if (!has.tcp && !has.udp) {
			for (let r in DEFAULT_TCP_PORTS)
				push(tcp, r);
			for (let r in DEFAULT_UDP_PORTS)
				push(udp, r);
			continue;
		}
		if (has.tcp)
			for (let r in (neg.tcp ? DEFAULT_TCP_PORTS : rng.tcp))
				push(tcp, r);
		if (has.udp)
			for (let r in (neg.udp ? DEFAULT_UDP_PORTS : rng.udp))
				push(udp, r);
	}
	let tm = V.merge_ranges(tcp), um = V.merge_ranges(udp);
	return { ok: true, tcp: V.ranges_to_strings(tm), udp: V.ranges_to_strings(um), tcp_ranges: tm, udp_ranges: um };
};

// Options whose value is a file (nfqws reads fakes/patterns as root while parsing options).
export const FILE_OPTIONS = [ 'hostlist', 'hostlist-exclude', 'hostlist-auto', 'hostlist-auto-debug', 'ipset', 'ipset-exclude',
	'dpi-desync-fake-http', 'dpi-desync-fake-tls', 'dpi-desync-fake-unknown', 'dpi-desync-fake-syndata', 'dpi-desync-fake-quic',
	'dpi-desync-fake-wireguard', 'dpi-desync-fake-dht', 'dpi-desync-fake-discord', 'dpi-desync-fake-stun',
	'dpi-desync-fake-unknown-udp', 'dpi-desync-split-seqovl-pattern', 'dpi-desync-fakedsplit-pattern',
	'dpi-desync-udplen-pattern', 'blob', 'lua-init' ];

// Files the engine writes (auto hostlists, as the unprivileged user) live only in this directory.
export const AUTOHOSTLIST_OPTIONS = [ 'hostlist-auto', 'hostlist-auto-debug' ];

export function autohostlist_dir() {
	return P.run + '/autohostlist';
};

export function allowed_roots() {
	let g = split(P.guard_hostlist, '/');
	pop(g);
	return uniq([ P.etc, P.share, P.bundle, P.run, join('/', g) ]);
};

function path_allowed(path, roots) {
	if (substr(path, 0, 1) != '/')
		return false;
	for (let seg in split(path, '/'))
		if (seg == '.' || seg == '..')
			return false;
	for (let r in roots)
		if (substr(path, 0, length(r) + 1) == r + '/')
			return true;
	return false;
}

// Pure: every file referenced by a strategy must lie inside zaprett's own directories, so a custom
// strategy cannot make the engine read (and send out as a fake packet) arbitrary files of the router.
export function check_file_options(tokens, roots) {
	for (let t in tokens) {
		let m = match(t, /^--?([A-Za-z0-9-]+)=(.*)$/);
		if (!m || index(FILE_OPTIONS, m[1]) < 0)
			continue;
		let v = m[2];
		if (index(AUTOHOSTLIST_OPTIONS, m[1]) >= 0) {
			if (!path_allowed(v, [ autohostlist_dir() ]))
				return fail('path_not_allowed', sprintf('Файл автолиста в --%s должен лежать в %s/', m[1], autohostlist_dir()));
			continue;
		}
		if (m[1] == 'blob') {
			let c = index(v, ':');
			if (c < 0)
				return fail('bad_option', sprintf('Неверное значение «%s»', t));
			v = substr(v, c + 1);
		}
		if (match(v, /^0[xX][0-9a-fA-F]*$/) || v == '!')
			continue;
		if (m[1] == 'lua-init' && substr(v, 0, 1) != '@')
			return fail('path_not_allowed', 'Встроенный Lua-код в --lua-init не разрешён, укажите файл: --lua-init=@<файл>');
		let fm = match(v, /^(\+[0-9]+)?@(.*)$/);
		if (fm)
			v = fm[2];
		if (!path_allowed(v, roots))
			return fail('path_not_allowed', sprintf('Файл «%s» в опции --%s находится вне каталогов zaprett', v, m[1]));
	}
	return { ok: true };
};

export const INCLUDE_FILTERS = [ 'hostlist', 'hostlist-domains', 'hostlist-auto', 'ipset', 'ipset-ip' ];

// Pure: profiles (1-based) that have no hostlist/ipset include filter and act on all traffic of their ports.
export function unfiltered_profiles(tokens) {
	let res = [], n = 0;
	for (let prof in split_profiles(tokens)) {
		n++;
		if (index(prof, '--skip') >= 0)
			continue;
		let filtered = false;
		for (let t in prof) {
			let m = match(t, /^--?([A-Za-z0-9-]+)=/);
			if (m && index(INCLUDE_FILTERS, m[1]) >= 0) {
				filtered = true;
				break;
			}
		}
		if (!filtered) {
			let p = extract_ports(prof);
			push(res, { profile: n, tcp: p.ok ? p.tcp : [], udp: p.ok ? p.udp : [] });
		}
	}
	return res;
};

function has_wide_range(ranges) {
	for (let r in ranges)
		if (r[1] - r[0] + 1 > WIDE_RANGE)
			return true;
	return false;
}

export function read_override() {
	let o = read_json(P.run + '/test-override', 4096);
	if (type(o) != 'object' || !is_id(o.strategy) || (o.engine != 'nfqws' && o.engine != 'nfqws2'))
		return null;
	return o;
};

// Subscriptions (src-<name>) that were never downloaded are skipped; any other missing item is an error.
function resolve_lists(idx, ids, itype, missing, not_downloaded) {
	let res = [];
	for (let id in ids) {
		let it = idx.items[itype]?.[id];
		if (!it) {
			push((substr(id, 0, 4) == 'src-') ? not_downloaded : missing, id);
			continue;
		}
		push(res, { id: id, file: it.file, entries: (it.source == 'user' && !is_file(it.file)) ? 0 : S.entries_of(it), source: it.source });
	}
	return res;
}

// Full pipeline without running the engine. opts: { index, engine, strategy, text, item, test_mode }
export function build(cfg, opts) {
	opts = opts ?? {};
	let idx = opts.index ?? S.scan();
	let engine = opts.engine ?? cfg.engine;
	let sid = opts.strategy ?? ((engine == 'nfqws2') ? cfg.strategy_nfqws2 : cfg.strategy);
	let warnings = [], details = {};
	let item = opts.item, text = opts.text;

	if (text == null) {
		if (!sid)
			return fail('no_strategy', (engine == 'nfqws2') ? 'Не выбрана стратегия для nfqws2' : 'Не выбрана стратегия');
		item = idx.items[engine]?.[sid];
		if (!item)
			return fail('strategy_not_found', sprintf('Стратегия «%s» для движка %s не найдена', sid, engine), { strategy: sid });
		text = S.read_strategy_text(item);
		if (text == null)
			return fail('strategy_unreadable', sprintf('Не удалось прочитать файл стратегии «%s» (нет файла или больше 64 КиБ)', sid));
	}
	item = item ?? { id: sid, name: sid, source: 'user', dependencies: [] };

	let tk = tokenize(text);
	let cn = canonicalize(tk.tokens, engine);
	if (!cn.ok)
		return cn;
	let tokens = cn.tokens;
	if (engine == 'nfqws')
		tokens = normalize_modes(tokens);
	let sr = strip_reserved(tokens);
	tokens = sr.tokens;
	if (length(sr.ignored)) {
		push(warnings, 'strategy_option_ignored');
		details.ignored_options = sr.ignored;
	}

	let missing = [], not_downloaded = [];
	let env = {
		mode: cfg.list_mode,
		lists: resolve_lists(idx, cfg.lists, 'list', missing, not_downloaded),
		exclude_lists: resolve_lists(idx, cfg.exclude_lists, 'list_exclude', missing, not_downloaded),
		ipsets: resolve_lists(idx, cfg.ipsets, 'ipset', missing, not_downloaded),
		exclude_ipsets: resolve_lists(idx, cfg.exclude_ipsets, 'ipset_exclude', missing, not_downloaded),
		guard_hostlist: P.guard_hostlist,
		guard_ipset: P.guard_ipset,
		zaprettdir: P.bundle + '/files',
		declared_deps: (item.source == 'user') ? null : item.dependencies,
		resolve: (itype, id) => idx.items[itype]?.[id]
	};
	S.flush_cache();
	if (length(missing))
		return fail('item_not_found', sprintf('Включённые списки не найдены: %s. Установите их или выключите в настройках.',
			join(', ', missing)), { missing_lists: missing });
	if (length(not_downloaded)) {
		push(warnings, 'source_not_downloaded');
		details.sources_not_downloaded = not_downloaded;
	}

	let profiles = [], empty_profiles = 0, uses_hostlists = false;
	for (let prof in split_profiles(tokens)) {
		if (!length(prof)) {
			empty_profiles++;
			continue;
		}
		if (index(prof, '${hostlists}') >= 0)
			uses_hostlists = true;
		let r = expand_profile(prof, env);
		if (!r.ok)
			return r;
		push(profiles, r.tokens);
	}
	if (!length(profiles))
		return fail('strategy_empty', sprintf('Стратегия «%s» не содержит ни одной опции', item.id));
	if (empty_profiles > 0 && length(split_profiles(tokens)) > 1) {
		push(warnings, 'empty_profile_removed');
		details.empty_profiles = empty_profiles;
	}
	if (uses_hostlists && cfg.list_mode != 'blacklist' && !list_nonempty(env.lists))
		push(warnings, 'no_active_lists');
	// the game filter goes to the end: the strategy's own profiles keep priority for their ports
	if (cfg.game_filter) {
		let gp = game_profiles(cfg, engine, env);
		if (!gp.ok)
			return gp;
		if (gp.profiles == null)
			push(warnings, 'game_filter_no_ipsets');
		else
			for (let p in gp.profiles)
				push(profiles, p);
	}

	let strat_tokens = join_profiles(profiles);
	let fc = check_file_options(strat_tokens, allowed_roots());
	if (!fc.ok)
		return fc;
	let ports = extract_ports(strat_tokens);
	if (!ports.ok)
		return ports;
	if (has_wide_range(ports.tcp_ranges) || has_wide_range(ports.udp_ranges))
		push(warnings, 'wide_port_range');
	// in blacklist mode every profile is meant to act on all traffic, the warning would be noise
	if (cfg.list_mode != 'blacklist') {
		let uf = unfiltered_profiles(strat_tokens);
		if (length(uf)) {
			push(warnings, 'profile_unfiltered');
			details.unfiltered_profiles = uf;
		}
	}

	let args = base_options(cfg, engine, strat_tokens);
	for (let t in strat_tokens)
		push(args, t);

	return {
		ok: true,
		engine: engine,
		strategy: { id: item.id, name: item.name ?? item.id, source: item.source },
		args: args,
		ports: { tcp: ports.tcp, udp: ports.udp },
		port_ranges: { tcp: ports.tcp_ranges, udp: ports.udp_ranges },
		warnings: warnings,
		details: details,
		dropped_tokens: tk.dropped
	};
};

// Step 5.
export function dry_run(engine, args) {
	let bin = P.libexec + '/' + engine;
	if (!is_file(bin))
		return { rc: -1, missing: true, output: sprintf('Не найден исполняемый файл движка %s (установите пакет zaprett-%s)', bin, engine) };
	let argv = [ bin, (engine == 'nfqws2') ? '--intercept=0' : '--dry-run' ];
	for (let a in args)
		if (substr(a, 0, 7) != '--debug')
			push(argv, a);
	let r = run(argv, { timeout: 30000, limit: 65536 });
	let lines = filter(split(r.stdout + '\n' + r.stderr, '\n'), (l) => trim(l) != '' && index(l, 'github version') != 0);
	return { rc: r.rc, output: join('\n', lines) };
};

// Creates missing user list files referenced by the configuration (nfqws refuses absent files).
export function ensure_user_files(cfg) {
	for (let opt in [ 'lists', 'exclude_lists', 'ipsets', 'exclude_ipsets' ]) {
		for (let id in cfg[opt]) {
			let path = S.user_list_path(id);
			if (path && !is_file(path)) {
				mkdir_p(P.user);
				fs.writefile(path, '');
				fs.chmod(path, MODE_FILE);
			}
		}
	}
};

export function dry_run_cache_path() {
	return P.run + '/dryrun-ok.json';
};

// Everything a dry-run checks: engine binary (size, mtime), the arguments and every file they name (size, mtime;
// a missing file is part of the key too). null when the binary is missing — then the dry-run always runs.
export function dry_run_key(engine, args) {
	let bin = fs.stat(P.libexec + '/' + engine);
	if (!bin)
		return null;
	let parts = [ sprintf('%s %d %d', engine, bin.size, bin.mtime) ];
	for (let a in args) {
		push(parts, a);
		let eq = index(a, '=');
		let v = (eq >= 0) ? substr(a, eq + 1) : a;
		if (substr(v, 0, 1) == '@')
			v = substr(v, 1);
		if (substr(v, 0, 1) != '/')
			continue;
		let st = fs.stat(v);
		push(parts, st ? sprintf('  %d %d', st.size, st.mtime) : '  missing');
	}
	return join('\n', parts);
};

// Runs build + dry-run. Returns the build result extended with dry_run { rc, output }.
// opts.force_dry_run: run the engine check even when the same arguments passed it before (`zaprett check`).
export function generate(cfg, opts) {
	opts = opts ?? {};
	let ov = opts.ignore_override ? null : read_override();
	if (ov && opts.strategy == null && opts.text == null) {
		opts.engine = ov.engine;
		opts.strategy = ov.strategy;
		opts.test_mode = true;
	}
	if (!opts.no_ensure)
		ensure_user_files(cfg);
	let b = build(cfg, opts);
	if (!b.ok)
		return b;
	b.test_mode = !!opts.test_mode;
	// the engine writes auto hostlists after dropping privileges: the directory belongs to that user
	if (!opts.no_ensure && length(filter(b.args, (a) => match(a, /^--?hostlist-auto(-debug)?=/)))) {
		mkdir_p(autohostlist_dir());
		fs.chown(autohostlist_dir(), cfg.user);
	}
	if (opts.skip_dry_run)
		return b;
	// `zaprett start` checks the arguments and then init runs gen-args with the same arguments: the second
	// dry-run (hundreds of ms on a weak router) is skipped when nothing it checks has changed
	let key = dry_run_key(b.engine, b.args);
	if (!opts.force_dry_run && key != null && read_json(dry_run_cache_path(), 1048576)?.key == key) {
		b.dry_run = { rc: 0, output: '', cached: true };
		return b;
	}
	let d = dry_run(b.engine, b.args);
	b.dry_run = { rc: d.rc, output: d.output };
	// opts.keep_dry_run_cache: the candidate of an isolated automatic selection must not evict the key of the main
	// engine (every restart of instance `test` runs gen-args of the main one as well)
	if (!opts.keep_dry_run_cache) {
		if (d.rc == 0 && key != null && mkdir_p(P.run))
			write_json(dry_run_cache_path(), { key: key });
		else if (d.rc != 0)
			fs.unlink(dry_run_cache_path());
	}
	if (d.rc != 0) {
		let r = fail(d.missing ? 'engine_missing' : 'dry_run_failed',
			d.missing ? d.output : 'Движок отклонил аргументы стратегии: ' + d.output, {
				strategy: b.strategy, engine: b.engine, args: b.args, dry_run: b.dry_run });
		return r;
	}
	return b;
};

// Writes /var/run/zaprett/{args,engine,ports.json,status.json} for the init script.
export function write_outputs(res) {
	if (!mkdir_p(P.run))
		return false;
	let st = {
		generated_at: time(),
		ok: res.ok,
		error: res.error,
		message: res.message,
		engine: res.engine,
		strategy: res.strategy,
		warnings: res.warnings ?? [],
		details: res.details ?? {},
		test_mode: !!res.test_mode
	};
	write_json(P.run + '/status.json', st);
	if (!res.ok)
		return true;
	return atomic_write(P.run + '/args', join('\n', res.args) + '\n') &&
		atomic_write(P.run + '/engine', res.engine + '\n') &&
		write_json(P.run + '/ports.json', res.ports);
};
