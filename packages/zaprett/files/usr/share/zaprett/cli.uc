// zaprett command line interface (ARCHITECTURE §6.2).
// Invoked as: ucode -S -- /usr/share/zaprett/cli.uc <command> ... [--json]
'use strict';

import * as fs from 'fs';
import { VERSION, is_id, fail } from 'zaprett.util';
import * as CMD from 'zaprett.commands';
import * as TXT from 'zaprett.text';
import * as J from 'zaprett.job';

const USAGE = `Использование: zaprett <команда> [аргументы] [--json]

Служба:
  status                          состояние
  start | stop | restart          управление движком
  enable | disable                включить/выключить (автозапуск)
  check                           проверить настройки без запуска
Списки и стратегии:
  items [--type <тип>]            установленные элементы
  list enable|disable <id>        включить/выключить список
  strategy set|show <id>          выбрать / показать стратегию
  strategy save <id> < файл       сохранить свою стратегию (id начинается с user-)
  strategy delete <id>            удалить свою стратегию
  user get <id>                   показать свой список (user-hosts, user-hosts-exclude, user-ipset, user-ipset-exclude)
  user set <id> < файл            сохранить свой список
  mode whitelist|blacklist        режим списков
  engine nfqws|nfqws2             движок
Репозиторий:
  repo fetch | repo list [--type <тип>]
  repo install <id>... | repo remove <id> | repo upgrade [--all | <id>...]
Подписки на внешние списки по ссылке:
  sources list | sources update [<имя>...]
  sources save <имя> < json      {"title","type","url","interval_hours","min_entries","enabled"}
  sources delete <имя>
  sources defaults                вернуть подписки по умолчанию, кроме удалённых пользователем
Быстрая настройка и автоподбор:
  presets | wizard apply <сервис>[:<вариант>]...   варианты сервиса — в presets (поле variants)
  test start [--strategies id,id] [--quick] [--apply-if-better] [--exclusive] | test status [--brief] | test stop | test apply <id>
                                  автоподбор идёт рядом с работающим обходом; --exclusive — с остановкой движка
Проверка и наблюдение:
  ensure                          сторож: запустить движок или правила, если они пропали (для cron)
  probe [--services id,id]        проверить доступность сервисов, не трогая движок (задача)
  probe status                    результат последней проверки
  monitor run | monitor status    одна проверка монитора (для cron) / состояние и история
  log [--tail N]                  строки системного журнала zaprett и nfqws (по умолчанию 200, не больше 1000)
  page overview|lists|strategies|diagnostics   данные страницы веб-интерфейса одним вызовом
  diagnose [--services id,id]     чем блокирует провайдер: DNS, IP, TLS, замедление, заглушка (задача)
  diagnose status                 результат последней диагностики
Шифрованный DNS:
  dns status                      работает ли шифрованный DNS (https-dns-proxy, stubby, dnscrypt-proxy)
  dns setup                       установить и запустить https-dns-proxy (задача)
Фоновые задачи:
  job status | job log [--tail N] | job cancel
Прочее:
  fw apply | fw remove | fw show | gen-args | version
  diag [--full]                   быстрая диагностика; --full добавляет список пакетов и журнал (по SSH)

Флаги: --json (вывод JSON), --foreground (выполнить задачу сразу), --quiet (без вывода при успехе)
`;

const TYPES_WITH_VALUE = { '--type': 'type', '--tail': 'tail', '--strategies': 'strategies', '--services': 'services' };

function parse_args(argv) {
	let flags = { json: false, foreground: false, quiet: false, all: false, quick: false, if_running: false, if_applied: false, full: false,
		apply_if_better: false, brief: false, exclusive: false };
	let opts = {}, pos = [];
	for (let i = 0; i < length(argv); i++) {
		let a = argv[i];
		if (a == '--json')
			flags.json = true;
		else if (a == '--foreground')
			flags.foreground = true;
		else if (a == '--quiet')
			flags.quiet = true;
		else if (a == '--all')
			flags.all = true;
		else if (a == '--quick')
			flags.quick = true;
		else if (a == '--if-running')
			flags.if_running = true;
		else if (a == '--if-applied')
			flags.if_applied = true;
		else if (a == '--full')
			flags.full = true;
		else if (a == '--apply-if-better')
			flags.apply_if_better = true;
		else if (a == '--brief')
			flags.brief = true;
		else if (a == '--exclusive')
			flags.exclusive = true;
		else if (TYPES_WITH_VALUE[a]) {
			if (i + 1 >= length(argv))
				return { error: 'Не указано значение для ' + a };
			opts[TYPES_WITH_VALUE[a]] = argv[++i];
		}
		else if (substr(a, 0, 2) == '--')
			return { error: 'Неизвестный флаг ' + a };
		else
			push(pos, a);
	}
	if (opts.strategies != null) {
		let ids = filter(split(opts.strategies, ','), (x) => x != '');
		for (let id in ids)
			if (!is_id(id))
				return { error: 'Недопустимый идентификатор стратегии: ' + id };
		flags.strategies = ids;
	}
	if (opts.services != null) {
		let ids = filter(split(opts.services, ','), (x) => x != '');
		if (!length(ids))
			return { error: 'Не указаны сервисы в --services' };
		for (let id in ids)
			if (!is_id(id))
				return { error: 'Недопустимый идентификатор сервиса: ' + id };
		flags.services = ids;
	}
	return { flags: flags, opts: opts, pos: pos };
}

