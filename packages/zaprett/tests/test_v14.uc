'use strict';

// Contract v1.4 §15.1–§15.5, §15.7: own flowtable, QUIC block, game filter, encrypted DNS, blocking diagnosis, the
// new warnings and page fields. The network, procd, the package manager and nft apply are replaced by hooks: nothing
// here may change the router (nft -c only checks).
import * as fs from 'fs';
import * as T from 'ztest';
import { P, set_paths, uniq_name, mkdir_p, run } from 'zaprett.util';
import * as V from 'zaprett.validate';
import * as C from 'zaprett.config';
import * as S from 'zaprett.store';
import * as G from 'zaprett.strategy';
import * as N from 'zaprett.nft';
import * as O from 'zaprett.offload';
import * as DNS from 'zaprett.dns';
import * as DG from 'zaprett.diagnose';
import * as CMD from 'zaprett.commands';
import * as TXT from 'zaprett.text';
import * as NET from 'zaprett.net';

let N_classify_error = (rc, err) => NET.classify(rc, err, 0, 0).error;

T.begin('v14');
T.selfcheck();
let W = T.sandbox('v14');
system([ 'cp', T.ROOT + '/files/usr/share/zaprett/presets.json', P.presets ]);

/* ---------- §15.2 options: validation ---------- */
let d = C.normalize({}, null, null);
T.eq([ d.quic_block, d.game_filter, d.game_ports_tcp, d.game_ports_udp, d.flow_offload ], [ false, false, '1024-65535', '1024-65535', 'own' ],
	'defaults of the v1.4 options');
let g = C.normalize({ game_ports_tcp: '27015,1024-2000,1500-3000', game_ports_udp: '', flow_offload: 'own', quic_block: '1', game_filter: '1' }, null, null);
T.eq([ g.game_ports_tcp, g.game_ports_udp, g.flow_offload, g.quic_block, g.game_filter, g.bad_options ],
	[ '1024-3000,27015', '', 'own', true, true, [] ], 'game ports merged, empty = none, mode own accepted');
for (let bad in [ '~1024-65535', '0-100', '70000', 'abc', '10-5', '1024-65536' ]) {
	let b = C.normalize({ game_ports_udp: bad }, null, null);
	T.ok(index(b.bad_options, 'game_ports_udp') >= 0 && b.game_ports_udp == '1024-65535',
		sprintf('negative control: game_ports_udp %J rejected -> default', bad));
}
T.eq(C.normalize({ flow_offload: 'turbo' }, null, null).bad_options, [ 'flow_offload' ], 'negative control: unknown flow_offload mode');
T.eq([ CMD.default_strategy({ defaults: { strategy: 'strategy-general', strategy_nfqws2: 'z2-general' } }, 'nfqws2'),
	CMD.default_strategy({ defaults: { strategy: 'strategy-general' } }, 'nfqws'), CMD.default_strategy({ defaults: {} }, 'nfqws2'),
	CMD.default_strategy({ defaults: { strategy_nfqws2: '../x' } }, 'nfqws2') ], [ 'z2-general', 'strategy-general', null, null ], 'default_strategy');
T.eq([ V.game_ports('443'), V.game_ports('1-65535'), V.game_ports('~5'), V.game_ports('0') ], [ '443', '1-65535', null, null ], 'V.game_ports');
// the shipped UCI file has the new options with the defaults of config.uc
let pkg = fs.readfile(T.ROOT + '/files/etc/config/zaprett');
for (let o in [ "option quic_block '0'", "option game_filter '0'", "option game_ports_tcp '1024-65535'",
	"option game_ports_udp '1024-65535'", "option flow_offload 'own'" ])
	T.ok(index(pkg, o) >= 0, 'package UCI file: ' + o);

/* ---------- §15.2 game filter in the generator ---------- */
let idx = S.scan();
let gcfg = (main) => {
	let m = { lists: [ 'list-youtube' ], strategy: 'strategy-general', exclude_lists: [], exclude_ipsets: [ 'user-ipset-exclude' ] };
	for (let k, v in main)
		m[k] = v;
	return C.normalize(m, null, null);
};
let prof_of = (args, flt) => filter(G.split_profiles(args), (p) => index(p, flt) >= 0);
let b0 = G.build(gcfg({}), { index: idx });
let b1 = G.build(gcfg({ game_filter: '1', ipsets: [ 'ipset-roblox' ] }), { index: idx });
T.ok(b0.ok && b1.ok, 'builds with and without the game filter: ' + (b1.message ?? ''));
let gt = prof_of(b1.args, '--filter-tcp=1024-65535'), gu = prof_of(b1.args, '--filter-udp=1024-65535');
T.eq([ length(gt), length(gu) ], [ 1, 1 ], 'one TCP and one UDP game profile');
let bin4pda = idx.items.bin[G.GAME_BIN_TCP]?.file, binquic = idx.items.bin[G.GAME_BIN_UDP]?.file;
let roblox = idx.items.ipset['ipset-roblox'].file;
T.eq(gt[0], [ '--filter-tcp=1024-65535', '--ipset=' + roblox, '--ipset=' + P.guard_ipset,
	'--ipset-exclude=' + idx.items.ipset_exclude['user-ipset-exclude'].file,
	'--dpi-desync=multisplit', '--dpi-desync-any-protocol=1', '--dpi-desync-cutoff=n3', '--dpi-desync-split-seqovl=568',
	'--dpi-desync-split-pos=1', '--dpi-desync-split-seqovl-pattern=' + bin4pda ], 'TCP game profile (Flowseal general.bat options)');
