// SPDX-License-Identifier: MIT
//
// Self-check of the rpcd plugin luci.zaprett against mock-zaprett.uc.
// Run through tests/run-plugin-tests.sh, which prepares a patched copy of
// the plugin (CLI and TMPDIR pointing into the test directory).
//
// Usage: ucode plugin-test.uc <test-dir>

'use strict';

import * as fs from 'fs';

const DIR = ARGV[0];
const plugin = loadfile(DIR + '/luci.zaprett.test')()['luci.zaprett'];
const plugin_nocli = loadfile(DIR + '/luci.zaprett.nocli')()['luci.zaprett'];

let passed = 0, failed = 0;

function check(name, cond, detail) {
	if (cond) {
		passed++;
		print('PASS ', name, '\n');
	}
	else {
		failed++;
		print('FAIL ', name, ' :: ', substr(sprintf('%J', detail), 0, 600), '\n');
	}
}

function call(name, args) {
	return plugin[name].call({ args: args ?? {} });
}

function is_err(res, code) {
	return type(res) == 'object' && res.ok === false && res.error == code;
}

// --- signature: argument type hints as rpcd derives them
const hints = {
	service: { action: 'string' }, items: { type: 'string' }, toggle_item: { id: 'string', enabled: 'bool' },
	set_strategy: { id: 'string' }, strategy_show: { id: 'string' }, strategy_save: { id: 'string', text: 'string' },
	strategy_delete: { id: 'string' }, user_get: { id: 'string' }, user_set: { id: 'string', text: 'string' },
	repo_list: { type: 'string' }, repo_install: { ids: 'array' }, repo_remove: { id: 'string' },
	repo_upgrade: { ids: 'array' }, wizard_apply: { services: 'array' },
	sources_update: { names: 'array' },
	source_save: { name: 'string', title: 'string', type: 'string', url: 'string', interval_hours: 'int', min_entries: 'int', enabled: 'bool' },
	source_delete: { name: 'string' },
	test_start: { strategies: 'array', quick: 'bool', apply_if_better: 'bool' }, test_apply: { id: 'string' }, job_log: { tail: 'int' },
	test_status: { brief: 'bool' }, probe_start: { services: 'array' }, log: { tail: 'int' }, page: { name: 'string' },
	diagnose_start: { services: 'array' }
};

for (let m, def in plugin) {
	const want = hints[m] ?? {};
	const have = {};

	for (let k, v in (def.args ?? {}))
		have[k] = type(v);

	check('signature ' + m, sprintf('%J', have) == sprintf('%J', want), { have, want });
}

check('method count', length(keys(plugin)) == 39, keys(plugin));

// --- basic reads
let r = call('status');
check('status ok', r.ok === true && r.engine == 'nfqws', r);

r = call('service', { action: 'start' });
check('service start', r.ok === true, r);
check('status running after start', call('status').running === true, null);

for (let bad in [ 'reboot', 'start; id', '', null ])
	check('service rejects ' + bad, is_err(call('service', { action: bad }), 'invalid_argument'), bad);

r = call('items');
check('items all', r.ok === true && length(r.items) >= 10, length(r.items));

r = call('items', { type: 'list' });
check('items type filter', r.ok === true && length(filter(r.items, i => i.type != 'list')) == 0, r.items);
check('items rejects bad type', is_err(call('items', { type: 'lists; id' }), 'invalid_argument'), null);
r = call('items', { type: 'byedpi' });
check('items accepts byedpi', r.ok === true && length(r.items) == 1 && r.items[0].type == 'byedpi', r);

// --- toggles
r = call('toggle_item', { id: 'zaprett-youtube', enabled: true });
check('toggle enable', r.ok === true && index(call('status').lists, 'zaprett-youtube') >= 0, r);
r = call('toggle_item', { id: 'zaprett-youtube', enabled: false });
check('toggle disable', r.ok === true && index(call('status').lists, 'zaprett-youtube') < 0, r);
check('toggle rejects path id', is_err(call('toggle_item', { id: '../etc/passwd', enabled: true }), 'invalid_id'), null);
check('toggle rejects string bool', is_err(call('toggle_item', { id: 'zaprett-youtube', enabled: 'yes' }), 'invalid_argument'), null);
check('toggle rejects 97-char id', is_err(call('toggle_item', { id: sprintf('%097d', 1), enabled: true }), 'invalid_id'), null);
check('toggle accepts 96-char id (backend says not_found)', is_err(call('toggle_item', { id: sprintf('%096d', 1), enabled: true }), 'not_found'), null);

