#!/usr/bin/env node
// SPDX-License-Identifier: MIT
//
// Smoke scenarios of the luci-app-zaprett views on a fake LuCI runtime
// (tests/fakeluci.js): load -> render -> polling -> quick setup, with the
// answers of the new (v1.4, v1.3) and of an older zaprett service. Every scenario
// also checks that no router string reached innerHTML.
//
// Usage: node tests/view-smoke.js

'use strict';

const F = require('./fakeluci.js');

let passed = 0, failed = 0;

function check(name, cond, detail) {
	if (cond) { passed++; console.log('PASS', name); }
	else { failed++; console.log('FAIL', name, detail !== undefined ? JSON.stringify(detail).slice(0, 400) : ''); }
}

const XSS = '<img src=x onerror=alert(1)>';

function job(name, state, extra) {
	return Object.assign({ id: '1-1', name, state, progress: (state == 'running') ? 40 : 100, message: 'msg ' + XSS, started: 1758100000, finished: 1758100100, rc: 0, result: {} }, extra ?? {});
}

function status(extra) {
	return Object.assign({
		ok: true, enabled: true, autostart: true, running: true, engine: 'nfqws', engine_version: 'v72.13',
		strategy: { id: 'strategy-general', name: 'General ' + XSS }, list_mode: 'whitelist',
		lists: [ 'zaprett-youtube' ], exclude_lists: [], ipsets: [], exclude_ipsets: [], nft_applied: true, wan: [ 'eth1' ],
		flow_offload: { fw4: false, mode: 'auto' }, warnings: [ 'ipv6_wan_unhandled', 'test_running', 'low_memory', 'monitor_degraded' ],
		version: '1.3.0', job: null, monitor: { state: 'ok', consecutive_failures: 0, checked_at: 1758100000 }, queue: { packets: 1234 }, ipv6_wan: true
	}, extra ?? {});
}

const presets = {
	ok: true, services: [
		{ id: 'youtube', name: 'YouTube', name_en: 'YouTube EN', description: 'Описание ' + XSS, description_en: 'Description EN', note: 'Заметка', note_en: 'Note EN',
			lists: [ 'zaprett-youtube' ], ipsets: [], sources: [], tier: 'light', works: 'yes', test_targets: [ { url: 'https://www.youtube.com/', min_bytes: 1 } ], enabled: false },
		{ id: 'discord', name: 'Discord', lists: [ 'zaprett-discord' ], ipsets: [], sources: [], tier: 'light', works: 'yes', test_targets: [ { url: 'https://discord.com/', min_bytes: 1 } ], enabled: false },
		{ id: 'rkn', name: 'РКН', lists: [], ipsets: [], sources: [ 'refilter' ], tier: 'full', works: 'partial', test_targets: [], enabled: false }
	], defaults: { services: [ 'youtube', 'discord' ] }, tiers: { full: { min_ram_mib: 200 } }, ram_total_mib: 128, recommended_tier: 'light'
};

const probeResult = (ytOk, dcOk) => ({ started: 1758100000, finished: 1758100010, engine_running: true, strategy: 'strategy-general', ok: 1, total: 2, services: [
	{ id: 'youtube', name: 'YouTube', ok: ytOk, total: 3, avg_ms: 420, targets: [ { url: 'https://www.youtube.com/' + XSS, ok: ytOk > 0, ms: 420, bytes: 900000, error: ytOk ? null : 'reset' } ] },
	{ id: 'discord', name: 'Discord', ok: dcOk, total: 3, avg_ms: null, targets: [ { url: 'https://discord.com/', ok: dcOk > 0, ms: 5000, bytes: 0, error: dcOk ? null : 'reset' } ] }
] });

const monitor = { ok: true, monitor: { enabled: true, auto_repair: true, interval: 30, threshold: 3, state: 'degraded', checked_at: 1758100000, ok: 1, total: 3,
	consecutive_failures: 3, history: Array.from({ length: 48 }, (_, i) => ({ t: 1758100000 - i * 1800, ok: i % 5 ? 3 : 0, total: i == 7 ? 0 : 3 })), last_repair: { t: 1758000000, job_id: 'x' } } };

function hasAttr(node, k, v) {
	if (node.attrs && node.attrs[k] == v)
		return true;

	return (node.childNodes ?? []).some(c => c.childNodes && hasAttr(c, k, v));
}

function findText(node, needle) {
	return F.text(node).indexOf(needle) >= 0;
}

