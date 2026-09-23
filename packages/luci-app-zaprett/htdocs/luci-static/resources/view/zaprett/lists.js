// SPDX-License-Identifier: MIT
// zaprett: lists page - processing mode, domain and IP network lists with
// switches, editors of the custom lists, subscriptions to external lists.

'use strict';
'require view';
'require dom';
'require ui';
'require uci';
'require form';
'require zaprett.common as zc';

const txt = zc.txt;

const LARGE_LIST = 100000;
const LARGE_RAM_MIB = 5;
const SOURCE_NAME_RE = /^[a-z0-9_]{1,32}$/;
const SOURCE_URL_RE = /^https:\/\/[A-Za-z0-9.-]+(:[0-9]{1,5})?(\/[^\s]*)?$/;

const USER_LISTS = {
	'user-hosts': 'domains',
	'user-hosts-exclude': 'domains',
	'user-ipset': 'networks',
	'user-ipset-exclude': 'networks'
};

return view.extend({
	load() {
		return Promise.all([
			zc.loadPage('lists', {
				status: () => zc.run(zc.callStatus),
				job: () => zc.run(zc.callJobStatus),
				items: () => zc.run(zc.callItems),
				sources: () => zc.run(zc.callSourcesList),
				presets: () => zc.run(zc.callPresets)
			}),
			uci.load('zaprett').catch(() => null)
		]).then(data => {
			const page = data[0];

			return [
				page.status, page.items, page.sources,
				(page.job instanceof Error) ? null : (page.job.job ?? null),
				data[1],
				(page.presets instanceof Error) ? null : page.presets
			];
		});
	},

	render(data) {
		const status = data[0];

		this.status = status;
		this.items = data[1];
		this.sources = data[2];
		this.memory = zc.memoryAdvice(data[5]);
		this.mode =(status instanceof Error) ? (uci.get('zaprett', 'main', 'list_mode') || 'whitelist') : (status.list_mode || 'whitelist');
		this.sourceJob = zc.jobBox(() => zc.cancelJob());
		this.listsNode = E('div', {});

		const m = new form.Map('zaprett', _('Lists'),
			_('Lists tell zaprett which sites and networks to process. Domain lists work by the site name (subdomains are included automatically), IP network lists work by addresses: they are needed for services that connect without a name, for example Telegram calls. Switches in the tables are saved immediately.'));

		const s = m.section(form.NamedSection, 'main', 'main', _('Processing mode'));

		s.addremove = false;
		s.anonymous = true;

		const o = s.option(form.ListValue, 'list_mode', _('Which sites to process'),
			_('Whitelist: zaprett processes only sites and networks from the enabled lists. Blacklist: zaprett processes connections to all sites. Exclusion lists work in both modes, but a domain exclusion only helps where the site is recognised by its name: if the same site is covered by an enabled IP network list or subscription, its connections are still processed. To exclude it completely, add its addresses to the IP network exclusions. The whitelist is safer: banks, government services and games keep working as before. After changing the mode press "Save & Apply" at the bottom of the page.'));

		o.value('whitelist', _('Whitelist: only the enabled lists (recommended)'));
		o.value('blacklist', _('Blacklist: everything except the exclusions'));
		o.default = 'whitelist';

		this.renderContent();

		if (data[3]?.name == 'sources-update') {
			this.sourceJob.update(data[3]);

			if (data[3].state == 'running')
				this.watchSources();
		}

		return m.render().then(mapNode => E([], [ mapNode, this.sourceJob.node, this.listsNode ]));
	},

	renderContent() {
		if (this.items instanceof Error) {
			dom.content(this.listsNode, zc.section(null, null, [ zc.errorBox(this.items, _('Could not load the lists')) ]));
			return;
		}

		const items = Array.isArray(this.items.items) ? this.items.items : [];
		const byType = type => items.filter(it => it.type == type);

		const domains = E('div', { 'data-tab': 'domains', 'data-tab-title': _('Domains') }, [
			this.renderGroup(byType('list'), 'include',
				_('Sites to bypass'),
				_('The bypass is applied to connections to these sites. Used in the whitelist mode.')),
			this.renderGroup(byType('list_exclude'), 'exclude',
				_('Sites to leave untouched'),
				_('Connections recognised by these site names are never changed, in both modes. If the same site is also covered by an enabled IP network list, add its addresses to the IP network exclusions as well.'))
		]);

		const networks = E('div', { 'data-tab': 'networks', 'data-tab-title': _('IP networks') }, [
			this.renderGroup(byType('ipset'), 'include',
				_('Networks to bypass'),
				_('Addresses or networks (for example 149.154.160.0/20) whose connections get the bypass. Used in the whitelist mode.')),
			this.renderGroup(byType('ipset_exclude'), 'exclude',
				_('Networks to leave untouched'),
				_('zaprett never changes connections to these addresses, in both modes. This is the only way to exclude a service that connects by address without a site name.'))
		]);

		const subscriptions = E('div', { 'data-tab': 'sources', 'data-tab-title': _('Subscriptions') }, [
			this.renderSources(items)
		]);

		const panes = E('div', {}, [ domains, networks, subscriptions ]);

		dom.content(this.listsNode, E('div', { 'class': 'cbi-section' }, [ panes ]));
		ui.tabs.initTabGroup(panes.childNodes);
	},

	refresh() {
		return Promise.all([
			zc.run(zc.callItems).catch(err => err),
			zc.run(zc.callSourcesList).catch(err => err)
		]).then(data => {
			this.items = data[0];
			this.sources = data[1];
			this.renderContent();
		});
	},

	/* --- lists --- */

	renderGroup(list, kind, title, description) {
		const unused = (kind == 'include') && (this.mode == 'blacklist');
		const rows = [
			E('tr', { 'class': 'tr table-titles' }, [
				E('th', { 'class': 'th', 'style': 'width:5em' }, txt(_('Enabled'))),
				E('th', { 'class': 'th' }, txt(_('List'))),
				E('th', { 'class': 'th' }, txt(_('Entries'))),
				E('th', { 'class': 'th' }, txt(_('Source'))),
				E('th', { 'class': 'th right' }, txt(''))
			])
		];

		const order = { user: 0, bundle: 1, repo: 2, url: 3 };
		const nameOf = it => zc.localized(it, 'name') ?? it.id;
		const sorted = list.slice().sort((a, b) =>
			((order[a.source] ?? 9) - (order[b.source] ?? 9)) || String(nameOf(a)).localeCompare(String(nameOf(b))));

		for (const it of sorted)
			rows.push(this.renderRow(it));

		return E('div', { 'class': 'cbi-section' }, [
			E('h3', {}, txt(title)),
			E('div', { 'class': 'cbi-section-descr' }, txt(description)),
			unused ? E('div', { 'class': 'alert-message notice' }, txt(_('These lists are not used in the blacklist mode.'))) : '',
			(rows.length > 1) ? E('table', { 'class': 'table' }, rows) : E('p', {}, txt(_('No lists of this kind are installed.')))
		]);
	},

	renderRow(it) {
		const isUser = (it.source == 'user') || USER_LISTS[it.id] != null;
		const box = E('input', { 'type': 'checkbox', 'checked': it.active ? '' : null, 'title': _('Use this list') });
		const note = E('span', {}, txt(''));
		const usedBy = Array.isArray(it.used_by) ? it.used_by.filter(x => x) : [];

		box.addEventListener('change', () => this.handleToggle(it, box, note));

		const description = zc.localized(it, 'description');
		const info = [
			E('strong', {}, txt(zc.localized(it, 'name') ?? it.id)),
			E('br'),
			E('small', {}, txt(it.id)),
			description ? E('div', { 'class': 'cbi-value-description' }, txt(description)) : ''
		];

		if (usedBy.length)
			info.push(E('div', { 'class': 'cbi-value-description' },
				txt(_('Also used directly by strategies: %s. For them the switch has no effect.').format(usedBy.join(', ')))));

		if (+it.entries > LARGE_LIST)
			info.push(E('div', { 'class': 'cbi-value-description' }, [
				E('strong', {}, txt(_('Large list: it needs a lot of router memory. Enable it only on routers with 256 MB of memory or more.')))
			]));

		const source = [ E('div', {}, txt(zc.sourceLabel(it.source))) ];

		if (it.version)
			source.push(E('small', {}, txt(_('version %s').format(it.version))));

		return E('tr', { 'class': 'tr' }, [
			zc.td(_('Enabled'), [ box, ' ', note ]),
			zc.td(_('List'), info),
			zc.td(_('Entries'), (it.entries != null) ? String(it.entries) : '—'),
			zc.td(_('Source'), source),
			zc.td(null, [
				isUser ? zc.button(_('Edit'), ui.createHandlerFn(this, 'handleEdit', it), 'edit') : ''
			], 'right')
		]);
	},

	handleToggle(it, box, note) {
		const enabled = box.checked;
		const name = zc.localized(it, 'name') ?? it.id;

		box.disabled = true;
		dom.content(note, E('em', {}, txt(_('saving…'))));

		return zc.run(zc.callToggleItem, it.id, enabled).then(res => {
			it.active = enabled;
			dom.content(note, '');

			const running = !(this.status instanceof Error) && this.status.running;
			const text = enabled ? _('List "%s" is enabled.').format(name) : _('List "%s" is disabled.').format(name);

			if (running && res.changed !== false && res.reloaded === false)
				zc.notifyInfo(text, [
					E('p', {}, txt(_('Restart zaprett to apply the change.'))),
					zc.button(_('Restart'), ui.createHandlerFn(this, 'handleRestart'), 'reload')
				]);
			else
				zc.notifyInfo(text);
		}).catch(err => {
			box.checked = !enabled;
			dom.content(note, '');
			zc.notifyError(enabled ? _('Could not enable the list "%s"').format(name) : _('Could not disable the list "%s"').format(name), err);
		}).finally(() => {
			box.disabled = false;
		});
	},

	handleRestart(ev) {
		return zc.run(zc.callService, 'restart')
			.then(() => zc.notifyInfo(_('zaprett is restarted.')))
			.catch(err => zc.notifyError(_('Could not restart zaprett'), err));
	},

	handleEdit(it, ev) {
		return zc.run(zc.callUserGet, it.id)
			.then(res => this.openEditor(it, res.text ?? ''))
			.catch(err => zc.notifyError(_('Could not load the list'), err));
	},

	/* Hints about typical mistakes; the backend does the real validation. */
	lintLines(text, kind) {
		const problems = [];
		const lines = text.split(/\r?\n/);

		for (let i = 0; i < lines.length && problems.length < 5; i++) {
			const value = lines[i].trim();

			if (!value || value.charAt(0) == '#')
				continue;

			if (kind == 'domains') {
				if (/^\*\./.test(value))
					problems.push(_('Line %d: masks like *.example.com are not supported, write example.com: subdomains are included automatically.').format(i + 1));
				else if (/\//.test(value))
					problems.push(_('Line %d: write only the site name without http:// and paths.').format(i + 1));
				else if (/\s/.test(value))
					problems.push(_('Line %d: one domain per line, without spaces.').format(i + 1));
				else if (/[^\x00-\x7f]/.test(value))
					problems.push(_('Line %d: write national domain names in punycode (xn--…), for example xn--p1ai instead of рф.').format(i + 1));
			}
			else if (!/^[0-9A-Fa-f:.]+(\/\d{1,3})?$/.test(value)) {
				problems.push(_('Line %d: expected an IPv4 or IPv6 address or network, for example 192.0.2.0/24.').format(i + 1));
			}
		}

		return problems;
	},

	openEditor(it, text) {
		const kind = USER_LISTS[it.id] ?? ((it.type == 'ipset' || it.type == 'ipset_exclude') ? 'networks' : 'domains');
		const area = E('textarea', { 'class': 'cbi-input-textarea', 'rows': '16', 'wrap': 'off', 'spellcheck': 'false', 'style': 'width:100%;font-family:monospace' });
		const counter = E('div', { 'class': 'cbi-value-description' }, txt(''));
		const hints = E('div', {});
		const errNode = E('div', {});

		area.value = text;

		const update = () => {
			const lines = area.value.split(/\r?\n/).map(l => l.trim()).filter(l => l && l.charAt(0) != '#');
			const problems = this.lintLines(area.value, kind);

			dom.content(counter, txt(_('Entries: %d, size: %s').format(lines.length, zc.formatBytes(zc.textBytes(area.value)))));
			dom.content(hints, problems.length
				? E('div', { 'class': 'alert-message warning' }, problems.map(p => E('div', {}, txt(p))))
				: '');
		};

		area.addEventListener('input', update);
		update();

		const save = () => {
			const body = area.value.replace(/\r\n/g, '\n');

			dom.content(errNode, '');

			if (zc.jsonBytes(body) > zc.MAX_WEB_JSON)
				return dom.content(errNode, E('div', { 'class': 'alert-message warning' },
					txt(_('The list is too large for the web interface (maximum %d KiB). Split it or copy the file to the router over SSH.').format(zc.MAX_WEB_JSON / 1024))));

			return zc.run(zc.callUserSet, it.id, body).then(res => {
				ui.hideModal();
				zc.notifyInfo((res.entries != null)
					? _('List "%s" is saved, entries: %d.').format(it.name ?? it.id, +res.entries)
					: _('List "%s" is saved.').format(it.name ?? it.id));

				return this.refresh();
			}).catch(err => dom.content(errNode, zc.errorBox(err, _('The list is not saved'))));
		};

		const help = (kind == 'domains')
			? _('One domain per line, for example example.com. Subdomains are included automatically: youtube.com also covers www.youtube.com. Lines starting with # are comments.')
			: _('One IPv4 or IPv6 address or network per line, for example 192.0.2.10 or 192.0.2.0/24. Lines starting with # are comments.');

		ui.showModal(txt(_('Edit list "%s"').format(it.name ?? it.id)), [
			E('p', {}, txt(help)),
			area,
			counter,
			hints,
			errNode,
			E('div', { 'class': 'right' }, [
				zc.button(_('Cancel'), ui.hideModal, 'neutral'), ' ',
				zc.button(_('Save'), ui.createHandlerFn(this, save), 'save')
			])
		], 'cbi-modal');
	},

	/* --- subscriptions --- */

	renderSources(items) {
		const intro = E('div', { 'class': 'cbi-section-descr' }, txt(_('Subscriptions are external lists that the router downloads by a link and updates on schedule, for example the registry of blocked sites. After the first download a subscription becomes a regular list: turn it on with the "Use" switch below or on the "Domains" and "IP networks" tabs. Large lists need a lot of router memory and wear the flash when updated too often.')));

		if (this.sources instanceof Error) {
			const unsupported = this.sources.zaprett && this.sources.code == 'usage';

			return E('div', { 'class': 'cbi-section' }, [
				intro,
				unsupported
					? E('div', { 'class': 'alert-message warning' }, txt(_('The installed zaprett service does not support subscriptions yet. Update the "zaprett" package.')))
					: zc.errorBox(this.sources, _('Could not load the subscriptions'))
			]);
		}

		const sources = Array.isArray(this.sources.sources) ? this.sources.sources : [];
		const itemById = {};

		for (const it of items)
			itemById[it.id] = it;

		const rows = [
			E('tr', { 'class': 'tr table-titles' }, [
				E('th', { 'class': 'th', 'style': 'width:5em' }, txt(_('Download'))),
				E('th', { 'class': 'th' }, txt(_('Subscription'))),
				E('th', { 'class': 'th' }, txt(_('Last update'))),
				E('th', { 'class': 'th' }, txt(_('Entries'))),
				E('th', { 'class': 'th' }, txt(_('Memory'))),
				E('th', { 'class': 'th', 'style': 'width:5em' }, txt(_('Use'))),
				E('th', { 'class': 'th right' }, txt(''))
			])
		];

		let ramEnabled = 0;

		for (const src of sources) {
			if (src.enabled && +src.ram_mib > 0)
				ramEnabled += +src.ram_mib;

			rows.push(this.renderSourceRow(src, itemById[src.item_id]));
		}

		const memory = this.memory;
		let ramWarning = '';

		if (memory.total && ramEnabled > 0 && ((memory.recommended == 'light' && ramEnabled >= LARGE_RAM_MIB) || ramEnabled > memory.total / 8))
			ramWarning = _('Enabled subscriptions need about %d MiB of memory, and the router has %d MiB in total. The router may become unstable: turn off large subscriptions.').format(Math.ceil(ramEnabled), memory.total);
		else if (!memory.total && ramEnabled >= LARGE_RAM_MIB)
			ramWarning = _('Enabled subscriptions need about %d MiB of router memory. On routers with 128 MB of memory or less this can make the router unstable.').format(Math.ceil(ramEnabled));

		return E('div', { 'class': 'cbi-section' }, [
			intro,
			memory.total ? E('p', {}, txt(_('Router memory: %d MiB.').format(memory.total))) : '',
			ramWarning ? E('div', { 'class': 'alert-message warning' }, txt(ramWarning)) : '',
			E('div', { 'style': 'margin-bottom:.5em' }, [
				zc.button(_('Update enabled now'), ui.createHandlerFn(this, 'handleSourcesUpdate', null), 'reload'), ' ',
				zc.button(_('Add subscription'), ui.createHandlerFn(this, 'openSourceEditor', null), 'add')
			]),
			sources.length ? E('table', { 'class': 'table' }, rows) : E('p', {}, txt(_('There are no subscriptions.')))
		]);
	},

	renderSourceRow(src, item) {
		const enabledBox = E('input', { 'type': 'checkbox', 'checked': src.enabled ? '' : null, 'title': _('Download and update this list') });
		/* a subscription can be switched on before its first download: the
		 * generator skips it and status shows source_not_downloaded */
		const listItem = item ?? { id: src.item_id, name: src.title || src.name, type: src.type, active: false, source: 'url' };
		const useBox = E('input', { 'type': 'checkbox', 'checked': item?.active ? '' : null,
			'title': item ? _('Use this list') : _('Use this list after the first download') });
		const note = E('span', {}, txt(''));
		const title = src.title || src.name;

		enabledBox.addEventListener('change', () => this.handleSourceEnable(src, enabledBox));
		useBox.addEventListener('change', () => this.handleToggle(listItem, useBox, note));

		/* status: ok | error | never | invalid (BACKEND.md section 6) */
		const state = [];

		if (src.status == 'invalid')
			state.push(E('div', {}, [ E('strong', {}, txt(_('the subscription settings are invalid'))) ]));
		else if (+src.last_update > 0)
			state.push(E('div', {}, txt(zc.formatTime(src.last_update))));
		else
			state.push(E('div', {}, txt(_('not downloaded yet'))));

		if (src.status == 'error' || (src.error && src.status != 'invalid'))
			state.push(E('div', { 'class': 'cbi-value-description' }, [
				E('strong', {}, txt(src.message || _('error: %s').format(src.error)))
			]));
		else if (src.message)
			state.push(E('div', { 'class': 'cbi-value-description' }, txt(src.message)));

		return E('tr', { 'class': 'tr' }, [
			zc.td(_('Download'), [ enabledBox ]),
			zc.td(_('Subscription'), [
				E('strong', {}, txt(title)),
				E('br'),
				E('small', {}, txt('%s · %s'.format(zc.typeLabel(src.type), src.name))),
				E('div', { 'class': 'cbi-value-description', 'style': 'word-break:break-all' }, txt(src.url ?? '')),
				E('div', { 'class': 'cbi-value-description' }, txt(_('Update every %d h').format(+src.interval_hours || 0)))
			]),
			zc.td(_('Last update'), state),
			zc.td(_('Entries'), (src.entries != null) ? '%d (%s)'.format(+src.entries, zc.formatBytes(src.size)) : '—'),
			zc.td(_('Memory'), (+src.ram_mib > 0)
				? [
					E((+src.ram_mib >= LARGE_RAM_MIB) ? 'strong' : 'span', {}, txt(_('≈ %d MiB').format(Math.ceil(+src.ram_mib)))),
					(this.memory.recommended == 'light' && +src.ram_mib >= LARGE_RAM_MIB)
						? E('div', { 'class': 'cbi-value-description' }, txt(_('too much for this router')))
						: ''
				]
				: '—'),
			zc.td(_('Use'), [ useBox, ' ', note ]),
			zc.td(null, [
				zc.button(_('Update now'), ui.createHandlerFn(this, 'handleSourcesUpdate', src.name), 'reload'), ' ',
				zc.button(_('Edit'), ui.createHandlerFn(this, 'openSourceEditor', src), 'edit'), ' ',
				zc.button(_('Delete'), ui.createHandlerFn(this, 'handleSourceDelete', src), 'remove')
			], 'right')
		]);
	},

	handleSourceEnable(src, box) {
		const enabled = box.checked;

		box.disabled = true;

		/* only the changed field is sent; the backend keeps the rest */
		return zc.run(zc.callSourceSave, src.name, undefined, undefined, undefined, undefined, undefined, enabled).then(() => {
			src.enabled = enabled;
			zc.notifyInfo(enabled
				? _('Subscription "%s" is enabled. Press "Update now" to download it right away.').format(src.title || src.name)
				: _('Subscription "%s" is disabled; the downloaded list is kept.').format(src.title || src.name));
		}).catch(err => {
			box.checked = !enabled;
			zc.notifyError(_('Could not change the subscription'), err);
		}).finally(() => {
			box.disabled = false;
		});
	},

	handleSourcesUpdate(name, ev) {
		return zc.run(zc.callSourcesUpdate, name ? [ name ] : undefined).then(res => {
			this.sourceJob.update(Object.assign({ state: 'running', progress: 0, message: '' }, res.job ?? { name: 'sources-update' }));
			this.watchSources();
		}).catch(err => zc.notifyError(_('Could not start updating the subscriptions'), err));
	},

	watchSources() {
		if (this.stopSourcesWatch)
			return;

		this.stopSourcesWatch = zc.watchJob(job => {
			if (job)
				this.sourceJob.update(job);
		}, job => {
			this.stopSourcesWatch = null;

			const failed = Array.isArray(job?.result?.failed) ? job.result.failed : [];

			if (job?.state == 'failed' || failed.length)
				ui.addNotification(null, [
					E('p', {}, [ E('strong', {}, txt(_('Some subscriptions could not be updated.'))) ]),
					...failed.map(f => E('div', {}, txt('%s: %s'.format(String(f.name ?? ''), String(f.message || f.error || ''))))),
					failed.length ? '' : E('p', {}, txt(job.message ?? ''))
				], 'danger');
			else if (job?.state == 'done')
				zc.notifyInfo((+job.result?.updated > 0)
					? _('Subscriptions are updated: %d, unchanged: %d.').format(+job.result.updated, +job.result?.unchanged || 0)
					: _('%s: finished.').format(zc.jobLabel(job.name)));

			return this.refresh();
		});
	},

	handleSourceDelete(src, ev) {
		return zc.confirm(_('Delete subscription'),
			_('Delete the subscription "%s"? The downloaded list is removed too.').format(src.title || src.name), _('Delete')).then(ok => {
			if (!ok)
				return;

			return zc.run(zc.callSourceDelete, src.name)
				.then(() => {
					zc.notifyInfo(_('Subscription "%s" is deleted.').format(src.title || src.name));

					return this.refresh();
				})
				.catch(err => zc.notifyError(_('Could not delete the subscription'), err));
		});
	},

	openSourceEditor(src, ev) {
		const isNew = !src;
		const name = E('input', { 'type': 'text', 'class': 'cbi-input-text', 'maxlength': '32', 'placeholder': 'my_list', 'readonly': isNew ? null : '' });
		const title = E('input', { 'type': 'text', 'class': 'cbi-input-text', 'maxlength': '128' });
		const type = E('select', { 'class': 'cbi-input-select' },
			[ 'list', 'list_exclude', 'ipset', 'ipset_exclude' ].map(t => E('option', { 'value': t }, txt(zc.typeLabel(t)))));
		const url = E('input', { 'type': 'url', 'class': 'cbi-input-text', 'maxlength': '2048', 'placeholder': 'https://example.com/list.txt', 'style': 'width:100%' });
		const interval = E('input', { 'type': 'number', 'class': 'cbi-input-text', 'min': '1', 'max': '8760', 'step': '1' });
		const minEntries = E('input', { 'type': 'number', 'class': 'cbi-input-text', 'min': '0', 'step': '1' });
		const enabled = E('input', { 'type': 'checkbox', 'id': 'zaprett-src-enabled' });
		const errNode = E('div', {});
		const min = (src && src.min_entries != null) ? src.min_entries : null;

		name.value = src?.name ?? '';
		title.value = src?.title ?? '';
		type.value = src?.type ?? 'list';
		url.value = src?.url ?? '';
		interval.value = String(+src?.interval_hours || 72);
		minEntries.value = (min != null) ? String(min) : '10';
		enabled.checked = src ? !!src.enabled : true;

		const field = (label, input, description) => E('div', { 'class': 'cbi-value' }, [
			E('label', { 'class': 'cbi-value-title' }, txt(label)),
			E('div', { 'class': 'cbi-value-field' }, [
				input,
				description ? E('div', { 'class': 'cbi-value-description' }, txt(description)) : ''
			])
		]);

		const warn = text => dom.content(errNode, E('div', { 'class': 'alert-message warning' }, txt(text)));

		const save = () => {
			const hours = +interval.value, entries = +minEntries.value;

			dom.content(errNode, '');

			if (!SOURCE_NAME_RE.test(name.value))
				return warn(_('Enter a name: lowercase Latin letters, digits and underscore, up to 32 characters.'));

			if (isNew && Array.isArray(this.sources?.sources) && this.sources.sources.some(s => s.name == name.value))
				return warn(_('A subscription with this name already exists.'));

			if (!title.value.trim())
				return warn(_('Enter a title.'));

			if (!SOURCE_URL_RE.test(url.value.trim()))
				return warn(_('Enter a link that starts with https://.'));

			if (!Number.isInteger(hours) || hours < 1 || hours > 8760)
				return warn(_('The update interval must be a whole number of hours from 1 to 8760.'));

			if (!Number.isInteger(entries) || entries < 0)
				return warn(_('The minimum number of entries must be a whole number.'));

			return zc.run(zc.callSourceSave, name.value, title.value.trim(), type.value, url.value.trim(), hours, entries, enabled.checked)
				.then(() => {
					ui.hideModal();
					zc.notifyInfo(_('Subscription "%s" is saved.').format(title.value.trim()));

					return this.refresh();
				})
				.catch(err => dom.content(errNode, zc.errorBox(err, _('The subscription is not saved'))));
		};

		ui.showModal(txt(isNew ? _('New subscription') : _('Edit subscription "%s"').format(src.title || src.name)), [
			field(_('Name'), name, _('Lowercase Latin letters, digits and underscore. The list will be named src-<name>.')),
			field(_('Title'), title, null),
			field(_('List type'), type, _('Domain lists and IP network lists; exclusions are sites and networks that must never be touched.')),
			field(_('Link'), url, _('Only https:// links. The file must contain one domain or network per line.')),
			field(_('Update every, hours'), interval, _('For large lists update not more often than every 48 hours: frequent writes wear the router flash.')),
			field(_('Minimum entries'), minEntries, _('If the downloaded file has fewer entries, it is considered broken and the previous list is kept.')),
			E('p', {}, [ enabled, ' ', E('label', { 'for': 'zaprett-src-enabled' }, txt(_('Download and update automatically'))) ]),
			errNode,
			E('div', { 'class': 'right' }, [
				zc.button(_('Cancel'), ui.hideModal, 'neutral'), ' ',
				zc.button(_('Save'), ui.createHandlerFn(this, save), 'save')
			])
		], 'cbi-modal');
	}
});
