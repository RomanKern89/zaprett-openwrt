# zaprett для OpenWrt — архитектура и контракты

> Версия документа: 1.2 (2026-09-17): закрытый список warnings, `recommended_tier`, `reloaded`, лимиты LuCI; v1.1 — добавлены подписки по URL (§4, §6.2, §11), исключения в обоих режимах (§5), пресеты с уровнями и целями (§9). Это **контракт** между частями продукта: бэкенд (`packages/zaprett`),
> веб-интерфейс (`packages/luci-app-zaprett`), бинарные пакеты (`packages/zaprett-nfqws*`), сборка (`build/`),
> данные по умолчанию (`packages/zaprett/files/usr/share/zaprett/bundle`). Меняя интерфейс, меняй этот файл.
>
> Ключевые решения (бэкенд на ucode без Lua, основной движок nfqws, собственная таблица nftables
> `inet zaprett`, защита от пустых списков) описаны в соответствующих разделах этого документа.

---

## 1. Что это

Порт Android-менеджера **zaprett** (CherretGit/zaprett-app + egor-white/zaprett) на роутеры OpenWrt 24.10 (opkg)
и 25.12 (apk). Сохраняется модель оригинала:

- **движок**: `nfqws` (zapret v72.13, основной) или `nfqws2` (zapret2, опционально);
- **стратегия** — текстовый файл аргументов движка с плейсхолдерами `${hostlists}`, `${ipsets}`,
  `${hostlist:id}`, `${hostlist_exclude:id}`, `${ipset:id}`, `${ipset_exclude:id}`, `${bin:id}`, `${lua_lib:id}`;
- **листы** доменов и **ipset** IP-сетей: include и exclude; **режим** `whitelist` (обход только по include-листам)
  или `blacklist` (обход всего, кроме exclude-листов);
- **репозиторий** zaprett-repo: `index.json` → манифесты (`schema 1`, `artifact.url`, `artifact.sha256`,
  `dependencies` = URL манифестов) → файлы;
- **автоподбор стратегии**: перебор стратегий с проверкой доступности целей и ранжированием.

Роутерные отличия от оригинала (обязательны):
1. nftables-таблица `inet zaprett` с очередью только первых пакетов и фильтром по mark (ADR-003), а не
   «весь трафик в NFQUEUE».
2. Защита от пустых листов (ADR-004).
3. Фильтр по клиентам LAN (IP/MAC) — аналог белого/чёрного списка приложений Android.
4. Веб-интерфейс LuCI на русском, фоновые задачи с прогрессом.
5. Работа без доступа к GitHub: встроенный снимок (bundle) листов, стратегий и фейков.

---

## 2. Пакеты

| Пакет | Arch | Содержимое | Зависимости |
|---|---|---|---|
| `zaprett` | all/noarch | CLI, ucode-библиотека, init, hotplug, uci-defaults, fw4-include, bundle | `+ucode +ucode-mod-fs +ucode-mod-uci +ucode-mod-ubus +nftables +kmod-nft-queue +uclient-fetch +ca-bundle +zaprett-nfqws` |
| `zaprett-nfqws` | по arch | `/usr/libexec/zaprett/nfqws` (статический zapret v72.13) | — |
| `zaprett-nfqws2` | по arch | `/usr/libexec/zaprett/nfqws2` (статический zapret2 v1.0.5.2) + `/usr/share/zaprett/lua/*.lua.gz` | — |
| `luci-app-zaprett` | all/noarch | JS-страницы, menu.d, acl.d, rpcd ucode-плагин `luci.zaprett` | `+luci-base +rpcd-mod-ucode +zaprett` |
| `luci-i18n-zaprett-ru` | all/noarch | перевод (генерирует luci.mk из `po/ru`) | — |

Версии: `zaprett`, `luci-app-zaprett` — `1.0.0-r1`; `zaprett-nfqws` — `72.13-r1`; `zaprett-nfqws2` — `1.0.5.2-r1`
(формат допустим для apk: `<digits>(.<digits>)*-r<N>`).

Соответствие OpenWrt arch → каталог статических бинарников релиза (research/01 §1.4):
`aarch64_*`→`linux-arm64`, `arm_*`→`linux-arm`, `mipsel_*`→`linux-mipsel`, `mips_*`→`linux-mips`,
`mips64_*`→`linux-mips64`, `i386_*`→`linux-x86`, `x86_64`→`linux-x86_64`, `powerpc_*`→`linux-ppc`,
`riscv64_*`→ только nfqws2 `linux-riscv64`. `mips64el_*`, `loongarch64_*`, `powerpc64_*` — бинарников нет.
**Уточнено сборкой (2026-09-17):** `linux-arm` собран под ARMv6KZ (`readelf -A`: Tag_CPU_arch v6KZ) → `arm_arm926ej-s`,
`arm_fa526`, `arm_xscale`, `armeb_xscale` (ARMv4/v5) **не поддерживаются**. riscv64 в v1.0 не поддерживается: пакет
`zaprett` зависит от `zaprett-nfqws`, которого для riscv64 нет (решение — виртуальная зависимость на движок, отложено).
Итог: 25.12 — 27 архитектур, 24.10 — 28 (список — `build/arches.txt`, `docs/BUILD.md`).

---

## 3. Файловая раскладка на роутере

