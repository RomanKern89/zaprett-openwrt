// zaprett: "how does the provider block it" (contract v1.4 §15.4). For every test target of the chosen services the
// router compares its system DNS with DNS-over-HTTPS, downloads the page as usual and, when that fails, checks
// whether a TCP connection to the real address is possible at all. The result describes what the router sees now:
// the engine is neither stopped nor restarted.
'use strict';

import * as fs from 'fs';
import { P, run, read_json, write_json, mkdir_p, ok, fail, NULL_CTX } from 'zaprett.util';
import * as V from 'zaprett.validate';
import * as N from 'zaprett.net';
import * as SV from 'zaprett.service';
import * as T from 'zaprett.tester';

export const VERDICTS = [ 'ok', 'dns_spoof', 'ip_block', 'tls_block', 'throttle', 'http_block', 'unknown' ];

// The known freeze of TLS connections after 14–24 KB from the server. uclient-fetch reports only the body, while the
// limit counts everything the server sent: the TLS handshake and the HTTP headers come first (measured on the stand:
// www.youtube.com, 26000 bytes of the connection -> 5433 bytes of body). So any download that broke off or froze
// after the body had started and before THROTTLE_MAX bytes counts as throttling.
export const THROTTLE_MAX = 24576;
export const THROTTLE_ERRORS = [ 'timeout', 'reset', 'tls_error', 'failed' ];
export const MAX_TARGETS = 20;
export const TCP_CHECK_IPS = 2;
export const BODY_HEAD = 4096;

// DNS-over-HTTPS by IP address, so the query does not depend on the system DNS it checks. Google answers JSON
// without extra headers; Cloudflare only with `accept: application/dns-json`.
export const DOH = [
	{ url: 'https://8.8.8.8/resolve?name=%s&type=A', header: null },
	{ url: 'https://1.1.1.1/dns-query?name=%s&type=A', header: 'accept: application/dns-json' }
];

// Text of typical provider stub pages (checked in the first bytes of an answer).
export const BLOCK_MARKERS = [ 'eais.rkn.gov.ru', 'blocklist.rkn', 'rkn.gov.ru', 'заблокирован', 'доступ ограничен',
	'доступ к ресурсу ограничен', 'blocked by', 'access to this resource is restricted' ];

export function result_path() {
	return P.run + '/diagnose.json';
};

// Pure: host and port of an http(s) URL, or null.
export function url_host(url) {
	let m = match(url ?? '', /^(https?):\/\/([A-Za-z0-9.-]+)(:([0-9]+))?(\/.*)?$/);
	if (!m)
		return null;
	let port = (m[4] != null && m[4] != '') ? int(m[4]) : ((m[1] == 'https') ? 443 : 80);
	return { scheme: m[1], host: lc(m[2]), port: port };
};

// Pure: IPv4 addresses from the output of busybox `nslookup -type=a <host>` (only the answer part: the address of
// the server comes before the first "Name:" line).
export function parse_nslookup(text) {
	let out = [], seen_name = false;
	for (let l in split(text ?? '', '\n')) {
		l = trim(l);
		if (index(l, 'Name:') == 0) {
			seen_name = true;
			continue;
		}
		let m = seen_name ? match(l, /^Address( [0-9]+)?:[ \t]*([0-9.]+)$/) : null;
		if (m && V.ipv4_valid(m[2]) && index(out, m[2]) < 0)
			push(out, m[2]);
	}
	return out;
};

// Pure: IPv4 addresses (type 1) from a JSON DoH answer, or null when the answer is not usable.
export function parse_doh(text) {
	let j = null;
	try {
		j = json(text ?? '');
	}
	catch (e) {
		return null;
	}
	if (type(j) != 'object' || j.Status == null)
		return null;
	let out = [];
	for (let a in ((type(j.Answer) == 'array') ? j.Answer : []))
		if (type(a) == 'object' && a.type == 1 && V.ipv4_valid(a.data) && index(out, a.data) < 0)
			push(out, a.data);
	return out;
};

