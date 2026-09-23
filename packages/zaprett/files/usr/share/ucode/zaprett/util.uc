// zaprett: common helpers (files, processes, JSON, logging).
// ucode modules allowed by ADR-001: fs, uci, ubus, math. Nothing else.
'use strict';

import * as fs from 'fs';

export const VERSION = '1.1.0';

// All filesystem locations used by the backend. Tests redirect them with set_paths().
export const P = {
	run: '/var/run/zaprett',
	tmp: '/var/run/zaprett/tmp',
	etc: '/etc/zaprett',
	user: '/etc/zaprett/user',
	share: '/usr/share/zaprett',
	bundle: '/usr/share/zaprett/bundle',
	libexec: '/usr/libexec/zaprett',
	lock_job: '/var/lock/zaprett-job.lock',
	lock_main: '/var/lock/zaprett.lock',
	lock_fw: '/var/lock/zaprett-fw.lock',	// fw apply / fw remove (called from init, hotplug and commands)
	lock_fw_wait_ms: 30000,
	cli: '/usr/share/zaprett/cli.uc',
	ucode: '/usr/bin/ucode',
	init: '/etc/init.d/zaprett',
	guard_hostlist: '/usr/share/zaprett/guard/hostlist-guard.txt',
	guard_ipset: '/usr/share/zaprett/guard/ipset-guard.txt',
	presets: '/usr/share/zaprett/presets.json',
	probe: '/usr/share/zaprett/probe.sh',
	crontab: '/etc/crontabs/root',
	firewall_init: '/etc/init.d/firewall',
	cron_init: '/etc/init.d/cron',
	meminfo: '/proc/meminfo',
	nfqueue: '/proc/net/netfilter/nfnetlink_queue',
	fw4_state: '/var/run/fw4.state',	// zones and their devices as fw4 resolved them (own flowtable, v1.4 §15.1)
	sys_net: '/sys/class/net',
	opkg: '/bin/opkg',
	apk: '/usr/bin/apk',
	initd: '/etc/init.d',
	luci: '/www/luci-static/resources/luci.js',
	logread: 'logread',
	passwd: '/etc/passwd',	// user of the isolated automatic selection (contract v1.6 §17)
	ssd: 'start-stop-daemon',
	syslog: true,
	uci_confdir: null,	// null = /etc/config (tests use a sandbox directory)
	uci_savedir: null
};

export const MODE_DIR = 493;	// 0755
export const MODE_FILE = 420;	// 0644

// Job context stub for synchronous calls without progress reporting.
export const NULL_CTX = {
	progress: (pct, msg) => null,
	cancelled: () => false,
	log: (msg) => null,
	partial: (res) => null
};

export function set_paths(over) {
	if (type(over) == 'object')
		for (let k, v in over)
			P[k] = v;
};

export function ok(extra) {
	let r = { ok: true };
	if (type(extra) == 'object')
		for (let k, v in extra)
			r[k] = v;
	return r;
};

export function fail(code, message, extra) {
	let r = { ok: false, error: code, message: message };
	if (type(extra) == 'object')
		for (let k, v in extra)
			r[k] = v;
	return r;
};

let seq = 0;
let cached_pid = null;

export function self_pid() {
	if (cached_pid == null) {
		let st = fs.readfile('/proc/self/stat', 64);
		cached_pid = st ? int(split(st, ' ')[0]) : 0;
	}
	return cached_pid;
};

export function uniq_name(prefix) {
	let c = clock(true) || [ time(), 0 ];
	seq++;
	return sprintf('%s.%d.%d%09d.%d', prefix, self_pid(), c[0], c[1], seq);
};

export function is_dir(path) {
	return fs.stat(path)?.type == 'directory';
};

export function is_file(path) {
	return fs.stat(path)?.type == 'file';
};

export function mkdir_p(path, mode) {
	if (is_dir(path))
		return true;
	let cur = '';
	for (let part in split(path, '/')) {
		if (part == '')
			continue;
		cur += '/' + part;
		let st = fs.stat(cur);
		if (st) {
			if (st.type != 'directory')
				return false;
			continue;
		}
		if (!fs.mkdir(cur, mode ?? MODE_DIR) && !is_dir(cur))
			return false;
	}
	return true;
};

