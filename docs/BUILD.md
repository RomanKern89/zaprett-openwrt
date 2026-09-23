# Сборка и выпуск пакетов zaprett

> Для разработчика. Как собрать пакеты для OpenWrt 25.12 (apk) и 24.10 (opkg), подписать фиды, сделать
> релизные бандлы и выпустить новую версию. Установка для пользователя — `docs/INSTALL.md`.

## 1. Что получается на выходе

| Пакет | Arch | Откуда | Версия |
|---|---|---|---|
| `zaprett` | noarch/all | `packages/zaprett` | из Makefile |
| `luci-app-zaprett`, `luci-i18n-zaprett-ru` | noarch/all | `packages/luci-app-zaprett` (перевод генерирует luci.mk) | из Makefile |
| `zaprett-nfqws` | по arch | статический `nfqws` из релиза bol-van/zapret `v72.13` (`openwrt-embedded`) | `72.13-r1` |
| `zaprett-nfqws2` | по arch | статический `nfqws2` + `lua/*.lua.gz` из релиза bol-van/zapret2 `v1.0.5.2` | `1.0.5.2-r1` |

Результат сборки (каталог `dist/`, в git не попадает):

```
dist/
  25.12/<arch>/           фид apk: *.apk + packages.adb (подписан ECDSA-ключом zaprett)
  24.10/<arch>/           фид opkg: *.ipk + Packages, Packages.gz, Packages.sig (usign)
  releases/zaprett-<ver>-<series>-<arch>.tar.gz   бандлы для пользователей
  keys/zaprett.pem, keys/zaprett-usign.pub        открытые ключи
  arches.txt              матрица архитектур, с которой собрано
  verify-25.12.txt, verify-24.10.txt              отчёты проверок (каждая строка OK/FAIL)
  refused-25.12.txt, refused-24.10.txt            архитектуры, от которых Makefile отказался (отрицательный контроль)
  SHA256SUMS
```

Бандл `zaprett-<ver>-<series>-<arch>/`: `feed/` (фид этой архитектуры), `keys/` (ключ для своей серии),
`install.sh`, `bundle.info` (`version`, `series`, `arch`, `pm`, `built`), `SHA256SUMS`.

## 2. Как устроена сборка

- **Где:** отдельная Linux-машина с Docker (проверено на Ubuntu с Docker 29), доступная по SSH по ключу.
  Рабочий каталог на ней — только `~/zaprett-build/`.
- **SDK:** официальные образы `openwrt/sdk:x86_64-v25.12.5` (apk) и `openwrt/sdk:x86_64-v24.10.8` (ipk).
  Дерево `/builder` каждого образа один раз копируется в `~/zaprett-build/sdk/<series>/` и монтируется в
  контейнер — фиды, хост-утилиты и кэш переживают перезапуски. Смена образа (другой Id) → дерево пересоздаётся.
- **Один x86_64-SDK на все архитектуры.** Бинарники не компилируются, поэтому пакет для любой arch
  собирается в x86_64-SDK: `make package/zaprett-nfqws/compile ZAPRETT_ARCH=<arch>`. `ZAPRETT_ARCH` →
  `PKGARCH` → поле `arch`/`Architecture` пакета. Для каждой arch — свой `PKG_BUILD_DIR`, `clean` перед сборкой.
  Makefile выбирает каталог `binaries/linux-*` по arch, проверяет ELF (класс, порядок байт, `e_machine`,
  `elf-check.sh`) и **отказывается** (`$(error …)`) для arch без бинарника. strip отключён (чужая arch, UPX).
- **noarch-пакеты** собираются прямым вызовом их Makefile (`make -r -C package/feeds/<feed>/<pkg> TOPDIR=/builder`),
  а не `make package/X/compile`: верхний уровень компилирует все рантайм-зависимости из дерева (ucode, все kmod
  ради `kmod-nft-queue`, openssl). Для luci.mk нужны фиды `base` и `luci` (неглубокие git-копии
  ровно тех коммитов, что в `feeds.conf.default` образа) и хост-утилиты `make package/luci-base/host/compile`.
- **Фид на arch:** noarch-пакеты + пакеты nfqws этой arch; индекс строится инструментами SDK:
  - apk: `apk mkndx --allow-untrusted --sign private-key.pem --output packages.adb *.apk`
    (как `package/Makefile` OpenWrt; сами .apk не подписываются, доверие — через подписанный индекс);
  - opkg: `scripts/ipkg-make-index.sh` → `Packages` (без Maintainer/Source/…, с обходом бага usign SHA-512),
    `Packages.gz`, `usign -S -m Packages -s key-build` → `Packages.sig`.
