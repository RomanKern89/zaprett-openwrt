// SPDX-License-Identifier: MIT
// zaprett: repository page - catalog of strategies, lists and helper files,
// install, update and remove with background job progress.

'use strict';
'require view';
'require dom';
'require ui';
'require uci';
'require zaprett.common as zc';

const txt = zc.txt;

const TYPE_ORDER = [ 'nfqws', 'nfqws2', 'list', 'list_exclude', 'ipset', 'ipset_exclude', 'bin', 'lua_lib', 'byedpi' ];

return view.extend({
	load() {
		return Promise.all([
			zc.run(zc.callRepoList).catch(err => err),
			zc.run(zc.callJobStatus).then(res => res.job ?? null, () => null),
			uci.load('zaprett').catch(() => null)
		]);
	},

	render(data) {
		this.selected = {};
		this.busy = false;
		this.filter = { query: '', type: '', installed: false, updates: false };

		this.infoNode = E('div', {});
		this.listNode = E('div', {});
		this.job = zc.jobBox(L.bind(this.handleCancel, this));

		this.applyList(data[0]);

		if (data[1]) {
			this.job.update(data[1]);

			if (data[1].state == 'running')
				this.watch(data[1]);
		}

		return E([], [
			E('h2', {}, txt(_('Repository'))),
			E('div', { 'class': 'cbi-map-descr' }, txt(_('The zaprett repository contains ready-made strategies, site lists and helper files (fake packets). Installed items are kept on the router and survive firmware updates. Downloading needs access to the repository server.'))),
			this.infoNode,
			this.job.node,
			this.renderFilters(),
			this.listNode
		]);
	},

	applyList(res) {
		this.error = (res instanceof Error) ? res : null;
		this.items = (res instanceof Error) ? [] : (Array.isArray(res.items) ? res.items : []);
		this.fetchedAt = (res instanceof Error) ? 0 : +res.fetched_at || 0;
		this.catalogUrl = (res instanceof Error) ? null : res.url;

		for (const id of Object.keys(this.selected))
			if (!this.items.some(it => it.id == id && !it.installed))
				delete this.selected[id];

		this.renderInfo();
		this.renderList();
	},

	refresh() {
		return zc.run(zc.callRepoList).catch(err => err).then(res => this.applyList(res));
	},

	renderInfo() {
		const url = this.catalogUrl || uci.get('zaprett', 'repo', 'url') || '';
		const updates = this.items.filter(it => it.update_available === true).length;

		this.updateAllButton = zc.button(_('Update all'), ui.createHandlerFn(this, 'handleUpgrade', null), 'apply',
			{ 'disabled': (this.busy || !updates) ? '' : null });
		this.fetchButton = zc.button(_('Update catalog'), ui.createHandlerFn(this, 'handleFetch'), 'reload',
			{ 'disabled': this.busy ? '' : null });

		dom.content(this.infoNode, zc.section(_('Catalog'), null, [
			E('p', {}, txt(_('Catalog address: %s').format(url || _('default')))),
			E('p', {}, txt(this.fetchedAt ? _('Catalog loaded: %s').format(zc.formatTime(this.fetchedAt)) : _('The catalog has not been loaded yet.'))),
			updates ? E('p', {}, txt(_('Updates available: %d').format(updates))) : '',
			this.error ? zc.errorBox(this.error, _('Could not read the catalog')) : '',
			E('div', {}, [ this.fetchButton, ' ', this.updateAllButton ])
		]));
	},

	renderFilters() {
		const search = E('input', { 'type': 'search', 'class': 'cbi-input-text', 'placeholder': _('Search by name, author or description') });
		const type = E('select', { 'class': 'cbi-input-select' }, [ E('option', { 'value': '' }, txt(_('All types'))) ]);
		const installed = E('input', { 'type': 'checkbox', 'id': 'zaprett-repo-installed' });
		const updates = E('input', { 'type': 'checkbox', 'id': 'zaprett-repo-updates' });

		for (const t of TYPE_ORDER)
			type.appendChild(E('option', { 'value': t }, txt(zc.typeLabel(t))));

		const apply = () => {
			this.filter = {
				query: search.value.trim().toLowerCase(),
				type: type.value,
				installed: installed.checked,
				updates: updates.checked
			};

			this.renderList();
		};

		search.addEventListener('input', apply);
		type.addEventListener('change', apply);
		installed.addEventListener('change', apply);
		updates.addEventListener('change', apply);

		this.installSelectedButton = zc.button(_('Install selected'), ui.createHandlerFn(this, 'handleInstallSelected'), 'apply', { 'disabled': '' });

		return E('div', { 'class': 'cbi-section' }, [
			E('div', { 'style': 'display:flex;gap:1em;flex-wrap:wrap;align-items:center' }, [
				search, type,
				E('label', {}, [ installed, ' ', E('span', {}, txt(_('Installed only'))) ]),
				E('label', {}, [ updates, ' ', E('span', {}, txt(_('With updates only'))) ]),
				this.installSelectedButton
			])
		]);
	},

	matches(it) {
		const f = this.filter;

		if (f.type && it.type != f.type)
			return false;

		if (f.installed && !it.installed)
			return false;

		if (f.updates && it.update_available !== true)
			return false;

		if (f.query && [ it.id, it.name, it.name_en, it.author, it.description, it.description_en ].join(' ').toLowerCase().indexOf(f.query) < 0)
			return false;

		return true;
	},

	renderList() {
		if (!this.items.length) {
			dom.content(this.listNode, zc.section(null, null, [
				E('p', {}, txt(this.fetchedAt
					? _('The catalog is empty.')
					: _('Press "Update catalog" to download the list of available items.')))
			]));

			this.updateSelection();

			return;
		}

		const groups = {};

		for (const it of this.items.filter(it => this.matches(it))) {
			const key = (TYPE_ORDER.indexOf(it.type) >= 0) ? it.type : 'other';

			(groups[key] = groups[key] ?? []).push(it);
		}

		const nodes = [];

		for (const key of [ ...TYPE_ORDER, 'other' ]) {
			if (!groups[key])
				continue;

			const list = groups[key].sort((a, b) => String(a.name ?? a.id).localeCompare(String(b.name ?? b.id)));

			nodes.push(zc.section('%s (%d)'.format((key == 'other') ? _('Other') : zc.typeLabel(key), list.length),
				(key == 'byedpi') ? _('ByeDPI strategies work only in the Android application and cannot be used on the router.') : null,
				[ E('table', { 'class': 'table' }, [ this.renderHeader(), ...list.map(it => this.renderRow(it)) ]) ]));
		}

		dom.content(this.listNode, nodes.length ? nodes : zc.section(null, null, [ E('p', {}, txt(_('Nothing matches the filter.'))) ]));
		this.updateSelection();
	},

	columns() {
		return [ _('Name'), _('Author'), _('Version'), _('Size'), _('State') ];
	},

	renderHeader() {
		return E('tr', { 'class': 'tr table-titles' }, [
			E('th', { 'class': 'th', 'style': 'width:2em' }, txt('')),
			...this.columns().map(t => E('th', { 'class': 'th' }, txt(t))),
			E('th', { 'class': 'th right' }, txt(''))
		]);
	},

	renderRow(it) {
		const supported = it.supported !== false && it.type != 'byedpi';
		const disabled = this.busy ? '' : null;
		let state, actions = [];

		/* items delivered with the package are "installed" too, but only
		 * items installed from the repository can be removed here */
		const fromRepo = it.installed && (it.installed_source == null || it.installed_source == 'repo');

		if (!supported)
			state = E('em', {}, txt(_('not supported on the router')));
		else if (it.update_available === true)
			state = E('span', { 'class': 'label warning' }, txt(_('update available')));
		else if (it.installed && !fromRepo)
			state = E('span', { 'class': 'label success' }, txt(_('built into the package')));
		else if (it.installed)
			state = E('span', { 'class': 'label success' }, txt(_('installed')));
		else
			state = E('span', {}, txt(_('not installed')));

		if (supported && !it.installed)
			actions.push(zc.button(_('Install'), ui.createHandlerFn(this, 'handleInstall', [ it.id ]), 'apply', { 'disabled': disabled }));

		if (supported && it.update_available === true)
			actions.push(zc.button(_('Update'), ui.createHandlerFn(this, 'handleUpgrade', it.id), 'apply', { 'disabled': disabled }), ' ');

		if (fromRepo)
			actions.push(zc.button(_('Remove'), ui.createHandlerFn(this, 'handleRemove', it), 'remove', { 'disabled': disabled }));

		const box = (supported && !it.installed)
			? E('input', { 'type': 'checkbox', 'checked': this.selected[it.id] ? '' : null, 'title': _('Select for installation'),
				'change': ev => { this.selected[it.id] = ev.target.checked; this.updateSelection(); } })
			: '';

		const version = (it.installed && it.installed_version && it.installed_version != it.version)
			? _('%s → %s').format(it.installed_version, it.version)
			: (it.version ?? '');

		const titles = this.columns();
		const name = zc.localized(it, 'name') ?? it.id;
		const description = zc.localized(it, 'description');

		return E('tr', { 'class': 'tr' }, [
			zc.td(_('Select'), [ box ]),
			zc.td(titles[0], [
				E('strong', {}, txt(name)),
				(name != it.id) ? E('small', {}, txt(' %s'.format(it.id))) : '',
				description ? E('div', { 'class': 'cbi-value-description' }, txt(description)) : '',
				it.error ? E('div', { 'class': 'cbi-value-description' }, [ E('strong', {}, txt(_('Catalog error: %s').format(it.error))) ]) : ''
			]),
			zc.td(titles[1], it.author ?? ''),
			zc.td(titles[2], version),
			zc.td(titles[3], (+it.size > 0) ? zc.formatBytes(it.size) : '—'),
			zc.td(titles[4], [ state ]),
			zc.td(null, actions, 'right')
		]);
	},

	updateSelection() {
		const count = Object.keys(this.selected).filter(id => this.selected[id]).length;

		if (this.installSelectedButton) {
			this.installSelectedButton.disabled = this.busy || !count;
			dom.content(this.installSelectedButton, txt(count ? _('Install selected (%d)').format(count) : _('Install selected')));
		}
	},

	setBusy(busy) {
		this.busy = busy;
		this.renderInfo();
		this.renderList();
	},

	/* Starts watching a job returned by a repository command; the stop
	 * function is kept, so a second command does not start a second poll. */
	watch(job) {
		this.setBusy(true);
		this.job.update(Object.assign({ state: 'running', progress: 0, message: '' }, job));

		const previous = this.stopWatch;

		if (previous)
			previous();

		this.stopWatch = zc.watchJob(j => {
			if (j)
				this.job.update(j);
		}, j => {
			this.stopWatch = null;
			this.setBusy(false);

			if (j?.state == 'done')
				zc.notifyInfo(_('%s: finished.').format(zc.jobLabel(j.name)));
			else if (j?.state == 'failed')
				ui.addNotification(null, [
					E('p', {}, [ E('strong', {}, txt(_('%s: failed').format(zc.jobLabel(j.name)))) ]),
					E('p', {}, txt(j.message ?? ''))
				], 'danger');

			return this.refresh();
		});
	},

	startJob(promise, errorTitle) {
		return promise.then(res => {
			if (L.isObject(res.job))
				this.watch(res.job);
			else
				return this.refresh();
		}).catch(err => zc.notifyError(errorTitle, err));
	},

	handleFetch(ev) {
		return this.startJob(zc.run(zc.callRepoFetch), _('Could not update the catalog'));
	},

	handleInstall(ids, ev) {
		return this.startJob(zc.run(zc.callRepoInstall, ids), _('Could not start the installation'));
	},

	handleInstallSelected(ev) {
		const ids = Object.keys(this.selected).filter(id => this.selected[id]);

		if (!ids.length)
			return Promise.resolve();

		this.selected = {};

		return this.handleInstall(ids, ev);
	},

	handleUpgrade(id, ev) {
		return this.startJob(zc.run(zc.callRepoUpgrade, id ? [ id ] : undefined), _('Could not start the update'));
	},

	handleRemove(it, ev) {
		return zc.confirm(_('Remove'), _('Remove "%s" from the router? If it is used by a strategy or enabled as a list, zaprett will refuse to remove it.').format(it.name ?? it.id), _('Remove')).then(ok => {
			if (!ok)
				return;

			return this.startJob(zc.run(zc.callRepoRemove, it.id), _('Could not remove "%s"').format(it.name ?? it.id));
		});
	},

	handleCancel(ev) {
		return zc.cancelJob();
	},

	handleSaveApply: null,
	handleSave: null,
	handleReset: null
});