// Pure: an address no real public site has: "this network", loopback, private, CGNAT, link-local, multicast and
// reserved ranges. A provider stub of the system DNS points at such addresses (or at its own block page, which
// shows up as a different address).
export function is_stub(ip) {
	let p = V.ipv4_parse(ip);
	if (!p)
		return false;
	let a = p[0], b = p[1];
	return a == 0 || a == 10 || a == 127 || a >= 224 || (a == 100 && b >= 64 && b <= 127) || (a == 169 && b == 254) ||
		(a == 172 && b >= 16 && b <= 31) || (a == 192 && b == 168);
};

// Pure: TCP was established when uclient-fetch got past the connection: any answer, a TLS error or a certificate
// error. "Connection failed" without an SSL error and "Failed to send request" mean no TCP connection
// (texts of uclient-fetch.c, both OpenWrt branches); a killed fetch tells nothing.
export function tcp_connected(rc, err) {
	let t = err ?? '';
	if (rc == 0)
		return true;
	if (rc < 0)
		return null;
	if (index(t, 'Failed to send request') >= 0 || index(t, N.SEND_FAILED) >= 0)
		return false;
	if (index(t, 'Connection failed') >= 0 && index(t, 'SSL error') < 0)
		return false;
	return true;
};

function has_block_marker(head) {
	let h = lc(head ?? '');
	for (let m in BLOCK_MARKERS)
		if (index(h, m) >= 0)
			return true;
	return false;
}

function any_in(a, b) {
	for (let x in a)
		if (index(b, x) >= 0)
			return true;
	return false;
}

// Pure: verdict of one target. t: { sys: [ip], doh: [ip] | null (DoH failed), fetch: { rc, err, bytes, min_bytes,
// head }, tcp: null | true | false (TCP to the real address; null = not checked) }.
// Returns { verdict, spoofed, need_tcp, reason }; need_tcp asks the caller to check TCP and call again.
export function judge(t) {
	let sys = t.sys ?? [], doh = t.doh ?? [], f = t.fetch ?? {};
	let c = N.classify(f.rc, f.err, f.bytes, f.min_bytes);
	let res = (v, sp, reason) => ({ verdict: v, spoofed: sp, need_tcp: false, reason: reason, error: c.ok ? null : c.error });
	if (length(sys) && length(filter(sys, (ip) => is_stub(ip))) == length(sys))
		return res('dns_spoof', true, 'stub_address');
	if (c.ok)
		return res('ok', false, null);
	if (!length(sys) && length(doh))
		return res('dns_spoof', true, 'no_system_answer');
	if (length(sys) && length(doh) && !any_in(sys, doh))
		return res('dns_spoof', true, 'address_differs');
	if (!length(sys) && t.doh == null)
		return res('unknown', false, 'dns_failed');
	if (c.http_status == 451)
		return res('http_block', false, 'http_451');
	if (c.error == 'tls_cert')
		return res('http_block', false, 'forged_certificate');
	if (has_block_marker(f.head))
		return res('http_block', false, 'stub_page');
	if ((f.bytes ?? 0) > 0 && (f.bytes ?? 0) <= THROTTLE_MAX && index(THROTTLE_ERRORS, c.error) >= 0)
		return res('throttle', false, 'stalled_' + c.error);
	if (c.error == 'tls_error')
		return res('tls_block', false, 'tls_error');
	if (index([ 'connect_failed', 'reset', 'timeout' ], c.error) >= 0) {
		if (t.tcp == null) {
			let r = res('unknown', false, 'tcp_not_checked');
			r.need_tcp = true;
			return r;
		}
		if (!t.tcp)
			return res('ip_block', false, 'tcp_failed');
		return res((c.error == 'connect_failed') ? 'unknown' : 'tls_block', false,
			(c.error == 'connect_failed') ? 'tcp_ok_fetch_failed' : c.error);
	}
	return res('unknown', false, c.error);
};

