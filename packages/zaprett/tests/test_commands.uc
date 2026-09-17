'use strict';

// Read-only and sandbox-only command checks. Commands that change UCI, start services or spawn
// background jobs are not run here (they need an installed package on a test router).
import * as fs from 'fs';
import * as T from 'ztest';
import { P, run, set_paths, try_lock, unlock } from 'zaprett.util';
import * as CMD from 'zaprett.commands';
import * as TXT from 'zaprett.text';

T.begin('commands');
T.selfcheck();
let W = T.sandbox('commands');

let r = CMD.items({ type: 'bin' });
T.ok(r.ok && length(r.items) == 6, 'items --type bin');
T.eq(CMD.items({ type: 'bogus' }).error, 'bad_type', 'items with unknown type');

r = CMD.strategy_show('strategy-general');
T.ok(r.ok && length(r.text) > 100 && type(r.args) == 'array' && r.ports?.tcp != null, 'strategy show returns text, args and ports');
T.eq(CMD.strategy_show('../x').error, 'bad_id', 'strategy show: bad id');
T.eq(CMD.strategy_show('nope').error, 'strategy_not_found', 'strategy show: unknown');

r = CMD.user_get('user-hosts');
T.eq([ r.ok, r.text, r.entries ], [ true, 'example.com\n', 1 ], 'user get');
T.eq(CMD.user_get('list-youtube').error, 'bad_id', 'user get: only user lists');
r = CMD.user_set('user-hosts', 'a.com\r\nB.org\n\n');
T.eq([ r.ok, r.entries ], [ true, 2 ], 'user set');
T.eq(fs.readfile(W + '/etc/user/hosts-include.txt'), 'a.com\nb.org\n', 'user list stored normalized');
T.eq(fs.stat(W + '/etc/user/hosts-include.txt').mode & 511, 420, 'user list readable by nfqws user (0644)');
r = CMD.user_set('user-hosts', 'good.com\n*.evil.com\n');
T.eq([ r.error, length(r.errors) ], [ 'invalid_entries', 1 ], 'user set rejects bad lines');
T.eq(fs.readfile(W + '/etc/user/hosts-include.txt'), 'a.com\nb.org\n', 'rejected list does not overwrite the file');
T.eq(CMD.user_set('user-ipset', '10.1.2.3/8\n').ok, true, 'user ipset set');
T.eq(fs.readfile(W + '/etc/user/ipset-include.txt'), '10.0.0.0/8\n', 'ipset normalized');
T.eq(CMD.user_set('user-hosts', null).error, 'too_large', 'oversized stdin');

if (T.NFQWS) {
	r = CMD.strategy_save('user-new', '--filter-tcp=443 ${hostlists} --dpi-desync=fake --dpi-desync-fake-tls=${bin:tls_clienthello_vk_com}\n');
	T.ok(r.ok, 'strategy save: ' + (r.message ?? ''));
	T.ok(fs.stat(W + '/etc/user/strategies/nfqws/user-new.txt')?.type == 'file', 'saved strategy file');
	r = CMD.strategy_save('user-broken', '--filter-tcp=443 --dpi-desync=bogusmode\n');
	T.eq(r.error, 'dry_run_failed', 'strategy save rejects what nfqws rejects');
	T.ok(fs.stat(W + '/etc/user/strategies/nfqws/user-broken.txt') == null, 'rejected strategy not saved');
	r = CMD.check();
	T.ok(r.ok && r.dry_run?.rc == 0 && length(r.args) > 3, 'check with default configuration: ' + (r.message ?? ''));
}
else
	print('SKIP [commands] nfqws binary not given\n');
T.eq(CMD.strategy_save('mine', '--filter-tcp=443').error, 'bad_id', 'own strategies need user- prefix');
T.eq(CMD.strategy_save('user-big', sprintf('%65537s', 'x')).error, 'too_large', 'strategy size limit');
T.eq(CMD.strategy_save('user-ph', '--x=${hostlists}').error, 'placeholder_in_token', 'strategy save validates placeholders');
T.eq(CMD.strategy_delete('strategy-general').error, 'bad_id', 'bundle strategy cannot be deleted');
T.eq(CMD.strategy_delete('user-none').error, 'not_found', 'delete unknown own strategy');
if (T.NFQWS)
	T.ok(CMD.strategy_delete('user-new').ok && fs.stat(W + '/etc/user/strategies/nfqws/user-new.txt') == null, 'delete own strategy');

