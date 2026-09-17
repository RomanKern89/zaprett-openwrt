'use strict';

import * as fs from 'fs';
import * as T from 'ztest';
import { P, try_lock, unlock, write_json, proc_starttime, ok, fail } from 'zaprett.util';
import * as J from 'zaprett.job';

T.begin('job');
T.selfcheck();
let W = T.sandbox('job');

T.ok(J.read() == null, 'no job initially');
T.eq(J.cancel().error, 'no_job', 'cancel without job');

// a job whose process disappeared is reported as failed
write_json(J.job_path(), { id: '1-1', name: 'test', state: 'running', progress: 10, started: time(), pid: 999999 });
let j = J.read();
T.eq([ j.state, j.rc ], [ 'failed', 1 ], 'dead runner detected');
write_json(J.job_path(), { id: '1-2', name: 'test', state: 'running', progress: 0, started: time() - 120, pid: 0 });
T.eq(J.read().state, 'failed', 'runner that never started detected');
write_json(J.job_path(), { id: '1-3', name: 'test', state: 'running', progress: 0, started: time(), pid: 0 });
T.eq(J.read().state, 'running', 'fresh job without pid still running');
T.ok(J.update('1-3', { progress: 50 }) && J.read().progress == 50, 'update');
T.ok(!J.update('other', { progress: 60 }) && J.read().progress == 50, 'update ignores other job ids');

// foreground execution
let seen = null;
let r = J.foreground('repo-fetch', (ctx) => {
	ctx.progress(30, 'шаг 1');
	seen = J.read();
	return ok({ value: 42 });
});
T.ok(r.ok && r.value == 42, 'foreground result returned');
T.eq([ seen.state, seen.progress, seen.message ], [ 'running', 30, 'шаг 1' ], 'progress visible while running');
j = J.read();
T.eq([ j.state, j.progress, j.rc, j.result.value ], [ 'done', 100, 0, 42 ], 'job finished');
T.ok(index(J.log_tail(10), 'шаг 1') >= 0, 'progress message in log');

r = J.foreground('test', (ctx) => fail('boom', 'ошибка'));
T.eq([ J.read().state, J.read().message ], [ 'failed', 'ошибка' ], 'failed job');
r = J.foreground('test', (ctx) => die('exception inside handler'));
T.eq([ r.error, J.read().state ], [ 'internal_error', 'failed' ], 'exception becomes internal_error');

// cancellation through the cancel file
r = J.foreground('test', (ctx) => {
	fs.writefile(P.run + '/job.cancel', J.read().id);
	return ctx.cancelled() ? fail('cancelled', 'отменено') : ok();
});
T.eq(J.read().state, 'cancelled', 'cancel file honoured');
r = J.foreground('test', (ctx) => {
	fs.writefile(P.run + '/job.cancel', 'another-id');
	return ctx.cancelled() ? fail('cancelled', 'x') : ok();
});
T.eq(J.read().state, 'done', 'cancel file of another job ignored');

// one job at a time
let lock = try_lock(P.lock_job);
T.ok(lock != null, 'lock acquired');
T.ok(try_lock(P.lock_job) == null, 'second lock on the same file is refused');
r = J.foreground('test', (ctx) => ok());
T.eq(r.error, 'job_busy', 'job refused while the lock is held');
unlock(lock);
T.ok(J.foreground('test', (ctx) => ok()).ok, 'job allowed after unlock');

/* ---- job cancel returns at once; status polls finish the cancellation (contract v1.2) ---- */
// a detached process standing in for the job runner (start-stop-daemon -b, as J.start does)
function spawn(tag, script) {
	let pidfile = W + '/run/' + tag + '.pid';
	system([ 'start-stop-daemon', '-S', '-b', '-m', '-p', pidfile, '-x', '/bin/sh', '--', '-c', script ]);
	for (let i = 0; i < 50 && !fs.readfile(pidfile); i++)
		sleep(20);
	return int(trim(fs.readfile(pidfile) ?? '0'));
}
function wait_dead(pid, ms) {
	for (let waited = 0; waited < ms; waited += 50) {
		if (!fs.stat('/proc/' + pid))
			return true;
		sleep(50);
	}
	return !fs.stat('/proc/' + pid);
}
function child_of(pid) {
	for (let d in fs.lsdir('/proc') ?? []) {
		let st = match(d, /^[0-9]+$/) ? fs.readfile('/proc/' + d + '/stat', 1024) : null;
		let rp = st ? rindex(st, ')') : -1;
		if (rp >= 0 && int(split(trim(substr(st, rp + 1)), ' ')[1]) == pid)
			return int(d);
	}
	return 0;
}
let cleanups = [];
let cleanup = (job) => { push(cleanups, job.id); return { restored: true }; };

