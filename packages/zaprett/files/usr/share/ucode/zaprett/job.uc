// zaprett: background jobs (ARCHITECTURE §6.3). One job at a time, guarded by flock on
// /var/lock/zaprett-job.lock; state in /var/run/zaprett/job.json, log in job.log.
'use strict';

import * as fs from 'fs';
import { P, read_json, write_json, mkdir_p, try_lock, wait_lock, unlock, self_pid, proc_starttime, kill,
	run, ok, fail } from 'zaprett.util';

export const LOG_LIMIT = 262144;
export const NAMES = [ 'repo-fetch', 'repo-install', 'repo-remove', 'repo-upgrade', 'sources-update', 'test', 'autoupdate', 'probe',
	'dns-setup', 'diagnose' ];

export function job_path() {
	return P.run + '/job.json';
};

export function log_path() {
	return P.run + '/job.log';
};

function cancel_path() {
	return P.run + '/job.cancel';
}

function pid_path() {
	return P.run + '/job.pid';
}

// The job process is alive when its pid exists and has the start time recorded in job.json.
function runner_alive(j) {
	let st = proc_starttime(j.pid);
	return st != null && (j.pid_start == null || st == j.pid_start);
}

// Reads job.json and detects jobs whose process has died.
export function read() {
	let j = read_json(job_path(), 1048576);
	if (type(j) != 'object')
		return null;
	if (j.state == 'running') {
		let dead = (j.pid > 0) ? !runner_alive(j) : (time() - (j.started ?? 0) > 60);
		if (dead) {
			j.state = 'failed';
			j.message = 'Задача прервалась (процесс завершился без отчёта)';
			j.finished = time();
			j.rc = 1;
			write_json(job_path(), j);
		}
	}
	return j;
};

export function update(id, fields) {
	let j = read_json(job_path(), 1048576);
	if (type(j) != 'object' || j.id != id)
		return false;
	for (let k, v in fields)
		j[k] = v;
	return write_json(job_path(), j);
};

export function append_log(line) {
	mkdir_p(P.run);
	let t = localtime();
	let text = sprintf('[%02d:%02d:%02d] %s\n', t.hour, t.min, t.sec, line);
	let st = fs.stat(log_path());
	if (st && st.size + length(text) > LOG_LIMIT) {
		let keep = fs.readfile(log_path()) ?? '';
		keep = substr(keep, length(keep) - int(LOG_LIMIT / 2));
		let nl = index(keep, '\n');
		if (nl >= 0)
			keep = substr(keep, nl + 1);
		fs.writefile(log_path(), '[…журнал обрезан…]\n' + keep);
	}
	let fh = fs.open(log_path(), 'a');
	if (fh) {
		fh.write(text);
		fh.close();
	}
};

export function log_tail(n) {
	let text = fs.readfile(log_path(), LOG_LIMIT * 2) ?? '';
	let lines = split(text, '\n');
	if (length(lines) && lines[length(lines) - 1] == '')
		pop(lines);
	n = int(n ?? 100);
	if (n < 1)
		n = 1;
	if (n > 5000)
		n = 5000;
	if (length(lines) > n)
		lines = slice(lines, length(lines) - n);
	return join('\n', lines);
};

export function busy_fail(cur) {
	return fail('job_busy', sprintf('Уже выполняется другая задача%s. Дождитесь её завершения или отмените её.',
		cur ? sprintf(' («%s»)', cur.name) : ''), { job: cur ? { id: cur.id, name: cur.name } : null });
};

function new_job(id, name, pid) {
	return {
		id: id, name: name, state: 'running', progress: 0, message: 'Задача запускается…',
		started: time(), finished: 0, rc: 0, result: {}, pid: pid, pid_start: pid ? proc_starttime(pid) : null
	};
}

function make_ctx(id) {
	let cancel_flag = false;
	let ctx = {
		id: id,
		cancelled: () => {
			if (cancel_flag)
				return true;
			let c = fs.readfile(cancel_path(), 128);
			if (c != null && trim(c) == id)
				cancel_flag = true;
			return cancel_flag;
		},
		set_cancelled: () => {
			cancel_flag = true;
		},
		progress: (pct, msg) => {
			let f = { progress: int(pct) };
			if (msg != null) {
				f.message = msg;
				append_log(msg);
			}
			update(id, f);
		},
		log: (msg) => append_log(msg),
		partial: (result) => update(id, { result: result })
	};
	return ctx;
}

function finish(id, ctx, res) {
	let state = ctx.cancelled() ? 'cancelled' : (res?.ok ? 'done' : 'failed');
	let msg = res?.message;
	if (state == 'cancelled')
		msg = 'Задача отменена';
	else if (state == 'done' && msg == null)
		msg = 'Готово';
	append_log(sprintf('Итог: %s%s', state, msg ? (' — ' + msg) : ''));
	update(id, {
		state: state,
		progress: (state == 'done') ? 100 : (read_json(job_path())?.progress ?? 0),
		message: msg,
		finished: time(),
		rc: (state == 'done') ? 0 : 1,
		result: res ?? {}
	});
	fs.unlink(cancel_path());
}

function execute(id, handler) {
	let ctx = make_ctx(id);
	signal('SIGTERM', () => ctx.set_cancelled());
	signal('SIGINT', () => ctx.set_cancelled());
	let res;
	try {
		res = handler(ctx);
	}
	catch (e) {
		append_log('Внутренняя ошибка: ' + e.message);
		res = fail('internal_error', 'Внутренняя ошибка: ' + e.message);
	}
	finish(id, ctx, res);
	return res;
}

