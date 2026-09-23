// zaprett: UCI configuration with defaults and validation (ARCHITECTURE §4).
'use strict';

import { cursor } from 'uci';
import * as V from 'zaprett.validate';
import { P, is_id } from 'zaprett.util';

// Имя секции подписки. Границы длины — через length(), см. util.is_id: интервал в регулярке ucode
// компилируется на каждый вызов и стоит миллисекунды.
function name_valid_1_32(s) {
	return type(s) == 'string' && length(s) >= 1 && length(s) <= 32 && match(s, /^[a-z0-9_]+$/) != null;
}

// UCI cursor; tests point it at a sandbox configuration directory through P.uci_confdir.
function uci_cursor() {
	return P.uci_confdir ? cursor(P.uci_confdir, P.uci_savedir ?? (P.uci_confdir + '.delta')) : cursor();
}

export const DEFAULTS = {
	main: {
		enabled: '0',
		engine: 'nfqws',
		strategy: 'strategy-general',
		strategy_nfqws2: '',
		list_mode: 'whitelist',
		lists: [ 'zaprett-youtube', 'zaprett-discord', 'user-hosts' ],
		exclude_lists: [ 'zaprett-exclude', 'user-hosts-exclude' ],
		ipsets: [],
		exclude_ipsets: [ 'zaprett-exclude-ipset', 'user-ipset-exclude' ],
		qnum: '200',
		desync_mark: '0x40000000',
		postnat_mark: '0x20000000',
		ipv6: '0',
		wan: [],
		tcp_pkt_out: '9',
		tcp_pkt_in: '3',
		udp_pkt_out: '9',
		udp_pkt_in: '0',
		flow_offload: 'own',
		clients_mode: 'all',
		clients: [],
		user: 'daemon',
		debug: '0',
		watchdog: '1',
		// contract v1.4 §15.2
		quic_block: '0',
		game_filter: '0',
		game_ports_tcp: '1024-65535',
		game_ports_udp: '1024-65535',
		deleted_sources: []
	},
	repo: {
		url: 'https://raw.githubusercontent.com/CherretGit/zaprett-repo/refs/heads/main/index.json',
		autoupdate: '1',
		autoupdate_hour: '4'
	},
	test: {
		timeout: '5',
		concurrency: '6',
		max_domains: '20',
		settle: '2'
	},
	// contract v1.3 §14.1; uci-defaults creates the section with these values when it is missing
	monitor: {
		enabled: '1',
		interval: '30',
		threshold: '3',
		auto_repair: '0',
		max_targets: '5',
		timeout: '8'
	}
};

// Minutes between monitor checks that a cron line can express (contract v1.3 §14.1).
export const MONITOR_INTERVALS = [ 10, 15, 20, 30, 60 ];

export const LIST_OPTIONS = [ 'lists', 'exclude_lists', 'ipsets', 'exclude_ipsets', 'wan', 'clients' ];

// Mark reserved for the LAN client filter (ARCHITECTURE §8).
export const CLIENT_MARK = 0x08000000;

// Conntrack mark of the connections of an isolated automatic selection (contract v1.6 §17): their replies go to the
// test queue as well.
export const TEST_MARK = 0x04000000;

function as_list(v) {
	if (v == null)
		return [];
	if (type(v) != 'array')
		v = [ v ];
	let out = [];
	for (let x in v) {
		x = trim('' + x);
		if (x != '' && index(out, x) < 0)
			push(out, x);
	}
	return out;
};

function pick(raw, name, sec) {
	return (raw != null && raw[name] != null) ? raw[name] : DEFAULTS[sec][name];
};

// UCI stores an empty list by removing the option (LuCI does the same), so inside an existing section a
// missing list option means "empty"; the defaults apply only when the whole section is missing.
function pick_list(raw, name, sec) {
	return (raw != null) ? (raw[name] ?? []) : DEFAULTS[sec][name];
}

