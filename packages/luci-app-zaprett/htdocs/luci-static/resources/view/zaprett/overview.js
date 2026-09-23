// SPDX-License-Identifier: MIT
// zaprett: overview page - service state, control, warnings, live check of
// the services, encrypted DNS, availability monitor and the step-by-step
// quick setup.

'use strict';
'require view';
'require dom';
'require ui';
'require zaprett.common as zc';
'require zaprett.health as zh';

const txt = zc.txt;

/* Seconds between state refreshes; polling pauses while the tab is hidden. */
const POLL_INTERVAL = 10;

/* Milliseconds to wait after a start before checking the sites. */
const SETTLE_MS = 2000;

return view.extend({
	load() {
		return zc.loadPage('overview', {
			status: () => zc.run(zc.callStatus),
			job: () => zc.run(zc.callJobStatus),
			presets: () => zc.run(zc.callPresets),
			monitor: () => zc.run(zc.callMonitorStatus),
			probe: () => zc.run(zc.callProbeStatus),
			/* status.dns carries the same; an older service has neither */
			dns: () => Promise.resolve(null)
		});
	},

	render(data) {
		this.status = data.status;
		this.presets = (data.presets instanceof Error) ? null : data.presets;
		this.monitor = data.monitor;
		this.probe = data.probe;
		this.probeRunning = false;
		this.monitorCheckedAt = this.monitorCheckedTime(data.monitor);

		this.statusNode = E('div', {});
		this.warningsNode = E('div', {});
		this.probeNode = E('div', {});
		this.monitorNode = E('div', {});
		this.dnsPage = data.dns;
		this.dnsBusy = false;
		this.dnsNode = E('div', {});
		this.dnsBox = zc.jobBox(null);
		this.dnsResultNode = E('div', {});

		this.renderStatus(data.status, (data.job instanceof Error) ? null : (data.job.job ?? null));
		this.renderProbe();
		this.renderMonitor();

		const expanded = this.needsSetup();
		const wizard = this.renderQuickSetup(data.presets, expanded);

		zc.pollVisible(() => this.refresh(), POLL_INTERVAL);

		return E([], [
			E('h2', {}, txt(_('zaprett'))),
			E('div', { 'class': 'cbi-map-descr' }, txt(_('zaprett helps to open sites and services that the internet provider slows down or blocks with deep packet inspection (DPI). It changes only the first packets of connections to the selected sites, so the rest of the traffic goes as usual and no VPN or proxy is needed.'))),
			expanded ? wizard : '',
			this.statusNode,
			this.warningsNode,
			this.probeNode,
			this.dnsNode,
			this.monitorNode,
			expanded ? '' : wizard
		]);
	},

	/* The quick setup is shown open at the top while nothing is set up yet:
	 * no list is enabled, or the service is off and was never checked. */
	needsSetup() {
		const st = this.status;

		if (st instanceof Error)
			return false;

		if (Array.isArray(st.warnings) && st.warnings.indexOf('no_active_lists') >= 0)
			return true;

		return !st.enabled && !st.running && !this.probeResult();
	},

	probeResult() {
		return (this.probe instanceof Error || !L.isObject(this.probe?.probe)) ? null : this.probe.probe;
	},

	monitorCheckedTime(res) {
		return (res instanceof Error) ? null : (res?.monitor?.checked_at ?? null);
	},

	/* One call per tick: "status" carries the job and the monitor summary;
	 * an older service without these fields gets the job separately. */
	refresh() {
		return zc.run(zc.callStatus).catch(err => err).then(st => {
			const job = (st instanceof Error || st.job === undefined)
				? zc.run(zc.callJobStatus).then(res => res.job ?? null, () => null)
				: Promise.resolve(st.job);

			return job.then(j => {
				this.status = st;
				this.renderStatus(st, j);

				const checked = (st instanceof Error) ? undefined : st.monitor?.checked_at;

				if (checked != null && checked != this.monitorCheckedAt && !zc.isUsageError(this.monitor))
					return this.reloadMonitor();

				/* no RPC: only the note about a stopped service may change */
				this.renderMonitor();
			});
		});
	},

	stateInfo(st) {
		if (st.test_mode)
			return [ _('Strategy selection is running'), 'notice', _('zaprett is checking strategies now; the previous state is restored when the check ends.') ];

		if (st.running && st.enabled)
			return [ _('Running'), 'success', _('The bypass works for the selected sites.') ];

		if (st.running)
			return [ _('Running, autostart is off'), 'success', _('The bypass works now, but it will not start after a router reboot.') ];

		if (st.enabled)
			return [ _('Enabled, but not running'), 'danger', _('The service should work, but the engine is not running. Press "Restart" or look at "Diagnostics".') ];

		return [ _('Stopped'), 'warning', _('The bypass is off: all traffic goes as usual.') ];
	},

	row(label, value) {
		return E('tr', { 'class': 'tr' }, [
			E('td', { 'class': 'td left', 'style': 'width:33%' }, [ E('strong', {}, txt(label)) ]),
			E('td', { 'class': 'td left' }, Array.isArray(value) ? value : txt(value))
		]);
	},

	link(label, page) {
		return E('a', { 'href': zc.pageUrl(page) }, txt(label));
	},

	/* Counter of the engine queue (status.queue, ARCHITECTURE §14.3). */
	queueText(st) {
		if (L.isObject(st.queue))
			return [
				_('%d packets processed').format(+st.queue.packets || 0),
				E('div', { 'class': 'cbi-value-description' }, txt(_('The number grows when sites from the enabled lists are opened. If it stays the same while you open them, traffic does not reach the engine.')))
			];

		return st.running ? _('the queue of the engine is not active') : _('the engine is not running');
	},

	renderStatus(st, job) {
		if (st instanceof Error) {
			dom.content(this.statusNode, zc.section(_('Service state'), null, [
				E('div', { 'class': 'alert-message danger' }, [
					E('p', {}, [ E('strong', {}, txt(_('Could not get the state of zaprett'))) ]),
					E('p', {}, txt(zc.errorMessage(st)))
				]),
				zc.button(_('Try again'), ui.createHandlerFn(this, 'refresh'), 'action')
			]));
			dom.content(this.warningsNode, '');

			return;
		}

		const state = this.stateInfo(st);
		const strategy = L.isObject(st.strategy) ? st.strategy : { id: st.strategy, name: null };
		const strategyText = !strategy.id ? _('not selected')
			: ((strategy.name && strategy.name != strategy.id) ? '%s (%s)'.format(strategy.name, strategy.id) : strategy.id);
		const count = v => Array.isArray(v) ? v.filter(x => x).length : 0;
		const offload = L.isObject(st.flow_offload) ? st.flow_offload : {};
		const autostart = (st.autostart != null) ? st.autostart : st.enabled;

		const rows = [
			this.row(_('State'), [
				E('span', { 'class': 'label %s'.format(state[1]) }, txt(state[0])),
				E('div', { 'class': 'cbi-value-description' }, txt(state[2]))
			]),
			this.row(_('Autostart'), autostart ? _('On: zaprett starts together with the router') : _('Off')),
			this.row(_('Engine'), [ '%s %s'.format(st.engine ?? '', st.engine_version ?? '').trim() || _('unknown') ]),
			this.row(_('Strategy'), [ strategyText, ' ', this.link(_('change'), 'strategies') ]),
			this.row(_('Which sites are processed'), [
				(st.list_mode == 'blacklist')
					? _('All sites except the exclusions (blacklist)')
					: _('Only sites from the enabled lists (whitelist)'),
				' ', this.link(_('lists'), 'lists')
			]),
			this.row(_('Enabled lists'), _('domains: %d, domain exclusions: %d, IP networks: %d, IP exclusions: %d').format(
				count(st.lists), count(st.exclude_lists), count(st.ipsets), count(st.exclude_ipsets))),
			(st.queue !== undefined) ? this.row(_('Engine activity'), this.queueText(st)) : '',
			this.row(_('Firewall rules'), st.nft_applied ? _('applied') : _('not applied')),
			this.row(_('Internet interfaces'), (Array.isArray(st.wan) && st.wan.length) ? st.wan.join(', ') : _('not detected')),
			this.row(_('Flow offloading'), '%s; %s'.format(
				(offload.fw4 || offload.fw4_hw) ? _('enabled in the firewall') : _('disabled in the firewall'),
				this.offloadText(offload))),
			this.row(_('zaprett version'), st.version || _('unknown'))
		];

		const buttons = E('div', { 'class': 'cbi-page-actions', 'style': 'text-align:left' }, [
			zc.button(_('Start'), ui.createHandlerFn(this, 'handleService', 'start'), 'positive', { 'disabled': st.running ? '' : null }), ' ',
			zc.button(_('Stop'), ui.createHandlerFn(this, 'handleService', 'stop'), 'negative', { 'disabled': st.running ? null : '' }), ' ',
			zc.button(_('Restart'), ui.createHandlerFn(this, 'handleService', 'restart'), 'reload', { 'disabled': st.enabled ? null : '' }), ' ',
			st.enabled
				? zc.button(_('Disable autostart'), ui.createHandlerFn(this, 'handleService', 'disable'), 'neutral')
				: zc.button(_('Enable autostart'), ui.createHandlerFn(this, 'handleService', 'enable'), 'neutral'), ' ',
			zc.button(_('Check configuration'), ui.createHandlerFn(this, 'handleCheck'), 'action')
		]);

		const nodes = [
			E('table', { 'class': 'table' }, rows),
			E('p', { 'class': 'cbi-value-description' }, txt(_('"Disable autostart" also stops zaprett. "Start" turns autostart on.'))),
			buttons
		];

		/* the probe and DNS setup jobs are shown in their own blocks below */
		if (L.isObject(job) && job.state == 'running' && job.name != 'probe' && job.name != 'dns-setup') {
			const page = { 'test': 'strategies', 'sources-update': 'lists', 'diagnose': 'diagnostics' }[job.name] ?? 'repo';

			nodes.unshift(E('div', { 'class': 'alert-message notice' }, [
				E('p', {}, txt(_('Background task: %s, %d%% done.').format(zc.jobLabel(job.name), +job.progress || 0))),
				E('p', {}, [ this.link(_('Show progress'), page) ])
			]));
		}

		dom.content(this.statusNode, zc.section(_('Service state'), null, nodes));
		this.renderWarnings(st);
		this.job = job;
		this.renderDns();
	},

	/* Mode of flow offloading (main.flow_offload: auto | keep | own, §15.1). */
	offloadText(offload) {
		if (offload.mode == 'keep')
			return _('zaprett does not change it');

		if (offload.mode == 'own')
			return (offload.own === true)
				? _('zaprett uses its own acceleration table')
				: _('zaprett will add its own acceleration table once acceleration is turned on in the firewall');

		return _('zaprett turns it off while running');
	},

	/* Problems first; codes that only inform go to a separate quiet block. */
	renderWarnings(st) {
		const warnings = Array.isArray(st.warnings) ? st.warnings : [];
		const problems = warnings.filter(w => !zc.isInfoWarning(w));
		const notes = warnings.filter(w => zc.isInfoWarning(w));

		dom.content(this.warningsNode, [
			problems.length ? zc.section(_('What needs attention'), null, problems.map(w => this.renderWarning(w, st, 'warning'))) : '',
			notes.length ? zc.section(_('For information'), null, notes.map(w => this.renderWarning(w, st, 'notice'))) : ''
		]);
	},

	renderWarning(warning, st, style) {
		const info = zc.warningInfo(warning, st);
		let action = '';

		switch (info.action) {
		case 'restart':
			action = zc.button(_('Restart'), ui.createHandlerFn(this, 'handleService', 'restart'), 'reload');
			break;

		case 'lists':
			action = this.link(_('Open "Lists"'), 'lists');
			break;

		case 'settings':
			action = this.link(_('Open "Settings"'), 'settings');
			break;

		case 'strategies':
			action = this.link(_('Open "Strategies"'), 'strategies');
			break;

		case 'diagnostics':
			action = this.link(_('Open "Diagnostics"'), 'diagnostics');
			break;

		case 'dns':
			if (this.dnsInfo()?.encrypted === false)
				action = zc.button(_('Turn on encrypted DNS'), ui.createHandlerFn(this, 'handleDns'), 'apply', { 'disabled': this.dnsBusy ? '' : null });
			break;
		}

		return E('div', { 'class': 'alert-message %s'.format(style) }, [
			E('p', {}, [ E('strong', {}, txt(info.title)) ]),
			E('p', {}, txt(info.text)),
			action ? E('p', {}, [ action ]) : ''
		]);
	},

	handleService(action, ev) {
		const texts = {
			start: [ _('zaprett is started.'), _('Could not start zaprett') ],
			stop: [ _('zaprett is stopped.'), _('Could not stop zaprett') ],
			restart: [ _('zaprett is restarted.'), _('Could not restart zaprett') ],
			enable: [ _('Autostart is enabled.'), _('Could not enable autostart') ],
			disable: [ _('Autostart is disabled, zaprett is stopped.'), _('Could not disable autostart') ]
		}[action];

		return zc.run(zc.callService, action)
			.then(() => zc.notifyInfo(texts[0]), err => zc.notifyError(texts[1], err))
			.then(() => this.refresh());
	},

	handleCheck(ev) {
		return zc.run(zc.callCheck).then(res => zc.renderCheck(res, null), err => {
			if (!err?.zaprett || err.code == 'backend_error' || err.code == 'timeout')
				throw err;

			return zc.renderCheck(null, err);
		}).then(nodes => {
			ui.showModal(txt(_('Configuration check')), [
				...nodes,
				E('div', { 'class': 'right' }, [ zc.button(_('Close'), ui.hideModal, 'neutral') ])
			], 'cbi-modal');
		}).catch(err => zc.notifyError(_('Could not check the configuration'), err));
	},

	/* --- live check of the services --- */

	renderProbe() {
		const res = this.probe;
		const probe = this.probeResult();
		const nodes = [];

		if (zc.isUsageError(res)) {
			dom.content(this.probeNode, zc.section(_('Do the sites open?'), null, [
				E('div', { 'class': 'alert-message notice' }, txt(_('The installed zaprett service cannot check the sites yet. Update the "zaprett" package.')))
			]));

			return;
		}

		if (res instanceof Error)
			nodes.push(zc.errorBox(res, _('Could not get the result of the last check')));

		if (this.probeRunning)
			nodes.push(E('div', { 'class': 'alert-message notice' }, txt(_('Checking the sites of the enabled services…'))));

		if (probe) {
			if (probe.engine_running === false)
				nodes.push(E('div', { 'class': 'alert-message warning' }, txt(_('The bypass was not running during this check, so it shows how the sites open without zaprett.'))));

			nodes.push(zh.probeCards(probe, this.presets), zh.probeSummary(probe));
		}
		else if (!this.probeRunning && !(res instanceof Error)) {
			nodes.push(E('p', {}, txt(_('The sites have not been checked yet.'))));
		}

		nodes.push(E('div', {}, [
			zc.button(_('Check now'), ui.createHandlerFn(this, 'handleProbe'), 'action', { 'disabled': this.probeRunning ? '' : null }), ' ',
			(probe && Array.isArray(probe.services) && probe.services.length)
				? zc.button(_('Details'), ui.createHandlerFn(this, () => zh.showProbeDetails(probe, this.presets)), 'neutral')
				: ''
		]));

		dom.content(this.probeNode, zc.section(_('Do the sites open?'),
			_('zaprett opens the check addresses of the enabled services from the router itself, through the bypass as it works now, and shows what opened. The bypass is not restarted for this check.'),
			nodes));
	},

	handleProbe(ev) {
		return this.runProbe(undefined);
	},

	/* Resolves with the background job once it is not running any more. */
	waitJob() {
		return new Promise(resolve => zc.watchJob(null, resolve, 2));
	},

	/* Starts "probe", waits for its job and shows the result. Resolves with
	 * the probe result, or null when the check could not be made. */
	runProbe(services) {
		return zc.run(zc.callProbeStart, services).then(() => {
			this.probeRunning = true;
			this.renderProbe();

			return this.waitJob();
		}).then(job => {
			if (job?.state == 'failed')
				ui.addNotification(null, E('p', {}, txt(_('The check of the sites failed: %s').format(job.message ?? ''))), 'danger');

			return zc.run(zc.callProbeStatus).catch(err => err);
		}).then(res => {
			this.probeRunning = false;
			this.probe = res;
			this.renderProbe();

			return this.probeResult();
		}).catch(err => {
			this.probeRunning = false;

			if (zc.isUsageError(err))
				this.probe = err;
			else
				zc.notifyError(_('Could not check the sites'), err);

			this.renderProbe();

			return null;
		});
	},

	/* --- encrypted DNS (§15.3) --- */

	/* {encrypted, provider} from status.dns or from the page answer; null
	 * when the service does not report it (older service: no card). */
	dnsInfo() {
		const st = this.status;

		if (!(st instanceof Error) && L.isObject(st?.dns))
			return st.dns;

		const page = this.dnsPage;

		return (!(page instanceof Error) && L.isObject(page?.dns)) ? page.dns : null;
	},

	renderDns() {
		const dns = this.dnsInfo();

		if (!dns) {
			dom.content(this.dnsNode, '');
			return;
		}

		/* a setup started earlier (another tab, before a reload) is still running */
		const elsewhere = !this.dnsBusy && L.isObject(this.job) && this.job.name == 'dns-setup' && this.job.state == 'running';

		dom.content(this.dnsNode, zc.section(_('Encrypted DNS'), null, [
			E('div', { 'style': 'margin:.5em 0' }, zh.dnsNodes(dns)),
			elsewhere ? E('div', { 'class': 'alert-message notice' }, txt(_('Encrypted DNS is being set up now (%d%% done).').format(+this.job.progress || 0))) : '',
			(dns.encrypted === true) ? '' : E('div', {}, [
				zc.button(_('Turn on encrypted DNS'), ui.createHandlerFn(this, 'handleDns'), 'apply', { 'disabled': (this.dnsBusy || elsewhere) ? '' : null })
			]),
			this.dnsBox.node,
			this.dnsResultNode
		]));
	},

	handleDns(ev) {
		this.dnsBusy = true;
		dom.content(this.dnsResultNode, '');
		this.renderDns();

		return zh.setupDns(this.dnsBox).then(result => {
			if (!result)
				return;

			if (result.dns)
				this.dnsPage = { ok: true, dns: result.dns };

			dom.content(this.dnsResultNode, zh.dnsSetupResult(result));
		}).catch(err => {
			dom.content(this.dnsResultNode, zc.errorBox(err, _('Could not turn on encrypted DNS')));
		}).finally(() => {
			this.dnsBusy = false;
			this.dnsBox.update(null);

			return this.refresh();
		});
	},

	/* --- availability monitor --- */

	renderMonitor() {
		const res = this.monitor;

		/* an older service has no monitor: the block is not shown */
		if (zc.isUsageError(res) || (!(res instanceof Error) && !L.isObject(res?.monitor))) {
			dom.content(this.monitorNode, '');
			return;
		}

		const title = _('Availability monitor');

		if (res instanceof Error) {
			dom.content(this.monitorNode, zc.section(title, null, [ zc.errorBox(res, _('Could not get the state of the monitor')) ]));
			return;
		}

		const m = res.monitor;

		if (!m.enabled) {
			dom.content(this.monitorNode, zc.section(title, null, [
				E('p', {}, txt(_('The monitor is off. When it is on, zaprett checks the sites of the enabled services on schedule and warns when they stop opening; it can also find a new strategy automatically.'))),
				E('p', {}, [ this.link(_('Open "Settings"'), 'settings') ])
			]));
			return;
		}

		/* after "stop" the watchdog and monitor schedules are removed on purpose
		 * (ARCHITECTURE §14.3): the state below is the last one before it */
		const st = this.status;
		const paused = !(st instanceof Error) && L.isObject(st) && !st.running;

		dom.content(this.monitorNode, zc.section(title,
			_('zaprett checks the sites of the enabled services on schedule. A check fails when less than half of the sites opened.'),
			[
				paused ? E('div', { 'class': 'alert-message notice' }, txt(_('zaprett is not running, so the monitor does not check the sites now. This is normal after "Stop"; the checks resume after "Start".'))) : '',
				...zh.monitorNodes(m),
				E('p', {}, [ this.link(_('Monitor settings'), 'settings') ])
			]));
	},

	reloadMonitor() {
		return zc.run(zc.callMonitorStatus).catch(err => err).then(res => {
			this.monitor = res;
			this.monitorCheckedAt = this.monitorCheckedTime(res);
			this.renderMonitor();
		});
	},

	/* --- quick setup --- */

	serviceState(svc) {
		if (svc.works == 'no')
			return { selectable: false, label: _('zaprett cannot help'), style: 'warning' };

		if (svc.available === false)
			return { selectable: false, label: _('lists are not installed'), style: 'notice' };

		if (svc.enabled === true || this.enabledVariant(svc))
			return { selectable: true, label: _('enabled'), style: 'success' };

		if (svc.partially_enabled === true)
			return { selectable: true, label: _('partially enabled'), style: 'notice' };

		return { selectable: true, label: null, style: null };
	},

	/* Valid variants of a service from the "presets" reply (ARCHITECTURE §16.3). */
	serviceVariants(svc) {
		return (Array.isArray(svc?.variants) ? svc.variants : []).filter(v => L.isObject(v) && zc.isId(v.id));
	},

	/* The variant that is on now (ARCHITECTURE §16.4: enabled_variant), or null. */
	enabledVariant(svc) {
		const id = svc?.enabled_variant;

		return (zc.isId(id) && this.serviceVariants(svc).some(v => v.id == id)) ? id : null;
	},

	/* Load estimate of a list set by its tier. */
	variantLoad(tier, memory) {
		if (tier != 'full')
			return _('Load: light, suits any router.');

		return (memory.recommended == 'light')
			? _('Load: heavy, needs at least %d MiB of memory: not recommended for this router.').format(memory.fullMin)
			: _('Load: heavy, needs at least %d MiB of memory.').format(memory.fullMin);
	},

	/* Choice of the list set of a service with variants: the main set and
	 * every variant with its description and load; the current one is checked. */
	renderVariants(svc, selectable, memory) {
		const variants = this.serviceVariants(svc);

		if (!variants.length)
			return '';

		const current = this.enabledVariant(svc) ?? '';
		const name = 'zaprett-var-%s'.format(svc.id);
		const radios = [];
		const option = (value, title, description, tier, available) => {
			const radio = E('input', {
				'type': 'radio', 'name': name, 'value': value,
				'checked': (value == current) ? '' : null,
				'disabled': (selectable && available) ? null : ''
			});

			radios.push(radio);

			return E('div', { 'style': 'margin:.25em 0' }, [
				E('label', {}, [ radio, ' ', E('strong', {}, txt(title)) ]),
				description ? E('div', { 'class': 'cbi-value-description' }, txt(description)) : '',
				E('div', { 'class': 'cbi-value-description' }, txt(this.variantLoad(tier, memory)))
			]);
		};

		const rows = [
			option('', _('Main'), _('The basic lists of the service: its sites and apps.'), svc.tier, true),
			...variants.map(v => option(v.id, zc.localized(v, 'name') || v.id, zc.localized(v, 'description'), v.tier ?? svc.tier, v.available !== false))
		];

		this.variantRadios[svc.id] = radios;

		return E('div', { 'style': 'margin:.5em 0 .25em 0' }, [
			E('div', { 'class': 'cbi-value-description' }, [ E('strong', {}, txt(_('List set:'))) ]),
			...rows
		]);
	},

	/* "id" or "id:variant" of a marked service (ARCHITECTURE §16.4). */
	serviceRef(id) {
		const radio = (this.variantRadios?.[id] ?? []).filter(r => r.checked)[0];

		return (radio && zc.isId(radio.value)) ? '%s:%s'.format(id, radio.value) : id;
	},

	serviceNotes(svc, memory) {
		const notes = [];

		if (svc.works == 'partial')
			notes.push(E('div', { 'class': 'cbi-value-description' }, txt(_('Helps only partially.'))));

		if (svc.tier == 'full')
			notes.push(E('div', { 'class': 'cbi-value-description' }, [
				E('strong', {}, txt((memory.recommended == 'light')
					? (memory.total
						? _('Needs at least %d MiB of memory, the router has %d MiB: not recommended.').format(memory.fullMin, memory.total)
						: _('Not recommended for this router: it has too little memory for large lists.'))
					: _('Uses large lists: needs a router with at least %d MiB of memory.').format(memory.fullMin)))
			]));

		if (Array.isArray(svc.sources) && svc.sources.length)
			notes.push(E('div', { 'class': 'cbi-value-description' }, txt(_('Uses subscriptions to external lists: they will be downloaded after applying.'))));

		if (svc.available === false && svc.works != 'no')
			notes.push(E('div', { 'class': 'cbi-value-description' }, [
				E('span', {}, txt(_('Its lists are not installed. Install them from the "Repository" page or set up a subscription on the "Lists" page.'))), ' ',
				this.link(_('Open "Lists"'), 'lists')
			]));

		const note = zc.localized(svc, 'note');

		if (note)
			notes.push(E('div', { 'class': 'cbi-value-description' }, [ E('em', {}, txt(note)) ]));

		return notes;
	},

	renderQuickSetup(presets, expanded) {
		const title = _('Quick setup');

		if (presets instanceof Error)
			return zc.section(title, null, [ zc.errorBox(presets, _('Could not load the list of services')) ]);

		const services = Array.isArray(presets.services) ? presets.services : [];
		const defaults = Array.isArray(presets.defaults?.services) ? presets.defaults.services : [];
		const memory = zc.memoryAdvice(presets);
		const isOn = s => s.enabled === true || s.partially_enabled === true || this.enabledVariant(s) != null;
		const anyEnabled = services.some(isOn);
		const boxes = [];

		this.variantRadios = {};

		const rows = services.map(svc => {
			const st = this.serviceState(svc);
			const checked = st.selectable && (anyEnabled ? isOn(svc) : defaults.indexOf(svc.id) >= 0);
			const id = 'zaprett-svc-%s'.format(svc.id);
			const box = E('input', { 'type': 'checkbox', 'value': svc.id, 'id': id, 'checked': checked ? '' : null, 'disabled': st.selectable ? null : '' });

			if (st.selectable)
				boxes.push(box);

			return E('div', { 'class': 'cbi-value' }, [
				E('label', { 'class': 'cbi-value-title', 'for': id }, txt(zc.localized(svc, 'name') ?? svc.id)),
				E('div', { 'class': 'cbi-value-field' }, [
					box,
					st.label ? E('span', { 'class': 'label %s'.format(st.style), 'style': 'margin-left:.5em' }, txt(st.label)) : '',
					E('div', { 'class': 'cbi-value-description' }, txt(zc.localized(svc, 'description') ?? '')),
					...this.serviceNotes(svc, memory),
					this.renderVariants(svc, st.selectable, memory)
				])
			]);
		});

		if (!rows.length)
			rows.push(E('p', {}, txt(_('The service list is empty.'))));

		if (memory.recommended == 'light' || (memory.recommended == 'full' && memory.total))
			rows.unshift(E('div', { 'class': 'alert-message notice' }, txt((memory.recommended == 'light')
				? (memory.total
					? _('The router has %d MiB of memory: choose services with ready-made lists and do not enable large registries of blocked sites.').format(memory.total)
					: _('The router has little memory: choose services with ready-made lists and do not enable large registries of blocked sites.'))
				: _('The router has %d MiB of memory: large registries of blocked sites can be enabled too.').format(memory.total))));

		this.wizardProgress = E('div', {});

		const content = [
			E('div', { 'class': 'cbi-section-descr' }, txt(_('Mark the services that do not work well with your provider and press "Apply and start". zaprett enables ready-made lists of their sites and the always-needed exclusions (government services, banks) in the whitelist mode, so other traffic stays untouched; lists and subscriptions of the services that are not marked are switched off. Then it starts the bypass and checks whether the sites open. If they do not, it offers to find a strategy that works with your provider.'))),
			E('div', {}, rows),
			E('div', { 'class': 'cbi-page-actions', 'style': 'text-align:left' }, [
				zc.button(_('Apply and start'), ui.createHandlerFn(this, 'handleWizard', boxes), 'apply')
			]),
			this.wizardProgress
		];

		if (expanded)
			return E('div', { 'class': 'cbi-section' }, [ E('h3', {}, txt(title)), ...content ]);

		return E('div', { 'class': 'cbi-section' }, [
			E('details', {}, [
				E('summary', { 'style': 'cursor:pointer' }, [ E('strong', {}, txt(_('Quick setup: choose the services again'))) ]),
				...content
			])
		]);
	},

	/* Step-by-step setup: lists → start → check → (strategy selection) → result. */
	handleWizard(boxes, ev) {
		const selected = boxes.filter(b => b.checked).map(b => b.value).filter(id => zc.isId(id));
		const refs = selected.map(id => this.serviceRef(id));

		if (!selected.length) {
			ui.addNotification(null, E('p', {}, txt(_('Mark at least one service.'))), 'warning');
			return Promise.resolve();
		}

		const steps = zh.steps([
			[ 'lists', _('Enable the lists of the selected services') ],
			[ 'start', _('Start the bypass') ],
			[ 'probe', _('Check whether the sites open') ]
		]);

		this.wizardResultNode = E('div', {});
		dom.content(this.wizardProgress, [ steps.node, this.wizardResultNode ]);
		steps.set('lists', 'running');

		return zc.run(zc.callWizardApply, refs).then(res => {
			/* a service with subscriptions starts their download: the check of
			 * the sites is a background task too, so it waits for the download */
			if (!L.isObject(res.job))
				return res;

			steps.set('lists', 'running', [
				...this.wizardListsDetail(res),
				E('div', {}, txt(_('Downloading the subscriptions of the selected services…')))
			]);

			return this.waitJob().then(() => res);
		}).then(res => {
			steps.set('lists', 'done', this.wizardListsDetail(res));
			steps.set('start', 'running');

			return this.ensureRunning(res).then(started => {
				steps.set('start', 'done', started ? _('zaprett is started.') : _('zaprett works with the new lists.'));

				return this.wizardProbe(steps, Array.isArray(res.services) ? res.services : selected);
			});
		}).catch(err => {
			const failed = this.wizardStepFailed ?? 'lists';

			steps.set(failed, 'failed', [ zc.errorBox(err) ]);
		}).finally(() => {
			this.wizardStepFailed = null;

			return this.refresh();
		});
	},

	wizardListsDetail(res) {
		const lines = [];
		const skipped = Array.isArray(res?.skipped) ? res.skipped : [];
		const sources = Array.isArray(res?.sources) ? res.sources : [];

		if (skipped.length)
			lines.push(_('These services are not enabled, because zaprett cannot help with them: %s.').format(skipped.map(s => zh.serviceName(s.id, null, this.presets)).join(', ')));

		const variants = L.isObject(res?.variants) ? Object.keys(res.variants).filter(id => zc.isId(res.variants[id])) : [];

		if (variants.length)
			lines.push(_('Other list sets are chosen: %s.').format(variants.map(id => '%s — %s'.format(
				zh.serviceName(id, null, this.presets), this.variantName(id, res.variants[id]))).join(', ')));

		if (sources.length)
			lines.push(_('Subscriptions are enabled: %s. Their lists are being downloaded; progress is on the "Lists" page, "Subscriptions" tab.').format(sources.join(', ')));

		if (Array.isArray(res?.sources_disabled) && res.sources_disabled.length)
			lines.push(_('Subscriptions of the services that are not marked are switched off: %s.').format(res.sources_disabled.join(', ')));

		if (L.isObject(res?.job_error))
			lines.push(_('The subscriptions could not be downloaded right now: %s. Open "Lists" → "Subscriptions" and press "Update now".')
				.format(res.job_error.message || res.job_error.code || ''));

		for (const w of (Array.isArray(res?.warnings) ? res.warnings : []).filter(w => !zc.isInfoWarning(w))) {
			const info = zc.warningInfo(w, res);

			lines.push('%s. %s'.format(info.title, info.text));
		}

		return lines.map(l => E('div', {}, txt(l)));
	},

	/* Name of a variant of a service in the interface language. */
	variantName(id, variant) {
		const list = Array.isArray(this.presets?.services) ? this.presets.services : [];
		const v = this.serviceVariants(list.filter(s => s.id == id)[0]).filter(x => x.id == variant)[0];

		return (v ? zc.localized(v, 'name') : null) || variant;
	},

	/* Resolves with true when zaprett had to be started. */
	ensureRunning(res) {
		const st = this.status;
		const running = !(st instanceof Error) && st.running;

		this.wizardStepFailed = 'start';

		const action = !running ? 'start' : ((res?.reloaded === true) ? null : 'restart');

		if (!action)
			return Promise.resolve(false);

		return zc.run(zc.callService, action)
			.then(() => new Promise(resolve => window.setTimeout(resolve, SETTLE_MS)))
			.then(() => action == 'start');
	},

	/* Services of the selection that have addresses to check. */
	checkableServices(ids) {
		const list = Array.isArray(this.presets?.services) ? this.presets.services : [];

		return ids.filter(id => list.some(s => s.id == id && Array.isArray(s.test_targets) && s.test_targets.length));
	},

	wizardProbe(steps, ids) {
		const services = this.checkableServices(ids);

		this.wizardStepFailed = 'probe';

		if (!services.length) {
			steps.set('probe', 'skipped', _('The selected services have no addresses to check.'));
			this.wizardDone(null);

			return Promise.resolve();
		}

		steps.set('probe', 'running');

		return this.runProbe(services).then(probe => {
			if (!probe) {
				steps.set('probe', zc.isUsageError(this.probe) ? 'skipped' : 'failed',
					zc.isUsageError(this.probe) ? _('The installed zaprett service cannot check the sites yet. Update the "zaprett" package.') : '');
				this.wizardDone(null);

				return;
			}

			steps.set('probe', 'done', [ zh.probeCards(probe, this.presets) ]);
			this.wizardDone(probe);
		});
	},

	/* Result of the setup: done, or an offer to find another strategy. */
	wizardDone(probe) {
		const failed = zh.failedServices(probe);

		if (!probe || !failed.length) {
			dom.content(this.wizardResultNode, E('div', { 'class': 'alert-message success' }, [
				E('p', {}, [ E('strong', {}, txt(probe ? _('Done: the selected services open through zaprett.') : _('Done: zaprett is set up and running.'))) ]),
				E('p', {}, txt(_('zaprett keeps working after a router reboot. If sites stop opening later, run automatic strategy selection on the "Strategies" page.')))
			]));

			return;
		}

		const names = failed.map(s => zh.serviceName(s.id, s.name, this.presets)).join(', ');

		this.wizardDiagNode = E('div', {});

		dom.content(this.wizardResultNode, E('div', { 'class': 'alert-message warning' }, [
			E('p', {}, [ E('strong', {}, txt(_('These services did not open: %s.').format(names))) ]),
			E('p', {}, txt(_('Different providers block in different ways, so the default strategy may not suit yours. Automatic selection checks strategies one by one and keeps the one that opens more sites. It takes several minutes; the bypass keeps working for your devices during the check.'))),
			E('p', {}, txt(_('You can first find out how the provider blocks these sites: if it substitutes DNS or blocks the addresses, another strategy will not help, and the check tells what will.'))),
			E('div', {}, [
				zc.button(_('Find a working strategy'), ui.createHandlerFn(this, 'handleWizardTest'), 'apply'), ' ',
				zc.button(_('Find out how the provider blocks'), ui.createHandlerFn(this, 'handleWizardDiagnose', failed.map(s => s.id).filter(id => zc.isId(id))), 'action')
			]),
			this.wizardDiagNode
		]));
	},

	/* Diagnosis of the services that did not open (§15.4). */
	handleWizardDiagnose(ids, ev) {
		const box = zc.jobBox(() => zc.cancelJob());
		const out = E('div', {});

		dom.content(this.wizardDiagNode, [ E('h4', {}, txt(_('How the provider blocks'))), box.node, out ]);

		return zh.runDiagnose(ids, box).then(diag => {
			box.update(null);
			dom.content(out, zh.diagnoseNodes(diag, this.dnsInfo()?.encrypted, {
				dns: ui.createHandlerFn(this, 'handleDnsFromDiagnose'),
				strategies: zc.button(_('Find a working strategy'), ui.createHandlerFn(this, 'handleWizardTest'), 'apply')
			}));
		}).catch(err => {
			box.update(null);
			dom.content(out, zc.isUsageError(err)
				? E('div', { 'class': 'alert-message notice' }, txt(_('The installed zaprett service cannot find out how the provider blocks yet. Update the "zaprett" package.')))
				: zc.errorBox(err, _('Could not find out how the provider blocks')));
		});
	},

	/* "Turn on encrypted DNS" in the conclusion of the diagnosis: the DNS
	 * card shows the progress and the result. */
	handleDnsFromDiagnose(ev) {
		if (typeof(this.dnsNode.scrollIntoView) == 'function')
			this.dnsNode.scrollIntoView({ block: 'start' });

		return this.handleDns(ev);
	},

	/* Quick selection that applies the best strategy itself when it is better
	 * (--apply-if-better); an older service gets it without the flag. */
	startSelection() {
		return zc.run(zc.callTestStart, undefined, true, true).then(res => ({ res: res, autoApply: true }), err => {
			if (!zc.isUsageError(err))
				throw err;

			return zc.run(zc.callTestStart, undefined, true).then(res => ({ res: res, autoApply: false }));
		});
	},

	handleWizardTest(ev) {
		const steps = zh.steps([
			[ 'select', _('Find a working strategy') ],
			[ 'probe', _('Check the sites again') ]
		]);
		const job = zc.jobBox(() => zc.cancelJob(zc.callTestStop));

		dom.content(this.wizardResultNode, [ steps.node, job.node ]);
		steps.set('select', 'running');

		return this.startSelection().then(started => new Promise(resolve => {
			job.update(Object.assign({ state: 'running', progress: 0, message: '' }, started.res.job ?? { name: 'test' }));
			zc.watchJob(j => { if (j) job.update(j); }, resolve, 3);
		}).then(j => zc.run(zc.callTestStatus, true).catch(err => zc.isUsageError(err) ? zc.run(zc.callTestStatus) : Promise.reject(err))
			.then(res => this.selectionResult(steps, j, res, started.autoApply))
		)).catch(err => steps.set('select', 'failed', [ zc.errorBox(err, _('Could not start strategy selection')) ]))
			.finally(() => this.refresh());
	},

	selectionResult(steps, job, res, autoApply) {
		const results = L.isObject(res?.results) ? res.results : {};
		const list = Array.isArray(results.results) ? results.results : [];
		const best = list.filter(r => r.status == 'done' && +r.ok > 0)[0];
		const applied = results.applied ?? job?.result?.applied ?? null;

		if (job?.state != 'done') {
			steps.set('select', 'failed', (job?.state == 'cancelled') ? _('The check was stopped before all strategies were checked.') : (job?.message ?? ''));
			return;
		}

		if (applied) {
			const item = list.filter(r => r.id == applied)[0];

			steps.set('select', 'done', _('Strategy "%s" is selected and applied.').format(item?.name ?? applied));

			return this.recheck(steps);
		}

		if (autoApply || !best) {
			steps.set('select', 'done', best
				? _('None of the checked strategies works better than the current one.')
				: _('None of the checked strategies helped.'));
			steps.set('probe', 'skipped');
			this.wizardResultNode.appendChild(E('div', { 'class': 'alert-message warning' }, [
				E('p', {}, txt(_('Install more strategies from the "Repository" page and run the selection again, or check that the enabled lists contain the blocked sites.'))),
				E('p', {}, [ this.link(_('Open "Strategies"'), 'strategies') ])
			]));

			return;
		}

		/* an older service does not apply the result itself */
		steps.set('select', 'done', _('Best result: "%s", %d of %d sites opened.').format(best.name ?? best.id, +best.ok || 0, +best.total || 0));

		return zc.run(zc.callTestApply, best.id).then(() => this.recheck(steps), err => {
			steps.set('probe', 'failed', [ zc.errorBox(err, _('Could not apply the strategy')) ]);
		});
	},

	recheck(steps) {
		steps.set('probe', 'running');

		return this.runProbe(this.checkableServices((this.probeResult()?.services ?? []).map(s => s.id))).then(probe => {
			steps.set('probe', probe ? 'done' : 'failed', probe ? [ zh.probeCards(probe, this.presets) ] : '');

			const failed = zh.failedServices(probe);

			this.wizardResultNode.appendChild(E('div', { 'class': 'alert-message %s'.format(failed.length ? 'warning' : 'success') },
				txt(failed.length
					? _('Some services still do not open. Look at the results on the "Strategies" page or install more strategies from the "Repository".')
					: _('Done: the selected services open through zaprett.'))));
		});
	},

	handleSaveApply: null,
	handleSave: null,
	handleReset: null
});