T.eq(slice(gu[0], 4), [ '--dpi-desync=fake', '--dpi-desync-repeats=12', '--dpi-desync-any-protocol=1',
	'--dpi-desync-fake-unknown-udp=' + binquic, '--dpi-desync-cutoff=n2' ], 'UDP game profile (Flowseal general.bat options)');
let last2 = slice(G.split_profiles(b1.args), -2);
T.eq([ last2[0][0], last2[1][0] ], [ '--filter-tcp=1024-65535', '--filter-udp=1024-65535' ], 'game profiles are the last two');
let b0i = G.build(gcfg({ ipsets: [ 'ipset-roblox' ] }), { index: idx });
T.eq(slice(G.split_profiles(b1.args), 0, length(G.split_profiles(b0i.args))), G.split_profiles(b0i.args),
	'the strategy profiles themselves are unchanged');
T.ok(index(b1.ports.tcp, '1024-65535') >= 0 && index(b1.ports.udp, '1024-65535') >= 0, 'game ports reach nft: ' + sprintf('%J', b1.ports));
T.ok(index(b1.warnings, 'wide_port_range') >= 0, 'wide game ports are reported as wide_port_range');
let b2 = G.build(gcfg({ game_filter: '1', ipsets: [] }), { index: idx });
T.ok(b2.ok && index(b2.warnings, 'game_filter_no_ipsets') >= 0 && !length(prof_of(b2.args, '--filter-tcp=1024-65535')),
	'no active include ipset: no game profile, warning game_filter_no_ipsets');
T.ok(index(b1.warnings, 'game_filter_no_ipsets') < 0 && index(b0.warnings, 'game_filter_no_ipsets') < 0, 'negative control: no warning with ipsets / filter off');
// an empty include ipset matches everything in nfqws: it must not count as an active ipset
T.write(P.user + '/ipset-include.txt', '');
let b3 = G.build(gcfg({ game_filter: '1', ipsets: [ 'user-ipset' ] }), { index: idx });
T.ok(b3.ok && index(b3.warnings, 'game_filter_no_ipsets') >= 0, 'empty user ipset is not an active ipset for the game filter');
let b4 = G.build(gcfg({ game_filter: '1', ipsets: [ 'ipset-roblox' ], game_ports_udp: '' }), { index: idx });
T.eq([ length(prof_of(b4.args, '--filter-tcp=1024-65535')), length(filter(G.split_profiles(b4.args), (p) => p[0] == '--filter-udp=1024-65535')) ],
	[ 1, 0 ], 'game_ports_udp empty: only the TCP profile');
let b5 = G.build(gcfg({ game_filter: '1', ipsets: [ 'ipset-roblox' ] }), { index: idx, engine: 'nfqws2', text: '--filter-tcp=443 ${hostlists} --lua-desync=fake:blob=fake_default_tls',
	item: { id: 'user-z2', source: 'user', dependencies: [] } });
let g2 = slice(G.split_profiles(b5.args), -2);
T.eq([ g2[0][4], g2[0][5], g2[0][6], g2[0][7], g2[1][4], g2[1][5], g2[1][7] ], [ '--blob=zaprett_game_tcp:@' + bin4pda, '--out-range=<n3', '--payload=all',
	'--lua-desync=multisplit:pos=1:seqovl=568:seqovl_pattern=zaprett_game_tcp', '--blob=zaprett_game_udp:@' + binquic, '--out-range=<n2',
	'--lua-desync=fake:blob=zaprett_game_udp:repeats=12' ], 'nfqws2: the same attack in nfqws2 syntax');
// dry-run of every variant with the real engines
if (T.NFQWS) {
	for (let v in [ [ 'game tcp+udp', b1 ], [ 'game tcp only', b4 ], [ 'no game profile', b2 ] ]) {
		let dr = G.dry_run('nfqws', v[1].args);
		T.ok(dr.rc == 0, sprintf('nfqws --dry-run %s: rc=%d %s', v[0], dr.rc, substr(dr.output, 0, 300)));
	}
	let broken = map(b1.args, (a) => (a == '--dpi-desync-cutoff=n2') ? '--dpi-desync-cutoff=q2' : a);
	T.ok(G.dry_run('nfqws', broken).rc != 0, 'negative control: nfqws rejects a broken game option');
}
else
	print('NOTE [v14] ZTEST_NFQWS not set: nfqws dry-run of the game filter skipped\n');
