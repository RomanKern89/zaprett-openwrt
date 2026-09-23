// zaprett: automatic selection without stopping the bypass (contract v1.6 §17, mode `isolated`).
// The checks of the tester run as the system user zaprett-test. Table inet zaprett sends the traffic of that user to
// queue qnum+1 of a second engine instance (procd instance `test`) and keeps it away from the main queue; the main
// engine keeps serving the clients. Everything is described by files in /var/run/zaprett: a reload, `fw apply`, the
// fw4 include or hotplug during a test render the same rules, and a test that died is cleaned up from these files.
'use strict';

import * as fs from 'fs';
import * as ubus from 'ubus';
import { P, read_json, write_json, atomic_write, mkdir_p, is_file, run } from 'zaprett.util';
import { CLIENT_MARK, TEST_MARK } from 'zaprett.config';

export const USER = 'zaprett-test';
export const ID = 29411;	// uid and gid given by uci-defaults (docs/BACKEND.md); the real ones are read from passwd
export const INSTANCE = 'test';

// Reasons of the fallback to mode exclusive (field mode_reason).
export const REASONS = [ 'forced', 'engine_not_running', 'no_test_user', 'qnum_out_of_range', 'mark_conflict',
	'write_failed', 'nft_rejected', 'instance_failed' ];

// Isolation part of the table: { uid, qnum, ports: null | { tcp, udp } } (ports null = baseline, no queue).
export function state_path() {
	return P.run + '/test-isolated.json';
};

// Arguments and engine of the candidate, read by the init script for instance `test`.
export function args_path() {
	return P.run + '/test-args';
};

export function engine_file() {
	return P.run + '/test-engine';
};

// Pure: { uid, gid } of a user in passwd text, or null.
export function passwd_entry(text, name) {
	for (let l in split(text ?? '', '\n')) {
		let f = split(l, ':');
		if (length(f) >= 4 && f[0] == name && match(f[2], /^[0-9]+$/) && match(f[3], /^[0-9]+$/))
			return { uid: int(f[2]), gid: int(f[3]) };
	}
	return null;
};

// The test user; uid 0 would be root and is never accepted.
export function test_user() {
	let e = passwd_entry(fs.readfile(P.passwd, 262144), USER);
	return (e && e.uid > 0) ? e : null;
};

// Pure: why the isolated mode cannot be used, or null. info: { enabled, running, stopped, user }.
export function refusal(cfg, info, opts) {
	if (opts?.exclusive)
		return 'forced';
	if (!info?.enabled || !info?.running || info?.stopped)
		return 'engine_not_running';
	if (!info?.user)
		return 'no_test_user';
	if (cfg.qnum >= 65535)
		return 'qnum_out_of_range';
	if ((cfg.desync_mark | cfg.postnat_mark | CLIENT_MARK) & TEST_MARK)
		return 'mark_conflict';
	return null;
};

function port_list(v) {
	return filter((type(v) == 'array') ? v : [], (p) => type(p) == 'string' && match(p, /^[0-9]+(-[0-9]+)?$/) != null);
}

// Pure: validated state for the renderer (the file is only root's, but nothing unchecked goes into nft text).
export function parse_state(o) {
	if (type(o) != 'object' || type(o.uid) != 'int' || o.uid <= 0 || type(o.qnum) != 'int' || o.qnum < 1 || o.qnum > 65535)
		return null;
	let ports = (type(o.ports) == 'object') ? { tcp: port_list(o.ports.tcp), udp: port_list(o.ports.udp) } : null;
	return { uid: o.uid, qnum: o.qnum, ports: ports };
};

export function read_state() {
	return parse_state(read_json(state_path(), 65536));
};

export function write_state(uid, qnum, ports) {
	return mkdir_p(P.run) && write_json(state_path(), { uid: uid, qnum: qnum, ports: ports });
};

export function write_candidate(engine, args) {
	return mkdir_p(P.run) && atomic_write(args_path(), join('\n', args) + '\n') && atomic_write(engine_file(), engine + '\n');
};

// Anything of an isolated test left in /var/run/zaprett.
export function leftover() {
	return is_file(state_path()) || is_file(args_path()) || is_file(engine_file());
};

export function instance_state() {
	let conn = ubus.connect();
	let res = { running: false, pid: null, present: false };
	if (!conn)
		return res;
	let l = conn.call('service', 'list', { name: 'zaprett' });
	conn.disconnect();
	let inst = l?.zaprett?.instances?.[INSTANCE];
	if (type(inst) == 'object') {
		res.present = true;
		res.running = !!inst.running;
		res.pid = inst.pid ?? null;
	}
	return res;
};

export function wait_instance(want, timeout_ms) {
	let waited = 0;
	while (true) {
		let st = instance_state();
		if (st.running == want || waited >= timeout_ms)
			return st;
		sleep(250);
		waited += 250;
	}
};

// Removes instance `test` alone: `service delete` with an instance name leaves the main engine as it is.
export function drop_instance() {
	if (!instance_state().present)
		return false;
	let conn = ubus.connect();
	if (!conn)
		return false;
	conn.call('service', 'delete', { name: 'zaprett', instance: INSTANCE });
	conn.disconnect();
	return true;
};

// Checks of a test killed with its job may still run as the test user.
export function kill_user_processes() {
	if (!test_user())
		return false;
	return run([ P.ssd, '-K', '-s', 'KILL', '-u', USER ], { timeout: 10000, limit: 4096 }).rc == 0;
};

// Removes the files, instance `test`, stray checks and (through fw_apply) the test chains. Returns the fw_apply result.
export function cleanup(fw_apply) {
	fs.unlink(state_path());
	fs.unlink(args_path());
	fs.unlink(engine_file());
	drop_instance();
	kill_user_processes();
	return (type(fw_apply) == 'function') ? fw_apply() : null;
};
