// SPDX-License-Identifier: MIT
// Blocks of luci-app-zaprett that show whether the bypass works: results of
// the live check of services ("probe"), the availability monitor with its
// history, encrypted DNS, the diagnosis of how the provider blocks
// ("diagnose"), and a small list of steps for multi-step actions.
//
// Security note: as in common.js, data from the router is passed to E() only
// inside arrays (zc.txt()) or as attribute values.

'use strict';
'require baseclass';
'require ui';
'require dom';
'require zaprett.common as zc';

const txt = zc.txt;

/* The monitor keeps the last 48 checks (ARCHITECTURE §14.3). */
const HISTORY_LENGTH = 48;

const COLORS = { ok: '#2e7d32', fail: '#c62828', none: '#9e9e9e' };

/* A check fails when less than half of its targets opened; a check without
 * targets does not count (ARCHITECTURE §14.1). */
function checkState(ok, total) {
	if (!(+total > 0))
		return 'none';

	return (+ok * 2 < +total) ? 'fail' : 'ok';
}

function swatch(state, title) {
	return E('span', {
		'title': title,
		'style': 'display:inline-block;width:.7em;height:1.5em;border-radius:2px;background:%s'.format(COLORS[state])
	});
}

return baseclass.extend({
	checkState: checkState,

	/* Name of a service in the interface language: presets carry name_en. */
	serviceName(id, fallback, presets) {
		const list = Array.isArray(presets?.services) ? presets.services : [];
		const preset = list.filter(s => s.id == id)[0];

		return (preset ? zc.localized(preset, 'name') : null) || fallback || id;
	},

	/* Services of a probe result that did not open. */
	failedServices(probe) {
		const list = Array.isArray(probe?.services) ? probe.services : [];

		return list.filter(s => checkState(s.ok, s.total) == 'fail');
	},

	/* One card: "YouTube ✓ 3/3, 420 ms" or "Discord ✗ 0/3 — the connection was reset". */
	probeCard(svc, presets) {
		const ok = +svc.ok || 0, total = +svc.total || 0;
		const state = !total ? 'none' : ((ok >= total) ? 'ok' : ((ok > 0) ? 'partial' : 'fail'));
		const mark = { ok: '✓', partial: '!', fail: '✗', none: '?' }[state];
		const style = { ok: 'success', partial: 'warning', fail: 'danger', none: 'notice' }[state];
		const failed = (Array.isArray(svc.targets) ? svc.targets : []).filter(t => !t.ok)[0];
		const result = total ? '%s %d/%d'.format(mark, ok, total) : '%s %s'.format(mark, _('nothing to check'));
		const time = (+svc.avg_ms > 0) ? ', ' + _('%d ms').format(+svc.avg_ms) : '';

		return E('div', { 'class': 'alert-message %s'.format(style), 'style': 'margin:0;min-width:12em;flex:1 1 12em' }, [
			E('strong', {}, txt(this.serviceName(svc.id, svc.name, presets))),
			E('div', { 'style': 'font-size:120%' }, txt(result + time)),
			(failed && state != 'ok') ? E('div', { 'class': 'cbi-value-description' }, txt(zc.targetErrorText(failed))) : ''
		]);
	},

	probeCards(probe, presets) {
		const list = Array.isArray(probe?.services) ? probe.services : [];

		return E('div', { 'style': 'display:flex;flex-wrap:wrap;gap:.5em;margin:.5em 0' }, list.map(s => this.probeCard(s, presets)));
	},

	/* Line under the cards: when and how the check was made. */
	probeSummary(probe) {
		const parts = [ _('Checked: %s').format(zc.formatTime(probe.finished || probe.started)) ];

		if (probe.strategy)
			parts.push(_('strategy: %s').format(L.isObject(probe.strategy) ? (probe.strategy.name || probe.strategy.id) : probe.strategy));

		return E('p', { 'class': 'cbi-value-description' }, txt(parts.join('; ')));
	},

	showProbeDetails(probe, presets) {
		const list = Array.isArray(probe?.services) ? probe.services : [];

		ui.showModal(txt(_('Check details')), [
			...list.map(s => E('div', {}, [
				E('h5', {}, txt('%s: %d/%d'.format(this.serviceName(s.id, s.name, presets), +s.ok || 0, +s.total || 0))),
				zc.targetsTable(s.targets)
			])),
			E('div', { 'class': 'right' }, [ zc.button(_('Close'), ui.hideModal, 'neutral') ])
		], 'cbi-modal');
	},

	monitorStateInfo(state) {
		return {
			ok: [ _('Sites open'), 'success', _('The last check was successful.') ],
			degraded: [ _('Sites stopped opening'), 'danger', _('Several checks in a row failed: the provider may have changed the blocking. Run automatic strategy selection.') ],
			repairing: [ _('Repairing'), 'notice', _('zaprett is looking for a working strategy automatically (quick selection).') ]
		}[state] ?? [ _('No data yet'), 'notice', _('The monitor has not made a check yet, or there was nothing to check.') ];
	},

	/* History of the monitor as a strip of coloured cells, oldest first. */
	historyStrip(history) {
		const list = (Array.isArray(history) ? history : []).slice(-HISTORY_LENGTH);
		const failed = list.filter(h => checkState(h.ok, h.total) == 'fail').length;
		const counted = list.filter(h => checkState(h.ok, h.total) != 'none').length;
		const cells = list.map(h => swatch(checkState(h.ok, h.total),
			'%s: %d/%d'.format(zc.formatTime(h.t), +h.ok || 0, +h.total || 0)));

		return E('div', {}, [
			E('div', { 'role': 'img', 'aria-label': _('Checks: %d, failed: %d').format(counted, failed),
				'style': 'display:flex;flex-wrap:wrap;gap:2px;margin:.5em 0' }, cells),
			E('div', { 'class': 'cbi-value-description', 'style': 'display:flex;flex-wrap:wrap;gap:1em;align-items:center' }, [
				E('span', {}, [ swatch('ok', ''), ' ', E('span', {}, txt(_('sites opened'))) ]),
				E('span', {}, [ swatch('fail', ''), ' ', E('span', {}, txt(_('less than half opened'))) ]),
				E('span', {}, [ swatch('none', ''), ' ', E('span', {}, txt(_('nothing to check'))) ]),
				E('span', {}, txt(_('Checks: %d, failed: %d').format(counted, failed)))
			])
		]);
	},

	/* Content of the monitor block from "monitor status" (§14.3). */
	monitorNodes(m) {
		const info = this.monitorStateInfo(m.state);
		const row = (label, value) => E('tr', { 'class': 'tr' }, [
			E('td', { 'class': 'td left', 'style': 'width:33%' }, [ E('strong', {}, txt(label)) ]),
			E('td', { 'class': 'td left' }, txt(value))
		]);
		const repair = L.isObject(m.last_repair) ? _('last time: %s').format(zc.formatTime(m.last_repair.t)) : _('has not run yet');

		/* The theme draws the "?" icon of .cbi-value-description in its left
		 * margin (absolute, left:-1.25em): the explanation goes on its own line
		 * with the theme margin, so the icon never covers the state label. */
		return [
			E('div', { 'style': 'margin:.5em 0' }, [
				E('span', { 'class': 'label %s'.format(info[1]), 'style': 'display:inline-block;white-space:nowrap' }, txt(info[0])),
				E('div', { 'class': 'cbi-value-description' }, txt(info[2]))
			]),
			E('table', { 'class': 'table' }, [
				row(_('Last check'), (+m.checked_at > 0)
					? _('%s, opened %d of %d').format(zc.formatTime(m.checked_at), +m.ok || 0, +m.total || 0)
					: _('never')),
				row(_('Failed checks in a row'), _('%d (state changes after %d)').format(+m.consecutive_failures || 0, +m.threshold || 0)),
				row(_('Check interval'), _('every %d min').format(+m.interval || 0)),
				row(_('Automatic repair'), m.auto_repair
					? _('on: quick strategy selection when the sites stop opening, %s').format(repair)
					: _('off'))
			]),
			(Array.isArray(m.history) && m.history.length) ? this.historyStrip(m.history) : ''
		];
	},

	/* --- background tasks with a result --- */

	/* Starts a background task with start() (a zc.run() promise) and waits
	 * until the job is not running any more, showing it in box (zc.jobBox)
	 * when given. Resolves with { res, job }; job is null when the answer
	 * started no job (for example "dns setup" when DNS is already set up). */
	runTask(start, box) {
		return start().then(res => {
			if (!L.isObject(res.job))
				return { res: res, job: null };

			box?.update(Object.assign({ state: 'running', progress: 0, message: '' }, res.job));

			return new Promise(resolve => zc.watchJob(j => { if (j) box?.update(j); }, resolve, 2))
				.then(job => ({ res: res, job: job }));
		});
	},

	/* --- encrypted DNS (ARCHITECTURE §15.3) --- */

	dnsProviderName(provider) {
		return { 'https-dns-proxy': 'https-dns-proxy', 'stubby': 'Stubby', 'dnscrypt-proxy': 'DNSCrypt' }[provider] ?? String(provider ?? '');
	},

	/* Label and explanation of the DNS state: status.dns or the answer of
	 * "dns status" ({encrypted, provider}). */
	dnsNodes(dns) {
		if (dns.encrypted === true)
			return [
				E('span', { 'class': 'label success', 'style': 'display:inline-block;white-space:nowrap' },
					txt(dns.provider ? _('On: %s').format(this.dnsProviderName(dns.provider)) : _('On'))),
				E('div', { 'class': 'cbi-value-description' }, txt(_('The router sends DNS requests encrypted: the provider cannot see or substitute them.')))
			];

		return [
			E('span', { 'class': 'label notice', 'style': 'display:inline-block;white-space:nowrap' }, txt(_('Off'))),
			E('div', { 'class': 'cbi-value-description' }, txt(_('The router asks the DNS servers of the provider without encryption. Some providers substitute the answers for blocked sites with the address of a stub page; then the site does not open even through zaprett. Encrypted DNS (DNS over HTTPS) hides the requests from the provider.')))
		];
	},

	/* Asks for confirmation, runs "dns setup" and reads the new state.
	 * Resolves with { job, changed, dns } or null when the user cancelled;
	 * rejects with the error of the start or of the state request. */
	setupDns(box) {
		return zc.confirm(_('Turn on encrypted DNS'),
			_('zaprett installs the https-dns-proxy package from the OpenWrt package repository (and its LuCI page, if LuCI is installed) and starts it. The router then sends DNS requests over HTTPS to public DNS servers set in that package; they can be changed on its settings page. The router needs internet access and some free space. Continue?'),
			_('Turn on'), 'apply').then(ok => {
			if (!ok)
				return null;

			return this.runTask(() => zc.run(zc.callDnsSetup), box).then(r => zc.run(zc.callDnsStatus).then(st => ({
				job: r.job,
				changed: r.res.changed !== false,
				dns: L.isObject(st.dns) ? st.dns : null
			})));
		});
	},

	/* Result of setupDns() as one message box. */
	dnsSetupResult(result) {
		const on = result?.dns?.encrypted === true;
		const job = result?.job;

		if (on)
			return E('div', { 'class': 'alert-message success' }, txt(result.changed
				? _('Encrypted DNS is on. Devices that use the router as their DNS server get encrypted DNS automatically.')
				: _('Encrypted DNS was already on, nothing needed to be done.')));

		return E('div', { 'class': 'alert-message danger' }, [
			E('p', {}, [ E('strong', {}, txt(_('Encrypted DNS could not be turned on'))) ]),
			E('p', { 'style': 'white-space:pre-wrap' }, txt((job?.state == 'failed' || job?.state == 'cancelled') && job.message
				? job.message
				: _('The package was not installed or its service is not running. Check the internet connection and free space of the router; the log of the task is on the "Diagnostics" page.')))
		]);
	},

	/* --- how the provider blocks (ARCHITECTURE §15.4) --- */

	/* [ label, style, advice ] for a verdict of "diagnose". */
	verdictInfo(verdict) {
		return {
			ok: [ _('Opens'), 'success',
				_('The sites open: no blocking is visible.') ],
			dns_spoof: [ _('DNS substitution'), 'danger',
				_('The provider substitutes DNS answers: instead of the real address of the site the router gets the address of a stub. Changing packets does not help while the requests go to the DNS of the provider. Turn on encrypted DNS: then the provider can neither see nor substitute the requests.') ],
			ip_block: [ _('Blocked by IP address'), 'danger',
				_('The provider does not let connections to the addresses of the site through. zaprett changes only the first packets of a connection and cannot open a blocked address: such sites need a VPN or a proxy.') ],
			tls_block: [ _('Blocked by site name (TLS)'), 'warning',
				_('The connection is established, but the provider breaks it when it sees the site name at the start of the encrypted connection. This is exactly what zaprett bypasses: run automatic strategy selection.') ],
			throttle: [ _('Slowed down'), 'warning',
				_('Data starts to come, but stops or freezes at about 14–24 KB: this is how the provider slows the site down. zaprett bypasses this: run automatic strategy selection.') ],
			http_block: [ _('Stub page of the provider'), 'warning',
				_('Instead of the site the provider returns its own page about the blocking. Usually the provider recognises the site name in the request, which zaprett can hide: run automatic strategy selection. If it does not help, turn on encrypted DNS.') ],
			unknown: [ _('Could not be determined'), 'notice',
				_('The site did not open, but it could not be determined how it is blocked. The site may be down by itself. Try automatic strategy selection; if it does not help, check the site through a VPN.') ]
		}[verdict] ?? [ String(verdict ?? ''), 'notice', _('The service returned a verdict this page does not know yet. Update the "luci-app-zaprett" package.') ];
	},

	/* Addresses of one target: what the DNS of the router and DNS over
	 * HTTPS answered. */
	dnsText(dns) {
		const list = v => (Array.isArray(v) && v.length) ? v.join(', ') : '—';

		if (!L.isObject(dns))
			return '—';

		return _('router: %s; DNS over HTTPS: %s').format(list(dns.system), list(dns.doh));
	},

	diagnoseTable(diag) {
		const titles = [ _('Site'), _('Result'), _('Addresses (DNS)'), _('Details') ];
		const rows = [ E('tr', { 'class': 'tr table-titles' }, titles.map(t => E('th', { 'class': 'th' }, txt(t)))) ];

		for (const t of (Array.isArray(diag?.targets) ? diag.targets : [])) {
			const info = this.verdictInfo(t.verdict);

			rows.push(E('tr', { 'class': 'tr' }, [
				E('td', { 'class': 'td', 'data-title': titles[0], 'style': 'word-break:break-all' }, txt(t.host || t.url)),
				E('td', { 'class': 'td', 'data-title': titles[1] }, [
					E('span', { 'class': 'label %s'.format(info[1]), 'style': 'display:inline-block;white-space:nowrap' }, txt(info[0]))
				]),
				E('td', { 'class': 'td', 'data-title': titles[2], 'style': 'word-break:break-all' }, [
					E('div', {}, txt(this.dnsText(t.dns))),
					(t.dns?.spoofed === true) ? E('strong', {}, txt(_('the router got a substituted address'))) : ''
				]),
				E('td', { 'class': 'td', 'data-title': titles[3], 'style': 'word-break:break-all' }, txt(t.detail ?? ''))
			]));
		}

		return E('table', { 'class': 'table' }, rows);
	},

	/* Conclusion with advice; actions: { dns: fn|null, strategies: node|null }.
	 * dns is offered only while encrypted DNS is off (dnsOn === false). */
	diagnoseSummary(diag, dnsOn, actions) {
		const summary = L.isObject(diag?.summary) ? diag.summary : {};
		const verdict = summary.verdict ?? 'unknown';
		const info = this.verdictInfo(verdict);
		const counts = L.isObject(summary.counts) ? summary.counts : {};
		const parts = Object.keys(counts).filter(v => +counts[v] > 0).map(v => '%s: %d'.format(this.verdictInfo(v)[0], +counts[v]));
		const okCount = +(counts.ok ?? 0);
		const total = Object.keys(counts).reduce((n, v) => n + (+counts[v] > 0 ? +counts[v] : 0), 0);
		const nodes = [
			E('p', {}, [ E('strong', {}, txt((verdict == 'ok') ? _('Conclusion: the sites open.')
				: (okCount > 0 ? _('Conclusion: %d of %d sites open; for the others — %s.').format(okCount, total, info[0])
					: _('Conclusion: %s.').format(info[0])))) ]),
			E('p', {}, txt(info[2]))
		];

		if (parts.length)
			nodes.push(E('p', { 'class': 'cbi-value-description' }, txt(_('Sites by result: %s.').format(parts.join('; ')))));

		if (verdict == 'dns_spoof' && dnsOn === true)
			nodes.push(E('p', {}, txt(_('Encrypted DNS is already on the router. Make sure the devices use the router as their DNS server and do not have another DNS set by hand.'))));

		const buttons = [];

		if ((verdict == 'dns_spoof' || verdict == 'http_block') && dnsOn === false && actions?.dns)
			buttons.push(zc.button(_('Turn on encrypted DNS'), actions.dns, 'apply'));

		if ([ 'tls_block', 'throttle', 'http_block', 'unknown' ].indexOf(verdict) >= 0 && actions?.strategies)
			buttons.push(actions.strategies);

		if (buttons.length)
			nodes.push(E('div', {}, buttons.reduce((list, b) => list.concat(list.length ? [ ' ', b ] : [ b ]), [])));

		return E('div', { 'class': 'alert-message %s'.format(info[1] == 'danger' ? 'warning' : ((verdict == 'ok') ? 'success' : 'notice')) }, nodes);
	},

	/* Note about the bypass during the diagnosis and the time of it. */
	diagnoseNote(diag) {
		return E('p', { 'class': 'cbi-value-description' }, txt('%s %s'.format(
			_('Checked: %s.').format(zc.formatTime(diag.finished || diag.started)),
			(diag.engine_running === true)
				? _('The bypass was running during the check, so the result shows what the router sees with zaprett.')
				: _('The bypass was not running during the check, so the result shows the blocking of the provider as it is.'))));
	},

	/* Whole result of "diagnose status" (the "diagnose" object or null). */
	diagnoseNodes(diag, dnsOn, actions) {
		if (!L.isObject(diag))
			return [ E('p', {}, txt(_('The check has not been made yet.'))) ];

		if (!Array.isArray(diag.targets) || !diag.targets.length)
			return [
				E('div', { 'class': 'alert-message notice' }, txt(_('There was nothing to check: no enabled service has check addresses.'))),
				this.diagnoseNote(diag)
			];

		return [ this.diagnoseSummary(diag, dnsOn, actions), this.diagnoseTable(diag), this.diagnoseNote(diag) ];
	},

	/* Runs "diagnose" for the given services (all active ones when empty) and
	 * resolves with the "diagnose" object of "diagnose status" or null. */
	runDiagnose(services, box) {
		const list = (Array.isArray(services) && services.length) ? services : undefined;

		return this.runTask(() => zc.run(zc.callDiagnoseStart, list), box).then(r => {
			if (r.job && r.job.state != 'done')
				throw new Error(r.job.message || zc.jobStateLabel(r.job.state));

			return zc.run(zc.callDiagnoseStatus);
		}).then(res => L.isObject(res.diagnose) ? res.diagnose : null);
	},

	/* List of steps of a multi-step action: { node, set(key, state, detail) }.
	 * state: pending | running | done | failed | skipped. */
	steps(list) {
		const node = E('ol', { 'style': 'margin:.5em 0 .5em 1.5em' });
		const items = {};
		const marks = { pending: '·', running: '…', done: '✓', failed: '✗', skipped: '–' };

		for (const step of list) {
			items[step[0]] = {
				mark: E('strong', { 'style': 'display:inline-block;width:1.2em' }, txt(marks.pending)),
				detail: E('div', { 'class': 'cbi-value-description' })
			};

			node.appendChild(E('li', {}, [ items[step[0]].mark, E('span', {}, txt(step[1])), items[step[0]].detail ]));
		}

		return {
			node: node,
			set: (key, state, detail) => {
				const item = items[key];

				if (!item)
					return;

				dom.content(item.mark, txt(marks[state] ?? marks.pending));

				if (detail !== undefined)
					dom.content(item.detail, Array.isArray(detail) ? detail : txt(detail));
			}
		};
	}
});
