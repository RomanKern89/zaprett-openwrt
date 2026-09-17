// SPDX-License-Identifier: MIT
// zaprett: diagnostics page - diagnostic report, last background task and
// its log, configuration check.

'use strict';
'require view';
'require dom';
'require poll';
'require ui';
'require zaprett.common as zc';

const txt = zc.txt;

const LOG_TAIL = 300;

const PRE_STYLE = 'white-space:pre-wrap;word-break:break-all;max-height:30em;overflow:auto';

return view.extend({
	load() {
		return Promise.all([
			zc.run(zc.callDiag).catch(err => err),
			zc.run(zc.callJobStatus).catch(err => err),
			zc.run(zc.callJobLog, LOG_TAIL).catch(err => err)
		]);
	},

	render(data) {
		this.diagText = '';
		this.logText = '';
		this.diagNode = E('div', {});
		this.jobNode = E('div', {});
		this.checkNode = E('div', {});

		this.showDiag(data[0]);
		this.showJob(data[1], data[2]);

		return E([], [
			E('h2', {}, txt(_('Diagnostics'))),
			E('div', { 'class': 'cbi-map-descr' }, txt(_('Information for finding problems. If you ask for help, attach the diagnostic report: it contains versions, the firewall table of zaprett, the service state and recent log messages, but no passwords.'))),
			zc.section(_('Diagnostic report'), null, [
				E('div', { 'style': 'margin-bottom:.5em' }, [
					zc.button(_('Refresh'), ui.createHandlerFn(this, 'handleDiag'), 'reload'), ' ',
					zc.button(_('Copy'), ui.createHandlerFn(this, () => zc.copyText(this.diagText)), 'action'), ' ',
					zc.button(_('Download'), ui.createHandlerFn(this, () => zc.downloadText('zaprett-diag.txt', this.diagText)), 'action')
				]),
				this.diagNode
			]),
			zc.section(_('Last background task'), _('Downloads from the repository, updates and automatic strategy selection run in the background. Here you can see the last such task and its log.'), [
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

	showJob(jobRes, logRes) {
		if (jobRes instanceof Error) {
			dom.content(this.jobNode, zc.errorBox(jobRes, _('Could not get the task state')));

			return;
		}

		const job = L.isObject(jobRes.job) ? jobRes.job : null;

		if (!job) {
			this.logText = '';
			dom.content(this.jobNode, E('p', {}, txt(_('No background tasks have run since the router started.'))));

			return;
		}

		this.logText = (logRes instanceof Error) ? '' : String(logRes.log ?? logRes.text ?? '');

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

	watchJob() {
		if (this.stopWatch)
			return;

		this.stopWatch = zc.watchJob(job => {
			if (job?.state == 'running')
				this.handleJob();
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
