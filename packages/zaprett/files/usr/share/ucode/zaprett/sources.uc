// zaprett: subscriptions to external lists by URL (contract v1.1 §4). A downloaded subscription becomes
// an ordinary item src-<name> in /etc/zaprett with "source": "url".
'use strict';

import * as fs from 'fs';
import { P, read_json, write_json, mkdir_p, is_file, file_size, copy_file, sha256_file, df_avail_kib, uniq_name,
	log, ok, fail, NULL_CTX, meminfo_mib, download_concurrency } from 'zaprett.util';
import * as V from 'zaprett.validate';
import * as C from 'zaprett.config';
import * as S from 'zaprett.store';
import * as N from 'zaprett.net';

// Hard limit of a downloaded subscription; a larger download is cut off by probe.sh and rejected.
export const MAX_SOURCE_BYTES = 16777216;
// parallel downloads, one at a time below LOW_MEM_MIB of MemAvailable (contract v1.3 §14.5)
export const CONCURRENCY = 2;
export const LOW_MEM_MIB = 48;
export const RESERVE_KIB = 256;
// The daily cron run starts at the same minute but a download takes some time: an interval that ends
// within this many seconds after the check still counts as elapsed.
export const DUE_TOLERANCE = 3600;
export const READ_CHUNK = 65536;
export const MAX_LINE = 4096;
const UTF8_BOM = chr(0xef, 0xbb, 0xbf);

export function item_id(name) {
	return 'src-' + name;
};

export function state_path() {
	return P.etc + '/sources-state.json';
};

export function item_paths(name, itype) {
	let dir = S.TYPES[itype].dir;
	return {
		file: P.etc + '/files/' + dir + '/' + item_id(name) + '.txt',
		manifest: P.etc + '/manifests/' + dir + '/' + item_id(name) + '.json'
	};
};

// Pure: one line of a downloaded list, kind 'hosts' | 'ipset'. Returns the normalized entry, '' for a line
// that is skipped (empty, comment) or null for an invalid one. domain_valid() accepts only lowercase
// ASCII labels, so masks (*.), inner spaces and IDN that is not in punycode are rejected by it.
export function normalize_line(kind, l) {
	l = trim(l);
	let c = substr(l, 0, 1);
	if (l == '' || c == '#' || c == ';' || c == '/' || c == '!')
		return '';
	if (kind == 'hosts') {
		let d = lc(l);
		return V.domain_valid(d) ? d : null;
	}
	let n = V.cidr_parse(l);
	return n ? n.net : null;
};

// Pure: normalization of a list given as a string. Returns { text, valid, invalid, bytes }.
export function normalize_text(kind, text) {
	if (substr(text, 0, 3) == UTF8_BOM)
		text = substr(text, 3);
	let out = [], valid = 0, invalid = 0;
	for (let l in split(text, '\n')) {
		let e = normalize_line(kind, l);
		if (e == null)
			invalid++;
		else if (e != '') {
			push(out, e);
			valid++;
		}
	}
	let res = length(out) ? (join('\n', out) + '\n') : '';
	return { text: res, valid: valid, invalid: invalid, bytes: length(res) };
};

// Streams a downloaded list into a normalized file without holding the list in memory: the input is read
// in READ_CHUNK blocks and the output is written in blocks of about the same size. A line longer than
// MAX_LINE counts as one invalid entry. Returns { valid, invalid, bytes } or null on a read/write error.
export function normalize_file(kind, src_path, dst_path) {
	let inp = fs.open(src_path, 'r');
	if (!inp)
		return null;
	let out = fs.open(dst_path, 'w');
	if (!out) {
		inp.close();
		return null;
	}
	let valid = 0, invalid = 0, bytes = 0, io_ok = true;
	let carry = '', overlong = false, first = true, buf = [], buf_len = 0;
	let flush = () => {
		if (!buf_len)
			return;
		let s = join('', buf);
		if (out.write(s) != length(s))
			io_ok = false;
		bytes += length(s);
		buf = [];
		buf_len = 0;
	};
	let take = (l) => {
		let e = normalize_line(kind, l);
		if (e == null)
			invalid++;
		else if (e != '') {
			valid++;
			push(buf, e + '\n');
			buf_len += length(e) + 1;
			if (buf_len >= READ_CHUNK)
				flush();
		}
	};
	while (io_ok) {
		let chunk = inp.read(READ_CHUNK);
		if (chunk == null) {
			io_ok = false;
			break;
		}
		if (chunk == '')
			break;
		if (first) {
			if (substr(chunk, 0, 3) == UTF8_BOM)
				chunk = substr(chunk, 3);
			first = false;
		}
		let parts = split(chunk, '\n');
		let tail = pop(parts);
		for (let i = 0; i < length(parts); i++) {
			if (i > 0) {
				take(parts[i]);
				continue;
			}
			if (overlong)
				invalid++;
			else
				take(carry + parts[0]);
			overlong = false;
			carry = '';
		}
		if (!overlong) {
			carry += tail;
			if (length(carry) > MAX_LINE) {
				overlong = true;
				carry = '';
			}
		}
	}
	if (overlong)
		invalid++;
	else if (carry != '')
		take(carry);
	flush();
	inp.close();
	if (!out.close() || fs.stat(dst_path)?.size != bytes)
		io_ok = false;
	return io_ok ? { valid: valid, invalid: invalid, bytes: bytes } : null;
};

