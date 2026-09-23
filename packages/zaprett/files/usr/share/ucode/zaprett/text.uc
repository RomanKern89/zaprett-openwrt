// zaprett: human-readable (Russian) output of CLI results.
'use strict';

export const WARNINGS = {
	bad_config: 'в /etc/config/zaprett есть неверные значения, вместо них взяты значения по умолчанию',
	generate_failed: 'не удалось подготовить аргументы движка (zaprett check покажет причину)',
	engine_missing: 'движок не установлен (пакет zaprett-nfqws или zaprett-nfqws2)',
	nft_queue_missing: 'в ядре нет модуля nft_queue (пакет kmod-nft-queue)',
	no_strategy: 'стратегия не выбрана',
	strategy_missing: 'выбранная стратегия не найдена среди установленных',
	not_running: 'служба включена, но движок не работает',
	nft_not_applied: 'движок работает, но правила nftables не установлены',
	no_wan: 'не найден WAN-интерфейс с маршрутом по умолчанию — трафик не будет обрабатываться',
	test_running: 'идёт автоподбор стратегии',
	flow_offload_enabled: 'включено программное/аппаратное ускорение (flow offloading) — обход может не работать',
	no_active_lists: 'не включено ни одного непустого списка доменов — обход не сработает ни для одного сайта',
	list_missing: 'часть включённых списков не найдена (удалены?)',
	strategy_option_ignored: 'часть опций стратегии проигнорирована: ими управляет zaprett',
	empty_profile_removed: 'из стратегии удалены пустые профили (лишние --new)',
	wide_port_range: 'стратегия перехватывает широкий диапазон портов — возможна повышенная нагрузка на процессор',
	config_was_invalid: 'конфигурация и до изменения была с ошибками',
	source_not_downloaded: 'включённая подписка ещё не загружена — выполните zaprett sources update',
	profile_unfiltered: 'часть профилей стратегии действует на весь трафик своих портов, без списков',
	ipv6_wan_unhandled: 'у WAN есть IPv6, а обработка IPv6 выключена — IPv6-соединения идут без обхода',
	low_memory: 'включённые сервисы или подписки тяжелы для памяти этого роутера',
	monitor_degraded: 'монитор доступности: сервисы открываются плохо несколько проверок подряд',
	flowtable_failed: 'собственная flowtable zaprett не установлена (нет устройств или ядро её не приняло) — соединения идут без ускорения',
	game_filter_no_ipsets: 'игровой фильтр включён, но нет ни одного включённого списка IP-сетей — игровой профиль не добавлен',
	dns_plain: 'включён сервис, которому нужен шифрованный DNS, а на роутере обычный DNS (zaprett dns setup)'
};

const VERDICT_TEXT = {
	ok: 'открывается',
	dns_spoof: 'подмена DNS',
	ip_block: 'блокировка по IP',
	tls_block: 'обрыв TLS',
	throttle: 'замедление (замирает на 14–24 КБ)',
	http_block: 'заглушка провайдера',
	unknown: 'не удалось определить'
};

const ENSURE_TEXT = {
	none: 'Сторож: всё в порядке',
	started: 'Сторож: движок не работал и запущен заново',
	fw_applied: 'Сторож: правила nftables восстановлены',
	skipped: 'Сторож: идёт автоподбор, проверка пропущена'
};

const MONITOR_STATE = { unknown: 'нет данных', ok: 'в порядке', degraded: 'плохая доступность', repairing: 'идёт автоподбор' };

export function warning_text(code) {
	return WARNINGS[code] ?? code;
};

function yesno(v) {
	return v ? 'да' : 'нет';
}

function warn_block(list) {
	if (type(list) != 'array' || !length(list))
		return '';
	return 'Предупреждения:\n' + join('\n', map(list, (w) => '  - ' + warning_text(w))) + '\n';
}

function fmt_status(r) {
	let s = '';
	s += sprintf('Служба:        %s%s\n', r.running ? 'работает' : 'остановлена', r.pid ? sprintf(' (pid %d)', r.pid) : '');
	s += sprintf('Включена:      %s (автозапуск: %s)\n', yesno(r.enabled), yesno(r.autostart));
	s += sprintf('Движок:        %s %s\n', r.engine, r.engine_version ?? '(не установлен)');
	s += sprintf('Стратегия:     %s\n', r.strategy?.id ?? '(не выбрана)');
	s += sprintf('Режим списков: %s\n', (r.list_mode == 'blacklist') ? 'blacklist (обход всего, кроме исключений)' : 'whitelist (обход только по спискам)');
	s += sprintf('Списки:        %s\n', length(r.lists) ? join(', ', r.lists) : '—');
	s += sprintf('Исключения:    %s\n', length(r.exclude_lists) ? join(', ', r.exclude_lists) : '—');
	s += sprintf('IP-сети:       %s\n', length(r.ipsets) ? join(', ', r.ipsets) : '—');
	s += sprintf('Правила nft:   %s\n', r.nft_applied ? 'установлены' : 'не установлены');
	s += sprintf('WAN:           %s\n', length(r.wan) ? join(', ', r.wan) : '—');
	s += sprintf('Ускорение fw4: %s (режим %s%s)\n', r.flow_offload?.fw4 ? 'включено' : 'выключено', r.flow_offload?.mode,
		r.flow_offload?.own ? ', своя flowtable установлена' : '');
	if (r.dns)
		s += sprintf('Шифрованный DNS: %s\n', r.dns.encrypted ? ('да, ' + r.dns.provider) : 'нет');
	s += sprintf('Версия:        %s\n', r.version);
	return s + warn_block(r.warnings);
}