```
/etc/config/zaprett                          UCI, единственный источник настроек (conffile)
/etc/init.d/zaprett                          procd: инстанс движка, respawn, триггеры
/etc/uci-defaults/90-zaprett                 первичная настройка, идемпотентно (fw4 include, cron)
/etc/hotplug.d/iface/60-zaprett              ifup/ifdown WAN → обновить наборы wanif
/usr/bin/zaprett                             CLI (sh-обёртка: exec ucode -S /usr/share/zaprett/cli.uc "$@")
/usr/share/zaprett/cli.uc                    точка входа CLI
/usr/share/ucode/zaprett/*.uc                библиотека (модули, см. §6)
/usr/share/zaprett/fw4-include.sh            скрипт fw4-include: восстановить таблицу после firewall start/reload
/usr/share/zaprett/guard/hostlist-guard.txt  `zaprett-guard.invalid`
/usr/share/zaprett/guard/ipset-guard.txt     `192.0.2.255/32` и `100::ffff/128`
/usr/share/zaprett/presets.json              сервисы → листы, цели проверки (§9)
/usr/share/zaprett/bundle/manifests/<dir>/<id>.json   встроенный снимок (read-only, ROM)
/usr/share/zaprett/bundle/files/<dir>/<file>
/usr/libexec/zaprett/nfqws, nfqws2           бинарники (из пакетов zaprett-nfqws*)
/etc/zaprett/manifests/<dir>/<id>.json       установленное из репозитория (overlay, сохраняется при sysupgrade)
/etc/zaprett/files/<dir>/<file>
/etc/zaprett/user/hosts-include.txt          пользовательские домены (редактор в LuCI) — id `user-hosts`
/etc/zaprett/user/hosts-exclude.txt          id `user-hosts-exclude`
/etc/zaprett/user/ipset-include.txt          id `user-ipset`
/etc/zaprett/user/ipset-exclude.txt          id `user-ipset-exclude`
/etc/zaprett/user/strategies/<engine>/<id>.txt   свои стратегии (манифест генерируется на лету)
/lib/upgrade/keep.d/zaprett                  /etc/zaprett/ сохраняется при sysupgrade
/var/run/zaprett/                            рантайм (tmpfs): args, nft, status, job, test, cache
/var/lock/zaprett.lock, zaprett-job.lock     flock
```

`<dir>` — как в оригинале: `lists/include`, `lists/exclude`, `ipset/include`, `ipset/exclude`,
`strategies/nfqws`, `strategies/nfqws2`, `strategies/byedpi` (игнорируется на роутере), `bin`, `lua`.
Тип элемента репозитория → `<dir>`: `list`→`lists/include`, `list_exclude`→`lists/exclude`, `ipset`→`ipset/include`,
`ipset_exclude`→`ipset/exclude`, `nfqws`→`strategies/nfqws`, `nfqws2`→`strategies/nfqws2`, `bin`→`bin`,
`lua_lib`→`lua`, `byedpi`→ не поддерживается (показывать как недоступный на роутере).

**Разрешение элемента по id:** `/etc/zaprett` (установлено) имеет приоритет над `bundle`. Пользовательские
элементы (`user-*`) — виртуальные манифесты. Локальный манифест = манифест оригинала + поля:

```json
{ "schema": 1, "id": "list-youtube", "name": "...", "version": "1.0.0", "author": "...",
  "description": "...", "dependencies": ["<id>", "..."], "file": "/etc/zaprett/files/lists/include/list-youtube.txt",
  "source": "bundle|repo|user", "sha256": "<hex>", "installed_at": 1758100000, "manifest_url": "https://..." }
```

`dependencies` в локальном манифесте — **id**, а не URL (URL переводится в id при установке по индексу).
Поле `type` (тип элемента репозитория) в локальном манифесте — желательно; если нет, тип выводится из каталога.
`bundle/index.json` — вспомогательный перечень, источник истины — каталоги `manifests/`. В upstream-индексе id
**не уникальны между типами** (`strategy-default` есть и nfqws, и byedpi) — ключ элемента = пара (type, id).

**Id встроенных элементов bundle (фиксированы):** листы `zaprett-youtube`, `zaprett-discord`, `zaprett-telegram`,
`zaprett-rutracker` (type `list`); `zaprett-telegram-ipset` (`ipset`); `zaprett-exclude` (`list_exclude`);
`zaprett-exclude-ipset` (`ipset_exclude`); стратегии и фейки — с id оригинала zaprett-repo (`strategy-general`,
`quic_initial_www_google_com` …). Подписки по умолчанию (секции `source`, все `enabled '0'`): `refilter_domains`
(list), `antifilter_allyouneed` (ipset), `cloudflare_v4` и `cloudflare_v6` (ipset, официальные диапазоны).

---

## 4. UCI `/etc/config/zaprett`