// --- strategies
r = call('set_strategy', { id: 'strategy-alt' });
check('set_strategy', r.ok === true && call('status').strategy.id == 'strategy-alt', r);
check('set_strategy rejects space', is_err(call('set_strategy', { id: 'a b' }), 'invalid_id'), null);

r = call('strategy_show', { id: 'strategy-general' });
check('strategy_show keeps ${hostlists}', r.ok === true && index(r.text, '${hostlists}') >= 0, r);

const nasty = "--dpi-desync=fake\n'; touch " + DIR + "/PWNED1 #\n$(touch " + DIR + "/PWNED2)\n`touch " + DIR + "/PWNED3`\nback\\slash \\n tab\t кириллица \"quotes\"\n";
r = call('strategy_save', { id: 'user-test', text: nasty });
check('strategy_save nasty text', r.ok === true && r.bytes == length(nasty), r);
r = call('strategy_show', { id: 'user-test' });
check('strategy text round trip', r.ok === true && r.text === nasty, r);
check('no shell injection', !fs.access(DIR + '/PWNED1') && !fs.access(DIR + '/PWNED2') && !fs.access(DIR + '/PWNED3'), null);
check('strategy_save needs user- prefix', is_err(call('strategy_save', { id: 'strategy-x', text: '--dpi-desync=fake' }), 'invalid_id'), null);
check('strategy_save rejects > 1 MiB', is_err(call('strategy_save', { id: 'user-big', text: sprintf('%1048577s', 'x') }), 'invalid_argument'), null);
check('strategy_save rejects non-string', is_err(call('strategy_save', { id: 'user-x', text: [ 'a' ] }), 'invalid_argument'), null);
r = call('strategy_save', { id: 'user-bad', text: 'no desync here' });
check('backend validation error passes through with details', is_err(r, 'dry_run_failed') && length(r.message) > 0 && r.dry_run?.rc == 1, r);
check('set_strategy to custom', call('set_strategy', { id: 'user-test' }).ok === true, null);
r = call('check');
check('check failure keeps dry_run output', is_err(r, 'dry_run_failed') && r.dry_run?.output == 'nfqws: invalid desync mode', r);
check('strategy_delete in use passes through', is_err(call('strategy_delete', { id: 'user-test' }), 'item_active'), null);
call('set_strategy', { id: 'strategy-alt' });
check('strategy_delete requires user- prefix', is_err(call('strategy_delete', { id: 'strategy-general' }), 'invalid_id'), null);
check('strategy_delete ok', call('strategy_delete', { id: 'user-test' }).ok === true, null);
check('deleted strategy is gone', is_err(call('strategy_show', { id: 'user-test' }), 'strategy_not_found'), null);

// --- user lists
const hosts = "youtube.com\n# комментарий\nexample.org\n";
r = call('user_set', { id: 'user-hosts', text: hosts });
check('user_set', r.ok === true && r.entries == 2, r);
check('user_get round trip', call('user_get', { id: 'user-hosts' }).text === hosts, null);
r = call('user_set', { id: 'user-hosts', text: '*.example.com\n' });
check('user_set line errors pass through', is_err(r, 'invalid_entries') && r.errors?.[0]?.line == 1, r);
check('user_get requires user- id', is_err(call('user_get', { id: 'zaprett-youtube' }), 'invalid_id'), null);
check('user_set empty text allowed', call('user_set', { id: 'user-ipset', text: '' }).ok === true, null);

