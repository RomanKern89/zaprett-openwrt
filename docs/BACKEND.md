# zaprett для OpenWrt — бэкенд (пакет `zaprett`)

> Версия 1.0.0 (2026-09-17), дополнено 2026-09-22. Реализация контракта `docs/ARCHITECTURE.md` **v1.4** (§3–§10, §14,
> §15.1–§15.5, серверная часть §15.7): сторож движка, монитор доступности с авторемонтом, живая проверка сервисов,
> `page`, `log`, пределы загрузок, только https для каталога, английские метаданные; v1.4 — собственная flowtable
> (`flow_offload=own`), блокировка QUIC, игровой фильтр, шифрованный DNS (`dns status/setup`), диагностика
> «чем блокирует провайдер» (`diagnose`). Подробно v1.4 — раздел 13. Отклонения от контракта — раздел 12.

---

## 1. Состав пакета

```
Makefile                                   OpenWrt-пакет, PKGARCH:=all
files/etc/config/zaprett                   UCI по умолчанию (enabled '0', 4 подписки выключены)
files/etc/init.d/zaprett                   procd-служба (START=95, STOP=10)
files/etc/uci-defaults/90-zaprett          fw4 include, недостающие подписки по умолчанию, cron; идемпотентно
files/etc/hotplug.d/iface/60-zaprett       ifup/ifdown -> обновить WAN в nft
files/etc/zaprett/user/*.txt               пустые пользовательские листы (conffiles)
files/lib/upgrade/keep.d/zaprett           /etc/zaprett/ сохраняется при sysupgrade
files/usr/bin/zaprett                      sh-обёртка: exec /usr/bin/ucode -S -- /usr/share/zaprett/cli.uc "$@"
files/usr/share/zaprett/cli.uc             разбор аргументов, вывод (JSON / русский текст)
files/usr/share/zaprett/probe.sh           параллельные загрузки uclient-fetch (репозиторий, подписки, автоподбор)
files/usr/share/zaprett/fw4-include.sh     восстановление таблицы после `service firewall stop/start`
files/usr/share/zaprett/guard/*.txt        листы-заглушки ADR-004
files/usr/share/ucode/zaprett/*.uc         библиотека (раздел 2)
tests/                                     юнит-тесты ucode + фикстуры (раздел 10)
```

`files/usr/share/zaprett/bundle/**` и `presets.json` собирает отдельный инструмент
(`tools/data/build_bundle.py`); бэкенд читает их по схеме §3 и §9.

Зависимости (как в §2): `ucode ucode-mod-fs ucode-mod-uci ucode-mod-ubus nftables kmod-nft-queue uclient-fetch
ca-bundle zaprett-nfqws`. Модули ucode — только `fs`, `uci`, `ubus` (ADR-001). Внешние программы из базовой
системы: `start-stop-daemon`, `logger`, `sha256sum`, `df`, `cp`, `rm`, `kill`, `nft`, `uclient-fetch`,
`logread`, `uci`, `jsonfilter` (в fw4-include).

## 2. Модули `/usr/share/ucode/zaprett/`

Подключение — статический `import ... from 'zaprett.<имя>'` (компилятор ищет `/usr/share/ucode/*.uc`;
проверено `ucode -c` на 24.10.8 и 25.12.5).

| Модуль | Назначение |
|---|---|
| `util` | пути `P` (переопределяются только в тестах, в том числе `/proc/meminfo`, счётчик NFQUEUE и `logread`); `meminfo_mib()`, `download_concurrency()`; `run()` — команда **массивом** через постоянную обёртку `sh -c 'exec "$@" <in >out 2>err'`; атомарная запись (tmp + проверка размера + rename); JSON; sha256; `df`; flock через `fs.file.lock`; время старта процесса; журнал через `logger` |
| `validate` | чистые проверки: id, домены, IPv4/IPv6/CIDR (с обнулением хостовых битов), MAC, метки, порты, версии, URL, текст пользовательских листов |
| `config` | UCI -> типизированная конфигурация с умолчаниями; неверное значение → умолчание + предупреждение `bad_config` (+`bad_options`); отсутствующая list-опция в существующей секции = пустой список (UCI и LuCI удаляют пустые списки); подписки `config source` |
| `store` | элементы: bundle < `/etc/zaprett` (репозиторий и подписки `source:url`) + пользовательские; проверка манифестов (id = имя файла, `type` = каталог, файл только внутри своего `files/`), подсчёт записей с кэшем, `used_by`, `active` |
| `strategy` | генератор аргументов (раздел 3), проверка путей файлов в опциях, dry-run, запись `/var/run/zaprett/{args,engine,ports.json,status.json}` |
| `nft` | WAN из `ubus network.interface dump`, генерация скрипта (§8) — с flowtable `ft` и цепочкой `forward_offload` (режим `own`) и цепочкой `forward_quic` (`quic_block`), `nft -c -f` → `nft -f`, удаление |
| `offload` | `flow_offload=auto\|own`: сохранить в `/etc/zaprett/offload-saved.json` и выключить fw4 offload, вернуть при выключении; для `own` — план собственной flowtable (`flowtable_plan()`: устройства из `/var/run/fw4.state`, для аппаратного ускорения — нижние устройства мостов/VLAN) |
| `service` | состояние (ubus `service list`), автозапуск (симлинк `/etc/rc.d/S95zaprett`), версия движка (кэш), предупреждения, вызов `/etc/init.d/zaprett` |
| `job` | фоновые задачи (раздел 8) |
| `net` | `probe()` поверх `probe.sh`, классификация результатов `uclient-fetch` |
| `dns` | шифрованный DNS (§15.3): какой провайдер работает (`https-dns-proxy`, `stubby`, `dnscrypt-proxy`: init-скрипт есть и у procd-службы есть работающий экземпляр), задача `dns-setup` (apk/opkg); `hooks` подменяют статус, запуск команд и менеджер пакетов в юнит-тестах |
| `diagnose` | «чем блокирует провайдер» (§15.4): системный DNS против DNS-over-HTTPS, загрузка, проверка TCP до настоящего адреса, вердикт (чистый `judge()`); `hooks` подменяют резолверы и загрузчик |
| `repo` | клиент zaprett-repo: индекс, манифесты, зависимости, установка/обновление/удаление |
| `sources` | подписки по URL: загрузка, нормализация, проверки, `src-<имя>` |
| `tester` | автоподбор (§10) и `--apply-if-better`; общие для `probe` и монитора `service_active()`, `preset_services()`, `probe_targets()`; `opts.hooks` (управление движком, признак «работает», опрос целей) подменяется в юнит-тестах |
| `isolate` | автоподбор без отключения обхода (§17, раздел 9): пользователь `zaprett-test` (из `P.passwd`), причины отката `refusal()`, состояние правил теста `test-isolated.json` (порты проверяются), файлы инстанса `test`, `cleanup()` (ubus `service delete` инстанса `test`, `start-stop-daemon -K -u`, `fw apply`) |
| `cron` | строки `/etc/crontabs/root` с метками `# zaprett-autoupdate`, `# zaprett-watchdog`, `# zaprett-monitor` (§14.2): чистый `render()`, `wanted()` по конфигурации и маркеру остановки, `sync()` |
| `health` | `ensure` (сторож), `probe` (проверка сервисов без перезапуска движка), монитор: чистый `monitor_step()`, `monitor_run()` / `monitor_status()`; `hooks` подменяют procd, движок, опрос целей и запуск задач в юнит-тестах |
| `commands` | команды CLI; каждая возвращает `{ok, ...}` или `{ok:false, error, message}` |
| `text` | человекочитаемый вывод и тексты предупреждений на русском |

### Ловушки платформы (проверены на обеих VM, учтены в коде)

- `export function f() {...};` — после тела **обязательна** `;`.
- Функции и константы модуля не «поднимаются»: использовать можно только объявленные выше по тексту.
- `"\xHH"` выше `0x7f` в строке кодируется как UTF-8 (2 байта); сырые байты — `chr(0xef, 0xbb, 0xbf)`.
- `int('0x40000000')` = 0 — шестнадцатеричное только через `hex()`.
- **Деление целых целочисленное:** `3/4 == 0`. Доли считаются как `a * 1.0 / b`.
- `fs.writefile()` не проверяет `fclose()`: запись в `/dev/full` возвращает полную длину. `atomic_write`
  поэтому сверяет размер файла на диске перед `rename`. `file.close()` всегда возвращает `true`.
- **Прямой вызов скобочного выражения с `??`, `||`, `&&` или `?:` портит стек**: `(a ?? obj.fn)(x)` при непустом
  `a` возвращает неверное значение и затирает соседние локальные переменные (то же для `||` и `?:`; форма с
  `&&` запрещена вместе с ними). Только через переменную: `let fn = a ?? obj.fn; fn(x)`. `tests/test_static.uc`
  запрещает все четыре формы во всех .uc пакета.
- `file.read('line')` в конце файла возвращает `null`, `file.read(N)` — пустую строку.
- **Интервал `{n,m}` в регулярке стоит миллисекунды на каждый вызов** (замер на обеих версиях, 1000 вызовов):
  `/^[A-Za-z0-9._-]{1,96}$/` — 4776 мс, `/^[A-Za-z0-9_.@-]{1,32}$/` — 2279 мс,
  `/^[a-z0-9_]([a-z0-9_-]{0,61}[a-z0-9_])?$/` — 1701 мс, `/^[0-9A-Za-z][0-9A-Za-z.+~_-]{0,31}$/` — 1316 мс,
  `/^[0-9a-f]{64}$/` — 257 мс; те же выражения с `+`/`*` — 20–40 мс. Маленькие интервалы дешевле, но тоже
  заметны в горячем пути: `{1,4}` — 0,056 мс, `{1,3}`×4 — 0,051 мс за вызов.
  Поэтому **все границы длины проверяются через `length()`**, а классы в регулярках — без интервалов
  (`util.is_id`, `validate.*`, `config.source_name_valid`, `cli.uc`). Эффект: `S.scan()` по настоящему
  bundle 1553 → 47 мс, `SV.status()` 1564 → 43 мс, `C.load()` 39 → 1 мс, `cidr_parse` IPv6 0,343 → 0,122 мс
  на строку, IPv4 0,083 → 0,072 мс. Порог зафиксирован тестом `test_bundle` (scan < 300 мс).
- `ulimit -f N` в busybox ash считает блоками по 512 байт; процесс, превысивший предел, получает SIGXFSZ
  (код 153 = 128+25).
- `system(argv, timeout_ms)` блокирует SIGCHLD в дочернем процессе, и встроенный `wait` busybox ash там
  **никогда не возвращается** (`sleep 1 & wait` висит до таймаута). Подстановка `$(...)`, конвейеры и обычные
  команды работают. Поэтому `probe.sh` запускает пачку загрузок одним конвейером, без `&`/`wait`.