export const REASON_TEXT = {
	stub_address: 'системный DNS вернул адрес-заглушку',
	no_system_answer: 'системный DNS не знает этот домен, а DNS-over-HTTPS знает',
	address_differs: 'системный DNS дал адрес, которого нет в ответе DNS-over-HTTPS, и сайт по нему не открылся',
	dns_failed: 'домен не удалось разрешить ни системным DNS, ни через DNS-over-HTTPS',
	http_451: 'сервер ответил кодом 451 (недоступно по юридическим причинам)',
	forged_certificate: 'вместо сайта отвечает чужой сертификат — заглушка провайдера',
	stub_page: 'вместо сайта пришла страница-заглушка',
	tcp_failed: 'TCP-соединение с настоящим адресом сайта не устанавливается',
	tcp_ok_fetch_failed: 'соединение с адресом из DNS-over-HTTPS есть, но загрузка не удалась',
	tls_error: 'соединение есть, но TLS-рукопожатие обрывается',
	reset: 'соединение есть, но сбрасывается',
	timeout: 'соединение есть, но ответа нет',
	tcp_not_checked: 'проверка TCP не выполнена'
};

// Pure: summary.verdict — the most frequent verdict other than ok (ties: order of VERDICTS), or ok.
export function summarize(targets) {
	let counts = {};
	for (let t in targets)
		counts[t.verdict] = (counts[t.verdict] ?? 0) + 1;
	let best = 'ok', n = 0;
	for (let v in VERDICTS)
		if (v != 'ok' && (counts[v] ?? 0) > n) {
			best = v;
			n = counts[v];
		}
	return { verdict: best, counts: counts };
};

function detail_text(j, t) {
	let s = REASON_TEXT[j.reason] ?? (j.reason ? ('ошибка загрузки: ' + (N.ERROR_TEXT[j.reason] ?? j.reason)) : 'сайт открывается');
	if (j.verdict == 'throttle')
		s = sprintf('данные идут и замирают на %d байт — похоже на замедление', t.fetch.bytes);
	return sprintf('%s (системный DNS: %s; DoH: %s)', s, length(t.sys) ? join(', ', t.sys) : '—',
		(t.doh == null) ? 'нет ответа' : (length(t.doh) ? join(', ', t.doh) : '—'));
}

function system_resolve(host) {
	let r = run([ 'nslookup', '-type=a', host ], { timeout: 12000, limit: 16384 });
	return parse_nslookup(r.stdout);
}

function doh_resolve(host, timeout) {
	for (let d in DOH) {
		let argv = [ 'uclient-fetch', '-q', '-4', '-T', '' + timeout, '-O', '-' ];
		if (d.header)
			push(argv, '--header=' + d.header);
		push(argv, sprintf(d.url, host));
		let r = run(argv, { timeout: (timeout * 2 + 5) * 1000, limit: 65536 });
		if (r.rc == 0) {
			let ips = parse_doh(r.stdout);
			if (ips != null)
				return ips;
		}
	}
	return null;
}

function read_head(path) {
	if (!path)
		return '';
	let fh = fs.open(path, 'r');
	if (!fh)
		return '';
	let h = fh.read(BODY_HEAD) ?? '';
	fh.close();
	return h;
}

