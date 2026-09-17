'use strict';

import * as T from 'ztest';
import * as V from 'zaprett.validate';
import * as C from 'zaprett.config';
import { is_id } from 'zaprett.util';

T.begin('validate');
T.selfcheck();

// ids
for (let id in [ 'strategy-general', 'quic_initial_www_google_com', 'user-hosts', 'a', 'list.v2' ])
	T.ok(is_id(id), 'id valid: ' + id);
for (let id in [ '', '.hidden', '..', 'a b', 'a/b', 'x;rm', sprintf('%097d', 1), '$(id)', 'ид' ])
	T.ok(!is_id(id), 'id invalid: ' + id);

// domains
for (let d in [ 'youtube.com', 'rr1---sn-abc.googlevideo.com', '^exact.example.org', 'xn--p1ai', 'ru', '_dmarc.example.com', '1.2.3.4', '2001:db8::1' ])
	T.ok(V.domain_valid(d), 'domain valid: ' + d);
for (let d in [ '', '*.youtube.com', 'youtube.com.', '-bad.com', 'bad-.com', 'a..b', 'Upper.com', 'sp ace.com', 'юникод.рф', sprintf('%064d.com', 1) ])
	T.ok(!V.domain_valid(d), 'domain invalid: ' + d);

// the fast label check must agree with the per-label regex it replaced (differential test)
function reference_domain_valid(s) {
	if (type(s) != 'string')
		return false;
	let d = (substr(s, 0, 1) == '^') ? substr(s, 1) : s;
	if (d == '' || length(d) > 253)
		return false;
	if (V.ipv4_valid(d) || V.ipv6_valid(d))
		return true;
	for (let l in split(d, '.'))
		if (l == '' || length(l) > 63 || !match(l, /^[a-z0-9_]([a-z0-9_-]*[a-z0-9_])?$/))
			return false;
	return true;
}
let samples = [ 'a', '-', '_', '.', 'a.', '.a', 'a-', '-a', 'a-b', 'a_b', '_a_', 'a.-b', 'a-.b', 'a--b.c', 'a..b', '^a.b', '^', '^^a',
	'^.a', '1.2.3.4', '999.1.1.1', '1.2.3', '::1', 'fe80::1', '2001:db8::', 'a:b', 'a b', 'a\tb', 'A.b', 'a.B', 'ya.ru\r',
	sprintf('%063d', 7), sprintf('%064d', 7), sprintf('%063d.%063d.%063d.%061d', 1, 2, 3, 4), sprintf('%063d.%063d.%063d.%062d', 1, 2, 3, 4),
	'a.' + sprintf('%064d', 1) + '.b', sprintf('%064d', 1) + '.' + sprintf('%0200d', 2), 'xn--80ak6aa92e.com', 'rr5---sn-n8v7kn7r.googlevideo.com',
	'a/b', 'a*b', 'a,b', 'a;b', 'ab!', 'пример', 'a' + chr(0xc3, 0xa9), null, 5 ];
let chars = [ 'a', 'z', '0', '9', '-', '_', '.', '^', ':', 'A' ];
let seed = 12345;
for (let i = 0; i < 3000; i++) {
	let s = '', len = 1 + (i % 12);
	for (let k = 0; k < len; k++) {
		seed = (seed * 1103515245 + 12345) % 2147483648;
		s += chars[seed % length(chars)];
	}
	push(samples, s);
}
let disagree = filter(samples, (s) => V.domain_valid(s) != reference_domain_valid(s));
T.eq(disagree, [], sprintf('domain_valid agrees with the reference on %d samples', length(samples)));
T.ok(length(filter(samples, (s) => reference_domain_valid(s))) > 100 && length(filter(samples, (s) => !reference_domain_valid(s))) > 100,
	'the samples contain both valid and invalid names');
T.ok(V.hostname_labels_valid('a-b.c') && !V.hostname_labels_valid('a.-b'), 'hostname_labels_valid basic');

