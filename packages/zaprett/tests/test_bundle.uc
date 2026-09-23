'use strict';

// The real package bundle (files/usr/share/zaprett/bundle) and presets.json: every nfqws strategy must build
// and pass `nfqws --dry-run` with the package default configuration; presets must name existing items.
// alt11 and shizapret-port argv are printed as NOTE lines for manual comparison with the strategy text.
import * as fs from 'fs';
import * as T from 'ztest';
import { P, set_paths, read_json } from 'zaprett.util';
import * as C from 'zaprett.config';
import * as S from 'zaprett.store';
import * as G from 'zaprett.strategy';
import * as SV from 'zaprett.service';

T.begin('bundle');
T.selfcheck();
let W = T.sandbox('bundle');
const REAL = T.ROOT + '/files/usr/share/zaprett/bundle';
const ROM = '/usr/share/zaprett/bundle';

// sandbox copy of the real bundle: manifest paths point to the router location and are moved into the sandbox
system([ 'rm', '-rf', W + '/bundle', W + '/etc/files', W + '/etc/manifests' ]);
system([ 'cp', '-R', REAL, W + '/bundle' ]);
let manifests = 0, foreign = [];
function rewrite(dir) {
	for (let n in sort(fs.lsdir(dir) ?? [])) {
		let p = dir + '/' + n;
		if (fs.stat(p).type == 'directory') {
			rewrite(p);
			continue;
		}
		let m = json(fs.readfile(p));
		manifests++;
		if (index(m.file, ROM + '/files/') != 0)
			push(foreign, n);
		m.file = W + '/bundle' + substr(m.file, length(ROM));
		fs.writefile(p, sprintf('%J', m));
	}
}
rewrite(W + '/bundle/manifests');
set_paths({ presets: T.ROOT + '/files/usr/share/zaprett/presets.json' });

T.eq(foreign, [], 'every bundle manifest points into ' + ROM + '/files');
let bindex = read_json(W + '/bundle/index.json', 1048576);
T.eq(manifests, length(bindex?.items ?? []), sprintf('bundle/index.json lists every manifest (%d)', manifests));

let idx = S.scan();
T.eq(idx.errors, [], 'real bundle manifests parse without errors');
let bundle_ids = {};
for (let t in S.TYPE_ORDER)
	for (let id, it in idx.items[t])
		if (it.source == 'bundle')
			bundle_ids[t + ':' + id] = true;
T.eq(length(keys(bundle_ids)), manifests, 'every manifest became an item');
T.eq(length(filter(bindex?.items ?? [], (e) => !bundle_ids[e.type + ':' + e.id])), 0, 'every index.json entry is a scanned item');
let strategies = sort(filter(keys(idx.items.nfqws), (id) => idx.items.nfqws[id].source == 'bundle'));
T.eq(length(strategies), 64, 'bundle has 64 nfqws strategies');

/* ---- every strategy with the package default configuration ---- */
let cfg = C.load();
T.eq([ cfg.lists, cfg.exclude_lists, cfg.exclude_ipsets ], [ [ 'zaprett-youtube', 'zaprett-discord', 'user-hosts' ],
	[ 'zaprett-exclude', 'user-hosts-exclude' ], [ 'zaprett-exclude-ipset', 'user-ipset-exclude' ] ], 'package default lists');
let built = 0, dry_ok = 0, failures = [], unfiltered = [], wide = [], results = {};
for (let id in strategies) {
	let g = G.generate(cfg, { engine: 'nfqws', strategy: id, index: idx, ignore_override: true, skip_dry_run: !T.NFQWS });
	results[id] = g;
	if (!g.ok) {
		push(failures, id + ': ' + g.error + ' ' + g.message);
		continue;
	}
	built++;
	if (g.dry_run?.rc == 0)
		dry_ok++;
	else if (T.NFQWS)
		push(failures, id + ': dry-run rc=' + g.dry_run?.rc + ' ' + g.dry_run?.output);
	if (index(g.warnings, 'profile_unfiltered') >= 0)
		push(unfiltered, id);
	if (index(g.warnings, 'wide_port_range') >= 0)
		push(wide, id);
	if (length(filter(g.args, (a) => index(a, '${') >= 0)))
		push(failures, id + ': placeholder left in args');
}
T.eq(failures, [], 'no strategy failed');
T.eq(built, 64, 'all 64 strategies built');
if (T.NFQWS)
	T.eq(dry_ok, 64, 'all 64 strategies pass nfqws --dry-run');
