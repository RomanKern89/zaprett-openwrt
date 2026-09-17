// zaprett: pure validation and parsing helpers (no I/O). Covered by tests/test_validate.uc.
'use strict';

export const MAX_USER_LIST_BYTES = 1048576;

// ucode encodes "\xHH" escapes above 0x7f as UTF-8, so raw bytes are built with chr()
const UTF8_BOM = chr(0xef, 0xbb, 0xbf);

function strip_bom(s) {
	return (substr(s, 0, 3) == UTF8_BOM) ? substr(s, 3) : s;
}

// Все границы длины в этом модуле проверяются через length(), а не интервалом `{n,m}` в регулярке:
// ucode компилирует регулярку на каждый вызов, и интервал стоит дорого (см. util.is_id). В горячем пути
// (нормализация подписки — проверка CIDR и домена на каждую строку) это давало 0,074 мс на строку IPv4
// и 0,35 мс на строку IPv6, то есть десятки секунд на большом списке.
function digits_only(s, min_len, max_len) {
	return type(s) == 'string' && length(s) >= min_len && length(s) <= max_len && match(s, /^[0-9]+$/) != null;
}

export function ipv4_parse(s) {
	if (type(s) != 'string' || !match(s, /^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$/))
		return null;
	let r = [];
	for (let p in split(s, '.')) {
		if (length(p) > 3)
			return null;
		if (length(p) > 1 && substr(p, 0, 1) == '0')
			return null;
		let n = int(p);
		if (n > 255)
			return null;
		push(r, n);
	}
	return r;
};

export function ipv4_valid(s) {
	return ipv4_parse(s) != null;
};

// Returns 8 groups (integers) or null.
export function ipv6_parse(s) {
	if (type(s) != 'string' || length(s) < 2 || length(s) > 45 || !match(s, /^[0-9a-fA-F:.]+$/))
		return null;
	let head = s, tail = null;
	let dc = index(s, '::');
	if (dc >= 0) {
		head = substr(s, 0, dc);
		tail = substr(s, dc + 2);
		if (index(tail, '::') >= 0)
			return null;
	}
	function groups(part, allow_v4) {
		if (part == '')
			return [];
		let out = [];
		let items = split(part, ':');
		for (let i = 0; i < length(items); i++) {
			let g = items[i];
			if (allow_v4 && i == length(items) - 1 && index(g, '.') >= 0) {
				let v4 = ipv4_parse(g);
				if (!v4)
					return null;
				push(out, v4[0] * 256 + v4[1], v4[2] * 256 + v4[3]);
				continue;
			}
			if (length(g) < 1 || length(g) > 4 || !match(g, /^[0-9a-fA-F]+$/))
				return null;
			push(out, hex(g));
		}
		return out;
	}
	let hg, tg;
	if (tail == null) {
		hg = groups(head, true);
		if (hg == null || length(hg) != 8)
			return null;
		return hg;
	}
	hg = groups(head, false);
	tg = groups(tail, true);
	if (hg == null || tg == null || length(hg) + length(tg) > 7)
		return null;
	let r = [];
	for (let g in hg)
		push(r, g);
	for (let i = length(hg) + length(tg); i < 8; i++)
		push(r, 0);
	for (let g in tg)
		push(r, g);
	return r;
};

export function ipv6_valid(s) {
	return ipv6_parse(s) != null;
};

// Labels of a host name: 1..63 characters of [a-z0-9_-], not starting or ending with '-'. Written without
// per-label regexes: lists have hundreds of thousands of lines and this check is ~6 times faster
// (52000 names: 1.6 s instead of 9.8 s on the x86 lab VM).
export function hostname_labels_valid(d) {
	let n = length(d);
	if (n == 0 || n > 253 || !match(d, /^[a-z0-9_.-]+$/))
		return false;
	let first = substr(d, 0, 1), last = substr(d, n - 1);
	if (first == '.' || first == '-' || last == '.' || last == '-' ||
	    index(d, '..') >= 0 || index(d, '.-') >= 0 || index(d, '-.') >= 0)
		return false;
	if (n > 63)
		for (let l in split(d, '.'))
			if (length(l) > 63)
				return false;
	return true;
};