function need(pos, n) {
	return length(pos) >= n;
}

function ids_valid(list) {
	for (let id in list)
		if (!is_id(id))
			return false;
	return true;
}

function srcnames_valid(list) {
	// длина через length(), см. util.is_id
	for (let n in list)
		if (length(n) < 1 || length(n) > 32 || !match(n, /^[a-z0-9_]+$/))
			return false;
	return true;
}

function usage_error(msg) {
	return fail('usage', msg ?? 'Неверные аргументы. Справка: zaprett help');
}

// Returns [ command name for rendering, result ].
function dispatch(p) {
	let pos = p.pos, flags = p.flags, opts = p.opts;
	let c = pos[0], sub = pos[1];
	switch (c) {
	case 'status': return [ c, CMD.status() ];
	case 'start': return [ c, CMD.start() ];
	case 'stop': return [ c, CMD.stop() ];
	case 'restart': return [ c, CMD.restart() ];
	case 'enable': return [ c, CMD.enable() ];
	case 'disable': return [ c, CMD.disable() ];
	case 'check': return [ c, CMD.check() ];
	case 'gen-args': return [ c, CMD.gen_args() ];
	case 'version': return [ c, CMD.version() ];
	case 'diag': return [ c, CMD.diag(flags) ];
	case 'items': return [ c, CMD.items(opts) ];
	case 'presets': return [ c, CMD.presets() ];

	case 'fw':
		if (sub == 'apply') return [ 'fw apply', CMD.fw_apply(flags) ];
		if (sub == 'remove') return [ 'fw remove', CMD.fw_remove() ];
		if (sub == 'show') return [ 'fw show', CMD.fw_show() ];
		break;

	case 'list':
		if ((sub == 'enable' || sub == 'disable') && need(pos, 3))
			return [ 'list ' + sub, CMD.list_toggle(pos[2], sub == 'enable') ];
		break;

	case 'strategy':
		if (sub == 'set' && need(pos, 3)) return [ 'strategy set', CMD.strategy_set(pos[2]) ];
		if (sub == 'show' && need(pos, 3)) return [ 'strategy show', CMD.strategy_show(pos[2]) ];
		if (sub == 'delete' && need(pos, 3)) return [ 'strategy delete', CMD.strategy_delete(pos[2]) ];
		if (sub == 'save' && need(pos, 3)) {
			let text = CMD.read_stdin(65536);
			return [ 'strategy save', (text == null) ? fail('too_large', 'Текст стратегии больше 64 КиБ') : CMD.strategy_save(pos[2], text) ];
		}
		break;

	case 'user':
		if (sub == 'get' && need(pos, 3)) return [ 'user get', CMD.user_get(pos[2]) ];
		if (sub == 'set' && need(pos, 3)) return [ 'user set', CMD.user_set(pos[2], CMD.read_stdin(CMD.MAX_STDIN)) ];
		break;

	case 'mode':
		if (need(pos, 2)) return [ c, CMD.set_mode(sub) ];
		break;

	case 'engine':
		if (need(pos, 2)) return [ c, CMD.set_engine(sub) ];
		break;

	case 'wizard':
		// <сервис> или <сервис>:<вариант> (контракт v1.7 §16.4)
		if (sub == 'apply' && need(pos, 3) && length(filter(slice(pos, 2), (x) => CMD.parse_service_ref(x) == null)) == 0)
			return [ 'wizard apply', CMD.wizard_apply(slice(pos, 2)) ];
		break;

	case 'repo':
		if (sub == 'fetch') return [ 'repo fetch', CMD.run_job('repo-fetch', [], flags) ];
		if (sub == 'list') return [ 'repo list', CMD.repo_list(opts) ];
		if (sub == 'install' && need(pos, 3) && ids_valid(slice(pos, 2)))
			return [ 'repo install', CMD.run_job('repo-install', slice(pos, 2), flags) ];
		if (sub == 'remove' && length(pos) == 3 && is_id(pos[2]))
			return [ 'repo remove', CMD.run_job('repo-remove', [ pos[2] ], flags) ];
		if (sub == 'upgrade' && ids_valid(slice(pos, 2)) && (flags.all != (length(pos) > 2)))
			return [ 'repo upgrade', CMD.run_job(flags.quiet && flags.all && flags.foreground ? 'autoupdate' : 'repo-upgrade', slice(pos, 2), flags) ];
		break;

	case 'sources':
		if (sub == 'list') return [ 'sources list', CMD.sources_list() ];
		if (sub == 'update' && srcnames_valid(slice(pos, 2)))
			return [ 'sources update', CMD.run_job('sources-update', slice(pos, 2), flags) ];
		if (sub == 'save' && length(pos) == 3)
			return [ 'sources save', CMD.sources_save(pos[2], CMD.read_stdin(65536)) ];
		if (sub == 'delete' && length(pos) == 3) return [ 'sources delete', CMD.sources_delete(pos[2]) ];
		if (sub == 'defaults' && length(pos) == 2) return [ 'sources defaults', CMD.sources_defaults() ];
		break;

	case 'test':
		if (sub == 'start') return [ 'test start', CMD.run_job('test', [], flags) ];
		if (sub == 'status') return [ 'test status', CMD.test_status(flags) ];
		if (sub == 'stop') return [ 'test stop', CMD.test_stop() ];
		if (sub == 'apply' && need(pos, 3)) return [ 'test apply', CMD.test_apply(pos[2]) ];
		break;

	case 'job':
		if (sub == 'status') return [ 'job status', CMD.job_status() ];
		if (sub == 'log') return [ 'job log', CMD.job_log(opts) ];
		if (sub == 'cancel') return [ 'job cancel', CMD.job_cancel() ];
		break;

	case 'offload':
		if (sub == 'apply') return [ 'offload apply', CMD.offload_apply() ];
		if (sub == 'restore') return [ 'offload restore', CMD.offload_restore() ];
		if (sub == 'status') return [ 'offload status', CMD.offload_status() ];
		break;

	case 'cron':
		if (sub == 'sync') return [ 'cron sync', CMD.cron_sync() ];
		break;

	case 'ensure':
		if (length(pos) == 1) return [ c, CMD.ensure() ];
		break;

	case 'probe':
		if (sub == 'status' && length(pos) == 2) return [ 'probe status', CMD.probe_status() ];
		if (length(pos) == 1) return [ 'probe', CMD.run_job('probe', [], flags) ];
		break;

	case 'monitor':
		if (sub == 'run' && length(pos) == 2) return [ 'monitor run', CMD.monitor_run() ];
		if (sub == 'status' && length(pos) == 2) return [ 'monitor status', CMD.monitor_status() ];
		break;

	case 'log':
		if (length(pos) == 1) return [ c, CMD.log_lines(opts) ];
		break;

	case 'dns':
		if (sub == 'status' && length(pos) == 2) return [ 'dns status', CMD.dns_status() ];
		if (sub == 'setup' && length(pos) == 2) return [ 'dns setup', CMD.dns_setup(flags) ];
		break;

	case 'diagnose':
		if (sub == 'status' && length(pos) == 2) return [ 'diagnose status', CMD.diagnose_status() ];
		if (length(pos) == 1) return [ 'diagnose', CMD.run_job('diagnose', [], flags) ];
		break;

	case 'page':
		if (length(pos) == 2 && CMD.PAGES[sub]) return [ 'page', CMD.page(sub) ];
		break;
	}
	return [ c ?? '', usage_error() ];
}