// Job `diagnose`. ids: null -> services that are switched on. hooks (unit tests): resolve(host), doh(host),
// probe(tasks, opts) like net.probe, instance_state.
export function run_diagnose(cfg, ids, ctx, hooks) {
	ctx = ctx ?? NULL_CTX;
	hooks = hooks ?? {};
	let ps = T.preset_services(read_json(P.presets, 1048576), cfg, ids);
	if (ps.error)
		return fail('unknown_service', sprintf('Сервис «%s» не найден в пресетах', ps.id));
	let resolve = hooks.resolve ?? system_resolve;
	let doh = hooks.doh ?? ((h) => doh_resolve(h, cfg.test.timeout));
	let probe = hooks.probe ?? N.probe;
	let instance_state = hooks.instance_state ?? SV.instance_state;
	let tmo = cfg.test.timeout;

	let targets = [], seen = {};
	for (let s in ps.services)
		for (let t in s.targets) {
			let u = url_host(t.url);
			if (!u || seen[t.url] || length(targets) >= MAX_TARGETS)
				continue;
			seen[t.url] = true;
			push(targets, { url: t.url, host: u.host, port: u.port, service: s.id, min_bytes: t.min_bytes });
		}
	let res = { started: time(), finished: 0, engine_running: !!instance_state().running, targets: [],
		summary: { verdict: 'ok', counts: {} } };
	if (!length(targets))
		return fail('no_targets', 'Нет целей для проверки: включите сервис в быстрой настройке или укажите --services');

	ctx.progress(5, sprintf('DNS: %d адресов', length(targets)));
	let dns = {};
	for (let t in targets)
		if (!dns[t.host])
			dns[t.host] = { sys: V.ipv4_valid(t.host) ? [ t.host ] : resolve(t.host), doh: V.ipv4_valid(t.host) ? [ t.host ] : doh(t.host) };

	ctx.progress(35, 'Загрузка страниц');
	let tasks = [];
	for (let i = 0; i < length(targets); i++)
		push(tasks, { key: 'f' + i, url: targets[i].url });
	let p = probe(tasks, { concurrency: cfg.test.concurrency, timeout: tmo, ipv4only: true, max_bytes: 1048576 });
	let judged = [], tcp_tasks = [];
	for (let i = 0; i < length(targets); i++) {
		let t = targets[i], r = p.results?.['f' + i] ?? { rc: -1, err: '', bytes: 0 };
		let d = dns[t.host];
		let st = { sys: d.sys, doh: d.doh, fetch: { rc: r.rc, err: r.err, bytes: r.bytes, min_bytes: t.min_bytes,
			head: read_head(r.body) }, tcp: null };
		let j = judge(st);
		if (j.need_tcp) {
			// the addresses the router really used first (system answer confirmed by DoH), then other DoH ones
			let ips = filter(d.doh ?? [], (ip) => index(d.sys, ip) >= 0);
			for (let ip in (d.doh ?? []))
				if (index(ips, ip) < 0)
					push(ips, ip);
			ips = slice(ips, 0, TCP_CHECK_IPS);
			for (let k = 0; k < length(ips); k++)
				push(tcp_tasks, { key: sprintf('t%d_%d', i, k), url: sprintf('https://%s:%d/', ips[k], t.port), target: i });
			if (!length(ips))
				st.tcp = false;
		}
		push(judged, st);
	}
	N.cleanup(p.dir);

	if (length(tcp_tasks)) {
		ctx.progress(70, sprintf('Проверка TCP: %d адресов', length(tcp_tasks)));
		let q = probe(tcp_tasks, { concurrency: cfg.test.concurrency, timeout: tmo, ipv4only: true, max_bytes: 65536 });
		for (let tt in tcp_tasks) {
			let r = q.results?.[tt.key];
			let c = r ? tcp_connected(r.rc, r.err) : null;
			let st = judged[tt.target];
			if (c == true)
				st.tcp = true;
			else if (c == false && st.tcp != true)
				st.tcp = false;
		}
		N.cleanup(q.dir);
	}

	for (let i = 0; i < length(targets); i++) {
		let t = targets[i], st = judged[i];
		let j = judge(st);
		push(res.targets, { url: t.url, host: t.host, service: t.service, verdict: j.verdict,
			dns: { system: st.sys, doh: st.doh ?? [], spoofed: j.spoofed }, detail: detail_text(j, st),
			reason: j.reason, error: j.error, bytes: st.fetch.bytes, tcp: st.tcp });
		ctx.log(sprintf('%s: %s — %s', t.url, j.verdict, j.reason ?? ''));
	}
	res.summary = summarize(res.targets);
	res.finished = time();
	mkdir_p(P.run);
	if (!write_json(result_path(), res))
		return fail('write_failed', 'Не удалось записать ' + result_path());
	return ok({ verdict: res.summary.verdict, counts: res.summary.counts, total: length(res.targets),
		message: sprintf('Итог: %s (%d адресов)', res.summary.verdict, length(res.targets)) });
};

export function status() {
	let d = read_json(result_path(), 1048576);
	return ok({ diagnose: (type(d) == 'object') ? d : null });
};