```
config main 'main'
	option enabled '0'                 # автозапуск и работа сервиса
	option engine 'nfqws'              # nfqws | nfqws2
	option strategy 'strategy-general' # id стратегии для nfqws
	option strategy_nfqws2 ''          # id стратегии для nfqws2
	option list_mode 'whitelist'       # whitelist | blacklist
	list lists 'zaprett-youtube'       # активные include-листы (id)
	list lists 'zaprett-discord'
	list lists 'user-hosts'
	list exclude_lists 'zaprett-exclude'
	list exclude_lists 'user-hosts-exclude'
	list ipsets ''                     # активные include-ipset (id)
	list exclude_ipsets 'zaprett-exclude-ipset'
	list exclude_ipsets 'user-ipset-exclude'
	option qnum '200'
	option desync_mark '0x40000000'
	option postnat_mark '0x20000000'
	option ipv6 '0'
	list wan ''                        # логические интерфейсы; пусто = все с маршрутом по умолчанию
	option tcp_pkt_out '9'
	option tcp_pkt_in '3'
	option udp_pkt_out '9'
	option udp_pkt_in '0'
	option flow_offload 'auto'         # auto (выключить fw4 offload при включении zaprett и вернуть при выключении) | keep
	option clients_mode 'all'          # all | include | exclude
	list clients ''                    # IPv4/CIDR или MAC
	option user 'daemon'               # пользователь nfqws после сброса привилегий
	option debug '0'
	list deleted_sources ''           # подписки по умолчанию, удалённые пользователем (uci-defaults их не возвращает)

config repo 'repo'
	option url 'https://raw.githubusercontent.com/CherretGit/zaprett-repo/refs/heads/main/index.json'
	option autoupdate '1'              # обновлять установленные элементы по cron
	option autoupdate_hour '4'

config test 'test'
	option timeout '5'                 # секунд на запрос
	option concurrency '6'
	option max_domains '20'            # доменов из листов на стратегию (плюс test_urls пресетов)
	option settle '2'                  # секунд после перезапуска движка до проверок

# Подписки на внешние листы по URL (без sha256-манифестов). Добавлено в v1.1 контракта.
config source 'refilter_domains'
	option enabled '0'
	option name 'Re:filter — заблокированные домены'
	option type 'list'                 # list | list_exclude | ipset | ipset_exclude
	option url 'https://github.com/1andrevich/Re-filter-lists/releases/latest/download/domains_all.lst'
	option interval_hours '72'         # не чаще раза в 48 ч для больших списков (износ flash)
	option min_entries '1000'          # меньше — загрузка считается битой, старый файл сохраняется
	option min_valid_ratio '0.99'
	option ram_mib '9'                 # оценка RAM nfqws, для предупреждения в UI
```

Подписка после успешной загрузки становится обычным элементом: id = `src-<имя секции>` (например
`src-refilter_domains`), файл `/etc/zaprett/files/<dir>/src-<имя>.txt`, манифест с `"source": "url"`, `version` =
дата загрузки `YYYY.MM.DD`, `sha256` посчитанный. Включается в работу так же, как листы: id в `lists`/`ipsets`/…
Нормализация при загрузке: убрать BOM и CR, обрезать пробелы, пропустить пустые и комментарии, выбросить `*.`-маски
и строки с пробелом внутри, домены привести к нижнему регистру, IDN не в punycode — выбросить; ipset — проверить
CIDR v4/v6. Запись на flash — только если sha256 изменился, атомарно.

Изменение через LuCI «Сохранить и применить» → `uci commit` → procd reload (§7).

---

## 5. Сборка аргументов движка (генератор)

Вход: UCI + выбранная стратегия. Выход: argv **построчно** в `/var/run/zaprett/args` (одна опция на строку,
без shell-раскрытия) и код возврата 0; при ошибке — код ≠ 0 и понятное сообщение на русском в stderr/JSON.