// Pure: turns raw UCI section objects into a typed configuration.
// Invalid values fall back to defaults and add a warning 'bad_config' with the option name.
export function normalize(main, repo, test, monitor) {
	let warnings = [], bad = [];
	function bad_opt(name) {
		push(bad, name);
	}
	// options of the monitor section are reported as monitor.<name>: `timeout` and `enabled` exist in other sections too
	let opt_name = (sec, name) => (sec == 'monitor') ? ('monitor.' + name) : name;
	function enum_opt(name, allowed) {
		let v = pick(main, name, 'main');
		if (index(allowed, v) < 0) {
			bad_opt(name);
			return DEFAULTS.main[name];
		}
		return v;
	}
	function bool_opt(raw, sec, name) {
		let v = pick(raw, name, sec);
		if (v == '1' || v == 'true' || v == 'on' || v == 'yes')
			return true;
		if (v == '0' || v == 'false' || v == 'off' || v == 'no' || v == '')
			return false;
		push(bad, opt_name(sec, name));
		return DEFAULTS[sec][name] == '1';
	}
	function uint_opt(raw, sec, name, min, max) {
		let n = V.parse_uint(pick(raw, name, sec), min, max);
		if (n == null) {
			push(bad, opt_name(sec, name));
			n = int(DEFAULTS[sec][name]);
		}
		return n;
	}
	function mark_opt(name) {
		let n = V.parse_mark(pick(main, name, 'main'));
		if (n == null) {
			bad_opt(name);
			n = V.parse_mark(DEFAULTS.main[name]);
		}
		return n;
	}

	// ports of the game filter profile (contract v1.4 §15.2): "a,b-c", no negation, 1..65535; '' = none
	function game_ports_opt(name) {
		let v = pick(main, name, 'main');
		if (v == '')
			return '';
		let r = V.game_ports(v);
		if (r == null) {
			bad_opt(name);
			return DEFAULTS.main[name];
		}
		return r;
	}

	let ids = (name) => filter(as_list(pick_list(main, name, 'main')), (x) => {
		if (!is_id(x)) {
			bad_opt(name);
			return false;
		}
		return true;
	});

	let cfg = {
		enabled: bool_opt(main, 'main', 'enabled'),
		engine: enum_opt('engine', [ 'nfqws', 'nfqws2' ]),
		strategy: pick(main, 'strategy', 'main'),
		strategy_nfqws2: pick(main, 'strategy_nfqws2', 'main'),
		list_mode: enum_opt('list_mode', [ 'whitelist', 'blacklist' ]),
		lists: ids('lists'),
		exclude_lists: ids('exclude_lists'),
		ipsets: ids('ipsets'),
		exclude_ipsets: ids('exclude_ipsets'),
		qnum: uint_opt(main, 'main', 'qnum', 0, 65535),
		desync_mark: mark_opt('desync_mark'),
		postnat_mark: mark_opt('postnat_mark'),
		ipv6: bool_opt(main, 'main', 'ipv6'),
		wan: [],
		tcp_pkt_out: uint_opt(main, 'main', 'tcp_pkt_out', 0, 1000),
		tcp_pkt_in: uint_opt(main, 'main', 'tcp_pkt_in', 0, 1000),
		udp_pkt_out: uint_opt(main, 'main', 'udp_pkt_out', 0, 1000),
		udp_pkt_in: uint_opt(main, 'main', 'udp_pkt_in', 0, 1000),
		flow_offload: enum_opt('flow_offload', [ 'auto', 'own', 'keep' ]),
		clients_mode: enum_opt('clients_mode', [ 'all', 'include', 'exclude' ]),
		clients4: [],
		clients_mac: [],
		user: pick(main, 'user', 'main'),
		debug: bool_opt(main, 'main', 'debug'),
		watchdog: bool_opt(main, 'main', 'watchdog'),
		quic_block: bool_opt(main, 'main', 'quic_block'),
		game_filter: bool_opt(main, 'main', 'game_filter'),
		game_ports_tcp: game_ports_opt('game_ports_tcp'),
		game_ports_udp: game_ports_opt('game_ports_udp'),
		deleted_sources: filter(as_list(pick_list(main, 'deleted_sources', 'main')), (x) => name_valid_1_32(x)),
		repo: {
			url: pick(repo, 'url', 'repo'),
			autoupdate: bool_opt(repo, 'repo', 'autoupdate'),
			autoupdate_hour: uint_opt(repo, 'repo', 'autoupdate_hour', 0, 23)
		},
		test: {
			timeout: uint_opt(test, 'test', 'timeout', 1, 60),
			concurrency: uint_opt(test, 'test', 'concurrency', 1, 16),
			max_domains: uint_opt(test, 'test', 'max_domains', 0, 500),
			settle: uint_opt(test, 'test', 'settle', 0, 30)
		},
		// a missing section means "monitor off" (status.monitor = null); uci-defaults creates it on install
		monitor: {
			present: monitor != null,
			enabled: monitor != null && bool_opt(monitor, 'monitor', 'enabled'),
			interval: uint_opt(monitor, 'monitor', 'interval', 1, 60),
			threshold: uint_opt(monitor, 'monitor', 'threshold', 1, 20),
			auto_repair: bool_opt(monitor, 'monitor', 'auto_repair'),
			max_targets: uint_opt(monitor, 'monitor', 'max_targets', 1, 20),
			timeout: uint_opt(monitor, 'monitor', 'timeout', 2, 30)
		}
	};

	if (index(MONITOR_INTERVALS, cfg.monitor.interval) < 0) {
		bad_opt('monitor.interval');
		cfg.monitor.interval = int(DEFAULTS.monitor.interval);
	}

	for (let s in [ 'strategy', 'strategy_nfqws2' ]) {
		if (cfg[s] != '' && !is_id(cfg[s])) {
			bad_opt(s);
			cfg[s] = '';
		}
	}

	for (let w in as_list(pick_list(main, 'wan', 'main'))) {
		if (V.iface_name_valid(w))
			push(cfg.wan, w);
		else
			bad_opt('wan');
	}

	for (let c in as_list(pick_list(main, 'clients', 'main'))) {
		let mac = V.mac_normalize(c);
		if (mac) {
			push(cfg.clients_mac, mac);
			continue;
		}
		let net = V.cidr_parse(c);
		if (net && net.family == 4)
			push(cfg.clients4, net.net);
		else
			bad_opt('clients');
	}

	if (!V.user_name_valid(cfg.user)) {
		bad_opt('user');
		cfg.user = DEFAULTS.main.user;
	}

	if (!V.https_url_valid(cfg.repo.url)) {
		push(bad, 'url');
		cfg.repo.url = DEFAULTS.repo.url;
	}

	// marks are bit masks: they must not share bits with each other or with the client-filter mark
	if ((cfg.desync_mark & cfg.postnat_mark) || (cfg.desync_mark & CLIENT_MARK) || (cfg.postnat_mark & CLIENT_MARK)) {
		bad_opt('desync_mark');
		cfg.desync_mark = V.parse_mark(DEFAULTS.main.desync_mark);
		cfg.postnat_mark = V.parse_mark(DEFAULTS.main.postnat_mark);
	}

	if (length(bad))
		push(warnings, 'bad_config');

	cfg.bad_options = uniq(bad);
	cfg.warnings = warnings;
	return cfg;
};