r = CMD.fw_show();
T.ok(r.ok && index(r.text, 'table inet zaprett {') >= 0, 'fw show renders the ruleset');

// fw apply / fw remove share one lock (init, hotplug and commands may run them at the same time).
// Nothing reaches nft here: with --if-applied and no ruleset file in the sandbox fw apply stops before nft.
T.ok(fs.stat(P.run + '/zaprett.nft') == null, 'no ruleset file in the sandbox');
set_paths({ lock_fw_wait_ms: 400 });
let fwl = try_lock(P.lock_fw);
let t0 = clock(true);
r = CMD.fw_apply({ if_applied: true });
let t1 = clock(true);
T.eq(r.error, 'busy', 'fw apply gives up while another fw command holds the lock');
T.ok((t1[0] - t0[0]) * 1000 + int((t1[1] - t0[1]) / 1000000) >= 400, 'fw apply waited for the lock first');
T.eq(CMD.fw_remove().error, 'busy', 'fw remove uses the same lock');
unlock(fwl);
r = CMD.fw_apply({ if_applied: true });
T.eq([ r.ok, r.skipped ], [ true, true ], 'negative control: the same call proceeds once the lock is free');

// diag: the default report must fit into the RPC timeout of the web interface; the slow blocks (package
// list, syslog) are only in --full
function section_of(text, title) {
	let parts = split(text, '===== ' + title + ' =====\n');
	return (length(parts) < 2) ? null : split(parts[1], '\n=====')[0];
}
let td0 = clock(true);
let dfast = CMD.diag({});
let td1 = clock(true);
let dfull = CMD.diag({ full: true });
let td2 = clock(true);
let ms = (a, b) => (b[0] - a[0]) * 1000 + int((b[1] - a[1]) / 1000000);
print(sprintf('NOTE [commands] diag: fast %d ms, --full %d ms\n', ms(td0, td1), ms(td1, td2)));
T.eq([ dfast.ok, dfast.full, dfull.ok, dfull.full ], [ true, false, true, true ], 'diag and diag --full');
T.ok(ms(td0, td1) < 5000, sprintf('fast diag fits the limit: %d ms', ms(td0, td1)));
T.ok(index(dfast.text, CMD.DIAG_FULL_HINT) >= 0, 'fast diag points to the full report');
T.eq(length(filter(split(dfast.text, '\n'), (l) => index(l, '(пропущено в быстром отчёте)') >= 0)), 2,
	'both slow blocks are marked as skipped');
T.ok(index(dfast.text, '===== Состояние =====') >= 0 && index(dfast.text, '===== Модули ядра =====') >= 0 &&
	index(dfast.text, '===== nft list table inet zaprett =====') >= 0, 'fast diag keeps state, kernel modules and the nft table');
T.ok(index(dfull.text, '(пропущено в быстром отчёте)') < 0, 'the full report skips nothing');
let pk_full = section_of(dfull.text, 'Пакеты'), pk_fast = section_of(dfast.text, 'Пакеты');
T.ok(pk_full != null && match(pk_full, /(^|\n)(ucode|nftables|uclient-fetch|ca-bundle|firewall4)/) != null,
	'the full report lists installed packages: ' + trim(substr(pk_full ?? '', 0, 60)));
T.ok(pk_fast != null && match(pk_fast, /(^|\n)(ucode|nftables|uclient-fetch|ca-bundle|firewall4)/) == null && index(pk_fast, 'Движки:') >= 0,
	'negative control: the fast report has no package list, only the engine check');
T.ok(section_of(dfull.text, 'Журнал (последние 100 строк)') != null && section_of(dfast.text, 'Журнал') != null,
	'syslog only in the full report');

// test status stays far below the 1 MiB ubus limit: per-target details only for the best results
let many = { results: [], baseline: { targets: [] } };
for (let i = 0; i < 12; i++) {
	let tg = [];
	for (let k = 0; k < 150; k++)
		push(tg, { url: sprintf('https://host%d.example/', k), ok: true });
	push(many.results, { id: 's' + i, targets: tg });
}
for (let k = 0; k < 150; k++)
	push(many.baseline.targets, { url: 'x' });