async function overviewFull() {
	F.state.calls = []; F.state.innerHTML = [];
	F.setReplies({
		page: p => ({ ok: true, status: status(), job: { ok: true, job: null }, presets, monitor, probe: { ok: true, probe: probeResult(3, 0) } })
	});
	const v = F.useModule('view.overview', 'view/zaprett/overview.js');
	const data = await v.load();
	check('overview: one page call on load', F.state.calls.length == 1 && F.state.calls[0].method == 'page' && F.state.calls[0].nobatch === true, F.state.calls);
	const node = v.render(data);
	check('overview: renders probe cards', findText(node, 'YouTube EN') && findText(node, '✓ 3/3, 420 ms') && findText(node, '✗ 0/3'), F.text(node).slice(0, 300));
	check('overview: English name shown for lang en', findText(node, 'Description EN') && findText(node, 'Note EN'), null);
	check('overview: monitor block with state', findText(node, 'Sites stopped opening') && findText(node, 'Checks: 47, failed: 10'), null);
	check('overview: queue counter', findText(node, '1234 packets processed'), null);
	check('overview: new warnings explained', findText(node, 'IPv6 connections go without the bypass') && findText(node, 'The enabled lists are too large') && findText(node, 'Sites of the enabled services stopped opening'), null);
	check('overview: info warning in its own block', findText(node, 'For information'), null);
	check('overview: wizard collapsed when set up', findText(node, 'Quick setup: choose the services again'), null);
	check('overview: no string reached innerHTML', F.state.innerHTML.length == 0, F.state.innerHTML);

	/* one poll tick = one status call (status carries job and monitor) */
	F.state.calls = [];
	F.setReplies({ status: status() });
	await F.state.polls[F.state.polls.length - 1]();
	check('overview: poll tick makes one call', F.state.calls.length == 1 && F.state.calls[0].method == 'status', F.state.calls.map(c => c.method));

	/* monitor checked_at changed -> monitor reloaded */
	F.state.calls = [];
	F.setReplies({ status: status({ monitor: { state: 'ok', consecutive_failures: 0, checked_at: 1758200000 } }), monitor_status: monitor });
	await F.state.polls[F.state.polls.length - 1]();
	check('overview: new monitor check reloads the monitor', F.state.calls.map(c => c.method).join(',') == 'status,monitor_status', F.state.calls.map(c => c.method));

	/* stopped service: the monitor block explains that checks are paused */
	check('overview: no pause note while running', !findText(v.monitorNode, 'does not check the sites now'), null);
	F.setReplies({ status: status({ running: false, monitor: { state: 'ok', consecutive_failures: 0, checked_at: 1758100000 } }) });
	await F.state.polls[F.state.polls.length - 1]();
	check('overview: pause note after stop', findText(v.monitorNode, 'does not check the sites now'), F.text(v.monitorNode).slice(0, 200));

	/* hidden tab: no calls */
	F.state.calls = [];
	F.document.hidden = true;
	await F.state.polls[F.state.polls.length - 1]();
	F.document.hidden = false;
	check('overview: no poll while the tab is hidden', F.state.calls.length == 0, F.state.calls);
	F.state.polls = [];
}

async function overviewOldBackend() {
	F.state.calls = []; F.state.innerHTML = [];
	const usage = { ok: false, error: 'usage', message: 'Неверные аргументы' };
	F.setReplies({
		page: usage, status: status({ job: undefined, monitor: undefined, queue: undefined, warnings: [ 'no_active_lists' ], enabled: false, running: false }),
		job_status: { ok: true, job: job('repo-install', 'running') }, presets, monitor_status: usage, probe_status: usage
	});
	const v = F.useModule('view.overview', 'view/zaprett/overview.js');
	const data = await v.load();
	const methods = F.state.calls.map(c => c.method).sort().join(',');
	check('old backend: page falls back to single calls', methods == 'job_status,monitor_status,page,presets,probe_status,status', methods);
	const node = v.render(data);
	check('old backend: monitor hidden, probe says update', !findText(node, 'Availability monitor') && findText(node, 'cannot check the sites yet'), null);
	check('old backend: wizard open at the top', findText(node, 'Apply and start') && !findText(node, 'choose the services again'), null);
	check('old backend: no queue row', !findText(node, 'Engine activity'), null);
	check('old backend: running job banner', findText(node, 'Background task'), null);
	F.state.calls = [];
	await F.state.polls[F.state.polls.length - 1]();
	check('old backend: poll asks the job separately', F.state.calls.map(c => c.method).join(',') == 'status,job_status', F.state.calls.map(c => c.method));
	F.state.polls = [];
}

async function wizardFlow(probeOk) {
	F.state.calls = []; F.state.innerHTML = []; F.state.modals = []; F.state.notes = [];
	let probeDone = false, testDone = false, applied = false;
	const replies = {
		page: { ok: true, status: status({ enabled: false, running: false, warnings: [ 'no_active_lists' ] }), job: { ok: true, job: null }, presets, monitor, probe: { ok: true, probe: null } },
		wizard_apply: { ok: true, services: [ 'youtube', 'discord' ], skipped: [], sources: [], reloaded: false, warnings: [ 'low_memory' ] },
		service: { ok: true },
		probe_start: () => { probeDone = false; return { ok: true, job: { id: 'p', name: 'probe' } }; },
		job_status: () => {
			if (!probeDone) { probeDone = true; return { ok: true, job: job('probe', 'running') }; }
			if (testDone === 'running') { testDone = true; return { ok: true, job: job('test', 'running') }; }
			return { ok: true, job: job(testDone ? 'test' : 'probe', 'done', testDone ? { result: { applied: 'strategy-alt' } } : {}) };
		},
		probe_status: () => ({ ok: true, probe: (probeOk || applied) ? probeResult(3, 3) : probeResult(3, 0) }),
		status: status(),
		test_start: p => { testDone = 'running'; return p.apply_if_better ? { ok: true, job: { id: 't', name: 'test' } } : { ok: false, error: 'bad' }; },
		test_status: p => { applied = true; return { ok: true, job: job('test', 'done'), results: { applied: 'strategy-alt', results: [ { id: 'strategy-alt', name: 'Alt', status: 'done', ok: 3, total: 3, ratio: 1 } ] } }; }
	};
	F.setReplies(replies);
	const v = F.useModule('view.overview', 'view/zaprett/overview.js');
	const node = v.render(await v.load());
	const boxes = [];
	(function walk(n) { if (n.tagName == 'input' && n.attrs.type == 'checkbox') boxes.push(n); (n.childNodes ?? []).forEach(c => c.childNodes && walk(c)); })(node);
	boxes.forEach(b => { b.checked = (b.attrs.value == 'youtube' || b.attrs.value == 'discord'); });
	const selectable = boxes.filter(b => b.attrs.disabled == null);
	const p = v.handleWizard(selectable);
	await F.settle(40);
	await p;
	const methods = F.state.calls.map(c => c.method);
	check('wizard: apply, start, probe in order', methods.indexOf('wizard_apply') < methods.indexOf('service') && methods.indexOf('service') < methods.indexOf('probe_start'), methods);
	const svc = F.state.calls.filter(c => c.method == 'service')[0];
	check('wizard: service start when stopped', svc?.params.action == 'start', svc);
	const pr = F.state.calls.filter(c => c.method == 'probe_start')[0];
	check('wizard: probe of the checkable selected services', JSON.stringify(pr?.params.services) == '["youtube","discord"]', pr);
	check('wizard: low_memory explained in the lists step', findText(v.wizardProgress, 'too large for this router'), null);

	if (probeOk) {
		check('wizard: success when all opened', findText(v.wizardProgress, 'Done: the selected services open through zaprett.'), F.text(v.wizardProgress).slice(-300));
	}
	else {
		check('wizard: offer selection when a service failed', findText(v.wizardProgress, 'These services did not open: Discord.') && findText(v.wizardProgress, 'Find a working strategy'), F.text(v.wizardProgress).slice(-300));
		F.state.calls = [];
		const t = v.handleWizardTest();
		await F.settle(40);
		await t;
		const ts = F.state.calls.filter(c => c.method == 'test_start')[0];
		check('wizard: selection asks --apply-if-better quick', ts?.params.apply_if_better === true && ts?.params.quick === true, ts);
		const tst = F.state.calls.filter(c => c.method == 'test_status')[0];
		check('wizard: result read with brief', tst?.params.brief === true, tst);
		check('wizard: applied strategy reported and rechecked', findText(v.wizardProgress, 'Strategy "Alt" is selected and applied.') && F.state.calls.some(c => c.method == 'probe_start'), F.text(v.wizardProgress).slice(-400));
		check('wizard: final success after recheck', findText(v.wizardProgress, 'Done: the selected services open through zaprett.'), F.text(v.wizardProgress).slice(-300));
	}

	check('wizard: no string reached innerHTML', F.state.innerHTML.length == 0, F.state.innerHTML);
	F.state.polls = [];
}