export function current_strategy_id(cfg, engine) {
	return ((engine ?? cfg.engine) == 'nfqws2') ? cfg.strategy_nfqws2 : cfg.strategy;
};

export function strategy_option(engine) {
	return (engine == 'nfqws2') ? 'strategy_nfqws2' : 'strategy';
};

function section(u, name) {
	let s = u.get_all('zaprett', name);
	return (type(s) == 'object') ? s : null;
}

export function load() {
	let u = uci_cursor();
	u.load('zaprett');
	let cfg = normalize(section(u, 'main'), section(u, 'repo'), section(u, 'test'), section(u, 'monitor'));
	cfg.present = (section(u, 'main') != null);
	u.unload('zaprett');
	return cfg;
};

// Applies changes to section 'main' (or another named section) and commits.
// changes: { option: string | array | null }. null deletes the option.
export function set(changes, sec) {
	sec = sec ?? 'main';
	let u = uci_cursor();
	u.load('zaprett');
	if (u.get('zaprett', sec) == null) {
		let t = (sec == 'main') ? 'main' : sec;
		u.set('zaprett', sec, t);
	}
	for (let k, v in changes) {
		if (v == null || (type(v) == 'array' && length(v) == 0))
			u.delete('zaprett', sec, k);
		else
			u.set('zaprett', sec, k, v);
	}
	let committed = u.commit('zaprett');
	u.unload('zaprett');
	return !!committed;
};

/* ---------- URL subscriptions: config source '<name>' (contract v1.1 §4) ---------- */

export const SOURCE_TYPES = [ 'list', 'list_exclude', 'ipset', 'ipset_exclude' ];

// Subscriptions shipped in /etc/config/zaprett (contract v1.1 §4); restored by `sources defaults`
// (uci-defaults) unless listed in main.deleted_sources.
export const DEFAULT_SOURCES = [
	{ name: 'refilter_domains', values: { enabled: '0', name: 'Re:filter — заблокированные домены', type: 'list',
		url: 'https://github.com/1andrevich/Re-filter-lists/releases/latest/download/domains_all.lst',
		interval_hours: '72', min_entries: '1000', min_valid_ratio: '0.99', ram_mib: '9' } },
	{ name: 'antifilter_allyouneed', values: { enabled: '0', name: 'antifilter — заблокированные IP-сети', type: 'ipset',
		url: 'https://antifilter.download/list/allyouneed.lst',
		interval_hours: '72', min_entries: '1000', min_valid_ratio: '0.99', ram_mib: '2' } },
	{ name: 'cloudflare_v4', values: { enabled: '0', name: 'Cloudflare — IPv4-сети', type: 'ipset',
		url: 'https://www.cloudflare.com/ips-v4',
		interval_hours: '168', min_entries: '5', min_valid_ratio: '0.99', ram_mib: '1' } },
	{ name: 'cloudflare_v6', values: { enabled: '0', name: 'Cloudflare — IPv6-сети', type: 'ipset',
		url: 'https://www.cloudflare.com/ips-v6',
		interval_hours: '168', min_entries: '3', min_valid_ratio: '0.99', ram_mib: '1' } }
];
export const SOURCE_DEFAULTS = { enabled: '0', name: '', type: 'list', url: '', interval_hours: '72', min_entries: '1',
	min_valid_ratio: '0.99', ram_mib: '0' };