let tr = CMD.trim_test_results(many);
T.eq([ length(tr.results[0].targets), length(tr.results[9].targets), tr.results[10].targets, tr.results[11].id, length(tr.baseline.targets), tr.targets_trimmed ],
	[ CMD.TEST_TARGETS_MAX, CMD.TEST_TARGETS_MAX, null, 's11', CMD.TEST_TARGETS_MAX, true ], 'test results trimmed');
T.eq(CMD.trim_test_results({ results: [ { id: 'a', targets: [ { url: 'x' } ] } ] }).targets_trimmed, false, 'negative control: small results untouched');

// every warning code of the contract has a text, and there are no codes outside the contract (v1.2 §6.2)
const CONTRACT_WARNINGS = [ 'no_active_lists', 'no_wan', 'flow_offload_enabled', 'nft_queue_missing', 'engine_missing', 'strategy_missing',
	'no_strategy', 'generate_failed', 'bad_config', 'config_was_invalid', 'list_missing', 'source_not_downloaded', 'profile_unfiltered',
	'wide_port_range', 'empty_profile_removed', 'strategy_option_ignored', 'test_running', 'not_running', 'nft_not_applied' ];
T.eq(sort(keys(TXT.WARNINGS)), sort(slice(CONTRACT_WARNINGS)), 'warning texts cover exactly the contract list');
r = CMD.repo_list({});
T.eq([ r.ok, r.fetched_at, r.items ], [ true, null, [] ], 'repo list without cache');
T.eq(CMD.job_status(), { ok: true, job: null }, 'job status without jobs');
T.eq(CMD.test_status().running, false, 'test status without jobs');

// human-readable rendering
let txt = TXT.render('status', { ok: true, running: false, enabled: true, autostart: true, engine: 'nfqws', engine_version: 'v72.13',
	strategy: { id: 'strategy-general' }, list_mode: 'whitelist', lists: [ 'a' ], exclude_lists: [], ipsets: [], nft_applied: false,
	wan: [], flow_offload: { fw4: false, mode: 'auto' }, version: '1.0.0', warnings: [ 'not_running', 'unknown_code' ] });
T.ok(index(txt, 'остановлена') >= 0 && index(txt, 'движок не работает') >= 0 && index(txt, 'unknown_code') >= 0, 'status text with warnings');
T.ok(index(TXT.render('x', { ok: false, error: 'e', message: 'Сообщение' }), 'Ошибка: Сообщение') == 0, 'error text');

// CLI argument handling (exits before any side effect)
let cli = [ 'ucode', '-S', '-L', T.ROOT + '/files/usr/share/ucode/*.uc', '--', T.ROOT + '/files/usr/share/zaprett/cli.uc' ];
function cli_run(args) {
	let a = slice(cli);
	for (let x in args)
		push(a, x);
	return run(a, { timeout: 30000 });
}
r = cli_run([ 'no-such-command', '--json' ]);
T.eq(r.rc, 2, 'unknown command exit code');
T.eq(json(r.stdout).error, 'usage', 'unknown command JSON error');
r = cli_run([ 'status', '--bogus-flag', '--json' ]);
T.eq([ r.rc, json(r.stdout).error ], [ 2, 'usage' ], 'unknown flag rejected');
r = cli_run([ 'repo', 'install', '../../etc', '--json' ]);
T.eq([ r.rc, json(r.stdout).error ], [ 2, 'usage' ], 'bad id rejected before any action');
r = cli_run([ 'test', 'start', '--strategies', 'a,../b', '--json' ]);
T.eq([ r.rc, json(r.stdout).error ], [ 2, 'usage' ], 'bad strategy id in --strategies rejected');
r = cli_run([ 'help' ]);
T.ok(r.rc == 0 && index(r.stdout, 'Использование: zaprett') == 0, 'help');
r = cli_run([ '__job-run', 'id', 'no-such-job' ]);
T.eq(r.rc, 2, 'internal job runner rejects unknown job names');

exit(T.finish());