// Launches `cli.uc __job-run <id> <name> args...` in background via start-stop-daemon.
export function start(name, args) {
	if (!mkdir_p(P.run))
		return fail('write_failed', 'Не удалось создать ' + P.run);
	let lock = try_lock(P.lock_job);
	if (!lock)
		return busy_fail(read());
	let id = sprintf('%d-%d', time(), self_pid());
	write_json(job_path(), new_job(id, name, 0));
	fs.writefile(log_path(), '');
	fs.unlink(cancel_path());
	fs.unlink(pid_path());
	append_log('Запуск задачи ' + name);
	unlock(lock);

	let argv = [ 'start-stop-daemon', '-S', '-b', '-m', '-p', pid_path(), '-x', P.ucode, '--',
		'-S', '--', P.cli, '__job-run', id, name ];
	for (let a in args)
		push(argv, '' + a);
	let r = run(argv, { timeout: 15000, limit: 16384 });
	if (r.rc != 0) {
		update(id, { state: 'failed', message: 'Не удалось запустить фоновую задачу: ' + trim(r.stdout + r.stderr), finished: time(), rc: 1 });
		return fail('job_spawn_failed', 'Не удалось запустить фоновую задачу');
	}
	for (let waited = 0; waited < 5000; waited += 100) {
		let cur = read_json(job_path(), 1048576);
		if (type(cur) != 'object' || cur.id != id)
			return busy_fail(cur);
		if (cur.pid > 0 || cur.state != 'running')
			return ok({ job: { id: id, name: name } });
		sleep(100);
	}
	return ok({ job: { id: id, name: name } });
};

// Body of the background process.
export function runner(id, name, handler) {
	let lock = wait_lock(P.lock_job, 5000);
	if (!lock)
		return 1;
	let j = read_json(job_path(), 1048576);
	if (type(j) != 'object' || j.id != id || j.state != 'running') {
		unlock(lock);
		return 1;
	}
	update(id, { pid: self_pid(), pid_start: proc_starttime(self_pid()), message: 'Выполняется…' });
	let res = execute(id, handler);
	unlock(lock);
	return res?.ok ? 0 : 1;
};

// Runs the job synchronously in the current process (--foreground).
export function foreground(name, handler) {
	if (!mkdir_p(P.run))
		return fail('write_failed', 'Не удалось создать ' + P.run);
	let lock = try_lock(P.lock_job);
	if (!lock)
		return busy_fail(read());
	let id = sprintf('%d-%d', time(), self_pid());
	write_json(job_path(), new_job(id, name, self_pid()));
	fs.writefile(log_path(), '');
	fs.unlink(cancel_path());
	append_log('Запуск задачи ' + name + ' (на переднем плане)');
	let res = execute(id, handler);
	unlock(lock);
	return res;
};

function children_of(pid) {
	let res = [];
	for (let d in (fs.lsdir('/proc') ?? [])) {
		if (!match(d, /^[0-9]+$/))
			continue;
		let st = fs.readfile('/proc/' + d + '/stat', 1024);
		if (!st)
			continue;
		// "pid (comm) state ppid ..." — comm may contain spaces, so parse after the last ')'
		let rp = rindex(st, ')');
		if (rp < 0)
			continue;
		let f = split(trim(substr(st, rp + 1)), ' ');
		if (int(f[1]) == pid)
			push(res, int(d));
	}
	return res;
}

// The parent is frozen first so that it cannot start a new step while its children are being killed.
function kill_tree(pid) {
	kill(pid, 'STOP');
	for (let c in children_of(pid))
		kill_tree(c);
	kill(pid, 'KILL');
}

// Seconds a runner gets to stop by itself after `job cancel` before it is killed.
export const CANCEL_GRACE = 20;

// Requests cancellation and returns at once (rpcd calls the CLI synchronously, contract v1.2 §11):
// cancel flag + SIGTERM; the runner stops between steps. enforce_cancel() finishes it later.
export function cancel() {
	let j = read();
	if (!j || j.state != 'running')
		return fail('no_job', 'Нет выполняющейся задачи');
	fs.writefile(cancel_path(), j.id);
	if (!j.cancel_requested)
		update(j.id, { cancel_requested: time(), message: 'Отмена задачи…' });
	if (j.pid > 0 && runner_alive(j))
		kill(j.pid, 'TERM');
	append_log('Запрошена отмена задачи');
	return ok({ state: 'cancelling', job: { id: j.id, name: j.name } });
};

function run_cleanup(cleanup, j) {
	if (type(cleanup) != 'function')
		return null;
	try {
		return cleanup(j);
	}
	catch (e) {
		return fail('internal_error', e.message);
	}
}

// Called on every status poll. Completes a cancellation when the runner ignored it for CANCEL_GRACE
// seconds (killed with its children) or died on SIGTERM before its handler was installed.
// cleanup: function(job), e.g. restoring the strategy after an automatic selection.
export function enforce_cancel(cleanup, now) {
	let j = read();
	if (!j || !j.cancel_requested || j.cancel_done)
		return j;
	now = now ?? time();
	if (j.state == 'running' && now - j.cancel_requested < CANCEL_GRACE)
		return j;
	if (j.state != 'running' && j.state != 'failed')
		return j;
	let forced = false;
	if (j.state == 'running' && j.pid > 0 && runner_alive(j)) {
		kill_tree(j.pid);
		forced = true;
	}
	let cres = run_cleanup(cleanup, j);
	append_log(forced ? 'Задача остановлена принудительно' : 'Задача прервана, выполнена очистка');
	update(j.id, { state: 'cancelled', message: forced ? 'Задача отменена (остановлена принудительно)' : 'Задача отменена',
		finished: time(), rc: 1, cancel_done: true, cleanup: cres });
	fs.unlink(cancel_path());
	return read();
};