// --- broken backend output
r = call('strategy_show', { id: 'mock-invalid-json' });
check('invalid JSON -> backend_error', is_err(r, 'backend_error') && index(r.message, 'this is not json') >= 0, r);
r = call('strategy_show', { id: 'mock-stderr' });
check('stderr only -> backend_error with stderr', is_err(r, 'backend_error') && index(r.message, 'fatal error in mock') >= 0, r);
r = call('strategy_show', { id: 'mock-big' });
check('reply over 900 KiB -> reply_too_large', is_err(r, 'reply_too_large') && index(r.message, 'ubus') >= 0, r);
r = call('strategy_show', { id: 'mock-huge' });
check('output over 4 MiB -> reply_too_large', is_err(r, 'reply_too_large') && index(r.message, 'bytes') >= 0, r);

const t0 = time();
r = call('strategy_show', { id: 'mock-sleep' });
const dt = time() - t0;
check('hanging backend -> timeout', is_err(r, 'timeout'), r);
check('time limit stops the call within 14..17 s', dt >= 14 && dt <= 17, dt);

r = plugin_nocli.status.call({ args: {} });
check('missing CLI -> backend_missing', is_err(r, 'backend_missing'), r);

// --- repository and jobs
check('repo_list before fetch', call('repo_list').ok === true, null);
check('repo_list rejects bad type', is_err(call('repo_list', { type: 'x y' }), 'invalid_argument'), null);
check('repo_install rejects empty', is_err(call('repo_install', { ids: [] }), 'invalid_id'), null);
check('repo_install rejects bad id', is_err(call('repo_install', { ids: [ 'ok-id', 'bad id' ] }), 'invalid_id'), null);
check('repo_install rejects non-array', is_err(call('repo_install', { ids: 'strategy-alt' }), 'invalid_id'), null);
r = call('repo_install', { ids: [ 'strategy-alt10', 'strategy-alt10' ] });
check('repo_install starts job', r.ok === true && r.job?.name == 'repo-install', r);
check('second job -> job_busy', is_err(call('repo_fetch'), 'job_busy'), null);
r = call('job_status');
check('job_status running', r.ok === true && r.job?.state == 'running', r);
r = call('job_cancel');
check('job_cancel answers cancelling at once', r.ok === true && r.state == 'cancelling' && r.job?.name == 'repo-install', r);
check('job_status finishes the cancellation', call('job_status').job?.state == 'cancelled', null);
check('job_cancel without a job', is_err(call('job_cancel'), 'no_job'), null);
r = call('repo_upgrade');
check('repo_upgrade without ids (--all)', r.ok === true && r.job?.name == 'repo-upgrade', r);
call('job_cancel');
check('repo_upgrade rejects bad ids', is_err(call('repo_upgrade', { ids: [ '' ] }), 'invalid_id'), null);
check('repo_remove in use passes through', is_err(call('repo_remove', { id: 'quic_initial_www_google_com' }), 'item_in_use'), null);

// --- subscriptions
r = call('sources_list');
check('sources_list', r.ok === true && length(r.sources) == 2, r);
check('sources_list fields of v1.2', r.sources[0].min_entries != null && r.sources[0].status != null &&
	exists(r.sources[0], 'message') && exists(r.sources[0], 'downloaded') && r.sources[0].item_id == 'src-refilter_domains', r.sources[0]);
r = call('sources_update');
check('sources_update all', r.ok === true && r.job?.name == 'sources-update', r);
call('job_cancel');
r = call('sources_update', { names: [ 'refilter_domains', 'refilter_domains' ] });
check('sources_update by name', r.ok === true, r);
call('job_cancel');
for (let bad in [ [ 'Bad-Name' ], [ 'a;b' ], 'refilter_domains', [ sprintf('%033d', 1) ] ])
	check('sources_update rejects ' + sprintf('%J', bad), is_err(call('sources_update', { names: bad }), 'invalid_argument'), bad);

const src = { name: 'my_list', title: 'Мой список «тест»', type: 'list', url: 'https://example.com/list.txt?a=1&b=2', interval_hours: 48, min_entries: 5, enabled: true };
r = call('source_save', src);
check('source_save', r.ok === true && r.created === true && r.item_id == 'src-my_list' &&
	r.mock_received?.title == src.title && r.mock_received?.url == src.url && r.mock_received?.min_entries == 5 && r.mock_received?.enabled === true, r);