- Любое действие `/etc/init.d/<procd-служба>` берёт `procd_lock` (flock `/var/lock/procd_<имя>.lock`).
  Дочерние процессы init-скрипта наследуют fd 1000 и не блокируются, но fw4 закрывает этот fd перед
  include-скриптами. Поэтому ни fw4-include, ни `status` не вызывают init-скрипт (см. раздел 4).

## 3. Сборка аргументов движка (§5)

1. Текст стратегии: построчно убираются висячие `\`, разбиение по пробельным символам; `--comment` без `=`
   и все токены до следующего `--...` выбрасываются; одиночные `\` выбрасываются.
   Затем `canonicalize()`: каждая опция → `--<полное имя>[=значение]` по таблице `long_options` движка, как её
   видит `getopt_long_only` (одиночный `-`, однозначный префикс, значение отдельным словом у опции с обязательным
   значением). Неизвестное/неоднозначное имя, значение у опции без значения, слово-не-опция → `bad_option`.
   Таблица — `zaprett/engine_options.uc`, генерируется из `long_options` исходников движков по тегам версий пакетов:
   `PYTHONUTF8=1 python tools/data/gen_engine_options.py` (`--check` — сверка, `--compare-help` — сверка с `--help`
   бинарника); пересобирать при каждом обновлении nfqws/nfqws2. В args уходит канонический токен.
2. nfqws: в `--dpi-desync=` — `split→fakedsplit`, `split2→multisplit`, `disorder→fakeddisorder`,
   `disorder2→multidisorder`.
3. Удаляются опции, которыми управляет zaprett (`--qnum --user --uid --daemon --pidfile --debug --dry-run
   --version --dpi-desync-fwmark --fwmark --intercept --chdir --writable --fuzz`) — предупреждение `strategy_option_ignored`.
4. Профили (отрезки между `--new`, у nfqws2 и `--new=<имя>`); пустые (в том числе после завершающего `--new`) удаляются —
   `empty_profile_removed`.
5. Активные элементы UCI: отсутствующий обычный лист — **ошибка** `item_not_found` (`missing_lists`);
   нескачанная подписка `src-<имя>` пропускается — предупреждение `source_not_downloaded`.
6. Плейсхолдеры:
   - `${hostlists}`: whitelist — `--hostlist=<файл>` каждого include-листа + guard + `--hostlist-exclude=<файл>`
     каждого exclude-листа, а в профиле без `${ipsets}` ещё и `--ipset-exclude=<файл>` каждого exclude-ipset
     (контракт v1.2 §5: пустой include-ipset проверку пропускает); blacklist — только `--hostlist-exclude`.
   - `${ipsets}`: whitelist — `--ipset=` + guard + `--ipset-exclude=`; blacklist — только `--ipset-exclude`.
     Доменные исключения в профиль «только `${ipsets}`» не вставляются (v1.2 §5).
   - **Профиль с обоими плейсхолдерами** (стратегия alt10). nfqws требует совпадения и хостлиста, и ipset, а
     пустой набор пропускает всё. Если записи есть только у одного вида, второй include-вид не выводится (как
     в оригинале, где он указывал на пустой файл). Если оба пусты — ставятся оба guard. Когда профиль остаётся
     только с ipset, в него **не** добавляются и `--hostlist-exclude`: любая hostlist-опция, даже exclude,
     заставляет nfqws требовать известное имя хоста (`PROFILE_HOSTLISTS_EMPTY` учитывает exclude), и профиль
     перестал бы работать для трафика без SNI. `--ipset-exclude` добавляются всегда.
   - `${hostlist:id}`, `${hostlist_exclude:id}`, `${ipset:id}`, `${ipset_exclude:id}`, `${bin:id}`,
     `${lua_lib:id}` — путь файла установленного элемента этого типа. Для стратегий из bundle/репозитория с
     непустым `dependencies` id обязан быть объявлен. `${zaprettdir}` → `/usr/share/zaprett/bundle/files`.
     Прочее — ошибка.
7. Опции-файлы (`--hostlist*`, `--ipset*`, `--dpi-desync-fake-*`, `*-pattern`, `--blob`, `--lua-init`) должны
   указывать внутрь `/etc/zaprett`, `/usr/share/zaprett`, `/var/run/zaprett`; `--hostlist-auto*` — только внутрь
   `/var/run/zaprett/autohostlist/` (каталог создаётся, владелец — пользователь движка из UCI). Hex-значения и `!` разрешены;
   встроенный Lua-код в `--lua-init` запрещён. Иначе `path_not_allowed`: nfqws читает фейки от root, и своя
   стратегия могла бы отправить в сеть содержимое любого файла роутера.
8. Базовые опции впереди: nfqws `--qnum --user --dpi-desync-fwmark`; nfqws2 `--qnum --user --fwmark` и
   `--lua-init=@/usr/share/zaprett/lua/zapret-lib.lua`, `…/zapret-antidpi.lua`, если своих нет;
   `debug=1` → `--debug=syslog` первым.
9. Проверка: `nfqws --dry-run ...` / `nfqws2 --intercept=0 ...` (без `--debug`), код 0 обязателен.
10. Порты для nft: объединение `--filter-tcp/--filter-udp` по профилям (кроме `--skip`), диапазоны сливаются;
    профиль без фильтров → TCP 80,443 и UDP 443; `~` → значения по умолчанию для протокола. Диапазон шире
    1024 портов → `wide_port_range`.
11. В режиме whitelist профили без include-фильтров (`--hostlist`, `--hostlist-domains`, `--hostlist-auto`,
    `--ipset`, `--ipset-ip`) → `profile_unfiltered`, `details.unfiltered_profiles: [{profile, tcp, udp}]`.

Если есть `/var/run/zaprett/test-override` (автоподбор), генератор берёт стратегию и движок из него,
а nft — без фильтра клиентов.

## 4. Служба, fw4, hotplug, cron

- `start_service`: при `enabled!=1` и без override — `fw remove`, `offload restore`, выход. Иначе
  `zaprett gen-args` (ошибка → syslog, таблица снимается, инстанс не создаётся и procd останавливает старый);
  `procd_open_instance engine` с аргументами из `/var/run/zaprett/args` (массивом через `set --`),
  `respawn 3600 5 5`, `file args`, stdout/stderr в syslog; `zaprett fw apply`; `zaprett offload apply` (не в тесте).
- `stop_service`: `fw remove`; `offload restore`, если служба выключена (`enabled=0`). Команда `zaprett stop`
  возвращает offload явно. Кроме перезапуска (`action=restart` в rc.common) пишет маркер
  `/var/run/zaprett/stopped` («остановлено пользователем») и вызывает `cron sync`: строки сторожа и монитора
  исчезают, `ensure` службу не поднимает. `start_service` снимает маркер и тоже вызывает `cron sync`.
  Пока идёт автоподбор (`test-override` или `test-state.json`), `cron sync` из init не вызывается — тест много раз
  останавливает и запускает движок, расписание при этом не трогается.
- `reload_service`: `start` (procd перезапускает движок только при изменении md5 файла `args`) + `zaprett cron sync`.
  `service_triggers`: `procd_add_reload_trigger zaprett`.
- fw4: `firewall.zaprett` (`type script`, `path /usr/share/zaprett/fw4-include.sh`, `fw4_compatible 1`).
  Скрипт повторяет `nft -f /var/run/zaprett/zaprett.nft`, если файл есть и procd сообщает
  `instances.engine.running = true` (`ubus call service list | jsonfilter`).
- hotplug `60-zaprett`: на `ifup/ifdown/ifupdate` (кроме loopback) при существующем `zaprett.nft` —
  `zaprett fw apply --if-applied` (применённая таблица получает новый набор WAN, в том числе пока идёт старт).
- cron (§14.2): `zaprett cron sync` держит по одной строке на метку:
  `*/5 * * * * /usr/bin/zaprett ensure --quiet # zaprett-watchdog` — при `main.enabled=1`, `main.watchdog=1` и без
  маркера остановки; `*/<interval> * * * * /usr/bin/zaprett monitor run --quiet # zaprett-monitor` (для 60 —
  `<M> * * * *`, минута случайная и сохраняется) — при `main.enabled=1`, `monitor.enabled=1` и без маркера;
  нет секции `monitor` — строки нет. Чужие строки не трогаются, дубли своих сводятся к одной. Запись файла и
  `cron restart` — только когда текст изменился. Третья строка —
  `<M> <autoupdate_hour> * * * /usr/bin/zaprett repo upgrade --all --foreground --quiet # zaprett-autoupdate`.
  Минута случайная и сохраняется. Строка нужна, если `repo.autoupdate=1` **или** включена хотя бы одна
  корректная подписка; иначе удаляется. Эта форма команды выполняет задачу `autoupdate`: подписки с истёкшим
  `interval_hours` обновляются всегда (допуск −1 ч, чтобы суточный запуск не сдвигал обновление на сутки),
  установленное из репозитория — только при `repo.autoupdate=1`. `cron sync` вызывается из `uci-defaults`,
  `start_service`/`stop_service`/`reload_service`, `enable`/`disable`, `sources save/delete` и `wizard apply`.
- `fw apply` не трогает ядро, если отрисованный текст совпадает с `/var/run/zaprett/zaprett.nft` и таблица
  стоит (`changed:false`) — hotplug и init вызывают его на каждое событие интерфейса.
- Повторный dry-run при `zaprett start` не выполняется: после успешной проверки в `/var/run/zaprett/dryrun-ok.json`
  хранится ключ — размер и mtime бинарника, аргументы построчно и размер/mtime каждого файла из аргументов
  (отсутствующий файл тоже часть ключа). Совпал ключ — dry-run пропускается (`dry_run.cached:true`); любое
  изменение аргументов, листа или бинарника проверяется заново; ошибка dry-run удаляет ключ; `zaprett check`
  проверяет всегда (`force_dry_run`).
- fw: `fw apply` (вместе с генерацией скрипта) и `fw remove` выполняются под общей блокировкой
  `/var/lock/zaprett-fw.lock` (ожидание до 30 с, затем ошибка `busy`): их одновременно вызывают init, hotplug и CLI.