// Reads a file with a hard size limit. Returns null when missing or larger than the limit.
export function read_limited(path, limit) {
	let st = fs.stat(path);
	if (!st || st.type != 'file' || st.size > limit)
		return null;
	return fs.readfile(path, limit);
};

export function read_json(path, limit) {
	let s = read_limited(path, limit ?? 1048576);
	if (s == null)
		return null;
	try {
		return json(s);
	}
	catch (e) {
		return null;
	}
};

// Atomic replace: write a temporary file in the same directory, then rename it over the target.
// fs.writefile() does not report errors of fclose(): for small data on a full flash it returns the full
// length while the file stays empty, so the size on disk is compared before the rename.
export function atomic_write(path, data, mode) {
	let tmp = uniq_name(path + '.tmp');
	let n = fs.writefile(tmp, data);
	if (n == null || n != length(data) || fs.stat(tmp)?.size != length(data)) {
		fs.unlink(tmp);
		return false;
	}
	fs.chmod(tmp, mode ?? MODE_FILE);
	if (!fs.rename(tmp, path)) {
		fs.unlink(tmp);
		return false;
	}
	return true;
};

export function write_json(path, obj) {
	return atomic_write(path, sprintf('%.J\n', obj));
};

// Runs a command given as an argv array (never through a shell string).
// The constant wrapper only redirects stdio; every variable part is a positional argument.
const RUN_WRAPPER = 'o="$1"; e="$2"; i="$3"; shift 3; exec "$@" <"$i" >"$o" 2>"$e"';

export function run(argv, opts) {
	opts = opts ?? {};
	if (!mkdir_p(P.tmp))
		return { rc: -1, stdout: '', stderr: 'не удалось создать временный каталог ' + P.tmp };
	let base = uniq_name(P.tmp + '/run');
	let o = base + '.out', e = base + '.err', i = '/dev/null';
	if (opts.input != null) {
		i = base + '.in';
		if (fs.writefile(i, opts.input) == null)
			return { rc: -1, stdout: '', stderr: 'не удалось записать временный файл' };
	}
	let cmd = [ '/bin/sh', '-c', RUN_WRAPPER, 'zaprett-run', o, e, i ];
	for (let a in argv)
		push(cmd, '' + a);
	let rc;
	try {
		rc = system(cmd, opts.timeout ?? 0);
	}
	catch (ex) {
		rc = -1;
	}
	let limit = opts.limit ?? 262144;
	let so = fs.readfile(o, limit) ?? '';
	let se = fs.readfile(e, limit) ?? '';
	fs.unlink(o);
	fs.unlink(e);
	if (opts.input != null)
		fs.unlink(i);
	return { rc: rc, stdout: so, stderr: se };
};

// Runs a command discarding all output, returns the exit code.
const QUIET_WRAPPER = 'exec "$@" </dev/null >/dev/null 2>&1';

export function run_quiet(argv, timeout) {
	let cmd = [ '/bin/sh', '-c', QUIET_WRAPPER, 'zaprett-run' ];
	for (let a in argv)
		push(cmd, '' + a);
	try {
		return system(cmd, timeout ?? 0);
	}
	catch (ex) {
		return -1;
	}
};

export function log(prio, msg) {
	if (P.syslog)
		run_quiet([ 'logger', '-t', 'zaprett', '-p', 'daemon.' + prio, '--', msg ], 5000);
};

export function sha256_file(path) {
	let r = run([ 'sha256sum', path ], { timeout: 120000, limit: 4096 });
	if (r.rc != 0)
		return null;
	let m = match(r.stdout, /^([0-9a-f]+)/);
	return (m && length(m[1]) >= 64) ? substr(m[1], 0, 64) : null;
};

