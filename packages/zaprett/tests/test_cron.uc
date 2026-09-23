'use strict';

// Contract v1.3 §14.2: autoupdate, watchdog and monitor lines of /etc/crontabs/root; the init script, uci-defaults
// and prerm that keep them. Scripts of the package run with every absolute path moved into the sandbox and with
// stubs of the commands they call, so the test router itself is never touched.
import * as fs from 'fs';
import * as T from 'ztest';
import { P, set_paths } from 'zaprett.util';
import * as C from 'zaprett.config';
import * as SV from 'zaprett.service';
import * as CR from 'zaprett.cron';
import * as CMD from 'zaprett.commands';

T.begin('cron');
T.selfcheck();
let W = T.sandbox('cron');

const AU = '17 4 * * * /usr/bin/zaprett repo upgrade --all --foreground --quiet # zaprett-autoupdate';
const WD = '*/5 * * * * /usr/bin/zaprett ensure --quiet # zaprett-watchdog';
let mon = (iv) => sprintf('*/%d * * * * /usr/bin/zaprett monitor run --quiet # zaprett-monitor', iv);

/* ---------- pure rendering ---------- */
let cfg = C.normalize({ enabled: '1' }, { autoupdate: '1', autoupdate_hour: '4' }, null, { enabled: '1', interval: '30' });
let r = CR.render('x 1 * * * /bin/true\n', CR.wanted(cfg, [], false), 17);
T.eq(r, 'x 1 * * * /bin/true\n' + AU + '\n' + WD + '\n' + mon(30) + '\n', 'enabled service: all three lines, other lines kept');
T.eq(CR.render(r, CR.wanted(cfg, [], false), 55), r, 'idempotent: minute of the daily line kept');
T.eq(CR.render(r, CR.wanted(cfg, [], true), 55), 'x 1 * * * /bin/true\n' + AU + '\n', 'stopped by the user: watchdog and monitor removed');
let off = C.normalize({ enabled: '0' }, { autoupdate: '1' }, null, { enabled: '1' });
T.eq(CR.render(r, CR.wanted(off, [], false), 55), 'x 1 * * * /bin/true\n' + AU + '\n', 'service disabled: only autoupdate');
let nowd = C.normalize({ enabled: '1', watchdog: '0' }, { autoupdate: '0' }, null, { enabled: '1', interval: '10' });
T.eq(CR.render(r, CR.wanted(nowd, [], false), 55), 'x 1 * * * /bin/true\n' + mon(10) + '\n', 'watchdog=0, autoupdate off, interval 10');
let nomon = C.normalize({ enabled: '1' }, { autoupdate: '0' }, null, { enabled: '0' });
T.eq(CR.render(r, CR.wanted(nomon, [], false), 55), 'x 1 * * * /bin/true\n' + WD + '\n', 'monitor.enabled=0: no monitor line');
let nosec = C.normalize({ enabled: '1' }, { autoupdate: '0' }, null, null);
T.eq(CR.render('', CR.wanted(nosec, [], false), 55), WD + '\n', 'no monitor section: no monitor line');
let hourly = C.normalize({ enabled: '1', watchdog: '0' }, { autoupdate: '0' }, null, { enabled: '1', interval: '60' });
let h1 = CR.render('', CR.wanted(hourly, [], false), 23);
T.eq(h1, '23 * * * * /usr/bin/zaprett monitor run --quiet # zaprett-monitor\n', 'interval 60: once an hour at a random minute');
T.eq(CR.render(h1, CR.wanted(hourly, [], false), 41), h1, 'hourly line keeps its minute');
T.eq(CR.render(h1 + h1, CR.wanted(hourly, [], false), 41), h1, 'duplicate lines collapsed');
// a mark not given to render() is left alone (the old autoupdate-only renderer)
T.eq(CMD.cron_render(r, false, 4, 1), 'x 1 * * * /bin/true\n' + WD + '\n' + mon(30) + '\n', 'autoupdate-only render keeps the other lines');
T.eq(CR.render('', {}, 1), '', 'nothing wanted: empty stays empty');
// invalid monitor values
let bad = C.normalize({}, null, null, { interval: '7', threshold: '0', timeout: '99', max_targets: 'x', enabled: 'maybe' });
T.eq([ bad.monitor.interval, bad.monitor.threshold, bad.monitor.timeout, bad.monitor.max_targets, bad.monitor.enabled ], [ 30, 3, 8, 5, true ],
	'invalid monitor values fall back to the defaults');