/* contract v1.7 §16.4: choice of the list set of a service with variants */
async function wizardVariants() {
	F.state.calls = []; F.state.innerHTML = [];
	const vpresets = JSON.parse(JSON.stringify(presets));
	vpresets.services[1] = Object.assign({}, vpresets.services[1], { name_en: 'Discord EN', enabled: false, enabled_variant: 'full', variants: [
		{ id: 'full', name: 'Расширенный', name_en: 'Extended ' + XSS, description: 'Больше', description_en: 'More domains ' + XSS, lists: [ 'zaprett-discord-full' ], ipsets: [], tier: 'light', enabled: true, available: true },
		{ id: 'voice', name: 'Голос', name_en: 'Voice EN', description_en: 'Voice networks', lists: [ 'zaprett-discord' ], ipsets: [ 'zaprett-discord-voice' ], tier: 'full', enabled: false, available: true },
		{ id: 'gone', name_en: 'Gone EN', lists: [ 'x' ], ipsets: [], tier: 'light', enabled: false, available: false },
		{ id: '../bad', name_en: 'Bad EN', lists: [ 'y' ], ipsets: [], tier: 'light' }
	] });
	F.setReplies({
		page: { ok: true, status: status({ enabled: false, running: false }), job: { ok: true, job: null }, presets: vpresets, monitor, probe: { ok: true, probe: null } },
		wizard_apply: p => ({ ok: true, services: [ 'youtube', 'discord' ], variants: { youtube: null, discord: (p.services[1] || '').split(':')[1] || null },
			skipped: [], sources: [], reloaded: false, warnings: [] }),
		service: { ok: true },
		probe_start: { ok: true, job: { id: 'p', name: 'probe' } },
		job_status: { ok: true, job: job('probe', 'done') },
		probe_status: { ok: true, probe: probeResult(3, 3) },
		status: status()
	});
	const v = F.useModule('view.overview', 'view/zaprett/overview.js');
	const node = v.render(await v.load());
	const inputs = [];
	(function walk(n) { if (n.tagName == 'input') inputs.push(n); (n.childNodes ?? []).forEach(c => c.childNodes && walk(c)); })(node);
	const radios = inputs.filter(i => i.attrs.type == 'radio');
	const boxes = inputs.filter(i => i.attrs.type == 'checkbox');
	check('variants: radios only for the service with variants, bad id dropped', radios.length == 4 && radios.every(r => r.attrs.name == 'zaprett-var-discord') &&
		radios.map(r => r.attrs.value).join(',') == ',full,voice,gone', radios.map(r => [ r.attrs.name, r.attrs.value ]));
	check('variants: current variant from enabled_variant', radios.filter(r => r.checked).map(r => r.attrs.value).join(',') == 'full', radios.map(r => r.checked));
	check('variants: unavailable variant disabled', radios[3].attrs.disabled != null && radios[2].attrs.disabled == null, radios.map(r => r.attrs.disabled));
	check('variants: service with the variant on counts as enabled', boxes.filter(b => b.attrs.value == 'discord')[0]?.checked === true &&
		boxes.filter(b => b.attrs.value == 'youtube')[0]?.checked === false, boxes.map(b => [ b.attrs.value, b.checked ]));
	check('variants: names, descriptions and load shown', findText(node, 'List set:') && findText(node, 'Main') && findText(node, 'Extended ' + XSS) &&
		findText(node, 'More domains ' + XSS) && findText(node, 'Voice EN') && findText(node, 'Load: light, suits any router.') &&
		findText(node, 'Load: heavy, needs at least 200 MiB of memory: not recommended for this router.') && !findText(node, 'Bad EN'), F.text(node).slice(0, 2000));
	check('variants: router names and descriptions never reach innerHTML', F.state.innerHTML.length == 0, F.state.innerHTML);

	/* voice chosen: "discord:voice" is sent, the check gets plain ids */
	radios.forEach(r => { r.checked = (r.attrs.value == 'voice'); });
	boxes.forEach(b => { b.checked = (b.attrs.value == 'youtube' || b.attrs.value == 'discord'); });
	let p = v.handleWizard(boxes.filter(b => b.attrs.disabled == null));
	await F.settle(40);
	await p;
	const wa = F.state.calls.filter(c => c.method == 'wizard_apply')[0];
	check('variants: wizard gets id:variant', JSON.stringify(wa?.params.services) == '["youtube","discord:voice"]', wa);
	const pr = F.state.calls.filter(c => c.method == 'probe_start')[0];
	check('variants: the check gets plain service ids', JSON.stringify(pr?.params.services) == '["youtube","discord"]', pr);
	check('variants: chosen set reported', findText(v.wizardProgress, 'Other list sets are chosen: Discord EN — Voice EN.'), F.text(v.wizardProgress).slice(0, 600));

	/* main set chosen: the plain id, nothing reported */
	F.state.calls = [];
	radios.forEach(r => { r.checked = (r.attrs.value == ''); });
	p = v.handleWizard(boxes.filter(b => b.attrs.disabled == null));
	await F.settle(40);
	await p;
	const wa2 = F.state.calls.filter(c => c.method == 'wizard_apply')[0];
	check('variants: main set sends the plain id', JSON.stringify(wa2?.params.services) == '["youtube","discord"]', wa2);
	check('variants: nothing reported for the main set', !findText(v.wizardProgress, 'Other list sets are chosen'), F.text(v.wizardProgress).slice(0, 600));
	check('variants: wizard puts no string into innerHTML', F.state.innerHTML.length == 0, F.state.innerHTML);
	F.state.polls = [];
}