r = call('source_save', { ...src, min_entries: null });
check('source_save without min_entries', r.ok === true && !exists(r.mock_received, 'min_entries'), r);
r = call('source_save', { name: 'my_list', enabled: false });
check('source_save with only the changed field', r.ok === true && length(keys(r.mock_received)) == 1 && r.mock_received.enabled === false, r);
check('source_save needs at least one field', is_err(call('source_save', { name: 'my_list' }), 'invalid_argument'), null);

const bad_sources = [
	[ 'uppercase name', { ...src, name: 'MyList' } ],
	[ 'http url', { ...src, url: 'http://example.com/list.txt' } ],
	[ 'url with space', { ...src, url: 'https://example.com/a b' } ],
	[ 'title with newline', { ...src, title: "a\nb" } ],
	[ 'unknown type', { ...src, type: 'nfqws' } ],
	[ 'zero interval', { ...src, interval_hours: 0 } ],
	[ 'string interval', { ...src, interval_hours: '48' } ],
	[ 'negative min_entries', { ...src, min_entries: -1 } ],
	[ 'string enabled', { ...src, enabled: 'true' } ]
];

for (let b in bad_sources)
	check('source_save rejects ' + b[0], is_err(call('source_save', b[1]), 'invalid_argument'), b[1]);

check('source_delete', call('source_delete', { name: 'my_list' }).ok === true, null);
check('source_delete rejects bad name', is_err(call('source_delete', { name: '../x' }), 'invalid_argument'), null);

// --- presets, wizard, tester
r = call('presets');
check('presets', r.ok === true && length(r.services) == 5, r);
check('wizard_apply rejects empty', is_err(call('wizard_apply', { services: [] }), 'invalid_argument'), null);
r = call('wizard_apply', { services: [ 'youtube', 'discord' ] });
check('wizard_apply', r.ok === true && index(r.lists, 'zaprett-youtube') >= 0, r);
r = call('wizard_apply', { services: [ 'youtube', 'chatgpt_claude' ] });
check('wizard_apply reports skipped services', r.ok === true && r.skipped?.[0]?.id == 'chatgpt_claude' && r.skipped[0].reason == 'works_no', r.skipped);
r = call('wizard_apply', { services: [ 'rkn_full' ] });
check('wizard_apply starts the subscription job', r.ok === true && index(r.sources, 'refilter_domains') >= 0 && r.job?.name == 'sources-update', r);
check('sources_save during a job -> job_busy', is_err(call('source_save', { name: 'my_list', enabled: true }), 'job_busy'), null);
call('job_cancel');
call('job_status');
check('wizard_apply preset_unavailable passes through', is_err(call('wizard_apply', { services: [ 'chatgpt_claude' ] }), 'preset_unavailable'), null);
// variants (contract v1.7 §16.4): "id:variant", both parts follow the id rule
r = call('wizard_apply', { services: [ 'youtube', 'discord:full' ] });
check('wizard_apply with a variant', r.ok === true && r.variants?.discord == 'full' && r.variants?.youtube === null &&
	index(r.lists, 'zaprett-discord-full') >= 0 && index(r.lists, 'zaprett-discord') < 0, r);
r = call('presets');
check('presets: enabled_variant', filter(r.services, s => s.id == 'discord')[0]?.enabled_variant == 'full', r.services);
r = call('wizard_apply', { services: [ 'youtube', 'discord' ] });
check('wizard_apply back to the core set', r.ok === true && r.variants?.discord === null && index(r.lists, 'zaprett-discord-full') < 0 &&
	index(r.lists, 'zaprett-discord') >= 0, r);
check('wizard_apply unknown_variant passes through', is_err(call('wizard_apply', { services: [ 'discord:nope' ] }), 'unknown_variant'), null);
const long_id = join('', map(split(sprintf('%96s', ''), ''), () => 'a'));
check('wizard_apply accepts parts of 96 characters', call('wizard_apply', { services: [ 'youtube:' + long_id ] }).error == 'unknown_variant', null);
for (let bad in [ 'discord:', ':full', 'a:b:c', 'discord:fu ll', 'discord:../x', 'discord:full;reboot', 'discord::full', 'discord:' + long_id + 'a', long_id + 'a:full', 7, null ])
	check(sprintf('wizard_apply rejects %J', bad), is_err(call('wizard_apply', { services: [ 'youtube', bad ] }), 'invalid_argument'), bad);