function fmt_items(r) {
	let s = '';
	for (let it in r.items) {
		s += sprintf('%-14s %-40s %-7s %s%s\n', it.type, it.id, it.source,
			it.active ? '[вкл] ' : '', (it.entries != null) ? sprintf('%d записей', it.entries) : (it.version ? 'v' + it.version : ''));
	}
	return s;
}

function fmt_repo_list(r) {
	if (r.fetched_at == null)
		return 'Индекс репозитория ещё не загружен. Выполните: zaprett repo fetch\n';
	let s = sprintf('Индекс загружен: %s\n', r.url);
	for (let it in r.items) {
		let st = it.installed ? (it.update_available ? 'есть обновление' : 'установлен') : (it.supported ? '' : 'не поддерживается');
		s += sprintf('%-14s %-40s %-8s %s\n', it.type, it.id, it.version ?? '?', st);
	}
	return s;
}

function fmt_sources(r) {
	let s = '';
	for (let x in r.sources)
		s += sprintf('%-24s %-14s %-8s %-6s %s%s\n', x.name, x.type, x.enabled ? 'вкл' : 'выкл', x.status,
			(x.entries != null) ? sprintf('%d записей', x.entries) : '', x.message ? (' — ' + x.message) : '');
	return s || 'Подписок нет.\n';
}

function fmt_check(r) {
	let s = sprintf('Стратегия %s (%s) — проверка движком пройдена.\n', r.strategy?.id, r.engine);
	s += sprintf('Порты TCP: %s\nПорты UDP: %s\n', join(',', r.ports?.tcp ?? []) || '—', join(',', r.ports?.udp ?? []) || '—');
	s += 'Аргументы:\n' + join('\n', map(r.args ?? [], (a) => '  ' + a)) + '\n';
	return s + warn_block(r.warnings);
}

function fmt_job(j) {
	if (!j)
		return 'Фоновых задач нет.\n';
	return sprintf('Задача %s (%s): %s, %d%%\n%s\n', j.name, j.id, j.state, j.progress ?? 0, j.message ?? '');
}

// Reasons of mode exclusive of an automatic selection (contract v1.6 §17).
const MODE_REASON_TEXT = {
	forced: 'выбран флагом --exclusive',
	engine_not_running: 'служба не работала',
	no_test_user: 'нет пользователя zaprett-test',
	qnum_out_of_range: 'номер очереди 65535 — нет места для второй очереди',
	mark_conflict: 'метки desync_mark/postnat_mark пересекаются с меткой проверки',
	write_failed: 'не удалось записать файлы проверки',
	nft_rejected: 'nftables не принял правила проверки',
	instance_failed: 'второй экземпляр движка не запустился'
};

function fmt_test(r) {
	let s = fmt_job(r.job);
	let res = r.results;
	if (!res)
		return s + 'Результатов автоподбора нет.\n';
	if (res.mode == 'isolated')
		s += 'Режим: обход для сети не отключался (проверка отдельным экземпляром движка)\n';
	else if (res.mode == 'exclusive')
		s += sprintf('Режим: на время подбора движок останавливался (%s)\n', MODE_REASON_TEXT[res.mode_reason] ?? res.mode_reason ?? '—');
	if (res.baseline)
		s += sprintf('Без обхода доступно: %d из %d\n', res.baseline.ok, res.baseline.total);
	for (let x in (res.results ?? []))
		s += sprintf('  %-45s %s %d/%d%s\n', x.id, x.status, x.ok, x.total, (x.avg_ms != null) ? sprintf(' %d мс', x.avg_ms) : '');
	return s;
}