/* a service with a subscription: the check waits for the download task */
async function wizardSources() {
	F.state.calls = []; F.state.innerHTML = [];
	const jobs = [ job('sources-update', 'running'), job('sources-update', 'done'), job('probe', 'running'), job('probe', 'done') ];
	F.setReplies({
		page: { ok: true, status: status({ enabled: false, running: false, warnings: [ 'no_active_lists' ] }), job: { ok: true, job: null }, presets, monitor, probe: { ok: true, probe: null } },
		wizard_apply: { ok: true, services: [ 'youtube' ], skipped: [], sources: [ 'refilter' ], reloaded: false, warnings: [], job: { id: 's', name: 'sources-update' } },
		service: { ok: true },
		probe_start: { ok: true, job: { id: 'p', name: 'probe' } },
		job_status: () => ({ ok: true, job: jobs.length > 1 ? jobs.shift() : jobs[0] }),
		probe_status: { ok: true, probe: probeResult(3, 3) },
		status: status()
	});
	const v = F.useModule('view.overview', 'view/zaprett/overview.js');
	v.render(await v.load());
	const box = F.E('input', { 'type': 'checkbox', 'value': 'youtube', 'checked': '' });
	const p = v.handleWizard([ box ]);
	await F.settle(40);
	await p;
	const methods = F.state.calls.map(c => c.method);
	const secondJob = methods.indexOf('job_status', methods.indexOf('job_status') + 1);
	check('wizard: probe waits for the subscription download', secondJob > 0 && methods.indexOf('probe_start') > secondJob, methods);
	check('wizard with subscriptions: finished', F.text(v.wizardProgress).indexOf('Done: the selected services open through zaprett.') >= 0, F.text(v.wizardProgress).slice(-300));
	F.state.polls = [];
}

/* repository: one job poll even when two commands start jobs */
async function repo() {
	F.state.calls = []; F.state.innerHTML = []; F.state.polls = [];
	F.setReplies({
		repo_list: { ok: true, fetched_at: 1758100000, url: 'https://example.com/index.json', items: [
			{ id: 'strategy-alt', type: 'nfqws', name: 'Alt', name_en: 'Alt EN', description: 'Описание ' + XSS, installed: false, supported: true, version: '1.0' } ] },
		job_status: { ok: true, job: job('repo-install', 'running') },
		repo_install: { ok: true, job: { id: 'r', name: 'repo-install' } }
	});
	const v = F.useModule('view.repo', 'view/zaprett/repo.js');
	const node = v.render(await v.load());
	check('repo: localized name and data-title', findText(node, 'Alt EN') && hasAttr(node, 'data-title', 'Author'), null);
	await v.handleInstall([ 'strategy-alt' ]);
	await v.handleInstall([ 'strategy-alt' ]);
	check('repo: a second job does not add a second poll', F.state.polls.length == 1, F.state.polls.length);
	check('repo: no string reached innerHTML', F.state.innerHTML.length == 0, F.state.innerHTML);
	F.state.polls = [];
}

