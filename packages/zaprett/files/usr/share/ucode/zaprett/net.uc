// zaprett: HTTP(S) through uclient-fetch (ADR-001: no uclient ucode module in 24.10).
'use strict';

import * as fs from 'fs';
import { P, run, mkdir_p, uniq_name, is_file } from 'zaprett.util';
import * as V from 'zaprett.validate';

// uclient-fetch exit codes and stderr texts, taken from uclient-fetch.c of both OpenWrt branches
// (24.10: uclient 88ae8f20, 25.12: uclient daad21fa):
//   0 success; 1 generic/unknown error; 2 write to output failed (25.12); 3 cannot open output file;
//   4 "Connection error: Connection failed" / "Connection error: Connection timed out" /
//     "Connection reset prematurely" / "Failed to send request: ..." (+ "SSL error: ..." on TLS failure; on 24.10 a
//     refused TCP connection also prints "SSL error: NET - Sending information through the socket failed");
//   5 "Connection error: Invalid SSL certificate" / "... Server hostname does not match SSL certificate"
//     (+ "SSL verify error: ..."); 8 "HTTP error <code>", redirect or range errors.
// Without -q the tool also prints "Downloading", "Connecting to", "Writing to", "Download completed".
export const SEND_FAILED = 'Sending information through the socket failed';

export function classify(rc, err_text, bytes, min_bytes) {
	let t = err_text ?? '';
	let hm = match(t, /HTTP error ([0-9]{3})/);
	let res = { ok: false, error: null, http_status: hm ? int(hm[1]) : null };
	min_bytes = min_bytes ?? 0;
	bytes = bytes ?? 0;
	if (rc == 0) {
		if (bytes >= min_bytes) {
			res.ok = true;
			return res;
		}
		res.error = 'too_small';
		return res;
	}
	if (rc == 8 && hm) {
		// the server answered with an error status: the path to it is not blocked
		if (min_bytes == 0) {
			res.ok = true;
			return res;
		}
		res.error = 'http_error';
		return res;
	}
	if (rc < 0)
		res.error = 'timeout';
	else if (index(t, 'Connection timed out') >= 0)
		res.error = 'timeout';
	else if (index(t, 'Connection reset prematurely') >= 0)
		res.error = 'reset';
	else if (index(t, 'SSL verify error') >= 0 || index(t, 'Invalid SSL certificate') >= 0 ||
	         index(t, 'does not match SSL certificate') >= 0)
		res.error = 'tls_cert';
	// 24.10 (mbedtls): a refused or reset TCP connection shows up as an SSL error of the first send — the ClientHello
	// never left, so this is no TLS problem (checked with a local TCP reset on 24.10.8)
	else if (index(t, SEND_FAILED) >= 0)
		res.error = 'connect_failed';
	else if (index(t, 'SSL error') >= 0)
		res.error = 'tls_error';
	else if (index(t, 'Connection failed') >= 0 || index(t, 'Failed to send request') >= 0)
		res.error = 'connect_failed';
	else if (rc == 8)
		res.error = 'http_error';
	else if (rc == 3 || rc == 2)
		res.error = 'local_error';
	else
		res.error = 'failed';
	return res;
};

export const ERROR_TEXT = {
	too_small: 'ответ слишком короткий (обрыв или заглушка)',
	http_error: 'сервер вернул ошибку HTTP',
	timeout: 'нет ответа (таймаут)',
	reset: 'соединение сброшено',
	tls_cert: 'подменённый сертификат',
	tls_error: 'ошибка TLS (обрыв рукопожатия)',
	connect_failed: 'не удалось подключиться',
	local_error: 'ошибка записи во временный файл',
	failed: 'ошибка загрузки'
};

function err_summary(text) {
	let keep = [];
	for (let l in split(text ?? '', '\n')) {
		l = trim(l);
		if (l == '' || index(l, 'Downloading') == 0 || index(l, 'Connecting to') == 0 || index(l, 'Writing to') == 0 ||
		    index(l, 'Redirected to') == 0 || index(l, 'Download completed') == 0 || index(l, '%') >= 0)
			continue;
		push(keep, l);
	}
	return substr(join('; ', keep), 0, 300);
}

// tasks: [ { key, url } ]. opts: { concurrency, timeout, ipv4only, max_bytes }.
// Returns { dir, results: { key: { rc, ms, bytes, body, err } } }; with max_bytes a larger download is cut
// off and reported with bytes > max_bytes. The caller must call cleanup(dir) after reading bodies.
export function probe(tasks, opts) {
	opts = opts ?? {};
	let dir = uniq_name(P.tmp + '/probe');
	if (!mkdir_p(dir))
		return { dir: null, results: {}, error: 'tmp_failed' };
	let lines = [];
	for (let t in tasks)
		if (match(t.key, /^[A-Za-z0-9_.-]+$/) && V.url_valid(t.url))
			push(lines, t.key + ' ' + t.url);
	let tfile = dir + '/tasks';
	fs.writefile(tfile, join('\n', lines) + '\n');
	let conc = opts.concurrency ?? 4, tmo = opts.timeout ?? 10;
	let batches = int((length(lines) + conc - 1) / conc);
	let limit_ms = (batches * (tmo * 3 + 5) + 30) * 1000;
	let pargs = [ tfile, dir, '' + conc, '' + tmo, opts.ipv4only ? '1' : '0', '' + int(opts.max_bytes ?? 0) ];
	let argv = [ '/bin/sh', P.probe ];
	// opts.user: the downloads run as that user (isolated automatic selection, contract v1.6 §17). start-stop-daemon
	// executes the script itself, so its "already running" match (argv[0] == the script) never hits a running shell.
	if (opts.user) {
		if (!fs.chown(dir, opts.user, opts.user))
			return { dir: dir, results: {}, error: 'tmp_failed' };
		argv = [ P.ssd, '-S', '-c', opts.user, '-x', P.probe, '--' ];
	}
	for (let a in pargs)
		push(argv, a);
	let r = run(argv, { timeout: limit_ms, limit: 16384 });
	let results = {};
	for (let t in tasks) {
		let res = fs.readfile(dir + '/' + t.key + '.res', 64);
		let body = dir + '/' + t.key + '.body';
		let err = fs.readfile(dir + '/' + t.key + '.err', 16384) ?? '';
		let rc = -1, ms = null;
		if (res) {
			let f = split(trim(res), ' ');
			rc = int(f[0]);
			ms = int(f[1]);
		}
		results[t.key] = {
			rc: rc,
			ms: ms,
			bytes: fs.stat(body)?.size ?? 0,
			body: is_file(body) ? body : null,
			err: err,
			summary: err_summary(err)
		};
	}
	return { dir: dir, results: results, rc: r.rc };
};

export function cleanup(dir) {
	if (type(dir) == 'string' && index(dir, P.tmp + '/probe.') == 0)
		run([ 'rm', '-rf', dir ], { timeout: 30000 });
};
