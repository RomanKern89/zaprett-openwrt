// zaprett: zaprett-repo client (index -> manifests -> artifacts with sha256), ARCHITECTURE §3, §6.2.
'use strict';

import * as fs from 'fs';
import { P, run, read_json, write_json, mkdir_p, atomic_write, is_file, is_id, sha256_file, df_avail_kib,
	uniq_name, log, ok, fail, MODE_FILE, NULL_CTX } from 'zaprett.util';
import * as V from 'zaprett.validate';
import * as S from 'zaprett.store';
import * as N from 'zaprett.net';

export const MAX_INDEX_BYTES = 1048576;
export const MAX_MANIFEST_BYTES = 65536;
export const MAX_ITEMS = 2000;
export const MAX_ARTIFACT_BYTES = 33554432;
export const RESERVE_KIB = 256;

export function cache_path() {
	return P.run + '/repo/index.json';
};

// Pure: validates index.json.
export function parse_index(obj) {
	if (type(obj) != 'object' || obj.schema != 1 || type(obj.items) != 'array')
		return { error: 'bad_index' };
	let items = [], seen = {}, skipped = 0;
	for (let it in obj.items) {
		if (type(it) != 'object' || !is_id(it.id) || !S.TYPES[it.type] || !V.url_valid(it.manifest)) {
			skipped++;
			continue;
		}
		let key = it.type + '/' + it.id;
		if (seen[key]) {
			skipped++;
			continue;
		}
		seen[key] = true;
		push(items, { id: it.id, type: it.type, manifest_url: it.manifest });
		if (length(items) >= MAX_ITEMS)
			break;
	}
	return { items: items, skipped: skipped };
};

function str(v, max) {
	return (type(v) == 'string') ? substr(trim(v), 0, max) : '';
}

// Pure: validates a manifest of the repository (schema 1).
export function parse_manifest(obj, index_item) {
	if (type(obj) != 'object' || obj.schema != 1)
		return { error: 'bad_manifest' };
	// The index id is authoritative (the manifest URL comes from the index). zaprett-repo has manifests
	// whose id differs slightly (e.g. "ipset-exclude-yandex." with a trailing dot), so a mismatch is noted
	// instead of rejecting the item; an id that is not even a string is still an error.
	if (type(obj.id) != 'string')
		return { error: 'id_mismatch' };
	let id_note = (obj.id != index_item.id) ? sprintf('в манифесте указан id «%s»', substr(obj.id, 0, 100)) : null;
	if (!V.version_valid(obj.version))
		return { error: 'bad_version' };
	let art = obj.artifact;
	let sha = (type(art?.sha256) == 'string') ? lc(art.sha256) : '';
	if (type(art) != 'object' || !V.url_valid(art.url) || !V.sha256_valid(sha))
		return { error: 'bad_artifact' };
	let deps = [];
	if (obj.dependencies != null) {
		if (type(obj.dependencies) != 'array')
			return { error: 'bad_dependencies' };
		for (let d in obj.dependencies) {
			if (!V.url_valid(d))
				return { error: 'bad_dependencies' };
			if (index(deps, d) < 0)
				push(deps, d);
		}
	}
	return {
		id: index_item.id,
		id_note: id_note,
		type: index_item.type,
		name: str(obj.name, 128) || index_item.id,
		version: obj.version,
		author: str(obj.author, 128),
		description: str(obj.description, 1024),
		dependencies: deps,
		artifact: { url: art.url, sha256: sha },
		manifest_url: index_item.manifest_url
	};
};

// Pure: is the repository manifest newer than the installed item?
// Bundle items are replaced only by a strictly newer version; items installed from the repository
// are also refreshed when the version is equal but the artifact checksum differs.
export function is_update(installed, manifest) {
	if (!installed || !manifest)
		return false;
	let c = V.version_cmp(manifest.version, installed.version);
	if (c > 0)
		return true;
	return (c == 0 && installed.source == 'repo' && installed.sha256 != null && installed.sha256 != manifest.artifact.sha256);
};

