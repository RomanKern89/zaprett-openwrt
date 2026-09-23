// SPDX-License-Identifier: MIT
// Shared helpers of luci-app-zaprett: RPC declarations, error texts,
// warnings, formatting, background job watching and small dialogs.
//
// Security note: dom.append()/E() put a plain string child into innerHTML.
// Data coming from the router is therefore always passed as an array child
// (see txt()), which LuCI turns into text nodes.

'use strict';
'require baseclass';
'require rpc';
'require ui';
'require dom';
'require poll';

/* LuCI accepts at most 100 KiB of JSON in one request (luci.http
 * HTTP_MAX_CONTENT); keep a margin for the JSON-RPC envelope. */
const MAX_WEB_JSON = 99 * 1024;

/* "zaprett strategy save" accepts at most 64 KiB. */
const MAX_STRATEGY_BYTES = 64 * 1024;

const ID_RE = /^[A-Za-z0-9._-]{1,96}$/;

/* Warning codes that only inform: nothing is broken (ARCHITECTURE §14.4, §15.5). */
const INFO_WARNINGS = [ 'empty_profile_removed', 'strategy_option_ignored', 'test_running', 'dns_plain' ];

/* nobatch: a slow method (diag, page) is sent in its own HTTP request, so it
 * does not delay the answers of quick methods batched together with it. */
function decl(method, params, nobatch) {
	return rpc.declare({
		object: 'luci.zaprett',
		method: method,
		params: params ?? [],
		expect: { '': {} },
		reject: true,
		nobatch: nobatch === true
	});
}

function txt(value) {
	return [ (value == null) ? '' : String(value) ];
}

function zaprettError(code, message, detail) {
	const err = new Error(message || code);

	err.zaprett = true;
	err.code = code;
	err.detail = detail;

	return err;
}

/* Texts for errors produced by the web interface layer itself. */
function ownErrorTexts() {
	return {
		backend_missing: _('The zaprett service package is not installed or damaged: /usr/bin/zaprett was not found. Reinstall the "zaprett" package.'),
		backend_error: _('The zaprett service returned an unexpected answer. Details are below; the "Diagnostics" page may help.'),
		timeout: _('The router did not finish the operation in time. It may be busy; wait a little and try again.'),
		reply_too_large: _('The answer is too large to show in the web interface.'),
		tmp_unavailable: _('The web interface could not create a temporary file on the router. Check free space in /tmp.'),
		invalid_argument: _('The request contains an invalid value.'),
		invalid_id: _('Invalid name. Use only Latin letters, digits, dot, dash and underscore (up to 96 characters).')
	};
}

/* Fallback texts for backend error codes; the backend normally sends its
 * own Russian message, which is shown instead. */
function backendErrorTexts() {
	return {
		job_busy: _('Another background task is already running. Wait until it finishes and try again.'),
		busy: _('Another service command is still running. Try again in a few seconds.'),
		test_running: _('Strategy selection is running. Wait until it finishes or stop it.'),
		not_found: _('The requested item was not found. The page may be outdated: reload it.'),
		strategy_not_found: _('The strategy was not found. The page may be outdated: reload it.'),
		item_active: _('The item is in use. Turn it off first.'),
		item_in_use: _('The item is needed by other strategies and cannot be removed.'),
		readonly_item: _('Built-in and custom items cannot be removed from the repository page.'),
		no_job: _('There is no running task.'),
		download_failed: _('Download failed. Check the internet connection of the router.'),
		no_space: _('Not enough free space on the router.'),
		dry_run_failed: _('The engine rejected the configuration.'),
		engine_missing: _('The bypass engine is not installed.'),
		engine_not_running: _('The engine did not start. See "Diagnostics" for details.'),
		invalid_entries: _('Some lines are not valid.'),
		too_large: _('The text is too large.'),
		no_targets: _('There is nothing to check: enable at least one site list or service.'),
		no_strategies: _('No strategies are installed for the current engine.'),
		repo_not_fetched: _('Load the repository catalog first.'),
		preset_unavailable: _('This service cannot be enabled automatically yet.'),
		uci_failed: _('The settings could not be saved.'),
		write_failed: _('A file could not be written on the router. Check free space.'),
		disabled: _('The service is switched off. Press "Start".'),
		bad_value: _('The request contains an invalid value.'),
		sha256_mismatch: _('The downloaded file is damaged (checksum mismatch). Try again later.'),
		source_update_failed: _('Some subscriptions could not be updated.'),
		cancelled: _('The operation was cancelled.')
	};
}