else
	print('SKIP [bundle] nfqws binary not given: dry-run not checked\n');
print(sprintf('NOTE [bundle] profile_unfiltered: %J\n', unfiltered));
print(sprintf('NOTE [bundle] wide_port_range: %J\n', wide));
T.has(unfiltered, 'strategy-alt5', 'alt5 reported as unfiltered');
T.has(unfiltered, 'strategy-default', 'default reported as unfiltered');

let B = W + '/bundle/files';
let base = [ '--qnum=200', '--user=daemon', '--dpi-desync-fwmark=0x40000000' ];
let hostlists = [ '--hostlist=' + B + '/lists/include/zaprett-youtube.txt', '--hostlist=' + B + '/lists/include/zaprett-discord.txt',
	'--hostlist=' + W + '/etc/user/hosts-include.txt', '--hostlist=' + P.guard_hostlist,
	'--hostlist-exclude=' + B + '/lists/exclude/zaprett-exclude.txt', '--hostlist-exclude=' + W + '/etc/user/hosts-exclude.txt' ];
let ipset_excl = [ '--ipset-exclude=' + B + '/ipset/exclude/zaprett-exclude-ipset.txt', '--ipset-exclude=' + W + '/etc/user/ipset-exclude.txt' ];

// base options are glued to the first profile: [ null, profile 1, profile 2, ... ] after they are cut off
function profiles(args) {
	let res = [ null, [] ];
	for (let a in slice(args, length(base))) {
		if (a == '--new')
			push(res, []);
		else
			push(res[length(res) - 1], a);
	}
	return res;
}
function note_args(id, args) {
	let pr = profiles(args);
	print(sprintf('NOTE [bundle] %s base: %s\n', id, join(' ', slice(args, 0, length(base)))));
	for (let n = 1; n < length(pr); n++)
		print(sprintf('NOTE [bundle] %s profile %d: %s\n', id, n, join(' ', pr[n])));
}

