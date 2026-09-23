'use strict';

import * as fs from 'fs';
import * as T from 'ztest';
import { P } from 'zaprett.util';
import * as C from 'zaprett.config';
import * as S from 'zaprett.store';
import * as G from 'zaprett.strategy';
import { ENGINE_OPTIONS } from 'zaprett.engine_options';

T.begin('strategy');
T.selfcheck();
let W = T.sandbox('strategy');

/* ---- tokenize ---- */
let tk = G.tokenize('--a=1 \\\n--b=2\t--c=3 \\\r\n  --comment Telegram (WebRTC) [W.I.P.] --d=4\n\\\n--comment=keep --e \\');
T.eq(tk.tokens, [ '--a=1', '--b=2', '--c=3', '--d=4', '--comment=keep', '--e' ], 'tokenize: backslashes, tabs, CRLF, comment junk');
T.eq(tk.dropped, [ '--comment', 'Telegram', '(WebRTC)', '[W.I.P.]' ], 'tokenize: dropped comment tokens');
T.eq(G.tokenize('').tokens, [], 'tokenize empty');
T.eq(G.tokenize('--comment only words').tokens, [], 'tokenize comment at end swallows the rest');
// negative control: a plain '\' inside a value is not a line continuation
T.eq(G.tokenize('--x=a\\b').tokens, [ '--x=a\\b' ], 'tokenize keeps inner backslash');

// CRLF strategies (e.g. uploaded from Windows through the web UI): CR is whitespace like in split_whitespace
let lf_text = fs.readfile(T.ROOT + '/tests/fixtures/bundle/files/strategies/nfqws/strategy-alt11.txt');
let crlf_text = replace(lf_text, '\n', '\r\n');
T.ok(length(split(lf_text, '\n')) > 5 && length(crlf_text) - length(lf_text) == length(split(lf_text, '\n')) - 1,
	'CRLF variant has a CR before every LF');
let tk_lf = G.tokenize(lf_text).tokens, tk_crlf = G.tokenize(crlf_text).tokens;
T.eq(tk_crlf, tk_lf, 'CRLF strategy tokenizes exactly like LF (alt11 with trailing backslashes)');
T.eq(length(filter(tk_crlf, (t) => index(t, '\r') >= 0)), 0, 'no token keeps a CR');
T.eq(G.tokenize('--a=1\r\n--new\r\n--b=2\r').tokens, [ '--a=1', '--new', '--b=2' ], 'CR at line ends and at the end of text');
// negative control: a byte that is not whitespace is kept, so the comparison above would notice a difference
T.ok(sprintf('%J', G.tokenize(replace(lf_text, '\n', chr(1) + '\n')).tokens) != sprintf('%J', tk_lf), 'negative control: non-whitespace byte changes tokens');

/* ---- legacy modes ---- */
T.eq(G.normalize_modes([ '--dpi-desync=fake,split2', '--dpi-desync-fooling=split', '--dpi-desync=disorder2', '--dpi-desync=split', '--dpi-desync=disorder,fake' ]),
	[ '--dpi-desync=fake,multisplit', '--dpi-desync-fooling=split', '--dpi-desync=multidisorder', '--dpi-desync=fakedsplit', '--dpi-desync=fakeddisorder,fake' ],
	'normalize legacy desync modes only in --dpi-desync');

/* ---- reserved options ---- */
let sr = G.strip_reserved([ '--qnum=5', '--user=root', '-uid=0', '--dpi-desync=fake', '--daemon', '--debug=@/tmp/x', '--fwmark=1', '--pidfile=/x', '--filter-tcp=443' ]);
T.eq(sr.tokens, [ '--dpi-desync=fake', '--filter-tcp=443' ], 'strip reserved options');
T.eq(sr.ignored, [ 'qnum', 'user', 'uid', 'daemon', 'debug', 'fwmark', 'pidfile' ], 'reserved options reported');

/* ---- options as getopt_long_only sees them (ZERR-033) ---- */
let cz = (toks, engine) => G.canonicalize(toks, engine ?? 'nfqws');
T.eq(cz([ '-dpi-desync=fake', '--dpi-desync-fake-qu=0x00', '--debu=@/tmp/x', '--qnum', '5', '-hostlist', '/etc/zaprettX/f', '${hostlists}', '--ne', '--new', '--dpi-desync-any' ]).tokens,
	[ '--dpi-desync=fake', '--dpi-desync-fake-quic=0x00', '--debug=@/tmp/x', '--qnum=5', '--hostlist=/etc/zaprettX/f', '${hostlists}', '--new', '--new', '--dpi-desync-any-protocol' ],
	'canonicalize: single dash, unique prefix, separate value of a required option, placeholders kept');
T.eq(cz([ '--dpi-desync-fake-tls=a', '--dpi-desync-fake-tls-mod=rnd', '--hostlist-auto=/x', '--hostlist=/y' ]).tokens,
	[ '--dpi-desync-fake-tls=a', '--dpi-desync-fake-tls-mod=rnd', '--hostlist-auto=/x', '--hostlist=/y' ], 'canonicalize: an exact name wins over longer ones');
T.eq(cz([ '--dpi-desync-fooling', '-x' ]).tokens, [ '--dpi-desync-fooling=-x' ], 'canonicalize: a required option takes the next word even when it starts with -');
T.eq(cz([ '--lua-ini', '@/x.lua', '-new=n1', '--templ', '--blo=a:0x00' ], 'nfqws2').tokens, [ '--lua-init=@/x.lua', '--new=n1', '--template', '--blob=a:0x00' ],
	'canonicalize nfqws2: own table, --new takes a name');