return baseclass.extend({
	MAX_WEB_JSON: MAX_WEB_JSON,
	MAX_STRATEGY_BYTES: MAX_STRATEGY_BYTES,
	ID_RE: ID_RE,

	callStatus: decl('status'),
	callService: decl('service', [ 'action' ]),
	callItems: decl('items', [ 'type' ]),
	callToggleItem: decl('toggle_item', [ 'id', 'enabled' ]),
	callSetStrategy: decl('set_strategy', [ 'id' ]),
	callStrategyShow: decl('strategy_show', [ 'id' ]),
	callStrategySave: decl('strategy_save', [ 'id', 'text' ]),
	callStrategyDelete: decl('strategy_delete', [ 'id' ]),
	callUserGet: decl('user_get', [ 'id' ]),
	callUserSet: decl('user_set', [ 'id', 'text' ]),
	callCheck: decl('check'),
	callRepoFetch: decl('repo_fetch'),
	callRepoList: decl('repo_list', [ 'type' ]),
	callRepoInstall: decl('repo_install', [ 'ids' ]),
	callRepoRemove: decl('repo_remove', [ 'id' ]),
	callRepoUpgrade: decl('repo_upgrade', [ 'ids' ]),
	callPresets: decl('presets'),
	callWizardApply: decl('wizard_apply', [ 'services' ]),
	callSourcesList: decl('sources_list'),
	callSourcesUpdate: decl('sources_update', [ 'names' ]),
	callSourceSave: decl('source_save', [ 'name', 'title', 'type', 'url', 'interval_hours', 'min_entries', 'enabled' ]),
	callSourceDelete: decl('source_delete', [ 'name' ]),
	callTestStart: decl('test_start', [ 'strategies', 'quick', 'apply_if_better' ]),
	callTestStatus: decl('test_status', [ 'brief' ]),
	callTestStop: decl('test_stop'),
	callTestApply: decl('test_apply', [ 'id' ]),
	callJobStatus: decl('job_status'),
	callJobLog: decl('job_log', [ 'tail' ]),
	callJobCancel: decl('job_cancel'),
	callDiag: decl('diag', [], true),
	callProbeStart: decl('probe_start', [ 'services' ]),
	callProbeStatus: decl('probe_status'),
	callMonitorStatus: decl('monitor_status'),
	callLog: decl('log', [ 'tail' ]),
	callPage: decl('page', [ 'name' ], true),
	callDnsStatus: decl('dns_status'),
	callDnsSetup: decl('dns_setup'),
	callDiagnoseStart: decl('diagnose_start', [ 'services' ]),
	callDiagnoseStatus: decl('diagnose_status'),

	txt: txt,

	/* Calls a declared RPC function; resolves with the reply when the backend
	 * reports success and rejects with a zaprett error otherwise. */
	run(fn, ...args) {
		return fn(...args).then(res => {
			if (!L.isObject(res))
				throw zaprettError('backend_error', null, { message: _('Empty answer') });

			if (res.ok !== true)
				throw zaprettError(res.error || 'backend_error', res.message, res);

			return res;
		});
	},

	/* An older zaprett service answers "usage" to a command or flag it does
	 * not know yet (page, probe, monitor, log, --brief, --apply-if-better). */
	isUsageError(err) {
		return err?.zaprett === true && err.code == 'usage';
	},

	/* Loads a page with one "page <name>" call (ARCHITECTURE §14.3): every
	 * field of the answer is the whole answer of one command. A field that
	 * is missing, or the whole call failing (older service), falls back to
	 * the separate call from fallbacks[key]. Resolves to { key: reply or
	 * Error } for every key of fallbacks, never rejects. */
	loadPage(name, fallbacks) {
		const keys = Object.keys(fallbacks);
		const single = key => Promise.resolve().then(() => fallbacks[key]()).catch(err => err);

		return this.run(this.callPage, name).catch(() => null).then(res => Promise.all(keys.map(key => {
			const part = res ? res[key] : null;

			if (!L.isObject(part))
				return single(key);

			return (part.ok === true) ? part : zaprettError(part.error || 'backend_error', part.message, part);
		}))).then(values => {
			const data = {};

			keys.forEach((key, i) => { data[key] = values[i]; });

			return data;
		});
	},

	/* Like poll.add(), but skips the ticks while the browser tab is hidden
	 * and refreshes at once when it becomes visible again. Returns a
	 * function that stops polling. */
	pollVisible(fn, interval) {
		let stopped = false, busy = false;

		const tick = () => {
			if (stopped || busy || document.hidden)
				return Promise.resolve();

			busy = true;

			return Promise.resolve().then(fn).catch(() => null).then(() => { busy = false; });
		};

		const onVisibility = () => {
			if (!document.hidden)
				tick();
		};

		const stop = () => {
			if (stopped)
				return;

			stopped = true;
			poll.remove(tick);
			document.removeEventListener('visibilitychange', onVisibility);
			window.removeEventListener('pagehide', stop);
		};

		poll.add(tick, interval);
		document.addEventListener('visibilitychange', onVisibility);
		window.addEventListener('pagehide', stop);

		return stop;
	},

	/* Language of the interface: <html lang> or the "lang_xx" class of <body>
	 * set by the theme; without them, whether our Russian catalog is active. */
	isRussianUI() {
		if (this.russianUI == null) {
			const html = String(document.documentElement.getAttribute('lang') ?? '');
			const body = String(document.body?.className ?? '').match(/(?:^|\s)lang_([A-Za-z_-]+)/);
			const lang = (html || (body ? body[1] : '')).toLowerCase();

			this.russianUI = lang ? /^ru/.test(lang) : (_('Try again') != 'Try again');
		}

		return this.russianUI;
	},

	/* Bilingual metadata (ARCHITECTURE §14.6): "<field>_en" when the
	 * interface is not Russian and the field is present. */
	localized(obj, field) {
		const en = L.isObject(obj) ? obj[field + '_en'] : null;

		if (typeof(en) == 'string' && en !== '' && !this.isRussianUI())
			return en;

		return L.isObject(obj) ? obj[field] : null;
	},

	isInfoWarning(warning) {
		return INFO_WARNINGS.indexOf(L.isObject(warning) ? warning.code : warning) >= 0;
	},

	/* Table cell with the column title in data-title: LuCI themes print it
	 * before the value when the table is shown as cards on a narrow screen. */
	td(title, content, cls) {
		const children = (content == null || typeof(content) == 'string' || typeof(content) == 'number') ? txt(content) : content;

		return E('td', { 'class': cls ? 'td ' + cls : 'td', 'data-title': title || null }, children);
	},

	/* Reason of a failed check of one address (tester and probe). */
	targetErrorText(t) {
		const text = {
			too_small: _('the answer was cut off'),
			http_error: _('the server returned an HTTP error'),
			timeout: _('no answer (timeout)'),
			reset: _('the connection was reset'),
			tls_cert: _('wrong certificate (substituted answer)'),
			tls_error: _('encryption error'),
			connect_failed: _('could not connect'),
			local_error: _('check error on the router'),
			failed: _('the address did not open')
		}[t?.error] ?? String(t?.error ?? '');

		return (+t?.http_status > 0) ? '%s (HTTP %d)'.format(text, +t.http_status) : text;
	},

	/* Table of checked addresses with their results. */
	targetsTable(targets) {
		const titles = [ _('Address'), _('Opened'), _('Time'), _('Received'), _('Error') ];
		const rows = [ E('tr', { 'class': 'tr table-titles' }, titles.map(t => E('th', { 'class': 'th' }, txt(t)))) ];

		for (const t of (Array.isArray(targets) ? targets : []))
			rows.push(E('tr', { 'class': 'tr' }, [
				E('td', { 'class': 'td', 'data-title': titles[0], 'style': 'word-break:break-all' }, txt(t.url)),
				this.td(titles[1], t.ok ? _('yes') : _('no')),
				this.td(titles[2], (+t.ms > 0) ? _('%d ms').format(+t.ms) : '—'),
				this.td(titles[3], this.formatBytes(t.bytes)),
				this.td(titles[4], [ E('div', {}, txt(t.ok ? '' : this.targetErrorText(t))), t.detail ? E('small', {}, txt(t.detail)) : '' ])
			]));

		return E('table', { 'class': 'table' }, rows);
	},

	isTimeout(err) {
		return (err?.zaprett && err.code == 'timeout') || /timed out|ubus code 7/.test(String(err?.message ?? ''));
	},

	/* Human-readable text for any error thrown by run() or by the RPC layer. */
	errorMessage(err) {
		if (err?.zaprett) {
			const own = ownErrorTexts()[err.code];
			const backendText = (typeof(err.detail?.message) == 'string') ? err.detail.message.trim() : '';

			if (own) {
				/* technical details of the web layer are useful only for unexpected answers */
				return (err.code == 'backend_error' && backendText) ? '%s %s'.format(own, backendText) : own;
			}

			if (backendText)
				return backendText;

			return backendErrorTexts()[err.code] ?? _('Unknown error: %s').format(err.code);
		}

		const msg = String(err?.message ?? err ?? '');
		let m;

		if ((m = msg.match(/ubus code (\d+)/)) != null) {
			switch (+m[1]) {
			case 3:
			case 4:
				return _('The web interface module of zaprett is not loaded. Run "/etc/init.d/rpcd restart" on the router or reboot it.');
			case 6:
				return _('Access denied. Log in to LuCI again as a user with access to zaprett.');
			case 7:
				return ownErrorTexts().timeout;
			default:
				return _('The router returned error code %d.').format(+m[1]);
			}
		}

		if (/-32002|Access denied/.test(msg))
			return _('Access denied. Log in to LuCI again as a user with access to zaprett.');

		if (/timed out/i.test(msg))
			return _('The router did not answer in time. Check that it is reachable and try again.');

		if ((m = msg.match(/HTTP error (\d+)/)) != null)
			return _('Connection to the router failed (HTTP %d). Reload the page.').format(+m[1]);

		return msg || _('Unknown error');
	},

	/* Explanations of invalid lines reported by "zaprett user set". */
	lineErrors(err) {
		const list = Array.isArray(err?.detail?.errors) ? err.detail.errors : [];
		const reasons = {
			bad_domain: _('not a valid domain name (use Latin letters; write Cyrillic domains in punycode, xn--…)'),
			bad_cidr: _('not a valid IPv4/IPv6 address or network'),
			bad_line: _('the line is too long or contains forbidden characters'),
			too_large: _('the list is larger than 1 MiB'),
			not_text: _('the data is not text')
		};

		return list.slice(0, 20).map(e => (+e.line > 0)
			? _('Line %d: "%s" — %s').format(+e.line, String(e.value ?? ''), reasons[e.reason] ?? String(e.reason ?? ''))
			: String(reasons[e.reason] ?? e.reason ?? ''));
	},

	notifyError(title, err) {
		ui.addNotification(null, [
			E('p', {}, [ E('strong', {}, txt(title)) ]),
			E('p', { 'style': 'white-space:pre-wrap' }, txt(this.errorMessage(err))),
			...this.lineErrors(err).map(l => E('div', {}, txt(l)))
		], 'danger');
	},

	notifyInfo(text, extra) {
		ui.addTimeLimitedNotification(null, [
			E('p', {}, txt(text)),
			...(extra ?? [])
		], 7000, 'info');
	},

	/* Inline error box for dialogs. */
	errorBox(err, title) {
		return E('div', { 'class': 'alert-message danger' }, [
			title ? E('p', {}, [ E('strong', {}, txt(title)) ]) : '',
			E('p', { 'style': 'white-space:pre-wrap' }, txt(this.errorMessage(err))),
			...this.lineErrors(err).map(l => E('div', {}, txt(l)))
		]);
	},

	isId(value) {
		return typeof(value) == 'string' && ID_RE.test(value);
	},

	textBytes(text) {
		return new Blob([ text ]).size;
	},

	/* Size of a text inside the JSON body of an RPC request. */
	jsonBytes(text) {
		return new Blob([ JSON.stringify(String(text)) ]).size;
	},

	/* Title, explanation and suggested action for a warning code of
	 * "zaprett status" (ARCHITECTURE §5, §6.2 and the backend). */
	warningInfo(warning, status) {
		const code = L.isObject(warning) ? warning.code : warning;
		/* "status" carries the details of the last generator run in
		 * details.generate (status.json), "check" carries them directly */
		const own = L.isObject(status?.details) ? status.details : {};
		const gen = L.isObject(own.generate?.details) ? own.generate.details : {};
		const details = Object.assign({}, gen, own);
		const info = {
			no_active_lists: [
				_('No site lists are enabled'),
				_('In whitelist mode zaprett bypasses blocking only for sites from the enabled lists. No list is enabled now, so the bypass affects nothing. Enable lists on the "Lists" page or use "Quick setup" below.'),
				'lists'
			],
			no_wan: [
				_('Internet interface not found'),
				_('zaprett did not find an interface with a default route, so its firewall rules are not bound to anything. Check the internet connection or choose the interface manually in "Settings".'),
				'settings'
			],
			flow_offload_enabled: [
				_('Flow offloading is enabled'),
				_('With software or hardware flow offloading most packets bypass the firewall, and zaprett does not see them. Choose "turn off automatically" for offloading in "Settings" or disable it in "Network → Firewall".'),
				'settings'
			],
			engine_missing: [
				_('The bypass engine is not installed'),
				_('The engine program (nfqws or nfqws2) was not found. Install the "zaprett-nfqws" package (or "zaprett-nfqws2" for the nfqws2 engine) for your router architecture.'),
				null
			],
			nft_queue_missing: [
				_('Kernel modules are missing'),
				_('zaprett needs the kmod-nft-queue and kmod-nfnetlink-queue kernel modules. Install them in "System → Software" and restart zaprett.'),
				null
			],
			no_strategy: [
				_('No strategy is selected'),
				_('Choose a strategy on the "Strategies" page or run automatic selection.'),
				'strategies'
			],
			strategy_missing: [
				_('The selected strategy was not found'),
				_('The strategy was removed or is not installed. Choose another one on the "Strategies" page.'),
				'strategies'
			],
			generate_failed: [
				_('The engine settings could not be prepared'),
				(typeof(details.generate?.message) == 'string' && details.generate.message)
					? _('The last start attempt failed: %s').format(details.generate.message)
					: _('The last start attempt failed. Open "Diagnostics" → "Check configuration" for details.'),
				'diagnostics'
			],
			bad_config: [
				_('Some settings have invalid values'),
				Array.isArray(details.bad_options) && details.bad_options.length
					? _('These options are ignored and default values are used instead: %s. Correct them in "Settings".').format(details.bad_options.join(', '))
					: _('Some options are ignored and default values are used instead. Check "Settings".'),
				'settings'
			],
			config_was_invalid: [
				_('Settings were already invalid'),
				_('The previous settings did not pass the check either. Open "Diagnostics" → "Check configuration".'),
				'diagnostics'
			],
			list_missing: [
				_('An enabled list was not found'),
				_('One of the enabled lists was removed or is not installed. Check the "Lists" page.'),
				'lists'
			],
			source_not_downloaded: [
				_('A subscription has not been downloaded yet'),
				_('An enabled subscription list has never been downloaded, so it is skipped for now. Open "Lists" → "Subscriptions" and press "Update now".'),
				'lists'
			],
			profile_unfiltered: [
				_('A strategy profile applies to all sites'),
				Array.isArray(details.unfiltered_profiles) && details.unfiltered_profiles.length
					? _('These parts of the strategy have no site or network list, so they process all traffic on their ports: %s. This is normal for some strategies (for example, Discord voice), but if sites that should stay untouched break, choose another strategy.')
						.format(details.unfiltered_profiles.map(p => {
							const ports = [ (p.tcp ?? []).length ? _('TCP %s').format(p.tcp.join(', ')) : '', (p.udp ?? []).length ? _('UDP %s').format(p.udp.join(', ')) : '' ].filter(x => x).join(', ');

							return _('part %d (%s)').format(+p.profile || 0, ports || _('all ports'));
						}).join('; '))
					: _('One of the profiles of the strategy has no site or network list, so it processes all traffic on its ports. This is normal for some strategies (for example, Discord voice), but if sites that should stay untouched break, choose another strategy.'),
				'strategies'
			],
			wide_port_range: [
				_('The strategy processes a wide port range'),
				_('The strategy sends many ports to the engine, which loads the router more. If the router slows down, choose another strategy.'),
				'strategies'
			],
			empty_profile_removed: [
				_('Empty profiles were removed from the strategy'),
				_('The strategy contains empty profiles (extra --new); they are ignored. Nothing needs to be done.'),
				null
			],
			strategy_option_ignored: [
				_('Some strategy options are ignored'),
				_('Options that zaprett sets itself (queue number, user, marks) are ignored in the strategy text. Nothing needs to be done.'),
				null
			],
			not_running: [
				_('zaprett is enabled but not running'),
				_('The service should work, but the engine process is not running: it probably stopped with an error. Press "Restart"; if it does not help, look at "Diagnostics".'),
				'restart'
			],
			nft_not_applied: [
				_('Firewall rules are not applied'),
				_('The engine is running, but without its nftables rules traffic does not reach it. Restart zaprett; details are on the "Diagnostics" page.'),
				'restart'
			],
			test_running: [
				_('Strategy selection is running'),
				_('During the check the bypass for your devices keeps working: strategies are tried by a separate test engine.'),
				'strategies'
			],
			ipv6_wan_unhandled: [
				_('IPv6 connections go without the bypass'),
				_('Your internet connection has IPv6, but zaprett processes only IPv4 now. Sites that the devices open over IPv6 are not unblocked. Turn on "Process IPv6" in "Settings".'),
				'settings'
			],
			low_memory: [
				_('The enabled lists are too large for this router'),
				_('The enabled services or subscriptions use large lists, and the router has little memory: the engine may crash or the router may slow down. Turn off large subscriptions on the "Lists" page and services marked as not recommended in "Quick setup".'),
				'lists'
			],
			monitor_degraded: [
				_('Sites of the enabled services stopped opening'),
				_('Several checks of the availability monitor in a row failed: the provider may have changed the blocking. Run automatic strategy selection. If automatic repair is on, zaprett is already looking for a working strategy.'),
				'strategies'
			],
			flowtable_failed: [
				_('The own acceleration table did not start'),
				_('"Own acceleration table" is chosen for flow offloading, but the router did not accept it (no suitable network devices, or the kernel does not support it). zaprett works without acceleration: the bypass works, but on a fast connection the speed may be lower. You can leave it so or choose "Turn off automatically" for offloading in "Settings".'),
				'settings'
			],
			game_filter_no_ipsets: [
				_('The game filter is not working: no IP network list'),
				_('The game filter processes only addresses from the enabled IP network lists, so that the rest of the traffic stays untouched. No such list is enabled now, so the filter is skipped. Enable the IP network list of the game on the "Lists" page ("IP networks" tab), or turn the game filter off in "Settings".'),
				'lists'
			],
			dns_plain: [
				_('DNS requests go without encryption'),
				_('For some of the enabled services the provider may substitute DNS answers; then the site does not open even though the bypass works. Encrypted DNS hides the requests from the provider.'),
				'dns'
			]
		}[code];

		if (!info)
			return { code: code, title: _('Warning: %s').format(code), text: _('The service reported a problem. See "Diagnostics" for details.'), action: 'diagnostics' };

		return { code: code, title: info[0], text: info[1], action: info[2] };
	},

	/* Router memory and the recommended preset tier from the "presets" reply
	 * (ARCHITECTURE §6.2: light below tiers.full.min_ram_mib, otherwise full). */
	memoryAdvice(presets) {
		const total = +presets?.ram_total_mib || 0;
		const fullMin = +presets?.tiers?.full?.min_ram_mib || 200;
		let recommended = presets?.recommended_tier ?? null;

		if (recommended != 'light' && recommended != 'full')
			recommended = total ? ((total < fullMin) ? 'light' : 'full') : null;

		return { total: total, fullMin: fullMin, recommended: recommended };
	},

	pageUrl(page) {
		return L.url('admin/services/zaprett', page);
	},

	typeLabel(type) {
		return {
			nfqws: _('nfqws strategy'),
			nfqws2: _('nfqws2 strategy'),
			list: _('Domain list'),
			list_exclude: _('Domain exclusions'),
			ipset: _('IP network list'),
			ipset_exclude: _('IP network exclusions'),
			bin: _('Fake packet file'),
			lua_lib: _('Lua library'),
			byedpi: _('ByeDPI strategy')
		}[type] ?? String(type ?? '');
	},

	sourceLabel(source) {
		return {
			bundle: _('Built-in'),
			repo: _('Repository'),
			user: _('Custom'),
			url: _('Subscription')
		}[source] ?? String(source ?? '');
	},

	jobLabel(name) {
		return {
			'repo-fetch': _('Loading the repository catalog'),
			'repo-install': _('Installing from the repository'),
			'repo-upgrade': _('Updating from the repository'),
			'repo-remove': _('Removing an item'),
			'sources-update': _('Updating subscriptions'),
			'test': _('Strategy selection'),
			'probe': _('Checking the sites'),
			'dns-setup': _('Setting up encrypted DNS'),
			'diagnose': _('Finding out how the provider blocks'),
			'autoupdate': _('Automatic update')
		}[name] ?? String(name ?? '');
	},

	jobStateLabel(state) {
		return {
			running: _('running'),
			done: _('finished'),
			failed: _('failed'),
			cancelled: _('cancelled')
		}[state] ?? String(state ?? '');
	},

	formatTime(epoch) {
		if (!(+epoch > 0))
			return _('never');

		return new Date(+epoch * 1000).toLocaleString();
	},

	formatBytes(bytes) {
		return '%1024.1mB'.format(+bytes || 0);
	},

	progressBar(percent, title) {
		const pct = Math.max(0, Math.min(100, Math.floor(+percent || 0)));

		return E('div', { 'class': 'cbi-progressbar', 'title': title ?? '%d%%'.format(pct) },
			E('div', { 'style': 'width:%d%%'.format(pct) }));
	},

	section(title, description, children) {
		return E('div', { 'class': 'cbi-section' }, [
			title ? E('h3', {}, txt(title)) : '',
			description ? E('div', { 'class': 'cbi-section-descr' }, txt(description)) : '',
			E('div', { 'class': 'cbi-section-node' }, children ?? [])
		]);
	},

	button(label, handler, style, attrs) {
		return E('button', Object.assign({
			'class': 'cbi-button cbi-button-%s'.format(style ?? 'action'),
			'click': handler
		}, attrs ?? {}), txt(label));
	},

	/* Resolves to true when the user confirms. */
	confirm(title, text, confirmLabel, style) {
		return new Promise(resolve => {
			ui.showModal(txt(title), [
				E('p', {}, txt(text)),
				E('div', { 'class': 'right' }, [
					E('button', { 'class': 'cbi-button', 'click': () => { ui.hideModal(); resolve(false); } }, txt(_('Cancel'))),
					' ',
					E('button', { 'class': 'cbi-button cbi-button-%s'.format(style ?? 'negative'), 'click': () => { ui.hideModal(); resolve(true); } }, txt(confirmLabel))
				])
			]);
		});
	},

	/* Result of "zaprett check": the reply of a successful check or the
	 * error of a failed one (the error carries dry_run and args as well). */
	renderCheck(res, err) {
		const data = err ? (err.detail ?? {}) : res;
		const dry = L.isObject(data.dry_run) ? data.dry_run : null;
		const ports = L.isObject(data.ports) ? data.ports : null;
		const args = Array.isArray(data.args) ? data.args : [];
		const pre = 'white-space:pre-wrap;word-break:break-all;max-height:20em;overflow:auto';
		const nodes = [];

		if (err)
			nodes.push(this.errorBox(err, _('The configuration does not pass the check')));
		else
			nodes.push(E('div', { 'class': 'alert-message success' }, txt(_('The engine accepts the current configuration.'))));

		if (ports)
			nodes.push(E('p', {}, txt(_('Processed ports: TCP %s; UDP %s').format(
				(ports.tcp ?? []).join(', ') || _('none'), (ports.udp ?? []).join(', ') || _('none')))));

		if (Array.isArray(data.warnings) && data.warnings.length)
			nodes.push(E('div', { 'class': 'alert-message warning' },
				data.warnings.map(w => {
					const info = this.warningInfo(w, data);

					return E('div', {}, [ E('strong', {}, txt(info.title)), E('div', {}, txt(info.text)) ]);
				})));

		if (dry && dry.output) {
			nodes.push(E('h5', {}, txt(_('Engine output'))));
			nodes.push(E('pre', { 'style': pre }, txt(dry.output)));
		}

		if (args.length) {
			nodes.push(E('h5', {}, txt(_('Engine arguments'))));
			nodes.push(E('pre', { 'style': pre }, txt(args.join('\n'))));
		}

		return nodes;
	},

	copyText(text) {
		const fallback = () => {
			const area = E('textarea', { 'style': 'position:fixed;top:0;left:0;width:1px;height:1px;opacity:0', 'readonly': 'readonly' });
			let copied = false;

			area.value = text;
			document.body.appendChild(area);
			area.select();

			try {
				copied = document.execCommand('copy');
			}
			catch (e) {
				copied = false;
			}

			document.body.removeChild(area);

			return copied;
		};

		const done = copied => {
			if (copied)
				this.notifyInfo(_('Copied to the clipboard.'));
			else
				ui.addNotification(null, E('p', {}, txt(_('The browser did not allow copying. Select the text and press Ctrl+C.'))), 'warning');

			return copied;
		};

		if (navigator.clipboard && window.isSecureContext)
			return navigator.clipboard.writeText(text).then(() => true, fallback).then(done);

		return Promise.resolve(fallback()).then(done);
	},

	downloadText(filename, text) {
		const url = URL.createObjectURL(new Blob([ text ], { type: 'text/plain;charset=utf-8' }));
		const link = E('a', { 'href': url, 'download': filename, 'style': 'display:none' });

		document.body.appendChild(link);
		link.click();
		document.body.removeChild(link);
		window.setTimeout(() => URL.revokeObjectURL(url), 1000);
	},

	/* Cancels the running background job. The backend answers at once with
	 * state "cancelling" and finishes the cancellation on the next status
	 * poll; "restored" means there was no job, only a leftover test state. */
	cancelJob(fn) {
		return this.run(fn ?? this.callJobCancel).then(res => {
			if (res.state == 'restored')
				this.notifyInfo(_('The previous strategy is restored.'));
			else if (res.state == 'cancelling')
				this.notifyInfo(_('Cancellation is requested. The task stops after the current step; this may take a minute.'));
			else
				this.notifyInfo(_('The task is cancelled.'));

			return res;
		}, err => {
			if (this.isTimeout(err))
				this.notifyInfo(_('Cancellation is requested. The task stops after the current step; this may take a minute.'));
			else
				this.notifyError(_('Could not cancel the task'), err);
		});
	},

	/* Polls "job_status" until the job leaves the running state.
	 *   onUpdate(job) - called with every received state
	 *   onFinish(job) - called once when the job is not running any more;
	 *                   with null when the state could not be read 5 times
	 * Polling pauses while the tab is hidden. Returns a function that stops
	 * watching. */
	watchJob(onUpdate, onFinish, interval) {
		let errors = 0;
		let stopped = false;
		let stopPoll = null;

		const stop = () => {
			if (!stopped) {
				stopped = true;
				stopPoll?.();
			}
		};

		const step = () => this.run(this.callJobStatus).then(res => {
			errors = 0;

			if (stopped)
				return;

			const job = L.isObject(res.job) ? res.job : null;

			if (onUpdate)
				onUpdate(job);

			if (!job || job.state != 'running') {
				stop();

				if (onFinish)
					onFinish(job);
			}
		}).catch(err => {
			if (++errors >= 5 && !stopped) {
				stop();
				this.notifyError(_('Lost track of the background task'), err);

				if (onFinish)
					onFinish(null);
			}
		});

		stopPoll = this.pollVisible(step, interval ?? 2);

		return stop;
	},

	/* Box with progress of a background job: { node, update(job) }. */
	jobBox(onCancel) {
		const title = E('strong', {}, txt(''));
		const state = E('span', {}, txt(''));
		const bar = E('div', {}, [ this.progressBar(0) ]);
		const message = E('div', { 'style': 'white-space:pre-wrap' }, txt(''));
		const cancel = onCancel ? this.button(_('Cancel task'), ui.createHandlerFn(this, onCancel), 'negative') : '';
		const node = E('div', { 'class': 'alert-message notice', 'style': 'display:none' }, [
			E('p', {}, [ title, ' ', state ]), bar, message, E('div', { 'class': 'right' }, [ cancel ])
		]);

		return {
			node: node,
			update: job => {
				if (!job) {
					node.style.display = 'none';
					return;
				}

				node.style.display = '';
				node.className = 'alert-message ' + ({ done: 'success', failed: 'danger', cancelled: 'warning' }[job.state] ?? 'notice');
				dom.content(title, txt(this.jobLabel(job.name)));
				dom.content(state, txt('(%s)'.format(this.jobStateLabel(job.state))));
				dom.content(bar, [ this.progressBar(job.state == 'done' ? 100 : job.progress, job.message) ]);
				dom.content(message, txt(job.message ?? ''));

				if (cancel)
					cancel.style.display = (job.state == 'running') ? '' : 'none';
			}
		};
	}
});
