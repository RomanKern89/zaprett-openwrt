// zaprett: local item store (ARCHITECTURE §3): bundle (ROM) < /etc/zaprett (installed) + user items.
'use strict';

import * as fs from 'fs';
import { P, read_json, read_limited, is_file, is_id, mkdir_p, write_json } from 'zaprett.util';
import * as V from 'zaprett.validate';

export const TYPES = {
	list: { dir: 'lists/include', kind: 'hosts', uci: 'lists', placeholder: 'hostlist' },
	list_exclude: { dir: 'lists/exclude', kind: 'hosts', uci: 'exclude_lists', placeholder: 'hostlist_exclude' },
	ipset: { dir: 'ipset/include', kind: 'ipset', uci: 'ipsets', placeholder: 'ipset' },
	ipset_exclude: { dir: 'ipset/exclude', kind: 'ipset', uci: 'exclude_ipsets', placeholder: 'ipset_exclude' },
	nfqws: { dir: 'strategies/nfqws', kind: 'strategy', engine: 'nfqws' },
	nfqws2: { dir: 'strategies/nfqws2', kind: 'strategy', engine: 'nfqws2' },
	bin: { dir: 'bin', kind: 'bin', placeholder: 'bin' },
	lua_lib: { dir: 'lua', kind: 'lua', placeholder: 'lua_lib' },
	byedpi: { dir: 'strategies/byedpi', kind: 'strategy', unsupported: true }
};

export const TYPE_ORDER = [ 'list', 'list_exclude', 'ipset', 'ipset_exclude', 'nfqws', 'nfqws2', 'bin', 'lua_lib', 'byedpi' ];
export const LIST_TYPES = [ 'list', 'list_exclude', 'ipset', 'ipset_exclude' ];
export const STRATEGY_TYPES = [ 'nfqws', 'nfqws2' ];

export const USER_LISTS = {
	'user-hosts': { type: 'list', file: 'hosts-include.txt', name: 'Мои домены' },
	'user-hosts-exclude': { type: 'list_exclude', file: 'hosts-exclude.txt', name: 'Мои домены-исключения' },
	'user-ipset': { type: 'ipset', file: 'ipset-include.txt', name: 'Мои IP-сети' },
	'user-ipset-exclude': { type: 'ipset_exclude', file: 'ipset-exclude.txt', name: 'Мои IP-сети-исключения' }
};

export const MAX_MANIFEST_BYTES = 65536;
export const MAX_STRATEGY_BYTES = 65536;
export const MAX_COUNT_BYTES = 16777216;

function clean_str(v, max) {
	return (type(v) == 'string') ? substr(trim(v), 0, max) : '';
}

// true when path is a normalized absolute path strictly inside root
export function path_under(path, root) {
	if (type(path) != 'string' || substr(path, 0, length(root) + 1) != root + '/')
		return false;
	for (let seg in split(substr(path, length(root) + 1), '/'))
		if (seg == '' || seg == '.' || seg == '..')
			return false;
	return true;
};

export function dep_to_id(dep) {
	if (type(dep) != 'string')
		return null;
	let id = dep;
	if (match(dep, /^https?:/)) {
		let parts = split(dep, '/');
		id = parts[length(parts) - 1];
		if (substr(id, -5) == '.json')
			id = substr(id, 0, length(id) - 5);
	}
	return is_id(id) ? id : null;
};