- **Проверка** (`build/verify.py` в том же контейнере) — см. раздел 6. Любой FAIL останавливает выпуск.
- Обе серии собираются параллельно (два контейнера).

Файлы:

| Файл | Где выполняется | Назначение |
|---|---|---|
| `build/remote.py` | Windows | упаковать исходники, залить на VM (sha256), запустить `build.sh`, следить за логом, забрать `dist.tar`, сверить `SHA256SUMS`, распаковать в `dist/` |
| `build/build.sh` | VM (хост) | подготовить SDK-деревья, запустить контейнеры серий, собрать `dist/` и бандлы |
| `build/sdk-build.sh` | контейнер SDK | фиды, сборка пакетов, отказы для arch без бинарника, индексы и подписи |
| `build/verify.py` | контейнер SDK | проверки фактами с отрицательными контролями |
| `build/test-install-rootfs.sh` | VM (хост) | прогон `install.sh` из x86_64-бандлов в контейнерах `openwrt/rootfs` (раздел 6.1) |
| `build/install.sh` | роутер | установщик из бандла (кладётся в каждый бандл) |
| `build/arches.txt` | — | матрица архитектур |
| `tools/ci/*.sh`, `tools/ci/*.py` | GitHub Actions / сборочная машина | CI-проверки, qemu-smoke, ключи, релиз (раздел 10) |
| `.github/workflows/*.yml` | GitHub Actions | CI, сборка, релиз по тегу, слежение за движком (раздел 10) |
| `build/keys/zaprett-apk.pub`, `build/keys/zaprett-usign.pub` | — | открытые ключи (в git) |
| `packages/zaprett-nfqws*/Makefile`, `elf-check.sh` | SDK | арх-пакеты nfqws/nfqws2 |

## 3. Ключи подписи

| Ключ | Приватная часть | Открытая часть |
|---|---|---|
| apk (ECDSA prime256v1) | `$ZAPRETT_SECRETS/zaprett-apk-private.pem` (по умолчанию `~/.zaprett-keys`) | `build/keys/zaprett-apk.pub` → на роутере `/etc/apk/keys/zaprett.pem` |
| usign (Ed25519) | `$ZAPRETT_SECRETS/zaprett-usign.key` (по умолчанию `~/.zaprett-keys`) | `build/keys/zaprett-usign.pub` → на роутере `/etc/opkg/keys/<отпечаток>` |

- В репозиторий и в рабочее дерево приватные ключи не попадают. На сборочной машине они лежат в `~/zaprett-build/keys/`
  (каталог 700, файлы 600); `remote.py` сверяет их sha256 с локальными перед каждой сборкой и перезаливает при расхождении.
- Ключи созданы один раз командой `python build/remote.py keys-init` (генерация в контейнере SDK без сети).
  Повторный `keys-init` откажется перезаписывать существующие ключи.
- Имена открытых ключей в `build/keys/` выбраны так, чтобы не попадать под `.gitignore` (`*.pem`, `key-build*`).
- **Смена ключа** ломает обновление у пользователей: `install.sh` откажется ставить бандл с другим ключом без
  `--force`. Менять ключ только при компрометации и сообщать об этом в релизе.

## 4. Сборка с нуля

Требования на управляющей машине: Python 3.9+ с `paramiko` и доступ к сборочной машине по SSH-ключу.

```sh
set ZAPRETT_BUILD_HOST=<адрес сборочной машины>
set ZAPRETT_BUILD_USER=<пользователь>
set ZAPRETT_BUILD_KEY=<путь к приватному ключу SSH>
python build/remote.py build                       # обе серии, все архитектуры
python build/remote.py build --series 25.12 --arch x86_64,mipsel_24kc
python build/remote.py build --extra-feed build/work/stub   # отладка конвейера на заглушках
```

(В Git Bash и на Linux: `export ZAPRETT_BUILD_HOST=…`.)

`remote.py build` отказывается запускаться, если в текстовых файлах `packages/` или `build/` есть CR (0x0D):
все скрипты уходят в Linux. `--allow-cr` превращает отказ в предупреждение.

Что происходит: исходники (`packages/`, `build/` без `build/work/`, `tools/elfcheck.py`) → tar.gz → VM →
`bash ~/zaprett-build/src/build/build.sh` в фоне (`setsid nohup`, лог `~/zaprett-build/logs/build-<время>.log`,
код возврата — в `.rc`) → `~/zaprett-build/out/dist.tar` → каталог `dist/` после сверки каждого файла с `SHA256SUMS`.