for (let c in [
	[ [ '--hostl=/etc/zaprettX/f' ], 'nfqws', 'ambiguous prefix' ], [ [ '--dpi-desync-fake-t=/etc/zaprettX/f' ], 'nfqws', 'ambiguous prefix (tls, tls-mod, tcp-mod)' ],
	[ [ '--foo=1' ], 'nfqws', 'unknown option' ], [ [ '--lua-init=@/x' ], 'nfqws', 'nfqws2 option in nfqws' ], [ [ '--dpi-desync=fake' ], 'nfqws2', 'nfqws option in nfqws2' ],
	[ [ 'stray' ], 'nfqws', 'a word that is not an option' ], [ [ '--' ], 'nfqws', 'end of options' ], [ [ '-' ], 'nfqws', 'lone dash' ],
	[ [ '---qnum=1' ], 'nfqws', 'three dashes' ], [ [ '--=x' ], 'nfqws', 'empty name' ], [ [ '--new=x' ], 'nfqws', 'no-argument option with a value' ],
	[ [ '--hostlist' ], 'nfqws', 'required option at the end' ], [ [ '--hostlist', '${hostlists}' ], 'nfqws', 'placeholder as a separate value' ],
	[ [ '-h' ], 'nfqws', 'one-letter ambiguous prefix' ] ])
	T.eq(cz(c[0], c[1]).error, 'bad_option', 'canonicalize rejects: ' + c[2]);
T.eq(G.resolve_option('dpi-desync', ENGINE_OPTIONS.nfqws), { name: 'dpi-desync' }, 'resolve: exact');
T.eq(G.resolve_option('dpi-desync-fake-t', ENGINE_OPTIONS.nfqws).ambiguous,
	[ 'dpi-desync-fake-tcp-mod', 'dpi-desync-fake-tls', 'dpi-desync-fake-tls-mod' ], 'resolve: ambiguous names listed');
// every name the checks look for exists in the engine tables (a typo in a list would silently disable its check)
let all_known = {};
for (let e in [ 'nfqws', 'nfqws2' ])
	for (let n in keys(ENGINE_OPTIONS[e]))
		all_known[n] = true;
T.eq(filter([ ...G.RESERVED_OPTIONS, ...G.FILE_OPTIONS, ...G.AUTOHOSTLIST_OPTIONS, ...G.INCLUDE_FILTERS ], (n) => !all_known[n]), [],
	'reserved, file and filter option names are engine options');
T.eq([ length(keys(ENGINE_OPTIONS.nfqws)), length(keys(ENGINE_OPTIONS.nfqws2)) ], [ 111, 63 ], 'engine tables: 111 nfqws and 63 nfqws2 options');
// nfqws2 profiles named by --new=<name>
T.eq(G.split_profiles([ '--filter-tcp=443', '--new=b', '--filter-udp=443', '--new', '--filter-tcp=80' ]),
	[ [ '--filter-tcp=443' ], [ '--new=b', '--filter-udp=443' ], [ '--filter-tcp=80' ] ], 'split_profiles: --new=<name> starts a profile');
T.eq(G.join_profiles(G.split_profiles([ '--a', '--new=b', '--c', '--new', '--d' ])), [ '--a', '--new=b', '--c', '--new', '--d' ], 'join_profiles keeps --new=<name>');
T.eq(G.extract_ports([ '--filter-udp=443', '--new=b', '--dpi-desync=fake' ]).tcp, [ '80', '443' ], 'ports: a named profile is a separate profile (defaults)');

/* ---- ports ---- */
T.eq(G.extract_ports([]).tcp, [ '80', '443' ], 'no filters -> tcp defaults');
T.eq(G.extract_ports([]).udp, [ '443' ], 'no filters -> udp defaults');
let pp = G.extract_ports([ '--filter-udp=443', '--new', '--filter-tcp=80,443' ]);
T.eq([ pp.tcp, pp.udp ], [ [ '80', '443' ], [ '443' ] ], 'tcp-only / udp-only profiles');
pp = G.extract_ports([ '--filter-tcp=~80' ]);
T.eq([ pp.tcp, pp.udp ], [ [ '80', '443' ], [] ], 'negated tcp filter -> tcp defaults, udp denied');
pp = G.extract_ports([ '--filter-udp=50000-50100', '--skip', '--new', '--filter-tcp=8443' ]);
T.eq([ pp.tcp, pp.udp ], [ [ '8443' ], [] ], 'skipped profile ignored');
pp = G.extract_ports([ '--filter-l7=stun', '--new', '--filter-udp=0-65535', '--new', '--filter-udp=443' ]);
T.eq([ pp.tcp, pp.udp ], [ [ '80', '443' ], [ '0-65535' ] ], 'overlapping udp ranges merged');
T.eq(G.extract_ports([ '--filter-tcp=99999' ]).error, 'bad_port_filter', 'bad port filter rejected');