- Пакет: `prerm` (не при обновлении) — отмена задачи, остановка, возврат offload, удаление таблицы, include,
  все три строки cron (`# zaprett-autoupdate`, `# zaprett-watchdog`, `# zaprett-monitor`) и `/var/run/zaprett`;
  `postinst` — сброс кэша. `uci-defaults` запускает `default_postinst`.
  Пустые каталоги пакетные менеджеры не убирают, это делают `postrm`: пакет `zaprett` проходит
  `/usr/share/zaprett` и `/usr/share/ucode/zaprett` от глубоких каталогов к корню (`find -type d | sort -r`,
  затем `rmdir`), поэтому каталог с файлами другого пакета (`/usr/share/zaprett/lua` пакета `zaprett-nfqws2`)
  и его родители остаются; `find -empty` в busybox обеих версий отсутствует и молча не находит ничего.
  Каталог `/usr/libexec/zaprett` создают пакеты движков, и они же снимают его своим `postrm` (`rmdir`, только
  если он пуст, — при установленном втором движке он остаётся); `zaprett-nfqws2` так же снимает свой
  `/usr/share/zaprett/lua`. `/etc/zaprett` — данные пользователя, его удаляет `install.sh --purge`.
  `uci-defaults` создаёт секцию `monitor` с умолчаниями, если её нет (существующая не меняется), и вызывает
  `zaprett sources defaults`: при обновлении сохранённый `/etc/config/zaprett` не
  содержит подписок, добавленных в новой версии, и они дописываются, кроме перечисленных в
  `main.deleted_sources` (их удалил пользователь).
- Изменения через CLI (`list`, `strategy set`, `mode`, `engine`, `wizard apply`) применяются к **работающему**
  движку (`init reload`). Остановленный движок не запускается. После установки/удаления из репозитория и
  обновления подписок перезагрузка идёт, только если изменились аргументы движка или заменены стратегии,
  bin или lua, которые движок читает при старте. Листы nfqws перечитывает сам.

## 5. nftables (§8)

Скрипт `/var/run/zaprett/zaprett.nft` проверяется `nft -c -f` и применяется одним `nft -f`
(`table` / `delete table` / `table {...}` — атомарная замена). Пример генератора на тестовой VM (WAN `eth1` из
netifd, стратегия `strategy-general`):

```nft
table inet zaprett {
	set wanif { type ifname; elements = { "eth1" } }
	set nozaprett { type ipv4_addr; flags interval; auto-merge; elements = { 0.0.0.0/8, 10.0.0.0/8, ... } }
	chain postnat_hook { type filter hook postrouting priority 101; policy accept;
		meta mark and 0x40000000 == 0 jump postnat }
	chain postnat {
		oifname @wanif tcp dport { 80, 443, 2053, 2083, 2087, 2096, 8443 } ct original packets 1-9 ip daddr != @nozaprett meta mark set meta mark or 0x20000000 ct mark set ct mark or 0x40000000 queue num 200 bypass
		oifname @wanif udp dport { 443, 19294-19344, 50000-50100 } ct original packets 1-9 ip daddr != @nozaprett meta mark set meta mark or 0x20000000 ct mark set ct mark or 0x40000000 queue num 200 bypass }
	chain prenat { type filter hook prerouting priority -101; policy accept;
		iifname @wanif tcp sport { 80, 443, 2053, 2083, 2087, 2096, 8443 } ct reply packets 1-3 ip saddr != @nozaprett ct mark set ct mark or 0x40000000 queue num 200 bypass }
	chain prerouting_icmp { ... }  chain predefrag { ... }  chain predefrag_nfqws { ... }
}
```

Отличия от эскиза §8: в `prenat` есть `ct mark set ct mark or <desync_mark>` (как в zapret `nft.sh`; от этого
зависит сброс ICMP time-exceeded); фильтр клиентов применяется и к `prenat`; при лимите 1 пишется
`packets 1`, а не `1-1`.

WAN — интерфейсы `up` с маршрутом `0.0.0.0/0` (`::/0` для IPv6) в основной таблице (у маршрута нет `table`),
устройство `l3_device`; либо логические интерфейсы из `list wan`. Имена устройств проверяются regex. Метки
проверяются как битовые маски: у `desync_mark`, `postnat_mark` и метки клиентов `0x08000000` не должно быть
общих битов.

## 6. CLI `/usr/bin/zaprett`

Флаги: `--json` (один JSON-объект в stdout), `--foreground` (задача выполняется сразу), `--quiet` (без вывода
при успехе), `--type <тип>`, `--tail N`, `--strategies id,id`, `--services id,id`, `--quick`,
`--apply-if-better`, `--brief`, `--all`, `--if-running`, `--if-applied`, `--full` (только `diag`).
Коды возврата: `0` — успех, `1` — ошибка, `2` — неверные аргументы (`error: "usage"`).
Ошибка: `{"ok":false,"error":"<код>","message":"<текст ru>", ...}`. Id: `^[A-Za-z0-9._-]{1,96}$`, не с точки.

| Команда | Ответ `--json` (успех) |
|---|---|
| `status` | `{ok, enabled, autostart, running, pid, engine, engine_version, strategy:{id,name,source}, list_mode, lists, exclude_lists, ipsets, exclude_ipsets, nft_applied, wan:[dev], flow_offload:{fw4, fw4_hw, mode, own}, dns:{encrypted, provider}, clients_mode, test_mode, queue: null\|{packets}, ipv6_wan, job: null\|{id,name,state,progress}, monitor: null\|{state,consecutive_failures,checked_at}, warnings:[код], details:{generate, bad_options, recovered_test?}, version}` — `queue.packets` — 8-й столбец `/proc/net/netfilter/nfnetlink_queue` для `qnum`; `job` — последняя задача в любом состоянии; `monitor` — `null`, если секции нет или монитор выключен |
| `start` | `{ok, running, pid, warnings, nft_applied}` — `enabled=1`, `init enable`, проверка генерации, ожидание запуска до 6 с |
| `stop` | `{ok, running:false}` — остановка + возврат offload |
| `restart` | `{ok, running, pid, warnings}`; при `enabled=0` — ошибка `disabled` |
| `enable` / `disable` | `{ok, enabled, autostart}`; `disable` также останавливает движок |
| `check` | `{ok, engine, strategy, args, ports:{tcp,udp}, dry_run:{rc,output}, warnings, details, dropped_tokens}` |
| `gen-args` | `{ok, path, engine, strategy, warnings, test_mode}`; текстом — путь |
| `fw apply [--if-running\|--if-applied]` | `{ok, applied, changed, flowtable: "hw"\|"sw"\|null, wan:{v4,v6}, ports, warnings}` или `{ok, skipped:true, changed:false}`; `changed:false` — тот же текст уже стоит, ядро не трогалось; в режиме `own` без принятой ядром flowtable — `flowtable_failed` в `warnings` |
| `fw remove` / `fw show` | `{ok, removed}` / `{ok, text, applied, wan, ports}` |
| `items [--type t]` | `{ok, items:[{id,type,name,version,author,description,name_en,description_en,source,file,entries,size,active,used_by,dependencies,supported}], errors:[{path,error}]}` |
| `list enable/disable <id>` | `{ok, id, type, enabled, changed, reloaded, warnings}`; `src-<имя>` можно включить до первой загрузки |
| `strategy set <id>` | `{ok, id, engine, reloaded, warnings}` |
| `strategy show <id>` | `{ok, id, type, name, source, description, dependencies, text, args, ports, warnings}` или `build_error/build_message` |
| `strategy save <id>` (stdin ≤64 КиБ, id `user-*`) | `{ok, id, engine, args, ports, warnings, reloaded}` — сохраняется только прошедшая dry-run |
| `strategy delete <id>` | `{ok, id, engines}` |
| `user get <id>` / `user set <id>` (stdin ≤1 МиБ) | `{ok, id, type, text, entries, size}` / `{ok, id, entries, size, reloaded}`; ошибка `invalid_entries` + `errors:[{line,value,reason}]` |
| `mode whitelist\|blacklist`, `engine nfqws\|nfqws2` | `{ok, list_mode\|engine, reloaded, warnings}` |
| `sources list` | `{ok, sources:[{name, enabled, title, type, url, interval_hours, min_entries, last_update, entries, size, status: ok\|error\|never\|invalid, error, message, ram_mib, item_id, downloaded}]}` |
| `sources update [<имя>...]` | `{ok, job:{id,name}}`; результат задачи `{updated, unchanged, failed:[{name,error,message}]}` |
| `sources save <имя>` (JSON в stdin ≤64 КиБ) / `sources delete <имя>` | `{ok, name, item_id, type, created, type_changed, url_changed, reloaded}` / `{ok, name, removed_item, reloaded}`; во время фоновой задачи — ошибка `job_busy` |
| `sources defaults` | `{ok, added:[имя], skipped:[имя]}` — служебная, для `uci-defaults` |
| `presets` | `{ok, schema, services:[{id,name,description,note,name_en,description_en,note_en,tier,works,lists,ipsets,sources,test_targets,enabled,partially_enabled,available,variants:[{id,name,name_en,description,description_en,lists,ipsets,sources,tier,enabled,available}],enabled_variant}], defaults, always, tiers, ram_total_mib, recommended_tier: light\|full}`. `enabled`/`partially_enabled` — по основному набору сервиса; `variants[].enabled` — включены все элементы варианта; `enabled_variant` — id включённого варианта (из нескольких — самый большой, при равенстве первый), `null` — работает основной набор или сервис выключен (контракт v1.7 §16.4). Вариант без своего `tier` наследует `tier` сервиса |
| `wizard apply <service>[:<variant>]...` | Для сервиса с `variants` можно указать вариант (`discord:voice`); без варианта — основной набор. Включаются элементы выбранного набора; все остальные наборы пресетов (другие варианты выбранного сервиса, любые наборы невыбранных) выключаются, кроме элементов, которые нужны выбранному набору; `user-*` не выключаются никогда. Ошибки: `unknown_variant` (у сервиса нет такого варианта), `bad_args` (часть ссылки не id или сервис указан с разными вариантами), `unknown_service`. Ответ `{ok, services, variants:{<service>: <variant>\|null}, skipped:[{id,reason}], lists, ipsets, exclude_lists, exclude_ipsets, list_mode, strategy, sources, sources_disabled, job?, job_error?:{code,message}, reloaded, warnings}`; сервис или выбранный вариант уровня `full` при ОЗУ меньше `tiers.full.min_ram_mib` применяется с `low_memory` в `warnings` (так же в `status`, если такой вариант включён целиком) |
| `repo fetch` / `repo install <id>...` / `repo remove <id>` / `repo upgrade [--all\|<id>...]` | `{ok, job:{id,name}}` (с `--foreground` — результат задачи) |
| `repo list [--type t]` | `{ok, fetched_at, url, items:[{id,type,name,version,author,description,installed,installed_version,installed_source,update_available,supported,size,error}]}` |
| `test start [--strategies a,b] [--quick] [--apply-if-better]` / `test status [--brief]` / `test stop` / `test apply <id>` | `{ok, job}` (результат задачи `{tested, best, baseline_ok, applied, message}`) / `{ok, running, job, results}` (в `results.results` подробности целей только у первых 10 результатов, не больше 100 целей; `results.targets_trimmed`; с `--brief` поля `targets` нет ни у результатов, ни у `baseline`) / как `job cancel`, либо `{ok, state:"restored", reloaded}` без задачи / `{ok, strategy, engine, reloaded, warnings}`. С `--apply-if-better` исходная стратегия всегда в переборе (первой); лучшая применяется, только если её доля строго больше доли исходной (исходная, которая не запустилась, считается 0); UCI меняется до возврата, поэтому движок перезапускается один раз. `applied: <id>\|null` — и в результате задачи, и в `test-results.json` |
| `ensure [--quiet]` | `{ok, action, reason?, pid?}`: `none` (`reason: disabled` или `stopped`, либо без `reason` — всё работает), `started` (`reason: not_running`), `fw_applied`, `skipped` (`reason: test_running`). Неудачный запуск — `{ok:false, error:"engine_not_running", action:"started"}`. Действия ≠ `none`/`skipped` пишутся в syslog («сторож: …»). Под блокировкой `zaprett.lock` |
| `probe [--services id,id] [--foreground]` / `probe status` | задача `probe`: цели `test_targets` сервисов, у которых включён лист, ipset или элемент подписки `src-<имя>` (с `--services` — названные, даже выключенные; неизвестный — `unknown_service`), одним параллельным опросом, **без** остановки и перезапуска движка; результат задачи `{reachable, total, services, message}`. `probe status` → `{ok, probe: null\|{started, finished, engine_running, strategy: id\|null, ok, total, services:[{id,name,ok,total,avg_ms,targets:[{url,ok,ms,bytes,error}]}]}}` из `/var/run/zaprett/probe.json`; `error` — `null` при `ok`, иначе код классификатора (раздел 9) |
| `monitor run [--quiet]` / `monitor status` | одна проверка: не больше `max_targets` целей тех же сервисов, таймаут `monitor.timeout`, параллельно; пропуск без записи — `{ok, skipped}` (`monitor_disabled`, `service_disabled`, `stopped`, `not_running`, `job_busy`; причина перепроверяется и после опроса); иначе `{ok, state, reachable, total, consecutive_failures, repair_started}`. `monitor status` → `{ok, monitor:{enabled, auto_repair, interval, threshold, state, checked_at, ok, total, consecutive_failures, history:[{t,ok,total}] (48), last_repair: null\|{t, job_id}}}` из `/var/run/zaprett/monitor.json` |
| `log [--tail N]` | `{ok, lines}` — строки `logread` с `zaprett` или `nfqws`, N по умолчанию 200, больше 1000 урезается до 1000, не число — `bad_value` |
| `dns status` / `dns setup [--foreground]` | `{ok, dns:{encrypted, provider}}` / при уже работающем провайдере сразу `{ok, changed:false, dns}`, иначе задача `dns-setup` (результат `{changed:true, packages, manager, dns, message}`; ошибки `no_package_manager`, `package_update_failed`, `package_install_failed`, `dns_not_running`, `cancelled`) |
| `diagnose [--services id,id] [--foreground]` / `diagnose status` | задача `diagnose` (результат `{verdict, counts, total, message}`) / `{ok, diagnose: null\|{started, finished, engine_running, targets:[{url, host, verdict, dns:{system, doh, spoofed}, detail, service, reason, error, bytes, tcp}], summary:{verdict, counts}}}` из `/var/run/zaprett/diagnose.json`; `unknown_service`, `no_targets` |
| `page overview\|lists\|strategies\|diagnostics` | `{ok, page, <поля>}` — один процесс; каждое поле — ответ команды целиком со своим `ok`: overview `status, job, presets, monitor, probe, dns`; lists `status, job, items, sources, presets`; strategies `status, job, items, test` (`test status --brief`); diagnostics `status, job, monitor, dns, diagnose` (`dns` = ответ `dns status`, `diagnose` = ответ `diagnose status`). Исключение в одной части не роняет страницу: поле получает `{ok:false, error:"internal_error"}` |
| `job status` / `job log [--tail N]` / `job cancel` | `{ok, job}` / `{ok, log}` / `{ok, state:"cancelling", job:{id,name}}` — сразу, не дожидаясь задачи |
| `diag [--full]` / `version` | `{ok, text, full}` / `{ok, version, nfqws, nfqws2}` |
| служебные | `offload apply\|restore\|status`, `cron sync`, `__job-run <id> <name> ...` |

