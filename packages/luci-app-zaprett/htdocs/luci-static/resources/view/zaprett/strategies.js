// SPDX-License-Identifier: MIT
// zaprett: strategies page - current strategy, automatic selection with
// results, list of installed strategies and editor of custom strategies.

'use strict';
'require view';
'require dom';
'require ui';
'require zaprett.common as zc';

const txt = zc.txt;

const EXAMPLE = '--filter-tcp=80,443 ${hostlists} --dpi-desync=fake,multisplit --dpi-desync-split-pos=1,midsld --new\n' +
	'--filter-udp=443 ${hostlists} --dpi-desync=fake --dpi-desync-repeats=6';

return view.extend({
	load() {
		return zc.loadPage('strategies', {
			status: () => zc.run(zc.callStatus),
			items: () => zc.run(zc.callItems),
			test: () => this.testStatus()
		}).then(data => [ data.status, data.items, (data.test instanceof Error) ? null : data.test ]);
	},

	/* Progress of the selection without per-address details (--brief); an
	 * older service does not know the flag and gets the full answer. */
	testStatus() {
		return zc.run(zc.callTestStatus, true).catch(err => {
			if (!zc.isUsageError(err))
				throw err;

			return zc.run(zc.callTestStatus);
		});
	},

	/* Strategies of the current engine: "page" returns items of all types. */
	engineItems(items, engine) {
		const list = (items instanceof Error) ? [] : (Array.isArray(items.items) ? items.items : []);

		return list.filter(it => it.type == null || it.type == engine);
	},

	render(data) {
		this.root = E('div', {});
		this.selected = {};
		this.applyData(data);

		return E([], [
			E('h2', {}, txt(_('Strategies'))),
			E('div', { 'class': 'cbi-map-descr' }, txt(_('A strategy is a set of engine options that describes how to modify the first packets of a connection so that the provider equipment does not recognize the site. Providers block differently, so a strategy that works for one may not work for another. The easiest way is to run automatic selection.'))),
			this.root
		]);
	},

	applyData(data) {
		const status = data[0], items = data[1], test = data[2];

		this.status = status;
		this.engine = (status instanceof Error) ? 'nfqws' : (status.engine || 'nfqws');
		this.items = this.engineItems(items, this.engine);

		dom.content(this.root, [
			this.renderCurrent(status),
			this.renderTest(test),
			this.renderList(items)
		]);

		if (test?.job?.state == 'running')
			this.watchTest();
	},

	currentId() {
		if (this.status instanceof Error)
			return null;

		return L.isObject(this.status.strategy) ? this.status.strategy.id : this.status.strategy;
	},

	findItem(id) {
		return this.items.filter(it => it.id == id)[0];
	},

	/* --- current strategy --- */

	renderCurrent(status) {
		if (status instanceof Error)
			return zc.section(_('Current strategy'), null, [ zc.errorBox(status, _('Could not get the state of zaprett')) ]);

		const id = this.currentId();
		const item = this.findItem(id);
		const name = zc.localized(item, 'name') ?? status.strategy?.name ?? id;
		const description = zc.localized(item, 'description');

		return zc.section(_('Current strategy'), null, [
			E('p', {}, [
				E('strong', {}, txt(id ? name : _('not selected'))),
				(id && name != id) ? E('span', {}, txt(' (%s)'.format(id))) : ''
			]),
			description ? E('p', {}, txt(description)) : '',
			E('p', {}, txt(_('Engine: %s').format(this.engine))),
			E('div', {}, [
				id ? zc.button(_('Show'), ui.createHandlerFn(this, 'handleShow', id), 'action') : '', ' ',
				zc.button(_('Check configuration'), ui.createHandlerFn(this, 'handleCheck'), 'action')
			])
		]);
	},

	/* --- automatic selection --- */

	renderTest(test) {
		this.quickBox = E('input', { 'type': 'checkbox', 'id': 'zaprett-test-quick', 'checked': '' });
		this.autoApplyBox = E('input', { 'type': 'checkbox', 'id': 'zaprett-test-apply' });
		this.testJob = zc.jobBox(L.bind(this.handleTestStop, this));
		this.resultsNode = E('div', {});
		this.startButton = zc.button(_('Start selection'), ui.createHandlerFn(this, 'handleTestStart'), 'apply');
		this.testRunning = (test?.job?.state == 'running');

		if (test?.job)
			this.testJob.update(test.job);

		this.startButton.disabled = this.testRunning;
		this.renderResults(test);

		return zc.section(_('Automatic selection'),
			_('zaprett checks strategies one by one with a separate copy of the engine that handles only the check requests of the router itself, and tries to open the sites of the enabled services and lists. The devices in your network keep using the bypass with the current strategy meanwhile. The result shows how many of the checked sites opened. First the sites are checked without the bypass, to see what is actually blocked.'),
			[
				E('div', { 'class': 'cbi-value' }, [
					E('label', { 'class': 'cbi-value-title', 'for': 'zaprett-test-quick' }, txt(_('Quick check'))),
					E('div', { 'class': 'cbi-value-field' }, [
						this.quickBox,
						E('div', { 'class': 'cbi-value-description' }, txt(_('Check only the recommended strategies: much faster. Without this option all installed strategies are checked. If you mark strategies in the table below, only the marked ones are checked.')))
					])
				]),
				E('div', { 'class': 'cbi-value' }, [
					E('label', { 'class': 'cbi-value-title', 'for': 'zaprett-test-apply' }, txt(_('Apply the best automatically'))),
					E('div', { 'class': 'cbi-value-field' }, [
						this.autoApplyBox,
						E('div', { 'class': 'cbi-value-description' }, txt(_('The current strategy is always checked too. When the check ends, the best strategy is applied if it opened more sites than the current one; otherwise nothing changes.')))
					])
				]),
				E('div', { 'class': 'alert-message notice' }, txt(_('The network is not left without the bypass during the check. Only when this is impossible (for example, the service is stopped) the engine is stopped for the time of the check, and the result says why. Nothing changes until you apply a result.'))),
				E('div', {}, [ this.startButton ]),
				this.testJob.node,
				this.resultsNode
			]);
	},

	renderResults(test) {
		const res = L.isObject(test?.results) ? test.results : null;
		const list = Array.isArray(res?.results) ? res.results : [];
		const running = test?.job?.state == 'running';

		if (!res || (!list.length && !res.baseline)) {
			dom.content(this.resultsNode, running ? E('p', {}, txt(_('Waiting for the first results…'))) : '');
			return;
		}

		const nodes = [];
		const base = L.isObject(res.baseline) ? res.baseline : null;

		if (base && +base.total > 0) {
			nodes.push(E('p', {}, txt(_('Without the bypass %d of %d checked sites opened.').format(+base.ok || 0, +base.total))));

			if (base.note == 'no_blocking_detected' || +base.ok >= +base.total)
				nodes.push(E('div', { 'class': 'alert-message warning' }, txt(_('All checked sites open even without zaprett. Either the provider does not block them now, or the checked sites are not representative, so the comparison below says little.'))));
		}

		/* contract v1.6 §17: mode isolated keeps the bypass for the network, exclusive stops the engine */
		if (res.mode == 'isolated')
			nodes.push(E('p', {}, txt(_('The check ran next to the working bypass: the network was not left without it.'))));
		else if (res.mode == 'exclusive')
			nodes.push(E('div', { 'class': 'alert-message warning' },
				txt(_('The engine was stopped for the time of the check: %s.').format(this.modeReason(res.mode_reason)))));

		if (res.finished > 0)
			nodes.push(E('p', {}, txt(_('Checked: %s').format(zc.formatTime(res.finished)))));

		if (res.state == 'cancelled')
			nodes.push(E('div', { 'class': 'alert-message warning' }, txt(_('The check was stopped before all strategies were checked.'))));

		if (res.applied) {
			const item = list.filter(r => r.id == res.applied)[0];

			nodes.push(E('div', { 'class': 'alert-message success' },
				txt(_('Strategy "%s" opened more sites than the previous one and is applied.').format(item?.name ?? res.applied))));
		}

		/* the backend keeps details only for the first results, the plugin drops
		 * them completely when the answer would not fit into one ubus message */
		if (test?.targets_trimmed || res.targets_trimmed)
			nodes.push(E('p', { 'class': 'cbi-value-description' }, txt(_('Too many sites were checked, so the details are shown only for the first strategies.'))));

		const best = running ? null : list.filter(r => r.status == 'done' && +r.ok > 0)[0];

		/* an automatically applied best result is already announced above */
		if (best && best.id != res.applied)
			nodes.push(E('div', { 'class': 'alert-message success' }, [
				E('p', {}, txt(_('Best result: "%s", %d of %d sites opened.').format(best.name ?? best.id, +best.ok || 0, +best.total || 0))),
				zc.button(_('Apply the best strategy'), ui.createHandlerFn(this, 'handleApply', best.id, best.name ?? best.id), 'apply')
			]));
		else if (!running && list.length)
			nodes.push(E('div', { 'class': 'alert-message warning' }, txt(_('None of the checked strategies helped. Install more strategies from the "Repository" page and run the selection again, or check that the enabled lists contain the blocked sites.'))));

		const titles = [ _('Strategy'), _('Sites opened'), _('Average time'), _('Result') ];
		const rows = [
			E('tr', { 'class': 'tr table-titles' }, [
				...titles.map(t => E('th', { 'class': 'th' }, txt(t))),
				E('th', { 'class': 'th right' }, txt(''))
			])
		];

		for (const r of list) {
			const ratio = Math.round((+r.ratio || 0) * 100);
			const tested = (r.status == 'done');
			/* a --brief answer has no targets at all: they are loaded by the button */
			const details = tested && (!Array.isArray(r.targets) || r.targets.length > 0);

			rows.push(E('tr', { 'class': 'tr' }, [
				zc.td(titles[0], [ E('strong', {}, txt(r.name ?? r.id)), E('br'), E('small', {}, txt(r.id)) ]),
				zc.td(titles[1], tested
					? [ E('div', {}, txt('%d / %d (%d%%)'.format(+r.ok || 0, +r.total || 0, ratio))), zc.progressBar(ratio) ]
					: '—'),
				zc.td(titles[2], (+r.avg_ms > 0) ? _('%d ms').format(+r.avg_ms) : '—'),
				zc.td(titles[3], [
					E('div', {}, txt(this.resultLabel(r))),
					r.message ? E('small', {}, txt(r.message)) : ''
				]),
				zc.td(null, [
					details ? zc.button(_('Details'), ui.createHandlerFn(this, 'showTargets', r), 'neutral') : '', ' ',
					(!running && tested && +r.ok > 0 && r.id != res.applied) ? zc.button(_('Apply'), ui.createHandlerFn(this, 'handleApply', r.id, r.name ?? r.id), 'apply') : ''
				], 'right')
			]));
		}

		nodes.push(E('table', { 'class': 'table' }, rows));
		dom.content(this.resultsNode, nodes);
	},

	modeReason(reason) {
		switch (reason) {
		case 'forced':
			return _('this mode was requested');

		case 'engine_not_running':
			return _('the service was not running');

		case 'no_test_user':
			return _('the system user zaprett-test is missing');

		case 'nft_rejected':
			return _('the firewall did not accept the check rules');

		case 'instance_failed':
			return _('the second copy of the engine did not start');

		default:
			return _('a separate check was not possible (%s)').format(reason ?? '?');
		}
	},

	resultLabel(r) {
		switch (r.status) {
		case 'invalid':
			return _('rejected by the engine');

		case 'start_failed':
		case 'engine_failed':
			return _('the engine did not start');

		case 'done':
			if (+r.ratio >= 1)
				return _('works');

			return (+r.ok > 0) ? _('works partially') : _('does not help');

		default:
			return _('not checked');
		}
	},

	/* Per-address details: present in the full answer of "test status" and
	 * loaded on demand when the page polls with --brief. */
	showTargets(r, ev) {
		const load = Array.isArray(r.targets)
			? Promise.resolve(r.targets)
			: zc.run(zc.callTestStatus).then(res => {
				const list = Array.isArray(res.results?.results) ? res.results.results : [];
				const full = list.filter(x => x.id == r.id)[0];

				return Array.isArray(full?.targets) ? full.targets : null;
			});

		return load.then(targets => {
			ui.showModal(txt(_('Check details: %s').format(r.name ?? r.id)), [
				targets
					? zc.targetsTable(targets)
					: E('p', {}, txt(_('Too many sites were checked, so the details are shown only for the first strategies.'))),
				E('div', { 'class': 'right' }, [ zc.button(_('Close'), ui.hideModal, 'neutral') ])
			], 'cbi-modal');
		}).catch(err => zc.notifyError(_('Could not load the details'), err));
	},

	selectedIds() {
		return Object.keys(this.selected).filter(id => this.selected[id] && this.findItem(id));
	},

	setRunning(running) {
		this.testRunning = running;

		/* createHandlerFn() re-enables the clicked button after the handler,
		 * so the state is applied again on the next tick */
		this.startButton.disabled = running;
		window.setTimeout(() => { this.startButton.disabled = this.testRunning; }, 0);
	},

	handleTestStart(ev) {
		const ids = this.selectedIds();
		const quick = !ids.length && this.quickBox.checked;
		const text = ids.length
			? _('%d marked strategies will be checked. The bypass keeps working for the network meanwhile. Start?').format(ids.length)
			: (quick ? _('The recommended strategies will be checked. The bypass keeps working for the network meanwhile. Start?')
				: _('All installed strategies will be checked; it may take a long time. The bypass keeps working for the network meanwhile. Start?'));

		return zc.confirm(_('Automatic selection'), text, _('Start'), 'apply').then(ok => {
			if (!ok)
				return;

			return this.startTest(ids.length ? ids : undefined, quick, this.autoApplyBox.checked).then(res => {
				this.setRunning(true);
				this.testJob.update(Object.assign({ state: 'running', progress: 0, message: '' }, res.job ?? { name: 'test' }));
				dom.content(this.resultsNode, E('p', {}, txt(_('Waiting for the first results…'))));
				this.watchTest();
			}).catch(err => zc.notifyError(_('Could not start strategy selection'), err));
		});
	},

	/* --apply-if-better is sent only when asked for; an older service does
	 * not know it, then the selection starts without it. */
	startTest(ids, quick, autoApply) {
		if (!autoApply)
			return zc.run(zc.callTestStart, ids, quick);

		return zc.run(zc.callTestStart, ids, quick, true).catch(err => {
			if (!zc.isUsageError(err))
				throw err;

			zc.notifyInfo(_('The installed zaprett service cannot apply the best strategy by itself: apply it from the results when the check ends.'));

			return zc.run(zc.callTestStart, ids, quick);
		});
	},

	handleTestStop(ev) {
		return zc.cancelJob(zc.callTestStop);
	},

	/* One brief "test status" per tick while the selection runs. */
	watchTest() {
		if (this.stopTestWatch)
			return;

		let errors = 0;

		const stop = () => {
			const stopPoll = this.stopTestWatch;

			this.stopTestWatch = null;

			if (stopPoll)
				stopPoll();
		};

		this.stopTestWatch = zc.pollVisible(() => this.testStatus().then(res => {
			errors = 0;

			const running = res.job?.state == 'running';

			if (res.job)
				this.testJob.update(res.job);

			this.setRunning(running);
			this.renderResults(res);

			if (!running) {
				stop();

				if (res.job?.state == 'done' && res.results?.applied)
					zc.notifyInfo(_('Strategy selection is finished, the best strategy is applied.'));
				else if (res.job?.state == 'done')
					zc.notifyInfo(_('Strategy selection is finished. Choose a result to apply.'));
				else if (res.job?.state == 'failed')
					ui.addNotification(null, E('p', {}, txt(_('Strategy selection failed: %s').format(res.job.message ?? ''))), 'danger');

				if (res.results?.applied)
					return this.reloadCurrent();
			}
		}).catch(err => {
			if (++errors >= 5) {
				stop();
				this.setRunning(false);
				zc.notifyError(_('Lost track of the background task'), err);
			}
		}), 3);
	},

	handleApply(id, name, ev) {
		return zc.run(zc.callTestApply, id).then(res => {
			zc.notifyInfo((res.restarted === true || res.reloaded === true)
				? _('Strategy "%s" is selected and applied.').format(name)
				: _('Strategy "%s" is selected. It will be used when zaprett starts.').format(name));

			return this.reloadCurrent();
		}).catch(err => zc.notifyError(_('Could not apply the strategy'), err));
	},

	/* Refreshes the state and the list without touching the selection box. */
	reloadCurrent() {
		return zc.run(zc.callStatus).catch(err => err).then(status => {
			const engine = (status instanceof Error) ? this.engine : (status.engine || 'nfqws');

			return zc.run(zc.callItems, engine).catch(err => err).then(items => {
				this.status = status;
				this.engine = engine;
				this.items = this.engineItems(items, engine);

				const current = this.renderCurrent(status);
				const list = this.renderList(items);

				this.root.replaceChild(current, this.root.firstChild);
				this.root.replaceChild(list, this.root.lastChild);
			});
		});
	},

	/* --- installed strategies --- */

	renderList(items) {
		if (items instanceof Error)
			return zc.section(_('Installed strategies'), null, [ zc.errorBox(items, _('Could not load the strategy list')) ]);

		const current = this.currentId();
		const search = E('input', { 'type': 'search', 'class': 'cbi-input-text', 'placeholder': _('Search by name or description') });
		const rows = [];
		const table = E('table', { 'class': 'table' });

		const titles = [ _('Strategy'), _('Author'), _('Version'), _('Source') ];
		const header = E('tr', { 'class': 'tr table-titles' }, [
			E('th', { 'class': 'th', 'style': 'width:2em' }, txt('')),
			...titles.map(t => E('th', { 'class': 'th' }, txt(t))),
			E('th', { 'class': 'th right' }, txt(''))
		]);

		const nameOf = it => zc.localized(it, 'name') ?? it.id;
		const sorted = this.items.slice().sort((a, b) => String(nameOf(a)).localeCompare(String(nameOf(b))));

		for (const it of sorted) {
			const active = (it.id == current);
			const isUser = (it.source == 'user');
			const description = zc.localized(it, 'description');
			const box = E('input', { 'type': 'checkbox', 'title': _('Check this strategy during automatic selection'), 'checked': this.selected[it.id] ? '' : null,
				'change': ev => { this.selected[it.id] = ev.target.checked; } });

			const row = E('tr', { 'class': 'tr' }, [
				zc.td(_('Check'), [ box ]),
				zc.td(titles[0], [
					E('strong', {}, txt(nameOf(it))),
					active ? E('span', { 'class': 'label success', 'style': 'margin-left:.5em' }, txt(_('in use'))) : '',
					E('br'),
					E('small', {}, txt(it.id)),
					description ? E('div', { 'class': 'cbi-value-description' }, txt(description)) : ''
				]),
				zc.td(titles[1], it.author ?? ''),
				zc.td(titles[2], it.version ?? ''),
				zc.td(titles[3], zc.sourceLabel(it.source)),
				zc.td(null, [
					active ? '' : zc.button(_('Use'), ui.createHandlerFn(this, 'handleUse', it), 'apply'), ' ',
					zc.button(_('Show'), ui.createHandlerFn(this, 'handleShow', it.id), 'neutral'), ' ',
					isUser ? zc.button(_('Edit'), ui.createHandlerFn(this, 'handleEdit', it.id), 'edit') : '', ' ',
					(isUser && !active) ? zc.button(_('Delete'), ui.createHandlerFn(this, 'handleDelete', it), 'remove') : ''
				], 'right')
			]);

			row.setAttribute('data-search', [ it.id, it.name, it.name_en, description, it.author ].join(' ').toLowerCase());
			rows.push(row);
		}

		dom.content(table, [ header, ...rows ]);

		search.addEventListener('input', () => {
			const q = search.value.trim().toLowerCase();

			for (const row of rows)
				row.style.display = (!q || row.getAttribute('data-search').indexOf(q) >= 0) ? '' : 'none';
		});

		return zc.section(_('Installed strategies'),
			_('Strategies for the %s engine. More strategies can be installed on the "Repository" page, or you can write your own.').format(this.engine),
			[
				E('div', { 'style': 'display:flex;gap:.5em;flex-wrap:wrap;margin-bottom:.5em' }, [
					search,
					zc.button(_('Create custom strategy'), ui.createHandlerFn(this, 'handleEdit', null), 'add')
				]),
				rows.length ? table : E('p', {}, txt(_('No strategies are installed for this engine.')))
			]);
	},

	handleUse(item, ev) {
		const name = item.name ?? item.id;

		return zc.run(zc.callSetStrategy, item.id).then(res => {
			const running = !(this.status instanceof Error) && this.status.running;

			if (res.reloaded === true || res.restarted === true)
				zc.notifyInfo(_('Strategy "%s" is selected and applied.').format(name));
			else if (!running)
				zc.notifyInfo(_('Strategy "%s" is selected. It will be used when zaprett starts.').format(name));
			else
				zc.notifyInfo(_('Strategy "%s" is selected. Restart zaprett to apply it.').format(name), [
					zc.button(_('Restart'), ui.createHandlerFn(this, 'handleRestart'), 'reload')
				]);

			return this.reloadCurrent();
		}).catch(err => zc.notifyError(_('Could not select the strategy'), err));
	},

	handleRestart(ev) {
		return zc.run(zc.callService, 'restart')
			.then(() => zc.notifyInfo(_('zaprett is restarted.')))
			.catch(err => zc.notifyError(_('Could not restart zaprett'), err));
	},

	handleShow(id, ev) {
		return zc.run(zc.callStrategyShow, id).then(res => {
			const ports = L.isObject(res.ports) ? res.ports : null;
			const pre = 'white-space:pre-wrap;word-break:break-all;max-height:20em;overflow:auto';

			ui.showModal(txt(_('Strategy "%s"').format(res.name ?? id)), [
				res.description ? E('p', {}, txt(res.description)) : '',
				E('h5', {}, txt(_('Strategy text'))),
				E('pre', { 'style': pre }, txt(res.text ?? '')),
				Array.isArray(res.args)
					? E('div', {}, [
						E('h5', {}, txt(_('Engine arguments with the lists substituted'))),
						E('pre', { 'style': pre }, txt(res.args.join('\n')))
					])
					: E('div', { 'class': 'alert-message warning' }, txt(res.build_message
						? _('The strategy cannot be used with the current settings: %s').format(res.build_message)
						: _('The engine arguments could not be built for this strategy.'))),
				ports ? E('p', {}, txt(_('Processed ports: TCP %s; UDP %s').format(
					(ports.tcp ?? []).join(', ') || _('none'), (ports.udp ?? []).join(', ') || _('none')))) : '',
				E('div', { 'class': 'right' }, [
					(res.source == 'user') ? zc.button(_('Edit'), ui.createHandlerFn(this, 'handleEdit', id), 'edit') : '', ' ',
					zc.button(_('Close'), ui.hideModal, 'neutral')
				])
			], 'cbi-modal');
		}).catch(err => zc.notifyError(_('Could not show the strategy'), err));
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

	handleEdit(id, ev) {
		if (!id)
			return Promise.resolve(this.openEditor(null, ''));

		return zc.run(zc.callStrategyShow, id)
			.then(res => this.openEditor(id, res.text ?? ''))
			.catch(err => zc.notifyError(_('Could not load the strategy'), err));
	},

	openEditor(id, text) {
		const isNew = !id;
		const nameInput = E('input', {
			'type': 'text', 'class': 'cbi-input-text', 'maxlength': '91', 'placeholder': 'my-strategy',
			'readonly': isNew ? null : ''
		});
		const area = E('textarea', { 'class': 'cbi-input-textarea', 'rows': '12', 'wrap': 'off', 'spellcheck': 'false', 'style': 'width:100%;font-family:monospace' });
		const useBox = E('input', { 'type': 'checkbox', 'id': 'zaprett-use-saved' });
		const errNode = E('div', {});

		nameInput.value = isNew ? '' : id.replace(/^user-/, '');
		area.value = text;

		const save = () => {
			const suffix = nameInput.value.trim();
			const newId = 'user-' + suffix;
			const body = area.value.replace(/\r\n/g, '\n');

			dom.content(errNode, '');

			if (!/^[A-Za-z0-9._-]{1,91}$/.test(suffix))
				return dom.content(errNode, E('div', { 'class': 'alert-message warning' }, txt(_('Enter a name: Latin letters, digits, dot, dash and underscore.'))));

			if (isNew && this.findItem(newId))
				return dom.content(errNode, E('div', { 'class': 'alert-message warning' }, txt(_('A strategy with this name already exists.'))));

			if (!body.trim())
				return dom.content(errNode, E('div', { 'class': 'alert-message warning' }, txt(_('The strategy text is empty.'))));

			if (zc.textBytes(body) > zc.MAX_STRATEGY_BYTES)
				return dom.content(errNode, E('div', { 'class': 'alert-message warning' }, txt(_('The text is too large (maximum %d KiB).').format(zc.MAX_STRATEGY_BYTES / 1024))));

			return zc.run(zc.callStrategySave, newId, body)
				.then(() => useBox.checked ? zc.run(zc.callSetStrategy, newId) : null)
				.then(res => {
					ui.hideModal();
					zc.notifyInfo(useBox.checked ? _('Strategy "%s" is saved and selected.').format(newId) : _('Strategy "%s" is saved.').format(newId));

					return this.reloadCurrent();
				})
				.catch(err => dom.content(errNode, zc.errorBox(err, _('The strategy is not saved'))));
		};

		ui.showModal(txt(isNew ? _('New custom strategy') : _('Edit strategy "%s"').format(id)), [
			E('div', { 'class': 'cbi-value' }, [
				E('label', { 'class': 'cbi-value-title' }, txt(_('Name'))),
				E('div', { 'class': 'cbi-value-field' }, [
					E('span', {}, txt('user-')), nameInput,
					E('div', { 'class': 'cbi-value-description' }, txt(_('Latin letters, digits, dot, dash and underscore.')))
				])
			]),
			E('p', {}, txt(_('The strategy is saved for the %s engine and checked by the engine before saving.').format(this.engine))),
			E('p', {}, txt(_('Write the engine options separated by spaces or line breaks. Put ${hostlists} where the enabled domain lists must be substituted and ${ipsets} for the IP network lists; --new starts the next profile. "--comment" skips the words after it up to the next option. Example:'))),
			E('pre', { 'style': 'white-space:pre-wrap;word-break:break-all' }, txt(EXAMPLE)),
			area,
			E('p', {}, [ useBox, ' ', E('label', { 'for': 'zaprett-use-saved' }, txt(_('Use this strategy after saving'))) ]),
			errNode,
			E('div', { 'class': 'right' }, [
				zc.button(_('Cancel'), ui.hideModal, 'neutral'), ' ',
				zc.button(_('Check and save'), ui.createHandlerFn(this, save), 'save')
			])
		], 'cbi-modal');
	},

	handleDelete(item, ev) {
		return zc.confirm(_('Delete strategy'), _('Delete the custom strategy "%s"? This cannot be undone.').format(item.id), _('Delete')).then(ok => {
			if (!ok)
				return;

			return zc.run(zc.callStrategyDelete, item.id).then(() => {
				delete this.selected[item.id];
				zc.notifyInfo(_('Strategy "%s" is deleted.').format(item.id));

				return this.reloadCurrent();
			}).catch(err => zc.notifyError(_('Could not delete the strategy'), err));
		});
	},

	handleSaveApply: null,
	handleSave: null,
	handleReset: null
});
