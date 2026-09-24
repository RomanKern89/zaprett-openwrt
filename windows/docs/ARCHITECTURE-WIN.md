# zaprett для Windows — архитектура и контракт компонентов

> Версия 0.2 (2026-09-23): решения спайка M1 — §12, §12.1. Версия 0.1 (2026-09-23). Основа — `research/04-windows-app-plan.md` и контракт роутерной версии
> `docs/ARCHITECTURE.md` (v1.7). Это **контракт** между частями Windows-приложения: ядро, служба, IPC, CLI,
> интерфейс, установщик. Меняя интерфейс между частями — меняй этот файл и поднимай версию.

## 1. Цель и границы

Приложение для Windows 10 2004+ (сборка 19041+, в том числе LTSC 2021) и Windows 11 (x64), которое на этом ПК делает то же, что zaprett на роутере:
обход DPI движком zapret (`winws.exe` + WinDivert) по стратегиям и спискам, с мастером, живой проверкой, автоподбором,
монитором, сторожем и диагностикой. Поставка — один MSI (per-machine), служба Windows, интерфейс без прав администратора.

Не входит в 1.0: ARM64, Windows 7/8.1, фильтр по программам (WinDivert на сетевом уровне не видит процесс),
обход для раздаваемого трафика (winws не обрабатывает проходящий трафик).

## 2. Решения (ADR-W)

| № | Решение |
|---|---|
| W-1 | Язык всего кода — C# .NET 10 (LTS до 2028-11). Ядро `Zaprett.Core` — `net10.0` без зависимостей от Windows API. |
| W-2 | Служба `zaprett` (LocalSystem, автозапуск с задержкой) владеет движком и данными; интерфейс и CLI — клиенты по именованному каналу. |
| W-3 | Интерфейс — WinUI 3 (Windows App SDK, unpackaged, self-contained). Если спайк M1 покажет, что WinUI 3 не собирается надёжно `dotnet build` без Visual Studio, — WPF + WPF-UI (Fluent); решение фиксируется в §12. |
| W-4 | Установщик — WiX Toolset v7 (SDK-style `.wixproj`), MSI per-machine, MajorUpgrade. |
| W-5 | Данные (стратегии, фейки, наши списки, `presets.json`) — те же файлы, что `packages/zaprett/files/usr/share/zaprett/bundle` и `presets.json`, копируются при сборке; один источник правды с роутером. |
| W-6 | Движок — `winws.exe` (zapret v72.13, `binaries/windows-x86_64`) и `winws2.exe` (zapret2 1.0.5.2, zapret-win-bundle); версии и sha256 — в `windows/engine/engine.lock.json`; файлы не переподписываются. |
| W-7 | Подпись на старте: без Authenticode; SHA256SUMS + манифест обновлений, подписанный ed25519 (открытый ключ зашит в приложение). |
| W-8 | Без телеметрии. Журнал и диагностический отчёт без секретов (URL подписок маскируются по query). |

## 3. Раскладка на ПК

```
C:\Program Files\zaprett\                 (запись — только Administrators/SYSTEM)
  zaprett-svc.exe                         служба
  zaprett.exe                             CLI
  ui\zaprett-ui.exe (+ runtime WinAppSDK)  интерфейс
  engine\winws.exe, cygwin1.dll, WinDivert.dll, WinDivert64.sys
  engine2\winws2.exe (+ lua\*.lua)         движок zapret2 (необязательный компонент MSI)
  bundle\ (files\, manifests\), presets.json
C:\ProgramData\zaprett\                    ACL: SYSTEM, Administrators — полный; Users — чтение
  config.json                             настройки (§5)
  user\hosts-include.txt, hosts-exclude.txt, ipset-include.txt, ipset-exclude.txt, strategies\<engine>\<id>.txt
  installed\manifests\…, installed\files\… элементы из репозитория/подписок (как /etc/zaprett на роутере)
  run\                                     рантайм: args, probe.json, monitor.json, test-results.json, job.json, job.log
  logs\zaprett.log                         журнал службы (ротация 5 × 1 МиБ)
```

Пути в стратегиях: `${hostlists}`/`${ipsets}`/`${bin:id}`/`${lua_lib:id}` раскрываются в абсолютные Windows-пути
(обратные слеши, в кавычках не нуждаются — argv передаётся массивом через `CreateProcess` с корректным экранированием).
Путь установки с не-ASCII символами допустим только если спайк M1 подтвердит, что cygwin winws его читает; иначе
MSI ставит в `C:\Program Files\zaprett` без выбора каталога.