/* ---- placeholder expansion (pure env) ---- */
function env(over) {
	let e = {
		mode: 'whitelist', lists: [ { file: '/L1', entries: 3 } ], exclude_lists: [ { file: '/E1', entries: 1 } ],
		ipsets: [], exclude_ipsets: [ { file: '/IE', entries: 1 } ], guard_hostlist: '/G', guard_ipset: '/GI',
		zaprettdir: '/Z', declared_deps: [ 'x' ],
		resolve: (t, id) => (t == 'bin' && id == 'x') ? { file: '/B/x.bin' } : null
	};
	for (let k, v in over)
		e[k] = v;
	return e;
}
let ex = G.expand_profile([ '--filter-tcp=443', '${hostlists}', '--dpi-desync-fake-tls=${bin:x}' ], env());
T.eq(ex.tokens, [ '--filter-tcp=443', '--hostlist=/L1', '--hostlist=/G', '--hostlist-exclude=/E1', '--ipset-exclude=/IE', '--dpi-desync-fake-tls=/B/x.bin' ],
	'whitelist hostlists-only profile: include lists, guard, domain and IP exclusions (contract v1.2), bin');
ex = G.expand_profile([ '--filter-tcp=443', '${hostlists}' ], env({ exclude_lists: [], exclude_ipsets: [] }));
T.eq(ex.tokens, [ '--filter-tcp=443', '--hostlist=/L1', '--hostlist=/G' ], 'whitelist without exclusions');
ex = G.expand_profile([ '--filter-udp=443', '${ipsets}' ], env({ ipsets: [ { file: '/I1', entries: 5 } ] }));
T.eq(ex.tokens, [ '--filter-udp=443', '--ipset=/I1', '--ipset=/GI', '--ipset-exclude=/IE' ],
	'ipsets-only profile: no --hostlist-exclude (would require a hostname, desync.c:254)');
T.ok(index(G.expand_profile([ '${ipsets}' ], env()).tokens, '--hostlist-exclude=/E1') < 0, 'negative control: domain exclusions never reach ipset-only profiles');
ex = G.expand_profile([ '${hostlists}', '${ipsets}' ], env({ mode: 'blacklist' }));
T.eq(ex.tokens, [ '--hostlist-exclude=/E1', '--ipset-exclude=/IE' ], 'blacklist expansion without guard');
ex = G.expand_profile([ '${hostlists}' ], env({ lists: [] }));
T.eq(ex.tokens, [ '--hostlist=/G', '--hostlist-exclude=/E1', '--ipset-exclude=/IE' ], 'ADR-004: no lists -> guard only (+exclusions)');
ex = G.expand_profile([ '${ipsets}' ], env());
T.eq(ex.tokens, [ '--ipset=/GI', '--ipset-exclude=/IE' ], 'ipsets: guard and ipset exclusions');
ex = G.expand_profile([ '${hostlists}', '${ipsets}' ], env({ ipsets: [] }));
T.eq(ex.tokens, [ '--hostlist=/L1', '--hostlist=/G', '--hostlist-exclude=/E1', '--ipset-exclude=/IE' ], 'dual placeholders: empty ipsets left out, exclusions kept');
ex = G.expand_profile([ '${hostlists}', '${ipsets}' ], env({ lists: [ { file: '/L0', entries: 0 } ], ipsets: [ { file: '/I1', entries: 2 } ] }));
T.eq(ex.tokens, [ '--ipset=/I1', '--ipset=/GI', '--ipset-exclude=/IE' ], 'dual placeholders: ipset-only profile gets no hostlist options at all');
ex = G.expand_profile([ '${hostlists}', '${ipsets}' ], env({ lists: [ { file: '/L0', entries: 0 } ], ipsets: [] }));
T.eq(ex.tokens, [ '--hostlist=/L0', '--hostlist=/G', '--hostlist-exclude=/E1', '--ipset=/GI', '--ipset-exclude=/IE' ], 'dual placeholders: both empty -> both guards');
ex = G.expand_profile([ '${ipsets}', '${hostlists}' ], env({ ipsets: [ { file: '/I1', entries: null } ] }));
T.eq(ex.tokens, [ '--ipset=/I1', '--ipset=/GI', '--ipset-exclude=/IE', '--hostlist=/L1', '--hostlist=/G', '--hostlist-exclude=/E1' ],
	'dual placeholders: both non-empty (gzip counts as non-empty)');
T.eq(G.expand_profile([ '${zaprettdir}/bin/a.bin' ], env()).tokens, [ '/Z/bin/a.bin' ], 'zaprettdir');
T.eq(G.expand_profile([ '--x=${hostlists}' ], env()).error, 'placeholder_in_token', 'hostlists inside token rejected');
T.eq(G.expand_profile([ '${foo}' ], env()).error, 'unknown_placeholder', 'unknown placeholder rejected');
T.eq(G.expand_profile([ '--y=${bin:y}' ], env()).error, 'dependency_not_declared', 'undeclared dependency rejected');
T.eq(G.expand_profile([ '--y=${bin:y}' ], env({ declared_deps: null })).error, 'item_not_installed', 'missing item rejected');
T.eq(G.expand_profile([ '--z=${bin:x' ], env()).error, 'unknown_placeholder', 'unclosed placeholder rejected');
T.eq(G.expand_profile([ '--z=${bin:../x}' ], env({ declared_deps: null })).error, 'bad_placeholder_id', 'path traversal id rejected');
T.eq(G.expand_profile([ '--z=${ipset:x}' ], env()).error, 'item_not_installed', 'placeholder type is respected (bin x is not an ipset)');