Алгоритм:
1. Взять текст стратегии (файл из манифеста); удалить висячие `\` в концах строк; разбить на токены по
   пробельным символам (как `split_whitespace` оригинала). `--comment` без `=` и все следующие токены до
   ближайшего токена, начинающегося с `--`, — выбросить (research/01 §5.3.2).
2. Нормализовать устаревшие режимы в значении `--dpi-desync=`: `split`→`fakedsplit`, `split2`→`multisplit`,
   `disorder`→`fakeddisorder`, `disorder2`→`multidisorder` (только nfqws).
3. Раскрыть плейсхолдеры **внутри токена** (токен может быть `--dpi-desync-fake-quic=${bin:x}`):
   - `${hostlists}` → whitelist: по токену `--hostlist=<file>` на каждый активный include-лист + всегда
     `--hostlist=/usr/share/zaprett/guard/hostlist-guard.txt` **+ `--hostlist-exclude=<file>` на каждый активный
     exclude-лист** (исключения действуют в обоих режимах — пользователь ожидает, что «Исключения» работают всегда;
     nfqws проверяет exclude раньше include); blacklist: только `--hostlist-exclude=<file>` на каждый активный
     exclude-лист (без guard, include-листы игнорируются). Если плейсхолдер — часть большего токена, это ошибка.
   - `${ipsets}` → аналогично: whitelist — `--ipset=<file>` + guard (`ipset-guard.txt`) + `--ipset-exclude=<file>`
     на каждый активный exclude-ipset; blacklist — только `--ipset-exclude=<file>`.
   - `${hostlist:id}` / `${hostlist_exclude:id}` / `${ipset:id}` / `${ipset_exclude:id}` / `${bin:id}` /
     `${lua_lib:id}` → путь файла элемента id. Элемент должен быть установлен; если это стратегия из
     репозитория с объявленными зависимостями — id обязан быть в её `dependencies` (как оригинал).
   - `${zaprettdir}` (встречается в стратегиях по умолчанию оригинала) → `/usr/share/zaprett/bundle/files`.
   - Неизвестный плейсхолдер → ошибка.
4. Добавить базовые опции в начало: nfqws — `--qnum=<qnum>`, `--user=<user>`, `--dpi-desync-fwmark=<desync_mark>`;
   nfqws2 — `--qnum`, `--user`, `--fwmark`, `--lua-init=@/usr/share/zaprett/lua/zapret-lib.lua` и
   `@…/zapret-antidpi.lua` (если в стратегии нет своих `--lua-init`). При `debug=1` — `--debug=syslog` первым.
5. Проверить: nfqws — `nfqws --dry-run <args>` (код 0 обязателен); nfqws2 — `nfqws2 --intercept=0 <args>` от root.
6. Из итоговых args извлечь порты для nft: объединение значений `--filter-tcp`/`--filter-udp` по всем профилям
   (профиль = отрезок между `--new`). Значения вида `a,b-c` раскладываются; `~` (отрицание) → профиль считать
   «без фильтра» по этому протоколу. Профиль без `--filter-tcp` и без `--filter-udp` → TCP `80,443` и UDP `443`.
   Итог — множества портов/диапазонов TCP и UDP (могут быть пустыми).

Если активных include-листов нет, режим whitelist и стратегия использует `${hostlists}` — запуск разрешён
(guard защищает), но статус несёт предупреждение `no_active_lists`.

**Граница исключений (v1.2, решение по ревью):** доменные исключения (`--hostlist-exclude`) вставляются только в
профили, где есть `${hostlists}`. В профили «только `${ipsets}`» их **не** вставлять: nfqws с непустым hostlist-фильтром
не срабатывает при неизвестном имени хоста (`nfq/desync.c:254`) — сломался бы обход трафика без SNI (Telegram MTProto).
Такие профили исключаются только IP-исключениями. Обратное безопасно и обязательно: в профили «только `${hostlists}`»
в whitelist вставляется `--ipset-exclude` на каждый активный exclude-ipset (при пустом include-ipset проверка проходит,
`nfq/ipset.c:221-232`). UI обязан честно объяснять: сайт, исключённый по домену, всё равно может попасть под обход по
IP-подписке — чтобы исключить его полностью, добавьте его адреса в «Исключения IP».

Активный элемент-подписка `src-<name>`, который ещё ни разу не скачан, **пропускается** с предупреждением
`source_not_downloaded` (для обычных элементов отсутствие — ошибка). Профили без hostlist/ipset в стратегии
(действуют на весь трафик своих портов) — предупреждение `profile_unfiltered` с номером профиля и портами.
Завершающий `--new` и пустые профили между `--new` выбрасываются.

---

## 6. Бэкенд (ucode)

### 6.1. Модули `/usr/share/ucode/zaprett/`

| Модуль | Ответственность |
|---|---|
| `util.uc` | чтение/запись файлов атомарно (tmp + rename), JSON, sha256 (через `sha256sum`), логирование в syslog (`logger -t zaprett`), выполнение команд массивом с захватом вывода |
| `config.uc` | чтение UCI с умолчаниями, валидация значений |
| `store.uc` | элементы: перечисление по типу, разрешение id (etc > bundle > user), чтение манифеста и содержимого, подсчёт строк, пользовательские листы и стратегии |
| `strategy.uc` | токенизация, нормализация, раскрытие плейсхолдеров, базовые опции, dry-run, порты (§5) |
| `nft.uc` | генерация nft-скрипта (§8), `nft -c -f`, применение, снятие, wanif по netifd (ubus `network.interface dump`) |
| `repo.uc` | загрузка индекса и манифестов (кэш `/var/run/zaprett/repo/`), разрешение зависимостей, установка с проверкой sha256 и свободного места, обновление, удаление с защитой зависимостей |
| `service.uc` | статус (pid движка через ubus `service list`, версия движка, предупреждения), управление через `/etc/init.d/zaprett` |
| `job.uc` | фоновые задачи: одна одновременно (flock), `job.json` + `job.log`, отмена |
| `tester.uc` | автоподбор стратегии (§10) |
| `offload.uc` | режим `flow_offload=auto`: сохранить и выключить `firewall.@defaults[0].flow_offloading(_hw)`, вернуть при выключении |

Ограничение ADR-001: только модули `fs`, `uci`, `ubus`, `math` (есть и в 24.10, и в 25.12). Нет `uloop`,
`uclient`, `log`, `socket`. Сеть — `uclient-fetch`; фоновые процессы — `start-stop-daemon -b`.

### 6.2. CLI `/usr/bin/zaprett`

Все команды принимают `--json` (вывод одним JSON-объектом в stdout, коды возврата те же). Без `--json` —
человекочитаемый русский текст. Ошибка в JSON: `{"ok": false, "error": "<код>", "message": "<текст ru>"}`.
Успех: `{"ok": true, ...}`. Аргументы-id проверяются regex `^[A-Za-z0-9._-]{1,96}$`.

| Команда | Результат (`--json`) |
|---|---|
| `status` | `{ok, enabled, running, pid, engine, engine_version, strategy:{id,name}, list_mode, lists:[id], exclude_lists:[id], ipsets:[id], exclude_ipsets:[id], nft_applied, wan:[dev], flow_offload:{fw4, mode}, warnings:[code], version}` |
| `start` / `stop` / `restart` | `{ok}` — через init-скрипт; `start` при `enabled=0` включает `enabled=1` |
| `enable` / `disable` | автозапуск + UCI `enabled` |
| `check` | `{ok, args:[...], ports:{tcp:[...], udp:[...]}, dry_run:{rc, output}}` — проверка текущей конфигурации без запуска |
| `gen-args` | (для init) args построчно в файл, путь в stdout |
| `fw apply` / `fw remove` / `fw show` | применить/снять таблицу / показать сгенерированный nft |
| `items [--type <type>]` | `{ok, items:[{id,type,name,version,author,description,source,file,entries,size,active,used_by:[id]}]}` |
| `list enable <id>` / `list disable <id>` | для листов и ipset: тип определяется по id; include/exclude — по типу элемента |
| `strategy set <id>` / `strategy show <id>` | выбрать (для текущего движка) / `{ok, id, text, args, ports}` |
| `strategy save <id>` (stdin) / `strategy delete <id>` | пользовательские стратегии (`user-` префикс обязателен) |
| `user get <id>` / `user set <id>` (stdin) | пользовательские листы (`user-hosts` и т.д.), валидация строк, лимит 1 МиБ |
| `mode <whitelist\|blacklist>` / `engine <nfqws\|nfqws2>` | UCI |
| `repo fetch` | (задача) загрузить индекс и манифесты в кэш |
| `repo list [--type]` | `{ok, fetched_at, items:[{id,type,name,version,author,description,installed_version,update_available,installed,supported,size}]}` из кэша |
| `repo install <id>...` / `repo remove <id>` / `repo upgrade [--all\|<id>...]` | (задачи) |
| `sources list` | `{ok, sources:[{name, enabled, title, type, url, interval_hours, last_update, entries, size, status, error, ram_mib, item_id}]}` |
| `sources update [<name>...]` | (задача) загрузить/обновить подписки (все включённые или указанные), нормализация и проверки из §4 |
| `sources save <name>` (JSON в stdin: `{title,type,url,interval_hours,min_entries,enabled}`) / `sources delete <name>` | управление подписками; имя секции `^[a-z0-9_]{1,32}$`, url только `https://` |
| `presets` | содержимое `presets.json` + признак, какие сервисы включены (по листам); поля `ram_total_mib` (ОЗУ роутера) и `recommended_tier` (`light` при < 200 МиБ, иначе `full`) |
| `wizard apply <service>...` | включить листы сервисов, режим whitelist, при необходимости установить из bundle |
| `test start [--strategies id,id] [--quick]` / `test status` / `test stop` / `test apply <id>` | (задача) автоподбор |
| `job status` / `job log [--tail N]` / `job cancel` | фоновые задачи |
| `diag` | текст диагностики: версии, uname, arch, пакеты, `nft list table inet zaprett`, статус, последние 100 строк syslog zaprett |
| `version` | `{ok, version, nfqws, nfqws2}` |