check('test_start rejects comma id', is_err(call('test_start', { strategies: [ 'a,b' ] }), 'invalid_id'), null);
check('test_start rejects string quick', is_err(call('test_start', { quick: 'yes' }), 'invalid_argument'), null);
r = call('test_start', { quick: true, strategies: [ 'strategy-general', 'strategy-alt' ] });
check('test_start', r.ok === true && r.job?.name == 'test', r);
check('test_status', call('test_status').ok === true, null);
r = call('test_stop');
check('test_stop answers cancelling at once', r.ok === true && r.state == 'cancelling', r);
check('test_status finishes the cancellation', call('test_status').job?.state == 'cancelled', null);
check('test_stop without a job', is_err(call('test_stop'), 'no_job'), null);

r = call('test_start', { strategies: [ 'mock-trimmed-results' ] });
check('test_start trimmed results', r.ok === true, r);
r = call('test_status');
check('backend-trimmed test_status passes through', r.ok === true && r.results?.targets_trimmed === true &&
	length(r.results.results) == 64 && length(r.results.results[0].targets) == 100 && r.results.results[10].targets == null &&
	r.targets_trimmed == null, { trimmed: r.results?.targets_trimmed, own: r.targets_trimmed });
call('job_cancel');
call('test_status');

r = call('test_start', { strategies: [ 'mock-big-results' ] });
check('test_start big results', r.ok === true, r);
r = call('test_status');
check('oversized test_status is trimmed by the plugin', r.ok === true && r.targets_trimmed === true && length(r.results?.results) == 64 &&
	r.results.results[0].targets == null && length(r.results.baseline?.targets) == 120 && length(sprintf('%J', r)) <= 900000, r.targets_trimmed);
call('job_cancel');
call('test_status');
r = call('test_apply', { id: 'strategy-general' });
check('test_apply', r.ok === true && r.reloaded != null, r);

check('job_log tail', call('job_log', { tail: 50 }).ok === true, null);
check('job_log no tail', call('job_log').ok === true, null);
for (let bad in [ 0, 5001, '5', 1.5 ])
	check('job_log rejects tail ' + bad, is_err(call('job_log', { tail: bad }), 'invalid_argument'), bad);

check('check', call('check').ok === true, null);
r = call('diag');
check('diag asks for the quick report', r.ok === true && type(r.text) == 'string' && r.full === false, r.full);

// --- contract v1.3 (ARCHITECTURE §14.3, §14.7)

// unknown arguments are refused (rpcd does the same before the call)
check('extra argument -> invalid_argument', is_err(call('status', { foo: 1 }), 'invalid_argument'), null);
check('extra argument next to a known one', is_err(call('page', { name: 'overview', tail: 5 }), 'invalid_argument'), null);
check('ubus_rpc_session is accepted', call('status', { ubus_rpc_session: 'x' }).ok === true, null);

r = call('status');
check('status v1.3 fields pass through', exists(r, 'job') && r.monitor?.state == 'ok' && r.ipv6_wan === true &&
	index(r.warnings, 'ipv6_wan_unhandled') >= 0, r);

const page_parts = {
	overview: [ 'status', 'job', 'presets', 'monitor', 'probe' ],
	lists: [ 'status', 'job', 'items', 'sources', 'presets' ],
	strategies: [ 'status', 'job', 'items', 'test' ],
	diagnostics: [ 'status', 'job', 'monitor' ]
};

for (let name, parts in page_parts) {
	r = call('page', { name: name });
	check('page ' + name, r.ok === true && length(filter(parts, p => r[p]?.ok !== true)) == 0, r);
}

for (let bad in [ 'settings', 'repo', '', 'overview; id', 'Overview', null, 1, [ 'overview' ] ])
	check('page rejects ' + sprintf('%J', bad), is_err(call('page', { name: bad }), 'invalid_argument'), bad);