// Hostname as nfqws matches it: lower-case labels of letters, digits, '-' and '_'.
// A leading '^' (strict match, no subdomains) is accepted. IPv4/IPv6 literals are accepted too.
export function domain_valid(s) {
	if (type(s) != 'string')
		return false;
	let d = (substr(s, 0, 1) == '^') ? substr(s, 1) : s;
	if (d == '' || length(d) > 253)
		return false;
	return hostname_labels_valid(d) || ipv4_valid(d) || ipv6_valid(d);
};

// IPv4 address or network in CIDR form. Returns { family, addr, prefix, net } or null.
export function cidr_parse(s) {
	if (type(s) != 'string')
		return null;
	let addr = s, prefix = null;
	let slash = index(s, '/');
	if (slash >= 0) {
		addr = substr(s, 0, slash);
		let p = substr(s, slash + 1);
		// проверка встроена, а не через digits_only(): на коротких строках вызов функции стоит дороже
		// самой проверки, а cidr_parse вызывается на каждую строку ipset-подписки
		if (length(p) < 1 || length(p) > 3 || !match(p, /^[0-9]+$/))
			return null;
		prefix = int(p);
	}
	let v4 = ipv4_parse(addr);
	if (v4) {
		if (prefix == null)
			prefix = 32;
		if (prefix > 32)
			return null;
		let num = ((v4[0] * 16777216) + (v4[1] * 65536) + (v4[2] * 256) + v4[3]);
		let mask = (prefix == 0) ? 0 : ((0xffffffff << (32 - prefix)) & 0xffffffff);
		let net = num & mask;
		let net_s = sprintf('%d.%d.%d.%d', (net >> 24) & 255, (net >> 16) & 255, (net >> 8) & 255, net & 255);
		return { family: 4, addr: addr, prefix: prefix, net: (prefix == 32) ? net_s : sprintf('%s/%d', net_s, prefix) };
	}
	let v6 = ipv6_parse(addr);
	if (v6) {
		if (prefix == null)
			prefix = 128;
		if (prefix > 128)
			return null;
		return { family: 6, addr: lc(addr), prefix: prefix, net: (prefix == 128) ? lc(addr) : sprintf('%s/%d', lc(addr), prefix) };
	}
	return null;
};

export function mac_normalize(s) {
	if (type(s) != 'string')
		return null;
	let m = lc(replace(s, '-', ':'));
	return match(m, /^[0-9a-f]{2}(:[0-9a-f]{2}){5}$/) ? m : null;
};

// Parses a mark value: decimal or 0xHEX, non-zero, fits 32 bits.
export function parse_mark(s) {
	if (type(s) == 'int')
		s = '' + s;
	if (type(s) != 'string')
		return null;
	let n = null;
	if (match(s, /^0[xX]/) && length(s) >= 3 && length(s) <= 10 && match(substr(s, 2), /^[0-9a-fA-F]+$/))
		n = hex(s);
	else if (digits_only(s, 1, 10))
		n = int(s);
	if (n == null || n <= 0 || n > 4294967295)
		return null;
	return n;
};

export function parse_uint(s, min, max) {
	if (type(s) == 'int')
		s = '' + s;
	if (!digits_only(s, 1, 9))
		return null;
	let n = int(s);
	return (n < min || n > max) ? null : n;
};

// Validates text of a user list. kind: 'hosts' or 'ipset'.
// Returns { ok, text, entries, errors: [ { line, value, reason } ] }. Comments ('#') are kept.
export function validate_list_text(kind, text, max_bytes) {
	if (type(text) != 'string')
		return { ok: false, text: '', entries: 0, errors: [ { line: 0, value: '', reason: 'not_text' } ] };
	if (length(text) > (max_bytes ?? MAX_USER_LIST_BYTES))
		return { ok: false, text: '', entries: 0, errors: [ { line: 0, value: '', reason: 'too_large' } ] };
	let out = [], errors = [], entries = 0;
	let lines = split(strip_bom(text), '\n');
	for (let i = 0; i < length(lines); i++) {
		let l = trim(lines[i]);
		if (l == '')
			continue;
		if (substr(l, 0, 1) == '#') {
			push(out, l);
			continue;
		}
		if (index(l, '\0') >= 0 || length(l) > 255) {
			push(errors, { line: i + 1, value: substr(l, 0, 64), reason: 'bad_line' });
			continue;
		}
		if (kind == 'hosts') {
			let d = lc(l);
			if (!domain_valid(d)) {
				push(errors, { line: i + 1, value: substr(l, 0, 64), reason: 'bad_domain' });
				continue;
			}
			push(out, d);
		}
		else {
			let c = cidr_parse(l);
			if (!c) {
				push(errors, { line: i + 1, value: substr(l, 0, 64), reason: 'bad_cidr' });
				continue;
			}
			push(out, c.net);
		}
		entries++;
	}
	return {
		ok: length(errors) == 0,
		text: length(out) ? join('\n', out) + '\n' : '',
		entries: entries,
		errors: slice(errors, 0, 50),
		error_count: length(errors)
	};
};

