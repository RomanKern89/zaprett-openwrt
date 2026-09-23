// zaprett: nftables table inet zaprett (ARCHITECTURE §8, ADR-003).
'use strict';

import * as fs from 'fs';
import * as ubus from 'ubus';
import { P, run, run_quiet, mkdir_p, atomic_write, fail, ok } from 'zaprett.util';
import * as V from 'zaprett.validate';
import { CLIENT_MARK, TEST_MARK } from 'zaprett.config';

export const TABLE = 'zaprett';
export const NOZAPRETT4 = [ '0.0.0.0/8', '10.0.0.0/8', '100.64.0.0/10', '127.0.0.0/8', '169.254.0.0/16',
	'172.16.0.0/12', '192.168.0.0/16', '224.0.0.0/3' ];
export const NOZAPRETT6 = [ '::1/128', 'fc00::/7', 'fe80::/10', 'ff00::/8' ];

export function nft_path() {
	return P.run + '/zaprett.nft';
};

// Pure: WAN devices from `ubus call network.interface dump`.
// wan_names empty -> every interface that is up and owns a default route in the main table.
export function wan_devices(dump, wan_names) {
	let v4 = [], v6 = [];
	let ifaces = (type(dump) == 'object' && type(dump.interface) == 'array') ? dump.interface : [];
	function add(arr, d) {
		if (V.iface_name_valid(d) && d != 'lo' && index(arr, d) < 0)
			push(arr, d);
	}
	for (let i in ifaces) {
		if (type(i) != 'object' || !i.up)
			continue;
		let d = i.l3_device ?? i.device;
		if (length(wan_names ?? [])) {
			if (index(wan_names, i.interface) >= 0) {
				add(v4, d);
				add(v6, d);
			}
			continue;
		}
		for (let r in (type(i.route) == 'array' ? i.route : [])) {
			if (type(r) != 'object' || r.table != null || r.mask != 0)
				continue;
			if (r.target == '0.0.0.0')
				add(v4, d);
			else if (r.target == '::')
				add(v6, d);
		}
	}
	return { v4: sort(v4), v6: sort(v6) };
};

// Pure: does the WAN have IPv6 (an interface that is up with a default route ::/0)? With wan_names only those
// interfaces count, plus interfaces on the same device (wan6 next to wan).
export function wan_ipv6_default(dump, wan_names) {
	let ifaces = (type(dump) == 'object' && type(dump.interface) == 'array') ? dump.interface : [];
	let named = length(wan_names ?? []) > 0, devs = [];
	for (let i in ifaces)
		if (type(i) == 'object' && named && index(wan_names, i.interface) >= 0)
			push(devs, i.l3_device ?? i.device);
	for (let i in ifaces) {
		if (type(i) != 'object' || !i.up)
			continue;
		if (named && index(wan_names, i.interface) < 0 && index(devs, i.l3_device ?? i.device) < 0)
			continue;
		for (let r in (type(i.route) == 'array' ? i.route : []))
			if (type(r) == 'object' && r.table == null && r.mask == 0 && r.target == '::')
				return true;
	}
	return false;
};

export function query_wan(cfg) {
	let conn = ubus.connect();
	let dump = conn ? conn.call('network.interface', 'dump', {}) : null;
	if (conn)
		conn.disconnect();
	let w = wan_devices(dump, cfg.wan);
	w.ipv6_default = wan_ipv6_default(dump, cfg.wan);
	return w;
};

// Text of the ruleset applied last (the file is removed by `fw remove`), or null.
export function applied_text() {
	return fs.readfile(nft_path(), 1048576);
};

function elements(items, quote) {
	if (!length(items))
		return '';
	let q = map(items, (x) => quote ? sprintf('"%s"', x) : x);
	return 'elements = { ' + join(', ', q) + ' } ';
}

function hexmark(n) {
	return sprintf('0x%08x', n);
}

function pkt_range(n) {
	return (n == 1) ? '1' : sprintf('1-%d', n);
}

// Pure: the packet count after which the own flowtable takes a connection (contract v1.4 §15.1): the engine has seen
// every packet the postnat rules send to it.
export function offload_after(cfg) {
	return (cfg.tcp_pkt_out > cfg.udp_pkt_out) ? cfg.tcp_pkt_out : cfg.udp_pkt_out;
};

// Outgoing rules to queue q (ports null = none). cl: client filter expression.
function push_postnat(L, cfg, ports, cl, q) {
	let dm = hexmark(cfg.desync_mark), pm = hexmark(cfg.postnat_mark);
	for (let proto in [ 'tcp', 'udp' ]) {
		let n = cfg[proto + '_pkt_out'], pl = ports?.[proto] ?? [];
		if (n <= 0 || !length(pl))
			continue;
		let set = '{ ' + join(', ', pl) + ' }';
		let tail = sprintf('%s meta mark set meta mark or %s ct mark set ct mark or %s queue num %d bypass', cl, pm, dm, q);
		push(L, sprintf('\t\toifname @wanif %s dport %s ct original packets %s ip daddr != @nozaprett%s', proto, set, pkt_range(n), tail));
		if (cfg.ipv6)
			push(L, sprintf('\t\toifname @wanif6 %s dport %s ct original packets %s ip6 daddr != @nozaprett6%s', proto, set, pkt_range(n), tail));
	}
}