// Pure: is the downloaded content acceptable? Returns null or an error code.
export function check_content(src, norm) {
	if (norm.valid < src.min_entries || norm.valid == 0)
		return 'too_few_entries';
	let ratio = norm.valid * 1.0 / (norm.valid + norm.invalid);
	if (ratio < src.min_valid_ratio)
		return 'bad_content';
	return null;
};

// Pure: due for an update by interval?
export function is_due(src, st, now) {
	if (type(st?.last_update) != 'int')
		return true;
	return now - st.last_update >= src.interval_hours * 3600 - DUE_TOLERANCE;
};

export const ERROR_TEXT = {
	too_few_entries: 'в загруженном списке слишком мало записей — похоже на обрыв или заглушку, оставлен прежний файл',
	bad_content: 'слишком много неверных строк — оставлен прежний файл',
	too_large: 'файл больше 16 МиБ — загрузка прервана, оставлен прежний файл',
	no_space: 'недостаточно места на флеше — оставлен прежний файл',
	write_failed: 'не удалось записать файл — оставлен прежний файл',
	invalid_source: 'неверные настройки подписки (тип или ссылка)'
};

function date_version() {
	let t = localtime();
	return sprintf('%04d.%02d.%02d', t.year, t.mon, t.mday);
}

// Puts a normalized temporary file in place. Flash is written only when the content or the URL changed.
function install(src, tmpfile, norm) {
	let p = item_paths(src.name, src.type);
	let old = read_json(p.manifest, 65536);
	let sum = sha256_file(tmpfile);
	if (sum == null)
		return { error: 'write_failed' };
	if (old?.sha256 == sum && old?.url == src.url && file_size(p.file) == norm.bytes)
		return { changed: false, sha256: sum };
	let need = int((norm.bytes + 1023) / 1024) + RESERVE_KIB;
	let avail = df_avail_kib(P.etc) ?? df_avail_kib('/etc');
	if (avail != null && avail < need)
		return { error: 'no_space' };
	if (!mkdir_p(fs.dirname(p.file)) || !mkdir_p(fs.dirname(p.manifest)))
		return { error: 'write_failed' };
	let part = uniq_name(p.file + '.tmp');
	if (!copy_file(tmpfile, part) || file_size(part) != norm.bytes || !fs.chmod(part, 420) || !fs.rename(part, p.file)) {
		fs.unlink(part);
		return { error: 'write_failed' };
	}
	let manifest = {
		schema: 1, id: item_id(src.name), type: src.type, name: src.title, version: date_version(), author: '',
		description: src.url, dependencies: [], file: p.file, source: 'url', sha256: sum, installed_at: time(),
		manifest_url: null, url: src.url, entries: norm.valid
	};
	if (!write_json(p.manifest, manifest))
		return { error: 'write_failed' };
	return { changed: true, sha256: sum };
}

// Downloaded body -> checks -> installed item. Returns { error, detail, entries, changed, sha256 }.
function process_download(src, res) {
	if (res && res.bytes > MAX_SOURCE_BYTES)
		return { error: 'too_large' };
	let c = N.classify(res?.rc, res?.err, res?.bytes, 1);
	if (!c.ok || !res.body)
		return { error: 'download_failed', detail: (N.ERROR_TEXT[c.error] ?? c.error) + (res?.summary ? ('. ' + res.summary) : '') };
	if (!mkdir_p(P.tmp))
		return { error: 'write_failed' };
	let tmp = uniq_name(P.tmp + '/src');
	let norm = normalize_file(S.TYPES[src.type].kind, res.body, tmp);
	fs.unlink(res.body);
	let r = { error: null, entries: norm?.valid };
	if (!norm)
		r.error = 'write_failed';
	else
		r.error = check_content(src, norm);
	if (r.error == null) {
		let i = install(src, tmp, norm);
		r.error = i.error;
		r.changed = i.changed;
		r.sha256 = i.sha256;
	}
	fs.unlink(tmp);
	return r;
}