// Counts entries of a plain text list the same way nfqws skips comments.
export function count_entries(text) {
	let n = 0;
	for (let l in split(text ?? '', '\n')) {
		let c = substr(l, 0, 1);
		if (l == '' || c == '#' || c == ';' || c == '/' || c == '\r')
			continue;
		n++;
	}
	return n;
};

// Version comparison: numeric components compared as numbers, others as strings.
export function version_cmp(a, b) {
	let pa = split('' + (a ?? ''), /[.+~_-]/), pb = split('' + (b ?? ''), /[.+~_-]/);
	let n = (length(pa) > length(pb)) ? length(pa) : length(pb);
	for (let i = 0; i < n; i++) {
		let x = pa[i], y = pb[i];
		if (x == null)
			x = '0';
		if (y == null)
			y = '0';
		let xn = match(x, /^[0-9]+$/), yn = match(y, /^[0-9]+$/);
		if (xn && yn) {
			let d = int(x) - int(y);
			if (d != 0)
				return (d > 0) ? 1 : -1;
		}
		else if (x != y) {
			if (xn)
				return 1;
			if (yn)
				return -1;
			return (x > y) ? 1 : -1;
		}
	}
	return 0;
};

// Границы длины — через length(): интервал в регулярке ucode стоит миллисекунды за вызов (см. util.is_id).
export function version_valid(v) {
	return type(v) == 'string' && length(v) >= 1 && length(v) <= 32 && match(v, /^[0-9A-Za-z][0-9A-Za-z.+~_-]*$/) != null;
};

// nfqws port filter value: "[~]p1[-p2],..." -> { ok, negated, ranges: [[lo,hi],...] }
export function parse_port_filter(value) {
	if (type(value) != 'string' || value == '')
		return { ok: false };
	let negated = false;
	let ranges = [];
	for (let item in split(value, ',')) {
		if (substr(item, 0, 1) == '~') {
			negated = true;
			item = substr(item, 1);
		}
		let m = match(item, /^([0-9]{1,5})(-([0-9]{1,5}))?$/);
		if (!m)
			return { ok: false };
		let lo = int(m[1]), hi = (m[3] != null && m[3] != '') ? int(m[3]) : lo;
		if (lo > 65535 || hi > 65535 || lo > hi)
			return { ok: false };
		push(ranges, [ lo, hi ]);
	}
	return { ok: true, negated: negated, ranges: ranges };
};

export function merge_ranges(ranges) {
	let sorted = sort(slice(ranges ?? []), (a, b) => (a[0] != b[0]) ? (a[0] - b[0]) : (a[1] - b[1]));
	let out = [];
	for (let r in sorted) {
		let last = length(out) ? out[length(out) - 1] : null;
		if (last && r[0] <= last[1] + 1) {
			if (r[1] > last[1])
				last[1] = r[1];
		}
		else {
			push(out, [ r[0], r[1] ]);
		}
	}
	return out;
};

export function ranges_to_strings(ranges) {
	return map(ranges, (r) => (r[0] == r[1]) ? ('' + r[0]) : sprintf('%d-%d', r[0], r[1]));
};

export function url_valid(u) {
	return type(u) == 'string' && length(u) <= 2048 &&
		match(u, /^https?:\/\/[A-Za-z0-9.-]+(:[0-9]{1,5})?(\/[A-Za-z0-9._~%!$&'()*+,;=:@\/?#-]*)?$/) != null;
};

export function sha256_valid(s) {
	return type(s) == 'string' && length(s) == 64 && match(s, /^[0-9a-f]+$/) != null;
};

export function iface_name_valid(s) {
	return type(s) == 'string' && length(s) >= 1 && length(s) <= 32 && match(s, /^[A-Za-z0-9_.@-]+$/) != null;
};

export function user_name_valid(s) {
	return type(s) == 'string' && length(s) >= 1 && length(s) <= 32 && match(s, /^[a-z_][a-z0-9_-]*$/) != null;
};