// Incoming (reply) rules to queue q.
function push_prenat(L, cfg, ports, cl, q) {
	let dm = hexmark(cfg.desync_mark);
	for (let proto in [ 'tcp', 'udp' ]) {
		let n = cfg[proto + '_pkt_in'], pl = ports?.[proto] ?? [];
		if (n <= 0 || !length(pl))
			continue;
		let set = '{ ' + join(', ', pl) + ' }';
		let tail = sprintf('%s ct mark set ct mark or %s queue num %d bypass', cl, dm, q);
		push(L, sprintf('\t\tiifname @wanif %s sport %s ct reply packets %s ip saddr != @nozaprett%s', proto, set, pkt_range(n), tail));
		if (cfg.ipv6)
			push(L, sprintf('\t\tiifname @wanif6 %s sport %s ct reply packets %s ip6 saddr != @nozaprett6%s', proto, set, pkt_range(n), tail));
	}
}

// Pure: renders the whole script. ports = { tcp: ["80","443"], udp: [...] }, wan = { v4: [...], v6: [...] }.
// opts: { test_mode, flowtable: null | { devices: [...], hw: bool } — the own flowtable of mode own (§15.1),
// isolation: null | { uid, qnum, ports } — test chains of the isolated automatic selection (v1.6 §17) }.
export function render(cfg, ports, wan, opts) {
	opts = opts ?? {};
	let dm = hexmark(cfg.desync_mark), pm = hexmark(cfg.postnat_mark), cm = hexmark(CLIENT_MARK);
	let q = cfg.qnum;
	let clients = (!opts.test_mode && cfg.clients_mode != 'all') ? cfg.clients_mode : null;
	let client_expr = (mode) => (mode == 'include') ? sprintf(' ct mark and %s != 0', cm) :
		((mode == 'exclude') ? sprintf(' ct mark and %s == 0', cm) : '');
	let cl = client_expr(clients);
	// the QUIC block serves LAN clients only, so it keeps the client filter during an automatic selection too
	let qclients = (cfg.quic_block && cfg.clients_mode != 'all') ? cfg.clients_mode : null;
	let qcl = client_expr(qclients);

	let L = [];
	push(L, 'table inet zaprett', 'delete table inet zaprett', 'table inet zaprett {');
	push(L, '\tset wanif { type ifname; ' + elements(wan.v4 ?? [], true) + '}');
	if (cfg.ipv6)
		push(L, '\tset wanif6 { type ifname; ' + elements(wan.v6 ?? [], true) + '}');
	push(L, '\tset nozaprett { type ipv4_addr; flags interval; auto-merge; ' + elements(NOZAPRETT4, false) + '}');
	if (cfg.ipv6)
		push(L, '\tset nozaprett6 { type ipv6_addr; flags interval; auto-merge; ' + elements(NOZAPRETT6, false) + '}');

	if (clients || qclients) {
		push(L, '\tset clients4 { type ipv4_addr; flags interval; auto-merge; ' + elements(cfg.clients4, false) + '}');
		push(L, '\tset clientsmac { type ether_addr; ' + elements(cfg.clients_mac, false) + '}');
		push(L, '\tchain clients_mark {');
		push(L, '\t\ttype filter hook prerouting priority -150; policy accept;');
		push(L, sprintf('\t\tiifname != @wanif ip saddr @clients4 ct mark set ct mark or %s', cm));
		push(L, sprintf('\t\tiifname != @wanif ether saddr @clientsmac ct mark set ct mark or %s', cm));
		push(L, '\t}');
	}

	let ft = opts.flowtable;
	if (ft && length(ft.devices)) {
		push(L, '\tflowtable ft {');
		push(L, '\t\thook ingress priority filter;');
		push(L, '\t\tdevices = { ' + join(', ', map(ft.devices, (d) => sprintf('"%s"', d))) + ' };');
		push(L, '\t\tcounter;');
		if (ft.hw)
			push(L, '\t\tflags offload;');
		push(L, '\t}');
		push(L, '\tchain forward_offload {');
		push(L, '\t\ttype filter hook forward priority filter + 1; policy accept;');
		push(L, sprintf('\t\tmeta l4proto { tcp, udp } ct original packets > %d flow add @ft', offload_after(cfg)));
		push(L, '\t}');
	}

	// QUIC of LAN clients (contract v1.4 §15.2): browsers fall back to TCP, where the bypass works. Only forwarded
	// traffic, so the router's own traffic is not touched; the client filter applies as in postnat.
	if (cfg.quic_block) {
		push(L, '\tchain forward_quic {');
		push(L, '\t\ttype filter hook forward priority filter - 1; policy accept;');
		push(L, sprintf('\t\tiifname != @wanif oifname @wanif udp dport 443%s counter drop', qcl));
		if (cfg.ipv6)
			push(L, sprintf('\t\tiifname != @wanif6 oifname @wanif6 udp dport 443%s counter drop', qcl));
		push(L, '\t}');
	}

	push(L, '\tchain postnat_hook {');
	push(L, '\t\ttype filter hook postrouting priority 101; policy accept;');
	push(L, sprintf('\t\tmeta mark and %s == 0 jump postnat', dm));
	push(L, '\t}');

	// isolated automatic selection (contract v1.6 §17): connections of the test user get TEST_MARK and leave the main
	// rules through goto; their own chains send them to the test queue (no rules at all during the baseline)
	let iso = opts.isolation, tm = hexmark(TEST_MARK);
	push(L, '\tchain postnat {');
	if (iso)
		push(L, sprintf('\t\tmeta skuid %d ct mark set ct mark or %s goto postnat_test', iso.uid, tm));
	push_postnat(L, cfg, ports, cl, q);
	push(L, '\t}');
	if (iso) {
		push(L, '\tchain postnat_test {');
		push_postnat(L, cfg, iso.ports, '', iso.qnum);
		push(L, '\t}');
	}

	push(L, '\tchain prenat {');
	push(L, '\t\ttype filter hook prerouting priority -101; policy accept;');
	if (iso)
		push(L, sprintf('\t\tct mark and %s != 0 goto prenat_test', tm));
	push_prenat(L, cfg, ports, cl, q);
	push(L, '\t}');
	if (iso) {
		push(L, '\tchain prenat_test {');
		push_prenat(L, cfg, iso.ports, '', iso.qnum);
		push(L, '\t}');
	}

	push(L, '\tchain prerouting_icmp {');
	push(L, '\t\ttype filter hook prerouting priority -99; policy accept;');
	push(L, '\t\ticmp type time-exceeded ct state invalid drop');
	push(L, sprintf('\t\ticmp type time-exceeded ct mark and %s != 0 drop', dm));
	if (cfg.ipv6)
		push(L, sprintf('\t\ticmpv6 type time-exceeded ct mark and %s != 0 drop', dm));
	push(L, '\t}');

	push(L, '\tchain predefrag {');
	push(L, '\t\ttype filter hook output priority -401; policy accept;');
	push(L, sprintf('\t\tmeta mark and %s != 0 jump predefrag_nfqws', dm));
	push(L, '\t}');
	push(L, '\tchain predefrag_nfqws {');
	push(L, sprintf('\t\tmeta mark and %s != 0 notrack', pm));
	push(L, '\t\tip frag-off & 0x1fff != 0 notrack');
	push(L, '\t\texthdr frag exists notrack');
	push(L, '\t\ttcp flags ! syn,rst,ack notrack');
	push(L, '\t}');
	push(L, '}');
	return join('\n', L) + '\n';
};