/* ---- file options must stay inside zaprett directories ---- */
let roots = [ '/etc/zaprett', '/usr/share/zaprett' ];
for (let t in [ '--dpi-desync-fake-tls=/usr/share/zaprett/bundle/files/bin/a.bin', '--dpi-desync-fake-quic=0xC30000000108', '--dpi-desync-fake-tls=!',
	'--hostlist=/etc/zaprett/files/lists/include/x.txt', '--dpi-desync-fake-tls=+10@/etc/zaprett/files/bin/a.bin', '--blob=g:@/usr/share/zaprett/x.bin',
	'--blob=z:0x00000000', '--lua-init=@/usr/share/zaprett/lua/zapret-lib.lua', '--hostlist-domains=a.com', '--dpi-desync-fake-tls-mod=rnd,sni=x.ru' ])
	T.ok(G.check_file_options([ t ], roots).ok, 'file option allowed: ' + t);
for (let t in [ '--dpi-desync-fake-tls=/etc/shadow', '--dpi-desync-fake-tls=+10@/etc/shadow', '--dpi-desync-fake-quic=etc/shadow',
	'--hostlist=/etc/zaprett/../shadow', '--ipset=/etc/zaprettX/a', '--blob=g:@/etc/shadow', '--lua-init=io.open("/etc/shadow")',
	'--lua-init=@/tmp/x.lua', '-dpi-desync-split-seqovl-pattern=/root/x', '--hostlist-auto=/etc/config/firewall' ])
	T.eq(G.check_file_options([ t ], roots).error, 'path_not_allowed', 'file option rejected: ' + t);
T.eq(G.check_file_options([ '--blob=nocolon' ], roots).error, 'bad_option', 'blob without name');
T.ok(G.check_file_options([ '--hostlist-auto=' + G.autohostlist_dir() + '/yt.txt', '--hostlist-auto-debug=' + G.autohostlist_dir() + '/dbg.log' ], roots).ok,
	'auto hostlist inside the autohostlist directory allowed');
for (let t in [ '--hostlist-auto=/etc/zaprett/files/lists/include/x.txt', '--hostlist-auto-debug=/usr/share/zaprett/x', '--hostlist-auto=' + P.run + '/x.txt' ])
	T.eq(G.check_file_options([ t ], roots).error, 'path_not_allowed', 'auto hostlist outside its directory rejected: ' + t);

/* ---- base options ---- */
let cfg = C.normalize(null, null, null);
T.eq(G.base_options(cfg, 'nfqws', []), [ '--qnum=200', '--user=daemon', '--dpi-desync-fwmark=0x40000000' ], 'nfqws base options');
let dcfg = C.normalize({ debug: '1', qnum: '300', user: 'nobody' }, null, null);
T.eq(G.base_options(dcfg, 'nfqws', []), [ '--debug=syslog', '--qnum=300', '--user=nobody', '--dpi-desync-fwmark=0x40000000' ], 'debug first');
T.eq(G.base_options(cfg, 'nfqws2', [ '--lua-desync=fake' ]), [ '--qnum=200', '--user=daemon', '--fwmark=0x40000000',
	'--lua-init=@' + P.share + '/lua/zapret-lib.lua', '--lua-init=@' + P.share + '/lua/zapret-antidpi.lua',
	'--lua-init=@' + P.share + '/lua/zapret-auto.lua' ], 'nfqws2 base options with default lua (lib, antidpi, auto)');
T.eq(G.base_options(cfg, 'nfqws2', [ '--lua-init=@/x.lua' ]), [ '--qnum=200', '--user=daemon', '--fwmark=0x40000000' ], 'nfqws2 own lua-init');

/* ---- build over the fixture store ---- */
let idx = S.scan();
let YT = W + '/etc/files/lists/include/list-youtube.txt';
let UH = W + '/etc/user/hosts-include.txt';
let GH = P.guard_hostlist, GI = P.guard_ipset;
let QUIC = W + '/bundle/files/bin/quic_initial_www_google_com.bin';
let GOOG = W + '/bundle/files/bin/tls_clienthello_www_google_com.bin';
let base = [ '--qnum=200', '--user=daemon', '--dpi-desync-fwmark=0x40000000' ];
let NOEX = { exclude_lists: [], exclude_ipsets: [] };
function wcfg(o) {
	for (let k, v in NOEX)
		if (o[k] == null)
			o[k] = v;
	return C.normalize(o, null, null);
}
let wl = wcfg({ lists: [ 'list-youtube', 'user-hosts' ], ipsets: [], strategy: 'strategy-general' });

// manual check 1: strategy-general (hostlists, ipsets, bin, trailing --new)
let b = G.build(wl, { index: idx });
T.ok(b.ok, 'build strategy-general: ' + (b.message ?? ''));
let expect = [];
for (let a in base) push(expect, a);
for (let a in [
	'--filter-udp=443', '--hostlist=' + YT, '--hostlist=' + UH, '--hostlist=' + GH, '--dpi-desync=fake', '--dpi-desync-repeats=6', '--dpi-desync-fake-quic=' + QUIC, '--new',
	'--filter-udp=19294-19344,50000-50100', '--filter-l7=discord,stun', '--dpi-desync=fake', '--dpi-desync-repeats=6', '--new',
	'--filter-tcp=80', '--hostlist=' + YT, '--hostlist=' + UH, '--hostlist=' + GH, '--dpi-desync=fake,multisplit', '--dpi-desync-autottl=2', '--dpi-desync-fooling=md5sig', '--new',
	'--filter-tcp=2053,2083,2087,2096,8443', '--hostlist-domains=discord.media', '--dpi-desync=fake,multidisorder', '--dpi-desync-split-pos=midsld', '--dpi-desync-repeats=8', '--dpi-desync-fooling=md5sig,badseq', '--new',
	'--filter-tcp=443', '--hostlist=' + YT, '--hostlist=' + UH, '--hostlist=' + GH, '--dpi-desync=fake,multidisorder', '--dpi-desync-split-pos=midsld', '--dpi-desync-repeats=8', '--dpi-desync-fooling=md5sig,badseq', '--new',
	'--filter-udp=443', '--ipset=' + GI, '--dpi-desync=fake', '--dpi-desync-repeats=6', '--dpi-desync-fake-quic=' + QUIC, '--new',
	'--filter-tcp=80', '--ipset=' + GI, '--dpi-desync=fake,multisplit', '--dpi-desync-autottl=2', '--dpi-desync-fooling=md5sig' ])
	push(expect, a);