T.eq(sort(bad.bad_options), [ 'monitor.enabled', 'monitor.interval', 'monitor.max_targets', 'monitor.threshold', 'monitor.timeout' ],
	'invalid monitor options reported with the section prefix');
T.has(bad.warnings, 'bad_config', 'bad_config for invalid monitor values');
T.eq([ C.normalize({}, null, null, null).monitor.present, C.normalize({}, null, null, null).monitor.enabled ], [ false, false ],
	'a missing monitor section means the monitor is off');
T.eq(C.normalize({}, null, null, null).watchdog, true, 'watchdog is on by default');

/* ---------- cron sync in the sandbox ---------- */
fs.writefile(P.crontab, '0 3 * * * /bin/true\n');
C.set({ enabled: '1' });
r = CMD.cron_sync();
let ct = fs.readfile(P.crontab);
T.ok(r.ok && r.changed && index(ct, WD) >= 0 && index(ct, mon(30)) >= 0 && index(ct, '# zaprett-autoupdate') >= 0 &&
	index(ct, '0 3 * * * /bin/true\n') == 0, 'cron sync adds all three lines: ' + ct);
T.eq(CMD.cron_sync().changed, false, 'cron sync idempotent');
fs.writefile(SV.stopped_path(), '');
CMD.cron_sync();
ct = fs.readfile(P.crontab);
T.ok(index(ct, '# zaprett-watchdog') < 0 && index(ct, '# zaprett-monitor') < 0 && index(ct, '# zaprett-autoupdate') >= 0,
	'stop mark: watchdog and monitor lines gone, autoupdate kept');
fs.unlink(SV.stopped_path());
CMD.cron_sync();
C.set({ watchdog: '0' });
C.set({ interval: '15' }, 'monitor');
CMD.cron_sync();
ct = fs.readfile(P.crontab);
T.ok(index(ct, '# zaprett-watchdog') < 0 && index(ct, mon(15)) >= 0, 'watchdog=0 removes its line, the monitor follows its interval');
C.set({ watchdog: '1', enabled: '0' });
CMD.cron_sync();
ct = fs.readfile(P.crontab);
T.ok(index(ct, '# zaprett-watchdog') < 0 && index(ct, '# zaprett-monitor') < 0, 'enabled=0 removes watchdog and monitor lines');
T.eq(length(filter(split(ct, '\n'), (l) => index(l, '# zaprett-') >= 0)), 1, 'exactly one zaprett line left (autoupdate)');