export function check_file(path) {
	let r = run([ 'nft', '-c', '-f', path ], { timeout: 30000, limit: 65536 });
	return { rc: r.rc, output: trim(r.stdout + '\n' + r.stderr) };
};

// Validates with `nft -c -f`, then applies atomically with one `nft -f`.
export function apply_text(text) {
	if (!mkdir_p(P.run))
		return fail('write_failed', 'Не удалось создать каталог ' + P.run);
	let tmp = P.run + '/zaprett.nft.new';
	if (!atomic_write(tmp, text))
		return fail('write_failed', 'Не удалось записать ' + tmp);
	let chk = check_file(tmp);
	if (chk.rc != 0) {
		fs.unlink(tmp);
		return fail('nft_check_failed', 'Правила nftables не прошли проверку (nft -c): ' + chk.output, { nft_output: chk.output });
	}
	let path = nft_path();
	if (!fs.rename(tmp, path)) {
		fs.unlink(tmp);
		return fail('write_failed', 'Не удалось записать ' + path);
	}
	let r = run([ 'nft', '-f', path ], { timeout: 30000, limit: 65536 });
	if (r.rc != 0) {
		fs.unlink(path);
		return fail('nft_apply_failed', 'nft не применил правила: ' + trim(r.stdout + '\n' + r.stderr));
	}
	return ok();
};

export function is_applied() {
	return run_quiet([ 'nft', 'list', 'table', 'inet', TABLE ], 10000) == 0;
};

export function remove() {
	fs.unlink(nft_path());
	if (!is_applied())
		return ok({ removed: false });
	let r = run([ 'nft', 'delete', 'table', 'inet', TABLE ], { timeout: 15000, limit: 16384 });
	if (r.rc != 0)
		return fail('nft_remove_failed', 'Не удалось удалить таблицу inet zaprett: ' + trim(r.stderr));
	return ok({ removed: true });
};

// Pure: does a listing of table inet zaprett contain the own flowtable?
export function has_flowtable(text) {
	return type(text) == 'string' && match(text, /\n[ \t]*flowtable ft \{/) != null;
};

export function list_table() {
	let r = run([ 'nft', 'list', 'table', 'inet', TABLE ], { timeout: 15000, limit: 262144 });
	return (r.rc == 0) ? r.stdout : null;
};