Время (сборочная машина, 4 ядра): полная сборка обеих серий (71 arch, 55 бандлов, 2673 проверки) — **~7 минут**;
пакет nfqws для одной arch — 4–8 с, noarch-пакеты — ~5 с. Первый запуск дольше: копирование SDK (~40 с на серию)
и неглубокая загрузка фидов base+luci (~25 с). `--no-fetch` не трогает сеть для фидов (ошибка, если их ещё нет).

Размеры (1.0.0-r1): бандл 0,6–0,8 МБ; `zaprett-nfqws` 122–327 КБ после установки (x86_64 … mips64, UPX не у mips64),
`zaprett-nfqws2` 359–900 КБ, `zaprett` ~320 КБ, `luci-app-zaprett` ~90 КБ, перевод ~45 КБ.
Весь `dist/` — ~70 МБ.

Опции `build.sh`: `--out DIR` и `--dist DIR` — отдельные каталоги результатов (нужны для сборки «второй версии»
при проверке обновления, раздел 6.1). По умолчанию `~/zaprett-build/out` и `~/zaprett-build/dist`.
`--no-copy` — собирать в `/builder` образа без постоянной копии SDK (CI, раздел 10.3). Рабочий каталог задаёт
`ZAPRETT_WORK` (по умолчанию `~/zaprett-build`).

## 5. Матрица архитектур

Список каталогов `downloads.openwrt.org/releases/<ver>/packages/` для 25.12.5 и 24.10.8 проверен 2026-09-17
(`build/arches.txt`). Соответствие arch → бинарник — ARCHITECTURE §2 с уточнением ниже.

| arch (OpenWrt) | каталог релиза | nfqws | nfqws2 | бандл |
|---|---|---|---|---|
| `aarch64_cortex-a53`, `aarch64_cortex-a72`, `aarch64_cortex-a76`, `aarch64_generic` | linux-arm64 | да | да | да |
| `arm_arm1176jzf-s_vfp`, `arm_cortex-a5_vfpv4`, `arm_cortex-a7`, `arm_cortex-a7_neon-vfpv4`, `arm_cortex-a7_vfpv4`, `arm_cortex-a8_vfpv3`, `arm_cortex-a9`, `arm_cortex-a9_neon`, `arm_cortex-a9_vfpv3-d16`, `arm_cortex-a15_neon-vfpv4` | linux-arm | да | да | да |
| `mipsel_24kc`, `mipsel_24kc_24kf`, `mipsel_74kc`, `mipsel_mips32` | linux-mipsel | да | да | да |
| `mips_24kc`, `mips_mips32`, `mips_4kec` (только 24.10) | linux-mips | да | да | да |
| `mips64_mips64r2`, `mips64_octeonplus` | linux-mips64 | да | да | да |
| `i386_pentium-mmx`, `i386_pentium4` | linux-x86 | да | да | да |
| `x86_64` | linux-x86_64 | да | да | да |
| `powerpc_464fp`, `powerpc_8548` | linux-ppc | да | да | да |
| `riscv64_generic` (25.12), `riscv64_riscv64` (24.10) | linux-riscv64 | **нет** | да | **нет** |
| `arm_arm926ej-s`, `arm_fa526`, `arm_xscale` | — | нет | нет | нет |
| `armeb_xscale`, `mips64el_mips64r2`, `powerpc64_e5500`, `loongarch64_generic` | — | нет | нет | нет |

Итого: 25.12 — 35 arch, из них 27 с бандлом; 24.10 — 36 arch, 28 с бандлом.

Уточнения (факты):

- **ARMv4/ARMv5 исключены.** `linux-arm` после `upx -d`: `readelf -A` → `Tag_CPU_arch: v6KZ`, `Tag_THUMB_ISA_use: Thumb-2`
  (оба бинарника, nfqws и nfqws2). На ARMv5TE (`arm926ej-s`, `xscale`) и ARMv4 (`fa526`) такой код не исполнится.
  В ARCHITECTURE §2 было «`arm_*` → linux-arm» — эти три arch из правила исключены.
- `armeb_*` — big-endian ARM, а `linux-arm` little-endian.
- `linux-mips` (проверен `nfqws` после `upx -d`): MIPS-I, soft-float (`readelf`: `ISA: MIPS1`, `FP ABI: Soft float`) —
  подходит для всех MIPS32. `linux-mipsel` собран тем же тулчейном в LE-варианте (research/01 §1.4), `readelf -A` не снимался.