/* ---- alt11, checked against its text by hand ---- */
let a11 = results['strategy-alt11'];
if (T.ok(a11?.ok, 'alt11 built')) {
	note_args('strategy-alt11', a11.args);
	let pr = profiles(a11.args);
	T.eq(length(pr) - 1, 7, 'alt11: 7 profiles (trailing backslashes joined the lines)');
	T.eq(slice(a11.args, 0, length(base)), base, 'alt11: base options before the first profile');
	let excl_domains = '--hostlist-exclude-domains=pusher.com,live-video.net,ttvnw.net,twitch.tv,mail.ru,citilink.ru,yandex.com,nvidia.com,donationalerts.com,vk.com,yandex.kz,mts.ru,multimc.org,ya.ru,dns-shop.ru,habr.com,3dnews.ru,sberbank.ru,ozon.ru,wildberries.ru,microsoft.com,msi.com,akamaitechnologies.com,2ip.ru,yandex.ru,boosty.to';
	let excl_ip = '--ipset-exclude-ip=0.0.0.0/8,10.0.0.0/8,127.0.0.0/8,172.16.0.0/12,192.168.0.0/16,169.254.0.0/16,224.0.0.0/4,100.64.0.0/10,::1,fc00::/7,fe80::/10';
	// profile 1: "--filter-udp=443 ${hostlists} --hostlist-exclude-domains=... --ipset-exclude-ip=... --dpi-desync=fake
	//             --dpi-desync-repeats=11 --dpi-desync-fake-quic=${bin:quic_initial_www_google_com}"
	let exp1 = [ '--filter-udp=443' ];
	for (let x in hostlists) push(exp1, x);
	for (let x in ipset_excl) push(exp1, x);
	for (let x in [ excl_domains, excl_ip, '--dpi-desync=fake', '--dpi-desync-repeats=11', '--dpi-desync-fake-quic=' + B + '/bin/quic_initial_www_google_com.bin' ])
		push(exp1, x);
	T.eq(pr[1], exp1, 'alt11 profile 1 (udp 443, hostlists)');
	T.eq(pr[2], [ '--filter-udp=19294-19344,50000-50100', '--filter-l7=discord,stun', '--dpi-desync=fake', '--dpi-desync-repeats=6' ],
		'alt11 profile 2 (discord voice, no placeholders)');
	T.eq(pr[3], [ '--filter-tcp=2053,2083,2087,2096,8443', '--hostlist-domains=discord.media', '--dpi-desync=fake,multisplit',
		'--dpi-desync-split-seqovl=681', '--dpi-desync-split-pos=1', '--dpi-desync-fooling=ts', '--dpi-desync-repeats=8',
		'--dpi-desync-split-seqovl-pattern=' + B + '/bin/tls_clienthello_www_google_com.bin',
		'--dpi-desync-fake-tls=' + B + '/bin/tls_clienthello_www_google_com.bin' ], 'alt11 profile 3 (discord.media)');
	// profile 6: "--filter-udp=443 ${ipsets} ..." — no include ipsets: only the ipset guard, and no domain exclusions (v1.2 §5)
	let exp6 = [ '--filter-udp=443', '--ipset=' + P.guard_ipset ];
	for (let x in ipset_excl) push(exp6, x);
	for (let x in [ excl_domains, excl_ip, '--dpi-desync=fake', '--dpi-desync-repeats=11', '--dpi-desync-fake-quic=' + B + '/bin/quic_initial_www_google_com.bin' ])
		push(exp6, x);
	T.eq(pr[6], exp6, 'alt11 profile 6 (udp 443, ipsets)');
	T.eq(pr[7][length(pr[7]) - 1], '--dpi-desync-fake-tls=' + B + '/bin/tls_clienthello_max_ru.bin', 'alt11: last token of the last profile');
	T.eq(a11.ports, { tcp: [ '80', '443', '2053', '2083', '2087', '2096', '8443' ], udp: [ '443', '19294-19344', '50000-50100' ] }, 'alt11 ports for nft');
}

/* ---- shizapret-port: --comment junk, 14 profiles ---- */
let sz = results['strategy-shizapret-port'];
if (T.ok(sz?.ok, 'shizapret-port built')) {
	note_args('strategy-shizapret-port', sz.args);
	let pr = profiles(sz.args);
	T.eq(length(pr) - 1, 14, 'shizapret-port: 14 profiles');
	T.eq(slice(sz.args, 0, length(base)), base, 'shizapret-port: base options first');
	let junk = filter(sz.args, (a) => substr(a, 0, 2) != '--');
	T.eq(junk, [], 'no comment words left (Telegram, (WebRTC), [W.I.P.], ...)');
	T.eq(length(filter(sz.args, (a) => a == '--comment' || index(a, '--comment=') == 0)), 0, 'bare --comment removed');
	T.eq(pr[1], [ '--filter-udp=1400', '--filter-l7=stun', '--dpi-desync=fake', '--dpi-desync-fake-stun=0x00' ], 'shizapret profile 1 (Telegram WebRTC)');
	// "--comment YouTube QUIC/QUIC --filter-udp=443 ${hostlists} --dpi-desync=fake --dpi-desync-repeats=11 --dpi-desync-fake-quic=${bin:...}"
	let exp6 = [ '--filter-udp=443' ];
	for (let x in hostlists) push(exp6, x);
	for (let x in ipset_excl) push(exp6, x);
	for (let x in [ '--dpi-desync=fake', '--dpi-desync-repeats=11', '--dpi-desync-fake-quic=' + B + '/bin/quic_initial_www_google_com.bin' ])
		push(exp6, x);
	T.eq(pr[6], exp6, 'shizapret profile 6 (YouTube QUIC)');
	T.eq(pr[11], [ '--filter-udp=0-65535', '--filter-l7=wireguard', '--dpi-desync=fake', '--dpi-desync-fake-wireguard=0x00',
		'--dpi-desync-repeats=4', '--dpi-desync-cutoff=n2' ], 'shizapret profile 11 (WireGuard)');
	T.has(sz.warnings, 'wide_port_range', 'udp 0-65535 reported as a wide port range');
	let p14 = [ '--filter-udp=443', '--ipset=' + P.guard_ipset ];
	for (let x in ipset_excl) push(p14, x);
	for (let x in [ '--dpi-desync=fake', '--dpi-desync-repeats=6' ])
		push(p14, x);
	T.eq(pr[14], p14, 'shizapret profile 14 (last, ipsets only: no domain exclusions)');
}

