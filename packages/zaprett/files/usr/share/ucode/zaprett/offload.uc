// zaprett: flow offloading (ARCHITECTURE §6.1, contract v1.4 §15.1). fw4 flow offloading lets established
// connections bypass netfilter, so nfqws would never see them. Modes:
//   auto — fw4 offloading is switched off while zaprett is enabled and restored when zaprett is disabled;
//   own  — the same, and table inet zaprett gets its own flowtable that takes a connection only after the
//          engine has seen its first packets (the table itself is rendered by zaprett.nft);
//   keep — nothing is touched.
'use strict';

import * as fs from 'fs';
import * as ubus from 'ubus';
import { P, read_json, write_json, mkdir_p, run_quiet, log, ok, fail } from 'zaprett.util';
import * as C from 'zaprett.config';

export const MODES_OFF = [ 'auto', 'own' ];

export function saved_path() {
	return P.etc + '/offload-saved.json';
};

function reload_firewall() {
	return run_quiet([ P.firewall_init, 'reload' ], 60000) == 0;
}

// fw4 offloading as it was before zaprett switched it off: the saved copy, or the current firewall setting.
export function original(saved, d) {
	if (type(saved) == 'object')
		return { flow_offloading: saved.flow_offloading == '1', flow_offloading_hw: saved.flow_offloading_hw == '1' };
	return { flow_offloading: d?.flow_offloading == '1', flow_offloading_hw: d?.flow_offloading_hw == '1' };
};

export function state(cfg) {
	let d = C.fw_defaults_section();
	let saved = read_json(saved_path(), 4096);
	return {
		fw4: {
			flow_offloading: (d?.flow_offloading == '1'),
			flow_offloading_hw: (d?.flow_offloading_hw == '1')
		},
		mode: cfg.flow_offload,
		saved: saved,
		original: original(saved, d)
	};
};

// Pure: devices of the own flowtable, as fw4 picks them for software offloading: related_physdevs of every zone
// in /var/run/fw4.state whose /sys/class/net/<dev> exists (exists(dev) is a callback), sorted, unique.
export function zone_devices(fw4_state, exists) {
	let out = [];
	for (let z in ((type(fw4_state?.zones) == 'array') ? fw4_state.zones : []))
		for (let d in ((type(z?.related_physdevs) == 'array') ? z.related_physdevs : []))
			if (type(d) == 'string' && d != '' && index(out, d) < 0 && exists(d))
				push(out, d);
	return sort(out);
};

// Pure: lower devices for hardware offloading (fw4 resolve_lower_devices): bridges and VLANs are replaced by
// their lower_* devices, recursively. devtype(dev) and lowers(dev) are callbacks.
export function lower_devices(devs, devtype, lowers) {
	let out = [];
	function walk(d, depth) {
		if (depth > 8)
			return;
		let t = devtype(d);
		if (t == 'bridge' || t == 'vlan') {
			for (let l in lowers(d))
				walk(l, depth + 1);
			return;
		}
		if (index(out, d) < 0)
			push(out, d);
	}
	for (let d in devs)
		walk(d, 0);
	return sort(out);
};

function sys_exists(dev) {
	return fs.stat(P.sys_net + '/' + dev) != null;
}

function sys_lowers(dev) {
	let res = [];
	let dir = fs.opendir(P.sys_net + '/' + dev);
	if (!dir)
		return res;
	let e;
	while ((e = dir.read()) != null)
		if (substr(e, 0, 6) == 'lower_')
			push(res, substr(e, 6));
	dir.close();
	return res;
}

// Wanted flowtable of mode own: null when not wanted (another mode, or fw4 offloading was off before zaprett:
// zaprett does not switch acceleration on for a router that did not use it). Otherwise
// { devices: [...], hw_devices: [...] | null } — hw_devices only when hardware offloading was on.
export function flowtable_plan(cfg, hooks) {
	hooks = hooks ?? {};
	if (cfg.flow_offload != 'own')
		return null;
	let st = hooks.state ?? state(cfg);
	if (!st.original.flow_offloading)
		return null;
	let fw4 = hooks.fw4_state ?? read_json(P.fw4_state, 1048576);
	let exists = hooks.exists ?? sys_exists;
	let devices = zone_devices(fw4, exists);
	let hw = null;
	if (st.original.flow_offloading_hw && length(devices)) {
		let devtype = hooks.devtype;
		if (!devtype) {
			let conn = ubus.connect();
			let ds = conn ? conn.call('network.device', 'status', {}) : null;
			if (conn)
				conn.disconnect();
			devtype = (d) => ds?.[d]?.devtype;
		}
		hw = lower_devices(devices, devtype, hooks.lowers ?? sys_lowers);
	}
	return { devices: devices, hw_devices: hw };
};

export function apply(cfg) {
	if (index(MODES_OFF, cfg.flow_offload) < 0)
		return ok({ changed: false });
	let d = C.fw_defaults_section();
	if (!d || d.flow_offloading != '1')
		return ok({ changed: false });
	mkdir_p(P.etc);
	let prev = read_json(saved_path(), 4096);
	if (type(prev) != 'object') {
		if (!write_json(saved_path(), {
			flow_offloading: d.flow_offloading,
			flow_offloading_hw: d.flow_offloading_hw,
			saved_at: time()
		}))
			return fail('write_failed', 'Не удалось сохранить исходные настройки ускорения (flow offloading)');
	}
	if (!C.fw_set_offload({ flow_offloading: '0', flow_offloading_hw: (d.flow_offloading_hw != null) ? '0' : null }))
		return fail('uci_failed', 'Не удалось выключить flow offloading в /etc/config/firewall');
	reload_firewall();
	log('notice', sprintf('ускорение fw4 (flow offloading) выключено на время работы zaprett (режим %s)', cfg.flow_offload));
	return ok({ changed: true });
};

export function restore() {
	let prev = read_json(saved_path(), 4096);
	if (type(prev) != 'object')
		return ok({ changed: false });
	let d = C.fw_defaults_section();
	let changed = false;
	// restore only if the value is still the one zaprett set; a manual change wins
	if (d && d.flow_offloading != '1' && prev.flow_offloading == '1') {
		if (!C.fw_set_offload({ flow_offloading: '1', flow_offloading_hw: prev.flow_offloading_hw }))
			return fail('uci_failed', 'Не удалось вернуть flow offloading в /etc/config/firewall');
		reload_firewall();
		changed = true;
		log('notice', 'flow offloading возвращён в исходное состояние');
	}
	fs.unlink(saved_path());
	return ok({ changed: changed });
};