- **riscv64:** есть только `zaprett-nfqws2` (в zapret v72.13 нет linux-riscv64). Пакет `zaprett` зависит от
  `zaprett-nfqws`, поэтому бандл для riscv64 не выпускается; фид с `zaprett-nfqws2` в `dist/<series>/riscv64_*` есть.
- **НЕ ПРОВЕРЕНО на железе:** запуск бинарников на arm/mips/ppc; `linux-ppc` на `powerpc_8548` (e500v2, SPE без
  классического FPU) — возможна проблема с плавающей точкой; `linux-mips64` на octeonplus.

## 6. Проверки (`build/verify.py`) и что они доказывают

Для каждой arch каждой серии, отчёт — `dist/verify-<series>.txt`:

1. В фиде ровно ожидаемые файлы: noarch-пакеты + nfqws/nfqws2 по матрице; для arch без бинарника пакета нет.
2. Метаданные пакета: apk — `apk adbdump --format json` (`name`, `version`, `arch`, `depends`); ipk — `control`
   из `control.tar.gz`. `arch` пакета nfqws = arch фида; noarch-пакеты — `noarch`/`all`.
3. Бинарник из пакета (apk: `apk extract`, ipk: `data.tar.gz`): ELF-класс/порядок байт/машина по независимому
   от Makefile соответствию, статический (нет `PT_INTERP`/`PT_DYNAMIC`), исполняемый; sha256 равен строке
   `sha256sum.txt` релиза (скачивается с GitHub в `dl/`) и файлу из закреплённого архива.
4. nfqws2: набор `lua/*.lua.gz` совпадает с архивом релиза побайтно.
5. Индекс apk: `adbdump` c нашим ключом → `sig …: OK`; «роутерная» загрузка репозитория
   `apk --keys-dir <наш ключ> --repository file://…/packages.adb fetch` → rc 0.
   Отрицательные контроли: чужой ключ → fetch отказывает; испорченный байт индекса → отказ; дописанный байт
   в .apk при исходном индексе → отказ по хэшу.
6. Индекс opkg: `Packages.gz` = `Packages`; `usign -V -P <каталог с ключом под отпечатком>` над `zcat Packages.gz`
   (как `opkg-key verify`) → OK; каждая запись `Filename/Size/SHA256sum` совпадает с файлом.
   Отрицательные контроли: чужой usign-ключ → отказ; изменённый `Packages` → отказ.
7. Сборка сама: для каждой ячейки «нет» матрицы `make … ZAPRETT_ARCH=<arch>` обязан упасть с сообщением
   `has no static …` и не оставить файлов (`dist/refused-<series>.txt`); для ячеек «да» в логе обязательна строка
   `elf-check: … OK`.

Ожидаемые версии и архив релиза `verify.py` берёт из Makefile пакетов (`PKG_VERSION`, `PKG_RELEASE`, `PKG_SOURCE*`).

Результат 2026-09-17 (полная сборка): 25.12 — 1228 проверок, 0 FAIL; 24.10 — 1445 проверок, 0 FAIL; отказы Makefile —
15 ячеек на серию. Отдельно, локально на Windows (`tools/elfcheck.py` + `sha256sum.txt`, скачанные независимо):
57/57 бинарников из ipk 24.10 — нужная ELF-архитектура, статические, sha256 из релиза; контроль с изменённым байтом → не найден.

### 6.1. Прогон установщика в контейнерах OpenWrt (не роутер)

`bash ~/zaprett-build/src/build/test-install-rootfs.sh [WORK] [releases второй версии]` — контейнеры
`openwrt/rootfs:x86_64-v25.12.4` (apk-tools 25.12.4; образа 25.12.5 в Docker Hub нет) и `openwrt/rootfs:x86_64-v24.10.8`:
настоящие busybox ash, apk/opkg и официальные репозитории OpenWrt; procd/ubus нет (отсюда «Failed to connect to ubus»
в скриптах пакетов). Отчёт — `~/zaprett-build/out/rootfs-test/report.txt`.

| Случай | Ожидание | 25.12 | 24.10 |
|---|---|---|---|
| `install.sh` → 4 пакета, `nfqws --version` = v72.13, повторный запуск, `--uninstall`, `--purge` | rc 0 | OK | OK |
| обновление r1 → r2 (бандл второй версии, `PKG_RELEASE:=2`) | версия nfqws выросла | OK `72.13-r1 -> 72.13-r2` | OK |
| `--with-nfqws2`, `nfqws2 --version` = v1.0.5.2, lua на месте | rc 0 | OK | OK |
| бандл aarch64 на x86_64 | отказ | OK | OK |
| бандл другой серии | отказ | OK | OK |
| испорчен подписанный индекс | отказ пакетного менеджера | OK (`ADB block error`) | OK (подпись) |
| изменён байт в `zaprett-nfqws` при целом индексе | отказ | OK (`file integrity error`) | OK (`Checksum or size mismatch`) |
| на роутере уже другой ключ zaprett | отказ без `--force` | OK | OK |