async function strategies() {
	F.state.calls = []; F.state.innerHTML = []; F.state.modals = [];
	F.setReplies({
		page: { ok: true, status: status(), job: { ok: true, job: null },
			items: { ok: true, items: [ { id: 'strategy-general', type: 'nfqws', name: 'General', name_en: 'General EN', source: 'bundle' }, { id: 'list-a', type: 'list', name: 'List A' }, { id: 's2', type: 'nfqws2', name: 'N2' } ] },
			test: { ok: true, job: job('test', 'done'), results: { finished: 1758100000, applied: 'strategy-general', baseline: { ok: 0, total: 3 },
				mode: 'exclusive', mode_reason: 'nft_rejected',
				results: [ { id: 'strategy-general', name: 'General', status: 'done', ok: 3, total: 3, ratio: 1, avg_ms: 400 }, { id: 'x', name: 'X', status: 'done', ok: 1, total: 3, ratio: 0.33 } ] } } },
		test_status: p => p.brief ? { ok: false, error: 'usage' } : { ok: true, job: job('test', 'done'), results: { results: [ { id: 'x', targets: [ { url: 'https://a/' + XSS, ok: false, error: 'tls_cert' } ] } ] } }
	});
	const v = F.useModule('view.strategies', 'view/zaprett/strategies.js');
	const data = await v.load();
	const node = v.render(data);
	check('strategies: one page call', F.state.calls.map(c => c.method).join(',') == 'page', F.state.calls.map(c => c.method));
	check('strategies: only engine strategies listed', findText(node, 'General EN') && !findText(node, 'List A') && !findText(node, 'N2'), null);
	check('strategies: auto-applied result announced', findText(node, 'opened more sites than the previous one and is applied'), null);
	check('strategies: data-title on result cells', hasAttr(node, 'data-title', 'Sites opened'), null);
	/* contract v1.6 §17: the mode of the selection and the reason of the fallback */
	check('strategies: exclusive mode and its reason shown', findText(node, 'The engine was stopped for the time of the check: the firewall did not accept the check rules.')
		&& !findText(node, 'next to the working bypass'), null);
	v.renderResults({ job: job('test', 'done'), results: { mode: 'isolated', mode_reason: null, baseline: { ok: 0, total: 3 }, results: [] } });
	check('strategies: isolated mode shown', findText(v.resultsNode, 'next to the working bypass') && !findText(v.resultsNode, 'was stopped for the time'), F.text(v.resultsNode));
	/* details on demand */
	await v.showTargets({ id: 'x', name: 'X' });
	const modal = F.state.modals[F.state.modals.length - 1];
	check('strategies: details loaded by the button', modal && findText(modal.node, 'wrong certificate'), modal && F.text(modal.node));
	check('strategies: no string reached innerHTML', F.state.innerHTML.length == 0, F.state.innerHTML);

	/* watchTest: brief with fallback to full on usage */
	F.state.calls = [];
	F.setReplies({ test_status: p => p.brief ? { ok: false, error: 'usage' } : { ok: true, job: job('test', 'done'), results: { results: [] } } });
	v.watchTest();
	await F.settle(3);
	check('strategies: poll falls back to full status on old service', F.state.calls.map(c => JSON.stringify(c.params)).join(';') == '{"brief":true};{}', F.state.calls.map(c => c.params));
	F.state.polls = [];
}

async function diagnostics() {
	F.state.calls = []; F.state.innerHTML = []; F.state.modals = [];
	F.setReplies({
		page: { ok: true, status: status(), job: { ok: true, job: job('test', 'running') }, monitor },
		dns_status: { ok: false, error: 'usage', message: 'Неверные аргументы' },
		diagnose_status: { ok: false, error: 'usage', message: 'Неверные аргументы' },
		diag: { ok: true, text: 'report ' + XSS, full: false },
		log: p => ({ ok: true, lines: Array.from({ length: p.tail }, (_, i) => 'line ' + i + ' ' + XSS) }),
		job_log: { ok: true, log: 'job log ' + XSS },
		job_status: { ok: true, job: job('test', 'running') }
	});
	const v = F.useModule('view.diagnostics', 'view/zaprett/diagnostics.js');
	const data = await v.load();
	const methods = F.state.calls.map(c => c.method).sort().join(',');
	check('diagnostics: page + diag + log + job_log (+ v1.4 fallbacks)', methods == 'diag,diagnose_status,dns_status,job_log,log,page', methods);
	const logCall = F.state.calls.filter(c => c.method == 'log')[0];
	check('diagnostics: engine log 200 lines', logCall.params.tail == 200, logCall);
	const node = v.render(data);
	check('diagnostics: engine log shown', findText(node, 'line 199 '), null);
	check('diagnostics: debug switch shown off', findText(node, 'Turn on the detailed log'), null);
	check('diagnostics: monitor shown', findText(node, 'Availability monitor'), null);
	check('diagnostics v1.3: diagnosis block says update', findText(node, 'cannot find out how the provider blocks yet') && !findText(node, 'Find out how the provider blocks'), null);
	F.state.calls = [];
	await F.settle(1);
	check('diagnostics: one job_status per tick', F.state.calls.map(c => c.method).join(',') == 'job_status', F.state.calls.map(c => c.method));
	check('diagnostics: no string reached innerHTML', F.state.innerHTML.length == 0, F.state.innerHTML);
	/* debug on: confirm dialog, then uci set + apply */
	const pr = v.handleDebug(true);
	const modal = F.state.modals[F.state.modals.length - 1];
	const buttons = [];
	(function walk(n) { if (n.tagName == 'button') buttons.push(n); (n.childNodes ?? []).forEach(c => c.childNodes && walk(c)); })(modal.node);
	buttons[buttons.length - 1].click();
	await pr;
	check('diagnostics: debug saved and applied', F.uciData.zaprett.main.debug == '1' && F.state.applied === true, F.uciData.zaprett.main);
	F.state.polls = [];

	/* old service without "log" */
	F.setReplies({
		page: { ok: false, error: 'usage' }, status: status(), job_status: { ok: true, job: null }, monitor_status: { ok: false, error: 'usage' },
		diag: { ok: true, text: 'r', full: false }, log: { ok: false, error: 'usage' }
	});
	const v2 = F.useModule('view.diagnostics', 'view/zaprett/diagnostics.js');
	const n2 = v2.render(await v2.load());
	check('diagnostics: old service log notice', findText(n2, 'logread -e zaprett') && !findText(n2, 'Availability monitor'), null);
	F.state.polls = [];
}

/* --- contract v1.4 (ARCHITECTURE §15) --- */

/* Clicks a button of the last modal: 0 = first (Cancel), -1 = last (confirm). */
function clickModal(index) {
	const modal = F.state.modals[F.state.modals.length - 1];
	const buttons = [];
	(function walk(n) { if (n.tagName == 'button') buttons.push(n); (n.childNodes ?? []).forEach(c => c.childNodes && walk(c)); })(modal.node);
	buttons[(index < 0) ? buttons.length + index : index].click();
}