T.eq(b.args, expect, 'strategy-general args (manual check)');
T.eq(b.ports, { tcp: [ '80', '443', '2053', '2083', '2087', '2096', '8443' ], udp: [ '443', '19294-19344', '50000-50100' ] }, 'strategy-general ports');
T.has(b.warnings, 'empty_profile_removed', 'trailing --new reported');
T.eq(b.strategy, { id: 'strategy-general', name: 'strategy-general', source: 'bundle' }, 'strategy info');

// manual check 2: strategy-default (legacy split2/disorder2, hex fake, bins)
b = G.build(wcfg({ lists: [ 'list-youtube' ], strategy: 'strategy-default' }), { index: idx });
expect = [];
for (let a in base) push(expect, a);
for (let a in [
	'--filter-tcp=80', '--dpi-desync=fake,multisplit', '--dpi-desync-autottl=2', '--dpi-desync-fooling=md5sig,badsum', '--hostlist=' + YT, '--hostlist=' + GH, '--new',
	'--filter-tcp=443', '--hostlist=' + YT, '--hostlist=' + GH, '--dpi-desync=fake,multisplit', '--dpi-desync-repeats=6', '--dpi-desync-fooling=md5sig,badsum', '--dpi-desync-fake-tls=' + GOOG, '--new',
	'--filter-tcp=80,443', '--dpi-desync=fake,multidisorder', '--dpi-desync-repeats=6', '--dpi-desync-autottl=2', '--dpi-desync-fooling=md5sig,badsum', '--hostlist=' + YT, '--hostlist=' + GH, '--new',
	'--filter-udp=50000-50100', '--dpi-desync=fake', '--dpi-desync-any-protocol', '--dpi-desync-fake-quic=0xC30000000108', '--new',
	'--filter-udp=443', '--hostlist=' + YT, '--hostlist=' + GH, '--dpi-desync=fake', '--dpi-desync-repeats=6', '--dpi-desync-fake-quic=' + QUIC, '--new',
	'--filter-udp=443', '--dpi-desync=fake', '--dpi-desync-repeats=6', '--hostlist=' + YT, '--hostlist=' + GH ])
	push(expect, a);
T.ok(b.ok, 'build strategy-default');
T.eq(b.args, expect, 'strategy-default args (manual check)');
T.lacks(b.warnings, 'empty_profile_removed', 'no empty profiles in strategy-default');

// manual check 3: strategy-shizapret-port (--comment junk, wide udp range)
b = G.build(wcfg({ lists: [ 'list-youtube' ], strategy: 'strategy-shizapret-port' }), { index: idx });
T.ok(b.ok, 'build strategy-shizapret-port');
for (let junk in [ '--comment', 'Telegram', '(WebRTC)', '[W.I.P.]', 'WireGuard', 'handshake', 'set(TCP' ])
	T.lacks(b.args, junk, 'comment junk removed: ' + junk);
T.eq(length(G.split_profiles(slice(b.args, 3))), 14, 'shizapret: 14 profiles');
T.eq(b.ports.udp, [ '0-65535' ], 'shizapret: udp 0-65535');
T.eq(b.ports.tcp, [ '80', '443', '853', '2053', '2083', '2087', '2096', '8443' ], 'shizapret: tcp ports');
T.has(b.warnings, 'wide_port_range', 'wide port range warning');

// manual check 4: strategy-alt10 (profiles with both ${hostlists} and ${ipsets}, no active ipsets)
b = G.build(wcfg({ lists: [ 'list-youtube' ], strategy: 'strategy-alt10' }), { index: idx });
T.ok(b.ok, 'build strategy-alt10');
let first = G.split_profiles(slice(b.args, 3))[0];
T.eq(first, [ '--filter-udp=443', '--hostlist=' + YT, '--hostlist=' + GH, '--dpi-desync=fake', '--dpi-desync-repeats=6', '--dpi-desync-fake-quic=' + QUIC ],
	'alt10 profile 1: ipset guard not added next to real hostlists');
let last = G.split_profiles(slice(b.args, 3))[6];
T.eq(slice(last, 0, 3), [ '--filter-tcp=80,443', '--hostlist=' + YT, '--hostlist=' + GH ], 'alt10 profile 7 keeps hostlists');

// manual check 5: strategy-alt11 (trailing backslashes)
b = G.build(wcfg({ lists: [ 'list-youtube' ], strategy: 'strategy-alt11' }), { index: idx });
T.ok(b.ok, 'build strategy-alt11');
T.lacks(b.args, '\\', 'alt11: no backslash tokens');
T.eq(length(G.split_profiles(slice(b.args, 3))), 7, 'alt11: 7 profiles');