**Коды предупреждений (`warnings`) — полный закрытый список (v1.2).** Бэкенд не выдаёт других, UI объясняет каждый:
`no_active_lists`, `no_wan`, `flow_offload_enabled`, `nft_queue_missing`, `engine_missing`, `strategy_missing`,
`no_strategy`, `generate_failed`, `bad_config` (с `details.bad_options`), `config_was_invalid`, `list_missing`,
`source_not_downloaded`, `profile_unfiltered` (с номером профиля и портами), `wide_port_range`, `empty_profile_removed`,
`strategy_option_ignored`, `test_running`, `not_running`, `nft_not_applied`. Новый код — только с правкой контракта.
Поле «изменение применено перезапуском» во всех ответах называется `reloaded`. Ошибка фоновой задачи, поставленной попутно (например, `wizard apply` → обновление подписок), возвращается отдельным полем `job_error {code, message}`, а не в `warnings`.

«(задача)» — команда ставит фоновую задачу и сразу возвращает `{ok, job:{id,name}}`; с флагом `--foreground`
выполняется синхронно (для cron и SSH).

### 6.3. Фоновые задачи

`/var/run/zaprett/job.json`:
```json
{ "id": "1758100000-1234", "name": "repo-install|repo-upgrade|repo-fetch|test|autoupdate",
  "state": "running|done|failed|cancelled", "progress": 0, "message": "текст ru",
  "started": 1758100000, "finished": 0, "rc": 0, "result": {} }
```
Одна задача одновременно (flock `/var/lock/zaprett-job.lock`); вторая получает `{"ok":false,"error":"job_busy"}`.
Лог задачи — `/var/run/zaprett/job.log` (обрезать до 256 КиБ). Отмена — `job cancel` (kill pid задачи,
state=cancelled, для `test` — восстановление исходной стратегии обязательно).

---

## 7. init / procd / триггеры

`/etc/init.d/zaprett`: `USE_PROCD=1`, `START=95`, `STOP=10`. На верхнем уровне файла — **ничего**, кроме
объявлений (Image Builder, research/02 §2).