## 4. Генератор аргументов (порт `strategy.uc`)

Алгоритм — §5 роутерного контракта без изменений: токенизация, удаление `--comment`, нормализация устаревших режимов,
плейсхолдеры (включая защиту от пустых списков guard-файлами, исключения в обоих режимах, границу исключений v1.2),
удаление пустых профилей, извлечение портов. Отличия только в базовых опциях и перехвате:

- базовые опции winws: `--wf-l3=ipv4[,ipv6]` (по `ipv6`), `--wf-tcp=<порты TCP>`, `--wf-udp=<порты UDP>` (порты — из
  шага 6 алгоритма), при `debug` — `--debug=1`: stdout движка служба пишет буферизованно в `<run>\engine-debug.log` (у test — `engine-debug-test.log`); НЕТ `--qnum`, `--user`, `--dpi-desync-fwmark`;
- winws2: опций `--wf-tcp`/`--wf-udp` у него НЕТ (только `--wf-tcp-in/-out`, `--wf-udp-in/-out`, `--wf-tcp-empty`) —
  генератор пишет `--wf-tcp-out`/`--wf-udp-out` (находка wincore по `nfq2/nfqws.c`; живым winws2 не проверено), плюс
  `--lua-init=@<engine2>\lua\zapret-lib.lua`, `zapret-antidpi.lua`, `zapret-auto.lua`;
- каждая опция стратегии перед проверками приводится к полному имени по таблице опций движка
  (`Strategy/EngineOptions.cs`); неизвестная или неоднозначная — ошибка `bad_option`;
- проверка аргументов: `winws --dry-run` (winws2 — `--intercept=0`), от имени службы;
- **conformance:** для набора эталонных пар «конфигурация + стратегия → argv» (генерируются роутерным генератором,
  лежат в `windows/tests/conformance/*.json`) C#-генератор обязан давать тот же argv после удаления базовых опций
  роутера и замены путей `/usr/share/zaprett/…` → `<bundle>\…`, `/etc/zaprett/…` → `<ProgramData>\…`.

## 5. Конфигурация `config.json`

Ключи — как в UCI роутера (§4, §14.1, §15 контракта), без роутерных: нет `qnum`, `desync_mark`, `postnat_mark`,
`user`, `wan`, `flow_offload`, `clients_mode`, `clients`, `tcp_pkt_*`, `udp_pkt_*`.

```json
{
  "schema": 1,
  "main": { "enabled": false, "autostart": false, "engine": "winws", "strategy": "strategy-general", "strategy_winws2": "",
            "list_mode": "whitelist", "lists": ["zaprett-youtube","zaprett-discord","user-hosts"],
            "exclude_lists": ["zaprett-exclude","user-hosts-exclude"], "ipsets": [],
            "exclude_ipsets": ["zaprett-exclude-ipset","user-ipset-exclude"], "ipv6": false, "debug": false,
            "watchdog": true, "quic_block": false, "game_filter": false,
            "game_ports_tcp": "1024-65535", "game_ports_udp": "1024-65535",
            "network_filter": { "mode": "all", "ssids": [], "skip_corporate": false } },
  "repo":    { "url": "https://raw.githubusercontent.com/CherretGit/zaprett-repo/refs/heads/main/index.json",
               "autoupdate": true, "autoupdate_hour": 4 },
  "test":    { "timeout": 5, "concurrency": 6, "max_domains": 20, "settle": 2 },
  "monitor": { "enabled": true, "interval": 30, "threshold": 3, "auto_repair": false, "max_targets": 5, "timeout": 8 },
  "dns":     { "mode": "system" },
  "sources": { "<name>": { "enabled": false, "name": "…", "type": "list", "url": "https://…", "interval_hours": 72,
                            "min_entries": 1000, "min_valid_ratio": 0.99, "ram_mib": 9 } },
  "update":  { "channel": "stable", "check": true }
}
```

`engine` ∈ `winws`, `winws2`. `network_filter.mode` ∈ `all` (везде), `ssids` (только в сетях Wi-Fi из списка →
`--ssid-filter`), и `skip_corporate` → `--nlm-filter` (не работать в доменной/корпоративной сети). `dns.mode` ∈ `system`,
`doh` (Windows 11 — штатный DoH на активных адаптерах; Windows 10 — локальный DoH-прокси, если установлен компонент).
Запись конфигурации — только службой, атомарно (tmp + `File.Replace`), с резервной копией `config.json.bak`.