// blacklist mode
b = G.build(C.normalize({ list_mode: 'blacklist', exclude_lists: [ 'list-exclude-general', 'user-hosts-exclude' ], exclude_ipsets: [ 'user-ipset-exclude' ], strategy: 'strategy-general' }, null, null), { index: idx });
T.ok(b.ok, 'blacklist build');
T.has(b.args, '--hostlist-exclude=' + W + '/bundle/files/lists/exclude/list-exclude-general.txt', 'blacklist exclude list');
T.has(b.args, '--ipset-exclude=' + W + '/etc/user/ipset-exclude.txt', 'blacklist exclude ipset');
T.lacks(b.args, '--hostlist=' + GH, 'blacklist has no guard');

// warnings and errors
b = G.build(C.normalize({ lists: [], strategy: 'strategy-general' }, null, null), { index: idx });
T.has(b.warnings, 'no_active_lists', 'no_active_lists warning');
b = G.build(C.normalize({ lists: [ 'no-such-list', 'list-youtube' ], strategy: 'strategy-general' }, null, null), { index: idx });
T.eq([ b.error, b.missing_lists ], [ 'item_not_found', [ 'no-such-list' ] ], 'missing regular list is an error (contract v1.1)');
b = G.build(C.normalize({ lists: [ 'src-never', 'src-demo', 'list-youtube' ], strategy: 'strategy-general' }, null, null), { index: idx });
T.ok(b.ok, 'missing subscription does not fail the build');
T.has(b.warnings, 'source_not_downloaded', 'source_not_downloaded warning');
T.eq(b.details.sources_not_downloaded, [ 'src-never' ], 'not downloaded subscription reported');
T.has(b.args, '--hostlist=' + W + '/etc/files/lists/include/src-demo.txt', 'downloaded subscription used like a list');
// profiles acting on all traffic of their ports
b = G.build(wcfg({ lists: [ 'list-youtube' ], strategy: 'strategy-general' }), { index: idx });
T.eq(b.details.unfiltered_profiles, [ { profile: 2, tcp: [], udp: [ '19294-19344', '50000-50100' ] } ], 'profile_unfiltered details');
T.has(b.warnings, 'profile_unfiltered', 'profile_unfiltered warning');
b = G.build(C.normalize({ list_mode: 'blacklist', exclude_lists: [], exclude_ipsets: [], strategy: 'strategy-general' }, null, null), { index: idx });
T.lacks(b.warnings, 'profile_unfiltered', 'no profile_unfiltered warning in blacklist mode');
T.eq(G.unfiltered_profiles([ '--filter-tcp=443', '--hostlist-domains=a.com', '--new', '--filter-udp=443', '--new', '--skip', '--filter-tcp=80' ]),
	[ { profile: 2, tcp: [], udp: [ '443' ] } ], 'unfiltered_profiles: filtered, unfiltered and skipped profiles');
// whitelist with exclusions in real builds
b = G.build(C.normalize({ lists: [ 'list-youtube' ], exclude_lists: [ 'zaprett-exclude' ], exclude_ipsets: [ 'zaprett-exclude-ipset' ], strategy: 'strategy-general' }, null, null), { index: idx });
T.has(b.args, '--hostlist-exclude=' + W + '/bundle/files/lists/exclude/zaprett-exclude.txt', 'whitelist build has hostlist exclusions');
T.has(b.args, '--ipset-exclude=' + W + '/bundle/files/ipset/exclude/zaprett-exclude-ipset.txt', 'whitelist build has ipset exclusions');
T.eq(G.build(C.normalize({ strategy: 'no-such-strategy' }, null, null), { index: idx }).error, 'strategy_not_found', 'unknown strategy');
T.eq(G.build(cfg, { index: idx, text: '  \n\\\n', item: { id: 'user-x', source: 'user', dependencies: [] } }).error, 'strategy_empty', 'empty strategy');
T.eq(G.build(cfg, { index: idx, text: '--filter-tcp=443 --dpi-desync-fake-tls=${bin:not_there}', item: { id: 'user-x', source: 'user', dependencies: [] } }).error,
	'item_not_installed', 'user strategy with missing bin');
b = G.build(cfg, { index: idx, strategy: 'user-test' });
T.ok(b.ok, 'user strategy from /etc/zaprett/user/strategies');
T.has(b.args, '--dpi-desync-fake-tls=' + W + '/bundle/files/bin/tls_clienthello_vk_com.bin', 'user strategy resolves any installed bin');
T.ok(idx.items.nfqws['not-user'] == null, 'user strategy without user- prefix ignored');
T.eq(G.build(cfg, { index: idx, text: '--filter-tcp=443 --dpi-desync=fake --dpi-desync-fake-tls=/etc/shadow', item: { id: 'user-x', source: 'user', dependencies: [] } }).error,
	'path_not_allowed', 'build rejects a strategy reading /etc/shadow');
