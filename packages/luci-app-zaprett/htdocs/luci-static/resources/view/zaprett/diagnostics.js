// SPDX-License-Identifier: MIT
// zaprett: diagnostics page - how the provider blocks, diagnostic report,
// engine log with the switch of the detailed log, availability monitor, last
// background task and its log, configuration check.

'use strict';
'require view';
'require dom';
'require ui';
'require uci';
'require zaprett.common as zc';
'require zaprett.health as zh';

const txt = zc.txt;

/* Lines of the task log and of the engine log ("zaprett log" allows 1..1000). */
const LOG_TAIL = 300;
const ENGINE_LOG_LINES = 200;

const PRE_STYLE = 'white-space:pre-wrap;word-break:break-all;max-height:30em;overflow:auto';

return view.extend({
	load() {
		return Promise.all([
			zc.loadPage('diagnostics', {
				status: () => zc.run(zc.callStatus),
				job: () => zc.run(zc.callJobStatus),
				monitor: () => zc.run(zc.callMonitorStatus),
				dns: () => zc.run(zc.callDnsStatus),
				diagnose: () => zc.run(zc.callDiagnoseStatus)
			}),
			zc.run(zc.callDiag).catch(err => err),
			zc.run(zc.callLog, ENGINE_LOG_LINES).catch(err => err),
			uci.load('zaprett').catch(() => null)
		]).then(data => {
			const job = (data[0].job instanceof Error) ? null : (data[0].job.job ?? null);
			/* the task log is read only when there is a task */
			const jobLog = job ? zc.run(zc.callJobLog, LOG_TAIL).catch(err => err) : null;

			return Promise.resolve(jobLog).then(log => [ data[0], data[1], data[2], log ]);
		});
	},

	render(data) {
		const page = data[0];

		this.status = page.status;
		this.diagText = '';
		this.logText = '';
		this.engineLogText = '';
		this.diagNode = E('div', {});
		this.engineLogNode = E('div', {});
		this.jobNode = E('div', {});
		this.checkNode = E('div', {});
		this.dns = page.dns;
		this.diagnose = page.diagnose;
		this.diagnoseNode = E('div', {});
		this.diagnoseBox = zc.jobBox(() => zc.cancelJob());
		this.diagnoseResultNode = E('div', {});
		this.diagnoseBusy = false;

		this.showDiag(data[1]);
		this.showEngineLog(data[2]);
		this.showJob(page.job, data[3]);
		this.renderDiagnose();

		const job = (page.job instanceof Error) ? null : page.job?.job;

		if (L.isObject(job) && job.name == 'diagnose' && job.state == 'running')
			this.handleDiagnose(null, true);

		return E([], [
			E('h2', {}, txt(_('Diagnostics'))),
			E('div', { 'class': 'cbi-map-descr' }, txt(_('Information for finding problems: the diagnostic report, the log of the engine and the last background task. If you ask for help, attach the report and the log. Look through them first: they contain the addresses and names of your network interfaces, the settings of zaprett (devices of the local network, subscription links) and the names of recently opened sites, but no passwords of the router.'))),
			this.diagnoseNode,
			zc.section(_('Diagnostic report'), _('Versions, system, kernel modules, service state, zaprett settings, engine arguments and the firewall table of zaprett.'), [
				E('div', { 'style': 'margin-bottom:.5em' }, [
					zc.button(_('Refresh'), ui.createHandlerFn(this, 'handleDiag'), 'reload'), ' ',
					zc.button(_('Copy'), ui.createHandlerFn(this, () => zc.copyText(this.diagText)), 'action'), ' ',
					zc.button(_('Download'), ui.createHandlerFn(this, () => zc.downloadText('zaprett-diag.txt', this.diagText)), 'action')
				]),
				this.diagNode
			]),
			zc.section(_('Engine log'), _('Recent messages of zaprett and of the engine from the system log, newest at the bottom.'), [
				E('div', { 'style': 'margin-bottom:.5em' }, [
					zc.button(_('Refresh'), ui.createHandlerFn(this, 'handleEngineLog'), 'reload'), ' ',
					zc.button(_('Copy'), ui.createHandlerFn(this, () => zc.copyText(this.engineLogText)), 'action')
				]),
				this.engineLogNode
			]),
			this.renderDebug(),
			this.renderMonitor(page.monitor),
			zc.section(_('Last background task'), _('Downloads from the repository, updates, checks of the sites and automatic strategy selection run in the background. Here you can see the last such task and its log.'), [
				E('div', { 'style': 'margin-bottom:.5em' }, [
					zc.button(_('Refresh'), ui.createHandlerFn(this, 'handleJob'), 'reload'), ' ',
					zc.button(_('Copy log'), ui.createHandlerFn(this, () => zc.copyText(this.logText)), 'action')
				]),
				this.jobNode
			]),
			zc.section(_('Configuration check'), _('Builds the engine arguments from the current settings and asks the engine to check them without starting.'), [
				E('div', { 'style': 'margin-bottom:.5em' }, [
					zc.button(_('Check configuration'), ui.createHandlerFn(this, 'handleCheck'), 'action')
				]),
				this.checkNode
			])
		]);
	},

	showDiag(res) {
		if (res instanceof Error) {
			this.diagText = zc.errorMessage(res);
			dom.content(this.diagNode, zc.errorBox(res, _('Could not collect the diagnostic report')));

			return;
		}

		this.diagText = String(res.text ?? res.output ?? '');
		dom.content(this.diagNode, [
			E('pre', { 'style': PRE_STYLE }, txt(this.diagText || _('The report is empty.'))),
			/* the web interface asks for the quick report; the slow blocks are
			 * available over SSH ("full" is true only in the full report) */
			(res.full === true) ? '' : E('div', { 'class': 'cbi-value-description' },
				txt(_('This is the quick report. The full one, with the package list and long log, is collected over SSH: zaprett diag --full')))
		]);
	},

	handleDiag(ev) {
		return zc.run(zc.callDiag).catch(err => err).then(res => this.showDiag(res));
	},

	/* --- engine log --- */

	showEngineLog(res) {
		if (zc.isUsageError(res)) {
			this.engineLogText = '';
			dom.content(this.engineLogNode, E('div', { 'class': 'alert-message notice' },
				txt(_('The installed zaprett service cannot show its log here yet. Update the "zaprett" package, or read the log over SSH: logread -e zaprett'))));

			return;
		}

		if (res instanceof Error) {
			this.engineLogText = zc.errorMessage(res);
			dom.content(this.engineLogNode, zc.errorBox(res, _('Could not read the log')));

			return;
		}

		this.engineLogText = (Array.isArray(res.lines) ? res.lines : []).join('\n');
		dom.content(this.engineLogNode, E('pre', { 'style': PRE_STYLE },
			txt(this.engineLogText || _('The log has no messages of zaprett yet.'))));
	},

	handleEngineLog(ev) {
		return zc.run(zc.callLog, ENGINE_LOG_LINES).catch(err => err).then(res => this.showEngineLog(res));
	},

	/* The detailed log is main.debug: the engine gets --debug=syslog. Saving
	 * goes through the usual LuCI apply, which reloads zaprett. */
	renderDebug() {
		const title = _('Detailed engine log');

		if (!uci.get('zaprett', 'main'))
			return '';

		const on = uci.get('zaprett', 'main', 'debug') == '1';

		return zc.section(title, _('With the detailed log the engine writes to the system log what it does with every processed connection. It helps to find out why a site does not open, but it loads the router and fills the log quickly: turn it on only for a short check and turn it off afterwards.'), [
			E('p', {}, [
				E('span', { 'class': 'label %s'.format(on ? 'warning' : 'notice') }, txt(on ? _('on') : _('off')))
			]),
			zc.button(on ? _('Turn off the detailed log') : _('Turn on the detailed log'),
				ui.createHandlerFn(this, 'handleDebug', !on), on ? 'neutral' : 'action')
		]);
	},

	handleDebug(enable, ev) {
		const apply = () => {
			uci.set('zaprett', 'main', 'debug', enable ? '1' : '0');

			return uci.save().then(() => ui.changes.apply(true));
		};

		if (!enable)
			return apply();

		return zc.confirm(_('Detailed engine log'),
			_('The engine will write lines about every processed connection. On a weak router this slows down the traffic, and the system log fills up quickly. A running zaprett is restarted to apply the change. Turn it on?'),
			_('Turn on'), 'apply').then(ok => ok ? apply() : null);
	},

	/* --- how the provider blocks (§15.4) --- */

	dnsOn() {
		const res = this.dns;

		if (!(res instanceof Error) && L.isObject(res?.dns))
			return res.dns.encrypted === true;

		const st = this.status;

		return (!(st instanceof Error) && L.isObject(st?.dns)) ? st.dns.encrypted === true : undefined;
	},

	renderDiagnose() {
		const res = this.diagnose;
		const title = _('How the provider blocks');

		if (zc.isUsageError(res)) {
			dom.content(this.diagnoseNode, zc.section(title, null, [
				E('div', { 'class': 'alert-message notice' }, txt(_('The installed zaprett service cannot find out how the provider blocks yet. Update the "zaprett" package.')))
			]));

			return;
		}

		const nodes = [];

		if (res instanceof Error)
			nodes.push(zc.errorBox(res, _('Could not get the result of the last check')));
		else if (!this.diagnoseBusy)
			nodes.push(...zh.diagnoseNodes(L.isObject(res?.diagnose) ? res.diagnose : null, this.dnsOn(), {
				dns: ui.createHandlerFn(this, 'handleDns'),
				strategies: E('a', { 'class': 'cbi-button cbi-button-action', 'href': zc.pageUrl('strategies') }, txt(_('Open "Strategies"')))
			}));

		dom.content(this.diagnoseNode, zc.section(title,
			_('zaprett opens the check addresses of the enabled services from the router and finds out how the provider blocks them: by substituting DNS answers, by IP address, by the site name, by slowing down, or with a stub page. The answer tells what helps: a strategy, encrypted DNS or only a VPN. The check changes no settings and does not restart the bypass.'),
			[
				E('div', { 'style': 'margin-bottom:.5em' }, [
					zc.button(_('Find out how the provider blocks'), ui.createHandlerFn(this, 'handleDiagnose'), 'action', { 'disabled': this.diagnoseBusy ? '' : null })
				]),
				this.diagnoseBox.node,
				this.diagnoseResultNode,
				E('div', {}, nodes)
			]));
	},

	/* Starts the diagnosis, or with watchOnly follows the one already running. */
	handleDiagnose(ev, watchOnly) {
		this.diagnoseBusy = true;
		dom.content(this.diagnoseResultNode, '');
		this.renderDiagnose();

		const task = watchOnly
			? zh.runTask(() => Promise.resolve({ job: { name: 'diagnose' } }), this.diagnoseBox).then(r => {
				if (r.job && r.job.state != 'done')
					throw new Error(r.job.message || zc.jobStateLabel(r.job.state));

				return zc.run(zc.callDiagnoseStatus);
			})
			: zh.runDiagnose(undefined, this.diagnoseBox).then(diag => ({ ok: true, diagnose: diag }));

		return task.then(res => {
			this.diagnose = res;
		}).catch(err => {
			if (zc.isUsageError(err))
				this.diagnose = err;
			else
				dom.content(this.diagnoseResultNode, zc.errorBox(err, _('Could not find out how the provider blocks')));
		}).finally(() => {
			this.diagnoseBusy = false;
			this.diagnoseBox.update(null);
			this.renderDiagnose();
		});
	},

	handleDns(ev) {
		return zh.setupDns(this.diagnoseBox).then(result => {
			if (!result)
				return;

			if (result.dns)
				this.dns = { ok: true, dns: result.dns };

			this.renderDiagnose();
			dom.content(this.diagnoseResultNode, zh.dnsSetupResult(result));
		}).catch(err => {
			dom.content(this.diagnoseResultNode, zc.errorBox(err, _('Could not turn on encrypted DNS')));
		}).finally(() => this.diagnoseBox.update(null));
	},

	/* --- monitor --- */

	renderMonitor(res) {
		if (zc.isUsageError(res) || (!(res instanceof Error) && !L.isObject(res?.monitor)))
			return '';

		const title = _('Availability monitor');

		if (res instanceof Error)
			return zc.section(title, null, [ zc.errorBox(res, _('Could not get the state of the monitor')) ]);

		if (!res.monitor.enabled)
			return zc.section(title, null, [ E('p', {}, txt(_('The monitor is off. It can be turned on in "Settings".'))) ]);

		return zc.section(title, null, zh.monitorNodes(res.monitor));
	},

	/* --- last background task --- */

	/* jobRes: answer of "job status" or an error; logRes: answer of
	 * "job log", an error, or undefined to keep the log shown now. */
	showJob(jobRes, logRes) {
		if (jobRes instanceof Error) {
			dom.content(this.jobNode, zc.errorBox(jobRes, _('Could not get the task state')));

			return;
		}

		const job = L.isObject(jobRes?.job) ? jobRes.job : null;

		if (!job) {
			this.logText = '';
			dom.content(this.jobNode, E('p', {}, txt(_('No background tasks have run since the router started.'))));

			return;
		}

		if (logRes !== undefined)
			this.logText = (logRes instanceof Error || !logRes) ? '' : String(logRes.log ?? logRes.text ?? '');

		const rows = [
			[ _('Task'), zc.jobLabel(job.name) ],
			[ _('State'), zc.jobStateLabel(job.state) ],
			[ _('Progress'), '%d%%'.format(+job.progress || 0) ],
			[ _('Message'), job.message ?? '' ],
			[ _('Started'), zc.formatTime(job.started) ],
			[ _('Finished'), (job.state == 'running') ? '—' : zc.formatTime(job.finished) ],
			[ _('Exit code'), (job.state == 'running') ? '—' : String(job.rc ?? '') ]
		];

		dom.content(this.jobNode, [
			E('table', { 'class': 'table' }, rows.map(r => E('tr', { 'class': 'tr' }, [
				E('td', { 'class': 'td left', 'style': 'width:33%' }, [ E('strong', {}, txt(r[0])) ]),
				E('td', { 'class': 'td left' }, txt(r[1]))
			]))),
			(job.state == 'running') ? E('div', {}, [ zc.button(_('Cancel task'), ui.createHandlerFn(this, 'handleCancel'), 'negative') ]) : '',
			E('h5', {}, txt(_('Task log (last %d lines)').format(LOG_TAIL))),
			(job.state == 'running') ? E('div', { 'class': 'cbi-value-description' }, txt(_('The log is updated when the task finishes; press "Refresh" to see it now.'))) : '',
			(logRes instanceof Error)
				? zc.errorBox(logRes, _('Could not read the task log'))
				: E('pre', { 'style': PRE_STYLE }, txt(this.logText || _('The log is empty.')))
		]);

		if (job.state == 'running')
			this.watchJob();
	},

	handleJob(ev) {
		return Promise.all([
			zc.run(zc.callJobStatus).catch(err => err),
			zc.run(zc.callJobLog, LOG_TAIL).catch(err => err)
		]).then(data => this.showJob(data[0], data[1]));
	},

	/* One "job status" per tick while the task runs; the log is read once
	 * more when it finishes. */
	watchJob() {
		if (this.stopWatch)
			return;

		this.stopWatch = zc.watchJob(job => {
			if (job?.state == 'running')
				this.showJob({ job: job }, undefined);
		}, () => {
			this.stopWatch = null;

			return this.handleJob();
		}, 3);
	},

	handleCancel(ev) {
		return zc.cancelJob().then(() => this.handleJob());
	},

	handleCheck(ev) {
		return zc.run(zc.callCheck).then(res => zc.renderCheck(res, null), err => {
			if (!err?.zaprett || err.code == 'backend_error' || err.code == 'timeout')
				throw err;

			return zc.renderCheck(null, err);
		}).then(nodes => dom.content(this.checkNode, nodes))
			.catch(err => dom.content(this.checkNode, zc.errorBox(err, _('Could not check the configuration'))));
	},

	handleSaveApply: null,
	handleSave: null,
	handleReset: null
});