// Pure: dependency closure in install order (dependencies first).
// by_url: { manifest_url: cache item with .manifest }. Returns { order: [items], error }.
export function resolve_order(roots, by_url) {
	let order = [], state = {}, err = null;
	function visit(item, stack) {
		if (err)
			return;
		let key = item.type + '/' + item.id;
		if (state[key] == 'done')
			return;
		if (state[key] == 'visiting')
			return;	// cycle: already on the stack, ignore the back edge
		state[key] = 'visiting';
		for (let u in item.manifest.dependencies) {
			let dep = by_url[u];
			if (!dep || !dep.manifest) {
				err = { error: 'dep_not_found', item: item.id, url: u };
				return;
			}
			visit(dep, stack);
		}
		state[key] = 'done';
		push(order, item);
	}
	for (let r in roots)
		visit(r, []);
	return err ? err : { order: order };
};

export function read_cache() {
	let c = read_json(cache_path(), 16777216);
	return (type(c) == 'object' && type(c.items) == 'array') ? c : null;
};

function download_one(url, key, timeout) {
	let p = N.probe([ { key: key, url: url } ], { concurrency: 1, timeout: timeout });
	return p;
}

// Downloads index.json and all manifests into the tmpfs cache.
export function fetch(cfg, ctx) {
	ctx = ctx ?? NULL_CTX;
	let url = cfg.repo.url;
	ctx.progress(2, 'Загрузка индекса репозитория: ' + url);
	let p = download_one(url, 'index', 30);
	let r = p.results.index;
	let cls = N.classify(r?.rc, r?.err, r?.bytes, 2);
	if (!cls.ok || !r.body) {
		N.cleanup(p.dir);
		return fail('download_failed', sprintf('Не удалось загрузить индекс репозитория (%s). %s',
			N.ERROR_TEXT[cls.error] ?? cls.error, r?.summary ?? ''), { url: url });
	}
	if (r.bytes > MAX_INDEX_BYTES) {
		N.cleanup(p.dir);
		return fail('too_large', 'Индекс репозитория больше 1 МиБ');
	}
	let obj = null;
	try {
		obj = json(fs.readfile(r.body));
	}
	catch (e) {
		obj = null;
	}
	N.cleanup(p.dir);
	let idx = parse_index(obj);
	if (idx.error)
		return fail('bad_index', 'Индекс репозитория повреждён или имеет неизвестный формат');
	if (ctx.cancelled())
		return fail('cancelled', 'Отменено');

	ctx.progress(10, sprintf('Загрузка манифестов: %d шт.', length(idx.items)));
	let tasks = [];
	for (let i = 0; i < length(idx.items); i++)
		push(tasks, { key: 'm' + i, url: idx.items[i].manifest_url });
	let pm = N.probe(tasks, { concurrency: 8, timeout: 20 });
	let items = [], errors = 0;
	for (let i = 0; i < length(idx.items); i++) {
		let it = idx.items[i], res = pm.results['m' + i];
		let entry = { id: it.id, type: it.type, manifest_url: it.manifest_url, manifest: null, error: null };
		let c = N.classify(res?.rc, res?.err, res?.bytes, 2);
		if (!c.ok || !res.body || res.bytes > MAX_MANIFEST_BYTES) {
			entry.error = c.ok ? 'too_large' : c.error;
			errors++;
		}
		else {
			let mo = null;
			try {
				mo = json(fs.readfile(res.body));
			}
			catch (e) {
				mo = null;
			}
			let m = parse_manifest(mo, it);
			if (m.error) {
				entry.error = m.error;
				errors++;
			}
			else {
				entry.manifest = m;
			}
		}
		push(items, entry);
	}
	N.cleanup(pm.dir);
	mkdir_p(P.run + '/repo');
	let cache = { fetched_at: time(), url: url, items: items, errors: errors, skipped: idx.skipped };
	if (!write_json(cache_path(), cache))
		return fail('write_failed', 'Не удалось сохранить кэш репозитория');
	ctx.progress(100, sprintf('Индекс загружен: %d элементов, ошибок %d', length(items), errors));
	return ok({ fetched_at: cache.fetched_at, total: length(items), errors: errors });
};