const diagResult = verdictOfFirst => ({ started: 1758100000, finished: 1758100030, engine_running: true, targets: [
	{ url: 'https://discord.com/', host: 'discord.com' + XSS, verdict: verdictOfFirst, dns: { system: [ '10.0.0.1' + XSS ], doh: [ '162.159.1.1' ], spoofed: verdictOfFirst == 'dns_spoof' }, detail: 'detail ' + XSS },
	{ url: 'https://www.youtube.com/', host: 'www.youtube.com', verdict: 'throttle', dns: { system: [ '1.1.1.1' ], doh: [ '1.1.1.1' ], spoofed: false }, detail: null },
	{ url: 'https://x.example/', host: 'x.example', verdict: 'weird' + XSS, dns: null, detail: null }
], summary: { verdict: verdictOfFirst, counts: { [verdictOfFirst]: 1, throttle: 1, ['weird' + XSS]: 1 } } });

async function overviewV14() {
	F.state.calls = []; F.state.innerHTML = []; F.state.modals = []; F.state.notes = [];
	const plain = { encrypted: false, provider: null }, on = { encrypted: true, provider: 'https-dns-proxy' };
	let setups = 0, dnsOn = false, jobState = 'running';
	const st = () => status({ dns: dnsOn ? on : plain, flow_offload: { fw4: false, mode: 'own', own: false },
		warnings: [ 'flowtable_failed', 'game_filter_no_ipsets', 'dns_plain' ] });
	F.setReplies({
		page: { ok: true, status: st(), job: { ok: true, job: null }, presets, monitor, probe: { ok: true, probe: null }, dns: { ok: true, dns: plain } },
		dns_setup: () => { setups++; return { ok: true, job: { id: 'd', name: 'dns-setup' } }; },
		job_status: () => { const j = job('dns-setup', jobState); jobState = 'done'; return { ok: true, job: j }; },
		dns_status: () => { dnsOn = true; return { ok: true, dns: on }; },
		status: () => st()
	});
	const v = F.useModule('view.overview', 'view/zaprett/overview.js');
	const node = v.render(await v.load());
	check('v1.4 overview: DNS card with the button', findText(v.dnsNode, 'Encrypted DNS') && findText(v.dnsNode, 'Turn on encrypted DNS') && findText(v.dnsNode, 'Off'), F.text(v.dnsNode));
	check('v1.4 overview: new warnings explained', findText(node, 'The own acceleration table did not start') && findText(node, 'The game filter is not working: no IP network list'), null);
	const w = F.text(v.warningsNode);
	check('v1.4 overview: dns_plain is informational', w.indexOf('For information') >= 0 && w.indexOf('DNS requests go without encryption') > w.indexOf('For information') &&
		w.indexOf('The own acceleration table did not start') < w.indexOf('For information'), w.slice(0, 400));
	check('v1.4 overview: dns_plain offers the DNS button', findText(v.warningsNode, 'Turn on encrypted DNS'), null);
	check('v1.4 overview: inactive own table explained', findText(node, 'zaprett will add its own acceleration table once acceleration is turned on in the firewall'), null);

	/* cancel in the confirmation dialog: nothing is installed */
	let p = v.handleDns();
	clickModal(0);
	await F.settle(10);
	await p;
	check('v1.4 overview: cancel installs nothing', setups == 0 && !F.state.calls.some(c => c.method == 'dns_setup'), F.state.calls.map(c => c.method));

	p = v.handleDns();
	clickModal(-1);
	await F.settle(10);
	await p;
	check('v1.4 overview: dns_setup after confirmation', setups == 1, setups);
	check('v1.4 overview: DNS result shown', findText(v.dnsNode, 'Encrypted DNS is on.'), F.text(v.dnsNode));
	check('v1.4 overview: card shows the provider after setup', findText(v.dnsNode, 'On: https-dns-proxy') && !findText(v.dnsNode, 'Turn on encrypted DNS'), F.text(v.dnsNode));
	check('v1.4 overview: no string reached innerHTML', F.state.innerHTML.length == 0, F.state.innerHTML);
	F.state.polls = [];
}

/* v1.3 service: no dns in status or page -> no DNS card, no extra calls */
async function overviewNoDns() {
	F.state.calls = []; F.state.innerHTML = [];
	F.setReplies({ page: { ok: true, status: status(), job: { ok: true, job: null }, presets, monitor, probe: { ok: true, probe: null } } });
	const v = F.useModule('view.overview', 'view/zaprett/overview.js');
	const node = v.render(await v.load());
	check('v1.3 overview: no DNS card and no dns call', !findText(node, 'Encrypted DNS') && F.state.calls.map(c => c.method).join(',') == 'page', F.state.calls.map(c => c.method));
	F.state.polls = [];
}