// ZERR-033: every file and reserved option is checked the same in its full and in any other form the engine accepts
let ux = { id: 'user-x', source: 'user', dependencies: [] };
let bt = (text, engine) => G.build(cfg, { index: idx, engine: engine, text: text, item: ux });
// shortest prefix that the engine resolves to this name only (null when the name is a prefix of another option)
function short_form(name, known) {
	for (let n = 1; n < length(name); n++)
		if (G.resolve_option(substr(name, 0, n), known).name == name)
			return substr(name, 0, n);
	return null;
}
const OUTSIDE = '/etc/zaprettX/f';
let file_value = (name) => (name == 'blob') ? 'x:@' + OUTSIDE : (name == 'lua-init') ? '@' + OUTSIDE : OUTSIDE;
let forms_checked = 0;
for (let engine in [ 'nfqws', 'nfqws2' ]) {
	let known = ENGINE_OPTIONS[engine];
	let tail = (engine == 'nfqws') ? ' --dpi-desync=fake' : ' --lua-desync=fake';
	for (let name in G.FILE_OPTIONS) {
		if (known[name] == null)
			continue;
		let v = file_value(name);
		let sf = short_form(name, known);
		let forms = [ '--' + name + '=' + v, '-' + name + '=' + v, '--' + name + ' ' + v ];
		if (sf)
			push(forms, '--' + sf + '=' + v, '-' + sf + ' ' + v);
		for (let f in forms) {
			T.eq(bt('--filter-tcp=443 ' + f + tail, engine).error, 'path_not_allowed', sprintf('%s: file option outside zaprett directories rejected: %s', engine, f));
			forms_checked++;
		}
	}
	for (let name in G.RESERVED_OPTIONS) {
		if (known[name] == null)
			continue;
		let v = (known[name] == 'no') ? '' : '=1';
		let sf = short_form(name, known);
		for (let f in [ '--' + name + v, '-' + name + v, ...(sf ? [ '--' + sf + v ] : []), ...((known[name] == 'required') ? [ '--' + name + ' 1' ] : []) ]) {
			let r = bt('--filter-tcp=443 ' + f + tail, engine);
			// after the base options (nfqws: 3, nfqws2: 3 + 3 default --lua-init) only the strategy itself is left
			T.eq([ r.ok, r.details?.ignored_options, slice(r.args ?? [], (engine == 'nfqws') ? 3 : 6) ], [ true, [ name ], [ '--filter-tcp=443', trim(tail) ] ],
				sprintf('%s: reserved option removed and reported: %s', engine, f));
			forms_checked++;
		}
	}
}
T.ok(forms_checked > 150, sprintf('option forms checked: %d', forms_checked));
T.eq(bt('--filter-tcp=443 --dpi-desync=fake --dpi-desync-fake-tls=' + W + '/bundle/files/bin/tls_clienthello_www_google_com.bin', 'nfqws').ok, true,
	'negative control: a file option inside zaprett directories passes');
b = bt('--filter-tcp=443 --dpi-desync=fake --debu=@/tmp/log --qnum 7 -us root --dpi-desync-fw 0x1', 'nfqws');
T.ok(b.ok, 'build with abbreviated reserved options: ' + (b.message ?? ''));
T.eq(slice(b.args, 3), [ '--filter-tcp=443', '--dpi-desync=fake' ], 'reserved options removed with their separate values');
T.eq(b.details?.ignored_options, [ 'debug', 'qnum', 'user', 'dpi-desync-fwmark' ], 'abbreviated reserved options reported by full name');
b = bt('--filter-tcp=443 --lua-desync=fake --fwm 0x1 --wri --chdi=/ --fuz=1', 'nfqws2');
T.eq([ b.ok, slice(b.args ?? [], 3), b.details?.ignored_options ], [ true, [ '--lua-init=@' + P.share + '/lua/zapret-lib.lua', '--lua-init=@' + P.share + '/lua/zapret-antidpi.lua',
	'--lua-init=@' + P.share + '/lua/zapret-auto.lua', '--filter-tcp=443', '--lua-desync=fake' ], [ 'fwmark', 'writable', 'chdir', 'fuzz' ] ], 'nfqws2 reserved options by prefix');
T.eq(bt('--filter-tcp=443 --dpi-desync=fake stray', 'nfqws').error, 'bad_option', 'build rejects a word that is not an option');
T.eq(bt('--filter-tcp=443 --hostl=/etc/zaprett/x --dpi-desync=fake', 'nfqws').error, 'bad_option', 'build rejects an ambiguous prefix');
b = bt('-filter-tcp 443 ${hostlists} -dpi-desync fake,split2 --dpi-desync-rep 6', 'nfqws');
T.ok(b.ok, 'abbreviated legit strategy builds: ' + (b.message ?? ''));
T.eq([ b.ports.tcp, slice(b.args, -2) ], [ [ '443' ], [ '--dpi-desync=fake,multisplit', '--dpi-desync-repeats=6' ] ],
	'abbreviated strategy: ports from the separate value, legacy mode normalized, full names');

