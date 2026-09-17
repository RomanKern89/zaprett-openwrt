'use strict';

import * as fs from 'fs';
import * as T from 'ztest';
import { P, run } from 'zaprett.util';
import * as C from 'zaprett.config';
import * as N from 'zaprett.nft';

T.begin('nft');
T.selfcheck();
let W = T.sandbox('nft');

/* ---- WAN detection from netifd dump ---- */
let dump = {
	interface: [
		{ interface: 'lan', up: true, l3_device: 'br-lan', device: 'br-lan', route: [] },
		{ interface: 'loopback', up: true, l3_device: 'lo', route: [ { target: '0.0.0.0', mask: 0 } ] },
		{ interface: 'wan', up: true, l3_device: 'pppoe-wan', device: 'eth0.2', route: [ { target: '0.0.0.0', mask: 0, nexthop: '10.0.0.1' } ] },
		{ interface: 'wan6', up: true, l3_device: 'pppoe-wan', route: [ { target: '::', mask: 0 } ] },
		{ interface: 'vpn', up: true, l3_device: 'wg0', route: [ { target: '0.0.0.0', mask: 0, table: 100 } ] },
		{ interface: 'lte', up: false, l3_device: 'wwan0', route: [ { target: '0.0.0.0', mask: 0 } ] },
		{ interface: 'guest', up: true, l3_device: 'br-guest', route: [ { target: '192.168.2.0', mask: 24 } ] },
		{ interface: 'evil', up: true, l3_device: 'x"; flush ruleset', route: [ { target: '0.0.0.0', mask: 0 } ] }
	]
};
T.eq(N.wan_devices(dump, []), { v4: [ 'pppoe-wan' ], v6: [ 'pppoe-wan' ] }, 'auto WAN: default routes of main table, up only, no lo, no bad names');
T.eq(N.wan_devices(dump, [ 'lan', 'lte' ]), { v4: [ 'br-lan' ], v6: [ 'br-lan' ] }, 'explicit WAN names, down interface skipped');
T.eq(N.wan_devices(null, []), { v4: [], v6: [] }, 'no dump');

/* ---- rendering ---- */
let cfg = C.normalize(null, null, null);
let ports = { tcp: [ '80', '443' ], udp: [ '443', '50000-50100' ] };
let wan = { v4: [ 'eth1' ], v6: [] };
let text = N.render(cfg, ports, wan);
let lines = map(split(text, '\n'), (l) => trim(l));
T.eq(slice(lines, 0, 3), [ 'table inet zaprett', 'delete table inet zaprett', 'table inet zaprett {' ], 'atomic table replacement header');
T.has(lines, 'set wanif { type ifname; elements = { "eth1" } }', 'wanif set');
T.has(lines, 'meta mark and 0x40000000 == 0 jump postnat', 'postnat hook with desync mark filter');
T.has(lines, 'oifname @wanif tcp dport { 80, 443 } ct original packets 1-9 ip daddr != @nozaprett meta mark set meta mark or 0x20000000 ct mark set ct mark or 0x40000000 queue num 200 bypass', 'tcp postnat rule');
T.has(lines, 'oifname @wanif udp dport { 443, 50000-50100 } ct original packets 1-9 ip daddr != @nozaprett meta mark set meta mark or 0x20000000 ct mark set ct mark or 0x40000000 queue num 200 bypass', 'udp postnat rule');
T.has(lines, 'iifname @wanif tcp sport { 80, 443 } ct reply packets 1-3 ip saddr != @nozaprett ct mark set ct mark or 0x40000000 queue num 200 bypass', 'tcp prenat rule');
T.ok(index(text, 'udp sport') < 0, 'no udp prenat rule when udp_pkt_in=0');
T.ok(index(text, 'wanif6') < 0 && index(text, 'ip6 ') < 0, 'no ipv6 parts by default');
T.ok(index(text, 'clients') < 0, 'no client filter in mode all');
T.has(lines, 'tcp flags ! syn,rst,ack notrack', 'datanoack notrack');
T.has(lines, 'meta mark and 0x20000000 != 0 notrack', 'postnat notrack');

let c2 = C.normalize({ ipv6: '1', tcp_pkt_out: '1', udp_pkt_out: '0', udp_pkt_in: '2', qnum: '321', clients_mode: 'include',
	clients: [ '192.168.1.10', 'AA:BB:CC:DD:EE:FF', '192.168.1.77/24', 'bogus' ] }, null, null);