/* ---- nfqws2 strategies (contract v1.4 §15.6) ---- */
// `nfqws2 --intercept=0` needs root, the binary and the Lua libraries of the zaprett-nfqws2 package (only *.lua.gz
// are installed; nfqws2 finds file.lua.gz by @file.lua). Without them only the generator is checked.
const NFQWS2 = getenv('ZTEST_NFQWS2') ?? (T.NFQWS ? fs.dirname(T.NFQWS) + '/nfqws2' : null);
const LUA_DIR = getenv('ZTEST_LUA') ?? '/usr/share/zaprett/lua';
let z2 = sort(filter(keys(idx.items.nfqws2 ?? {}), (id) => idx.items.nfqws2[id].source == 'bundle'));
T.ok(length(z2) >= 10, sprintf('bundle has nfqws2 strategies: %d', length(z2)));
T.eq(filter(z2, (id) => substr(id, 0, 3) != 'z2-'), [], 'every nfqws2 strategy id starts with z2-');
let bad_sha = [];
for (let id in z2) {
	let it = idx.items.nfqws2[id];
	let p = fs.popen('sha256sum ' + REAL + substr(it.file, length(W + '/bundle')));
	let sum = p ? split(trim(p.read('all') ?? ''), ' ')[0] : '';
	if (p) p.close();
	if (sum != it.sha256)
		push(bad_sha, id);
}
T.eq(bad_sha, [], 'nfqws2 manifests: sha256 of every file matches');
let has_z2 = !!(NFQWS2 && fs.stat(NFQWS2)?.type == 'file' && fs.stat(LUA_DIR + '/zapret-lib.lua.gz')?.type == 'file');
// strategies with their own --lua-init name the router Lua directory; in the sandbox it lives under P.share
for (let id in z2) {
	let f = idx.items.nfqws2[id].file, text = fs.readfile(f);
	if (index(text, '/usr/share/zaprett/lua/') >= 0)
		fs.writefile(f, replace(text, '/usr/share/zaprett/lua/', P.share + '/lua/'));
}
if (has_z2) {
	system([ 'cp', NFQWS2, P.libexec + '/nfqws2' ]);
	fs.chmod(P.libexec + '/nfqws2', 493);
	system([ 'sh', '-c', 'cp "$0"/*.lua.gz "$1"/', LUA_DIR, P.share + '/lua' ]);
}
let z2_built = 0, z2_ok = 0, z2_fail = [], z2_res = {};
for (let id in z2) {
	let g = G.generate(cfg, { engine: 'nfqws2', strategy: id, index: idx, ignore_override: true, skip_dry_run: !has_z2,
		force_dry_run: true });
	z2_res[id] = g;
	if (!g.ok) {
		push(z2_fail, id + ': ' + g.error + ' ' + g.message);
		continue;
	}
	z2_built++;
	if (length(filter(g.args, (a) => index(a, '${') >= 0 || substr(a, 0, 12) == '--dpi-desync')))
		push(z2_fail, id + ': placeholder or nfqws option left in args');
	if (!length(filter(g.args, (a) => substr(a, 0, 11) == '--lua-init=')))
		push(z2_fail, id + ': no --lua-init');
	if (g.dry_run?.rc == 0)
		z2_ok++;
	else if (has_z2)
		push(z2_fail, id + ': intercept rc=' + g.dry_run?.rc + ' ' + g.dry_run?.output);
}
T.eq(z2_fail, [], 'no nfqws2 strategy failed');
T.eq(z2_built, length(z2), sprintf('all %d nfqws2 strategies built', length(z2)));
if (has_z2) {
	T.eq(z2_ok, length(z2), sprintf('all %d nfqws2 strategies pass nfqws2 --intercept=0', length(z2)));
	// negative controls with the real engine: the same arguments broken in one place must be rejected
	let gen = z2_res['z2-general'];
	if (T.ok(gen?.ok, 'z2-general built for the negative controls')) {
		let broken = map(gen.args, (a) => replace(a, '--lua-desync=multidisorder:', '--lua-desync=no_such_fn_xyz:'));
		T.ok(join(' ', broken) != join(' ', gen.args), 'negative control: a desync function was replaced');
		T.ok(G.dry_run('nfqws2', broken).rc != 0, 'negative control: nfqws2 rejects an unknown Lua function');
		let badpl = map(gen.args, (a) => (a == '--payload=quic_initial') ? '--payload=quic_initiall' : a);
		T.ok(G.dry_run('nfqws2', badpl).rc != 0, 'negative control: nfqws2 rejects an unknown payload type');
		// a blob referenced by a name that was never defined is NOT caught by --intercept=0 (only when a packet comes);
		// tools/data/bundle_checks.py checks blob names offline (E_BLOB_UNDEFINED)
		let noblob = map(gen.args, (a) => replace(a, 'blob=quic_google:', 'blob=quic_nope:'));
		print(sprintf('NOTE [bundle] nfqws2 --intercept=0 with an undefined blob name: rc=%d (runtime-only error)\n',
			G.dry_run('nfqws2', noblob).rc));
	}
}
else
	print(sprintf('SKIP [bundle] nfqws2 binary (%s) or %s/zapret-lib.lua.gz missing: nfqws2 --intercept=0 not checked\n', NFQWS2, LUA_DIR));