// the package zaprett-nfqws2 installs the Lua libraries gzipped (zapret-lib.lua.gz); nfqws2 finds them by the .lua name
let have_lua = (fs.stat('/usr/share/zaprett/lua/zapret-lib.lua.gz') ?? fs.stat('/usr/share/zaprett/lua/zapret-lib.lua'))?.type == 'file';
if (fs.stat('/usr/libexec/zaprett/nfqws2')?.type == 'file' && have_lua) {
	system([ 'cp', '/usr/libexec/zaprett/nfqws2', P.libexec + '/nfqws2' ]);
	system([ 'sh', '-c', 'cp /usr/share/zaprett/lua/* "$0"/', P.share + '/lua' ]);
	print('NOTE [v14] nfqws2 dry-run of the game filter runs with ' + join(' ', fs.lsdir(P.share + '/lua') ?? []) + '\n');
	let dr = G.dry_run('nfqws2', b5.args);
	T.ok(dr.rc == 0, sprintf('nfqws2 --intercept=0 game tcp+udp: rc=%d %s', dr.rc, substr(dr.output, 0, 300)));
	let broken = map(b5.args, (a) => (a == '--out-range=<n2') ? '--out-range=<q2' : a);
	T.ok(G.dry_run('nfqws2', broken).rc != 0, 'negative control: nfqws2 rejects a broken game option');
	// zapret-auto.lua is loaded by default: the circular orchestrator exists (engine2, strategies z2-circular*)
	let circ = G.build(gcfg({}), { index: idx, engine: 'nfqws2', text: '--filter-tcp=443 ${hostlists} --lua-desync=circular',
		item: { id: 'user-z2c', source: 'user', dependencies: [] } });
	T.ok(circ.ok && G.dry_run('nfqws2', circ.args).rc == 0, 'nfqws2 accepts circular with the default lua-init');
	let circ_noauto = filter(circ.args, (a) => index(a, 'zapret-auto.lua') < 0);
	T.ok(G.dry_run('nfqws2', circ_noauto).rc != 0, 'negative control: without zapret-auto.lua circular does not exist');

	// `engine nfqws2` with no nfqws2 strategy takes presets.defaults.strategy_nfqws2 (when installed)
	T.write(P.user + '/strategies/nfqws2/user-z2d.txt', '--filter-tcp=443 ${hostlists} --lua-desync=fake:blob=fake_default_tls\n');
	let pres_saved = fs.readfile(P.presets);
	let pj = json(pres_saved);
	pj.defaults.strategy_nfqws2 = 'user-z2d';
	fs.writefile(P.presets, sprintf('%J', pj));
	C.set({ engine: 'nfqws', strategy_nfqws2: null, lists: [ 'list-youtube' ], ipsets: [] });
	let se = CMD.set_engine('nfqws2');
	T.eq([ se.ok, se.strategy, C.load().engine, C.load().strategy_nfqws2 ], [ true, 'user-z2d', 'nfqws2', 'user-z2d' ],
		'engine nfqws2 without a strategy: preset default chosen: ' + (se.message ?? ''));
	C.set({ engine: 'nfqws', strategy_nfqws2: 'user-z2c-none' });
	se = CMD.set_engine('nfqws2');
	T.ok(!se.ok && C.load().strategy_nfqws2 == 'user-z2c-none', 'negative control: a chosen (even missing) strategy is not replaced');
	pj.defaults.strategy_nfqws2 = 'z2-not-installed';
	fs.writefile(P.presets, sprintf('%J', pj));
	C.set({ engine: 'nfqws', strategy_nfqws2: null });
	T.eq(CMD.set_engine('nfqws2').error, 'no_strategy', 'negative control: a preset default that is not installed is not written');
	fs.writefile(P.presets, pres_saved);
	C.set({ engine: 'nfqws', strategy_nfqws2: null });
}
else
	print('NOTE [v14] nfqws2 with lua files not installed on this host: nfqws2 dry-run skipped\n');

/* ---------- §15.1 own flowtable: devices, plan, rendering ---------- */
let fw4st = { zones: [ { name: 'lan', related_physdevs: [ 'br-lan' ] }, { name: 'wan', related_physdevs: [ 'eth1', 'eth1', 'ghost0' ] },
	{ name: 'guest', related_physdevs: null } ] };
let exists = (dev) => dev != 'ghost0';
T.eq(O.zone_devices(fw4st, exists), [ 'br-lan', 'eth1' ], 'flowtable devices: related_physdevs of zones, existing only, unique');
T.eq(O.zone_devices(null, exists), [], 'no fw4 state -> no devices');
let dt = { 'br-lan': 'bridge', 'br-lan.10': 'vlan' };
let lw = { 'br-lan': [ 'lan1', 'lan2', 'br-lan.10' ], 'br-lan.10': [ 'lan3' ] };
T.eq(O.lower_devices([ 'br-lan', 'eth1' ], (x) => dt[x], (x) => lw[x] ?? []), [ 'eth1', 'lan1', 'lan2', 'lan3' ], 'hw devices: bridges/VLANs replaced by lower devices');
let st_of = (fo, hw, saved) => ({ fw4: { flow_offloading: fo, flow_offloading_hw: hw }, saved: saved,
	original: O.original(saved, { flow_offloading: fo ? '1' : null, flow_offloading_hw: hw ? '1' : null }) });
let ph = (st) => ({ state: st, fw4_state: fw4st, exists: exists, devtype: (x) => dt[x], lowers: (x) => lw[x] ?? [] });
let own = C.normalize({ flow_offload: 'own' }, null, null);
T.eq(O.flowtable_plan(own, ph(st_of(true, false, null))), { devices: [ 'br-lan', 'eth1' ], hw_devices: null }, 'plan: fw4 offloading on -> software flowtable');
T.eq(O.flowtable_plan(own, ph(st_of(false, false, { flow_offloading: '1', flow_offloading_hw: '1' }))),
	{ devices: [ 'br-lan', 'eth1' ], hw_devices: [ 'eth1', 'lan1', 'lan2', 'lan3' ] }, 'plan: saved original (hw on) wins over the switched-off fw4');
