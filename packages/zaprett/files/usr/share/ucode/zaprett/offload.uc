// zaprett: flow_offload=auto (ARCHITECTURE §6.1). fw4 flow offloading lets established
// connections bypass netfilter, so nfqws would never see them; it is switched off while zaprett
// is enabled and restored when zaprett is disabled.
'use strict';

import * as fs from 'fs';
import { P, read_json, write_json, mkdir_p, run_quiet, log, ok, fail } from 'zaprett.util';
import * as C from 'zaprett.config';

export function saved_path() {
	return P.etc + '/offload-saved.json';
};

function reload_firewall() {
	return run_quiet([ P.firewall_init, 'reload' ], 60000) == 0;
}

export function state(cfg) {
	let d = C.fw_defaults_section();
	return {
		fw4: {
			flow_offloading: (d?.flow_offloading == '1'),
			flow_offloading_hw: (d?.flow_offloading_hw == '1')
		},
		mode: cfg.flow_offload,
		saved: read_json(saved_path(), 4096)
	};
};

export function apply(cfg) {
	if (cfg.flow_offload != 'auto')
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
	log('notice', 'flow offloading выключен на время работы zaprett (режим auto)');
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