/* ---- cost of scanning the real bundle (LuCI calls status on every page load) ---- */
// Порог намеренно с запасом к измеренным на x86-VM стенда значениям (scan ~50 мс, status ~45 мс):
// регрессия вроде интервала `{1,96}` в регулярке возвращала scan к 1,5 с и падала бы здесь.
function ms(f) {
	let a = clock(true);
	f();
	let b = clock(true);
	return (b[0] - a[0]) * 1000 + int((b[1] - a[1]) / 1000000);
}
let scan_ms = ms(() => S.scan());
let status_ms = ms(() => SV.status(cfg));
let load_ms = ms(() => C.load());
print(sprintf('NOTE [bundle] S.scan() %d ms, SV.status() %d ms, C.load() %d ms (%d manifests)\n',
	scan_ms, status_ms, load_ms, manifests));
T.ok(scan_ms < 300, sprintf('S.scan() over the real bundle: %d ms (limit 300)', scan_ms));
T.ok(status_ms < 500, sprintf('SV.status(): %d ms (limit 500)', status_ms));
T.ok(load_ms < 100, sprintf('C.load(): %d ms (limit 100)', load_ms));

/* ---- presets.json references ---- */
let presets = read_json(P.presets, 1048576);
T.ok(type(presets?.services) == 'array' && length(presets.services) > 0, 'presets.json readable');
let source_names = map(C.load_sources(), (s) => s.name);
let missing = [];
for (let s in presets?.services ?? []) {
	for (let l in s.lists ?? [])
		if (!idx.items.list[l]) push(missing, s.id + ' list ' + l);
	for (let l in s.ipsets ?? [])
		if (!idx.items.ipset[l]) push(missing, s.id + ' ipset ' + l);
	for (let n in s.sources ?? [])
		if (index(source_names, n) < 0) push(missing, s.id + ' source ' + n);
}
for (let l in presets?.always?.exclude_lists ?? [])
	if (!idx.items.list_exclude[l]) push(missing, 'always ' + l);