// names: null -> all enabled (with opts.due_only: only those whose interval elapsed); otherwise the named ones.
// opts.probe replaces N.probe (unit tests feed prepared downloads without network).
export function update(names, ctx, opts) {
	ctx = ctx ?? NULL_CTX;
	opts = opts ?? {};
	let all = C.load_sources();
	let by_name = {};
	for (let s in all)
		by_name[s.name] = s;
	let state = read_json(state_path(), 1048576) ?? {};
	let now = time();
	let targets = [];
	if (names != null && length(names)) {
		for (let n in names) {
			if (!by_name[n])
				return fail('not_found', sprintf('Подписка «%s» не найдена', n));
			push(targets, by_name[n]);
		}
	}
	else {
		for (let s in all)
			if (s.enabled && (!opts.due_only || is_due(s, state[s.name], now)))
				push(targets, s);
	}
	if (!length(targets))
		return ok({ updated: [], unchanged: [], failed: [], message: 'Нет подписок для обновления' });

	let tasks = [];
	for (let i = 0; i < length(targets); i++)
		if (targets[i].valid)
			push(tasks, { key: 's' + i, url: targets[i].url });
	ctx.progress(5, sprintf('Загрузка подписок: %d шт.', length(tasks)));
	// not `(opts.probe ?? N.probe)(...)`: ucode compiles that as a method call and, when opts.probe is set,
	// corrupts the caller's stack (checked on 24.10 and 25.12)
	let probe = opts.probe ?? N.probe;
	let conc = download_concurrency(CONCURRENCY, LOW_MEM_MIB, opts.mem_available_mib ?? meminfo_mib('MemAvailable'));
	let dl = probe(tasks, { concurrency: conc, timeout: 60, max_bytes: MAX_SOURCE_BYTES });

	let updated = [], unchanged = [], failed = [];
	for (let i = 0; i < length(targets); i++) {
		if (ctx.cancelled())
			break;
		let src = targets[i];
		ctx.progress(20 + int(75 * i / length(targets)), 'Обработка ' + src.name);
		let st = state[src.name] ?? {};
		st.last_check = now;
		let r = src.valid ? process_download(src, dl.results['s' + i]) : { error: 'invalid_source' };
		if (r.error == null) {
			st.last_update = now;
			st.sha256 = r.sha256;
			push(r.changed ? updated : unchanged, src.name);
			log('info', sprintf('подписка %s: %s, записей %d', src.name, r.changed ? 'обновлена' : 'без изменений', r.entries));
		}
		st.entries = r.entries ?? st.entries;
		st.status = r.error ? 'error' : 'ok';
		st.error = r.error;
		st.message = r.error ? (r.detail ?? ERROR_TEXT[r.error] ?? r.error) : null;
		state[src.name] = st;
		if (r.error) {
			push(failed, { name: src.name, error: r.error, message: st.message });
			ctx.log(sprintf('%s: %s', src.name, st.message));
		}
	}
	N.cleanup(dl.dir);
	mkdir_p(P.etc);
	write_json(state_path(), state);
	S.flush_cache();
	let res = { updated: updated, unchanged: unchanged, failed: failed };
	if (length(failed))
		return fail('source_update_failed', 'Не удалось обновить: ' + join(', ', map(failed, (f) => f.name + ' (' + f.message + ')')), res);
	return ok(res);
};

export function list() {
	let state = read_json(state_path(), 1048576) ?? {};
	let idx = S.scan();
	let out = [];
	for (let s in C.load_sources()) {
		let item = (index(C.SOURCE_TYPES, s.type) >= 0) ? idx.items[s.type][item_id(s.name)] : null;
		if (item && item.source != 'url')
			item = null;
		let st = state[s.name] ?? {};
		push(out, {
			name: s.name,
			enabled: s.enabled,
			title: s.title,
			type: s.type,
			url: s.url,
			interval_hours: s.interval_hours,
			min_entries: s.min_entries,
			last_update: st.last_update ?? item?.installed_at ?? null,
			entries: item ? S.entries_of(item) : null,
			size: item ? (fs.stat(item.file)?.size ?? null) : null,
			status: !s.valid ? 'invalid' : (st.status ?? (item ? 'ok' : 'never')),
			error: st.error ?? (s.valid ? null : 'invalid_source'),
			message: st.message ?? null,
			ram_mib: s.ram_mib,
			item_id: item_id(s.name),
			downloaded: !!item
		});
	}
	S.flush_cache();
	return ok({ sources: out });
};

// Forgets the download state of a subscription (its URL changed): the next update, including the cron
// run with due_only, downloads it at once. The previously downloaded file stays in use until then.
export function reset_state(name) {
	let state = read_json(state_path(), 1048576);
	if (type(state) != 'object' || type(state[name]) != 'object')
		return true;
	delete state[name];
	return write_json(state_path(), state);
};

// Removes the downloaded item of a subscription (all list types, in case the type was changed).
export function remove_item(name) {
	let removed = false;
	for (let t in C.SOURCE_TYPES) {
		let p = item_paths(name, t);
		if (is_file(p.manifest) || is_file(p.file)) {
			fs.unlink(p.file);
			fs.unlink(p.manifest);
			removed = true;
		}
	}
	reset_state(name);
	return removed;
};
