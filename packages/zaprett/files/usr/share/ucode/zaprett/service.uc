// zaprett: service state and control through the procd init script.
'use strict';

import * as fs from 'fs';
import * as ubus from 'ubus';
import { P, VERSION, run, run_quiet, read_json, write_json, mkdir_p, is_file, ok, fail } from 'zaprett.util';
import * as C from 'zaprett.config';
import * as S from 'zaprett.store';
import * as N from 'zaprett.nft';
import * as O from 'zaprett.offload';

export function engine_path(engine) {
	return P.libexec + '/' + engine;
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

	let applied = N.is_applied();
	let wan = N.query_wan(cfg);
	if (cfg.enabled && !inst.running)
		add('not_running');
	if (inst.running && !applied)
		add('nft_not_applied');
	if (!length(wan.v4) && !(cfg.ipv6 && length(wan.v6)))
		add('no_wan');
	if (override)
		add('test_running');
	let off = O.state(cfg);
	if (off.fw4.flow_offloading && (cfg.enabled || inst.running))
		add('flow_offload_enabled');

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
		flow_offload: { fw4: off.fw4.flow_offloading, fw4_hw: off.fw4.flow_offloading_hw, mode: cfg.flow_offload },
		clients_mode: cfg.clients_mode,
		test_mode: !!override,
		warnings: warnings,
		details: { generate: gen, bad_options: cfg.bad_options },
		version: VERSION
	};
};