`main.enabled` — обход включён сейчас: по нему работают сторож и монитор. `main.autostart` — включать обход при старте
Windows: при первом запуске службы после загрузки ОС (`StartupAsync(osBoot: true)`) `enabled` приводится к `autostart`
и снимается пометка `stop`, всё остальное не трогается; при перезапуске только службы (обновление, сбой) `enabled` и
пометка остаются как были. Загрузку ОС определяет служба (волатильный ключ реестра). В старом
config.json без поля `autostart` оно равно `enabled` (поведение прежнее). Методы:
- `autostart {enable}` меняет только `autostart`, движок не трогает;
- `start` включает сейчас (`enabled=true`), `stop` останавливает сейчас; `autostart` оба не меняют;
- `enable` / `disable` (CLI, установщик `AUTOSTART=1`) — прежний смысл: `enabled` и `autostart` вместе, `disable` ещё и
  останавливает движок;
- `wizard.apply {autostart}` — ставит `autostart`, если аргумент передан.

`status.autostart` = `main.autostart`.

## 6. IPC

- Канал `\\.\pipe\zaprett`, DACL: `SYSTEM` и `Administrators` — полный доступ; `Interactive Users` (S-1-5-4) — чтение
  и запись в канал; **права на изменяющие методы проверяются службой по токену клиента**: состояние и чтение — любой
  интерактивный пользователь; изменение настроек и управление — член `Administrators` или локальной группы
  `zaprett Operators` (создаёт MSI; первый пользователь, ставящий MSI, добавляется в неё).
- Протокол — JSON-RPC 2.0, одно сообщение = UTF-8 JSON с префиксом длины (uint32 LE), максимум 4 МиБ.
- **Методы = команды CLI роутерного контракта** (§6.2, §14.3, §15.3–15.4, §16.4, §17) с теми же JSON-ответами
  (`{ok, …}` / `{ok:false, error, message}`), кроме роутерных (`fw *`, `offload`, `cron`, `gen-args`). Имя метода —
  команда через точку: `status`, `start`, `stop`, `restart`, `enable`, `disable`, `autostart` (новый, §5), `check`, `items`, `list.enable`,
  `list.disable`, `strategy.set`, `strategy.show`, `strategy.save`, `strategy.delete`, `user.get`, `user.set`, `mode`,
  `engine`, `repo.fetch`, `repo.list`, `repo.install`, `repo.remove`, `repo.upgrade`, `sources.list`, `sources.update`,
  `sources.save`, `sources.delete`, `presets`, `wizard.apply`, `test.start`, `test.status`, `test.stop`, `test.apply`,
  `job.status`, `job.log`, `job.cancel`, `probe`, `probe.status`, `monitor.status`, `diagnose`, `diagnose.status`,
  `dns.status`, `dns.setup`, `log`, `diag`, `version`, `page`, `conflicts` (новый, §8), `settings.get`, `settings.set`
  (частичное обновление config.json с валидацией), `update.check`, `update.install`.
- `status` дополнительно: `platform: {os, build, arch, hvci, smart_app_control, defender}`, `windivert: {loaded, version,
  foreign: [имена чужих служб WinDivert]}`; поля роутера `wan`, `flow_offload`, `queue` отсутствуют, вместо `queue` —
  `engine_stats: {pid, uptime_s}`.
- События: метод `subscribe` открывает поток уведомлений `{"method":"event","params":{"type":"status|job|probe|monitor","data":{…}}}`
  — интерфейс не опрашивает службу чаще раза в 30 с, если подписка работает.

## 7. Движок и задачи

- Движок — дочерний процесс службы в Job Object (`KILL_ON_JOB_CLOSE`), stdout/stderr → журнал; перезапуск при падении
  с паузой 5 с, после 5 падений за 10 минут — пауза 5 минут и событие; сторож раз в 5 минут (как `ensure`).
- Задачи — одна одновременно (`job.*` как §6.3 роутера), в памяти службы + `run\job.json`.
- Автоподбор: режим `isolated` (§17 роутера), если спайк M1 подтвердит способ изоляции проверочного трафика (привязка
  проверочных соединений к диапазону локальных портов + `--wf-raw` у кандидата, исключение диапазона у основного);
  иначе `exclusive` с понятным предупреждением. Поля `mode`/`mode_reason` — как на роутере.
