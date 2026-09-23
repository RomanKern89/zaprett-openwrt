// zaprett: service state and control through the procd init script.
'use strict';

import * as fs from 'fs';
import * as ubus from 'ubus';
import { P, VERSION, run, run_quiet, read_json, write_json, mkdir_p, is_file, ok, fail } from 'zaprett.util';
import * as C from 'zaprett.config';
import * as S from 'zaprett.store';
import * as N from 'zaprett.nft';
import * as O from 'zaprett.offload';
import * as ISO from 'zaprett.isolate';

export function engine_path(engine) {
	return P.libexec + '/' + engine;
};

// Written by `stop` of the init script, removed by `start`: the user stopped the service on purpose, so the
// watchdog must not start it again and the watchdog/monitor cron lines are removed (contract v1.3 §14.2).
export function stopped_path() {
	return P.run + '/stopped';
};

// Pure: cumulative packet counter of NFQUEUE <qnum> from /proc/net/netfilter/nfnetlink_queue (8th column,
// id_sequence; the 3rd one is the momentary queue length), or null when the queue is not bound.
export function queue_packets(text, qnum) {
	for (let l in split(text ?? '', '\n')) {
		let f = filter(split(trim(l), ' '), (x) => x != '');
		if (length(f) >= 8 && f[0] == '' + qnum && match(f[7], /^[0-9]+$/))
			return int(f[7]);
	}
	return null;
};

// Engine version from `<engine> --version`, cached by binary size+mtime.
export function engine_version(engine) {
	let bin = engine_path(engine);
	let st = fs.stat(bin);
	if (!st || st.type != 'file')
		return null;
	let cpath = P.run + '/cache/version-' + engine + '.json';
	let c = read_json(cpath, 4096);
	if (type(c) == 'object' && c.size == st.size && c.mtime == st.mtime)
		return c.version;
	let r = run([ bin, '--version' ], { timeout: 10000, limit: 8192 });
	let text = r.stdout + '\n' + r.stderr;
	let m = match(text, /github version (v[0-9A-Za-z._-]+)/) ?? match(text, /version (v?[0-9][0-9A-Za-z._-]*)/);
	let v = m ? m[1] : (trim(split(text, '\n')[0]) || null);
	if (v != null && length(v) > 64)
		v = substr(v, 0, 64);
	if (mkdir_p(P.run + '/cache'))
		write_json(cpath, { size: st.size, mtime: st.mtime, version: v });
	return v;
};

export function instance_state() {
	let conn = ubus.connect();
	let res = { running: false, pid: null };
	if (!conn)
		return res;
	let l = conn.call('service', 'list', { name: 'zaprett' });
	conn.disconnect();
	let inst = l?.zaprett?.instances?.engine;
	if (type(inst) == 'object') {
		res.running = !!inst.running;
		res.pid = inst.pid ?? null;
	}
	return res;
};

export function init_action(action, timeout) {
	if (!is_file(P.init))
		return { rc: -1, output: 'нет ' + P.init };
	let r = run([ P.init, action ], { timeout: timeout ?? 120000, limit: 65536 });
	return { rc: r.rc, output: trim(r.stdout + '\n' + r.stderr) };
};

// Autostart = rc.d symlink (what rc.common `enabled` checks). The init script itself is not called:
// every init action waits for procd_lock, which is held during start/stop/reload.
export function init_enabled() {
	return fs.lstat('/etc/rc.d/S95zaprett')?.type == 'link';
};

function nft_queue_available() {
	if (is_file('/sys/module/nft_queue/initstate') || fs.stat('/sys/module/nft_queue')?.type == 'directory')
		return true;
	let rel = trim(fs.readfile('/proc/sys/kernel/osrelease', 128) ?? '');
	return rel != '' && is_file('/lib/modules/' + rel + '/nft_queue.ko');
}

export function wait_running(want, timeout_ms) {
	let waited = 0;
	while (true) {
		let st = instance_state();
		if (st.running == want || waited >= timeout_ms)
			return st;
		sleep(250);
		waited += 250;
	}
};

export function status(cfg) {
	let idx = S.scan();
	let inst = instance_state();
	let gen = read_json(P.run + '/status.json', 65536);
	let override = read_json(P.run + '/test-override', 4096);
	let engine = cfg.engine;
	let sid = C.current_strategy_id(cfg, engine);
	let sitem = sid ? idx.items[engine]?.[sid] : null;
	let warnings = [];
	let add = (w) => { if (index(warnings, w) < 0) push(warnings, w); };

	for (let w in cfg.warnings)
		add(w);
	if (type(gen?.warnings) == 'array')
		for (let w in gen.warnings)
			add(w);
	if (gen && gen.ok == false)
		add('generate_failed');
	if (!is_file(engine_path(engine)))
		add('engine_missing');
	if (!nft_queue_available())
		add('nft_queue_missing');
	if (!sid)
		add('no_strategy');
	else if (!sitem)
		add('strategy_missing');
	// an enabled list that is not installed stops generation; a subscription not downloaded yet is only skipped
	for (let t in S.LIST_TYPES)
		for (let id in cfg[S.TYPES[t].uci])
			if (substr(id, 0, 4) != 'src-' && !idx.items[t]?.[id])
				add('list_missing');

	let table = N.list_table();
	let applied = (table != null);
	let own = N.has_flowtable(table);
	let wan = N.query_wan(cfg);
	if (cfg.enabled && !inst.running)
		add('not_running');
	if (inst.running && !applied)
		add('nft_not_applied');
	if (!length(wan.v4) && !(cfg.ipv6 && length(wan.v6)))
		add('no_wan');
	// isolated automatic selection (contract v1.6 §17): the engine works as usual, the test runs next to it
	if (override || is_file(ISO.state_path()))
		add('test_running');
	let off = O.state(cfg);
	if (off.fw4.flow_offloading && (cfg.enabled || inst.running))
		add('flow_offload_enabled');
	// mode own: the table is in the kernel, but without the flowtable it should have (contract v1.4 §15.1)
	if (applied && !own && O.flowtable_plan(cfg, { state: off }) != null)
		add('flowtable_failed');
	// IPv6 connections pass without the bypass: the table has no ip6 rules when ipv6=0
	if (wan.ipv6_default && !cfg.ipv6 && (cfg.enabled || inst.running))
		add('ipv6_wan_unhandled');
	let qp = queue_packets(fs.readfile(P.nfqueue, 65536), cfg.qnum);

	let wans = [];
	for (let d in wan.v4)
		push(wans, d);
	for (let d in wan.v6)
		if (index(wans, d) < 0)
			push(wans, d);

	return {
		ok: true,
		enabled: cfg.enabled,
		autostart: init_enabled(),
		running: inst.running,
		pid: inst.pid,
		engine: engine,
		engine_version: engine_version(engine),
		strategy: { id: sid || null, name: sitem?.name ?? null, source: sitem?.source ?? null },
		list_mode: cfg.list_mode,
		lists: cfg.lists,
		exclude_lists: cfg.exclude_lists,
		ipsets: cfg.ipsets,
		exclude_ipsets: cfg.exclude_ipsets,
		nft_applied: applied,
		wan: wans,
		flow_offload: { fw4: off.fw4.flow_offloading, fw4_hw: off.fw4.flow_offloading_hw, mode: cfg.flow_offload, own: own },
		clients_mode: cfg.clients_mode,
		test_mode: !!override,
		queue: (qp == null) ? null : { packets: qp },
		ipv6_wan: !!wan.ipv6_default,
		warnings: warnings,
		details: { generate: gen, bad_options: cfg.bad_options },
		version: VERSION
	};
};