check('page without name', is_err(call('page'), 'invalid_argument'), null);

// log
r = call('log');
check('log default tail', r.ok === true && length(r.lines) == 200 && index(r.lines[0], '<b>html</b>') >= 0, length(r.lines));
r = call('log', { tail: 1000 });
check('log tail 1000', r.ok === true && length(r.lines) == 1000, length(r.lines));
check('log tail 1', call('log', { tail: 1 }).lines?.[0] != null, null);
for (let bad in [ 0, 1001, -1, '200', 1.5, true ])
	check('log rejects tail ' + sprintf('%J', bad), is_err(call('log', { tail: bad }), 'invalid_argument'), bad);

// monitor
r = call('monitor_status');
check('monitor_status', r.ok === true && r.monitor?.state == 'ok' && length(r.monitor.history) == 48, r.monitor?.state);

// probe
for (let bad in [ [ 'a,b' ], 'youtube', [ '' ], [ 'x y' ], [ 1 ] ])
	check('probe_start rejects ' + sprintf('%J', bad), is_err(call('probe_start', { services: bad }), 'invalid_argument'), bad);

check('probe_start unknown service passes through', is_err(call('probe_start', { services: [ 'nosuchservice' ] }), 'unknown_service'), null);
check('probe_status before a probe', call('probe_status').probe === null, null);
r = call('probe_start', { services: [ 'youtube', 'discord', 'youtube' ] });
check('probe_start starts a job', r.ok === true && r.job?.name == 'probe', r);
check('probe during a job -> job_busy', is_err(call('probe_start'), 'job_busy'), null);
system([ 'sleep', '3' ]);
check('job of the probe is done', call('job_status').job?.state == 'done', null);
r = call('probe_status');
check('probe_status after the probe', r.ok === true && length(r.probe?.services) == 2 && r.probe.services[0].id == 'youtube' &&
	r.probe.services[0].ok == 1 && r.probe.services[1].ok == 0, r.probe);
r = call('probe_start');
check('probe_start without services', r.ok === true && r.job?.name == 'probe', r);
call('job_cancel');
call('job_status');

// test_status brief and --apply-if-better
check('test_status rejects string brief', is_err(call('test_status', { brief: 'yes' }), 'invalid_argument'), null);
check('test_start rejects string apply_if_better', is_err(call('test_start', { apply_if_better: 'yes' }), 'invalid_argument'), null);
r = call('test_start', { quick: true, apply_if_better: true });
check('test_start with apply_if_better', r.ok === true && r.job?.name == 'test', r);
r = call('test_status', { brief: true });
check('--apply-if-better reaches the CLI', index(r.mock_flags, '--apply-if-better') >= 0 && index(r.mock_flags, '--quick') >= 0, r.mock_flags);
check('test_status brief has no targets', r.ok === true && r.results?.baseline != null && !exists(r.results.baseline, 'targets'), r.results?.baseline);
check('test_status without brief has targets', length(call('test_status').results?.baseline?.targets) > 0, null);
call('job_cancel');
call('test_status');
r = call('test_start', { quick: true, apply_if_better: false });
check('apply_if_better false is not passed', r.ok === true && index(call('test_status').mock_flags, '--apply-if-better') < 0, null);
call('job_cancel');
call('test_status');

r = call('test_start', { strategies: [ 'mock-big-results' ] });
check('test_start big results for the page', r.ok === true, r);
r = call('page', { name: 'strategies' });
check('oversized page strategies is trimmed by the plugin', r.ok === true && r.test?.targets_trimmed === true &&
	r.test.results?.results?.[0]?.targets == null && length(sprintf('%J', r)) <= 900000, r.test?.targets_trimmed);
call('job_cancel');
call('test_status');

// --- contract v1.4 (ARCHITECTURE §15.3, §15.4, §15.7)