Изменения настроек через CLI (`list`, `strategy set`, `mode`, `engine`, `wizard apply`) сначала прогоняют
генерацию и dry-run с новой конфигурацией. Изменение, которое ломает рабочую конфигурацию, не сохраняется.
Поле «применено перезапуском» во всех ответах — `reloaded` (контракт v1.2).

### Примеры (реальный вывод на тестовой VM, сокращено)

```json
{"ok":true,"engine":"nfqws","strategy":{"id":"strategy-general","name":"strategy-general","source":"bundle"},
 "args":["--qnum=200","--user=daemon","--dpi-desync-fwmark=0x40000000","--filter-udp=443",
         "--hostlist=/usr/share/zaprett/bundle/files/lists/include/zaprett-youtube.txt","...",
         "--hostlist=/usr/share/zaprett/guard/hostlist-guard.txt","--hostlist-exclude=/usr/share/zaprett/bundle/files/lists/exclude/zaprett-exclude.txt","..."],
 "ports":{"tcp":["80","443","2053","2083","2087","2096","8443"],"udp":["443","19294-19344","50000-50100"]},
 "dry_run":{"rc":0,"output":"... command line parameters verified"},
 "warnings":["empty_profile_removed","profile_unfiltered"],
 "details":{"empty_profiles":1,"unfiltered_profiles":[{"profile":2,"tcp":[],"udp":["19294-19344","50000-50100"]}]}}
```

```json
{"ok":false,"error":"invalid_entries","message":"Неверных строк: 1. Исправьте их и сохраните снова.",
 "errors":[{"line":2,"value":"*.bad.com","reason":"bad_domain"}]}
```

```json
{"ok":false,"error":"dry_run_failed","message":"Движок отклонил аргументы стратегии: invalid dpi-desync mode",
 "strategy":{"id":"user-x","name":"user-x","source":"user"},"engine":"nfqws","args":["..."],"dry_run":{"rc":1,"output":"invalid dpi-desync mode"}}
```

### Коды предупреждений (`warnings`) — закрытый список контракта v1.2 + v1.3 + v1.4

`bad_config`, `generate_failed`, `engine_missing`, `nft_queue_missing`, `no_strategy`, `strategy_missing`,
`list_missing` (в `status`: включён обычный лист, которого нет среди установленных), `not_running`,
`nft_not_applied`, `no_wan`, `test_running`, `flow_offload_enabled`, `no_active_lists`, `source_not_downloaded`,
`profile_unfiltered`, `strategy_option_ignored`, `empty_profile_removed`, `wide_port_range`, `config_was_invalid`,
`ipv6_wan_unhandled` (у WAN маршрут `::/0`, а `ipv6=0`; при включённой или работающей службе), `low_memory`
(включён сервис уровня `full` при ОЗУ меньше `tiers.full.min_ram_mib` или `ram_mib` включённых подписок больше
половины `MemAvailable`), `monitor_degraded` (монитор включён и в состоянии `degraded` или `repairing`);
v1.4: `flowtable_failed` (режим `own`, исходно fw4-ускорение было включено, таблица в ядре есть, а своей flowtable в
ней нет — нет устройств или ядро не приняло), `game_filter_no_ipsets` (игровой фильтр включён, а непустых включённых
include-ipset нет — профиль не добавлен), `dns_plain` (информационное: включён сервис с `needs_dns`, шифрованного DNS нет).
Других кодов бэкенд не выдаёт; русские тексты — `text.uc`, `WARNINGS` (тест сверяет ключи со списком
контракта). Ошибка попутной фоновой задачи идёт в `job_error {code, message}`, не в `warnings`.

### Основные коды ошибок

`usage`, `bad_id`, `bad_type`, `bad_value`, `bad_args`, `not_found`, `ambiguous_id`, `uci_failed`, `write_failed`,
`busy`, `disabled`, `test_running`, `engine_missing`, `engine_not_running`, `stop_failed`, `no_strategy`,
`strategy_not_found`, `strategy_unreadable`, `strategy_empty`, `item_not_found`, `placeholder_in_token`,
`unknown_placeholder`, `bad_placeholder_id`, `dependency_not_declared`, `item_not_installed`, `path_not_allowed`,
`bad_option`, `bad_port_filter`, `dry_run_failed`, `nft_check_failed`, `nft_apply_failed`, `nft_remove_failed`,
`too_large`, `invalid_entries`, `item_active`, `item_in_use`, `readonly_item`, `not_installed`, `presets_missing`,
`unknown_service`, `unknown_variant`, `preset_unavailable`, `preset_item_missing`, `job_busy`, `job_spawn_failed`, `no_job`,
`internal_error`, `download_failed`, `bad_index`, `not_in_repo`, `unsupported_item`, `dep_not_found`,
`sha256_mismatch`, `no_space`, `repo_not_fetched`, `cancelled`, `no_targets`, `no_strategies`,
`engine_not_running` (и у `ensure`), `logread_failed`, `source_update_failed` (с `failed[].error`: `invalid_source`, `download_failed`, `too_large`, `too_few_entries`,
`bad_content`, `no_space`, `write_failed`).

## 7. Подписки по URL (§4, v1.1)

- UCI `config source '<имя>'` (`^[a-z0-9_]{1,32}$`): `enabled`, `name` (название), `type`
  (`list|list_exclude|ipset|ipset_exclude`), `url` (только `https://`), `interval_hours` (1…8760),
  `min_entries`, `min_valid_ratio` (0…1), `ram_mib`. По умолчанию выключены `refilter_domains` (72 ч, ≥1000,
  9 МиБ), `antifilter_allyouneed` (72 ч, ≥1000, 2 МиБ), `cloudflare_v4` (168 ч, ≥5, 1 МиБ), `cloudflare_v6`
  (168 ч, ≥3, 1 МиБ); те же значения — `config.uc`, `DEFAULT_SOURCES` (тест сверяет их с файлом конфигурации).
- `sources update`: загрузка через `probe.sh` (2 потока, таймаут 60 с). Предел — **16 МиБ**: `probe.sh` ставит
  `ulimit -f`, загрузка обрывается на пределе (SIGXFSZ) и не занимает tmpfs сверх него; ошибка `too_large`
  «файл больше 16 МиБ — загрузка прервана, оставлен прежний файл».