T.eq(O.flowtable_plan(own, ph(st_of(false, false, null))), null, 'plan: fw4 offloading was off -> zaprett does not add acceleration');
T.eq(O.flowtable_plan(C.normalize({ flow_offload: 'auto' }, null, null), ph(st_of(true, false, null))), null, 'negative control: mode auto has no flowtable');
T.eq(O.flowtable_plan(own, { state: st_of(true, false, null), fw4_state: { zones: [] }, exists: exists }), { devices: [], hw_devices: null },
	'plan without devices is still wanted (-> flowtable_failed)');

let cfg = C.normalize({ tcp_pkt_out: '9', udp_pkt_out: '4' }, null, null);
T.eq([ N.offload_after(cfg), N.offload_after(C.normalize({ tcp_pkt_out: '2', udp_pkt_out: '6' }, null, null)) ], [ 9, 6 ], 'offload after max(tcp_pkt_out, udp_pkt_out)');
let ports = { tcp: [ '80', '443' ], udp: [ '443' ] }, wan = { v4: [ 'eth1' ], v6: [] };
let tft = N.render(cfg, ports, wan, { flowtable: { devices: [ 'br-lan', 'eth1' ], hw: false } });
let lft = map(split(tft, '\n'), (l) => trim(l));
T.has(lft, 'flowtable ft {', 'flowtable present');
T.has(lft, 'devices = { "br-lan", "eth1" };', 'flowtable devices');
T.has(lft, 'type filter hook forward priority filter + 1; policy accept;', 'forward_offload after fw4 (filter+1)');
T.has(lft, 'meta l4proto { tcp, udp } ct original packets > 9 flow add @ft', 'flow add only after the engine has seen the first packets');
T.ok(index(tft, 'flags offload') < 0, 'software flowtable without flags offload');
T.ok(index(N.render(cfg, ports, wan, { flowtable: { devices: [ 'eth1' ], hw: true } }), 'flags offload;') > 0, 'hardware flowtable has flags offload');
T.ok(index(N.render(cfg, ports, wan, {}), 'flowtable') < 0 && index(N.render(cfg, ports, wan, { flowtable: { devices: [], hw: false } }), 'flowtable') < 0,
	'negative control: no flowtable without plan or devices');
T.ok(N.has_flowtable(tft) && !N.has_flowtable(N.render(cfg, ports, wan, {})) && !N.has_flowtable(null), 'has_flowtable');

// candidates: hw, sw, none — in that order
let cands = CMD.fw_candidates(cfg, ports, wan, false, { devices: [ 'br-lan', 'eth1' ], hw_devices: [ 'eth1', 'lan1' ] });
T.eq(map(cands, (c) => c.flowtable), [ 'hw', 'sw', null ], 'fw candidates: hardware, software, without flowtable');
T.eq(map(CMD.fw_candidates(cfg, ports, wan, false, null), (c) => c.flowtable), [ null ], 'no plan: one candidate');
T.eq(map(CMD.fw_candidates(cfg, ports, wan, false, { devices: [], hw_devices: null }), (c) => c.flowtable), [ null ], 'plan without devices: one candidate');
let applied_log = [];
let ahooks = (cur, present, accept) => ({
	applied_text: () => cur, is_applied: () => present,
	apply_text: (t) => { push(applied_log, t); return accept(t) ? { ok: true } : { ok: false, error: 'nft_check_failed', message: 'x' }; }
});
applied_log = [];
let a = CMD.apply_candidates(cands, ahooks(null, false, (t) => index(t, 'flags offload') < 0 && index(t, 'flowtable') >= 0));
T.eq([ a.ok, a.changed, a.flowtable, length(applied_log) ], [ true, true, 'sw', 2 ], 'hardware rejected -> software flowtable applied');
applied_log = [];
a = CMD.apply_candidates(cands, ahooks(null, false, (t) => index(t, 'flowtable') < 0));
T.eq([ a.ok, a.flowtable, length(applied_log) ], [ true, null, 3 ], 'every flowtable rejected -> table without it');
applied_log = [];
a = CMD.apply_candidates(cands, ahooks(cands[1].text, true, (t) => false));
T.eq([ a.ok, a.changed, a.flowtable ], [ true, false, 'sw' ], 'same ruleset already in the kernel -> unchanged');
applied_log = [];
a = CMD.apply_candidates(cands, ahooks(cands[1].text, false, (t) => false));
T.eq([ a.ok, a.error, length(applied_log) ], [ false, 'nft_check_failed', 3 ], 'negative control: text matches but table gone -> applied again, errors surface');

/* ---------- §15.2 QUIC block ---------- */
let q0 = N.render(cfg, ports, wan, {});
let q1 = N.render(C.normalize({ quic_block: '1' }, null, null), ports, wan, {});
let lq = map(split(q1, '\n'), (l) => trim(l));
T.has(lq, 'type filter hook forward priority filter - 1; policy accept;', 'QUIC block in hook forward');
T.has(lq, 'iifname != @wanif oifname @wanif udp dport 443 counter drop', 'QUIC of LAN clients dropped');
T.ok(index(q0, 'forward_quic') < 0, 'negative control: no QUIC block by default');
let qi = N.render(C.normalize({ quic_block: '1', clients_mode: 'include', clients: [ '10.0.0.5' ] }, null, null), ports, wan, { test_mode: true });
T.ok(index(qi, 'udp dport 443 ct mark and 0x08000000 != 0 counter drop') > 0 && index(qi, 'chain clients_mark') > 0,
	'QUIC block keeps the client filter (and its marking chain) even in test mode');