- `start_service`: если `enabled!=1` — выйти; `zaprett gen-args` (ошибка → `logger`, выход ≠ 0, таблицу не
  ставить); `procd_open_instance engine`; `command /usr/libexec/zaprett/<engine> <args построчно>`;
  `respawn 3600 5 5`; `file /var/run/zaprett/args`; stdout/stderr в syslog; `procd_close_instance`;
  затем `zaprett fw apply` и `offload` (режим auto).
- `stop_service`: `zaprett fw remove`; вернуть offload (auto).
- `reload_service`: `start` (procd перезапустит инстанс, если изменился args по md5) + `fw apply`.
- `service_triggers`: `procd_add_reload_trigger zaprett`; `procd_add_interface_trigger "interface.*" <wan> /etc/init.d/zaprett reload` для каждого WAN (или hotplug-скрипт §3).
- Аргументы в procd передаются массивом: чтение `args` через `while IFS= read -r a; do set -- "$@" "$a"; done`.

fw4-интеграция: `uci-defaults` создаёт `firewall.zaprett=include`, `type=script`, `path=/usr/share/zaprett/fw4-include.sh`,
`fw4_compatible=1`. Скрипт: если сервис запущен и `/var/run/zaprett/zaprett.nft` существует — `nft -f` его
(идемпотентно). Удаление пакета (`prerm`) — удаляет include, таблицу, cron-строку.

Автообновление: строка в `/etc/crontabs/root` с меткой `# zaprett-autoupdate`: `<M> <autoupdate_hour> * * * zaprett repo upgrade --all --foreground --quiet`,
минута случайная при установке; `/etc/init.d/cron restart`.

---

## 8. nftables

Скрипт `/var/run/zaprett/zaprett.nft`, применяется одним `nft -f` после `nft -c -f`:

```nft
table inet zaprett
delete table inet zaprett
table inet zaprett {
	set wanif  { type ifname; elements = { "pppoe-wan" } }
	set wanif6 { type ifname; elements = { "pppoe-wan" } }       # только при ipv6=1
	set nozaprett { type ipv4_addr; flags interval; auto-merge;
		elements = { 0.0.0.0/8, 10.0.0.0/8, 100.64.0.0/10, 127.0.0.0/8, 169.254.0.0/16, 172.16.0.0/12, 192.168.0.0/16, 224.0.0.0/3 } }
	set nozaprett6 { type ipv6_addr; flags interval; auto-merge; elements = { ::1/128, fc00::/7, fe80::/10, ff00::/8 } }
	set clients4 { type ipv4_addr; flags interval; }              # только при clients_mode != all
	set clientsmac { type ether_addr; }

	chain clients_mark {                                          # только при clients_mode != all
		type filter hook prerouting priority -150; policy accept;
		iifname != @wanif ip saddr @clients4 ct mark set ct mark or 0x08000000
		iifname != @wanif ether saddr @clientsmac ct mark set ct mark or 0x08000000
	}

	chain postnat_hook {
		type filter hook postrouting priority 101; policy accept;
		meta mark and 0x40000000 == 0 jump postnat
	}
	chain postnat {
		# на каждый протокол с непустым набором портов; <CLIENTS> = "" | "ct mark and 0x08000000 != 0" (include) | "ct mark and 0x08000000 == 0" (exclude)
		oifname @wanif tcp dport { 80, 443 } ct original packets 1-9 ip daddr != @nozaprett <CLIENTS> meta mark set meta mark or 0x20000000 ct mark set ct mark or 0x40000000 queue num 200 bypass
		oifname @wanif udp dport { 443 } ct original packets 1-9 ip daddr != @nozaprett <CLIENTS> meta mark set meta mark or 0x20000000 ct mark set ct mark or 0x40000000 queue num 200 bypass
		# + ip6 варианты при ipv6=1
	}
	chain prenat {
		type filter hook prerouting priority -101; policy accept;
		iifname @wanif tcp sport { 80, 443 } ct reply packets 1-3 ip saddr != @nozaprett queue num 200 bypass
		# udp — только если udp_pkt_in > 0
	}
	chain prerouting_icmp {
		type filter hook prerouting priority -99; policy accept;
		icmp type time-exceeded ct state invalid drop
		icmp type time-exceeded ct mark and 0x40000000 != 0 drop
	}
	chain predefrag {
		type filter hook output priority -401; policy accept;
		meta mark and 0x40000000 != 0 jump predefrag_nfqws
	}
	chain predefrag_nfqws {
		meta mark and 0x20000000 != 0 notrack
		ip frag-off & 0x1fff != 0 notrack
		exthdr frag exists notrack
		tcp flags ! syn,rst,ack notrack
	}
}
```

- Числа (метки, qnum, лимиты пакетов, порты) — из UCI и §5.6. `ct original packets 1-N`: при N=0 правило не
  ставится. Порты ≥ 32 отдельных элементов или диапазоны — допустимы в анонимном наборе `{ }`.
- `wanif`: устройства (`l3_device`) логических интерфейсов из `wan` или всех с маршрутом `0.0.0.0/0` (ubus
  `network.interface dump`). Пустой набор → таблица ставится, но с предупреждением `no_wan`.
- Router-local трафик проходит `postrouting` — автоподбор с самого роутера использует те же правила.
- При `clients_mode=include` локальный трафик роутера не маркируется → не обрабатывается; **во время автоподбора**
  фильтр клиентов временно не применяется (генератор получает флаг `test_mode`).

