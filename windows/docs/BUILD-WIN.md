# zaprett for Windows — сборка, выпуск, проверка

> Контракт компонентов — [ARCHITECTURE-WIN.md](ARCHITECTURE-WIN.md) (W-4, W-6, W-7, §3, §10). Здесь — как из
> исходников получить MSI, как выпустить релиз и как пользователю проверить скачанный файл.

## 1. Что нужно

| Что | Версия | Зачем |
|---|---|---|
| Windows 10/11 x64 | — | сборка MSI (WiX и Windows Installer COM есть только в Windows) |
| .NET SDK | по `windows/global.json` (10.0.401, `rollForward: latestFeature`) | решение, publish, WiX SDK |
| Windows PowerShell 5.1 или PowerShell 7 | — | `windows/build/*.ps1` |
| Интернет при первой сборке | — | NuGet (WiX 7.0.0, пакеты .NET, `Microsoft.NETFramework.ReferenceAssemblies` для `setup.exe`) и архивы движка |

Visual Studio не нужна: WiX v7 подключается как MSBuild SDK (`WixToolset.Sdk/7.0.0`) и скачивается NuGet'ом.
Если `dotnet` лежит не в `PATH`, путь к нему — в переменной `DOTNET_EXE`.

## 2. Сборка одной командой

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File windows/build/build.ps1
```

Шаги `build.ps1`:

1. `fetch-engine.ps1` — движок по `windows/engine/engine.lock.json` (раздел 3);
2. `dotnet test windows/Zaprett.slnx` (пропуск — `-SkipTests`);
3. self-contained publish `win-x64`: служба (`zaprett-svc.exe`), CLI (`zaprett.exe`) и интерфейс (`zaprett-ui.exe`)
   — в один каталог `app\` с одной общей средой .NET (ARCHITECTURE-WIN §12.1); файл, который у двух publish совпал
   по имени, но отличается содержимым, останавливает сборку;
4. данные — те же файлы, что у пакета роутера: `packages/zaprett/files/usr/share/zaprett/{bundle,guard,presets.json}`;
5. WiX: `windows/installer/Zaprett.Installer.wixproj`, по MSI на культуру (`ru-RU`, `en-US`) с одним ProductCode
   (выводится из версии — одинаковый при повторной сборке той же версии);
6. `msi-languages.ps1` — один многоязычный MSI: база `ru-RU` (1049) + встроенные языковые преобразования `1033`
   (en-US) и `2052` (zh-CN), Template `x64;1049,1033,2052` (ARCHITECTURE-WIN §12.2). Windows Installer
   выбирает встроенное преобразование по ФОРМАТУ региона пользователя (GetUserDefaultLangID), а не по языку
   интерфейса Windows. Проверено 2026-09-23: Win11 с интерфейсом ru-RU — формат ru-RU → русский, Set-Culture en-US →
   английский, обратно ru-RU → русский; Win10 с интерфейсом en-US и форматом ru-RU → русский. Для уже установленного
   продукта msiexec берёт язык из кэша прошлой установки. Явно — `TRANSFORMS=:1033` / `TRANSFORMS=:2052`
   (двоеточие = встроенное преобразование);
7. `build-setup.ps1 -Msi <msi>` — `zaprett-<версия>-x64-setup.exe`: тот же MSI внутри загрузчика, который просит
   права администратора сразу при запуске (раздел 4.1).

Результат — `windows/artifacts/dist/zaprett-<версия>-x64.msi`, `zaprett-<версия>-x64-setup.exe` и `SHA256SUMS`
с обоими файлами.

Параметры: `-Version X.Y.Z` (по умолчанию `<Version>` из `windows/Directory.Build.props`), `-Offline` (движок
только из кэша), `-SkipTests`, `-StubUi` (заглушка вместо интерфейса — только для проверки установщика; такой MSI
получает суффикс `-STUB-UI` и в релиз не попадает: `windows.yml` это проверяет), `-OutDir <каталог>`.

Сборка ВСЕГДА публикует службу, CLI и интерфейс заново из рабочего дерева `windows/` с `-p:Version=<версия>`
и `-p:PublishReadyToRun=true` (быстрее старт службы после загрузки; MSI ~66 МБ вместо ~55)
(повторного использования прошлого publish нет: из-за него тестовый MSI однажды получил чужие и устаревшие
бинарники) и сразу проверяет `build/check-versions.ps1`: у каждого `zaprett*.exe/dll` и `Zaprett.*.dll`
FileVersion = `X.Y.Z.0`, ProductVersion = `X.Y.Z` или `X.Y.Z+<commit>`, коммит у всех один, все такие .dll —
ReadyToRun (в CLR-заголовке есть ManagedNativeHeader) — иначе сборка
падает. В ProductVersion стоит коммит HEAD; незакоммиченные правки рабочего дерева в сборку тоже входят (строка
`sources:` в выводе показывает их число).

Всё, что порождает сборка, лежит в `windows/artifacts/` (в `.gitignore`): `cache\` (архивы движка), `engine\`,
`dist\` и рабочий каталог прогона `run-<время>-<pid>\` (`published\`, `stage\`, `wix\`; удаляется при успехе,
остаётся при ошибке). Одновременно идёт только одна сборка (`artifacts\build.lock`).

Воспроизводимость: publish идёт с `Deterministic`/`ContinuousIntegrationBuild`, движок и данные побайтно
закреплены. Сам MSI побайтно не воспроизводится (WiX пишет в него время сборки и новый PackageCode); сверять
надо содержимое, а не sha256 MSI.

## 3. Движок: `engine.lock.json` и `fetch-engine.ps1`

| Движок | Источник | Что берётся |
|---|---|---|
| `winws` v72.13 | `bol-van/zapret`, релиз `zapret-v72.13.zip` | `binaries/windows-x86_64/{winws.exe, cygwin1.dll, WinDivert.dll, WinDivert64.sys}` → `engine\` |
| `winws2` v1.0.5.2 | `bol-van/zapret2`, релиз `zapret2-v1.0.5.2.zip` | `binaries/windows-x86_64/{winws2.exe, cygwin1.dll, WinDivert.dll, WinDivert64.sys}` + `lua/*.lua` → `engine2\` |

`killall.exe`, `mdig.exe`, `ip2net.exe` не берутся никогда (лишнее для антивирусов, ARCHITECTURE-WIN W-6).

Три независимые сверки на каждый прогон:

1. sha256 и размер архива = lock (значение в lock взято из digest ассета релиза в GitHub API);
2. sha256 файла `sha256sum.txt` релиза = lock, и строка каждого бинарника в нём = sha256 этого бинарника в lock;
3. sha256 и размер каждого извлечённого файла = lock.

`winws2.exe`, `cygwin1.dll`, `WinDivert*` релиза zapret2 побайтно совпадают с `bol-van/zapret-win-bundle`
(коммит записан в lock, `cross_check`). Lua взяты из того же релиза (они же лежат в
`zapret2-v1.0.5.2-openwrt-embedded.tar.gz`, из которого собирается пакет роутера), а не из win-bundle: там
`zapret-lib.lua` новее релиза.

`fetch-engine.ps1 -SelfTest` — отрицательные контроли: подменённый байт архива, неверный sha256 файла в lock,
архив, перепакованный с изменённым `winws.exe` при «исправленном» sha256 архива, — все три обязаны упасть.
CI прогоняет их при каждой сборке.

**Обновление движка.** `engine-watch.yml` сообщает о новом релизе. Порядок: скачать архив и `sha256sum.txt`,
взять sha256 архива из digest ассета (`gh api repos/bol-van/zapret/releases/tags/<tag>`), пересчитать sha256
нужных файлов, сверить их со строками `sha256sum.txt`, вписать всё в lock, прогнать `fetch-engine.ps1 -SelfTest`
и сборку. Версия движка у роутера и у Windows меняется одним коммитом.

## 4. Что делает MSI

- Установка на машину (per-machine), каталог по умолчанию `C:\Program Files\zaprett`; другой — в окне установщика
  или `INSTALLFOLDER=...` (пробелы и не-ASCII допустимы, ARCHITECTURE-WIN §12.1); обновление остаётся в каталоге
  установленной версии (`HKLM\SOFTWARE\zaprett\InstallDir`). Windows 10 2004 (сборка 19041)+ x64, включая LTSC 2021 — иначе установка
  не начнётся. Размер MSI с настоящим интерфейсом WinUI — около 55 МБ (цель ≤ 100 МБ).
- Интерфейс установщика: приветствие → лицензия MIT → предупреждение о WinDivert и антивирусах (флажки значка в
  трее и ярлыка на рабочем столе) → каталог → установка. Страница «Готово к установке» подсказывает нажать «Да» в
  запросе Windows (или мигающий значок щита на панели задач), страница `UserExit` («прервано») — вероятную причину
  (запрос прав не подтверждён) и `zaprett-setup.exe` как выход. Языки: русский (основной), английский, китайский упрощённый. Страницы лицензии —
  `installer/License.<культура>.rtf`, генерируются `installer/license/make_license_rtf.py` (текст MIT — английский
  оригинал, пояснение и сторонние компоненты — на языке страницы).
- `LANG` (`ru` | `en` | `zh-CN`, по умолчанию — язык установщика: `ru`, а при `TRANSFORMS=:1033` / `:2052` — `en` / `zh-CN`) записывается как `ui.language` в
  `C:\ProgramData\zaprett\config.json`, только если файла ещё нет (первая установка); остальные ключи служба
  берёт по умолчанию. Недопустимое значение → `ru`. Существующий `config.json` (обновление) не меняется.
- Удаление перед остановкой службы вызывает `zaprett.exe dns off --quiet` (служба возвращает DNS адаптеров,
  если меняла его); при обновлении DNS не трогается.
- Зависимости ставить отдельно не нужно — всё внутри MSI: среда .NET 10 (self-contained: `coreclr.dll`,
  `hostfxr.dll`), Windows App SDK / WinUI 3 (self-contained, без пакетов `WindowsAppRuntime` из Store), движок
  `winws.exe` с `cygwin1.dll`, драйвер `WinDivert64.sys` + `WinDivert.dll`. Библиотеки VC++ (`vcruntime140`,
  `msvcp140`) не импортирует ни один модуль пакета (CRT вшит статически), поэтому Visual C++ Redistributable не
  нужен. Проверено 2026-09-23: сканер импортов по 294 PE-файлам `artifacts\stage` против System32 чистой
  Windows 10 LTSC 2021 (19044) — неразрешённых нет, кроме модулей ядра у драйвера; на той же чистой системе (без
  VC++, .NET и Windows App Runtime) установка из одного MSI → мастер → обход работает.
- Компоненты: `Core` (служба, CLI, интерфейс, `engine\`, данные — обязателен), `Engine2` (`engine2\`, ставится по
  умолчанию), `DohProxy` (заготовка, пустая, не ставится).
- Служба `zaprett` (LocalSystem, обычный автозапуск без задержки — обход работает сразу после входа; перезапуск
  при сбое через 5 с, сброс счётчика — сутки):
  запускается после установки, останавливается и удаляется при удалении.
- `C:\ProgramData\zaprett`: SYSTEM и Administrators — полный доступ, Users — чтение, наследование от ProgramData
  отключено (SDDL `D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)`).
- Локальная группа `zaprett Operators` (ARCHITECTURE-WIN §6) создаётся, устанавливающий пользователь (`UserSID`)
  добавляется в неё.
- Ярлык в меню «Пуск»; значок в трее при входе любого пользователя — `HKLM\...\Run\zaprett`
  (`"<каталог>\zaprett-ui.exe" --tray`). Это не компонент MSI: выбор пользователя хранится в
  `HKLM\SOFTWARE\zaprett\TrayAutostart` (REG_DWORD 1/0) и меняется в Настройках (через службу). Первая установка
  записывает выбор из флажка или `TRAYAUTOSTART=0`; обновление с 0.1.x, где выбора ещё нет, берёт его из наличия
  значения `Run\zaprett`; исправление и обновление выбор не меняют (флажок при обновлении скрыт, `TRAYAUTOSTART`
  игнорируется). После удаления старой версии `ZaprettApplyTray` приводит `Run\zaprett` к выбору, служба делает
  то же при каждом старте. Удаление стирает и значение `Run`, и выбор.
- Ярлык на рабочем столе всех пользователей (Public desktop): `DESKTOPSHORTCUT` = `1` (по умолчанию) | `0`, флажок
  на странице предупреждения. Выбор хранится в `HKLM\SOFTWARE\zaprett\DesktopShortcut` и запоминается как `AUTOSTART`
  (`SetDesktopShortcutFromSaved` после `AppSearch`, явное значение важнее); обновление с 0.1.0–0.1.2 (выбора нет)
  получает `1`. Флажок показывается и при обновлении и начинается с сохранённого выбора; снятый — компонент
  `UiDesktopShortcut` не ставится, и удаление старой версии уносит её ярлык. Флажок работает через
  `ZAPRETT_DESKTOP_CHK` (снятый флажок оставляет свойство пустым), «Далее» записывает в `DESKTOPSHORTCUT` 1 или 0.
- Параметры для службы: `HKLM\SOFTWARE\zaprett` — `InstallDir`, `Version`, `AutoStart`, `Services`, `TrayAutostart`
  (и `DesktopShortcut` — только для установщика).
  `AUTOSTART` и `SERVICES` запоминаются: исправление и обновление без них в командной строке берут значения
  установленной версии (`RegistrySearch` → `SetAutoStartFromSaved` / `SetServicesFromSaved` после `AppSearch`, в
  обеих последовательностях); явно заданное значение важнее; без того и другого `AUTOSTART=0`. Служба применяет их
  только при первом старте (`install-options.applied`), так что это лишь сохраняет в реестре правду.
- Удаление (настоящее, не при обновлении): остановить `winws.exe`/`winws2.exe` из нашего каталога (чужие не
  трогаются), выгрузить драйвер `WinDivert*` (`sc stop` — служба драйвера исчезает сама, без перезагрузки) **только**
  если её `ImagePath` указывает в наш каталог, удалить правила брандмауэра `zaprett QUIC block` и группы `zaprett`,
  удалить группу `zaprett Operators`; `REMOVEDATA=1` — удалить `C:\ProgramData\zaprett`. Эти шаги —
  `windows/installer/scripts/installer-actions.ps1` (вывод — в журнал MSI).
- Установка, исправление и обновление начинаются с `installer/scripts/prepare.ps1` (build.ps1 встраивает скрипт в
  MSI как `-EncodedCommand`, см. ниже про `EmbeddedScripts.wxs`): закрыть значок в трее, остановить службу, наши winws/winws2 и выгрузить НАШ драйвер
  WinDivert (чужой не трогается) — иначе занятый `engine\WinDivert64.sys` требовал перезагрузки (1903 / 3010).
  Действие `ZaprettPrepare` — ОТЛОЖЕННОЕ, от LocalSystem, сразу после `InstallInitialize`: при запуске двойным
  щелчком клиент msiexec не повышен, и немедленное действие получало отфильтрованный токен пользователя (не могло
  остановить службу). Поэтому старая версия удаляется ПОСЛЕ установки новой (`MajorUpgrade
  Schedule="afterInstallExecute"`; GUID компонентов стабильны, общие файлы остаются). Служба запускается снова в
  `StartServices`; при откате исправления или обновления — действием `ZaprettRestartService`. Удаление старой версии
  идёт уже после старта новой службы и выполняет действия СТАРОГО пакета: у 0.1.1 это `ZaprettPrepare`, который
  останавливал только что запущенную службу (D23). Поэтому с 0.1.2 `ZaprettPrepare` не выполняется ни при каком
  удалении (и при удалении этой версии более новой — `UPGRADINGPRODUCTCODE`), `ZaprettCloseUi` — тоже, а после
  `RemoveExistingProducts` при обновлении `ZaprettStartService` (`installer-actions.ps1 -Action StartService`,
  LocalSystem) запускает службу, если она не работает и тип запуска «Автоматически», и ждёт Running; значок
  возвращает служба по `run\ui-relaunch.json`. При настоящем удалении
  отложенное `ZaprettStopUi` (тоже LocalSystem) закрывает только значки (службе ещё нужно вернуть DNS). Restart Manager отключён (`MSIRESTARTMANAGERCONTROL=Disable`): при
  запущенном лишнем `winws.exe` он останавливал службу, и удаление завершалось кодом 1601.
- Без полного интерфейса (`/qn`, `/qb`, `/qr`) MSI сам ставит `REBOOT=ReallySuppress`: тихая установка никогда не
  перезагружает компьютер, в худшем случае код 3010. Явный `REBOOT=Force` (или другое значение) из командной строки
  имеет приоритет; в полном интерфейсе запрос на перезагрузку появляется, только если она действительно нужна.
- Первая установка: на последнем окне флажок «Запустить zaprett» (включён) — открывается окно zaprett (с мастером)
  от имени пользователя без повышения (если сам установщик запущен повышенным — через Explorer). Своя копия окна
  завершения WixUI: флажок 12×12 и прозрачная подпись, без серой полосы на белом фоне.
- Исправление и обновление (с интерфейсом и тихие): `prepare.ps1`, закрывая значок в трее, записывает
  `C:\ProgramData\zaprett\run\ui-relaunch.json` = `{"version":1,"sessions":[<SessionId>…]}`; служба при старте
  запускает `zaprett-ui.exe --tray` в этих сессиях токеном их пользователя (не повышенным) и удаляет файл. При
  удалении файл не пишется.
- «Используемые файлы» при исправлении/обновлении: `InstallValidate` (раньше любого отложенного действия) показывает
  окно, если файлы держит процесс с окном на рабочем столе клиента; процессы без окна (служба, winws) пропускаются
  («Window could not be found» в журнале). Без диалога `FilesInUse` в пакете MSI вместо окна даёт ошибку 2803, поэтому
  диалоги `FilesInUse`/`MsiRMFilesInUse` в пакете есть, а свой `zaprett-ui` пользователя закрывается ещё до расчёта
  файлов: немедленное действие `ZaprettCloseUi` (`installer/scripts/close-ui.ps1`, встроен как `-EncodedCommand`,
  от имени устанавливающего пользователя, до `CostInitialize`, в последовательности выполнения — значит, и при
  `/passive`, `/qb`). Сессии, где оно закрыло значок, — в `HKCU\Software\zaprett\UiRelaunchPending`
  (`<unix-время>;<сессии>`); `prepare.ps1` забирает их из всех загруженных профилей (не старше 15 минут и только
  сессии того же пользователя) и добавляет в `ui-relaunch.json`. При настоящем удалении (кнопка «Удалить» в окне
  обслуживания) действие тоже закрывает свой значок, но ничего не записывает: `ZAPRETT_CLOSE_UI_REMOVE` — тот же
  скрипт с первой строкой `$NoRelaunch = $true`. `ZaprettCloseUi` выполняется только при `MsiRunningElevated`: при
  отказе в UAC последовательность выполнения всё равно начинается (без повышения) и завершается ошибкой 1730 —
  закрытый там значок было бы некому вернуть. Вторая линия: кнопка «Готово» страниц `UserExit` и `FatalError`
  (Order 0, раньше их EndDialog) запускает `ZaprettRestoreUi` (`installer/scripts/restore-ui.ps1`, в клиенте): если
  `UiRelaunchPending` осталась (подготовка её не забрала), скрипт удаляет её и запускает `zaprett-ui.exe --tray` в
  этой сессии без повышения. Скрипты build.ps1 передаёт WiX через сгенерированный `EmbeddedScripts.wxs` (свойства
  `ZAPRETT_PREPARE`, `ZAPRETT_CLOSE_UI`, `ZAPRETT_CLOSE_UI_REMOVE`, `ZAPRETT_RESTORE_UI`), а не командной строкой:
  вместе они больше её предела 32767 символов.
- Вывод скриптов в журнал MSI: WixQuietExec декодирует его как OEM и записывает в журнал, который читается как ANSI,
  поэтому скрипты пишут байты в ANSI-кодировке (не-ASCII пути читаемы; символы вне этой кодировки — `\uXXXX`).
- Неудачная первая установка откатывается полностью, включая созданную группу и записанный ею `config.json`.
- Обновление — `MajorUpgrade`: старая версия удаляется целиком, данные и группа остаются; ставить старую поверх
  новой нельзя.
- Подавленные проверки ICE (`Zaprett.Installer.wixproj`): ICE43/ICE57 (ярлык per-machine пакета в общем меню
  «Пуск»), ICE03 (библиотеки Windows App SDK несут коды языков 1152/1153/1169, которых нет в таблице ICE03).

Тихая установка для администраторов:

```
msiexec /i zaprett-1.0.0-x64.msi /qn AUTOSTART=1 SERVICES=youtube,discord /l*v install.log
msiexec /i zaprett-1.0.0-x64.msi TRANSFORMS=:2052 LANG=zh-CN          (китайский установщик и интерфейс)
msiexec /i zaprett-1.0.0-x64.msi /qn DESKTOPSHORTCUT=0                 (без ярлыка на рабочем столе)
msiexec /x zaprett-1.0.0-x64.msi /qn REMOVEDATA=1
zaprett-1.0.0-x64-setup.exe /qn SERVICES=youtube,discord AUTOSTART=1 /l*v install.log   (то же через setup.exe)
```

### 4.1. `zaprett-<версия>-x64-setup.exe`

Зачем: обычный MSI просит права администратора только после кнопки «Установить», в конце страниц. Запрос UAC
иногда открывается позади других окон (мигающий щит на панели задач); без ответа он закрывается сам примерно через
2 минуты, и установка заканчивается страницей «прервано» — ничего не поставлено, в «Приложениях» пусто.
`setup.exe` спрашивает права сразу после двойного щелчка, до страниц установщика, и затем запускает тот же MSI
с полным интерфейсом.

- Проект — `windows/setup/Zaprett.Setup.csproj` (в `Zaprett.slnx`): net48, WinExe, `AssemblyName` `zaprett-setup`,
  манифест `asInvoker`; повышение — сам себя через ShellExecute `runas`. Нужен только .NET Framework 4.8 (есть в
  Windows 10 2004+ и Windows 11); для сборки — NuGet-пакет `Microsoft.NETFramework.ReferenceAssemblies`
  (восстанавливается автоматически).
- `build/build-setup.ps1 -Msi <msi> [-OutDir <каталог>] [-Configuration Release]` собирает проект с
  `-p:ZaprettMsi=<msi> -p:ZaprettMsiSha256=<sha>`: MSI встраивается ресурсом `zaprett.msi`, в метаданных сборки —
  `ZaprettMsiSha256` и `ZaprettMsiName`. Версия берётся из ProductVersion MSI. После сборки скрипт проверяет у exe
  FileVersion = `<версия>.0` и что SHA-256 встроенного ресурса, прочитанного из готового exe, равен SHA-256 MSI.
  Имя результата — `<имя MSI без расширения>-setup.exe`.
- При запуске: отказ или закрытие запроса UAC — сообщение с объяснением и кнопками Windows «Повтор» / «Отмена»
  (`MessageBoxW` с `MB_SETFOREGROUND | MB_TOPMOST`: после отказа передний план у другого окна, обычное окно открывалось
  неактивным; при `/qn` и т. п. — без сообщения, код 1602). Язык сообщения — язык интерфейса Windows
  (`CurrentUICulture`): текст цитирует окно UAC и называет кнопки, а их Windows рисует на языке интерфейса; страницы
  MSI при этом идут на языке регионального формата (так выбирает преобразование Windows Installer). Если процесс уже
  повышен (запуск от администратора, повышенная консоль), второго запроса нет; если повышенная копия всё же
  оказалась без прав — понятное сообщение и 1603. Затем MSI кладётся в **кэш пакета**
  `%CommonProgramFiles%\zaprett\Installer\<версия>\` (писать под Common Files могут только SYSTEM и администраторы —
  пакет нельзя подменить между проверкой и запуском msiexec). Этот каталог Windows Installer записывает источником
  продукта, и «Исправить» берёт файлы оттуда: копия в `%WINDIR%\Installer` без файлов внутри (EmbedCab), а источник во
  временном каталоге после его удаления давал при восстановлении 1603. На время работы msiexec файл открыт **только на
  чтение** с `FileShare.Read` (дескриптор с правом записи давал 1619, ZERR-060); SHA-256 сверяется через этот же
  дескриптор, запускается `%SystemRoot%\System32\msiexec.exe /i "<msi>" <аргументы пользователя>`. После успешной
  установки (0, 3010, 1641) пакеты других версий удаляются; при неудаче первой установки (продукт не установлен —
  `MsiQueryProductState`) удаляется и свой. Настоящее удаление zaprett удаляет весь кэш
  (`installer-actions.ps1 -Action Uninstall`). Обновление обычным `.msi` поверх установленного через `setup.exe`
  оставляет пакет прежней версии до удаления программы. Аргументы командной строки передаются msiexec без
  изменений, код возврата — код msiexec; непредвиденная ошибка — 1603, а не аварийное окно.
- Подмена DLL рядом с exe: повышенная копия запускается из той же папки, что и exe (обычно «Загрузки»), а
  `mscoree.dll` не входит в KnownDLLs. Пока exe не подписан, это не расширяет риск: тот, кто может положить DLL в эту
  папку, может подменить и сам exe. **С появлением подписи** (UAC покажет проверенного издателя) это нужно закрыть —
  например, нативной заглушкой с `SetDefaultDllDirectories`.
- Тесты: `tests/Zaprett.Core.Tests/SetupLogicTests.cs` (`setup/SetupLogic.cs` подключён в тестовый проект ссылкой)
  и `InstallerSourceTests.cs`.

## 5. WiX v7 и OSMF EULA

WiX Toolset v7 распространяется по OSMF EULA v1.1 и требует явного согласия: без него сборка падает с
`WIX7015: You must accept the Open Source Maintenance Fee (OSMF) EULA`. `build.ps1` передаёт согласие
свойством MSBuild `-p:AcceptEula=wix7` (для `wix.exe` — ключ `-acceptEula wix7`); файл согласия в профиле не
создаётся.

Взнос OSMF по EULA v1.1 обязателен только тому, кто получает от проектов с WiX доход **от 10 000 долларов США в
год**. zaprett — некоммерческий проект под лицензией MIT, дохода не приносит, поэтому взнос не требуется. Если это
изменится (продажа, платная поддержка, спонсорство на эту сумму), взнос нужно оформить до следующего выпуска.

## 6. Выпуск релиза

1. Поднять `<Version>` в `windows/Directory.Build.props` (формат `X.Y.Z` — ProductVersion MSI), закоммитить.
2. Поставить тег `win-v<версия>` (например `win-v1.0.0`) и отправить его. Теги роутера (`v*`) и Windows (`win-v*`)
   не пересекаются.
3. `windows.yml`: сборка на `windows-latest` (тег обязан совпасть с `<Version>`), затем задание `release`
   в окружении `release`: проверка, что в артефактах нет `-STUB-UI`, `sha256sum -c SHA256SUMS`, `update.json`
   (sha256 и размер MSI, ссылка на него в релизе), подпись ключом из секрета, проверка подписи ВСТРОЕННЫМ в
   приложение открытым ключом, `gh release create` с файлами `zaprett-<версия>-x64-setup.exe`,
   `zaprett-<версия>-x64.msi`, `SHA256SUMS`, `update.json`, `update.json.sig`.

**`setup.exe` и подпись.** `build.ps1` собирает `zaprett-<версия>-x64-setup.exe` вокруг ещё не подписанного MSI —
его выпускает только неподписанный прогон. В подписанном прогоне задание `sign` идёт так: подписать MSI (SignPath) →
`build-setup.ps1` с **подписанным** MSI → при заданной переменной репозитория
`SIGNPATH_SETUP_ARTIFACT_CONFIGURATION_SLUG` — второй запрос SignPath на exe (своя конфигурация артефакта: голый PE),
проверка подписи exe (тот же сертификат, что у MSI, есть метка времени, файл отличается от неподписанного); без
переменной — exe без подписи с предупреждением → сверка, что ресурс `zaprett.msi` внутри exe побайтно равен
подписанному MSI → `SHA256SUMS` на оба файла. Задание `release` берёт exe из `sign` и сверяет его SHA-256 с выходом
`sign` (exe из `build` в подписанный релиз не попадает). Проверено локальным прогоном шагов (Windows PowerShell /
Git Bash) на файлах 0.1.3 с отрицательными контролями: exe от другой сборки и «подписанный» MSI, равный
неподписанному, — отказ. На раннерах GitHub эти шаги ещё не выполнялись: `.github/workflows` в публичный репозиторий
не выкладывается (у токена нет scope `workflow`).

### Размер в «Приложениях» (ARPSIZE)

`build.ps1` суммирует размеры всех файлов stage (всё, что в нём лежит, попадает в MSI) и передаёт сумму в КБ
свойством `-p:ZaprettArpSizeKb` → `ARPSIZE`. Без этого Windows Installer считает размер сам, а при обновлении
(старая версия удаляется после установки новой) учитывает обе версии: 418 МБ вместо 236 МБ после 0.1.0 → 0.1.1.
Сборка MSI без `ZaprettArpSizeKb` (не через `build.ps1`) останавливается ошибкой ещё до компоновки. После сборки
`build/check-arpsize.ps1` пересчитывает размер независимо, по таблице File готового MSI: `ARPSIZE` должен быть
равен сумме `FileSize` в КБ (округление вверх), иначе сборка падает. Сверка 2026-09-23: сумма File по MSI 0.1.1
(`3f199bf`) = 241 491 431 байт, ровно размер каталога установки на тестовой машине с Windows 11.

### Выпуск: проверка против прошлого релиза

Обновление устроено как `MajorUpgrade Schedule="afterInstallExecute"`: сначала ставится новая версия, потом
удаляется старая. Это безопасно, только если у каждого компонента с тем же ключевым путём (каталог + ключевой
файл; для компонентов-реестра и компонентов-каталогов — их идентификатор) GUID не изменился. Иначе удаление старой
версии сотрёт файлы, которые новая только что поставила. Поэтому каждый выпуск собирается с MSI прошлого релиза:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File windows/build/build.ps1 -PreviousMsi <путь>\zaprett-0.1.0-x64.msi
```

`build/check-components.ps1` сравнивает таблицы Component обоих MSI. Хотя бы один отличающийся GUID — сборка
падает, причём с перечнем (`DIFFERENT GUID: <путь>  {старый} -> {новый}`). Компоненты, которые есть только в
старом или только в новом пакете, выводятся для сведения: они удаляются или добавляются обычным порядком. Без
`-PreviousMsi` проверка пропускается с предупреждением в выводе — для выпуска так нельзя. GUID компонентов WiX
выводит из пути файла, поэтому переименование или перенос файла даёт новый компонент, а не другой GUID. Опасный
случай — ручной `Guid=` у компонента или смена `Id` каталога.

Отрицательный контроль (2026-09-23): копия MSI, у которой ComponentId компонента `ServiceExe` вручную заменён на
`{DEADBEEF-…}`. Результат — `DIFFERENT GUID: …\zaprett-svc.exe`, `FAIL`, код 1. Для сравнения: опубликованный 0.1.0
против сборки текущего дерева — 730 из 730 компонентов совпадают, `ok`.

### Секреты

| Где | Имя | Содержимое |
|---|---|---|
| GitHub, environment `release` | `ZAPRETT_UPDATE_KEY` | содержимое файла приватного ключа (две строки: заголовок закрытого ключа `zaprett-update-ed25519 … v1` и base64 зерна ключа; файл создаёт `keygen`) |

Приватный ключ хранится вне репозитория: путь — `--key`, иначе переменная `ZAPRETT_UPDATE_KEY` (путь к файлу),
иначе `~/.zaprett-keys/update-ed25519.key`. `keygen` создаёт файл только если его нет и даёт права только
владельцу. Держите резервную копию ключа в надёжном месте: без него обновления перестанут проходить проверку у
всех установленных копий, а смена ключа требует выпуска новой версии со встроенным новым открытым ключом.

Открытый ключ — `windows/tools/update-sign/keys/update-ed25519.pub`, он же константа
`UpdateKeys.ProductionPublicKeyBase64` в `Zaprett.UpdateVerify` (тест сверяет их между собой). Задание `release`
подписывает с `--expect-pub` этого файла: если секрет не от этого ключа, релиз не выпускается.

## 7. Манифест обновлений (ARCHITECTURE-WIN W-7)

`update.json` (UTF-8, LF):

```json
{
  "version": "1.0.0",
  "channel": "stable",
  "msi_url": "https://github.com/<владелец>/<репозиторий>/releases/download/win-v1.0.0/zaprett-1.0.0-x64.msi",
  "sha256": "<64 hex>",
  "size": 34000000,
  "released_at": "2026-09-23T10:00:00Z",
  "notes_url": "https://github.com/<владелец>/<репозиторий>/releases/tag/win-v1.0.0"
}
```

`update.json.sig` — одна строка: base64 64-байтной подписи ed25519 над **точными байтами** `update.json`.
Служба проверяет подпись встроенным ключом (`UpdateVerifier.Verify`), только потом разбирает JSON (схема строгая:
версия `X.Y.Z[-суффикс]`, канал `stable|beta`, только https без логина в URL, `sha256` — 64 строчные hex, размер
1 байт…512 МиБ, время UTC), после скачивания MSI сверяет sha256 и размер (`UpdateVerifier.MatchesFile`).

Инструмент `windows/tools/update-sign` (`zaprett-update-sign`):

```
dotnet run --project windows/tools/update-sign/Zaprett.UpdateSign -- keygen --pub windows/tools/update-sign/keys/update-ed25519.pub
dotnet run --project windows/tools/update-sign/Zaprett.UpdateSign -- manifest --msi <msi> --version 1.0.0 --channel stable --msi-url <url> --notes-url <url> --out update.json
dotnet run --project windows/tools/update-sign/Zaprett.UpdateSign -- sign --manifest update.json --expect-pub windows/tools/update-sign/keys/update-ed25519.pub
dotnet run --project windows/tools/update-sign/Zaprett.UpdateSign -- verify --manifest update.json --sig update.json.sig --msi <msi>
```

Коды возврата: 0 — успех, 1 — проверка не прошла, 2 — ошибка в аргументах. Тесты
(`Zaprett.UpdateVerify.Tests`, входят в решение): каждый изменённый байт манифеста и каждый изменённый бит подписи
отвергаются, чужой ключ, битая подпись, подписанный, но неверный по схеме манифест, лишний перевод строки —
тоже; встроенный ключ равен файлу `.pub`.

## 8. Как пользователю проверить скачанный MSI

Authenticode-подписи пока нет (ARCHITECTURE-WIN W-7) ни у MSI, ни у `setup.exe`, поэтому SmartScreen покажет
предупреждение, а запрос UAC — «неизвестный издатель». Проверка файла:

```powershell
Get-FileHash .\zaprett-1.0.0-x64-setup.exe -Algorithm SHA256    # сравнить со строкой в SHA256SUMS релиза
Get-FileHash .\zaprett-1.0.0-x64.msi -Algorithm SHA256
```

Подпись манифеста (при наличии .NET SDK и исходников) — команда `verify` из раздела 7; служба делает то же
самое сама перед каждым обновлением.