let qe = N.render(C.normalize({ quic_block: '1', clients_mode: 'exclude', clients: [ '10.0.0.5' ], ipv6: '1' }, null, null), ports, { v4: [ 'eth1' ], v6: [ 'eth1' ] }, {});
T.ok(index(qe, 'udp dport 443 ct mark and 0x08000000 == 0 counter drop') > 0 && index(qe, 'iifname != @wanif6 oifname @wanif6 udp dport 443') > 0,
	'QUIC block: exclude filter and IPv6');
// the kernel parser accepts every variant (nft -c only)
let have_queue = fs.stat('/sys/module/nft_queue')?.type == 'directory';
let ncheck = (t, name) => {
	if (!have_queue)
		t = replace(t, / queue num [0-9]+ bypass/g, ' accept');
	let path = W + '/' + name + '.nft';
	fs.writefile(path, t);
	return N.check_file(path);
};
let devs = filter([ 'br-lan', 'eth1', 'eth0' ], (x) => fs.stat('/sys/class/net/' + x) != null);
if (length(devs)) {
	let tables_before = run([ 'nft', 'list', 'tables' ]).stdout;
	for (let v in [ [ 'flowtable-sw', N.render(cfg, ports, wan, { flowtable: { devices: devs, hw: false } }) ],
		[ 'flowtable-hw', N.render(cfg, ports, wan, { flowtable: { devices: devs, hw: true } }) ], [ 'quic', q1 ], [ 'quic-include', qi ], [ 'quic-exclude-v6', qe ] ]) {
		let r = ncheck(v[1], v[0]);
		T.ok(r.rc == 0, sprintf('nft -c %s: %s', v[0], r.output));
	}
	let r = ncheck(N.render(cfg, ports, wan, { flowtable: { devices: [ 'nosuchdev9' ], hw: false } }), 'flowtable-nodev');
	T.ok(r.rc != 0, 'negative control: nft -c rejects a flowtable on a missing device');
	T.eq(run([ 'nft', 'list', 'tables' ]).stdout, tables_before, 'nft -c did not change the ruleset');
}
else
	print('NOTE [v14] no br-lan/eth1/eth0 on this host: nft -c of the flowtable skipped\n');

/* ---------- §15.3 encrypted DNS ---------- */
T.eq(DNS.summarize([ { id: 'stubby', installed: true, running: true }, { id: 'https-dns-proxy', installed: true, running: true } ]),
	{ encrypted: true, provider: 'https-dns-proxy' }, 'provider order: https-dns-proxy first');
T.eq(DNS.summarize([ { id: 'https-dns-proxy', installed: true, running: false }, { id: 'dnscrypt-proxy', installed: true, running: true } ]),
	{ encrypted: true, provider: 'dnscrypt-proxy' }, 'installed but stopped does not count');
T.eq(DNS.summarize([ { id: 'https-dns-proxy', installed: false, running: true } ]), { encrypted: false, provider: null }, 'negative control: not installed');
T.eq(DNS.summarize(null), { encrypted: false, provider: null }, 'nothing');
T.eq([ DNS.service_running({ 'https-dns-proxy': { instances: { instance1: { running: false }, instance2: { running: true } } } }, 'https-dns-proxy'),
	DNS.service_running({ 'https-dns-proxy': { instances: { instance1: { running: false } } } }, 'https-dns-proxy'),
	DNS.service_running({}, 'https-dns-proxy') ], [ true, false, false ], 'service_running: any running instance');
let sp = DNS.setup_plan(true, true, true);
T.eq([ sp.manager, sp.update, sp.install ], [ 'apk', [ P.apk, 'update' ], [ P.apk, 'add', 'https-dns-proxy', 'luci-app-https-dns-proxy' ] ], 'apk plan with LuCI');
sp = DNS.setup_plan(false, true, false);
T.eq([ sp.manager, sp.install ], [ 'opkg', [ P.opkg, 'install', 'https-dns-proxy' ] ], 'opkg plan without LuCI');
T.eq(DNS.setup_plan(false, false, true), null, 'no package manager');

let runs = [], dns_state = { encrypted: false, provider: null };
let shooks = (fail_at) => ({
	status: () => dns_state,
	run: (argv, o) => {
		push(runs, join(' ', argv));
		if (argv[1] == 'restart' && fail_at != 'start')
			dns_state = { encrypted: true, provider: 'https-dns-proxy' };
		return { rc: (argv[1] == fail_at) ? 1 : 0, stdout: 'out', stderr: (argv[1] == fail_at) ? 'ERROR: unable to select packages' : '' };
	},
	has_apk: true, has_opkg: false, luci: false, wait_ms: 0
});
runs = [];
let r = DNS.setup(null, shooks(null));
T.eq([ r.ok, r.changed, r.dns, runs ], [ true, true, { encrypted: true, provider: 'https-dns-proxy' },
	[ P.apk + ' update', P.apk + ' add https-dns-proxy', P.initd + '/https-dns-proxy enable', P.initd + '/https-dns-proxy restart' ] ],
	'dns setup: update, install, enable, start');