---

## 9. Пресеты `/usr/share/zaprett/presets.json`

```json
{
  "schema": 1,
  "services": [
    { "id": "youtube", "name": "YouTube", "description": "...",
      "lists": ["zaprett-youtube"], "ipsets": [], "sources": [],
      "tier": "light",
      "test_targets": [ { "url": "https://www.youtube.com/", "min_bytes": 131072 },
                        { "url": "https://redirector.googlevideo.com/report_mapping?di=no", "min_bytes": 0 } ],
      "works": "yes|partial|no",
      "note": "текст ru: что помогает, чего ждать" }
  ],
  "always": { "exclude_lists": ["zaprett-exclude"], "exclude_ipsets": ["zaprett-exclude-ipset"] },
  "tiers": { "light": { "min_ram_mib": 0 }, "full": { "min_ram_mib": 200 } },
  "defaults": { "services": ["youtube", "discord"], "strategy": "strategy-general",
                "quick_test_strategies": ["strategy-general", "..."] }
}
```

`sources` — имена секций подписок (§4), которые мастер включает для сервиса и сразу ставит задачу `sources update`.
`works=no` (гео-блок сервиса, блок по IP) — показывать пользователю честно, мастер такой сервис не включает.
Наполнение — по `research/03-lists.md`. Все `lists`/`ipsets` пресетов обязаны существовать в bundle.
Цели проверки — справочные размеры ответов на 2026-09-17: `www.youtube.com/` ≈ 925 КБ, `discord.com/` ≈ 170 КБ,
`telegram.org/` ≈ 20 КБ, `speed.cloudflare.com/__down?bytes=102400` = 102 400 Б, `rutracker.org/forum/index.php` ≈ 96 КБ,
`i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg` ≈ 21 КБ; `redirector.googlevideo.com/report_mapping?di=no` ≈ 38–98 Б (зависит от клиента) и
`gateway.discord.gg/` (HTTP 404) — проверка только рукопожатия (`min_bytes 0`, ответ сервера = доступно).
`min_bytes` для «заморозки 16 КБ» ставить > 16384 и < фактического размера.

---

## 10. Автоподбор стратегии (tester)

1. Цели: `test_targets` всех сервисов, чьи листы активны, + до `max_domains` доменов из активных include-листов
   (без масок, дедуп), для доменов — `https://<domain>/` с `min_bytes=0`.
2. **Базовый прогон** без обхода (таблица снята, движок остановлен): запомнить, какие цели недоступны. Если всё
   доступно — результат `no_blocking_detected` (сравнивать нечего, но прогон стратегий разрешён).
3. Для каждой стратегии-кандидата (по умолчанию все установленные для текущего движка; `--quick` — из
   `presets.defaults` + top-10 по порядку bundle): записать override в `/var/run/zaprett/test-override`
   (генератор его учитывает вместо UCI-стратегии), `gen-args` + dry-run (ошибка → статус `invalid`),
   перезапуск инстанса движка, пауза `settle`, проверка целей параллельно (`concurrency`), запись результата.
4. Проверка цели: `uclient-fetch -q -T <timeout> -O <tmp> <url>`. Успех = rc 0 и размер ≥ `min_bytes`; а также
   HTTP-ответ с ошибкой статуса (сервер ответил) при `min_bytes=0`. Классификация по stderr (`HTTP error` →
   ответ получен; `Connection reset`, `timed out`, `SSL`, `Failed to` → недоступно) — формулировки сверять с
   исходником uclient-fetch.
5. Итог: `/var/run/zaprett/test-results.json`: `{started, finished, baseline:{ok,total,targets:[...]}, results:[{id,name,ok,total,ratio,avg_ms,status,targets:[{url,ok,ms,bytes,error}]}]}`,
   сортировка по ratio ↓, затем avg_ms ↑.
6. По завершении/отмене: вернуть исходную стратегию и перезапустить движок (или не запускать, если сервис был
   остановлен). `test apply <id>` — записать выбранную в UCI и перезапустить.

---

## 11. LuCI (`admin/services/zaprett`)

Бэкенд: rpcd ucode-плагин `/usr/share/rpcd/ucode/luci.zaprett` — объект `luci.zaprett`, методы вызывают CLI
`/usr/bin/zaprett ... --json` массивом аргументов и возвращают распарсенный JSON. Никакого generic exec в ACL.

| Метод | Аргументы | CLI |
|---|---|---|
| `status` | — | `status` |
| `service` | `action` ∈ start,stop,restart,enable,disable | соответствующая |
| `items` | `type?` | `items` |
| `toggle_item` | `id`, `enabled` | `list enable/disable` |
| `set_strategy` | `id` | `strategy set` |
| `strategy_show` | `id` | `strategy show` |
| `strategy_save` | `id`, `text` | `strategy save` (stdin) |
| `strategy_delete` | `id` | `strategy delete` |
| `user_get` / `user_set` | `id` / `id`,`text` | `user get/set` |
| `check` | — | `check` |
| `repo_fetch` / `repo_list` / `repo_install` / `repo_remove` / `repo_upgrade` | `type?` / `ids[]` / `id` / `ids[]?` | `repo …` |
| `presets` / `wizard_apply` | — / `services[]` | `presets` / `wizard apply` |
| `sources_list` / `sources_update` / `source_save` / `source_delete` | — / `names[]?` / `name`,`title`,`type`,`url`,`interval_hours`,`min_entries`,`enabled` / `name` | `sources …` |
| `test_start` / `test_status` / `test_stop` / `test_apply` | `strategies[]?`,`quick?` / — / — / `id` | `test …` |
| `job_status` / `job_log` / `job_cancel` | — / `tail?` / — | `job …` |
| `diag` | — | `diag` |