- Проверки доступности — `HttpClient` (`SocketsHttpHandler`), классификация ошибок — коды роутера (`timeout`, `reset`,
  `tls_cert`, `tls_error`, `connect_failed`, `http_error`, `too_small`, `local_error`, `failed`).

## 8. Конфликты

`conflicts` → `{ok, items:[{id, name, severity: "block"|"warn"|"info", detail, fix}]}`. Проверяются: службы `WinDivert*`
и `GoodbyeDPI`, `zapret`/`winws` чужой установки, процессы и службы Adguard, Killer Network, Intel Connectivity Network
Service, Check Point, SmartByte, активные VPN-адаптеры (по типу/описанию), системный прокси, путь установки с не-ASCII.

## 9. Интерфейс

Экраны и поведение — `research/04-windows-app-plan.md` §4 (мастер, главная, сервисы, стратегии, списки, диагностика,
настройки, трей). Тексты — из переводов LuCI, где совпадает смысл (`packages/luci-app-zaprett/po/ru/zaprett.po`),
языки ru/en по языку системы с переключателем. Все данные с диска/службы выводятся как текст (без HTML/XAML-разметки).

## 10. MSI

`research/04-windows-app-plan.md` §7. Компоненты: Core (обязательный: служба, CLI, UI, engine, bundle), Engine2 (winws2),
DohProxy (Windows 10). Свойства: `AUTOSTART`, `SERVICES` (через запятую, как `wizard.apply`), `LANG` (ru|en|zh-CN), `INSTALLFOLDER`
(выбор каталога разрешён — §12.1; обновление остаётся в прежнем каталоге по `HKLM\SOFTWARE\zaprett\InstallDir`).
Удаление: остановить службу и движок, `sc stop/delete WinDivert` только если драйвер загружен нашей копией (путь
`ImagePath` в нашем каталоге), удалить правила брандмауэра zaprett, вернуть DNS; флажок `REMOVEDATA=1` — удалить ProgramData.

## 11. Тесты и приёмка

- Unit (xUnit): ядро ≥ 80 %, conformance, IPC-протокол, валидация, парсер winws-вывода.
- Интеграция на тестовых машинах Windows 11 24H2 и Windows 10 LTSC 2021 (19044): чистая установка → мастер → служба → движок → живая проверка → автоподбор → удаление.
- Отрицательный контроль в каждом наборе; итог — `windows/tests/RESULTS.md` с непроверенным.

## 12. Журнал решений спайка M1

Спайк выполнен на Windows 11 24H2 (2026-09-23); подробный отчёт с командами — во внутренних материалах проекта, ниже — принятые решения.