runs = [];
r = DNS.setup(null, shooks(null));
T.eq([ r.ok, r.changed, runs ], [ true, false, [] ], 'dns setup again: changed:false, nothing run');
dns_state = { encrypted: false, provider: null };
r = DNS.setup(null, shooks('add'));
T.eq([ r.ok, r.error, index(r.message, 'unable to select') >= 0 ], [ false, 'package_install_failed', true ], 'install failure reported with the manager output');
dns_state = { encrypted: false, provider: null };
r = DNS.setup(null, shooks('update'));
T.eq([ r.ok, r.error ], [ false, 'package_update_failed' ], 'update failure');
dns_state = { encrypted: false, provider: null };
r = DNS.setup(null, shooks('start'));
T.eq([ r.ok, r.error ], [ false, 'dns_not_running' ], 'installed but not running -> error, not success');

// dns_plain
let psets = json(fs.readfile(P.presets));
T.eq(map(filter(psets.services, (s) => s.needs_dns), (s) => s.id), [ 'rutracker', 'rkn_full', 'whatsapp' ], 'services with needs_dns');
let rt = C.normalize({ lists: [ 'zaprett-rutracker' ] }, null, null), yt = C.normalize({ lists: [ 'zaprett-youtube' ] }, null, null);
T.eq([ CMD.dns_plain(rt, psets, { encrypted: false }), CMD.dns_plain(rt, psets, { encrypted: true }), CMD.dns_plain(yt, psets, { encrypted: false }) ],
	[ true, false, false ], 'dns_plain: needs_dns service on + plain DNS; negative controls');

/* ---------- §15.4 diagnosis: parsers and verdicts ---------- */
let ns = 'Server:\t\t127.0.0.1\nAddress:\t127.0.0.1:53\n\nNon-authoritative answer:\nName:\trutracker.org\nAddress: 104.21.32.39\nName:\trutracker.org\nAddress: 172.67.182.196\n\n';
T.eq(DG.parse_nslookup(ns), [ '104.21.32.39', '172.67.182.196' ], 'nslookup answer (server address skipped)');
T.eq(DG.parse_nslookup('Server:\t\t127.0.0.1\nAddress:\t127.0.0.1:53\n\n** server can\'t find x.invalid: NXDOMAIN\n'), [], 'NXDOMAIN -> no addresses');
T.eq(DG.parse_nslookup('Name: a\nAddress 1: 1.2.3.4\nAddress: fe80::1\n'), [ '1.2.3.4' ], 'numbered address, IPv6 skipped');
T.eq(DG.parse_doh('{"Status":0,"Answer":[{"type":5,"data":"x.cdn."},{"type":1,"data":"142.251.153.4"},{"type":1,"data":"142.251.153.4"}]}'), [ '142.251.153.4' ], 'DoH JSON: A records');
T.eq([ DG.parse_doh('{"Status":3}'), DG.parse_doh('<html>'), DG.parse_doh('{"x":1}') ], [ [], null, null ], 'DoH: NXDOMAIN empty, garbage null');
T.eq(map([ '0.0.0.0', '127.0.0.2', '10.10.10.10', '192.168.1.1', '172.20.0.1', '100.64.0.1', '169.254.1.1', '224.0.0.1', '8.8.8.8', '172.32.0.1', '100.128.0.1' ],
	(x) => DG.is_stub(x)), [ true, true, true, true, true, true, true, true, false, false, false ], 'stub addresses');
T.eq([ DG.tcp_connected(0, ''), DG.tcp_connected(4, 'Connection error: Connection failed\n'), DG.tcp_connected(4, 'Failed to send request: Operation not permitted\n'),
	DG.tcp_connected(4, 'SSL error: x\nConnection error: Connection failed\n'), DG.tcp_connected(5, 'Connection error: Invalid SSL certificate\n'),
	DG.tcp_connected(8, 'HTTP error 400\n'), DG.tcp_connected(4, 'Connection reset prematurely\n'), DG.tcp_connected(-9, '') ],
	[ true, false, false, true, true, true, true, null ], 'tcp_connected by uclient-fetch texts');
// 24.10 (mbedtls) prints a refused TCP connection as an SSL error of the first send (seen live with a TCP reset)
let refused2410 = 'Connecting to 162.159.130.234:443\nSSL error: NET - Sending information through the socket failed\nConnection error: Connection failed\n';
T.eq([ DG.tcp_connected(4, refused2410), N_classify_error(4, refused2410) ], [ false, 'connect_failed' ], '24.10 refused TCP is no TLS error');
T.eq(N_classify_error(4, 'SSL error: SSL - A fatal alert message was received from our peer\nConnection error: Connection failed\n'), 'tls_error',
	'negative control: a real TLS failure stays tls_error');
T.eq([ DG.url_host('https://www.youtube.com/'), DG.url_host('http://a.b:8080/x?y'), DG.url_host('ftp://x/') ],
	[ { scheme: 'https', host: 'www.youtube.com', port: 443 }, { scheme: 'http', host: 'a.b', port: 8080 }, null ], 'url_host');