Бандл второй версии для проверки обновления:

```sh
W=~/zaprett-build; cp -a $W/src $W/src-r2
sed -i 's/^PKG_RELEASE:=1$/PKG_RELEASE:=2/' $W/src-r2/packages/{zaprett,luci-app-zaprett,zaprett-nfqws,zaprett-nfqws2}/Makefile
bash $W/src-r2/build/build.sh --arch x86_64 --no-fetch --out $W/upgrade/out --dist $W/upgrade/dist
bash $W/src/build/test-install-rootfs.sh $W $W/upgrade/dist/releases
```

## 7. Выпуск новой версии zaprett

Основной путь — тег `v<версия>-r<релиз>` и `release.yml` (раздел 10.5). Ручной путь (сборочная машина):

1. Поднять `PKG_VERSION`/`PKG_RELEASE` в `packages/zaprett/Makefile` и `packages/luci-app-zaprett/Makefile`
   (формат apk: `<цифры>(.<цифры>)*-r<N>`).
2. `python build/remote.py build` — дождаться `rc=0`, прочитать `dist/verify-*.txt` (0 FAIL).
3. Опубликовать релиз с `dist/releases/*.tar.gz`, `dist/keys/*` и `dist/SHA256SUMS`. В описании релиза —
   таблица «OpenWrt/arch → файл» и sha256 ключей.
4. Установочные проверки на тестовых роутерах OpenWrt 25.12 и 24.10.

## 8. Обновление nfqws / nfqws2

1. Найти новый тег: `https://api.github.com/repos/bol-van/zapret/releases/latest` (и `zapret2`). Это делает
   `engine-watch.yml` ежедневно: issue «Engine update: …» уже содержит sha256 (скачанный и из API) и таблицу
   `binaries/linux-*` → архитектуры; проверка ниже всё равно обязательна.
2. Скачать `…-openwrt-embedded.tar.gz` и посчитать sha256 **двумя способами**: поле `digest` ассета в GitHub API
   и локальный `sha256sum`/`hashlib` — должны совпасть.
3. В `packages/zaprett-nfqws/Makefile`: `PKG_VERSION`, `PKG_HASH`, `PKG_RELEASE:=1`. Имя архива и префикс
   каталога (`zapret-v<ver>/`) строятся из `PKG_VERSION`.
4. Проверить, не поменялся ли набор `binaries/linux-*` (`tar tzf архив | grep binaries/linux`) и ISA arm
   (`upx -d`, `readelf -A` в контейнере `ubuntu:24.04` с `upx-ucl binutils-multiarch`). Если да — поправить
   соответствие в обоих Makefile, в `build/arches.txt` и в `expected_elf()` в `build/verify.py`.
5. Класть архив в `~/zaprett-build/dl/` не обязательно: SDK скачает его сам и проверит `PKG_HASH`.
   `verify.py` сам возьмёт новую версию из Makefile и скачает `sha256sum.txt` нового релиза.
6. Собрать, проверить отчёт, прогнать `test-install-rootfs.sh`, выпустить (раздел 7).

## 9. Обслуживание сборочной машины

- Свои объекты: образы `openwrt/sdk:x86_64-v25.12.5` (3,2 ГБ), `openwrt/sdk:x86_64-v24.10.8` (3,1 ГБ),
  `openwrt/rootfs:x86_64-v25.12.4`, `openwrt/rootfs:x86_64-v24.10.8` (~20 МБ), каталог `~/zaprett-build/`
  (деревья SDK ~3,3 ГБ), контейнеры `zaprett-sdk-*`/`zaprett-verify-*`/`zaprett-rootfs-*` (все `--rm`).
  Разово для `readelf` использовался образ `ubuntu:24.04`.
- Если машина используется не только для сборки: чужие контейнеры, образы и тома не трогать,
  `docker system prune` не запускать.
- Освободить место: `rm -rf ~/zaprett-build/sdk ~/zaprett-build/out ~/zaprett-build/dist` (пересоздадутся),
  образы SDK — `docker rmi openwrt/sdk:x86_64-v25.12.5 openwrt/sdk:x86_64-v24.10.8`. Приватные ключи в
  `~/zaprett-build/keys` не удалять, не имея их копии вне сборочной машины.