let t2 = N.render(c2, ports, { v4: [ 'eth1' ], v6: [ 'eth1', '6in4-wan' ] });
let l2 = map(split(t2, '\n'), (l) => trim(l));
T.has(l2, 'set wanif6 { type ifname; elements = { "eth1", "6in4-wan" } }', 'wanif6');
T.has(l2, 'oifname @wanif6 tcp dport { 80, 443 } ct original packets 1 ip6 daddr != @nozaprett6 ct mark and 0x08000000 != 0 meta mark set meta mark or 0x20000000 ct mark set ct mark or 0x40000000 queue num 321 bypass', 'ipv6 rule, single packet, include clients');
T.ok(index(t2, 'udp dport') < 0, 'udp_pkt_out=0 removes udp postnat rules');
T.has(l2, 'iifname @wanif udp sport { 443, 50000-50100 } ct reply packets 1-2 ip saddr != @nozaprett ct mark and 0x08000000 != 0 ct mark set ct mark or 0x40000000 queue num 321 bypass', 'udp prenat with udp_pkt_in');
T.has(l2, 'set clients4 { type ipv4_addr; flags interval; auto-merge; elements = { 192.168.1.10, 192.168.1.0/24 } }', 'clients4 normalized');
T.has(l2, 'set clientsmac { type ether_addr; elements = { aa:bb:cc:dd:ee:ff } }', 'clients mac');
T.has(l2, 'icmpv6 type time-exceeded ct mark and 0x40000000 != 0 drop', 'icmpv6 drop');
T.has(c2.bad_options, 'clients', 'bogus client reported');
let t3 = N.render(c2, ports, { v4: [], v6: [] }, { test_mode: true });
T.ok(index(t3, 'clients') < 0, 'test mode ignores client filter');
T.has(map(split(t3, '\n'), (l) => trim(l)), 'set wanif { type ifname; }', 'empty wanif set without elements');
let c4 = C.normalize({ clients_mode: 'exclude', clients: [] }, null, null);
T.ok(index(N.render(c4, ports, wan), 'ct mark and 0x08000000 == 0') > 0, 'exclude clients expression');
T.ok(index(N.render(cfg, { tcp: [], udp: [] }, wan), 'dport') < 0, 'no ports -> no queue rules');

/* ---- nft -c syntax check on this host ---- */
let tables_before = run([ 'nft', 'list', 'tables' ]).stdout;
function check(t, name) {
	let path = W + '/' + name + '.nft';
	fs.writefile(path, t);
	return N.check_file(path);
}
let have_queue = fs.stat('/sys/module/nft_queue')?.type == 'directory';
for (let variant in [ [ 'default', text ], [ 'ipv6-clients', t2 ], [ 'test-mode', t3 ] ]) {
	let t = variant[1];
	if (!have_queue)
		t = replace(t, / queue num [0-9]+ bypass/g, ' accept');
	let r = check(t, variant[0]);
	T.ok(r.rc == 0, sprintf('nft -c %s%s: %s', variant[0], have_queue ? '' : ' (queue->accept, no nft_queue module)', r.output));
}
if (!have_queue) {
	// the queue statement is still parsed: a syntax error there must be reported by the parser
	let r = check('table inet zt_parse {\n\tchain c {\n\t\ttype filter hook postrouting priority 101; policy accept;\n\t\ttcp dport 443 queue num 200 bypas\n\t}\n}\n', 'queue-typo');
	T.ok(r.rc != 0 && index(r.output, 'syntax error') >= 0, 'nft -c rejects a typo in the queue statement: ' + r.output);
	print('NOTE [nft] kernel module nft_queue is absent on this host: queue statements checked by parser only\n');
	let orig = check(text, 'default-with-queue');
	print(sprintf('NOTE [nft] unmodified script on this host: rc=%d %s\n', orig.rc, replace(orig.output, '\n', ' | ')));
}
// negative controls: broken scripts must fail nft -c
T.ok(check(replace(text, 'ct original packets 1-9', 'ct original packetz 1-9'), 'broken1').rc != 0, 'nft -c rejects a syntax error');
T.ok(check(replace(text, '@nozaprett', '@nosuchset'), 'broken2').rc != 0, 'nft -c rejects a reference to a missing set');
// nft -c never applies: the host's table list is unchanged
T.eq(run([ 'nft', 'list', 'tables' ]).stdout, tables_before, 'nft -c did not change the ruleset');

exit(T.finish());
