// zaprett: nftables table inet zaprett (ARCHITECTURE §8, ADR-003).
'use strict';

import * as fs from 'fs';
import * as ubus from 'ubus';
import { P, run, run_quiet, mkdir_p, atomic_write, fail, ok } from 'zaprett.util';
import * as V from 'zaprett.validate';

export const TABLE = 'zaprett';
export const CLIENT_MARK = 0x08000000;
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

export function query_wan(cfg) {
	let conn = ubus.connect();
	let dump = conn ? conn.call('network.interface', 'dump', {}) : null;
	if (conn)
		conn.disconnect();
	return wan_devices(dump, cfg.wan);
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

// Pure: renders the whole script. ports = { tcp: ["80","443"], udp: [...] }, wan = { v4: [...], v6: [...] }.
export function render(cfg, ports, wan, opts) {
	opts = opts ?? {};
	let dm = hexmark(cfg.desync_mark), pm = hexmark(cfg.postnat_mark), cm = hexmark(CLIENT_MARK);
	let q = cfg.qnum;
	let clients = (!opts.test_mode && cfg.clients_mode != 'all') ? cfg.clients_mode : null;
	let cl = '';
	if (clients == 'include')
		cl = sprintf(' ct mark and %s != 0', cm);
	else if (clients == 'exclude')
		cl = sprintf(' ct mark and %s == 0', cm);

	let L = [];
	push(L, 'table inet zaprett', 'delete table inet zaprett', 'table inet zaprett {');
	push(L, '\tset wanif { type ifname; ' + elements(wan.v4 ?? [], true) + '}');
	if (cfg.ipv6)
		push(L, '\tset wanif6 { type ifname; ' + elements(wan.v6 ?? [], true) + '}');
	push(L, '\tset nozaprett { type ipv4_addr; flags interval; auto-merge; ' + elements(NOZAPRETT4, false) + '}');
	if (cfg.ipv6)
		push(L, '\tset nozaprett6 { type ipv6_addr; flags interval; auto-merge; ' + elements(NOZAPRETT6, false) + '}');

	if (clients) {
		push(L, '\tset clients4 { type ipv4_addr; flags interval; auto-merge; ' + elements(cfg.clients4, false) + '}');
		push(L, '\tset clientsmac { type ether_addr; ' + elements(cfg.clients_mac, false) + '}');
		push(L, '\tchain clients_mark {');
		push(L, '\t\ttype filter hook prerouting priority -150; policy accept;');
		push(L, sprintf('\t\tiifname != @wanif ip saddr @clients4 ct mark set ct mark or %s', cm));
		push(L, sprintf('\t\tiifname != @wanif ether saddr @clientsmac ct mark set ct mark or %s', cm));
		push(L, '\t}');
	}

	push(L, '\tchain postnat_hook {');
	push(L, '\t\ttype filter hook postrouting priority 101; policy accept;');
	push(L, sprintf('\t\tmeta mark and %s == 0 jump postnat', dm));
	push(L, '\t}');

	push(L, '\tchain postnat {');
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
	push(L, '\t}');

	push(L, '\tchain prenat {');
	push(L, '\t\ttype filter hook prerouting priority -101; policy accept;');
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
	push(L, '\t}');

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

export function list_table() {
	let r = run([ 'nft', 'list', 'table', 'inet', TABLE ], { timeout: 15000, limit: 262144 });
	return (r.rc == 0) ? r.stdout : null;
};