// 1. a runner that stops on SIGTERM
let pid1 = spawn('term', 'while :; do sleep 1; done');
let st1 = proc_starttime(pid1);
T.ok(pid1 > 0 && fs.stat('/proc/' + pid1) != null, 'test runner process started');
write_json(J.job_path(), { id: 'c-1', name: 'test', state: 'running', progress: 5, started: time(), pid: pid1, pid_start: proc_starttime(pid1) });
let t0 = clock(true);
r = J.cancel();
let t1 = clock(true);
let took = (t1[0] - t0[0]) * 1000 + int((t1[1] - t0[1]) / 1000000);
T.eq([ r.ok, r.state, r.job?.id ], [ true, 'cancelling', 'c-1' ], 'job cancel answers {ok, state: cancelling} at once');
T.ok(took < 3000, sprintf('job cancel did not wait for the job (%d ms)', took));
T.eq(trim(fs.readfile(P.run + '/job.cancel') ?? ''), 'c-1', 'cancel flag written for the runner');
T.ok(type(J.read().cancel_requested) == 'int', 'cancel request recorded in job.json');
T.ok(wait_dead(pid1, 3000), 'runner got SIGTERM');
j = J.enforce_cancel(cleanup, time());
T.eq([ j.state, j.cancel_done, cleanups ], [ 'cancelled', true, [ 'c-1' ] ], 'runner died without a report: the poll marks it cancelled and cleans up');
T.eq(J.enforce_cancel(cleanup, time() + 100).state, 'cancelled', 'second poll changes nothing');
T.eq(cleanups, [ 'c-1' ], 'cleanup ran once');

// 2. a runner that ignores SIGTERM is killed with its children after the grace period
let pid2 = spawn('stubborn', 'trap "" TERM; while :; do sleep 600; done');
sleep(300);
let kid = child_of(pid2);
let st2 = proc_starttime(pid2), stk = proc_starttime(kid);
T.ok(kid > 0 && fs.stat('/proc/' + kid) != null, 'runner has a long-running child');
write_json(J.job_path(), { id: 'c-2', name: 'repo-upgrade', state: 'running', progress: 5, started: time(), pid: pid2, pid_start: proc_starttime(pid2) });
T.eq(J.enforce_cancel(cleanup, time() + 1000).state, 'running', 'negative control: without a cancel request nothing is killed');
T.ok(fs.stat('/proc/' + pid2) != null, 'runner still alive without a cancel request');
T.eq(J.cancel().state, 'cancelling', 'cancel requested');
sleep(300);
T.ok(fs.stat('/proc/' + pid2) != null, 'runner ignores SIGTERM');
let req = J.read().cancel_requested;
T.eq(J.enforce_cancel(cleanup, req + J.CANCEL_GRACE - 1).state, 'running', 'within the grace period the runner is left alone');
j = J.enforce_cancel(cleanup, req + J.CANCEL_GRACE);
T.eq([ j.state, j.cancel_done ], [ 'cancelled', true ], 'after the grace period the job is cancelled');
T.ok(index(j.message, 'принудительно') >= 0, 'message says the job was stopped forcibly: ' + j.message);
T.ok(wait_dead(pid2, 3000), 'stubborn runner killed');
T.ok(kid > 0 && wait_dead(kid, 3000), sprintf('its child %d killed too', kid));
T.eq(cleanups, [ 'c-1', 'c-2' ], 'cleanup ran after the forced stop');
T.eq(J.cancel().error, 'no_job', 'nothing to cancel afterwards');
// nothing may outlive the test even when a check above failed (only the same processes: pid and start time)
for (let p in [ [ pid1, st1 ], [ pid2, st2 ], [ kid, stk ] ])
	if (p[0] > 1 && p[1] != null && proc_starttime(p[0]) == p[1])
		system([ 'kill', '-9', '' + p[0] ]);

// log size limit
J.foreground('test', (ctx) => {
	let chunk = sprintf('%0200d', 7);
	for (let i = 0; i < 2000; i++)
		ctx.log(chunk);
	return ok();
});
let size = fs.stat(J.log_path()).size;
T.ok(size <= J.LOG_LIMIT + 1024, sprintf('log truncated to the limit: %d bytes', size));
T.eq(length(split(J.log_tail(5), '\n')), 5, 'log tail lines');

exit(T.finish());