- Нормализация потоковая (`normalize_file`): файл читается блоками по 64 КиБ, результат пишется блоками во
  временный файл в tmpfs, скачанное тело сразу удаляется. BOM и CR убираются, пробелы по краям обрезаются,
  пустые строки и комментарии (`# ; / !`) пропускаются. `*.`-маски, строки с пробелом внутри, домены не в
  punycode, строки длиннее 4 КиБ и неверные записи выбрасываются и считаются неверными. Домены приводятся к
  нижнему регистру; для ipset CIDR проверяется и нормализуется. Загрузка принимается, если верных записей не
  меньше `min_entries` и не 0, а их доля не ниже `min_valid_ratio`. Иначе остаётся прежний файл, а в
  состоянии пишется ошибка.
- Результат — элемент `src-<имя>`: `/etc/zaprett/files/<dir>/src-<имя>.txt` и манифест `source:"url"`,
  `version` `YYYY.MM.DD`, `sha256`, `entries`, `url`. На флеш пишется, только если изменились sha256 или URL
  (копия рядом с целью, сверка размера, `rename`). Состояние (`last_update`, `last_check`, `sha256`, `status`,
  `error`, `message`, `entries`) хранится в `/etc/zaprett/sources-state.json`.
- Включение в работу — id `src-<имя>` в `lists`/`ipsets`/`exclude_*`. Нескачанная подписка при генерации
  пропускается с предупреждением `source_not_downloaded`.
- `sources save` и `sources delete` берут блокировку фоновых задач: при выполняющейся задаче — `job_busy`,
  ничего не меняется. Перезагрузка движка и `cron sync` — после снятия блокировки.
- `sources save` при смене типа: сначала пишется UCI (секция и перенос id из опции прежнего типа в опцию нового —
  включённая подписка остаётся включённой), затем удаляется скачанный элемент прежнего типа; `reloaded` — если
  изменились аргументы движка. Смена URL сбрасывает состояние (`last_update`, `sha256`): ближайшее обновление, в
  том числе по cron, скачает подписку сразу; до этого используется прежний файл.
- `sources delete` удаляет секцию, элемент, состояние и id из опций UCI. Удалённая подписка по умолчанию
  дописывается в `main.deleted_sources`, и `uci-defaults` её не возвращает; `sources save` с тем же именем
  убирает её из этого списка.
- Мастер: сервисы с `works:"no"` пропускаются (`skipped`); `sources` выбранных сервисов включаются
  (`enabled=1`), их id попадают в опции по типу, и сразу ставится задача `sources update`. Подписки
  невыбранных сервисов убираются из опций и выключаются (`enabled=0`, поле `sources_disabled`), если их не
  использует другой выбранный сервис. Если задачу поставить нельзя — `job_error {code, message}`.

## 8. Фоновые задачи

`job.json`: `{id, name, state: running|done|failed|cancelled, progress, message, started, finished, rc, result,
pid, pid_start}`; журнал `job.log` (не больше 256 КиБ, при переполнении остаётся последняя половина).
Задачи: `repo-fetch`, `repo-install`, `repo-remove`, `repo-upgrade`, `sources-update`, `test`, `autoupdate`, `probe`,
`dns-setup`, `diagnose`.

- Запуск: `flock` на `/var/lock/zaprett-job.lock` (занято → `job_busy`), запись `job.json`, снятие блокировки,
  затем `start-stop-daemon -S -b -m -p /var/run/zaprett/job.pid -x /usr/bin/ucode -- -S --
  /usr/share/zaprett/cli.uc __job-run <id> <name> ...`. Исполнитель сам берёт блокировку и пишет свой pid и
  время старта процесса; CLI отвечает после этого (ожидание до 5 с).
- Живость: pid существует, и его время старта (поле 22 `/proc/<pid>/stat`) совпадает с записанным. Иначе
  задача помечается `failed`.
- Отмена (`job cancel`, `test stop`) отвечает сразу `{ok, state:"cancelling"}`: rpcd вызывает CLI синхронно.
  Пишется файл `job.cancel` с id, в `job.json` — `cancel_requested`, исполнителю уходит SIGTERM; обработчик
  ставит флаг, задача проверяет его между шагами и завершается как `cancelled`. Довершает отмену любой опрос
  (`status`, `job status`, `test status`): если исполнитель не остановился за 20 с — SIGSTOP ему, SIGKILL
  дереву процессов, очистка (для `test` — возврат исходной стратегии), `state:"cancelled"`, `cancel_done`;
  если исполнитель умер сам — только очистка.
- Если автоподбор умер вне отмены (OOM, kill -9), оставшийся `test-override` снимается при ближайшем
  `status` или `test status` (`details.recovered_test`).
- `--foreground` выполняет задачу в текущем процессе с тем же `job.json` и блокировкой (cron, SSH).

## 9. Репозиторий и автоподбор

**Пределы загрузок (§14.5).** Каждая загрузка идёт через `probe.sh` с `max_bytes` (обрыв `ulimit -f`): индекс
2 МиБ, манифест 64 КиБ, артефакт 32 МиБ, подписка 16 МиБ. Размер сверяется **до** кода возврата (обрезанный файл
заканчивается SIGXFSZ, а не ошибкой загрузки) → `too_large`, временный каталог удаляется. Артефакты — 4 потока,
при `MemAvailable < 64 МиБ` — 1; подписки — 2, при `MemAvailable < 48 МиБ` — 1; манифесты — 8.
`repo.url` — только `https://` (иначе `bad_config` с `bad_options: url` и адрес по умолчанию).

**Репозиторий.** `repo fetch`: `index.json` (≤2 МиБ, `schema 1`), затем все манифесты (8 потоков) в кэш
`/var/run/zaprett/repo/index.json`. Ключ элемента — пара (type, id). Id берётся из индекса: у
`ipset-exclude-x5group` и `ipset-exclude-yandex` в zaprett-repo id манифеста с точкой на конце — элемент
принимается, расхождение отмечается в `id_note`.
`repo install`: зависимости разрешаются по URL манифестов (сначала зависимости, цикл не зацикливает).
Загрузка идёт в tmpfs, затем проверяются код, размер (≤32 МиБ) и **sha256**, место на флеше (`df`, запас
256 КиБ). Файл копируется во временный рядом с целью, sha256 проверяется повторно, затем `rename`. Локальный
манифест получает `type`, `dependencies` в виде id, `source:"repo"`, `sha256`, `installed_at`, `manifest_url`.
Файл: `/etc/zaprett/files/<dir>/<id>.<ext>`.
Обновление: для установленного из репозитория — при более новой версии или той же версии с другой sha256;
для bundle — только при строго более новой версии, чтобы не заменять отобранные листы bundle. `--all` и
автообновление касаются только установленного из репозитория.
`repo remove`: удаляется только установленное в `/etc/zaprett`. Если есть такой же элемент в bundle, удаление
разрешено (останется версия bundle). Иначе удаление запрещено, пока элемент нужен стратегиям
(`item_in_use`) или включён (`item_active`). Подписки удаляются только через `sources delete`.

**Автоподбор** (`test start`).
- **Цели:** `test_targets` сервисов из `presets.json`, чьи листы или ipset включены, плюс до `max_domains`
  доменов из включённых include-листов (`https://<домен>/`, `min_bytes 0`). Маски, IP, одиночные метки и домены
  с `_` пропускаются.
- **Кандидаты:** `--strategies`; `--quick` — стратегия по умолчанию, `defaults.quick_test_strategies`, затем
  первые стратегии bundle, всего до 12; без флагов — все установленные для текущего движка, текущая первой.
- **Режим `isolated` (по умолчанию, контракт v1.6 §17; модуль `isolate.uc`)** — основной движок не
  останавливается и не перезапускается, сеть остаётся с обходом:
  1. **Пользователь проверок** `zaprett-test`, uid/gid **29411** — создаёт `uci-defaults` через `user_add`/`group_add`
     из `/lib/functions.sh` (строки в `/etc/passwd`, `/etc/group`, `/etc/shadow`), один раз; если uid или gid 29411
     уже заняты чужими — пользователь не создаётся (→ `no_test_user`). `postrm` при удалении пакета удаляет только
     строки `^zaprett-test:` (под `lock /var/lock/passwd`). Загрузки идут через
     `start-stop-daemon -S -c zaprett-test -x /usr/share/zaprett/probe.sh -- …` (в busybox обеих версий нет `su`;
     проверено: `meta skuid 29411 counter` растёт только от этих загрузок, от root — нет). Каталог загрузок
     отдаётся пользователю `chown`; не удалось — ошибка `tmp_failed`, а не загрузка от root.
  2. **Правила** — из `/var/run/zaprett/test-isolated.json` `{uid, qnum, ports}` рисует обычный `fw apply`
     (поэтому их восстанавливают и fw4-include после `firewall restart`, и hotplug — проверено живым
     `firewall restart` посреди теста). В `postnat` первым правилом
     `meta skuid <uid> ct mark set ct mark or 0x04000000 goto postnat_test`, в `prenat` первым —
     `ct mark and 0x04000000 != 0 goto prenat_test` (ответы; `skuid` в prerouting не работает). Цепочки
     `postnat_test`/`prenat_test` — те же правила с портами и лимитами пакетов кандидата, очередь `qnum+1`, **без**
     фильтра клиентов; на базовом прогоне (`ports: null`) они пусты — проверки не идут ни в одну очередь.
     `goto`, а не `jump`: трафик проверок не возвращается в основные правила очереди `qnum`.
  3. **Инстанс `test`** — `/etc/init.d/zaprett` в `start_service` объявляет его из `/var/run/zaprett/test-args` и
     `test-engine` (без respawn; `--qnum=<qnum+1>`, `--user` как у основного). Каждая стратегия — запись этих файлов
     + `fw apply` + `init start`: procd перезапускает только инстанс с изменившимися аргументами, а reload во время
     теста объявляет `test` заново (procd снимает не объявленные при `start` инстансы). Генерация кандидата не
     трогает кэш dry-run основного движка (`keep_dry_run_cache`).
  4. **Подготовка**: пишутся состояние и пустые правила теста, `fw apply` (отказ → `nft_rejected`), инстанс `test`
     запускается с текущей стратегией; через 1 с не работает → `instance_failed`. Затем базовая проверка,
     кандидаты, в конце `ISO.cleanup`: файлы, `ubus call service delete {name, instance:"test"}` (основной инстанс
     не трогается), `start-stop-daemon -K -s KILL -u zaprett-test`, `fw apply` без цепочек теста. Основной движок
     перезапускается только для стратегии, применённой `--apply-if-better`, или запускается, если умер за время
     теста (но не после `stop` пользователя — маркер `stopped`).
  5. **Откат на `exclusive`** (прежний режим ниже) с `mode_reason`: `forced` (`--exclusive`), `engine_not_running`
     (служба выключена, остановлена или движок не работает), `no_test_user`, `qnum_out_of_range` (`qnum` 65535),
     `mark_conflict` (`desync_mark`/`postnat_mark` задевают бит `0x04000000`), `write_failed`, `nft_rejected`,
     `instance_failed`.
  6. **Падение задачи** (`kill -9`, OOM): любой следующий `status`/`job status`/`test status`/`test stop`/`ensure`/
     `monitor run` видит оставшиеся файлы теста при неработающей задаче и снимает всё то же, что `cleanup`;
     `test-results.json` из `running` становится `failed`.