## 10. CI (GitHub Actions) и выпуск тегом

Все проверки и сборка вынесены в скрипты `tools/ci/`, workflow только вызывают их. Те же скрипты запускаются
на сборочной машине (Linux + Docker, bash, curl, git, python3, node) — результат должен совпадать.

### 10.1. Workflow

| Файл | Когда | Что делает |
|---|---|---|
| `.github/workflows/ci.yml` | push в `main`, pull request, вручную | `static-checks.sh`; `ucode-tests.sh` для 25.12 и 24.10 (матрица) |
| `.github/workflows/build.yml` | push в `main`, pull request, вручную | для каждой серии: одноразовые ключи (`gen-keys.sh`), `build.sh --no-copy` (все arch, внутри `verify.py`), `qemu-smoke.sh`; затем `test-install-rootfs.sh` на бандлах обеих серий. Артефакты `dist-<серия>`, `logs-<серия>`, `rootfs-test` (7 дней) |
| `.github/workflows/release.yml` | тег `v*` | тег = версия пакетов (`check-release-tag.sh`); сборка обеих серий с ключами из секретов (`release-keys.sh`); `qemu-smoke.sh`; `test-install-rootfs.sh`; `merge-dist.sh`; GitHub Release (бандлы, `zaprett.pem`, `zaprett-usign.pub`, `SHA256SUMS`); фиды `<серия>/<arch>/` на GitHub Pages |
| `.github/workflows/engine-watch.yml` | ежедневно 06:17 UTC, вручную | `engine_watch.py`: новый релиз bol-van/zapret или zapret2 → issue (не PR) с sha256 и архитектурами |

Права минимальные: по умолчанию `contents: read`; `contents: write` — только у задания публикации релиза,
`pages: write` + `id-token: write` — только у выкладки Pages, `issues: write` — только у engine-watch.
Используются только официальные actions с закреплённой мажорной версией (`actions/checkout@v7`,
`setup-python@v7`, `setup-node@v7`, `upload-artifact@v7`, `download-artifact@v8`, `upload-pages-artifact@v5`,
`deploy-pages@v5`); релиз создаётся штатным `gh` раннера.

### 10.2. Скрипты `tools/ci/`

| Скрипт | Что проверяет / делает |
|---|---|
| `static-checks.sh [root]` | CR в текстовых файлах (`check_cr.py`, байтово; `*.bin`, `*.gz` и другие двоичные исключены); `check_static.py` и `negative_controls.py` пакета LuCI (нужен python-модуль `babel`); `check_names.js` (acorn 8.15.0 ставится `npm` в кэш или берётся из `$ACORN`); `node --check` всех `*.js` (представления LuCI — обёрнутыми в функцию, как это делает LuCI); `tools/data/test_build_bundle.py` — **только при наличии `upstream/`** (исходники zapret и очищенные листы не хранятся в git, в CI шаг честно помечается `SKIP`); `sh -n` скриптов пакетов, `bash -n` скриптов `build/`, `tools/ci/`; `py_compile` всех `*.py` |
| `ucode-tests.sh <25.12\|24.10> [--ref REF\|--tree DIR]` | тесты бэкенда (`packages/zaprett/tests/run.sh`) и rpcd-плагина (`run-plugin-tests.sh`) в контейнере `openwrt/rootfs:x86_64-v25.12.4` / `-v24.10.8`. Берётся **закоммиченное** дерево (`git archive HEAD`), незакоммиченные правки не влияют. nfqws — статический `linux-x86_64` из архива, закреплённого `PKG_HASH` в Makefile (sha256 сверяется). Внутри контейнера — как на роутере: `/usr/libexec/zaprett/nfqws`, `ubusd`, `uhttpd` на 127.0.0.1:80 (для проверки лимита загрузки), `--cap-add NET_ADMIN` (без него `nft -c` и `nfqws --dry-run` падают `Operation not permitted`) |
| `qemu-smoke.sh [--dist DIR]` | `nfqws --version` / `nfqws2 --version` каждой «y»-ячейки `build/arches.txt` под qemu-user с ближайшей моделью CPU (24Kc, 74Kf, 4KEc, arm1176, cortex-a7…a76, Octeon68XX, mpc8548, 440epx, pentium…); с `--dist` — ещё и бинарники из собранных `.ipk` 24.10. Отрицательные контроли: `linux-arm` на ARMv5 (`-cpu arm926`) обязан упасть (SIGILL — подтверждает исключение `arm_arm926ej-s`), MIPS-бинарник под `qemu-arm` не загружается. Без qemu в PATH запускает себя в `ubuntu:24.04` с `qemu-user-static` |
| `gen-keys.sh <dir> [sdk-образ]` | одноразовые ключи apk/usign (как `remote.py keys-init`, контейнер без сети) |
| `release-keys.sh <dir> [sdk-образ]` | ключи из переменных окружения (секреты); отказ, если открытые ключи не совпадают с `build/keys/*.pub`, если приватный apk-ключ не порождает этот открытый (`openssl`) или usign-ключ не подписывает проверочное сообщение (контроль: изменённое сообщение не проходит) |
| `check-release-tag.sh <тег>` | тег обязан быть `v<PKG_VERSION>-r<PKG_RELEASE>` пакета `zaprett`; у `luci-app-zaprett` те же версия и релиз |
| `merge-dist.sh <out> <версия> <dist>...` | объединяет `dist/` серий: сверка `SHA256SUMS` каждой, одинаковые ключи, нет `FAIL` в `verify-*.txt`, все бандлы нужной версии; готовит `assets/` (релиз), `site/` (Pages), `notes.md` |
| `engine_watch.py [--dry-run] [--pinned pkg=ver]` | сравнивает `PKG_VERSION` с `releases/latest`; для нового релиза скачивает архив, сверяет sha256 с полем `digest` API, перечисляет `binaries/linux-*` и сопоставляет с `build/arches.txt`; одна issue на релиз (повторно не создаётся) |
| `free-disk.sh` | только на раннере GitHub (иначе отказ): удаляет неиспользуемые тулчейны (.NET, Android, GHC, CodeQL) |