| № | Решение | Основание (SPIKE-M1) |
|---|---|---|
| S-1 | Проверка аргументов стратегии — `winws --dry-run` (rc 0/1, ошибки в stdout). `--dry-run` НЕ компилирует фильтр WinDivert: служба строит `--wf-raw` только из шаблонов S-5 и после старта ждёт в stdout `windivert initialized` (0,1–0,85 с), иначе — ошибка запуска. | §1, §2, §5 |
| S-2 | Пути: пробелы и не-ASCII в пути установки/данных допустимы (winws v72.13 читает списки, фейки, `--debug=@`, грузит драйвер из `…\тест папка\`, `Program Files`). Ограничение §3 «без выбора каталога из-за cygwin» снимается; аргументы — только массивом (`ArgumentList`). | §3 |
| S-3 | `debug` движка — только временно (N минут). `--debug=@file` НЕ используем: winws открывает и закрывает файл на каждую строку — TLS-рукопожатие 1,5–2 с против 0,2–0,4 с без отладки и 0,4–0,5 с с `--debug=1` (замер 2026-09-23, Win10 19044; исключение Defender почти не влияет), первый пакет может не уложиться в очередь WinDivert. Отладка — `--debug=1` в stdout, файл пишет служба. Журнал читать с `FileShare.ReadWrite`. | §2 |
| S-4 | Движок под службой LocalSystem в Job Object `KILL_ON_JOB_CLOSE` подтверждён: остановка и аварийное завершение службы гасят winws, перезапуск по выходу работает. Назначение в job — до первого выполнения (`CREATE_SUSPENDED`/`PROC_THREAD_ATTRIBUTE_JOB_LIST`). | §4 |
| S-5 | Автоподбор `isolated` (TCP): основной экземпляр ВСЕГДА запускается с исключением портов проверки (автоподбор его не перезапускает); если динамический диапазон TCP Windows (WMI MSFT_NetTCPSetting + netsh) пересекается с `40000–40100` — исключение только на время теста. Проверки с портов `40000–40100` (вне динамического диапазона), при `AddressAlreadyInUse` на connect — следующий порт; ОБА экземпляра с `--wf-raw=@file` = `(<--wf-save --dry-run стратегии>) and <условие>`: основной — `(!tcp or (outbound and (tcp.SrcPort < LO or tcp.SrcPort > HI)) or (inbound and (tcp.DstPort < LO or tcp.DstPort > HI)))`, кандидат — `tcp and ((outbound and tcp.SrcPort >= LO and tcp.SrcPort <= HI) or (inbound and tcp.DstPort >= LO and tcp.DstPort <= HI))`. `--wf-raw-part` не годится (OR), `!(…)` в фильтре запрещён (WinDivert: «The parameter is incorrect»). UDP/QUIC-проверки не изолируются → для них `exclusive` или пропуск. | §5 |
| S-6 | `dns.setup` (Windows 11): серверы с известным шаблоном на адаптер + `…\Dnscache\InterfaceSpecificParameters\{guid}\DohInterfaceSettings\Doh\<ip>` `DohFlags`=1 (QWORD), без UDP-фолбэка; признак работы — 0 пакетов на 53 и TCP :443 службы Dnscache к резолверу. Перед изменением сохранять серверы адаптера (DHCP/статика), записи `DohWellKnownServers` (включая наличие значения `Flags`), подключи `Doh`; откат — ровно к сохранённому. | §6 |
| S-7 | W-3 подтверждено: WinUI 3, Windows App SDK **2.5.1**, unpackaged, self-contained, сборка `dotnet publish` без Visual Studio. Обязательно `EnableMsixTooling=true` (иначе в publish нет `<app>.pri`/`*.xbf` и падение 0xc000027b). Ссылаться на `Microsoft.WindowsAppSDK.WinUI` (+`DWrite`), не на метапакет: 173 МБ вместо 230 МБ. Интерфейс не запускается в сеансе 0 (0xc0000602) — только в сеансе пользователя. | §7 |
| S-8 | WiX v7: сборка с `-p:AcceptEula=wix7` (значение проверяется). `ServiceConfig DelayedAutoStart` + `util:ServiceConfig` применяются. Удаление: отложенный CA после `StopServices` (`Impersonate=no`, `Return=ignore`): если `ImagePath` службы `WinDivert` — наш `engine\WinDivert64.sys`, то `sc stop WinDivert` — драйвер выгружается и служба исчезает **без перезагрузки** (после завершения всех winws); чужой драйвер не трогать. | §8 |
| S-9 | Defender (signatures 1.459.343.0): 0 срабатываний на winws/WinDivert/MSI; исключений не добавлять. | §9 |
| S-10 | Размер: служба self-contained single-file 75,9 МБ (в MSI вместе с движком — 29,6 МБ), интерфейс 173 МБ (ZIP 65,5 МБ). Оценка MSI с двумя копиями среды .NET ≈ 90–95 МБ (сумма измеренных сжатых частей, MSI целиком не собирался) — больше цели плана «≤ 60 МБ»; нужно решение (общая среда для службы/CLI/UI, AOT или trim службы, trim UI после e2e). | §4, §7, §8 |

### 12.1. Решения координатора по итогам спайка (2026-09-23, v0.2 контракта)

- Диапазон локальных портов проверочного трафика автоподбора — **40000–40100** (проверен спайком; вне динамического
  диапазона Windows 49152–65535); `CoreOptions.TestLocalPortFrom/To` по умолчанию = 40000/40100.
- Изоляция по S-5 включается службой (`IsolationSupported=true`) только для TCP-целей; UDP/QUIC-цели в режиме isolated
  пропускаются с пометкой в результате. Служба строит `--wf-raw` обоим экземплярам только из шаблонов S-5 и считает
  экземпляр запущенным после `windivert initialized` в выводе (S-1).
- §3: выбор каталога установки разрешён (S-2), по умолчанию `C:\Program Files\zaprett`; пункт «путь с не-ASCII» из §8
  (конфликты) удалён. Вместо него конфликт `foreign_windivert`: служба `WinDivert` загружена, а её `ImagePath` не наш
  `engine\WinDivert64.sys` (severity `block`, совет: закрыть/удалить GoodbyeDPI, zapret и т.п.).
- §10: удаление выгружает драйвер без перезагрузки (S-8), текст «после перезагрузки» не использовать.
- Размер (S-10): служба, CLI и интерфейс публикуются self-contained **в один каталог** с общей средой .NET (одна копия
  runtime), интерфейс — на компонентных пакетах WinUI (S-7); цель MSI ≤ 100 МБ; trim интерфейса — только после e2e.
- DoH (S-6) — Windows 11 через `DohFlags` адаптера с полным сохранением и откатом; Windows 10 — `not_supported` в 1.0,
  DoH-прокси — отдельная задача после выпуска.

### 12.2. Языки (указание пользователя 2026-09-23)

Три языка: **ru (основной, по умолчанию)**, en, **zh-CN** (упрощённый китайский). Интерфейс, трей, уведомления, установщик
MSI (WixUI ru-RU/en-US/zh-CN, выбор языка по языку системы, иначе русский), CLI, тексты ошибок/предупреждений/задач.
Язык по умолчанию — русский независимо от языка системы; переключатель в настройках (`ui.language` ∈ `ru`, `en`, `zh-CN`
в `config.json` — чтобы служба и CLI говорили на том же языке). Ядро возвращает коды (`error`, `warnings`, вердикты) и
`message` на языке из аргумента `lang` вызова (любой метод принимает необязательный `lang`, иначе `ui.language`);
интерфейс показывает тексты по кодам из своих ресурсов, `message` — запасной вариант. Все строки — из ресурсов, без
склейки фраз; ширина макетов рассчитана на самый длинный язык (обычно русский), шрифт с китайскими глифами
(Microsoft YaHei UI / Segoe UI Variable с фолбэком).

## 13. Интерфейсы между частями (код)

- Ядро ↔ служба: `src/Zaprett.Core/Platform/IPlatform.cs` (`PlatformServices`: пути, часы, процессы, движок, HTTP-проверки,
  DNS, брандмауэр, конфликты, сведения о системе, журнал) и `src/Zaprett.Core/ICommandDispatcher.cs` (вход ядра:
  `InvokeAsync(method, args, caller)` → JSON-ответ роутерного формата). Ядро не вызывает Windows API напрямую.
- Служба ↔ интерфейс/CLI: `src/Zaprett.Ipc/IZaprettClient.cs` (`CallAsync`, `SubscribeAsync`); интерфейс
  разрабатывается против поддельной реализации этого интерфейса.
- Менять сигнатуры — только правкой этого раздела и сообщением координатору.

### 13.1. API ядра (зафиксировано wincore, 2026-09-23)

- `new CommandDispatcher(PlatformServices, CoreOptions?)`; после старта службы — `StartupAsync(osBoot, ct)` (`StartupAsync(ct)` = `osBoot: true`). `ct` — токен остановки службы: им
  завершается фоновая работа ядра (внеочередная проверка монитора после исчезновения конфликта).
- Внутренние методы для таймеров службы (с `CallerInfo.System`): `ensure` (5 мин), `monitor.run` (`monitor.interval`),
  `autoupdate` (раз в сутки, `repo.autoupdate_hour`).
- `CoreOptions { NetworkList, Updates, IsolationSupported=false, TestLocalPortFrom=41000, TestLocalPortTo=41999, Version }`;
  типы `INetworkList`, `IUpdateControl` — `Platform/IPlatformExtras.cs`.
- Изоляция автоподбора: ядро запускает инстанс `test` и шлёт проверки с `LocalPortFrom/To`; фильтр WinDivert
  (test — только диапазон, main — без него) навешивает служба.
- Аргументы методов — объект: `{id}`, `{text}`, `{ids}`/`{all}`, `{names}`, `{services}` (`id` или `id:variant`),
  `test.start {strategies, quick, apply_if_better, exclusive}`, `{brief}`, `{tail}`, `{full}`, `page {name}`,
  `update {channel}`, `dns.setup {enable}`, `autostart {enable}`, `wizard.apply {services, autostart?}`, у задачных — `{foreground}`; `settings.set` — частичный config.json (null удаляет).
- События: `status`, `job`, `probe`, `monitor`.