- **Режим `exclusive`** (прежний, §10):
  1. Записывается исходное состояние, движок останавливается (`init stop`), идёт базовая проверка без обхода.
  2. Для каждой стратегии: генерация и dry-run (ошибка → `invalid`), `test-override`, `init start`, пауза
     `settle`, проверка, что движок запущен, проверка целей, запись `test-results.json`.
  3. При любом исходе override удаляется и восстанавливается исходное состояние (был запущен → `start`,
     иначе `stop`).
- **Поля:** `mode` (`isolated`|`exclusive`) и `mode_reason` (`null` в isolated) — в `test-results.json`, в
  `test status` (и `--brief`) на верхнем уровне и в `results`, в `result` задачи. Статус службы во время любого
  теста несёт `test_running`.
- **Сортировка:** `done` выше остальных, затем доля успешных ↓ (дробная), затем среднее время ↑.

Проверка цели: `uclient-fetch -T <timeout> [-4 при ipv6=0] -U <UA браузера> -O <файл> <url>` **без `-q`** (с
`-q` uclient-fetch не печатает ни «HTTP error», ни «Connection ...»). Классификация по коду и stderr: тексты из
`uclient-fetch.c` обеих версий, отмеченные ✓ подтверждены живыми запусками на VM.

| Код | stderr | Результат |
|---|---|---|
| 0 | `Download completed (N bytes)` | успех, если байт ≥ `min_bytes`, иначе `too_small` |
| 8 | `HTTP error NNN` ✓ | сервер ответил: успех при `min_bytes=0`, иначе `http_error` |
| 4 | `Connection error: Connection timed out` | `timeout` (соединение установлено, ответа нет) |
| 4 | `Connection reset prematurely` | `reset` |
| 4 | `SSL error: ...` + `Connection error: Connection failed` | `tls_error` |
| 5 | `SSL verify error` / `Invalid SSL certificate` / `does not match SSL certificate` | `tls_cert` |
| 4 | `Connection error: Connection failed` ✓ (24.10, отказ в соединении) / `Failed to send request: ...` ✓ (25.12 — отказ; обе версии — TCP без ответа за `-T`; DNS) | `connect_failed` |
| −9 | убит по общему таймауту | `timeout` |
| 3/2 | ошибка записи файла | `local_error` |
| 4 | `SSL error: NET - Sending information through the socket failed` + `Connection error: Connection failed` ✓ (24.10, mbedtls: отказ или сброс TCP — ClientHello не ушёл) | `connect_failed` (до 2026-09-22 ошибочно было `tls_error`) |

## 10. Тесты

```
tests/run.sh <корень пакета> <рабочий каталог в /tmp> [бинарник nfqws] [имена тестов...]
tests/lib/ztest.uc          мини-фреймворк (FAIL/RESULT, selfcheck, песочница, перенаправление путей P)
tests/test_*.uc             validate, config, store, strategy, nft, repo, sources, tester, job, commands, uci,
                            bundle, static, health (ensure, probe, монитор, status, log, page), cron (строки cron,
                            init, uci-defaults и prerm в песочнице), v13 (пределы загрузок, https, кэш dry-run,
                            --apply-if-better, IPv6 WAN, английские метаданные), v14 (flowtable, QUIC, игровой фильтр,
                            DNS, диагностика — раздел 13), pkgscripts (последний читает Makefile'ы пакетов движков рядом с
                            корнем пакета: `packages/zaprett-nfqws*/Makefile`; без них — SKIP)
tests/tools/mem_normalize.uc  ручной замер пиковой памяти нормализации подписок (VmHWM)
tests/fixtures/             bundle из zaprett-repo (64 стратегии, 6 bin, листы), фиксированные id bundle, «установленные»,
                            битые и подписочные манифесты, пользовательские файлы, index.json и 102 манифеста
tests/tools/make_fixtures.py  пересборка фикстур из клона zaprett-repo
tests/lib/ucscan.uc         статический сканер запрещённого вызова `(a ?? obj.fn)(x)` (см. test_static)
tests/compile_all.uc        импорт всех модулей (модуль с `export` компилируется только импортом, не `ucode -c`)
```

Запуск на роутере без установки пакета (пишется только рабочий каталог):

```sh
sh tests/run.sh /tmp/zaprett-dev-backend/pkg /tmp/zaprett-dev-backend/work /tmp/zaprett-dev-backend/bin/nfqws
# ZTEST_BLACKHOLE=<неиспользуемый IP изолированного сегмента> — живая проверка «TCP без ответа»
```

**Результат 2026-09-17 (контракт v1.2).** OpenWrt 24.10.8 (ucode 2025.07.18, nft 1.1.1) и 25.12.5
(ucode 2026.01.16, nft 1.1.6): по **990 PASS / 0 FAIL** на каждой (около 2 мин). По тестам: validate 136,
config 42, store 58, strategy 205, nft 36 (37, если в ядре нет `nft_queue`: тогда добавляется проверка, что
парсер `nft -c` всё равно ловит опечатку в `queue`), repo 58 (+2 с `ZTEST_BLACKHOLE`, в этом прогоне не задан),
sources 74, tester 48, job 46, commands 60, uci 113, bundle 41, static 46, pkgscripts 27.

`test_bundle` работает с настоящим `files/usr/share/zaprett/bundle` (пути манифестов переносятся в песочницу):
77 манифестов = 77 записей `index.json`, все 64 стратегии собираются с конфигурацией пакета и проходят
`nfqws --dry-run`; argv alt11 и shizapret-port выводятся строками `NOTE` и сверены с текстом стратегий
вручную, ключевые профили закреплены проверками; все ссылки `presets.json` (листы, ipset, подписки,
`always`, `quick_test_strategies`, `defaults`) существуют. Там же зафиксированы пороги стоимости:
`S.scan()` по настоящему bundle (77 манифестов) — 38–39 мс при пороге 300 мс, `SV.status()` — 56–59 мс,
`C.load()` — 0–1 мс (до правки регулярок было 1553 / 1564 / 39 мс).

`test_uci` выполняет команды, меняющие настройки (`list`, `strategy set`, `mode`, `engine`, `wizard apply`,
`sources save/delete`, offload, cron), на копии `/etc/config/zaprett` из пакета в песочнице
(`P.uci_confdir`). Firewall, cron и init-скрипты в песочнице подменены несуществующими путями, syslog выключен.
После прогона сверено, что `/etc/config/firewall` (md5), `/etc/crontabs`, `/etc/zaprett`, `/var/run/zaprett` и
`/tmp/.uci` стенда не изменились.

Что входит в проверку:
- все 64 стратегии zaprett-repo собираются и проходят `nfqws --dry-run` v72.13;
- argv сверен вручную для strategy-general, -default, -shizapret-port, -alt10 и -alt11;
- сгенерированные скрипты проходят `nft -c -f`. Если в ядре нет `nft_queue`, queue заменяется на accept, а сама
  инструкция queue проверяется парсером;
- статические проверки по всем .uc пакета (`test_static`): запрет прямого вызова скобочного выражения с
  `??`/`||`/`&&`/`?:` и сверка каждого обращения `<алиас>.<имя>` к импортированному модулю `zaprett.*` с его
  экспортами — так ловится класс ошибок «импортирован не тот модуль» (реальный дефект: `N.is_applied()`
  при `import * as N from 'zaprett.net'`, из-за которого падал автоподбор);
- полный проход автоподбора до ветки `status=done`: управление движком и опрос целей подменяются через
  `opts.hooks`, проверяются статусы кандидатов, базовая проверка, `nft_applied`, выбор лучшей стратегии,
  снятие override и отмена между стратегиями;
- живые запуски uclient-fetch к локальным целям;
- параллельные загрузки в три пачки не зависают.

Отрицательные контроли:
- **мутационный прогон** (`tools/dev/mutate_run.py`, не входит в пакет): по одной правке контракта v1.2
  откатывается в копии пакета на VM, и нужный тест обязан упасть. 32 мутации (A–E, M1–M3, LOW1–9,
  `list_missing`, обрезка результатов теста, быстрые проверки имени и id, разделение `diag`, неверный
  импорт модуля — статикой и прогоном автоподбора, неполная очистка каталогов — четырьмя вариантами
  postrm (движки и своё дерево каталогов), сам статический сканер).
  Ожидаемо не обнаруживается одна — `carry += tail` → `carry = tail` в потоковой нормализации:
  эквивалентный мутант, при длине строки до 4 КиБ и блоке 64 КиБ обе формы дают тот же результат; потерю
  строк на границе блоков ловит отдельная мутация `LOW6-chunk-border`;
- сравнение 1≠2 в каждом тесте;
- `domain_valid` сверяется с прежней реализацией (регулярка на каждую метку) на 3043 строках, из них 3000
  случайных из алфавита `a z 0 9 - _ . ^ : A`;
- все проверки, где интервал регулярки заменён на `length()` (`is_id`, `version_valid`, `sha256_valid`,
  `iface_name_valid`, `user_name_valid`, `source_name_valid`, группа IPv6, префикс CIDR, метки, `parse_uint`,
  `ipv4_parse`), сверяются с исходными регулярками на общем наборе строк с граничными длинами
  (0, 1, 15–17, 31–33, 63–65, 95–97, 128) и символами вне класса;
- заведомо неверные стратегии, опции и файлы для dry-run;
- битые nft-скрипты;
- старый `probe.sh` с `wait`: регрессионный тест падает (3 из 7, 63 с);
- `3/4 == 0`;
- падающий и аварийный тест-файл: `run.sh` возвращает 1.

**Результат 2026-09-22 (контракт v1.4).** 24.10.8 и 25.12.5: по **1337 PASS / 0 FAIL** (backend), плагин
227/0. Новый файл `test_v14.uc` — 145 проверок: параметры v1.4 и их отказы, игровой фильтр (состав профилей по
Flowseal, порядок, пустой ipset не считается, `nfqws --dry-run` трёх вариантов и `nfqws2 --intercept=0` с
отрицательными контролями), план и отрисовка своей flowtable, порядок кандидатов hw → sw → без неё и откат
(`apply_candidates` с подменой ядра), `nft -c` всех новых вариантов и отказ на несуществующем устройстве, QUIC с
фильтром клиентов и IPv6, провайдеры DNS, `dns-setup` (успех, повтор без действий, ошибки обновления и установки,
«установлен, но не запустился»), `dns_plain`, разбор `nslookup`/DoH, адреса-заглушки, признак TCP по текстам
uclient-fetch обеих версий, все вердикты, задача `diagnose` целиком с подменой сети, поля `page`, тексты, CLI.