export function list(cfg, only_type) {
	let cache = read_cache();
	if (!cache)
		return ok({ fetched_at: null, url: cfg.repo.url, items: [] });
	let idx = S.scan();
	let out = [];
	for (let e in cache.items) {
		if (only_type != null && e.type != only_type)
			continue;
		let m = e.manifest;
		let inst = idx.items[e.type]?.[e.id];
		if (inst && inst.source == 'user')
			inst = null;
		push(out, {
			id: e.id,
			type: e.type,
			name: m?.name ?? e.id,
			version: m?.version ?? null,
			author: m?.author ?? '',
			description: m?.description ?? '',
			installed: !!inst,
			installed_version: inst?.version ?? null,
			installed_source: inst?.source ?? null,
			update_available: is_update(inst, m),
			supported: !S.TYPES[e.type].unsupported,
			size: inst ? (fs.stat(inst.file)?.size ?? null) : null,
			error: e.error
		});
	}
	return ok({ fetched_at: cache.fetched_at, url: cache.url, items: out });
};

function ext_of(url) {
	let parts = split(split(url, '?')[0], '/');
	let m = match(parts[length(parts) - 1], /\.([A-Za-z0-9]{1,8})$/);
	return m ? lc(m[1]) : 'txt';
}

function find_cached(cache, id, types) {
	return filter(cache.items, (e) => e.id == id && e.manifest != null && !S.TYPES[e.type].unsupported &&
		(types == null || index(types, e.type) >= 0));
}

function install_artifact(entry, dl, dep_ids) {
	let m = entry.manifest, t = S.TYPES[entry.type];
	let fdir = P.etc + '/files/' + t.dir, mdir = P.etc + '/manifests/' + t.dir;
	if (!mkdir_p(fdir) || !mkdir_p(mdir))
		return fail('write_failed', 'Не удалось создать каталоги в ' + P.etc);
	let dst = fdir + '/' + m.id + '.' + ext_of(m.artifact.url);
	let tmp = uniq_name(dst + '.tmp');
	let r = run([ 'cp', dl.body, tmp ], { timeout: 120000, limit: 4096 });
	if (r.rc != 0) {
		fs.unlink(tmp);
		return fail('write_failed', 'Не удалось записать ' + dst + ': ' + trim(r.stderr));
	}
	fs.chmod(tmp, MODE_FILE);
	if (sha256_file(tmp) != m.artifact.sha256) {
		fs.unlink(tmp);
		return fail('write_failed', 'Файл повредился при записи на флеш: ' + dst);
	}
	let old = read_json(mdir + '/' + m.id + '.json', MAX_MANIFEST_BYTES);
	if (!fs.rename(tmp, dst)) {
		fs.unlink(tmp);
		return fail('write_failed', 'Не удалось записать ' + dst);
	}
	let local = {
		schema: 1, id: m.id, type: entry.type, name: m.name, version: m.version, author: m.author, description: m.description,
		dependencies: dep_ids, file: dst, source: 'repo', sha256: m.artifact.sha256, installed_at: time(),
		manifest_url: m.manifest_url
	};
	if (!write_json(mdir + '/' + m.id + '.json', local))
		return fail('write_failed', 'Не удалось записать манифест ' + m.id);
	if (type(old?.file) == 'string' && old.file != dst && S.path_under(old.file, P.etc + '/files'))
		fs.unlink(old.file);
	return ok({ file: dst });
}