function output(cmd, res, flags) {
	if (flags.json) {
		print(sprintf('%J\n', res));
		return;
	}
	if (res.ok && flags.quiet)
		return;
	let text = TXT.render(cmd, res);
	if (res.ok)
		print(text);
	else
		fs.stderr.write(text);
}

function main(argv) {
	// background job runner: __job-run <id> <name> [args...]
	if (argv[0] == '__job-run') {
		if (length(argv) < 3 || !CMD.JOB_HANDLERS[argv[2]])
			return 2;
		let p = parse_args(slice(argv, 3));
		if (p.error)
			return 2;
		let handler = CMD.JOB_HANDLERS[argv[2]];
		return J.runner(argv[1], argv[2], (ctx) => handler(ctx, p.pos, p.flags));
	}

	let p = parse_args(argv);
	let json_mode = (index(argv, '--json') >= 0);
	if (p.error) {
		output('', usage_error(p.error), { json: json_mode });
		return 2;
	}
	if (!length(p.pos) || p.pos[0] == 'help') {
		if (json_mode)
			print(sprintf('%J\n', { ok: true, usage: USAGE, version: VERSION }));
		else
			print(USAGE);
		return length(p.pos) ? 0 : 2;
	}

	let res;
	let cmd = p.pos[0];
	try {
		let d = dispatch(p);
		cmd = d[0];
		res = d[1];
	}
	catch (e) {
		res = fail('internal_error', 'Внутренняя ошибка: ' + e.message, { stacktrace: e.stacktrace?.[0]?.context });
	}
	output(cmd, res, p.flags);
	if (res.ok)
		return 0;
	return (res.error == 'usage') ? 2 : 1;
}

exit(main(ARGV));
