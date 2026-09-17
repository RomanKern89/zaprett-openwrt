// Minimal test harness for the zaprett backend. Runs on the router with plain ucode.
// Environment: ZTEST_ROOT (package root: Makefile, files/, tests/), ZTEST_WORK (writable sandbox),
// ZTEST_NFQWS (optional nfqws binary for dry-run checks).
'use strict';

import * as fs from 'fs';
import { set_paths, run } from 'zaprett.util';

const st = { name: '', pass: 0, fail: 0 };

export const ROOT = getenv('ZTEST_ROOT') ?? '.';
export const WORK = getenv('ZTEST_WORK') ?? '/tmp/zaprett-dev-backend/work';
export const NFQWS = getenv('ZTEST_NFQWS');

export function begin(name) {
	st.name = name;
};

function same(a, b) {
	return sprintf('%J', a) == sprintf('%J', b);
}

export function ok(cond, msg) {
	if (cond)
		st.pass++;
	else {
		st.fail++;
		print(sprintf('FAIL [%s] %s\n', st.name, msg));
	}
	return !!cond;
};

export function eq(actual, expected, msg) {
	return ok(same(actual, expected), sprintf('%s: expected %J, got %J', msg, expected, actual));
};

export function has(list, value, msg) {
	return ok(type(list) == 'array' && index(list, value) >= 0, sprintf('%s: %J not in %J', msg, value, list));
};

export function lacks(list, value, msg) {
	return ok(type(list) != 'array' || index(list, value) < 0, sprintf('%s: %J unexpectedly in %J', msg, value, list));
};

// The harness must itself be able to fail: comparisons of different values are false.
export function selfcheck() {
	ok(!same(1, 2), 'harness: 1 != 2');
	ok(!same([ 'a' ], [ 'b' ]), 'harness: [a] != [b]');
	ok(!same({ a: 1 }, { a: '1' }), 'harness: int 1 != string 1');
	ok(same({ a: [ 1, 2 ] }, { a: [ 1, 2 ] }), 'harness: equal objects');
};

export function finish() {
	print(sprintf('RESULT %s pass=%d fail=%d\n', st.name, st.pass, st.fail));
	return st.fail ? 1 : 0;
};

// Fresh sandbox <WORK>/<sub> with fixtures copied and all backend paths redirected into it.
export function sandbox(sub) {
	let w = WORK + '/' + sub;
	if (index(w, '/tmp/') != 0)
		die('sandbox must live under /tmp');
	system([ 'rm', '-rf', w ]);
	system([ 'mkdir', '-p', w + '/run/tmp', w + '/lock', w + '/libexec', w + '/share/lua' ]);
	let fx = ROOT + '/tests/fixtures';
	system([ 'cp', '-R', fx + '/bundle', w + '/bundle' ]);
	system([ 'cp', '-R', fx + '/etc', w + '/etc' ]);
	system([ 'cp', '-R', fx + '/user', w + '/etc/user' ]);
	// UCI in the sandbox: the package default configuration plus a minimal firewall config
	system([ 'mkdir', '-p', w + '/uci', w + '/uci.delta' ]);
	system([ 'cp', ROOT + '/files/etc/config/zaprett', w + '/uci/zaprett' ]);
	fs.writefile(w + '/uci/firewall', "\nconfig defaults\n\toption input 'REJECT'\n\toption flow_offloading '1'\n\toption flow_offloading_hw '1'\n");
	if (NFQWS && fs.stat(NFQWS)?.type == 'file') {
		system([ 'cp', NFQWS, w + '/libexec/nfqws' ]);
		fs.chmod(w + '/libexec/nfqws', 493);
	}
	set_paths({
		run: w + '/run', tmp: w + '/run/tmp', etc: w + '/etc', user: w + '/etc/user', share: w + '/share',
		bundle: w + '/bundle', libexec: w + '/libexec', lock_job: w + '/lock/job.lock', lock_main: w + '/lock/main.lock', lock_fw: w + '/lock/fw.lock',
		init: w + '/no-init', presets: w + '/presets.json', crontab: w + '/crontab',
		guard_hostlist: ROOT + '/files/usr/share/zaprett/guard/hostlist-guard.txt',
		guard_ipset: ROOT + '/files/usr/share/zaprett/guard/ipset-guard.txt',
		probe: ROOT + '/files/usr/share/zaprett/probe.sh', cli: ROOT + '/files/usr/share/zaprett/cli.uc',
		// nothing outside the sandbox may be started or changed by tests
		ucode: w + '/no-ucode', firewall_init: w + '/no-firewall-init', cron_init: w + '/no-cron-init', syslog: false,
		uci_confdir: w + '/uci', uci_savedir: w + '/uci.delta'
	});
	return w;
};

export function write(path, text) {
	system([ 'mkdir', '-p', fs.dirname(path) ]);
	return fs.writefile(path, text);
};
