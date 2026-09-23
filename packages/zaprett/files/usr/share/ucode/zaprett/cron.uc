// zaprett: lines of /etc/crontabs/root (ARCHITECTURE §7, contract v1.3 §14.2). Each line carries its own mark;
// `zaprett cron sync` keeps exactly one line or none per mark and never touches other lines.
'use strict';

import * as fs from 'fs';
import { P, run, mkdir_p, atomic_write, is_file, ok, fail } from 'zaprett.util';
import * as C from 'zaprett.config';
import * as SV from 'zaprett.service';

export const MARK_AUTOUPDATE = '# zaprett-autoupdate';
export const MARK_WATCHDOG = '# zaprett-watchdog';
export const MARK_MONITOR = '# zaprett-monitor';
// order of the managed lines at the end of the crontab
export const MARKS = [ MARK_AUTOUPDATE, MARK_WATCHDOG, MARK_MONITOR ];

// Pure: new crontab text. want: { <mark>: function(minute) -> line | null }; a mark missing from `want` is left
// as it is, null removes its lines. The minute of an existing line ("M ...") is kept, so that a daily or hourly
// run does not move on every sync (of duplicates the last one wins); a new line gets random_minute.
export function render(text, want, random_minute) {
	let other = [], minutes = {};
	let wk = keys(want);
	let managed = filter(MARKS, (m) => index(wk, m) >= 0);
	for (let l in split(text ?? '', '\n')) {
		let mark = null;
		for (let m in managed)
			if (index(l, m) >= 0)
				mark = m;
		if (mark == null) {
			push(other, l);
			continue;
		}
		let mm = match(l, /^([0-9]+) /);
		if (mm && length(mm[1]) <= 2 && int(mm[1]) < 60)
			minutes[mark] = int(mm[1]);
	}
	while (length(other) && other[length(other) - 1] == '')
		pop(other);
	for (let m in managed) {
		let make = want[m];
		if (make != null)
			push(other, make(minutes[m] ?? random_minute));
	}
	return length(other) ? (join('\n', other) + '\n') : '';
};

export function autoupdate_line(hour) {
	return (minute) => sprintf('%d %d * * * /usr/bin/zaprett repo upgrade --all --foreground --quiet %s', minute, hour, MARK_AUTOUPDATE);
};

export function watchdog_line() {
	return (minute) => '*/5 * * * * /usr/bin/zaprett ensure --quiet ' + MARK_WATCHDOG;
};

export function monitor_line(interval) {
	if (interval >= 60)
		return (minute) => sprintf('%d * * * * /usr/bin/zaprett monitor run --quiet %s', minute, MARK_MONITOR);
	return (minute) => sprintf('*/%d * * * * /usr/bin/zaprett monitor run --quiet %s', interval, MARK_MONITOR);
};

// The daily run is needed for repository autoupdate and for every enabled subscription; subscriptions
// are updated by their own intervals even when repo.autoupdate is off.
export function autoupdate_needed(cfg, sources) {
	if (cfg.repo.autoupdate)
		return true;
	for (let s in sources)
		if (s.enabled && s.valid)
			return true;
	return false;
};

// Pure: all managed lines for a configuration. stopped: the user stopped the service (`zaprett stop`) —
// the watchdog must not start it again and the monitor has nothing to check.
export function wanted(cfg, sources, stopped) {
	let active = cfg.enabled && !stopped;
	let w = {};
	w[MARK_AUTOUPDATE] = autoupdate_needed(cfg, sources) ? autoupdate_line(cfg.repo.autoupdate_hour) : null;
	w[MARK_WATCHDOG] = (active && cfg.watchdog) ? watchdog_line() : null;
	w[MARK_MONITOR] = (active && cfg.monitor.enabled) ? monitor_line(cfg.monitor.interval) : null;
	return w;
};

function random_minute() {
	let uuid = fs.readfile('/proc/sys/kernel/random/uuid', 64) ?? '00';
	return hex(substr(replace(uuid, '-', ''), 0, 4)) % 60;
}

export function sync() {
	let cfg = C.load();
	let cur = fs.readfile(P.crontab, 1048576) ?? '';
	let next = render(cur, wanted(cfg, C.load_sources(), is_file(SV.stopped_path())), random_minute());
	if (cur == next)
		return ok({ changed: false });
	mkdir_p(fs.dirname(P.crontab));
	if (!atomic_write(P.crontab, next, 384))
		return fail('write_failed', 'Не удалось обновить ' + P.crontab);
	if (is_file(P.cron_init))
		run([ P.cron_init, 'restart' ], { timeout: 30000 });
	return ok({ changed: true });
};