**Ограничения платформы (выяснено при разработке LuCI):** LuCI не принимает запрос больше 100 КиБ → из веба свой
лист сохраняется до ~99 КиБ, стратегия до 64 КиБ (CLI через SSH — до 1 МиБ). rpcd выполняет вызовы ucode-плагина
**синхронно**: любой метод, который ждёт (например, отмена задачи), подвешивает весь LuCI → методы только ставят
задачу/флаг и сразу возвращаются (`job cancel` → `{ok:true,state:"cancelling"}`). Имя поля «сервис перезапущен» во
всех ответах — `reloaded`.

ACL `luci-app-zaprett`: read — `status, items, strategy_show, user_get, check, repo_list, presets, sources_list, test_status, job_status, job_log, diag` + `uci: zaprett`; write — остальные + `uci: zaprett`.

Страницы (JS-views `zaprett/*.js`, меню «Службы → zaprett»):
1. **Обзор** — состояние, движок и стратегия, кнопки Запустить/Остановить/Перезапустить, автозапуск,
   предупреждения человеческим языком; «Быстрая настройка» (выбор сервисов из пресетов → включить листы →
   предложить автоподбор).
2. **Стратегии** — список для текущего движка (текущая выделена), выбор, просмотр, свои стратегии (редактор),
   автоподбор с прогрессом и таблицей результатов, «Применить».
3. **Списки** — вкладки Домены (включить/исключить), IP-сети и **Подписки** (внешние листы по URL: включить,
   обновить сейчас, дата/число записей/ошибка, оценка RAM с предупреждением, добавить свою по https-ссылке);
   переключатели, число записей, источник/версия; редактор своих доменов/сетей; режим whitelist/blacklist
   (с пояснением: исключения действуют в обоих режимах).
4. **Репозиторий** — каталог по типам, установить/обновить/удалить, «Обновить всё», дата загрузки индекса.
5. **Настройки** — `form.Map('zaprett')`: движок, WAN, IPv6, клиенты LAN, offload, автообновление, расширенные
   (qnum, метки, лимиты пакетов, пользователь, отладка), адрес репозитория.
6. **Диагностика** — `diag`, журнал задачи, кнопка «Скопировать».

Правила UI: весь пользовательский текст через `_()` (msgid на английском, полный перевод в `po/ru/zaprett.po`);
вывод данных только через `E()`/`dom.content` **с массивом или текстовым узлом** (строку LuCI вставляет через innerHTML); долгие операции — через задачи и
`poll.add`; совместимость с LuCI 24.10 (не использовать API, появившиеся только в 25.12).

---

## 12. Сборка и выпуск

- SDK-образы (docker): `openwrt/sdk:x86_64-v25.12.5` (apk) и `openwrt/sdk:x86_64-v24.10.8` (ipk).
- noarch-пакеты собираются штатно в этих SDK.
- Архитектурные `zaprett-nfqws*`: Makefile с `PKGARCH:=$(ZAPRETT_ARCH)` и выбором каталога бинарника по нему,
  отключённым strip; сборка в x86_64-SDK с `ZAPRETT_ARCH=<arch>` по списку архитектур. Метаданные результата
  (`arch`) обязательно проверять.
- Фид: `dist/<series>/<arch>/` = noarch + arch-пакеты + индекс (`packages.adb` подписанный / `Packages` + `Packages.sig`),
  приватные ключи подписи хранятся вне репозитория, публичные ключи идут в релиз и в бандл.
- Релизный бандл на arch: `zaprett-<ver>-<series>-<arch>.tar.gz` с фидом, ключом и `install.sh`
  (определяет apk/opkg и arch, ставит ключ, ставит пакеты + `luci-i18n-zaprett-ru`).
- Список архитектур (минимум): `x86_64, aarch64_cortex-a53, aarch64_cortex-a72, aarch64_cortex-a76, aarch64_generic,
  arm_cortex-a7_neon-vfpv4, arm_cortex-a7, arm_cortex-a9, arm_cortex-a9_vfpv3-d16, arm_cortex-a15_neon-vfpv4,
  arm_cortex-a5_vfpv4, arm_arm1176jzf-s_vfp, mipsel_24kc, mipsel_74kc, mips_24kc, mips_4kec, i386_pentium4, riscv64_generic (только nfqws2)`
  — сверить с реальным списком каталогов `downloads.openwrt.org/releases/<ver>/packages/`.

---

## 13. Тестирование

Выполняется на двух тестовых роутерах (OpenWrt 25.12 и 24.10) и клиенте за одним из них: установка,
работа службы, живучесть правил, веб-интерфейс, реальный трафик через провайдера, удаление. К каждому
ключевому пункту обязателен отрицательный контроль, который должен упасть. Результаты и список
непроверенного — `../tests/RESULTS.md`.