function fmt_probe(p) {
	if (!p)
		return 'Проверок сервисов ещё не было. Запуск: zaprett probe\n';
	let s = sprintf('Доступно %d из %d адресов (движок %s)\n', p.ok, p.total, p.engine_running ? 'работает' : 'не работает');
	for (let x in (p.services ?? [])) {
		s += sprintf('  %-20s %d/%d%s\n', x.id, x.ok, x.total, (x.avg_ms != null) ? sprintf(' %d мс', x.avg_ms) : '');
		for (let t in (x.targets ?? []))
			s += sprintf('    %s %s%s\n', t.ok ? 'OK  ' : 'FAIL', t.url, t.error ? (' — ' + t.error) : '');
	}
	return s;
}

function fmt_dns(d) {
	return d?.encrypted ? sprintf('Шифрованный DNS работает: %s\n', d.provider) :
		'Шифрованного DNS нет: запросы идут открытым текстом. Включить: zaprett dns setup\n';
}

function fmt_diagnose(d) {
	if (!d)
		return 'Диагностики ещё не было. Запуск: zaprett diagnose\n';
	let s = sprintf('Итог: %s (движок %s)\n', VERDICT_TEXT[d.summary?.verdict] ?? d.summary?.verdict,
		d.engine_running ? 'работает' : 'не работает');
	for (let t in (d.targets ?? []))
		s += sprintf('  %-14s %s\n      %s\n', t.verdict, t.url, t.detail ?? '');
	return s;
}

function fmt_monitor(m) {
	let s = sprintf('Монитор: %s, состояние: %s\n', m.enabled ? 'включён' : 'выключен', MONITOR_STATE[m.state] ?? m.state);
	s += sprintf('Раз в %d мин, порог %d, авторемонт: %s\n', m.interval, m.threshold, m.auto_repair ? 'да' : 'нет');
	if (m.checked_at != null)
		s += sprintf('Последняя проверка: доступно %d из %d, неудачных подряд: %d\n', m.ok, m.total, m.consecutive_failures);
	if (m.last_repair)
		s += sprintf('Последний авторемонт: задача %s\n', m.last_repair.job_id);
	return s;
}

export function render(cmd, r) {
	if (!r.ok) {
		let s = 'Ошибка: ' + (r.message ?? r.error) + '\n';
		if (type(r.errors) == 'array')
			for (let e in r.errors)
				s += sprintf('  строка %d: %s\n', e.line, e.value);
		if (r.dry_run?.output && r.error != 'dry_run_failed')
			s += r.dry_run.output + '\n';
		return s;
	}
	switch (cmd) {
	case 'status':
		return fmt_status(r);
	case 'items':
		return fmt_items(r);
	case 'repo list':
		return fmt_repo_list(r);
	case 'sources list':
		return fmt_sources(r);
	case 'check':
		return fmt_check(r);
	case 'gen-args':
		return r.path + '\n';
	case 'fw show':
	case 'diag':
		return r.text;
	case 'strategy show':
		return r.text + (r.args ? ('\n--- аргументы движка ---\n' + join('\n', r.args) + '\n') : ('\n' + (r.build_message ?? '') + '\n'));
	case 'user get':
		return r.text;
	case 'job status':
		return fmt_job(r.job);
	case 'job log':
		return r.log + '\n';
	case 'test status':
		return fmt_test(r);
	case 'ensure':
		return (ENSURE_TEXT[r.action] ?? r.action) + '\n';
	case 'probe status':
		return fmt_probe(r.probe);
	case 'monitor status':
		return fmt_monitor(r.monitor);
	case 'dns status':
		return fmt_dns(r.dns);
	case 'diagnose status':
		return fmt_diagnose(r.diagnose);
	case 'monitor run':
		return r.skipped ? sprintf('Проверка пропущена: %s\n', r.skipped) :
			sprintf('Доступно %d из %d, состояние: %s\n', r.reachable, r.total, MONITOR_STATE[r.state] ?? r.state);
	case 'log':
		return length(r.lines) ? (join('\n', r.lines) + '\n') : 'В журнале нет строк zaprett.\n';
	case 'page':
		return sprintf('%.J\n', r);
	case 'version':
		return sprintf('zaprett %s\nnfqws: %s\nnfqws2: %s\n', r.version, r.nfqws ?? '—', r.nfqws2 ?? '—');
	case 'presets': {
		let s = '';
		for (let x in r.services)
			s += sprintf('%-12s %-30s %s\n', x.id, x.name ?? '', x.enabled ? '[включён]' : '');
		return s;
	}
	}
	if (cmd == 'dns setup' && r.changed === false && !r.job)
		return fmt_dns(r.dns);
	let s = (r.message ?? 'Готово') + '\n' + warn_block(r.warnings);
	if (r.job)
		s = sprintf('Задача запущена: %s (%s). Ход выполнения: zaprett job status\n', r.job.name, r.job.id);
	if (r.job_error)
		s += 'Фоновая задача не запущена: ' + (r.job_error.message ?? r.job_error.code) + '\n';
	return s;
};
