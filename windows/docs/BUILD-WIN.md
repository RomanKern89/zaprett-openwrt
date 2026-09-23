# zaprett for Windows — сборка, выпуск, проверка

> Контракт компонентов — [ARCHITECTURE-WIN.md](ARCHITECTURE-WIN.md) (W-4, W-6, W-7, §3, §10). Здесь — как из
> исходников получить MSI, как выпустить релиз и как пользователю проверить скачанный файл.

## 1. Что нужно

| Что | Версия | Зачем |
|---|---|---|
| Windows 10/11 x64 | — | сборка MSI (WiX и Windows Installer COM есть только в Windows) |
| .NET SDK | по `windows/global.json` (10.0.401, `rollForward: latestFeature`) | решение, publish, WiX SDK |
| Windows PowerShell 5.1 или PowerShell 7 | — | `windows/build/*.ps1` |
| Интернет при первой сборке | — | NuGet (WiX 7.0.0, пакеты .NET) и архивы движка |

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
   (en-US) и `2052` (zh-CN), Template `x64;1049,1033,2052` (ARCHITECTURE-WIN §12.2). Автовыбор
   преобразования по языку системы НЕ подтверждён: на Windows 10 с интерфейсом en-US (региональный стандарт ru-RU)
   обычный запуск дал русский установщик (журнал MSI: `Product Language: 1049`, 2026-09-23). Язык задаётся явно —
   `TRANSFORMS=:1033` / `TRANSFORMS=:2052` (двоеточие = встроенное преобразование) вместе с `LANG=en` / `LANG=zh-CN`;
7. результат — `windows/artifacts/dist/zaprett-<версия>-x64.msi` и `SHA256SUMS`.

Параметры: `-Version X.Y.Z` (по умолчанию `<Version>` из `windows/Directory.Build.props`), `-Offline` (движок
только из кэша), `-SkipTests`, `-SkipPublish` (взять прошлый publish из `artifacts\published` — для работы над
установщиком), `-SourceRoot <копия windows/>` (publish из другой копии исходников, например из выгрузки коммита),
`-StubUi` (заглушка вместо интерфейса — только для проверки установщика; такой MSI получает суффикс `-STUB-UI`
и в релиз не попадает: `windows.yml` это проверяет).

Всё, что порождает сборка, лежит в `windows/artifacts/` (в `.gitignore`): `cache\` (архивы движка), `engine\`,
`published\`, `stage\`, `wix\`, `dist\`.

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
- Интерфейс установщика: приветствие → лицензия MIT → предупреждение о WinDivert и антивирусах (флажок значка в
  трее) → каталог → установка. Языки: русский (основной), английский, китайский упрощённый. Страницы лицензии —
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
- Служба `zaprett` (LocalSystem, автозапуск с задержкой, перезапуск при сбое через 5 с, сброс счётчика — сутки):
  запускается после установки, останавливается и удаляется при удалении.
- `C:\ProgramData\zaprett`: SYSTEM и Administrators — полный доступ, Users — чтение, наследование от ProgramData
  отключено (SDDL `D:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)`).
- Локальная группа `zaprett Operators` (ARCHITECTURE-WIN §6) создаётся, устанавливающий пользователь (`UserSID`)
  добавляется в неё.
- Ярлык в меню «Пуск»; значок в трее при входе любого пользователя — `HKLM\...\Run\zaprett`
  (`"<каталог>\zaprett-ui.exe" --tray`), флажок в установщике или `TRAYAUTOSTART=0`.
- Параметры для службы: `HKLM\SOFTWARE\zaprett` — `InstallDir`, `Version`, `AutoStart`, `Services`.
- Удаление (настоящее, не при обновлении): остановить `winws.exe`/`winws2.exe` из нашего каталога (чужие не
  трогаются), выгрузить драйвер `WinDivert*` (`sc stop` — служба драйвера исчезает сама, без перезагрузки) **только**
  если её `ImagePath` указывает в наш каталог, удалить правила брандмауэра `zaprett QUIC block` и группы `zaprett`,
  удалить группу `zaprett Operators`; `REMOVEDATA=1` — удалить `C:\ProgramData\zaprett`. Эти шаги —
  `windows/installer/scripts/installer-actions.ps1` (вывод — в журнал MSI).
- Перед любой установкой, обновлением и удалением закрывается значок в трее (`taskkill /IM zaprett-ui.exe`), чтобы
  не понадобилась перезагрузка. Restart Manager отключён (`MSIRESTARTMANAGERCONTROL=Disable`): при запущенном
  лишнем `winws.exe` он останавливал службу, и удаление завершалось кодом 1601.
- Неудачная первая установка откатывается полностью, включая созданную группу и записанный ею `config.json`.
- Обновление — `MajorUpgrade`: старая версия удаляется целиком, данные и группа остаются; ставить старую поверх
  новой нельзя.
- Подавленные проверки ICE (`Zaprett.Installer.wixproj`): ICE43/ICE57 (ярлык per-machine пакета в общем меню
  «Пуск»), ICE03 (библиотеки Windows App SDK несут коды языков 1152/1153/1169, которых нет в таблице ICE03).

Тихая установка для администраторов:

```
msiexec /i zaprett-1.0.0-x64.msi /qn AUTOSTART=1 SERVICES=youtube,discord /l*v install.log
msiexec /i zaprett-1.0.0-x64.msi TRANSFORMS=:2052 LANG=zh-CN          (китайский установщик и интерфейс)
msiexec /x zaprett-1.0.0-x64.msi /qn REMOVEDATA=1
```

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
   приложение открытым ключом, `gh release create` с файлами `zaprett-<версия>-x64.msi`, `SHA256SUMS`,
   `update.json`, `update.json.sig`.

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

Authenticode-подписи пока нет (ARCHITECTURE-WIN W-7), поэтому SmartScreen покажет «неизвестный издатель».
Проверка файла:

```powershell
Get-FileHash .\zaprett-1.0.0-x64.msi -Algorithm SHA256    # сравнить со строкой в SHA256SUMS релиза
```

Подпись манифеста (при наличии .NET SDK и исходников) — команда `verify` из раздела 7; служба делает то же
самое сама перед каждым обновлением.