Запуск на сборочной машине (из клона репозитория):

```sh
export ZAPRETT_CI_CACHE=~/zaprett-ci/cache ZAPRETT_CI_OUT=~/zaprett-ci/out
bash tools/ci/static-checks.sh
bash tools/ci/ucode-tests.sh 25.12 && bash tools/ci/ucode-tests.sh 24.10
bash tools/ci/gen-keys.sh ~/zaprett-ci/work/keys
ZAPRETT_WORK=~/zaprett-ci/work bash build/build.sh --series 25.12 --arch x86_64 --no-copy
bash tools/ci/qemu-smoke.sh --dist ~/zaprett-ci/work/dist
```

Результаты прогона 2026-09-22 на сборочной машине (коммит `3f2967f`): `ucode-tests` — 990 PASS / 0 FAIL бэкенда и
145 / 0 плагина на обеих сериях (как на роутерах стенда); `static-checks` — 7 PASS, `bundle` SKIP (нет `upstream/`
в клоне); `qemu-smoke` — 58 бинарников релизов + 6 из `.ipk` + 2 отрицательных контроля, 0 FAIL; сборка
`--no-copy` 25.12 x86_64 — `verify` 44 проверки, 0 FAIL (и так же под uid 1001, как на раннере GitHub);
`test-install-rootfs.sh` с обновлением r1→r2 — 22 OK, 0 FAIL.

**Число проверок бэкенда зависит от ядра хоста:** `test_nft.uc` при незагруженном модуле `nft_queue`
(`/sys/module/nft_queue`) выполняет ветку «только парсер» — на одну проверку больше (991). Роутер и
сборочная машина после первого прогона — 990. В `ci.yml` модуль загружается `sudo modprobe nft_queue`.

### 10.3. Особенности сборки на раннере

- `build.sh --no-copy` собирает прямо в `/builder` образа SDK, без копии в `WORK/sdk/<серия>` (экономит
  ~1,4 ГБ и ~40 с; фиды base/luci загружаются заново при каждом запуске). Без `--no-copy` поведение прежнее.
- Контейнеры SDK работают от `buildbot` (uid 1000). На раннере GitHub пользователь — uid 1001, и без
  выравнивания `cp`/запись в смонтированные каталоги падают `Permission denied` (проверено: исходный `build.sh`
  под uid 1001 — rc=1). Поэтому `build.sh` при несовпадении uid передаёт `out/<серия>`, `dl`, `keys` (и копию SDK)
  uid-у SDK через одноразовый контейнер от root и возвращает владельца после сборки — без `sudo` на хосте.
  На сборочной машине (uid 1000 = SDK) ничего не меняется.
- Диск: образ SDK ~3,2 ГБ + сборка всех arch одной серии; серии собираются на разных раннерах (матрица),
  перед сборкой `free-disk.sh`.