/* ---------- init script: stop marks, start clears, restart does neither ---------- */
let CLI_LOG = W + '/cli.log';
T.write(W + '/bin/zaprett', '#!/bin/sh\necho "$*" >> ' + CLI_LOG + '\nexit 0\n');
fs.chmod(W + '/bin/zaprett', 493);
let RUN = W + '/initrun';
function init_call(fn, action, enabled, setup) {
	system([ 'rm', '-rf', RUN, CLI_LOG ]);
	system([ 'mkdir', '-p', RUN ]);
	for (let f in (setup ?? []))
		fs.writefile(RUN + '/' + f, '');
	let sh = W + '/init-call.sh';
	fs.writefile(sh, join('\n', [
		'config_load() { :; }',
		'config_get_bool() { eval "$1=' + enabled + '"; }',
		'logger() { :; }',
		'procd_open_instance() { :; }', 'procd_set_param() { :; }', 'procd_close_instance() { :; }',
		// rc.common's own start (reload_service calls it)
		'start() { :; }',
		'. ' + T.ROOT + '/files/etc/init.d/zaprett',
		'ZAPRETT_CLI=' + W + '/bin/zaprett',
		'ZAPRETT_RUN=' + RUN,
		'action=' + action,
		fn,
		''
	]));
	return system([ 'sh', sh ]);
}
let cli_calls = () => filter(split(fs.readfile(CLI_LOG) ?? '', '\n'), (l) => l != '');
init_call('stop_service', 'stop', 1);
T.ok(fs.stat(RUN + '/stopped') != null && index(cli_calls(), 'cron sync --quiet') >= 0, 'init stop: stop mark written, cron sync called');
init_call('stop_service', 'restart', 1);
T.ok(fs.stat(RUN + '/stopped') == null && index(cli_calls(), 'cron sync --quiet') < 0, 'init restart: no stop mark, no cron sync');
init_call('start_service', 'start', 0, [ 'stopped' ]);
T.ok(fs.stat(RUN + '/stopped') == null && index(cli_calls(), 'cron sync --quiet') >= 0, 'init start (disabled): mark cleared, cron sync called');
init_call('stop_service', 'stop', 1, [ 'test-state.json' ]);
T.ok(index(cli_calls(), 'cron sync --quiet') < 0, 'during an automatic selection the schedule is left alone');
init_call('reload_service', 'reload', 0);
T.ok(index(cli_calls(), 'cron sync --quiet') >= 0, 'init reload calls cron sync');
// Image Builder runs the file at the top level: nothing there but declarations
let top = filter(split(fs.readfile(T.ROOT + '/files/etc/init.d/zaprett'), '\n'), (l) => l != '' && substr(l, 0, 1) != '#' &&
	substr(l, 0, 1) != '\t' && substr(l, 0, 1) != '}' && !match(l, /^[a-z_]+\(\) \{$/) && !match(l, /^[A-Z_]+=[^ ]*$/));
T.eq(top, [], 'init script: only declarations at the top level');

/* ---------- uci-defaults: monitor section created once, user values kept ---------- */
let UD = W + '/ud';
function uci_defaults(conf) {
	system([ 'rm', '-rf', UD ]);
	system([ 'mkdir', '-p', UD + '/conf', UD + '/delta' ]);
	fs.writefile(UD + '/conf/zaprett', conf);
	fs.writefile(UD + '/conf/firewall', '\nconfig defaults\n');
	let src = fs.readfile(T.ROOT + '/files/etc/uci-defaults/90-zaprett');
	src = join('uci -c ' + UD + '/conf -t ' + UD + '/delta ', split(src, 'uci '));
	src = join(W + '/bin/zaprett', split(src, '/usr/bin/zaprett'));
	src = join(UD + '/etczaprett', split(src, '/etc/zaprett'));
	fs.writefile(UD + '/ud.sh', src);
	return system([ 'sh', UD + '/ud.sh' ]);
}
T.eq(uci_defaults("config main 'main'\n\toption enabled '1'\n"), 0, 'uci-defaults runs');
let conf1 = fs.readfile(UD + '/conf/zaprett');
T.ok(index(conf1, "config monitor 'monitor'") >= 0 && index(conf1, "option interval '30'") >= 0 && index(conf1, "option auto_repair '0'") >= 0,
	'missing monitor section created with the defaults: ' + conf1);
let before = fs.readfile(UD + '/conf/zaprett');
let src2 = fs.readfile(UD + '/ud.sh');
system([ 'sh', UD + '/ud.sh' ]);
T.eq(fs.readfile(UD + '/conf/zaprett'), before, 'second run changes nothing (idempotent)');
uci_defaults("config main 'main'\n\toption enabled '1'\n\nconfig monitor 'monitor'\n\toption enabled '0'\n\toption interval '10'\n");
let conf2 = fs.readfile(UD + '/conf/zaprett');
T.ok(index(conf2, "option interval '10'") >= 0 && index(conf2, "option enabled '0'") >= 0 && index(conf2, "option interval '30'") < 0,
	'upgrade over an existing monitor section keeps the user values: ' + conf2);
T.ok(index(fs.readfile(CLI_LOG) ?? '', 'cron sync --quiet') >= 0, 'uci-defaults calls cron sync');
T.ok(src2 != null && index(src2, '/usr/bin/zaprett') < 0 && index(src2, 'uci -c ') >= 0, 'test copy of uci-defaults is sandboxed');

/* ---------- prerm of the package: all three lines removed ---------- */
function script_of(path, pkg, section) {
	let text = fs.readfile(path, 262144);
	let head = 'define Package/' + pkg + '/' + section + '\n';
	let start = index(text, head);
	if (start < 0)
		return null;
	let body = substr(text, start + length(head));
	return substr(body, 0, index(body, '\nendef') + 1);
}
let prerm = script_of(T.ROOT + '/Makefile', 'zaprett', 'prerm');
T.ok(prerm != null, 'prerm found in the Makefile');
let PR = W + '/prerm';
system([ 'mkdir', '-p', PR + '/bin', PR + '/crontabs', PR + '/init.d' ]);
for (let stub in [ 'nft', 'uci' ]) {
	fs.writefile(PR + '/bin/' + stub, '#!/bin/sh\necho "' + stub + ' $*" >> ' + PR + '/stub.log\nexit 1\n');
	fs.chmod(PR + '/bin/' + stub, 493);
}
fs.writefile(PR + '/init.d/zaprett', '#!/bin/sh\nexit 0\n');
fs.writefile(PR + '/init.d/cron', '#!/bin/sh\necho "cron $*" >> ' + PR + '/stub.log\n');
fs.chmod(PR + '/init.d/zaprett', 493);
fs.chmod(PR + '/init.d/cron', 493);
let other_line = '0 1 * * * /usr/bin/other-job';
fs.writefile(PR + '/crontabs/root', other_line + '\n' + AU + '\n' + WD + '\n' + mon(30) + '\n');
let sh = join('$', split(prerm, '$$'));
for (let pp in [ [ '/etc/crontabs/root', PR + '/crontabs/root' ], [ '/etc/init.d/', PR + '/init.d/' ], [ '/usr/bin/zaprett', W + '/bin/zaprett' ],
	[ '/var/run/zaprett', PR + '/varrun' ] ])
	sh = join(pp[1], split(sh, pp[0]));
fs.writefile(PR + '/prerm.sh', 'PATH=' + PR + '/bin:$PATH\n' + sh);
T.eq(system([ 'sh', PR + '/prerm.sh', 'remove' ]), 0, 'prerm runs');
T.eq(fs.readfile(PR + '/crontabs/root'), other_line + '\n', 'prerm removed all three zaprett lines and kept the others');
T.ok(index(fs.readfile(PR + '/stub.log') ?? '', 'cron restart') >= 0, 'cron restarted after the change');
// negative control: the same script with the watchdog mark taken out of it leaves that line behind
fs.writefile(PR + '/crontabs/root', other_line + '\n' + WD + '\n');
let sh_bad = join('/# zaprett-nothing/d', split(sh, "/# zaprett-watchdog/d"));
fs.writefile(PR + '/prerm-bad.sh', 'PATH=' + PR + '/bin:$PATH\n' + sh_bad);
system([ 'sh', PR + '/prerm-bad.sh', 'remove' ]);
T.ok(index(fs.readfile(PR + '/crontabs/root'), '# zaprett-watchdog') >= 0, 'negative control: without its sed expression the line stays');

exit(T.finish());