let F = (rc, err, bytes, min) => ({ rc: rc, err: err, bytes: bytes, min_bytes: min ?? 0, head: '' });
let J = (sys, doh, f, tcp) => DG.judge({ sys: sys, doh: doh, fetch: f, tcp: tcp });
let OK = F(0, 'Download completed (900000 bytes)\n', 900000, 131072);
let A = [ '142.251.153.4' ], B = [ '5.6.7.8' ];
T.eq(J(A, A, OK, null).verdict, 'ok', 'ok');
T.eq(J(B, A, OK, null).verdict, 'ok', 'CDN answer differs but the site opens -> ok, not a false dns_spoof');
T.eq([ J([ '10.10.10.10' ], A, OK, null).verdict, J([ '10.10.10.10' ], A, OK, null).spoofed ], [ 'dns_spoof', true ], 'stub address -> dns_spoof even if something answered');
T.eq(J([], A, F(4, 'Failed to send request: x\n', 0), null).verdict, 'dns_spoof', 'no system answer, DoH knows the domain -> dns_spoof');
T.eq(J(B, A, F(4, 'Connection error: Connection failed\n', 0), null).verdict, 'dns_spoof', 'foreign address and the site fails -> dns_spoof');
T.eq(J([], null, F(4, 'Failed to send request: x\n', 0), null).verdict, 'unknown', 'no DNS at all -> unknown');
let ib = J(A, A, F(4, 'Connection error: Connection failed\n', 0), null);
T.eq([ ib.verdict, ib.need_tcp ], [ 'unknown', true ], 'connect failed: TCP check requested');
T.eq(J(A, A, F(4, 'Connection error: Connection failed\n', 0), false).verdict, 'ip_block', 'TCP to the real address fails -> ip_block');
T.eq([ J(A, A, F(4, refused2410, 0), null).need_tcp, J(A, A, F(4, refused2410, 0), false).verdict ], [ true, 'ip_block' ],
	'24.10: refused TCP (SSL send error) -> TCP check -> ip_block');
T.eq(J(A, A, F(4, 'Connection reset prematurely\n', 0), true).verdict, 'tls_block', 'TCP works, handshake reset -> tls_block');
T.eq(J(A, A, F(4, 'SSL error: -0x7280\nConnection error: Connection failed\n', 0), null).verdict, 'tls_block', 'SSL error -> tls_block without TCP check');
T.eq(J(A, A, F(4, 'Connection error: Connection timed out\n', 16384, 131072), null).verdict, 'throttle', 'stalls at 16 KB -> throttle');
T.eq(J(A, A, F(4, 'Connection error: Connection timed out\n', 5433, 131072), null).verdict, 'throttle', 'body froze at 5 KB (TLS + headers came first) -> throttle');
T.eq(J(A, A, F(4, 'Connection reset prematurely\n', 9000, 131072), null).verdict, 'throttle', 'broke off after 9 KB -> throttle');
T.eq(J(A, A, F(0, 'Download completed (20000 bytes)\n', 20000, 131072), true).verdict, 'unknown', 'negative control: a complete short download is not a freeze');
T.eq(J(A, A, F(4, 'Connection error: Connection timed out\n', 60000, 131072), true).verdict, 'tls_block', 'negative control: 60 KB is not the 16 KB freeze');
T.eq(J(A, A, F(5, 'Connection error: Invalid SSL certificate\n', 0), null).verdict, 'http_block', 'forged certificate -> http_block');
T.eq(J(A, A, F(8, 'HTTP error 451\n', 0, 1000), null).verdict, 'http_block', 'HTTP 451 -> http_block');
let stubpage = F(0, '', 900, 32768);
stubpage.head = '<html>Доступ к ресурсу ограничен ... eais.rkn.gov.ru</html>';
T.eq(J(A, A, stubpage, null).verdict, 'http_block', 'stub page text -> http_block');
T.eq(J(A, A, F(0, '', 900, 32768), true).verdict, 'unknown', 'negative control: short page without stub text is not http_block');
T.eq(DG.summarize([ { verdict: 'ok' }, { verdict: 'ip_block' }, { verdict: 'dns_spoof' }, { verdict: 'dns_spoof' } ]),
	{ verdict: 'dns_spoof', counts: { ok: 1, ip_block: 1, dns_spoof: 2 } }, 'summary: most frequent non-ok');
T.eq(DG.summarize([ { verdict: 'ok' }, { verdict: 'ok' } ]).verdict, 'ok', 'summary: all ok');
T.eq(DG.summarize([ { verdict: 'throttle' }, { verdict: 'ip_block' } ]).verdict, 'ip_block', 'summary tie: order of the verdict list');

/* ---------- §15.4 diagnosis job end to end with hooks ---------- */
let probe_calls = [];
function fake_probe(tasks, opts) {
	push(probe_calls, map(tasks, (t) => t.url));
	let dir = uniq_name(P.tmp + '/probe');
	mkdir_p(dir);
	let res = {};
	for (let t in tasks) {
		if (index(t.url, 'www.youtube.com') >= 0)
			res[t.key] = { rc: 0, ms: 5, bytes: 900000, body: null, err: 'Download completed (900000 bytes)\n' };
		else if (index(t.url, 'discord.com') >= 0)
			res[t.key] = { rc: 4, ms: 5, bytes: 0, body: null, err: 'Connection error: Connection failed\n' };
		else if (index(t.url, 'https://9.9.9.1:443/') == 0)
			res[t.key] = { rc: 4, ms: 5, bytes: 0, body: null, err: 'Connection error: Connection failed\n' };
		else
			res[t.key] = { rc: 4, ms: 5, bytes: 16000, body: null, err: 'Connection error: Connection timed out\n' };
	}
	return { dir: dir, results: res, rc: 0 };
}
let dhooks = {
	probe: fake_probe,
	resolve: (h) => (h == 'discord.com') ? [ '9.9.9.1' ] : ((h == 'rutracker.org') ? [ '10.10.10.10' ] : [ '1.1.1.9' ]),
	doh: (h) => (h == 'discord.com') ? [ '9.9.9.1' ] : [ '1.1.1.9' ],
	instance_state: () => ({ running: true, pid: 7 })
};
let dcfg = C.normalize({ lists: [ 'zaprett-youtube', 'zaprett-discord', 'zaprett-rutracker' ] }, null, null);
r = DG.run_diagnose(dcfg, null, null, dhooks);
T.ok(r.ok, 'diagnose job: ' + (r.message ?? ''));
let ds = CMD.diagnose_status().diagnose;
let by = {};
for (let t in ds.targets)
	by[t.url] = t;