// all fixture strategies (64 from zaprett-repo)
let ids = sort(filter(keys(idx.items.nfqws), (id) => idx.items.nfqws[id].source == 'bundle'));
T.eq(length(ids), 64, 'fixture has 64 nfqws strategies');
let built = 0, dry_ok = 0, dry_fail = [];
for (let id in ids) {
	let r = G.build(C.normalize({ lists: [ 'list-youtube', 'user-hosts' ], ipsets: [ 'ipset-roblox' ], strategy: id }, null, null), { index: idx });
	if (!r.ok) {
		T.ok(false, sprintf('build %s: %s %s', id, r.error, r.message));
		continue;
	}
	built++;
	T.ok(length(filter(r.args, (a) => index(a, '${') >= 0 || a == '\\')) == 0, 'no placeholders left: ' + id);
	if (T.NFQWS) {
		let d = G.dry_run('nfqws', r.args);
		if (d.rc == 0 && index(d.output, 'command line parameters verified') >= 0)
			dry_ok++;
		else
			push(dry_fail, id + ': ' + d.output);
	}
}
T.eq(built, 64, 'all 64 strategies build');
if (T.NFQWS) {
	T.eq(dry_ok, 64, 'nfqws --dry-run accepts all 64 strategies');
	T.eq(dry_fail, [], 'dry-run failures');
	// negative controls for dry-run
	let good = G.build(wl, { index: idx });
	let bad1 = slice(good.args);
	push(bad1, '--dpi-desync=bogusmode');
	T.ok(G.dry_run('nfqws', bad1).rc != 0, 'dry-run rejects bogus desync mode');
	let bad2 = slice(good.args);
	push(bad2, '--hostlist=' + W + '/does-not-exist.txt');
	T.ok(G.dry_run('nfqws', bad2).rc != 0, 'dry-run rejects missing hostlist file');
	let bad3 = slice(good.args);
	push(bad3, '--dpi-desync-foobar=1');
	T.ok(G.dry_run('nfqws', bad3).rc != 0, 'dry-run rejects unknown option');
	T.eq(G.generate(wl, { index: idx, ignore_override: true }).dry_run?.rc, 0, 'generate() runs dry-run');
	let crlf_build = G.generate(wl, { index: idx, ignore_override: true, text: crlf_text, item: { id: 'user-crlf', source: 'user', dependencies: [] } });
	T.ok(crlf_build.ok && crlf_build.dry_run.rc == 0, 'CRLF user strategy passes nfqws --dry-run: ' + (crlf_build.message ?? ''));
	// the table agrees with the engine: what resolve_option() resolves the engine parses, what it calls ambiguous the engine refuses
	let rd = G.dry_run('nfqws', [ '--qnum=200', '--filter-tcp=443', '-dpi-desync', 'fake', '--dpi-desync-fake-qu', QUIC, '-hostlist=' + YT ]);
	T.ok(rd.rc == 0 && index(rd.output, 'command line parameters verified') >= 0, 'nfqws parses the forms canonicalize() resolves: ' + rd.output);
	T.ok(G.dry_run('nfqws', [ '--qnum=200', '--hostl=' + YT ]).rc != 0, 'nfqws refuses a prefix that resolve_option() calls ambiguous');
	T.ok(G.dry_run('nfqws', [ '--qnum=200', '--dpi-desync-fake-qu', W + '/does-not-exist.bin' ]).rc != 0,
		'nfqws takes the next word as the value of a required option (as canonicalize() does)');
	let cb = G.generate(wl, { index: idx, ignore_override: true, item: ux,
		text: '-filter-tcp 443 ${hostlists} -dpi-desync fake,split2 --dpi-desync-fake-qu ' + QUIC });
	T.ok(cb.ok && cb.dry_run.rc == 0 && index(cb.args, '--dpi-desync-fake-quic=' + QUIC) >= 0, 'canonical arguments pass nfqws --dry-run: ' + (cb.message ?? ''));
}
else
	print('SKIP [strategy] nfqws binary not given: dry-run checks skipped\n');
let miss = G.dry_run('nfqws2', []);
T.ok(miss.missing && miss.rc == -1, 'dry-run reports missing engine');

let ah = G.generate(cfg, { index: idx, ignore_override: true, skip_dry_run: true, item: { id: 'user-ah', source: 'user', dependencies: [] },
	text: '--filter-tcp=443 --hostlist-auto=' + G.autohostlist_dir() + '/auto.txt --dpi-desync=fake' });
let ahst = fs.stat(G.autohostlist_dir());
T.ok(ah.ok && ahst?.type == 'directory' && ahst.uid == 1, 'autohostlist directory created and owned by the engine user (daemon, uid 1)');

/* ---- override and outputs ---- */
T.ok(G.read_override() == null, 'no override');
T.write(P.run + '/test-override', '{"engine":"nfqws","strategy":"../x"}');
T.ok(G.read_override() == null, 'invalid override ignored');
T.write(P.run + '/test-override', '{"engine":"nfqws","strategy":"strategy-alt"}');
let g = G.generate(wl, { index: idx, skip_dry_run: true });
T.ok(g.ok && g.strategy.id == 'strategy-alt' && g.test_mode, 'generate() follows test override');
g = G.generate(wl, { index: idx, skip_dry_run: true, ignore_override: true });
T.eq(g.strategy.id, 'strategy-general', 'ignore_override');
fs.unlink(P.run + '/test-override');
T.ok(G.write_outputs(g), 'write_outputs');
T.eq(split(trim(fs.readfile(P.run + '/args')), '\n'), g.args, 'args file: one argument per line');
T.eq(trim(fs.readfile(P.run + '/engine')), 'nfqws', 'engine file');
T.eq(json(fs.readfile(P.run + '/ports.json')), g.ports, 'ports.json');

// ensure_user_files creates missing user lists
fs.unlink(W + '/etc/user/hosts-exclude.txt');
G.ensure_user_files(C.normalize({ exclude_lists: [ 'user-hosts-exclude' ] }, null, null));
T.ok(fs.stat(W + '/etc/user/hosts-exclude.txt')?.type == 'file', 'missing user list recreated');

exit(T.finish());