/* ---- length bounds moved from regex intervals to length(): behaviour must be identical ---- */
// A regex interval like {1,96} costs milliseconds per call in ucode, so every bound above ~16 was rewritten
// as a length() check plus an unbounded character class. Here the new predicates are compared with the
// original regexes on the same samples, boundary lengths included.
let bound_samples = [ '', 'a', 'A', '0', '_', '-', '.', '@', ':', ' ', 'a b', 'a\tb', 'a\nb', 'пример', 'a' + chr(0xff),
	'0x', '0X1', '0xFFFFFFFF', '0xfffffffff', '4294967295', '4294967296', '0123456789', '999999999', '1000000000',
	'1.0.0', '1.0.0~rc1', '2026.09.17', '-1.0', '.1.0', '1.0!', 'v1.0+build_1', 'daemon', 'root', 'Root', '_x-1', 'eth0.2',
	'br-lan', 'wan@if', 'a/b', '192.168.1.1', '10.1.2.3', '255.255.255.255', '192.168.001.1', '1.2.3', '1.2.3.4.5',
	'256.1.1.1', '0.0.0.0', '1.02.3.4', '01.2.3.4', '1.2.3.04', '1000.1.1.1', '10.0.0.0/8',
	'10.0.0.0/33', '10.0.0.0/008', '10.0.0.0/', '::1', '2001:db8::1', '2001:db8::12345', 'fe80::1%eth0', 'ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff' ];