// ids: [id]; opts: { upgrade_deps, only_updates }
export function install(cfg, ids, ctx, opts) {
	ctx = ctx ?? NULL_CTX;
	opts = opts ?? {};
	let cache = read_cache();
	if (!cache || opts.refetch) {
		let f = fetch(cfg, ctx);
		if (!f.ok)
			return f;
		cache = read_cache();
	}
	let by_url = {};
	for (let e in cache.items)
		by_url[e.manifest_url] = e;

	let roots = [];
	for (let id in ids) {
		if (!is_id(id))
			return fail('bad_id', 'Недопустимый идентификатор: ' + id);
		let c = find_cached(cache, id, opts.types);
		if (!length(c)) {
			let any = filter(cache.items, (e) => e.id == id);
			if (length(any) && S.TYPES[any[0].type].unsupported)
				return fail('unsupported_item', sprintf('Элемент «%s» (%s) не поддерживается на роутере', id, any[0].type));
			return fail('not_in_repo', sprintf('Элемент «%s» не найден в индексе репозитория', id));
		}
		if (length(c) > 1)
			return fail('ambiguous_id', sprintf('Идентификатор «%s» встречается в нескольких типах: %s', id, join(', ', map(c, (e) => e.type))));
		push(roots, c[0]);
	}

	let ro = resolve_order(roots, by_url);
	if (ro.error)
		return fail('dep_not_found', sprintf('Зависимость элемента «%s» не найдена в индексе: %s', ro.item, ro.url));

	let idx = S.scan();
	let root_keys = map(roots, (e) => e.type + '/' + e.id);
	let plan = [], skipped = [];
	for (let e in ro.order) {
		let inst = idx.items[e.type]?.[e.id];
		if (inst?.source == 'user')
			inst = null;
		let is_root = index(root_keys, e.type + '/' + e.id) >= 0;
		if (is_root && !opts.only_updates)
			push(plan, e);
		else if (!inst || ((is_root || opts.upgrade_deps) && is_update(inst, e.manifest)))
			push(plan, e);
		else
			push(skipped, e.id);
	}
	if (!length(plan))
		return ok({ installed: [], skipped: skipped, message: 'Всё уже установлено и обновлено' });

	ctx.progress(20, sprintf('Загрузка файлов: %d шт.', length(plan)));
	let tasks = [];
	for (let i = 0; i < length(plan); i++)
		push(tasks, { key: 'a' + i, url: plan[i].manifest.artifact.url });
	let dl = N.probe(tasks, { concurrency: 4, timeout: 30 });
	if (ctx.cancelled()) {
		N.cleanup(dl.dir);
		return fail('cancelled', 'Отменено');
	}

	let total_kib = 0;
	for (let i = 0; i < length(plan); i++) {
		let e = plan[i], res = dl.results['a' + i];
		let c = N.classify(res?.rc, res?.err, res?.bytes, 0);
		if (!c.ok || !res.body) {
			N.cleanup(dl.dir);
			return fail('download_failed', sprintf('Не удалось скачать «%s»: %s. %s', e.id, N.ERROR_TEXT[c.error] ?? c.error ?? 'нет файла', res?.summary ?? ''));
		}
		if (res.bytes > MAX_ARTIFACT_BYTES) {
			N.cleanup(dl.dir);
			return fail('too_large', sprintf('Файл «%s» слишком большой для роутера (%d байт)', e.id, res.bytes));
		}
		let sum = sha256_file(res.body);
		if (sum != e.manifest.artifact.sha256) {
			N.cleanup(dl.dir);
			return fail('sha256_mismatch', sprintf('Контрольная сумма «%s» не совпала с манифестом — файл отклонён', e.id));
		}
		total_kib += int((res.bytes + 1023) / 1024);
	}

	let avail = df_avail_kib(P.etc) ?? df_avail_kib('/etc');
	if (avail != null && avail < total_kib + RESERVE_KIB) {
		N.cleanup(dl.dir);
		return fail('no_space', sprintf('Недостаточно места на флеше: нужно %d КиБ, свободно %d КиБ', total_kib + RESERVE_KIB, avail));
	}

	let installed = [], failed = null;
	for (let i = 0; i < length(plan); i++) {
		let e = plan[i];
		ctx.progress(40 + int(55 * i / length(plan)), 'Установка ' + e.id);
		let dep_ids = [];
		for (let u in e.manifest.dependencies)
			if (by_url[u])
				push(dep_ids, by_url[u].id);
		let r = install_artifact(e, dl.results['a' + i], dep_ids);
		if (!r.ok) {
			failed = r;
			break;
		}
		push(installed, e.id);
		log('info', sprintf('установлен %s %s версии %s', e.type, e.id, e.manifest.version));
	}
	N.cleanup(dl.dir);
	S.flush_cache();
	if (failed) {
		failed.installed = installed;
		return failed;
	}
	return ok({ installed: installed, skipped: skipped, types: map(plan, (e) => e.type) });
};

