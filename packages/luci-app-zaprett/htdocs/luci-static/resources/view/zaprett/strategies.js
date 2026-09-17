// SPDX-License-Identifier: MIT
// zaprett: strategies page - current strategy, automatic selection with
// results, list of installed strategies and editor of custom strategies.

'use strict';
'require view';
'require dom';
'require poll';
'require ui';
'require zaprett.common as zc';

const txt = zc.txt;

const EXAMPLE = '--filter-tcp=80,443 ${hostlists} --dpi-desync=fake,multisplit --dpi-desync-split-pos=1,midsld --new\n' +
	'--filter-udp=443 ${hostlists} --dpi-desync=fake --dpi-desync-repeats=6';

return view.extend({
	load() {
		return zc.run(zc.callStatus).catch(err => err).then(status => {
			const engine = (status instanceof Error) ? 'nfqws' : (status.engine || 'nfqws');

			return Promise.all([
				status,
				zc.run(zc.callItems, engine).catch(err => err),
				zc.run(zc.callTestStatus).catch(() => null)
			]);
		});
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
		this.items = (items instanceof Error) ? [] : (Array.isArray(items.items) ? items.items : []);

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
		const name = item?.name ?? status.strategy?.name ?? id;

		return zc.section(_('Current strategy'), null, [
			E('p', {}, [
				E('strong', {}, txt(id ? name : _('not selected'))),
				(id && name != id) ? E('span', {}, txt(' (%s)'.format(id))) : ''
			]),
			item?.description ? E('p', {}, txt(item.description)) : '',
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
		this.testJob = zc.jobBox(L.bind(this.handleTestStop, this));
		this.resultsNode = E('div', {});
		this.startButton = zc.button(_('Start selection'), ui.createHandlerFn(this, 'handleTestStart'), 'apply');
		this.testRunning = (test?.job?.state == 'running');

		if (test?.job)
			this.testJob.update(test.job);

		this.startButton.disabled = this.testRunning;
		this.renderResults(test);

		return zc.section(_('Automatic selection'),
			_('zaprett checks strategies one by one: it restarts the engine with each of them and tries to open the sites of the enabled services and lists. The result shows how many of the checked sites opened. First the sites are checked without the bypass, to see what is actually blocked.'),
			[
				E('div', { 'class': 'cbi-value' }, [
					E('label', { 'class': 'cbi-value-title', 'for': 'zaprett-test-quick' }, txt(_('Quick check'))),
					E('div', { 'class': 'cbi-value-field' }, [
						this.quickBox,
						E('div', { 'class': 'cbi-value-description' }, txt(_('Check only the recommended strategies: much faster. Without this option all installed strategies are checked. If you mark strategies in the table below, only the marked ones are checked.')))
					])
				]),
				E('div', { 'class': 'alert-message notice' }, txt(_('During the check connections through zaprett may briefly drop. After the check the previous strategy is restored; nothing changes until you apply a result.'))),
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

		if (res.finished > 0)
			nodes.push(E('p', {}, txt(_('Checked: %s').format(zc.formatTime(res.finished)))));

		if (res.state == 'cancelled')
			nodes.push(E('div', { 'class': 'alert-message warning' }, txt(_('The check was stopped before all strategies were checked.'))));

		/* the backend keeps details only for the first results, the plugin drops
		 * them completely when the answer would not fit into one ubus message */
		if (test?.targets_trimmed || res.targets_trimmed)
			nodes.push(E('p', { 'class': 'cbi-value-description' }, txt(_('Too many sites were checked, so the details are shown only for the first strategies.'))));

		const best = running ? null : list.filter(r => r.status == 'done' && +r.ok > 0)[0];

		if (best)
			nodes.push(E('div', { 'class': 'alert-message success' }, [
				E('p', {}, txt(_('Best result: "%s", %d of %d sites opened.').format(best.name ?? best.id, +best.ok || 0, +best.total || 0))),
				zc.button(_('Apply the best strategy'), ui.createHandlerFn(this, 'handleApply', best.id, best.name ?? best.id), 'apply')
			]));
		else if (!running && list.length)
			nodes.push(E('div', { 'class': 'alert-message warning' }, txt(_('None of the checked strategies helped. Install more strategies from the "Repository" page and run the selection again, or check that the enabled lists contain the blocked sites.'))));

		const rows = [
			E('tr', { 'class': 'tr table-titles' }, [
				E('th', { 'class': 'th' }, txt(_('Strategy'))),
				E('th', { 'class': 'th' }, txt(_('Sites opened'))),
				E('th', { 'class': 'th' }, txt(_('Average time'))),
				E('th', { 'class': 'th' }, txt(_('Result'))),
				E('th', { 'class': 'th right' }, txt(''))
			])
		];

		for (const r of list) {
			const ratio = Math.round((+r.ratio || 0) * 100);
			const tested = (r.status == 'done');

			rows.push(E('tr', { 'class': 'tr' }, [
				E('td', { 'class': 'td' }, [ E('strong', {}, txt(r.name ?? r.id)), E('br'), E('small', {}, txt(r.id)) ]),
				E('td', { 'class': 'td' }, tested
					? [ E('div', {}, txt('%d / %d (%d%%)'.format(+r.ok || 0, +r.total || 0, ratio))), zc.progressBar(ratio) ]
					: txt('—')),
				E('td', { 'class': 'td' }, txt((+r.avg_ms > 0) ? _('%d ms').format(+r.avg_ms) : '—')),
				E('td', { 'class': 'td' }, [
					E('div', {}, txt(this.resultLabel(r))),
					r.message ? E('small', {}, txt(r.message)) : ''
				]),
				E('td', { 'class': 'td right' }, [
					(Array.isArray(r.targets) && r.targets.length) ? zc.button(_('Details'), ui.createHandlerFn(this, 'showTargets', r), 'neutral') : '', ' ',
					(!running && tested && +r.ok > 0) ? zc.button(_('Apply'), ui.createHandlerFn(this, 'handleApply', r.id, r.name ?? r.id), 'apply') : ''
				])
			]));
		}

		nodes.push(E('table', { 'class': 'table' }, rows));
		dom.content(this.resultsNode, nodes);
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

	targetError(t) {
		const text = {
			too_small: _('the answer was cut off'),
			http_error: _('the server returned an HTTP error'),
			timeout: _('no answer (timeout)'),
			reset: _('the connection was reset'),
			tls_cert: _('wrong certificate (substituted answer)'),
			tls_error: _('encryption error'),
			connect_failed: _('could not connect'),
			local_error: _('check error on the router'),
			failed: _('failed')
		}[t.error] ?? String(t.error ?? '');

		return (t.http_status > 0) ? '%s (HTTP %d)'.format(text, +t.http_status) : text;
	},

	showTargets(r, ev) {
		const targets = Array.isArray(r.targets) ? r.targets : [];
		const rows = [
			E('tr', { 'class': 'tr table-titles' }, [
				E('th', { 'class': 'th' }, txt(_('Address'))),
				E('th', { 'class': 'th' }, txt(_('Opened'))),
				E('th', { 'class': 'th' }, txt(_('Time'))),
				E('th', { 'class': 'th' }, txt(_('Received'))),
				E('th', { 'class': 'th' }, txt(_('Error')))
			])
		];

		for (const t of targets)
			rows.push(E('tr', { 'class': 'tr' }, [
				E('td', { 'class': 'td', 'style': 'word-break:break-all' }, txt(t.url)),
				E('td', { 'class': 'td' }, txt(t.ok ? _('yes') : _('no'))),
				E('td', { 'class': 'td' }, txt((+t.ms > 0) ? _('%d ms').format(+t.ms) : '—')),
				E('td', { 'class': 'td' }, txt(zc.formatBytes(t.bytes))),
				E('td', { 'class': 'td' }, [ E('div', {}, txt(t.ok ? '' : this.targetError(t))), t.detail ? E('small', {}, txt(t.detail)) : '' ])
			]));

		ui.showModal(txt(_('Check details: %s').format(r.name ?? r.id)), [
			E('table', { 'class': 'table' }, rows),
			E('div', { 'class': 'right' }, [ zc.button(_('Close'), ui.hideModal, 'neutral') ])
		], 'cbi-modal');
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
			? _('%d marked strategies will be checked. During the check connections through zaprett may briefly drop. Start?').format(ids.length)
			: (quick ? _('The recommended strategies will be checked. During the check connections through zaprett may briefly drop. Start?')
				: _('All installed strategies will be checked; it may take a long time. During the check connections through zaprett may briefly drop. Start?'));

		return zc.confirm(_('Automatic selection'), text, _('Start'), 'apply').then(ok => {
			if (!ok)
				return;

			return zc.run(zc.callTestStart, ids.length ? ids : undefined, quick).then(res => {
				this.setRunning(true);
				this.testJob.update(Object.assign({ state: 'running', progress: 0, message: '' }, res.job ?? { name: 'test' }));
				dom.content(this.resultsNode, E('p', {}, txt(_('Waiting for the first results…'))));
				this.watchTest();
			}).catch(err => zc.notifyError(_('Could not start strategy selection'), err));
		});
	},

	handleTestStop(ev) {
		return zc.cancelJob(zc.callTestStop);
	},

	watchTest() {
		if (this.testPoll)
			return;

		let errors = 0;

		const stop = () => {
			if (this.testPoll) {
				poll.remove(this.testPoll);
				window.removeEventListener('pagehide', stop);
				this.testPoll = null;
			}
		};

		this.testPoll = () => zc.run(zc.callTestStatus).then(res => {
			errors = 0;

			const running = res.job?.state == 'running';

			if (res.job)
				this.testJob.update(res.job);

			this.setRunning(running);
			this.renderResults(res);

			if (!running) {
				stop();

				if (res.job?.state == 'done')
					zc.notifyInfo(_('Strategy selection is finished. Choose a result to apply.'));
				else if (res.job?.state == 'failed')
					ui.addNotification(null, E('p', {}, txt(_('Strategy selection failed: %s').format(res.job.message ?? ''))), 'danger');
			}
		}).catch(err => {
			if (++errors >= 5) {
				stop();
				this.setRunning(false);
				zc.notifyError(_('Lost track of the background task'), err);
			}
		});

		window.addEventListener('pagehide', stop);
		poll.add(this.testPoll, 3);
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
				this.items = (items instanceof Error) ? [] : (Array.isArray(items.items) ? items.items : []);

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

		const header = E('tr', { 'class': 'tr table-titles' }, [
			E('th', { 'class': 'th', 'style': 'width:2em' }, txt('')),
			E('th', { 'class': 'th' }, txt(_('Strategy'))),
			E('th', { 'class': 'th' }, txt(_('Author'))),
			E('th', { 'class': 'th' }, txt(_('Version'))),
			E('th', { 'class': 'th' }, txt(_('Source'))),
			E('th', { 'class': 'th right' }, txt(''))
		]);

		const sorted = this.items.slice().sort((a, b) => String(a.name ?? a.id).localeCompare(String(b.name ?? b.id)));

		for (const it of sorted) {
			const active = (it.id == current);
			const isUser = (it.source == 'user');
			const box = E('input', { 'type': 'checkbox', 'title': _('Check this strategy during automatic selection'), 'checked': this.selected[it.id] ? '' : null,
				'change': ev => { this.selected[it.id] = ev.target.checked; } });

			const row = E('tr', { 'class': 'tr' }, [
				E('td', { 'class': 'td' }, [ box ]),
				E('td', { 'class': 'td' }, [
					E('strong', {}, txt(it.name ?? it.id)),
					active ? E('span', { 'class': 'label success', 'style': 'margin-left:.5em' }, txt(_('in use'))) : '',
					E('br'),
					E('small', {}, txt(it.id)),
					it.description ? E('div', { 'class': 'cbi-value-description' }, txt(it.description)) : ''
				]),
				E('td', { 'class': 'td' }, txt(it.author ?? '')),
				E('td', { 'class': 'td' }, txt(it.version ?? '')),
				E('td', { 'class': 'td' }, txt(zc.sourceLabel(it.source))),
				E('td', { 'class': 'td right' }, [
					active ? '' : zc.button(_('Use'), ui.createHandlerFn(this, 'handleUse', it), 'apply'), ' ',
					zc.button(_('Show'), ui.createHandlerFn(this, 'handleShow', it.id), 'neutral'), ' ',
					isUser ? zc.button(_('Edit'), ui.createHandlerFn(this, 'handleEdit', it.id), 'edit') : '', ' ',
					(isUser && !active) ? zc.button(_('Delete'), ui.createHandlerFn(this, 'handleDelete', it), 'remove') : ''
				])
			]);

			row.setAttribute('data-search', [ it.id, it.name, it.description, it.author ].join(' ').toLowerCase());
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