T.eq([ by['https://www.youtube.com/'].verdict, by['https://discord.com/'].verdict, by['https://rutracker.org/forum/index.php'].verdict,
	by['https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg'].verdict ], [ 'ok', 'ip_block', 'dns_spoof', 'throttle' ], 'verdict per target');
T.eq(by['https://rutracker.org/forum/index.php'].dns, { system: [ '10.10.10.10' ], doh: [ '1.1.1.9' ], spoofed: true }, 'dns block of a target');
T.eq([ ds.engine_running, type(ds.started), type(ds.finished), type(ds.summary.counts) ], [ true, 'int', 'int', 'object' ], 'result fields');
T.ok(length(filter(keys(ds.targets[0]), (k) => index([ 'url', 'host', 'verdict', 'dns', 'detail' ], k) >= 0)) == 5, 'contract fields of a target');
T.ok(length(probe_calls) == 2 && length(filter(probe_calls[1], (u) => u == 'https://9.9.9.1:443/')) == 1,
	'TCP check only for the target that needed it: ' + sprintf('%J', probe_calls[1]));
T.eq(filter(keys(by), (u) => index(u, 'telegram') >= 0), [], 'services that are off are not checked');
r = DG.run_diagnose(dcfg, [ 'telegram' ], null, dhooks);
T.eq(map(CMD.diagnose_status().diagnose.targets, (t) => t.service), [ 'telegram' ], '--services picks the named service');
T.eq(DG.run_diagnose(dcfg, [ 'no-such' ], null, dhooks).error, 'unknown_service', 'unknown service');
T.eq(DG.run_diagnose(C.normalize({ lists: [] }, null, null), null, null, dhooks).error, 'no_targets', 'nothing switched on -> no_targets');
T.eq(filter(fs.lsdir(P.tmp) ?? [], (n) => index(n, 'probe.') == 0), [], 'temporary download directories removed');

/* ---------- §15.5 texts, §15.7 page, CLI ---------- */
for (let w in [ 'flowtable_failed', 'game_filter_no_ipsets', 'dns_plain' ])
	T.ok(TXT.WARNINGS[w] != null, 'warning text: ' + w);
T.eq([ CMD.PAGES.overview, CMD.PAGES.diagnostics ], [ [ 'status', 'job', 'presets', 'monitor', 'probe', 'dns' ],
	[ 'status', 'job', 'monitor', 'dns', 'diagnose' ] ], 'page fields (contract v1.4 §15.7)');
let pg = CMD.page('diagnostics');
T.ok(pg.ok && pg.dns?.ok && type(pg.dns.dns?.encrypted) == 'bool' && pg.diagnose?.ok && pg.diagnose.diagnose?.summary != null,
	'page diagnostics carries dns and diagnose: ' + sprintf('%.200J', { dns: pg.dns, d: pg.diagnose?.ok }));
T.ok(type(CMD.page('overview').dns?.dns?.encrypted) == 'bool', 'page overview carries dns');
let stt = CMD.status();
T.ok(type(stt.dns?.encrypted) == 'bool' && ('provider' in stt.dns) && type(stt.flow_offload?.own) == 'bool', 'status: dns and flow_offload.own');
let dtxt = TXT.render('diagnose status', { ok: true, diagnose: ds });
T.ok(index(dtxt, 'Итог: замедление') == 0 && index(dtxt, 'dns_spoof') > 0 && index(dtxt, 'ip_block') > 0, 'diagnose text: summary and verdicts');
T.ok(index(TXT.render('dns status', { ok: true, dns: { encrypted: false, provider: null } }), 'zaprett dns setup') > 0, 'dns status text');

let cli = [ 'ucode', '-S', '-L', T.ROOT + '/files/usr/share/ucode/*.uc', '--', T.ROOT + '/files/usr/share/zaprett/cli.uc' ];
function cli_run(args) {
	let x = slice(cli);
	for (let y in args)
		push(x, y);
	return run(x, { timeout: 30000 });
}
let u = cli_run([ 'diagnose', 'bogus', '--json' ]);
T.eq([ u.rc, json(u.stdout).error ], [ 2, 'usage' ], 'CLI: diagnose with a stray argument -> usage');
u = cli_run([ 'dns', 'nope', '--json' ]);
T.eq([ u.rc, json(u.stdout).error ], [ 2, 'usage' ], 'CLI: dns nope -> usage');
u = cli_run([ 'help' ]);
T.ok(index(u.stdout, 'diagnose') >= 0 && index(u.stdout, 'dns setup') >= 0, 'CLI help lists diagnose and dns');

exit(T.finish());