for (let l in presets?.always?.exclude_ipsets ?? [])
	if (!idx.items.ipset_exclude[l]) push(missing, 'always ' + l);
for (let id in presets?.defaults?.quick_test_strategies ?? [])
	if (!idx.items.nfqws[id]) push(missing, 'quick_test ' + id);
if (!idx.items.nfqws[presets?.defaults?.strategy])
	push(missing, 'defaults.strategy');
// contract v1.4 §15.6: both keys or none
if (presets?.defaults?.strategy_nfqws2 != null || presets?.defaults?.quick_test_strategies_nfqws2 != null) {
	if (!idx.items.nfqws2?.[presets.defaults.strategy_nfqws2])
		push(missing, 'defaults.strategy_nfqws2');
	for (let id in presets.defaults.quick_test_strategies_nfqws2 ?? [ '(none)' ])
		if (!idx.items.nfqws2?.[id]) push(missing, 'quick_test_nfqws2 ' + id);
}
for (let id in presets?.defaults?.services ?? [])
	if (!length(filter(presets.services, (s) => s.id == id))) push(missing, 'defaults.services ' + id);
T.eq(missing, [], 'every preset reference exists in the bundle and in the default UCI subscriptions');
// negative control: the same check finds an unknown id
T.ok(!idx.items.list['zaprett-no-such-list'], 'negative control: unknown list id is not found');

/* ---- contract v1.5 §16: own per-service lists and preset variants ---- */
let vmissing = [], nvariants = 0;
for (let s in presets?.services ?? [])
	for (let v in s.variants ?? []) {
		nvariants++;
		for (let l in v.lists ?? [])
			if (!idx.items.list[l]) push(vmissing, s.id + '/' + v.id + ' list ' + l);
		for (let l in v.ipsets ?? [])
			if (!idx.items.ipset[l]) push(vmissing, s.id + '/' + v.id + ' ipset ' + l);
	}
T.ok(nvariants >= 6, sprintf('presets carry service variants: %d', nvariants));
T.eq(vmissing, [], 'every preset variant reference exists in the bundle');
let own = [];
for (let t in [ 'list', 'ipset' ])
	for (let id in sort(keys(idx.items[t] ?? {})))
		if (idx.items[t][id].source == 'bundle' && idx.items[t][id].author == 'zaprett-openwrt')
			push(own, [ t, id ]);
T.ok(length(own) >= 12, sprintf('bundle has own per-service lists: %d', length(own)));
if (T.NFQWS) {
	let bad = [];
	for (let o in own) {
		let opt = (o[0] == 'list') ? '--hostlist=' : '--ipset=';
		let rc = system([ 'sh', '-c', '"$0" --dry-run --qnum=200 "$1" >/dev/null 2>&1', T.NFQWS, opt + idx.items[o[0]][o[1]].file ]);
		if (rc != 0)
			push(bad, o[1] + ' rc=' + rc);
	}
	T.eq(bad, [], sprintf('every own list loads in nfqws --dry-run (%d lists)', length(own)));
	let nrc = system([ 'sh', '-c', '"$0" --dry-run --qnum=200 "$1" >/dev/null 2>&1', T.NFQWS, '--hostlist=' + W + '/no-such-list.txt' ]);
	T.ok(nrc != 0, 'negative control: nfqws --dry-run fails on a missing list file');
}
else
	print('SKIP [bundle] nfqws binary not given: own lists not loaded by nfqws\n');

exit(T.finish());
