# zaprett for OpenWrt

[Русская версия / Russian version →](README.md)

**DPI bypass on your OpenWrt router.** Some networks inspect the first packets of every connection,
read the site name from them (SNI in TLS, `Host` in plain HTTP, the QUIC initial packet) and then
throttle or drop that connection. zaprett reshapes those first packets so the inspection box cannot
reassemble the name, while the destination server still can.

It is **not** a VPN or a proxy: your traffic is not tunneled or relayed anywhere. Only the opening
packets of connections to the sites you selected are modified, so there is no bandwidth penalty and
no third party in the path. Set it up once on the router — every device in the house benefits, with
nothing to install on phones, TVs or consoles.

This is a port of the Android manager [zaprett](https://github.com/CherretGit/zaprett-app) to
OpenWrt, built on the [nfqws](https://github.com/bol-van/zapret) engine (v72.13) with a LuCI web
interface, 64 ready-made strategies and curated site lists.

![zaprett web interface: service state, warnings and quick setup](docs/screenshots/en-01-overview.png)

---

## Windows app (beta)

No OpenWrt router, or you need the bypass on just one computer? **zaprett for Windows 0.1.0** does the same on a
Windows 10 (2004 or later, including LTSC 2021) or Windows 11 x64 PC: a first-run wizard, site checks, automatic
strategy selection that does not stop the bypass, a monitor, diagnostics, own lists and subscriptions. One MSI
installer with everything inside (.NET runtime, WinDivert driver, zapret and zapret2 engines); the interface is in
English, Russian and Chinese.

| | |
|---|---|
| [![Home](windows/docs/screenshots/light-en-01-home.png)](windows/docs/screenshots/light-en-01-home.png) | [![Setup wizard](windows/docs/screenshots/light-en-29-wizard-2-services.png)](windows/docs/screenshots/light-en-29-wizard-2-services.png) |
| **Home**: bypass state and site check | **Wizard**: choose services in a couple of minutes |

It is a beta: the installer is not signed with a code-signing certificate, so Windows shows a SmartScreen warning.
Installation, SHA256 verification and all features — **[zaprett for Windows guide](windows/README.en.md)**
([Русский](windows/README.md), [简体中文](windows/README.zh-CN.md)).

---

## What is new in 1.1

- **Availability monitor** checks the sites on schedule and, on a lasting failure, can select a strategy by itself;
  the **watchdog** brings the engine and the rules back if they disappear.
- **"How the provider blocks" diagnosis**: DNS spoofing, IP blocking, drops by site name, throttling, block pages —
  and what will help; encrypted DNS with one button.
- **The project's own lists** for YouTube, Discord, Telegram, RuTracker, Roblox, Signal and sites behind Cloudflare,
  with variants (main, extended, IP networks, Discord voice).
- **Automatic selection without stopping the bypass**: strategies are tested by a separate test engine.
- Own flow offload table, QUIC blocking, game filter, 14 strategies for the zapret2 engine.
- Names and descriptions of services, lists and strategies in English and Chinese (the Chinese ones are used by the
  Windows app).
- **Security fix.** In 1.0.0 the validation of options in custom strategies was incomplete: an option could be written
  in a form the check did not recognise. In 1.1.0 every option is first brought to its full name using the engine's
  own option tables, and unknown or ambiguous options are rejected. **Everyone on 1.0.0 is advised to upgrade.**

---

## Is this for you?

**It helps when** a network reads the site name from your connections and acts on it — the classic
symptom is a service that is reachable but unusable: video that buffers forever, a chat app that
connects but never loads media, a site that returns the first few kilobytes and then stalls.

**It does not help when:**

- the service is blocked **by IP address** — nothing in the first packets matters then;
- the service itself refuses to serve your country or network;
- the block happens **in DNS** — you need encrypted DNS on the router (`https-dns-proxy`) instead;
- you simply want to hide your IP or your traffic from your provider. That is what a VPN is for.

**Where it was built and tested.** This tool was developed against DPI deployed by Russian ISPs, and
its bundled lists cover services commonly throttled there (YouTube, Discord, Telegram, RuTracker,
sites behind Cloudflare, national blocklists). The mechanism itself is generic — it works against any
inspection that keys on SNI/`Host` — but the built-in lists and the preset catalogue are
Russia-oriented. If your situation is different, use your own domain and IP lists; the interface has
editors and URL subscriptions for exactly that.

**Please check what applies to you.** Reshaping packets to defeat traffic inspection may be
restricted by your provider's terms of service, your employer's or school's network policy, or local
law. You are responsible for how you use it.

---

## What it does

- **Web interface in LuCI** (Services → zaprett) with six pages: overview, strategies, lists,
  repository, settings, diagnostics. Everything the UI does is also available from the CLI.
- **Quick setup by service.** Tick what is broken for you; the product enables the matching domain
  and IP lists, the always-needed exclusions (banks, government services, marketplaces) and the
  whitelist mode, so the rest of your traffic is left alone. Services that a DPI bypass *cannot*
  fix are shown with the reason and cannot be enabled — no false promises.
- **Strategy auto-selection.** Different inspection boxes need different tricks, so the product
  probes them for you: first it checks the targets **without** the bypass to learn what is actually
  broken, then tries each strategy, ranks the results and offers the best one. 12 recommended
  strategies take a few minutes; all 64 take longer. **Your devices keep the bypass during the check:**
  candidate strategies run in a separate test engine that sees only the test requests. 14 extra strategies
  for the zapret2 engine are included, among them an orchestrator that switches tricks per site by itself.
- **See whether it works right now.** "Check now" opens the addresses of the enabled services through the
  running bypass and shows what opened. A **monitor** does the same on a schedule, keeps a history and can
  run the auto-selection by itself when sites stop opening (it applies a new strategy only if it is better).
  A **watchdog** restores the engine and the firewall rules every 5 minutes if they are gone.
- **What exactly your ISP blocks.** The diagnosis tells DNS spoofing, IP blocking, SNI-based resets,
  throttling and block pages apart, and says what helps: a strategy, encrypted DNS or only a VPN.
  Encrypted DNS (https-dns-proxy) is one button away.
- **Our own lists.** For YouTube, Discord, Telegram, RuTracker, Roblox, Signal and Cloudflare-hosted sites —
  several variants each (core, full, IP networks, Discord voice), built by the project's generator from
  primary sources (service docs, certificates, real browser sessions, client code, AS announcements); every
  entry is verified and explained in the build log ([docs/LISTS.md](docs/LISTS.md)).
- **Fast on small routers.** Its own flow-offload table instead of disabling offloading for the whole router,
  one-click QUIC blocking, a game filter for UDP games, bounded downloads and memory use.
- **Lists under your control.** Built-in lists (cleaned, domains verified to resolve), your own
  domains and IP networks, exclusion lists that apply in both modes, and **subscriptions** to
  external lists over HTTPS with scheduled refresh, a 16 MiB download cap, sanity checks against
  truncated files, and flash writes only when the content actually changed.
- **Repository browser** for the zaprett catalogue: strategies and lists with sha256 verification
  and dependency handling; installs run as background jobs with progress and cancel.
- **LAN client filter** — apply the bypass to every device, only to listed devices, or to everything
  except listed devices (by IP, subnet or MAC).
- **Polite to your router.** Rules live in their own nftables table `inet zaprett` and never rewrite
  your firewall; they are restored after `reload`, `restart`, a full firewall stop and after a
  reboot. The engine runs as the unprivileged `daemon` user. Flow offloading — which would let
  packets skip the firewall entirely — is either replaced by zaprett's own offload table (the first
  packets of each connection still go through the bypass) or switched off while the service runs, and
  restored afterwards.
- **Works without GitHub access:** the engine, 64 + 14 strategies and our own lists ship inside the package.

| | |
|---|---|
| [![Strategies and auto-selection](docs/screenshots/en-02-strategies.png)](docs/screenshots/en-02-strategies.png) | [![Lists, exclusions and subscriptions](docs/screenshots/en-03-lists.png)](docs/screenshots/en-03-lists.png) |
| **Strategies:** current strategy, auto-selection, custom strategies | **Lists:** domains, IP networks, exclusions, subscriptions |

### Interface language

The web interface is **English by default** — the source strings are English and the Russian
translation ships as a separate package (`luci-i18n-zaprett-ru`), which LuCI uses only when the
interface language is Russian. Verified on the test bench: with `LuCI language = English` all six
pages render in English.

Since 1.1 the names, descriptions and notes of the built-in services in Quick setup and of the
built-in lists are shown in English too, and so are the descriptions of the built-in strategies (including the
64 taken from the zaprett repository). **What is still Russian only:** the names of the default list subscriptions. The
list contents themselves are domains and IP networks, so they are language-neutral.

---

## Requirements

- OpenWrt **24.10.x** (opkg) or **25.12.x** (apk) with the fw4/nftables firewall. Older releases
  (23.05 and earlier) and snapshots are not supported.
- Architectures: 27 for 25.12 and 28 for 24.10 — x86_64, i386, aarch64 (cortex-a53/a72/a76/generic),
  arm (cortex-a5/a7/a8/a9/a15, arm1176jzf-s), mips/mipsel (24kc, 74kc, mips32, 4kec), mips64,
  powerpc. **Not supported:** riscv64 (no engine binary) and ARMv4/v5 (`arm926ej-s`, `fa526`, `xscale`).
- Free RAM: about 20 MB with the built-in lists. The large national registries (≈81k domains and
  ≈17k IP networks) need a router with **at least 200 MB of RAM** — the interface warns you.
- Free flash: about 2 MB (packages are 0.6–0.8 MB plus dependencies).
- Internet access on the router during installation: dependencies such as `kmod-nft-queue` and
  `ucode` come from the official OpenWrt repository.

## Install

Download the bundle for your OpenWrt release and architecture from
[Releases](../../releases). To find out what you need:

```sh
cat /etc/openwrt_release     # DISTRIB_RELEASE — the release (24.10.x or 25.12.x)
cat /etc/apk/arch            # OpenWrt 25.12: package architecture
opkg print-architecture      # OpenWrt 24.10: the line with the highest number
```

Then on the router:

```sh
cd /tmp
tar -xzf zaprett-1.1.0-r1-25.12-x86_64.tar.gz
cd zaprett-1.1.0-r1-25.12-x86_64
sh install.sh
```

The installer checks that the bundle matches your release and architecture, adds the zaprett public
key, attaches the bundle's package feed with signature verification and installs four packages. Add
`--with-nfqws2` for the optional second engine (zapret2, Lua strategies). English-only setup: the
Russian translation package is installed as well but is inactive unless the interface language is
Russian; remove it with `apk del luci-i18n-zaprett-ru` / `opkg remove luci-i18n-zaprett-ru` if you
prefer.

Then open LuCI → **Services → zaprett** → *Quick setup* → tick the services → **Apply and start**.
The wizard enables the lists, starts the bypass and checks whether the sites open; if a service does
not open, it offers **Find a working strategy** or, first, **Find out how the provider blocks**.

Uninstall: `sh install.sh --uninstall` (add `--purge` to remove settings and lists as well).
Installation details, upgrades, firmware upgrades and a GUI-only path are in
[docs/INSTALL.md](docs/INSTALL.md) (Russian).

## Command line

```sh
zaprett status              # state, lists, warnings
zaprett start | stop | restart
zaprett check               # build engine arguments and validate them without starting
zaprett test start --quick  # strategy auto-selection; zaprett test status — progress
zaprett list enable zaprett-telegram
zaprett sources update      # refresh subscriptions
zaprett diag                # short report; zaprett diag --full — the long one
```

Every command accepts `--json`. Exit codes: `0` success, `1` error, `2` bad arguments. Changes made
through the CLI are validated by the engine first, so a change that would break a working
configuration is not saved. Full reference: [docs/BACKEND.md](docs/BACKEND.md) §6 (Russian).

## What has been verified, and what has not

Tested on x86 virtual machines running OpenWrt 25.12.5 and 24.10.8, through a real ISP:

- without the bypass, **1 of 25** target sites opened; the default strategy `strategy-general`
  fixed nothing on that ISP; the auto-selected `strategy-alt` scored 15 of 25 from the router, and
  **from a client behind the router** YouTube (~894 KB), Discord (170 KB) and RuTracker (96 KB)
  loaded fully;
- 990 backend tests and 145 rpcd-plugin tests pass on both releases;
- firewall rules survive `reload`, `restart`, a full firewall stop and a reboot;
- uninstall with `--purge` leaves no files and no directories behind (verified twice);
- the published bundle was downloaded from this repository onto a router, checksum verified,
  installed from scratch and started successfully.

**Not verified:** real MIPS/ARM hardware (packages are built but untested); a full sweep of all 64
strategies (the result file lived in tmpfs and was lost); live downloads of the external list
subscriptions; package upgrades in place (r1 → r2) on a router; coexistence with a VPN or
policy-based routing on the same router; browsers other than Chromium. The complete list is in
[tests/RESULTS.md](tests/RESULTS.md) (Russian).

## Documentation

Detailed documentation is currently **in Russian**; an English translation of the user guide is an
open task. The tables below say what each file covers.

| File | Contents |
|---|---|
| [docs/GUIDE.md](docs/GUIDE.md) | user guide: setup, every page and setting explained, typical scenarios, troubleshooting, limits |
| [docs/INSTALL.md](docs/INSTALL.md) | install, upgrade, uninstall, what to do after a firmware upgrade |
| [docs/LISTS.md](docs/LISTS.md) | what is in the built-in lists, where each entry came from, memory footprint |
| [docs/LUCI.md](docs/LUCI.md) | how the web interface and its rpcd plugin are built |
| [docs/BACKEND.md](docs/BACKEND.md) | CLI reference, backend internals, debugging |
| [docs/BUILD.md](docs/BUILD.md) | building packages for every architecture |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | the contract between the parts of the product |
| [tests/RESULTS.md](tests/RESULTS.md) | how it was tested and what the results were |

## Credits and licenses

- [bol-van/zapret](https://github.com/bol-van/zapret) — the nfqws engine (MIT).
- [CherretGit/zaprett-app](https://github.com/CherretGit/zaprett-app) and
  [egor-white/zaprett](https://github.com/egor-white/zaprett) — the original manager and the
  strategy/list model.
- [CherretGit/zaprett-repo](https://github.com/CherretGit/zaprett-repo) — strategies and lists
  (no license stated).
- [Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube),
  [remittor/zapret-openwrt](https://github.com/remittor/zapret-openwrt),
  [1andrevich/Re-filter-lists](https://github.com/1andrevich/Re-filter-lists) — lists and prior
  OpenWrt work (MIT).

The code in this repository is MIT ([LICENSE](LICENSE)). Lists and strategies keep the terms of their
sources; some sources state no license, which is noted in [docs/LISTS.md](docs/LISTS.md).
