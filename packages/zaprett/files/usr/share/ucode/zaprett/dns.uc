// zaprett: encrypted DNS on the router (contract v1.4 §15.3): which provider works, and installation of
// https-dns-proxy with the package manager of the router (apk on 25.12, opkg on 24.10).
'use strict';

import * as ubus from 'ubus';
import { P, run, is_file, log, ok, fail, NULL_CTX } from 'zaprett.util';

// A provider counts as switched on when its package is installed (init script present) and its procd service
// has a running instance.
export const PROVIDERS = [ 'https-dns-proxy', 'stubby', 'dnscrypt-proxy' ];
export const SETUP_PROVIDER = 'https-dns-proxy';
export const SETUP_LUCI = 'luci-app-https-dns-proxy';

// Pure: { encrypted, provider } from [ { id, installed, running } ] (the first working provider of PROVIDERS order).
export function summarize(info) {
	for (let id in PROVIDERS)
		for (let p in (info ?? []))
			if (p?.id == id && p.installed && p.running)
				return { encrypted: true, provider: id };
	return { encrypted: false, provider: null };
};

// Pure: does a `service list` answer of one service have a running instance?
export function service_running(list, name) {
	let inst = list?.[name]?.instances;
	if (type(inst) != 'object')
		return false;
	for (let k, v in inst)
		if (type(v) == 'object' && v.running)
			return true;
	return false;
};

export function providers_info() {
	let conn = ubus.connect();
	let out = [];
	for (let id in PROVIDERS) {
		let installed = is_file(P.initd + '/' + id);
		let running = false;
		if (installed && conn)
			running = service_running(conn.call('service', 'list', { name: id }), id);
		push(out, { id: id, installed: installed, running: running });
	}
	if (conn)
		conn.disconnect();
	return out;
};

export function status() {
	return summarize(providers_info());
};

// Pure: package manager commands for the router. has_apk / has_opkg: which tool exists; luci: LuCI is installed.
export function setup_plan(has_apk, has_opkg, luci) {
	let pkgs = [ SETUP_PROVIDER ];
	if (luci)
		push(pkgs, SETUP_LUCI);
	if (has_apk) {
		let add = [ P.apk, 'add' ];
		for (let p in pkgs)
			push(add, p);
		return { manager: 'apk', packages: pkgs, update: [ P.apk, 'update' ], install: add };
	}
	if (has_opkg) {
		let add = [ P.opkg, 'install' ];
		for (let p in pkgs)
			push(add, p);
		return { manager: 'opkg', packages: pkgs, update: [ P.opkg, 'update' ], install: add };
	}
	return null;
};

function out_tail(r) {
	let t = trim((r.stdout ?? '') + '\n' + (r.stderr ?? ''));
	return (length(t) > 400) ? substr(t, length(t) - 400) : t;
}

// Job `dns-setup`. hooks (unit tests): status, run, has_apk, has_opkg, luci, wait_ms.
export function setup(ctx, hooks) {
	ctx = ctx ?? NULL_CTX;
	hooks = hooks ?? {};
	let st_fn = hooks.status ?? status;
	let run_fn = hooks.run ?? run;
	let st = st_fn();
	if (st.encrypted)
		return ok({ changed: false, dns: st, message: sprintf('Шифрованный DNS уже работает (%s)', st.provider) });
	let plan = setup_plan(hooks.has_apk ?? is_file(P.apk), hooks.has_opkg ?? is_file(P.opkg), hooks.luci ?? is_file(P.luci));
	if (!plan)
		return fail('no_package_manager', 'На роутере нет ни apk, ни opkg — установить пакет нечем');
	ctx.progress(10, 'Обновление списка пакетов (' + plan.manager + ')');
	let r = run_fn(plan.update, { timeout: 300000, limit: 262144 });
	ctx.log(sprintf('%s: rc=%d', join(' ', plan.update), r.rc));
	if (r.rc != 0)
		return fail('package_update_failed', 'Не удалось обновить список пакетов: ' + out_tail(r));
	if (ctx.cancelled())
		return fail('cancelled', 'Задача отменена');
	ctx.progress(40, 'Установка ' + join(', ', plan.packages));
	r = run_fn(plan.install, { timeout: 600000, limit: 262144 });
	ctx.log(sprintf('%s: rc=%d', join(' ', plan.install), r.rc));
	if (r.rc != 0)
		return fail('package_install_failed', 'Не удалось установить ' + join(', ', plan.packages) + ': ' + out_tail(r));
	ctx.progress(80, 'Запуск ' + SETUP_PROVIDER);
	let init = P.initd + '/' + SETUP_PROVIDER;
	run_fn([ init, 'enable' ], { timeout: 30000 });
	run_fn([ init, 'restart' ], { timeout: 60000 });
	// procd starts the instances asynchronously
	let waited = 0, wait_ms = hooks.wait_ms ?? 15000;
	while (true) {
		st = st_fn();
		if (st.encrypted || waited >= wait_ms)
			break;
		sleep(500);
		waited += 500;
	}
	if (!st.encrypted)
		return fail('dns_not_running', SETUP_PROVIDER + ' установлен, но служба не запустилась. Подробности: logread -e https-dns-proxy',
			{ dns: st });
	log('notice', sprintf('шифрованный DNS: установлен и запущен %s', SETUP_PROVIDER));
	return ok({ changed: true, packages: plan.packages, manager: plan.manager, dns: st,
		message: 'Шифрованный DNS включён: ' + SETUP_PROVIDER });
};