Мутационная проверка v1.4 (скрипт-драйвер вне пакета, копия пакета на тестовом роутере 25.12): 19 мутаций, **19 обнаружены**, контрольный
прогон без мутаций 229/0 — порог `flow add` убран; порог = min вместо max; план без учёта исходного ускорения;
нет отката к следующему кандидату; QUIC без фильтра клиентов; пустой ipset считается активным; нет guard-ipset;
игровые профили не в конце; «установлен» = «работает»; `dns-setup` без проверки запуска; `dns_plain` при шифрованном
DNS; 10/8 не заглушка; «адрес другой» раньше «сайт открылся» (ложная подмена DNS у CDN); текст отказа TCP 24.10 не
учтён в `tcp_connected` и в `classify`; любой размер = замедление; `page overview` без `dns`; hw и sw переставлены;
отрицание в игровых портах. `status → flowtable_failed` юнит-тестом не закреплено (зависит от таблицы в ядре стенда) —
проверено вживую (раздел 13).

## 11. Отладка

- `zaprett status`, `zaprett check` — что сломано и почему (аргументы, dry-run, предупреждения).
- `zaprett diag` — быстрый отчёт: версии, наличие движков, модули ядра (из `/sys/module`), состояние, UCI,
  args, `status.json`, таблица nft, фоновая задача. Рассчитан на ответ веб-интерфейсу (RPC ждёт ≤19 с), поэтому
  перечисление пакетов (`opkg list-installed` / `apk list -I`, до 60 с) и журнал в него не входят — вместо них
  строка «полный отчёт: zaprett diag --full (по SSH)». На VM стенда быстрый отчёт занимает доли секунды.
- `zaprett diag --full` — то же плюс список установленных пакетов и последние 100 строк `logread -e zaprett`.
  Запускать по SSH: на слабом роутере он может не успеть ответить веб-интерфейсу.
- `logread -e zaprett` — сообщения бэкенда; вывод nfqws пишется в syslog под именем бинарника. `debug '1'`
  добавляет `--debug=syslog`.
- Файлы: `/var/run/zaprett/{args,engine,ports.json,status.json,zaprett.nft,job.json,job.log,test-override,
  test-results.json,repo/index.json,probe.json,monitor.json,dryrun-ok.json,stopped}`,
  `/etc/zaprett/{sources-state.json,offload-saved.json}`.
- `zaprett log --tail 50` — последние строки zaprett и nfqws; `logread | grep сторож` — действия сторожа.
- `zaprett fw show` — скрипт nft без применения; `nft list table inet zaprett` — что стоит в ядре.

## 12. Отклонения от контракта и ограничения

Отклонения (с причинами):
1. `conffiles`: вместо каталога `/etc/zaprett/user/` перечислены четыре файла. opkg и apk ведут conffiles
   пофайлово (apk считает sha256 только файлов); каталог целиком сохраняется через `keep.d`.
2. Профиль с `${hostlists}` и `${ipsets}` одновременно — правило раздела 3. Буквальное «всегда guard» ломало бы
   alt10, а exclude-хостлисты в ipset-профиле требовали бы SNI.
3. `profile_unfiltered` выдаётся только в режиме whitelist: в blacklist весь трафик и так обрабатывается.
4. Проверка путей файлов в опциях стратегий и удаление управляемых zaprett опций — дополнительная защита.
5. `uclient-fetch` вызывается без `-q` (§10 п.4 указывает `-q`, но тогда нет текстов для классификации), с
   браузерным User-Agent и с `-4` при `ipv6=0`.
6. nft: connmark и фильтр клиентов в prenat, `packets 1` вместо `1-1` (раздел 5).
7. Offload возвращается при выключении службы (`enabled=0`, `disable`, `stop`), а не при каждом внутреннем
   перезапуске. `reload_service` не вызывает `fw apply` отдельно: его делает `start_service`.
8. Автоподбор и изменения через CLI не запускают остановленный вручную движок.
9. Дополнительные модули `validate`, `net`, `sources`, `commands`, `text` и скрипт `probe.sh`; служебные команды и
   флаги (`offload`, `cron sync`, `__job-run`, `--if-applied`); дополнительные поля JSON.
   `diag` разделён на быстрый (по умолчанию, для веб-интерфейса) и `--full` (по SSH): §6.2 описывает один
   отчёт, но перечисление пакетов не укладывается в таймаут RPC на слабом роутере.
10. id, начинающиеся с точки, запрещены (строже regex контракта).
11. `restart` при `enabled=0` возвращает ошибку `disabled`; `enable` не запускает движок.
12. `sources list` содержит также `min_entries`, `message`, `downloaded`; статус `invalid` — для секции с
    неверным типом или ссылкой. `sources save` отвечает также `created`, `type_changed`, `url_changed`;
    `wizard apply` — `sources_disabled`.
13. Служебная команда `sources defaults` (для `uci-defaults`); значения подписок по умолчанию продублированы в
    `config.uc` (`DEFAULT_SOURCES`), тест сверяет их с `/etc/config/zaprett`.
14. `list_missing` выдаёт `status`, когда включён обычный лист, которого нет среди установленных (генерация в
    этом случае падает с `item_not_found`, и `generate_failed` одно не объясняет причину).
15. v1.3: строки сторожа и монитора снимаются и при `zaprett stop` (маркер `/var/run/zaprett/stopped`), а не
    только при `enabled=0` — иначе сторож через 5 минут запускал бы остановленную вручную службу. Внесено в
    контракт (§14.3, «Уточнения по реализации»).
16. v1.3: счётчики в результатах задачи `probe` и `monitor run` называются `reachable` (поле `ok` ответа занято
    признаком успеха); в `probe status` / `monitor status` — `ok`, как в контракте.
17. v1.3: сервис считается включённым и по элементу своей подписки `src-<имя>` в `lists`/`ipsets` (раньше — только
    по листам и ipset); это меняет и цели автоподбора (у «Сайты за Cloudflare» они появляются).
18. v1.3: `monitor.json` лежит в tmpfs — после перезагрузки история и время последнего авторемонта пропадают,
    поэтому авторемонт после перезагрузки возможен раньше 6 часов.

19. v1.4: собственная flowtable ставится, только если fw4-ускорение было включено до zaprett (сохранённое значение
    или текущее): роутеру без ускорения zaprett его не добавляет. `own` для такого роутера ведёт себя как `auto`.
20. v1.4: flowtable с `counter` (как у fw4) и `hook ingress priority filter`; аппаратный вариант (`flags offload`)
    пробуется первым, при отказе ядра — программный, затем таблица без flowtable (`flowtable_failed`).
21. v1.4: блокировка QUIC — `counter drop` в своей цепочке `forward_quic` (hook forward, priority filter − 1), фильтр
    клиентов действует и во время автоподбора (цепочка `clients_mark` ставится ради QUIC и в тестовом режиме).
22. v1.4: игровой фильтр — два профиля (TCP и UDP, каждый при непустых портах), с guard-ipset и всеми
    `--ipset-exclude`; для nfqws2 — та же атака в его синтаксисе (`--blob`, `--out-range`, `--lua-desync`).
    Широкие игровые порты дают штатное `wide_port_range`.
23. v1.4: `diagnose` — дополнительные поля цели `service, reason, error, bytes, tcp`; подменённый сертификат и страница
    с текстом заглушки → `http_block`; «замедление» — загрузка оборвалась или замерла, когда тело уже шло и его было
    не больше 24 КиБ (нижней границы 14 КБ по телу нет: uclient-fetch видит только тело, а TLS и заголовки съедают
    первые килобайты — на стенде 26000 байт соединения дали 5433 байта тела); `summary.verdict` при равенстве —
    по порядку списка вердиктов.

Ограничения и что не проверено:
- Сборка пакета в SDK (Makefile), установка, `uci-defaults` (в том числе `sources defaults`),
  init/procd/fw4/hotplug/cron, фоновые задачи через `start-stop-daemon` из CLI, автоподбор, установка из
  репозитория и загрузка подписок из интернета **этими тестами не покрыты**: в них проверяются чистые
  функции, команды в песочнице, компиляция и синтаксис. Что из этого проверено вживую на роутере, а что
  нет, перечислено в `../tests/RESULTS.md`. Логика подписок проверена с подставленным загрузчиком;
  обрыв по `ulimit -f` — живой загрузкой с локального uhttpd.
- Размер артефакта репозитория заранее неизвестен: загрузка идёт в RAM (tmpfs) и обрывается на 32 МиБ
  (индекс — 2 МиБ, манифест — 64 КиБ, подписка — 16 МиБ). `repo list` показывает `size` только для
  установленного.
- Не проверено вживую: авторемонт монитора (запуск `test --quick --apply-if-better` при `degraded`) и полный
  проход `test start --apply-if-better` на роутере — только юнит-тестами с подменой движка и опроса целей.
- Число записей gzip-листов не считается (`entries: null`).
- `uclient-fetch -T` — таймаут бездействия: медленный, но живой ответ может идти дольше. Общий предел на пачку
  проверок — `(timeout·3+5)` с на группу.
- nfqws не принимает ipset-файл с mtime 0 («cannot access ipset file»). На стенде это проявилось при распаковке
  tar без времени; у файлов пакетов OpenWrt время ненулевое.
- Hotplug и `stop` разведены блокировкой fw: `fw remove` сначала удаляет `zaprett.nft`, и ждущий hotplug с
  `--if-applied` после неё ничего не ставит. Одновременный запуск init-действий этим не упорядочивается
  (порядок задаёт `procd_lock`).
- Нормализация подписок выполняется в ucode. Замер на x86-VM стенда (25.12.5, 4 vCPU), `tests/tools/mem_normalize.uc`:
  | Файл | потоково (`sources update`) | целиком в памяти (контроль) |
  |---|---|---|
  | 1,5 МиБ, 52 000 доменов | 1,5 с, пик VmHWM 2,0 МиБ | 1,4 с, 10,6 МиБ |
  | 16 МиБ, 520 217 строк | 15,4 с, пик 2,0 МиБ | 13,5 с, 95,9 МиБ |
  Результаты обоих режимов совпадают побайтно. До ускорения проверки имени (`hostname_labels_valid`) 16 МиБ
  обрабатывались 94 с. На слабых MIPS время будет в разы больше (не измерялось); tmpfs на время обработки
  занимают скачанный файл (≤16 МиБ) и нормализованная копия, тело удаляется сразу после нормализации.
  Стоимость проверки одной строки на той же VM: домен 0,019 мс, IPv4-CIDR 0,072 мс, IPv6-CIDR 0,122 мс
  (до правки регулярок — 0,032 / 0,083 / 0,343 мс). Большая ipset-подписка на сотни тысяч строк остаётся
  дорогой по времени: 500 тыс. строк IPv6 — около минуты на x86-VM.