export function source_name_valid(name) {
	return name_valid_1_32(name);
};

export function source_url_valid(url) {
	return V.https_url_valid(url);
};

// Pure: typed subscription from a raw UCI section. valid=false when name/type/url are unusable.
export function normalize_source(name, raw) {
	let bad = [];
	let get = (k) => (raw != null && raw[k] != null) ? raw[k] : SOURCE_DEFAULTS[k];
	let en = get('enabled');
	let s = {
		name: name,
		enabled: (en == '1' || en == 'true' || en == 'on' || en == 'yes'),
		title: (type(get('name')) == 'string') ? substr(trim(get('name')), 0, 128) : '',
		type: get('type'),
		url: get('url'),
		interval_hours: V.parse_uint(get('interval_hours'), 1, 8760),
		min_entries: V.parse_uint(get('min_entries'), 0, 100000000),
		min_valid_ratio: null,
		ram_mib: V.parse_uint(get('ram_mib'), 0, 65536)
	};
	let r = get('min_valid_ratio');
	if (type(r) == 'string' && match(r, /^(0(\.[0-9]{1,6})?|1(\.0{1,6})?)$/))
		s.min_valid_ratio = json(r);
	for (let k in [ 'interval_hours', 'min_entries', 'min_valid_ratio', 'ram_mib' ]) {
		if (s[k] == null) {
			push(bad, k);
			s[k] = (k == 'min_valid_ratio') ? json(SOURCE_DEFAULTS[k]) : int(SOURCE_DEFAULTS[k]);
		}
	}
	if (!source_name_valid(name))
		push(bad, 'section');
	if (index(SOURCE_TYPES, s.type) < 0)
		push(bad, 'type');
	if (!source_url_valid(s.url))
		push(bad, 'url');
	if (s.title == '')
		s.title = name;
	s.valid = !(index(bad, 'section') >= 0 || index(bad, 'type') >= 0 || index(bad, 'url') >= 0);
	s.bad_options = bad;
	return s;
};

export function load_sources() {
	let u = uci_cursor();
	u.load('zaprett');
	let res = [];
	u.foreach('zaprett', 'source', (sec) => {
		push(res, normalize_source(sec['.name'], sec));
	});
	u.unload('zaprett');
	return res;
};

// values: UCI option -> string (null deletes). Creates the section when missing.
export function set_source(name, values) {
	let u = uci_cursor();
	u.load('zaprett');
	if (u.get('zaprett', name) == null)
		u.set('zaprett', name, 'source');
	else if (u.get('zaprett', name) != 'source') {
		u.unload('zaprett');
		return false;
	}
	for (let k, v in values) {
		if (v == null)
			u.delete('zaprett', name, k);
		else
			u.set('zaprett', name, k, '' + v);
	}
	let c = u.commit('zaprett');
	u.unload('zaprett');
	return !!c;
};

export function delete_source(name) {
	let u = uci_cursor();
	u.load('zaprett');
	if (u.get('zaprett', name) != 'source') {
		u.unload('zaprett');
		return false;
	}
	u.delete('zaprett', name);
	let c = u.commit('zaprett');
	u.unload('zaprett');
	return !!c;
};

// Raw access to another package (firewall) for offload handling.
export function fw_defaults_section() {
	let u = uci_cursor();
	u.load('firewall');
	let name = null;
	u.foreach('firewall', 'defaults', (s) => {
		if (name == null)
			name = s['.name'];
	});
	let res = null;
	if (name != null)
		res = { name: name, flow_offloading: u.get('firewall', name, 'flow_offloading'), flow_offloading_hw: u.get('firewall', name, 'flow_offloading_hw') };
	u.unload('firewall');
	return res;
};

export function fw_set_offload(values) {
	let u = uci_cursor();
	u.load('firewall');
	let name = null;
	u.foreach('firewall', 'defaults', (s) => {
		if (name == null)
			name = s['.name'];
	});
	if (name == null) {
		u.unload('firewall');
		return false;
	}
	for (let k, v in values) {
		if (v == null)
			u.delete('firewall', name, k);
		else
			u.set('firewall', name, k, v);
	}
	let okc = u.commit('firewall');
	u.unload('firewall');
	return !!okc;
};