// Pure: validates a manifest object read from <root>/manifests/<dir>/<file_id>.json.
export function parse_manifest(obj, itype, source, file_id, root) {
	// required: schema, id, file, sha256; optional fields may be absent or null (bundle: manifest_url null)
	if (type(obj) != 'object')
		return { error: 'bad_json' };
	if (obj.schema != 1)
		return { error: 'bad_schema' };
	if (!is_id(obj.id))
		return { error: 'bad_id' };
	if (file_id != null && obj.id != file_id)
		return { error: 'id_mismatch' };
	if (obj.type != null && obj.type != itype)
		return { error: 'type_mismatch' };
	let files_root = root + '/files';
	let file = obj.file;
	if (type(file) != 'string' || file == '')
		return { error: 'no_file' };
	if (substr(file, 0, 1) != '/')
		file = files_root + '/' + TYPES[itype].dir + '/' + file;
	if (!path_under(file, files_root))
		return { error: 'file_outside_root' };
	if (!V.sha256_valid(obj.sha256))
		return { error: 'no_sha256' };
	let deps = [];
	if (type(obj.dependencies) == 'array') {
		for (let d in obj.dependencies) {
			let id = dep_to_id(d);
			if (id != null && index(deps, id) < 0)
				push(deps, id);
		}
	}
	let version = V.version_valid(obj.version) ? obj.version : '0';
	return {
		id: obj.id,
		type: itype,
		name: clean_str(obj.name, 128) || obj.id,
		version: version,
		author: clean_str(obj.author, 128),
		description: clean_str(obj.description, 1024),
		dependencies: deps,
		file: file,
		// items in /etc/zaprett come from the repository or from URL subscriptions (contract v1.1)
		source: (source == 'repo' && obj.source == 'url') ? 'url' : source,
		entries_hint: (type(obj.entries) == 'int') ? obj.entries : null,
		sha256: obj.sha256,
		installed_at: (type(obj.installed_at) == 'int') ? obj.installed_at : null,
		manifest_url: V.url_valid(obj.manifest_url) ? obj.manifest_url : null,
		manifest_path: null
	};
};

function add_user_items(idx) {
	for (let id, u in USER_LISTS) {
		idx.items[u.type][id] = {
			id: id, type: u.type, name: u.name, version: '', author: '', description: '',
			dependencies: [], file: P.user + '/' + u.file, source: 'user', sha256: null,
			installed_at: null, manifest_url: null, manifest_path: null
		};
	}
	for (let eng in STRATEGY_TYPES) {
		let dir = P.user + '/strategies/' + eng;
		for (let n in sort(fs.lsdir(dir) ?? [])) {
			if (substr(n, -4) != '.txt')
				continue;
			let id = substr(n, 0, length(n) - 4);
			if (!is_id(id) || substr(id, 0, 5) != 'user-' || !is_file(dir + '/' + n))
				continue;
			idx.items[eng][id] = {
				id: id, type: eng, name: id, version: '', author: '', description: 'Своя стратегия',
				dependencies: [], file: dir + '/' + n, source: 'user', sha256: null,
				installed_at: null, manifest_url: null, manifest_path: null
			};
		}
	}
}

export function scan() {
	let idx = { items: {}, errors: [] };
	for (let t in TYPE_ORDER)
		idx.items[t] = {};
	let sources = [ { source: 'bundle', root: P.bundle }, { source: 'repo', root: P.etc } ];
	for (let src in sources) {
		for (let t in TYPE_ORDER) {
			let mdir = src.root + '/manifests/' + TYPES[t].dir;
			for (let n in sort(fs.lsdir(mdir) ?? [])) {
				if (substr(n, -5) != '.json')
					continue;
				let fid = substr(n, 0, length(n) - 5);
				let path = mdir + '/' + n;
				if (!is_id(fid)) {
					push(idx.errors, { path: path, error: 'bad_id' });
					continue;
				}
				let it = parse_manifest(read_json(path, MAX_MANIFEST_BYTES), t, src.source, fid, src.root);
				if (it.error) {
					push(idx.errors, { path: path, error: it.error });
					continue;
				}
				if (!is_file(it.file)) {
					push(idx.errors, { path: path, error: 'file_missing' });
					continue;
				}
				it.manifest_path = path;
				idx.items[t][it.id] = it;
			}
		}
	}
	add_user_items(idx);
	return idx;
};

export function find(idx, id, types) {
	for (let t in (types ?? TYPE_ORDER)) {
		let it = idx.items[t]?.[id];
		if (it)
			return it;
	}
	return null;
};

export function find_all(idx, id, types) {
	let res = [];
	for (let t in (types ?? TYPE_ORDER)) {
		let it = idx.items[t]?.[id];
		if (it)
			push(res, it);
	}
	return res;
};