// Available space in KiB on the filesystem holding path (busybox df -k).
export function df_avail_kib(path) {
	let r = run([ 'df', '-k', path ], { timeout: 10000, limit: 8192 });
	if (r.rc != 0)
		return null;
	let lines = filter(split(trim(r.stdout), '\n'), (l) => l != '');
	if (length(lines) < 2)
		return null;
	// the data may wrap onto two lines when the device name is long
	let fields = filter(split(replace(join(' ', slice(lines, 1)), '\t', ' '), ' '), (f) => f != '');
	for (let idx = 0; idx + 3 < length(fields); idx++) {
		if (match(fields[idx], /^[0-9]+$/) && match(fields[idx + 1], /^[0-9]+$/) &&
		    match(fields[idx + 2], /^[0-9]+$/) && match(fields[idx + 3], /^[0-9]+%$/))
			return int(fields[idx + 2]);
	}
	return null;
};

export function pid_alive(pid) {
	pid = int(pid);
	return pid > 0 && is_dir('/proc/' + pid);
};

// Start time of a process (field 22 of /proc/<pid>/stat) — distinguishes a live process from a reused pid.
export function proc_starttime(pid) {
	pid = int(pid);
	if (pid <= 0)
		return null;
	let st = fs.readfile('/proc/' + pid + '/stat', 1024);
	if (!st)
		return null;
	let rp = rindex(st, ')');
	if (rp < 0)
		return null;
	let f = split(trim(substr(st, rp + 1)), ' ');
	return (length(f) > 19) ? f[19] : null;
};

export function kill(pid, sig) {
	pid = int(pid);
	if (pid <= 1)
		return false;
	return run_quiet([ 'kill', '-' + sig, '' + pid ], 5000) == 0;
};

export function tail_lines(text, n) {
	let lines = split(text ?? '', '\n');
	if (length(lines) && lines[length(lines) - 1] == '')
		pop(lines);
	if (n > 0 && length(lines) > n)
		lines = slice(lines, length(lines) - n);
	return join('\n', lines);
};

// Длина проверяется через length(), а не интервалом в регулярке: ucode компилирует регулярку на каждый
// вызов, и интервал вида {1,96} стоит около 4,8 мс за вызов против 0,04 мс у класса без границ
// (замер на обеих версиях). is_id вызывается на каждый манифест и каждый id, поэтому это заметно.
export function is_id(s) {
	return type(s) == 'string' && length(s) >= 1 && length(s) <= 96 && substr(s, 0, 1) != '.' &&
		match(s, /^[A-Za-z0-9._-]+$/) != null;
};

// A /proc/meminfo value (MemTotal, MemAvailable...) in MiB, or null when unknown.
export function meminfo_mib(key) {
	let head = key + ':';
	for (let l in split(fs.readfile(P.meminfo, 16384) ?? '', '\n')) {
		if (substr(l, 0, length(head)) != head)
			continue;
		let m = match(l, /([0-9]+) kB/);
		return m ? int(int(m[1]) / 1024) : null;
	}
	return null;
};

// Pure: parallel downloads — `normal`, or one at a time when little memory is available (unknown = normal).
export function download_concurrency(normal, low_mib, avail_mib) {
	return (avail_mib != null && avail_mib < low_mib) ? 1 : normal;
};

export function file_size(path) {
	return fs.stat(path)?.size;
};

export function copy_file(src, dst) {
	let r = run([ 'cp', src, dst ], { timeout: 120000, limit: 4096 });
	return r.rc == 0;
};

// Lock helpers based on flock(2) through fs.file.lock (present in ucode 24.10 and 25.12).
export function try_lock(path) {
	mkdir_p(fs.dirname(path));
	let fh = fs.open(path, 'a');
	if (!fh)
		return null;
	if (!fh.lock('xn')) {
		fh.close();
		return null;
	}
	return fh;
};

export function wait_lock(path, timeout_ms) {
	let waited = 0;
	while (true) {
		let fh = try_lock(path);
		if (fh || waited >= timeout_ms)
			return fh;
		sleep(200);
		waited += 200;
	}
};

export function unlock(fh) {
	if (fh) {
		fh.lock('u');
		fh.close();
	}
};