export function remove(cfg, id) {
	if (!is_id(id))
		return fail('bad_id', 'Недопустимый идентификатор: ' + id);
	let idx = S.scan();
	let all = S.find_all(idx, id);
	let mine = filter(all, (it) => it.source == 'repo');
	if (!length(mine)) {
		if (length(filter(all, (it) => it.source == 'url')))
			return fail('readonly_item', sprintf('«%s» — подписка по ссылке; удаляется командой zaprett sources delete', id));
		if (length(all))
			return fail('readonly_item', sprintf('Элемент «%s» входит в состав пакета или является пользовательским и не удаляется', id));
		return fail('not_installed', sprintf('Элемент «%s» не установлен', id));
	}
	if (length(mine) > 1)
		return fail('ambiguous_id', sprintf('Идентификатор «%s» установлен в нескольких типах', id));
	let it = mine[0];
	let fallback = S.bundle_item(it.type, id);
	if (!fallback) {
		let users = filter(S.used_by(idx, id), (x) => x != id);
		if (length(users))
			return fail('item_in_use', sprintf('Элемент «%s» нужен стратегиям: %s', id, join(', ', users)), { used_by: users });
		if (S.is_active(it, cfg, idx))
			return fail('item_active', sprintf('Элемент «%s» сейчас используется в настройках. Сначала отключите его.', id));
	}
	if (S.path_under(it.file, P.etc + '/files'))
		fs.unlink(it.file);
	fs.unlink(it.manifest_path);
	log('info', sprintf('удалён %s %s', it.type, id));
	return ok({ removed: id, type: it.type, fallback: fallback ? 'bundle' : null });
};

// Upgrades installed items. ids == null -> every item installed from the repository.
export function upgrade(cfg, ids, ctx, opts) {
	ctx = ctx ?? NULL_CTX;
	opts = opts ?? {};
	if (!opts.no_fetch) {
		let f = fetch(cfg, ctx);
		if (!f.ok)
			return f;
	}
	let cache = read_cache();
	if (!cache)
		return fail('repo_not_fetched', 'Индекс репозитория не загружен');
	let idx = S.scan();
	let targets = [], up_to_date = [];
	for (let e in cache.items) {
		if (!e.manifest || S.TYPES[e.type].unsupported)
			continue;
		let inst = idx.items[e.type]?.[e.id];
		if (!inst || inst.source == 'user')
			continue;
		if (ids == null && inst.source != 'repo')
			continue;
		if (ids != null && index(ids, e.id) < 0)
			continue;
		if (is_update(inst, e.manifest))
			push(targets, e.id);
		else
			push(up_to_date, e.id);
	}
	if (ids != null) {
		for (let id in ids)
			if (index(targets, id) < 0 && index(up_to_date, id) < 0)
				return fail('not_installed', sprintf('Элемент «%s» не установлен или отсутствует в репозитории', id));
	}
	if (!length(targets))
		return ok({ installed: [], up_to_date: up_to_date, message: 'Обновлений нет' });
	let r = install(cfg, uniq(targets), ctx, { upgrade_deps: true, only_updates: false });
	r.up_to_date = up_to_date;
	return r;
};