- Бандлы подписаны одноразовыми ключами только в `build.yml`: они для проверок, не для роутеров.

### 10.4. Настройки репозитория (делает владелец один раз)

1. **Settings → Pages → Build and deployment → Source: GitHub Actions.**
2. **Settings → Environments → `github-pages` → Deployment branches and tags:** добавить правило для тегов `v*`
   (по умолчанию окружение принимает только ветку по умолчанию, и выкладка с тега будет отклонена).
3. **Settings → Environments → New environment `release`:** Deployment branches and tags → только теги `v*`;
   рекомендуется Required reviewers (релиз ждёт подтверждения). Секреты окружения:

   | Секрет | Содержимое |
   |---|---|
   | `ZAPRETT_APK_PRIVATE_KEY` | файл `$ZAPRETT_SECRETS/zaprett-apk-private.pem` целиком (PEM-файл с приватным EC-ключом, от первой до последней строки) |
   | `ZAPRETT_APK_PUBLIC_KEY` | `zaprett-apk-public.pem` (= `build/keys/zaprett-apk.pub`) |
   | `ZAPRETT_USIGN_SECRET` | `zaprett-usign.key` (две строки: строка-комментарий usign и строка ключа) |
   | `ZAPRETT_USIGN_PUBLIC` | `zaprett-usign.pub` (= `build/keys/zaprett-usign.pub`) |

   Задать из командной строки (значение читается из файла, в историю оболочки не попадает):
   `gh secret set ZAPRETT_APK_PRIVATE_KEY --env release < "$ZAPRETT_SECRETS/zaprett-apk-private.pem"` и так же остальные.
4. **Settings → Actions → General → Workflow permissions:** достаточно «Read repository contents»; нужные
   workflow сами повышают права через `permissions:`.

### 10.5. Выпуск релиза тегом

1. Поднять `PKG_VERSION`/`PKG_RELEASE` в `packages/zaprett/Makefile` и `packages/luci-app-zaprett/Makefile`
   (одинаково), закоммитить, дождаться зелёных `CI` и `Build` на `main`.
2. `git tag v<PKG_VERSION>-r<PKG_RELEASE> && git push origin v<PKG_VERSION>-r<PKG_RELEASE>`.
3. `release.yml`: тег не совпал с версией → стоп до сборки; секреты не те → стоп на `release-keys.sh`; любой
   `FAIL` в `verify.py`, qemu-smoke или установочных тестах → релиз не создаётся.
4. После публикации проверить: страница релиза (бандлы, `SHA256SUMS`, sha256 ключей в описании совпадают с
   `build/keys`), `https://romankern89.github.io/zaprett-openwrt/25.12/x86_64/packages.adb` отдаётся, на тестовом
   роутере `sh install.sh --feed` → `apk update` / `opkg update` без ошибок подписи.

### 10.6. Смена ключей подписи

Смена ключа ломает обновления у всех: `install.sh` откажется ставить бандл с другим ключом без `--force`, а
подключённый онлайн-фид станет «UNTRUSTED» (25.12) / не пройдёт проверку подписи (24.10). Менять только при
компрометации.

1. Новые ключи: `python build/remote.py keys-init` в **новый** каталог `ZAPRETT_SECRETS` (старый не перезаписывать).
2. Открытые части → `build/keys/zaprett-apk.pub`, `build/keys/zaprett-usign.pub` (коммит); секреты окружения
   `release` — заменить все четыре. `release-keys.sh` не даст собрать релиз, если хоть один секрет не от пары
   из `build/keys`.
3. Выпустить релиз (новая версия), в описании — предупреждение: переустановить бандлом с `--force`
   (`sh install.sh --force --feed`), старый ключ `/etc/apk/keys/zaprett.pem` / `/etc/opkg/keys/<отпечаток>`
   установщик заменит.
4. При компрометации: удалить старые секреты, отозвать доступ к окружению `release`, сообщить в релизе
   отпечатки старого и нового ключей.

**Офлайн-копии.** Приватные ключи существуют в трёх местах: каталог `ZAPRETT_SECRETS` управляющей машины,
`~/zaprett-build/keys` сборочной машины и секреты окружения `release` (GitHub не отдаёт их обратно — это не
резервная копия). Держать ещё одну копию вне сети: зашифрованный архив (`gpg -c` или `7z -p`) с четырьмя
файлами на двух носителях, пароль — отдельно. Проверка копии раз в полгода: распаковать во временный каталог и
прогнать `release-keys.sh` (он сравнит пару с `build/keys`).