/* quick setup: a service did not open -> diagnosis offered and shown */
async function wizardDiagnose() {
	F.state.calls = []; F.state.innerHTML = []; F.state.modals = [];
	let diagState = 'running';
	F.setReplies({
		page: { ok: true, status: status({ dns: { encrypted: false, provider: null } }), job: { ok: true, job: null }, presets, monitor, probe: { ok: true, probe: null } },
		diagnose_start: { ok: true, job: { id: 'g', name: 'diagnose' } },
		job_status: () => { const j = job('diagnose', diagState); diagState = 'done'; return { ok: true, job: j }; },
		diagnose_status: { ok: true, diagnose: diagResult('dns_spoof') },
		status: status({ dns: { encrypted: false, provider: null } })
	});
	const v = F.useModule('view.overview', 'view/zaprett/overview.js');
	v.render(await v.load());
	v.wizardResultNode = F.E('div', {});
	v.wizardDone(probeResult(3, 0));
	check('wizard: diagnosis offered when a service failed', findText(v.wizardResultNode, 'Find out how the provider blocks') && findText(v.wizardResultNode, 'Find a working strategy'), F.text(v.wizardResultNode));
	F.state.calls = [];
	const p = v.handleWizardDiagnose([ 'discord' ]);
	await F.settle(10);
	await p;
	const ds = F.state.calls.filter(c => c.method == 'diagnose_start')[0];
	check('wizard: diagnosis of the failed services', JSON.stringify(ds?.params.services) == '["discord"]', ds);
	check('wizard: diagnosis result with advice', findText(v.wizardDiagNode, 'Conclusion: DNS substitution.') && findText(v.wizardDiagNode, 'Turn on encrypted DNS') &&
		findText(v.wizardDiagNode, 'the router got a substituted address') && findText(v.wizardDiagNode, 'Slowed down'), F.text(v.wizardDiagNode).slice(0, 500));
	check('wizard: diagnosis puts no string into innerHTML', F.state.innerHTML.length == 0, F.state.innerHTML);

	/* older service: "usage" -> a notice instead of an error */
	F.setReplies({ diagnose_start: { ok: false, error: 'usage', message: 'Неверные аргументы' } });
	await v.handleWizardDiagnose([ 'discord' ]);
	check('wizard: old service gets an update notice', findText(v.wizardDiagNode, 'cannot find out how the provider blocks yet'), F.text(v.wizardDiagNode));
	F.state.polls = [];
}

async function diagnosticsV14() {
	F.state.calls = []; F.state.innerHTML = []; F.state.modals = [];
	let diagState = 'running', started = null;
	F.setReplies({
		page: { ok: true, status: status(), job: { ok: true, job: null }, monitor, dns: { ok: true, dns: { encrypted: false, provider: null } },
			diagnose: { ok: true, diagnose: diagResult('dns_spoof') } },
		diag: { ok: true, text: 'r', full: false },
		log: { ok: true, lines: [] },
		diagnose_start: p => { started = p; diagState = 'running'; return { ok: true, job: { id: 'g', name: 'diagnose' } }; },
		job_status: () => { const j = job('diagnose', diagState); diagState = 'done'; return { ok: true, job: j }; },
		job_log: { ok: true, log: '' },
		diagnose_status: { ok: true, diagnose: diagResult('ok') },
		dns_setup: { ok: true, changed: false },
		dns_status: { ok: true, dns: { encrypted: true, provider: 'https-dns-proxy' } }
	});
	const v = F.useModule('view.diagnostics', 'view/zaprett/diagnostics.js');
	const data = await v.load();
	check('v1.4 diagnostics: page carries dns and diagnose', F.state.calls.map(c => c.method).sort().join(',') == 'diag,log,page', F.state.calls.map(c => c.method));
	const node = v.render(data);
	check('v1.4 diagnostics: result with the conclusion', findText(node, 'How the provider blocks') && findText(node, 'Conclusion: DNS substitution.') &&
		findText(node, 'Turn on encrypted DNS') && findText(node, 'the router got a substituted address'), F.text(v.diagnoseNode).slice(0, 500));
	check('v1.4 diagnostics: unknown verdict shown as text', findText(v.diagnoseNode, 'weird' + XSS), null);
	check('v1.4 diagnostics: no string reached innerHTML', F.state.innerHTML.length == 0, F.state.innerHTML);

	F.state.calls = [];
	const p = v.handleDiagnose();
	await F.settle(10);
	await p;
	check('v1.4 diagnostics: diagnosis of all active services', started != null && started.services === undefined, started);
	check('v1.4 diagnostics: new result shown', findText(v.diagnoseNode, 'Conclusion: the sites open.') && !findText(v.diagnoseNode, 'Conclusion: DNS substitution.'), F.text(v.diagnoseNode).slice(0, 400));
	F.state.polls = [];

	/* encrypted DNS was already on: no job, the result says so */
	v.diagnose = { ok: true, diagnose: diagResult('dns_spoof') };
	v.renderDiagnose();
	const d = v.handleDns();
	clickModal(-1);
	await d;
	check('v1.4 diagnostics: DNS already on', findText(v.diagnoseNode, 'Encrypted DNS was already on') && findText(v.diagnoseNode, 'Encrypted DNS is already on the router'), F.text(v.diagnoseNode).slice(0, 600));
	check('v1.4 diagnostics: no string reached innerHTML after the actions', F.state.innerHTML.length == 0, F.state.innerHTML);
	F.state.polls = [];
}

/* a diagnosis already running when the page opens is followed, not restarted */
async function diagnosticsRunning() {
	F.state.calls = []; F.state.innerHTML = [];
	let n = 0;
	F.setReplies({
		page: { ok: true, status: status(), job: { ok: true, job: job('diagnose', 'running') }, monitor, dns: { ok: true, dns: { encrypted: true, provider: 'stubby' } },
			diagnose: { ok: true, diagnose: null } },
		diag: { ok: true, text: 'r', full: false },
		log: { ok: true, lines: [] },
		job_log: { ok: true, log: '' },
		job_status: () => ({ ok: true, job: job('diagnose', (n++ < 1) ? 'running' : 'done') }),
		diagnose_status: { ok: true, diagnose: diagResult('ip_block') }
	});
	const v = F.useModule('view.diagnostics', 'view/zaprett/diagnostics.js');
	v.render(await v.load());
	await F.settle(10);
	check('v1.4 diagnostics: running diagnosis followed', !F.state.calls.some(c => c.method == 'diagnose_start') && findText(v.diagnoseNode, 'Conclusion: Blocked by IP address.') &&
		findText(v.diagnoseNode, 'need a VPN'), F.text(v.diagnoseNode).slice(0, 400));
	check('v1.4 diagnostics: no DNS button when DNS is on', !findText(v.diagnoseNode, 'Turn on encrypted DNS'), null);
	F.state.polls = [];
}