export function bundle_item(itype, id) {
	let path = P.bundle + '/manifests/' + TYPES[itype].dir + '/' + id + '.json';
	let it = parse_manifest(read_json(path, MAX_MANIFEST_BYTES), itype, 'bundle', id, P.bundle);
	if (it.error || !is_file(it.file))
		return null;
	it.manifest_path = path;
	return it;
};

export function read_strategy_text(item) {
	return read_limited(item.file, MAX_STRATEGY_BYTES);
};

// ucode encodes "\xHH" escapes above 0x7f as UTF-8, so the gzip magic is built with chr()
const GZIP_MAGIC = chr(0x1f, 0x8b);

export function is_gzip(path) {
	return fs.readfile(path, 2) == GZIP_MAGIC;
};

// Entry counter with a cache keyed by path, size and mtime (kept in tmpfs).
let entries_cache = null;
let entries_dirty = false;

function cache_path() {
	return P.run + '/cache/entries.json';
}

export function entries_of(item) {
	if (!TYPES[item.type] || (TYPES[item.type].kind != 'hosts' && TYPES[item.type].kind != 'ipset'))
		return null;
	let st = fs.stat(item.file);
	if (!st || st.type != 'file')
		return 0;
	if (entries_cache == null)
		entries_cache = read_json(cache_path(), 4194304) ?? {};
	let c = entries_cache[item.file];
	if (c && c.size == st.size && c.mtime == st.mtime)
		return c.entries;
	let n = null;
	if (st.size == 0)
		n = 0;
	else if (st.size <= MAX_COUNT_BYTES && !is_gzip(item.file))
		n = V.count_entries(fs.readfile(item.file));
	entries_cache[item.file] = { size: st.size, mtime: st.mtime, entries: n };
	entries_dirty = true;
	return n;
};

export function flush_cache() {
	if (entries_dirty && mkdir_p(P.run + '/cache')) {
		write_json(cache_path(), entries_cache);
		entries_dirty = false;
	}
};

export function is_active(item, cfg, idx) {
	let t = TYPES[item.type];
	if (t.uci)
		return index(cfg[t.uci], item.id) >= 0;
	if (item.type == 'nfqws')
		return cfg.strategy == item.id;
	if (item.type == 'nfqws2')
		return cfg.strategy_nfqws2 == item.id;
	if (item.type == 'bin' || item.type == 'lua_lib') {
		let sid = (cfg.engine == 'nfqws2') ? cfg.strategy_nfqws2 : cfg.strategy;
		let s = idx.items[cfg.engine]?.[sid];
		return !!(s && index(s.dependencies, item.id) >= 0);
	}
	return false;
};

export function used_by(idx, id) {
	let res = [];
	for (let t in STRATEGY_TYPES)
		for (let sid, s in idx.items[t])
			if (index(s.dependencies, id) >= 0 && index(res, sid) < 0)
				push(res, sid);
	return sort(res);
};

export function describe(idx, cfg, only_type) {
	let out = [];
	for (let t in TYPE_ORDER) {
		if (only_type != null && only_type != t)
			continue;
		for (let id in sort(keys(idx.items[t]))) {
			let it = idx.items[t][id];
			push(out, {
				id: it.id,
				type: it.type,
				name: it.name,
				version: it.version,
				author: it.author,
				description: it.description,
				source: it.source,
				file: it.file,
				entries: entries_of(it),
				size: fs.stat(it.file)?.size ?? 0,
				active: is_active(it, cfg, idx),
				used_by: used_by(idx, it.id),
				dependencies: it.dependencies,
				supported: !TYPES[t].unsupported
			});
		}
	}
	flush_cache();
	return out;
};

export function user_list_path(id) {
	let u = USER_LISTS[id];
	return u ? (P.user + '/' + u.file) : null;
};

export function user_strategy_path(engine, id) {
	return P.user + '/strategies/' + engine + '/' + id + '.txt';
};