// encrypted DNS
r = call('dns_status');
check('dns_status before setup', r.ok === true && r.dns?.encrypted === false && r.dns.provider === null, r);
check('dns_status refuses arguments', is_err(call('dns_status', { provider: 'stubby' }), 'invalid_argument'), null);
check('dns_setup refuses arguments', is_err(call('dns_setup', { package: 'x; id' }), 'invalid_argument'), null);
check('status carries dns', call('status').dns?.encrypted === false, null);
r = call('dns_setup');
check('dns_setup starts a job', r.ok === true && r.job?.name == 'dns-setup', r);
check('dns_setup during a job -> job_busy', is_err(call('dns_setup'), 'job_busy'), null);
check('diagnose during a job -> job_busy', is_err(call('diagnose_start'), 'job_busy'), null);
system([ 'sleep', '3' ]);
check('job of dns setup is done', call('job_status').job?.state == 'done', null);
r = call('dns_status');
check('dns_status after setup', r.ok === true && r.dns?.encrypted === true && r.dns.provider == 'https-dns-proxy', r);
r = call('dns_setup');
check('dns_setup again -> changed false', r.ok === true && r.changed === false && r.job == null, r);

// diagnosis of the blocking method
for (let bad in [ [ 'a,b' ], 'youtube', [ '' ], [ 'x y' ], [ 1 ], [ '../x' ] ])
	check('diagnose_start rejects ' + sprintf('%J', bad), is_err(call('diagnose_start', { services: bad }), 'invalid_argument'), bad);

check('diagnose_start refuses unknown argument', is_err(call('diagnose_start', { services: [ 'youtube' ], full: true }), 'invalid_argument'), null);
check('diagnose_start unknown service passes through', is_err(call('diagnose_start', { services: [ 'nosuchservice' ] }), 'unknown_service'), null);
check('diagnose_status before a diagnosis', call('diagnose_status').diagnose === null, null);
r = call('diagnose_start', { services: [ 'youtube', 'discord', 'telegram', 'discord' ] });
check('diagnose_start starts a job', r.ok === true && r.job?.name == 'diagnose', r);
system([ 'sleep', '3' ]);
check('job of the diagnosis is done', call('job_status').job?.state == 'done', null);
r = call('diagnose_status');
check('diagnose_status after the diagnosis', r.ok === true && length(r.diagnose?.targets) == 3 &&
	r.diagnose.targets[0].verdict == 'throttle' && r.diagnose.targets[1].verdict == 'dns_spoof' &&
	r.diagnose.targets[1].dns?.spoofed === true && r.diagnose.targets[0].host == 'www.youtube.com' &&
	r.diagnose.summary?.counts?.ip_block == 1, r.diagnose);
r = call('diagnose_start');
check('diagnose_start without services', r.ok === true && r.job?.name == 'diagnose', r);
call('job_cancel');
call('job_status');

// pages of v1.4
r = call('page', { name: 'diagnostics' });
check('page diagnostics carries dns and diagnose', r.ok === true && r.dns?.ok === true && r.dns.dns?.encrypted === true &&
	r.diagnose?.ok === true && exists(r.diagnose, 'diagnose'), r);
r = call('page', { name: 'overview' });
check('page overview carries dns', r.ok === true && r.dns?.dns?.encrypted === true && r.status?.flow_offload?.own === false, r.dns);

// --- temporary files are cleaned up
check('no temporary files left', length(fs.lsdir(DIR + '/run') ?? []) == 0, fs.lsdir(DIR + '/run'));

// --- unsafe temporary directory is refused
fs.rmdir(DIR + '/run');
fs.symlink('/tmp', DIR + '/run');
check('symlinked tmp dir -> tmp_unavailable', is_err(call('status'), 'tmp_unavailable'), null);
fs.unlink(DIR + '/run');
fs.mkdir(DIR + '/run');
fs.chmod(DIR + '/run', 0o777);
check('world-writable tmp dir -> tmp_unavailable', is_err(call('status'), 'tmp_unavailable'), null);
fs.rmdir(DIR + '/run');
check('missing tmp dir is created', call('status').ok === true, null);
const st = fs.lstat(DIR + '/run');
check('created tmp dir is private', st?.type == 'directory' && !st.perm.group_read && !st.perm.other_read, st);

print(sprintf('\nRESULT passed=%d failed=%d\n', passed, failed));
exit(failed ? 1 : 0);