for (let n in [ 1, 15, 16, 17, 31, 32, 33, 63, 64, 65, 95, 96, 97, 128 ]) {
	push(bound_samples, sprintf('%0' + n + 'd', 1));				// digits
	push(bound_samples, substr('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 0, n));
	push(bound_samples, substr('0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef', 0, n));
	push(bound_samples, 'x' + substr('.-_@:!', 0, 1) + substr('bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', 0, n > 2 ? n - 2 : 0));
}
let pairs = [
	[ 'is_id', (s) => is_id(s), (s) => type(s) == 'string' && substr(s, 0, 1) != '.' && match(s, /^[A-Za-z0-9._-]{1,96}$/) != null ],
	[ 'version_valid', (s) => V.version_valid(s), (s) => type(s) == 'string' && match(s, /^[0-9A-Za-z][0-9A-Za-z.+~_-]{0,31}$/) != null ],
	[ 'sha256_valid', (s) => V.sha256_valid(s), (s) => type(s) == 'string' && match(s, /^[0-9a-f]{64}$/) != null ],
	[ 'iface_name_valid', (s) => V.iface_name_valid(s), (s) => type(s) == 'string' && match(s, /^[A-Za-z0-9_.@-]{1,32}$/) != null ],
	[ 'user_name_valid', (s) => V.user_name_valid(s), (s) => type(s) == 'string' && match(s, /^[a-z_][a-z0-9_-]{0,31}$/) != null ],
	[ 'source_name_valid', (s) => C.source_name_valid(s), (s) => type(s) == 'string' && match(s, /^[a-z0-9_]{1,32}$/) != null ],
	[ 'ipv6 group', (s) => (length(s) >= 1 && length(s) <= 4 && match(s, /^[0-9a-fA-F]+$/) != null),
		(s) => match(s, /^[0-9a-fA-F]{1,4}$/) != null ],
	[ 'cidr prefix', (s) => (length(s) >= 1 && length(s) <= 3 && match(s, /^[0-9]+$/) != null),
		(s) => match(s, /^[0-9]{1,3}$/) != null ],
	[ 'mark hex', (s) => (match(s, /^0[xX]/) != null && length(s) >= 3 && length(s) <= 10 && match(substr(s, 2), /^[0-9a-fA-F]+$/) != null),
		(s) => match(s, /^0[xX][0-9a-fA-F]{1,8}$/) != null ],
	[ 'mark dec', (s) => (type(s) == 'string' && length(s) >= 1 && length(s) <= 10 && match(s, /^[0-9]+$/) != null),
		(s) => match(s, /^[0-9]{1,10}$/) != null ],
	[ 'uint digits', (s) => (type(s) == 'string' && length(s) >= 1 && length(s) <= 9 && match(s, /^[0-9]+$/) != null),
		(s) => match(s, /^[0-9]{1,9}$/) != null ]
];
for (let p in pairs) {
	let differ = filter(bound_samples, (s) => !!p[1](s) != !!p[2](s));
	T.eq(differ, [], sprintf('%s: length() check equals the old regex interval on %d samples', p[0], length(bound_samples)));
}
// the same for the parsers that used intervals inside
function ref_ipv4_parse(s) {
	if (type(s) != 'string' || !match(s, /^[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}$/))
		return null;
	let r = [];
	for (let p in split(s, '.')) {
		if (length(p) > 1 && substr(p, 0, 1) == '0')
			return null;
		let n = int(p);
		if (n > 255)
			return null;
		push(r, n);
	}
	return r;
}
let v4differ = filter(bound_samples, (s) => sprintf('%J', V.ipv4_parse(s)) != sprintf('%J', ref_ipv4_parse(s)));
T.eq(v4differ, [], 'ipv4_parse unchanged by the rewrite');
T.ok(length(filter(bound_samples, (s) => ref_ipv4_parse(s) != null)) >= 3, 'the samples contain valid IPv4 addresses');
T.eq([ V.parse_mark('0x40000000'), V.parse_mark('0xfffffffff'), V.parse_mark('4294967295'), V.parse_mark('4294967296'),
	V.parse_mark('0'), V.parse_mark('0x0') ], [ 1073741824, null, 4294967295, null, null, null ], 'parse_mark bounds kept');
T.eq([ V.parse_uint('999999999', 0, 1000000000), V.parse_uint('1000000000', 0, 2000000000), V.parse_uint('', 0, 1) ],
	[ 999999999, null, null ], 'parse_uint bounds kept (9 digits max)');

// IPv4 / IPv6 / CIDR
T.eq(V.ipv4_parse('192.168.1.1'), [ 192, 168, 1, 1 ], 'ipv4 parse');
for (let a in [ '256.1.1.1', '1.2.3', '01.2.3.4', '1.2.3.4.5', 'a.b.c.d' ])
	T.ok(V.ipv4_parse(a) == null, 'ipv4 invalid: ' + a);
for (let a in [ '::1', '2001:db8::', 'fe80::1:2', '::ffff:192.0.2.1', '1:2:3:4:5:6:7:8', '::' ])
	T.ok(V.ipv6_valid(a), 'ipv6 valid: ' + a);
for (let a in [ ':::', '1::2::3', '1:2:3:4:5:6:7:8:9', 'g::1', '12345::', '1:2:3:4:5:6:7' ])
	T.ok(!V.ipv6_valid(a), 'ipv6 invalid: ' + a);
T.eq(V.cidr_parse('192.168.1.77/24')?.net, '192.168.1.0/24', 'cidr host bits masked');
T.eq(V.cidr_parse('10.1.2.3')?.net, '10.1.2.3', 'single ipv4');
T.eq(V.cidr_parse('0.0.0.0/0')?.net, '0.0.0.0/0', 'default route cidr');
T.eq(V.cidr_parse('2001:DB8::/32')?.net, '2001:db8::/32', 'ipv6 cidr lower-cased');
for (let c in [ '10.0.0.0/33', '10.0.0.0/', '10.0.0.0/8/1', '::/129', 'x/8' ])
	T.ok(V.cidr_parse(c) == null, 'cidr invalid: ' + c);

// MAC
T.eq(V.mac_normalize('AA-bb-CC-01-02-03'), 'aa:bb:cc:01:02:03', 'mac normalized');
T.ok(V.mac_normalize('aa:bb:cc:01:02') == null, 'mac too short');

// marks and numbers
T.eq(V.parse_mark('0x40000000'), 1073741824, 'hex mark');
T.eq(V.parse_mark('536870912'), 536870912, 'decimal mark');
for (let m in [ '0', '0x0', '0x100000000', '-1', '0xZZ', '', '99999999999' ])
	T.ok(V.parse_mark(m) == null, 'mark invalid: ' + m);
T.eq(V.parse_uint('200', 0, 65535), 200, 'uint');
T.ok(V.parse_uint('65536', 0, 65535) == null, 'uint over max');
T.ok(V.parse_uint('1e3', 0, 65535) == null, 'uint not decimal');

// user lists: hosts
let r = V.validate_list_text('hosts', chr(0xef, 0xbb, 0xbf) + 'YouTube.com\r\n# comment\r\n\r\n  googlevideo.com  \n^exact.org\n');
T.ok(r.ok, 'hosts list with BOM/CRLF/spaces/uppercase accepted');
T.eq(r.text, 'youtube.com\n# comment\ngooglevideo.com\n^exact.org\n', 'hosts list normalized');
T.eq(r.entries, 3, 'hosts entries');
r = V.validate_list_text('hosts', 'good.com\n*.bad.com\nbad domain.com\nalso-good.net\n');
T.ok(!r.ok, 'hosts list with bad lines rejected');
T.eq(map(r.errors, (e) => e.line), [ 2, 3 ], 'bad line numbers');
r = V.validate_list_text('hosts', sprintf('%1048577s', 'x'));
T.ok(!r.ok && r.errors[0].reason == 'too_large', 'hosts list size limit');
// user lists: ipset
r = V.validate_list_text('ipset', '10.0.0.1/8\n192.168.1.5\n2001:db8::/32\n#c\n');
T.ok(r.ok, 'ipset list accepted');
T.eq(r.text, '10.0.0.0/8\n192.168.1.5\n2001:db8::/32\n#c\n', 'ipset list normalized');
r = V.validate_list_text('ipset', 'example.com\n1.2.3.4/40\n');
T.eq(r.error_count, 2, 'ipset list rejects domains and bad prefixes');

T.eq(V.count_entries('a.com\n#x\n;y\n/z\n\nb.com\n'), 2, 'count_entries skips nfqws comments');

// versions
let cases = [ [ '1.0.0', '1.0.0', 0 ], [ '1.0.1', '1.0.0', 1 ], [ '1.0', '1.0.1', -1 ], [ '1.10.0', '1.9.9', 1 ],
	[ '2026.09.17', '2026.9.17', 0 ], [ '1.0.0-r2', '1.0.0-r1', 1 ], [ '1.0.0', '1.0.0-rc1', 1 ], [ '0', '0.0.1', -1 ] ];
for (let c in cases)
	T.eq(V.version_cmp(c[0], c[1]), c[2], sprintf('version_cmp %s %s', c[0], c[1]));
T.ok(V.version_valid('1.0.0') && V.version_valid('2026.09.17') && !V.version_valid('1 0') && !V.version_valid(''), 'version_valid');

// ports
T.eq(V.parse_port_filter('80,443'), { ok: true, negated: false, ranges: [ [ 80, 80 ], [ 443, 443 ] ] }, 'ports list');
T.eq(V.parse_port_filter('50000-65535').ranges, [ [ 50000, 65535 ] ], 'port range');
T.ok(V.parse_port_filter('~80').negated, 'negated port');
for (let p in [ '', '65536', '10-5', 'a', '80,', '1-2-3' ])
	T.ok(!V.parse_port_filter(p).ok, 'port filter invalid: ' + p);
T.eq(V.merge_ranges([ [ 443, 443 ], [ 80, 80 ], [ 50000, 50100 ], [ 50050, 65535 ], [ 81, 81 ], [ 0, 65535 ] ]), [ [ 0, 65535 ] ], 'merge to full range');
T.eq(V.ranges_to_strings(V.merge_ranges([ [ 443, 443 ], [ 80, 80 ], [ 81, 90 ], [ 19294, 19344 ], [ 50000, 50100 ] ])),
	[ '80-90', '443', '19294-19344', '50000-50100' ], 'merge and stringify');

// urls
for (let u in [ 'https://raw.githubusercontent.com/CherretGit/zaprett-repo/refs/heads/main/index.json', 'https://www.youtube.com/', 'http://a.b:8080/x?y=1&z=%20' ])
	T.ok(V.url_valid(u), 'url valid: ' + u);
for (let u in [ 'ftp://x.org/', 'https://', 'https://a.b/ c', 'https://a.b/"x', 'https://a.b/\\x', 'file:///etc/passwd', 'https://a.b/`id`', 'https://a.b/\nx' ])
	T.ok(!V.url_valid(u), 'url invalid: ' + u);

exit(T.finish());