- Автоподбор с роутера проверяет только трафик самого роутера; при `clients_mode=include` фильтр клиентов на
  время теста снимается.

## 13. Контракт v1.4 (§15): как устроено и как проверено

### 13.1. Собственная flowtable (`flow_offload own`, по умолчанию для новых установок)

- `start_service`: `fw apply` рисует таблицу с `flowtable ft { hook ingress priority filter; devices = {…}; counter; }`
  и `chain forward_offload { type filter hook forward priority filter + 1; meta l4proto { tcp, udp } ct original packets
  > N flow add @ft }`, N = max(`tcp_pkt_out`, `udp_pkt_out`); затем `offload apply` сохраняет и выключает ускорение fw4
  (как `auto`). Устройства — `related_physdevs` зон из `/var/run/fw4.state`, существующие в `/sys/class/net` (как у fw4
  для программного ускорения); при исходном `flow_offloading_hw=1` — сначала вариант с `flags offload` на нижних
  устройствах мостов/VLAN (`lower_*`, как `resolve_lower_devices` fw4).
- Нет устройств или ядро не приняло ни один вариант → таблица без flowtable, `flowtable_failed` (в `fw apply` и в
  `status`). `status.flow_offload.own` — flowtable есть в `nft list table inet zaprett`.
- `stop`/`disable` → `offload restore` возвращает `flow_offloading(_hw)` fw4, таблица снимается.

Проверено на тестовом роутере (OpenWrt 25.12.5), клиентская машина за роутером, исходно `flow_offloading=1` (выставлено для опыта):
- в ядре `flowtable ft { hook ingress priority filter devices = { "br-lan", "eth1" } counter }` и правило
  `meta l4proto { tcp, udp } ct original packets > 9 flow add @ft`; fw4 своей flowtable больше не держит;
- счётчик очереди (8-й столбец `nfnetlink_queue`) на три запроса клиента к `https://www.youtube.com/`: 0 → 12 → 24 → 36
  (+12 на соединение: 9 исходящих + 3 ответных). Отрицательный контроль — `keep` при включённом ускорении fw4:
  0 → 3 → 6 → 9 (+3: движок видит только рукопожатие TCP, ClientHello уходит мимо);
- соединение клиента в `/proc/net/nf_conntrack` с флагом `[OFFLOAD]` (и `mark=1073741824` — прошло через очередь);
  в `auto` тех же загрузок `[OFFLOAD]` — 0;
- после `zaprett stop` — `flow_offloading=1`, flowtable fw4 вернулась, `offload-saved.json` удалён;
- нет устройств (обнулены `related_physdevs` в `fw4.state`) → `fw apply` `flowtable: null, warnings: ["flowtable_failed"]`,
  `status.flow_offload.own=false` + `flowtable_failed`; после `firewall reload` — снова `sw`, предупреждения нет;
- `flow_offloading_hw=1` → `flowtable ft { devices = { "eth0", "eth1" } flags offload counter }` (br-lan заменён на eth0).

Пропускная способность (клиент → тестовый роутер → HTTP-сервер в той же сети, адреса 192.168/16 в очередь не попадают, меряется
только пересылка): без ограничений ~10 Гбит/с во всех режимах (auto 10065/10444, own 10489/10120, keep 10381/10353
Мбит/с; softirq роутера 34–37 % / 26–31 % / 27–28 %); при `cpulimit 0.3` у VM роутера — auto 1143/1103, own 1395/1164,
keep 1179/1164 Мбит/с. Разница в пределах разброса: x86-VM стенда упирается не в процессор роутера, выигрыш
ускорения на слабом железе здесь **не измерен**.

Почему `own` по умолчанию: при включённом ускорении fw4 режим `keep` ломает обход (движок не видит ClientHello —
измерено выше), `auto` работает, но выключает ускорение для всего трафика, а `own` сохраняет и обход (движок видит
те же 12 пакетов, что в `auto`), и ускорение ([OFFLOAD]); для роутера без ускорения `own` ≡ `auto`, при отказе
flowtable — тоже `auto` с предупреждением. У существующих установок значение в `/etc/config/zaprett` не меняется.

### 13.2. Блокировка QUIC и игровой фильтр

- `quic_block=1`: `chain forward_quic { type filter hook forward priority filter - 1; iifname != @wanif oifname @wanif
  udp dport 443 [фильтр клиентов] counter drop }` (+ `wanif6` при `ipv6=1`). Вживую: три UDP-пакета клиента на
  8.8.8.8:443 — `counter packets 3 bytes 99 drop`; на порт 444 — прошли (conntrack `dport=444 … [UNREPLIED]` с SNAT);
  при `quic_block=0` цепочки нет.
- `game_filter=1`: в конец аргументов — профили по образцу Game Filter Flowseal zapret-discord-youtube (`general.bat`,
  UDP-фейк — как в редакции 2026-02-23; текущая 2026-08-30 берёт `ACTIVE_GAME_UDP.bin` = `quic_initial_4pda_to.bin`,
  которого нет в bundle):
  ```
  --filter-tcp=<game_ports_tcp> --ipset=<включённые include-ipset> --ipset=<guard> --ipset-exclude=<…>
    --dpi-desync=multisplit --dpi-desync-any-protocol=1 --dpi-desync-cutoff=n3 --dpi-desync-split-seqovl=568
    --dpi-desync-split-pos=1 --dpi-desync-split-seqovl-pattern=<bin tls_clienthello_4pda_to>
  --filter-udp=<game_ports_udp> --ipset=… --ipset-exclude=… --dpi-desync=fake --dpi-desync-repeats=12
    --dpi-desync-any-protocol=1 --dpi-desync-fake-unknown-udp=<bin quic_initial_www_google_com> --dpi-desync-cutoff=n2
  ```
  nfqws2: `--blob=zaprett_game_tcp:@<bin> --out-range=<n3 --payload=all --lua-desync=multisplit:pos=1:seqovl=568:seqovl_pattern=zaprett_game_tcp`
  и `--blob=zaprett_game_udp:@<bin> --out-range=<n2 --payload=all --lua-desync=fake:blob=zaprett_game_udp:repeats=12`.
  Пустой (0 записей) include-ipset не считается: у nfqws пустой фильтр пропускает всё. Вживую на обеих VM: без ipset —
  `game_filter_no_ipsets`, профилей нет; с `zaprett-telegram-ipset` — движок работает с обоими профилями, в `postnat`
  порты `{ 80, 443, 1024-65535 }` и `{ 443, 1024-65535 }`, `zaprett check` — `dry_run.rc 0`.

### 13.3. Шифрованный DNS

`dns setup` на тестовых роутерах 25.12 (apk) и 24.10 (opkg): `{changed:true, packages:["https-dns-proxy","luci-app-https-dns-proxy"],
manager:"apk"|"opkg", dns:{encrypted:true, provider:"https-dns-proxy"}}` за 17–19 с; до — `encrypted:false`; повтор —
`{changed:false}` без задачи. После: dnsmasq `noresolv=1`, `server=127.0.0.1#5053/5054`, прокси слушает 5053/5054,
`nslookup` через dnsmasq и напрямую через 5053 отвечает. `dns_plain`: с включённым `zaprett-rutracker` — нет при
работающем прокси, есть после `/etc/init.d/https-dns-proxy stop`, снова нет после `start`.
`needs_dns: true` — `rutracker` (заметка сервиса о подмене DNS), `rkn_full` («многие сайты заблокированы … через
DNS»), `whatsapp` (исключение доменов из НСДИ; сервис `works:"no"`, предупреждения не даёт, поле — для интерфейса).

### 13.4. Диагностика

Порядок для каждой цели (`test_targets` выбранных или включённых сервисов, не больше 20): `nslookup -type=a` через
системный DNS; DoH по IP-адресу — `https://8.8.8.8/resolve?name=…&type=A`, при отказе
`https://1.1.1.1/dns-query?…` с `accept: application/dns-json` (у Cloudflare без заголовка — HTTP-ошибка, проверено);
загрузка как у автоподбора (`probe.sh`, `-4`, тело до 1 МиБ); при `connect_failed`/`reset`/`timeout` без данных —
TCP до двух адресов из DoH (сначала совпавших с системными) через `uclient-fetch https://<ip>:<порт>/`: ошибка
сертификата или любой ответ = соединение есть; `Failed to send request`, `Connection failed` без SSL-ошибки и
(24.10) `NET - Sending information through the socket failed` = нет.
Вердикт (`judge`): все системные адреса — заглушки (0/8, 10/8, 127/8, 100.64/10, 169.254/16, 172.16/12,
192.168/16, 224/3) → `dns_spoof`; сайт открылся → `ok` (адрес CDN может отличаться от DoH — это не подмена); нет
системного ответа при ответе DoH или ни одного общего адреса → `dns_spoof`; 451, чужой сертификат, текст заглушки →
`http_block`; обрыв/зависание при теле 1…24576 байт → `throttle`; SSL-ошибка → `tls_block`; TCP до настоящего адреса
не устанавливается → `ip_block`; соединение есть, но сброс/таймаут → `tls_block`; иначе `unknown`.

Живые отрицательные контроли на обеих VM (временные `/tmp/hosts/b2-ztest` и таблица `inet ztest` удалены, проверено
`ls`/`nft list`): `10.10.10.10 www.youtube.com` в `/tmp/hosts` → `dns_spoof` (`stub_address`), после удаления — `ok`;
`reject with tcp reset` на все адреса `gateway.discord.gg` → `ip_block` (`tcp_failed`), после удаления — `ok`
(на 24.10 первый прогон дал `tls_block` — отсюда исправление классификатора, см. раздел 9); сброс ответов
`www.youtube.com` после 26000 байт соединения → `throttle` (тело 10700 байт), после 90000 байт → не `throttle`
(`tls_block`, тело 71 КБ). На стенде трафик уходит через xray-tproxy VM 200: TCP «устанавливается» всегда, поэтому
`discord.com` (на стенде не отвечает и при выключенном обходе) даёт `tls_block`, а подмена на публичный чужой адрес
не воспроизводится (tproxy ведёт по SNI на настоящий сервер) — проверено только заглушкой.