/* validation of the game filter ports (settings.js, §15.2) */
function settingsPorts() {
	const src = require('fs').readFileSync(require('path').join(__dirname, '..', 'htdocs/luci-static/resources/view/zaprett/settings.js'), 'utf8');
	const start = src.indexOf('const PORT_RANGE_RE'), end = src.indexOf('function markValue');
	const validatePorts = new Function('_', src.slice(start, end) + '\nreturn validatePorts;')(s => s);
	const good = [ '', '1024-65535', '80', '3478,50000-50100', '1-65535', '443,80,8080-8090' ];
	const bad = [ '0', '65536', '100-50', '1024 - 2000', '1024, 2000', 'abc', '01', '1,,2', ',80', '80-', '1-65536', '-80' ];
	check('settings: valid ports accepted', good.every(v => validatePorts('main', v) === true), good.map(v => [ v, validatePorts('main', v) ]));
	check('settings: invalid ports rejected', bad.every(v => typeof(validatePorts('main', v)) == 'string'), bad.map(v => [ v, validatePorts('main', v) ]));
}

async function commonUnits() {
	const zc = F.useModule('zaprett.common', 'zaprett/common.js');
	F.state.lang = 'ru';
	zc.russianUI = null;
	check('localized: ru keeps the original', zc.localized({ name: 'Имя', name_en: 'Name' }, 'name') == 'Имя', null);
	F.state.lang = 'en';
	zc.russianUI = null;
	check('localized: en takes name_en', zc.localized({ name: 'Имя', name_en: 'Name' }, 'name') == 'Name', null);
	check('localized: en without name_en keeps the original', zc.localized({ name: 'Имя' }, 'name') == 'Имя', null);
	check('localized: empty name_en ignored', zc.localized({ name: 'Имя', name_en: '' }, 'name') == 'Имя', null);
	check('info warnings', zc.isInfoWarning('test_running') && zc.isInfoWarning({ code: 'empty_profile_removed' }) && !zc.isInfoWarning('low_memory'), null);
	const w = zc.warningInfo('monitor_degraded', {});
	check('warning text for monitor_degraded', w.action == 'strategies' && /stopped opening/.test(w.title), w);
	/* negative control: an unknown code still gets the generic text */
	check('unknown warning -> generic', /Warning: nope/.test(zc.warningInfo('nope', {}).title), null);
	/* closed list of probe error codes (ARCHITECTURE §14.3): each has its own text */
	const codes = [ 'timeout', 'reset', 'tls_cert', 'tls_error', 'connect_failed', 'http_error', 'too_small', 'local_error', 'failed' ];
	const texts = codes.map(c => zc.targetErrorText({ error: c }));
	check('every probe error code has a text', texts.every((t, i) => t && t != codes[i]) && new Set(texts).size == codes.length, texts);
	check('unknown error code is shown as is', zc.targetErrorText({ error: 'weird' }) == 'weird', null);
	const zh = F.useModule('zaprett.health', 'zaprett/health.js');
	check('checkState rule ok*2<total', zh.checkState(1, 3) == 'fail' && zh.checkState(2, 4) == 'ok' && zh.checkState(0, 0) == 'none', null);
	/* contract v1.4: warnings (§15.5), verdicts (§15.4), job names */
	check('v1.4 warnings have their own texts', [ 'flowtable_failed', 'game_filter_no_ipsets', 'dns_plain' ].every(c => !/Warning:/.test(zc.warningInfo(c, {}).title)), null);
	check('dns_plain is informational, the others are not', zc.isInfoWarning('dns_plain') && !zc.isInfoWarning('flowtable_failed') && !zc.isInfoWarning('game_filter_no_ipsets'), null);
	const verdicts = [ 'ok', 'dns_spoof', 'ip_block', 'tls_block', 'throttle', 'http_block', 'unknown' ];
	const labels = verdicts.map(x => zh.verdictInfo(x)[0]), advice = verdicts.map(x => zh.verdictInfo(x)[2]);
	check('every verdict has its own label and advice', new Set(labels).size == verdicts.length && new Set(advice).size == verdicts.length && labels.every((t, i) => t != verdicts[i]), labels);
	check('unknown verdict is shown as is', zh.verdictInfo('weird')[0] == 'weird', null);
	check('job names of v1.4 have labels', zc.jobLabel('dns-setup') != 'dns-setup' && zc.jobLabel('diagnose') != 'diagnose', null);
}

/* A scenario that waits on a promise nobody resolves leaves the event loop
 * empty, and node would exit with code 0 without the RESULT line. */
let finished = false;

process.on('exit', () => {
	if (!finished) {
		console.log('FAIL scenarios did not finish (a promise never settled)');
		console.log('\nRESULT passed=%d failed=%d', passed, failed + 1);
		process.exitCode = 1;
	}
});

(async () => {
	F.state.lang = 'en';
	try {
		await commonUnits();
		await overviewFull();
		await overviewOldBackend();
		await wizardFlow(true);
		await wizardFlow(false);
		await wizardSources();
		await wizardVariants();
		await repo();
		await strategies();
		await diagnostics();
		await overviewV14();
		await overviewNoDns();
		await wizardDiagnose();
		await diagnosticsV14();
		await diagnosticsRunning();
		settingsPorts();
	}
	catch (e) {
		failed++;
		console.log('CRASH', e.stack);
	}
	finished = true;
	console.log('\nRESULT passed=%d failed=%d', passed, failed);
	process.exit(failed ? 1 : 0);
})();
