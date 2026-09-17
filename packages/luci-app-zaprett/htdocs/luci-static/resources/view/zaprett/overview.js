// SPDX-License-Identifier: MIT
// zaprett: overview page - service state, control, warnings, quick setup.

'use strict';
'require view';
'require dom';
'require poll';
'require ui';
'require zaprett.common as zc';

const txt = zc.txt;

return view.extend({
	load() {
		return Promise.all([
			zc.run(zc.callStatus).catch(err => err),
			zc.run(zc.callPresets).catch(err => err),
			zc.run(zc.callJobStatus).then(res => res.job ?? null, () => null)
		]);
	},

	render(data) {
		this.status = data[0];
		this.statusNode = E('div', {});
		this.renderStatus(data[0], data[2]);

		poll.add(L.bind(this.refresh, this), 5);

		return E([], [
			E('h2', {}, txt(_('zaprett'))),
			E('div', { 'class': 'cbi-map-descr' }, txt(_('zaprett helps to open sites and services that the internet provider slows down or blocks with deep packet inspection (DPI). It changes only the first packets of connections to the selected sites, so the rest of the traffic goes as usual and no VPN or proxy is needed.'))),
			this.statusNode,
			this.renderQuickSetup(data[1])
		]);
	},

	refresh() {
		return Promise.all([
			zc.run(zc.callStatus).catch(err => err),
			zc.run(zc.callJobStatus).then(res => res.job ?? null, () => null)
		]).then(data => {
			this.status = data[0];
			this.renderStatus(data[0], data[1]);
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

	renderStatus(st, job) {
		if (st instanceof Error) {
			dom.content(this.statusNode, zc.section(_('Service state'), null, [
				E('div', { 'class': 'alert-message danger' }, [
					E('p', {}, [ E('strong', {}, txt(_('Could not get the state of zaprett'))) ]),
					E('p', {}, txt(zc.errorMessage(st)))
				]),
				zc.button(_('Try again'), ui.createHandlerFn(this, 'refresh'), 'action')
			]));

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
			this.row(_('Firewall rules'), st.nft_applied ? _('applied') : _('not applied')),
			this.row(_('Internet interfaces'), (Array.isArray(st.wan) && st.wan.length) ? st.wan.join(', ') : _('not detected')),
			this.row(_('Flow offloading'), '%s; %s'.format(
				(offload.fw4 || offload.fw4_hw) ? _('enabled in the firewall') : _('disabled in the firewall'),
				(offload.mode == 'keep') ? _('zaprett does not change it') : _('zaprett turns it off while running'))),
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

		if (job && job.state == 'running') {
			const page = (job.name == 'test') ? 'strategies' : ((job.name == 'sources-update') ? 'lists' : 'repo');

			nodes.unshift(E('div', { 'class': 'alert-message notice' }, [
				E('p', {}, txt(_('Background task: %s, %d%% done.').format(zc.jobLabel(job.name), +job.progress || 0))),
				E('p', {}, [ this.link(_('Show progress'), page) ])
			]));
		}

		const warnings = Array.isArray(st.warnings) ? st.warnings : [];

		dom.content(this.statusNode, [
			zc.section(_('Service state'), null, nodes),
			warnings.length ? zc.section(_('What needs attention'), null, warnings.map(w => this.renderWarning(w, st))) : ''
		]);
	},

	renderWarning(warning, st) {
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
		}

		return E('div', { 'class': 'alert-message warning' }, [
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

	/* --- quick setup --- */

	serviceState(svc) {
		if (svc.works == 'no')
			return { selectable: false, label: _('zaprett cannot help'), style: 'warning' };

		if (svc.available === false)
			return { selectable: false, label: _('lists are not installed'), style: 'notice' };

		if (svc.enabled === true)
			return { selectable: true, label: _('enabled'), style: 'success' };

		if (svc.partially_enabled === true)
			return { selectable: true, label: _('partially enabled'), style: 'notice' };

		return { selectable: true, label: null, style: null };
	},

	renderQuickSetup(presets) {
		if (presets instanceof Error)
			return zc.section(_('Quick setup'), null, [ zc.errorBox(presets, _('Could not load the list of services')) ]);

		const services = Array.isArray(presets.services) ? presets.services : [];
		const defaults = Array.isArray(presets.defaults?.services) ? presets.defaults.services : [];
		const memory = zc.memoryAdvice(presets);
		const anyEnabled = services.some(s => s.enabled === true || s.partially_enabled === true);
		const boxes = [];

		const rows = services.map(svc => {
			const st = this.serviceState(svc);
			const checked = st.selectable && (anyEnabled ? (svc.enabled === true || svc.partially_enabled === true) : defaults.indexOf(svc.id) >= 0);
			const id = 'zaprett-svc-%s'.format(svc.id);
			const box = E('input', { 'type': 'checkbox', 'value': svc.id, 'id': id, 'checked': checked ? '' : null, 'disabled': st.selectable ? null : '' });
			const notes = [];

			if (st.selectable)
				boxes.push(box);

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
					txt(_('Its lists are not installed. Install them from the "Repository" page or set up a subscription on the "Lists" page.'))[0], ' ',
					this.link(_('Open "Lists"'), 'lists')
				]));

			if (svc.note)
				notes.push(E('div', { 'class': 'cbi-value-description' }, [ E('em', {}, txt(svc.note)) ]));

			return E('div', { 'class': 'cbi-value' }, [
				E('label', { 'class': 'cbi-value-title', 'for': id }, txt(svc.name ?? svc.id)),
				E('div', { 'class': 'cbi-value-field' }, [
					box,
					st.label ? E('span', { 'class': 'label %s'.format(st.style), 'style': 'margin-left:.5em' }, txt(st.label)) : '',
					E('div', { 'class': 'cbi-value-description' }, txt(svc.description ?? '')),
					...notes
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

		return zc.section(_('Quick setup'),
			_('Mark the services that do not work well with your provider. zaprett enables ready-made lists of their sites, the always-needed exclusions (government services, banks) and the whitelist mode, so other traffic stays untouched. Lists and subscriptions of the services that are not marked are switched off. If a service needs a subscription, its download starts right away. After that it is worth finding a strategy that works with your provider.'),
			[
				E('div', {}, rows),
				E('div', { 'class': 'cbi-page-actions', 'style': 'text-align:left' }, [
					zc.button(_('Apply the selection'), ui.createHandlerFn(this, 'handleWizard', boxes), 'apply')
				])
			]);
	},

	handleWizard(boxes, ev) {
		const selected = boxes.filter(b => b.checked).map(b => b.value).filter(id => zc.isId(id));

		if (!selected.length) {
			ui.addNotification(null, E('p', {}, txt(_('Mark at least one service.'))), 'warning');
			return Promise.resolve();
		}

		return zc.run(zc.callWizardApply, selected).then(res => {
			this.showWizardDone(res);

			return this.refresh();
		}).catch(err => zc.notifyError(_('Could not enable the lists of the selected services'), err));
	},

	showWizardDone(res) {
		const running = !(this.status instanceof Error) && this.status?.running;
		const skipped = Array.isArray(res?.skipped) ? res.skipped : [];
		const sources = Array.isArray(res?.sources) ? res.sources : [];
		const extra = [];

		if (skipped.length)
			extra.push(E('div', { 'class': 'alert-message warning' }, txt(_('These services are not enabled, because zaprett cannot help with them: %s.')
				.format(skipped.map(s => String(s.id ?? '')).join(', ')))));

		if (sources.length)
			extra.push(E('p', {}, txt(_('Subscriptions are enabled: %s. Their lists are being downloaded; progress is on the "Lists" page, "Subscriptions" tab.').format(sources.join(', ')))));

		if (Array.isArray(res?.sources_disabled) && res.sources_disabled.length)
			extra.push(E('p', {}, txt(_('Subscriptions of the services that are not marked are switched off: %s.').format(res.sources_disabled.join(', ')))));

		if (L.isObject(res?.job_error))
			extra.push(E('div', { 'class': 'alert-message warning' }, txt(_('The subscriptions could not be downloaded right now: %s. Open "Lists" → "Subscriptions" and press "Update now".')
				.format(res.job_error.message || res.job_error.code || ''))));

		ui.showModal(txt(_('Lists are enabled')), [
			E('p', {}, txt(_('The lists of the selected services are enabled, zaprett works in the whitelist mode.'))),
			...extra,
			running
				? E('p', {}, txt(res?.reloaded ? _('zaprett has applied the new lists.') : _('Restart zaprett to apply the new lists.')))
				: E('p', {}, txt(_('zaprett is not running yet. Start it to make the bypass work.'))),
			E('p', {}, txt(_('Different providers block in different ways, so the default strategy may not suit yours. Automatic selection checks strategies one by one and shows which of them open the selected services. It takes several minutes; connections through zaprett may briefly drop during the check.'))),
			E('div', { 'class': 'right' }, [
				zc.button(_('Later'), ui.hideModal, 'neutral'), ' ',
				running ? '' : zc.button(_('Start zaprett'), ui.createHandlerFn(this, () => {
					ui.hideModal();
					return this.handleService('start');
				}), 'positive'), ' ',
				zc.button(_('Find a working strategy'), ui.createHandlerFn(this, 'handleStartTest'), 'action')
			])
		], 'cbi-modal');
	},

	handleStartTest(ev) {
		return zc.run(zc.callTestStart, undefined, true).then(() => {
			ui.hideModal();
			window.location.href = zc.pageUrl('strategies');
		}).catch(err => {
			ui.hideModal();
			zc.notifyError(_('Could not start strategy selection'), err);
		});
	},

	handleSaveApply: null,
	handleSave: null,
	handleReset: null
});
