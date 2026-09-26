# zaprett for OpenWrt and Windows

[Русский](README.md) | **English**

[![Router release](https://img.shields.io/github/v/release/RomanKern89/zaprett-openwrt?filter=v*&label=router)](https://github.com/RomanKern89/zaprett-openwrt/releases)
[![Windows release](https://img.shields.io/github/v/release/RomanKern89/zaprett-openwrt?filter=win-v*&label=Windows)](https://github.com/RomanKern89/zaprett-openwrt/releases)
[![License MIT](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

**DPI bypass** for every device at home (an OpenWrt router) or for a single computer (Windows). Some networks inspect
the first packets of every connection, read the site name from them (SNI in TLS, `Host` in plain HTTP, the QUIC
initial packet) and then throttle or drop that connection. zaprett reshapes those first packets so the inspection box
cannot reassemble the name, while the destination server still can.

It is **not** a VPN or a proxy: your traffic is not tunneled or relayed anywhere. Only the opening packets of
connections to the sites you selected are modified, so there is no bandwidth penalty and no third party in the path.
This is a port of the Android manager [zaprett](https://github.com/CherretGit/zaprett-app), built on the
[zapret](https://github.com/bol-van/zapret) engines, with an interface in English and Russian.

![zaprett web interface on the router: state, warnings, site check, monitor](docs/screenshots/en-01-overview.png)

| | OpenWrt router | Windows PC |
|---|---|---|
| **Version** | 1.1.0-r1 | 0.1.3 |
| **For** | every device at home: phones, TVs, set-top boxes, consoles | one Windows 10/11 PC |
| **Install** | a `.tar.gz` bundle and `sh install.sh` | `zaprett-0.1.3-x64-setup.exe` (or the MSI for administrators) |
| **Interface** | LuCI → Services → zaprett | an app with a tray icon |
| **Guide** | [docs/GUIDE.en.md](docs/GUIDE.en.md) · [install](docs/INSTALL.en.md) | [windows/README.en.md](windows/README.en.md) |

---

## Contents

- [How it works](#how-it-works)
- [Is this for you?](#is-this-for-you)
- [zaprett for routers: features](#zaprett-for-routers-features)
- [Screenshots](#screenshots)
- [Requirements](#requirements)
- [Install in 5 minutes](#install-in-5-minutes)
- [Command line](#command-line)
- [zaprett for Windows](#zaprett-for-windows)
- [Code signing policy](#code-signing-policy)
- [What is new in 1.1](#what-is-new-in-11)
- [What has been verified, and what has not](#what-has-been-verified-and-what-has-not)
- [Privacy](#privacy)
- [Documentation](#documentation)
- [Credits and licenses](#credits-and-licenses)

---

## How it works

Even in an encrypted connection (HTTPS, QUIC) the first packets carry the **site name** in plain text. Deep packet
inspection (DPI) equipment reads it and throttles or drops the connection. zaprett changes the **shape of the first
packets** (splits the request, sends a fake packet, reorders segments and so on) so the DPI box can no longer
reassemble the name. Which trick works depends on the provider, so the product selects a strategy for you.

```
devices ──► router: nftables ──► nfqws engine ──► provider (DPI) ──► site
                    (only sites from the enabled lists)
```

## Is this for you?

**It helps when** a network reads the site name from your connections and acts on it. The classic symptom is a
service that is reachable but unusable: video that buffers forever, a chat app that connects but never loads media,
a site that returns the first few kilobytes and then stalls.

**It does not help when:**

- the service is blocked **by IP address**: nothing in the first packets matters then;
- the service itself refuses to serve your country or network;
- the block happens **in DNS**: you need encrypted DNS on the router (`https-dns-proxy`, one button in zaprett) instead;
- you want to hide your IP or your traffic from your provider. That is what a VPN is for.

**Where it was built and tested.** zaprett was developed against DPI deployed by Russian ISPs, and its bundled lists
cover services commonly throttled there (YouTube, Discord, Telegram, RuTracker, sites behind Cloudflare, national
blocklists). The mechanism itself is generic and works against any inspection that keys on SNI/`Host`, but the
built-in lists and the service catalogue are Russia-oriented. If your situation is different, use your own domain and
IP lists; the interface has editors and URL subscriptions for exactly that.

**Please check what applies to you.** Reshaping packets to defeat traffic inspection may be restricted by your
provider's terms of service, your employer's or school's network policy, or local law. You are responsible for how
you use it.

---

## zaprett for routers: features

**One-screen quick setup.** Tick the services that are broken for you: YouTube, Discord, Telegram, RuTracker, sites
behind Cloudflare, Roblox, Signal, "other blocked websites". zaprett enables the matching domain and IP lists, the
always-needed exclusions (banks, government services, marketplaces) and the whitelist mode, so the rest of your
traffic is left alone. Then it starts the bypass and checks from the router whether the sites open. Services that a
DPI bypass *cannot* fix (WhatsApp, Instagram and Facebook, X, ChatGPT and Claude, Spotify) are shown with the reason
and cannot be enabled: no false promises. The interface knows the router's memory and warns when a choice is too heavy.

**Strategy auto-selection that does not stop the bypass.** First the targets are checked **without** the bypass to
learn what is actually broken, then each strategy is tried; the results are ranked and the best one is applied with
one click, or automatically, **only if it beats the current one**. The 12 recommended strategies take a few minutes,
all of them take longer. Candidates run in a separate test engine, so your devices keep the bypass the whole time.

**64 + 14 strategies.** 64 ready-made strategies for `nfqws` from the zaprett repository and 14 of the project's own
strategies for the `nfqws2` (zapret2) engine, including an orchestrator that switches tricks per site by itself when a
site stops opening. You can write your own: the engine validates it before it is saved, and a broken strategy is
never saved.

**See whether it works right now.** "Check now" opens the addresses of the enabled services through the running bypass
and shows cards like "YouTube ✓ 3/3, 616 ms". The **availability monitor** does the same on a schedule, keeps a history
of 48 checks and can select a strategy by itself on a lasting failure. The **watchdog** brings the engine and the
firewall rules back every 5 minutes if they are gone.

**What exactly your ISP blocks.** The diagnosis tells DNS spoofing, IP blocking, resets by site name, throttling and
block pages apart, and says what helps: a strategy, encrypted DNS or only a VPN. Encrypted DNS (`https-dns-proxy`) is
one button away.

**The project's own lists.** YouTube, Discord, Telegram, RuTracker, Roblox, Signal and Cloudflare-hosted sites, with
variants: main, extended, with voice servers (Discord), built-in snapshot (Cloudflare). They are built by the project's
generator from primary sources (service docs, certificates, real browser sessions, client code); every entry is
verified and explained ([docs/LISTS.md](docs/LISTS.md), Russian).

**Lists under your control.** Your own domains and IP networks, exclusion lists that apply in both modes, and
**subscriptions** to external lists over HTTPS with scheduled refresh (Re:filter, antifilter.download, Cloudflare
ranges), a 16 MiB download cap, checks against truncated files, and flash writes only when the content changed.

**Repository browser** for the zaprett catalogue: strategies and lists with sha256 verification and dependency
handling; installs and updates run as background jobs with progress and cancel; automatic update once a day.

**Fast and polite to your router.** Its own flow-offload table instead of disabling offloading for the whole router;
one-click QUIC blocking; a game filter for UDP games; a LAN client filter (every device, only the listed ones, or all
except the listed ones, by IP, subnet or MAC). Rules live in their own nftables table `inet zaprett`, never rewrite
your firewall and survive firewall restarts and reboots; the engine runs as an unprivileged user.

**Web interface and CLI.** Six LuCI pages in English and Russian: overview, strategies, lists, repository, settings,
diagnostics. Everything is also available from the `zaprett` command line, with JSON output.

**Works without GitHub access:** the engine, 64 + 14 strategies and the project's lists ship inside the package.

---

## Screenshots

| | |
|---|---|
| [![Quick setup](docs/screenshots/en-07-quick-setup.png)](docs/screenshots/en-07-quick-setup.png) | [![Strategies and auto-selection](docs/screenshots/en-02-strategies.png)](docs/screenshots/en-02-strategies.png) |
| **Quick setup:** services and list sets | **Strategies:** current strategy, auto-selection, installed ones |
| [![Lists](docs/screenshots/en-03-lists.png)](docs/screenshots/en-03-lists.png) | [![Repository](docs/screenshots/en-04-repo.png)](docs/screenshots/en-04-repo.png) |
| **Lists:** domains, IP networks, exclusions, subscriptions | **Repository:** the zaprett catalogue and updates |
| [![Settings](docs/screenshots/en-05-settings.png)](docs/screenshots/en-05-settings.png) | [![Diagnostics](docs/screenshots/en-06-diagnostics.png)](docs/screenshots/en-06-diagnostics.png) |
| **Settings:** engine, mode, offloading, QUIC, LAN clients | **Diagnostics:** how the provider blocks, report, log |

Every page is explained in the [user guide](docs/GUIDE.en.md).

**Interface language.** The web interface is English by default: the source strings are English, and the Russian
translation (`luci-i18n-zaprett-ru`) is used only when the LuCI language is Russian. The quick setup services and the
built-in lists are shown in English. **Still Russian in 1.1.0-r1:** the descriptions of the built-in strategies and of
the repository catalogue, the titles of the default subscriptions, and messages produced by the service itself
(diagnostic report headings, background task messages, CLI and installer output). English strategy descriptions are
already in the source tree and are not in the 1.1.0-r1 package.

---

## Requirements

- OpenWrt **24.10.x** (opkg) or **25.12.x** (apk) with the fw4/nftables firewall. Older releases (23.05 and earlier)
  and snapshots are not supported.
- Architectures: 27 for 25.12 and 28 for 24.10: x86_64, i386, aarch64 (cortex-a53/a72/a76/generic), arm
  (cortex-a5/a7/a8/a9/a15, arm1176jzf-s), mips/mipsel (24kc, 74kc, mips32), mips64, powerpc. **Not supported:**
  ARMv4/v5 (`arm926ej-s`, `fa526`, `xscale`), `loongarch64`, `mips64el`, `powerpc64` and `riscv64`.
- Free RAM: about 20 MB with the built-in lists. The large national registries (≈81k domains and ≈17k IP networks)
  need a router with **at least 200 MB of RAM**; the interface warns you.
- Free flash: about 2 MB (packages are 0.6–0.8 MB plus dependencies).
- Internet access on the router during installation: dependencies such as `kmod-nft-queue` and `ucode` come from the
  official OpenWrt repository.

## Install in 5 minutes

1. Download the bundle for your OpenWrt release and architecture from
   [Releases](https://github.com/RomanKern89/zaprett-openwrt/releases)
   (how to find them: [INSTALL.en.md §2](docs/INSTALL.en.md#2-find-your-openwrt-release-and-architecture)).
2. Copy it to the router and run the installer:

   ```sh
   cd /tmp
   tar -xzf zaprett-1.1.0-r1-25.12-x86_64.tar.gz
   cd zaprett-1.1.0-r1-25.12-x86_64
   sh install.sh             # --with-nfqws2: second engine; --feed: updates through apk/opkg
   ```

   The same on OpenWrt 25.12 (apk) and 24.10 (opkg). The installer checks that the bundle matches your router, adds
   the zaprett key, attaches the bundle's feed with signature verification and installs the packages. Installing
   through LuCI only, without SSH: [INSTALL.en.md §4](docs/INSTALL.en.md#4-installing-through-the-web-interface-only).
3. Open LuCI → **Services → zaprett** → *Quick setup* → tick the services → **Apply and start**. The wizard enables
   the lists, starts the bypass and checks the sites; if something does not open, it offers **Find out how the
   provider blocks** and **Find a working strategy**.

Update with a new bundle, or with `apk upgrade` / `opkg upgrade` when installed with `--feed`. Uninstall with
`sh install.sh --uninstall` (add `--purge` to remove the settings too). Details:
[user guide, §12–13](docs/GUIDE.en.md#12-updating).

## Command line

```sh
zaprett status                # state, lists, warnings
zaprett start | stop | restart
zaprett test start --quick    # strategy auto-selection; zaprett test status: progress
zaprett probe                 # check whether the sites open
zaprett diagnose              # how the provider blocks
zaprett list enable zaprett-telegram
zaprett diag                  # troubleshooting report; --full: the long one
```

Every command accepts `--json`; exit codes are `0` success, `1` error, `2` bad arguments. Changes made through the CLI
are validated by the engine first. The full list: [user guide, §11](docs/GUIDE.en.md#11-command-line).

---

## zaprett for Windows

No OpenWrt router, or you need the bypass on just one computer? **zaprett for Windows 0.1.3** does the same on a
Windows 10 (2004 or later, including LTSC 2021) or Windows 11 x64 PC: a first-run wizard, site checks, automatic
strategy selection that does not stop the bypass, a monitor, diagnostics, own lists and subscriptions. One
installer with everything inside (.NET runtime, WinDivert driver, zapret and zapret2 engines) — download
`zaprett-0.1.3-x64-setup.exe`: it asks for administrator rights right away and runs the MSI; the `.msi` itself is for
administrators and silent installs; the interface is in
English, Russian and Chinese.

| | |
|---|---|
| [![Home](windows/docs/screenshots/light-en-01-home.png)](windows/docs/screenshots/light-en-01-home.png) | [![Setup wizard](windows/docs/screenshots/light-en-29-wizard-2-services.png)](windows/docs/screenshots/light-en-29-wizard-2-services.png) |
| **Home:** bypass state and site check | **Wizard:** choose services in a couple of minutes |

Installation, SHA256 verification and all features: **[zaprett for Windows guide](windows/README.en.md)**
([Русский](windows/README.md), [简体中文](windows/README.zh-CN.md)).

## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by [SignPath Foundation](https://signpath.org/). Windows releases are signed once the SignPath Foundation application is approved; until then the MSI and `setup.exe` are not Authenticode-signed. Team roles, what is signed and the privacy policy: [docs/CODE_SIGNING.md](docs/CODE_SIGNING.md).

---

## What is new in 1.1

- **Availability monitor** checks the sites on schedule and, on a lasting failure, can select a strategy by itself;
  the **watchdog** brings the engine and the rules back if they disappear.
- **"How the provider blocks" diagnosis** and encrypted DNS with one button.
- **The project's own lists** for YouTube, Discord, Telegram, RuTracker, Roblox, Signal and sites behind Cloudflare,
  with variants.
- **Automatic selection without stopping the bypass**: strategies are tested by a separate test engine.
- Own flow offload table, QUIC blocking, game filter, 14 strategies for the zapret2 engine.
- **Security fix.** In 1.0.0 the validation of options in custom strategies was incomplete: an option could be written
  in a form the check did not recognise. In 1.1.0 every option is first brought to its full name using the engine's
  own option tables, and unknown or ambiguous options are rejected. **Everyone on 1.0.0 is advised to upgrade.**

---

## What has been verified, and what has not

Tested on x86 virtual machines running OpenWrt 25.12.5 and 24.10.8, through a real ISP:

- without the bypass, **1 of 25** target sites opened; the default strategy `strategy-general` fixed nothing on that
  ISP; the auto-selected `strategy-alt` scored 15 of 25 from the router, and **from a client behind the router**
  YouTube (~894 KB), Discord (170 KB) and RuTracker (96 KB) loaded fully; no strategy fixed Telegram;
- the backend and rpcd-plugin test suites pass on both releases;
- firewall rules survive `reload`, `restart`, a full firewall stop and a reboot;
- uninstall with `--purge` leaves no files and no directories behind;
- the published bundle was downloaded onto a router, checksum verified, installed from scratch and started, and an
  upgrade from the published 1.0.0-r1 was tested.

**Not verified:** real MIPS/ARM hardware (packages are built but untested); a full sweep of all 64 strategies (the
result file lived in tmpfs and was lost); live downloads of the external list subscriptions on the test bench;
coexistence with a VPN or policy-based routing on the same router; browsers other than Chromium. The
complete list is in [tests/RESULTS.md](tests/RESULTS.md) (Russian).

## Privacy

Traffic does not go to third-party servers, there is no telemetry, and your lists and settings stay on the router. The
router connects to the internet only for what you enabled: the repository catalogue, subscriptions, the check addresses
of the services during checks and selection, DNS over HTTPS during diagnosis, and the OpenWrt repositories when
installing encrypted DNS. The full list of addresses: [user guide, §16](docs/GUIDE.en.md#16-limits-and-privacy).

## Documentation

| File | Contents |
|---|---|
| [docs/GUIDE.en.md](docs/GUIDE.en.md) · [RU](docs/GUIDE.md) | **user guide: install, every page and setting, CLI, updating, uninstalling, FAQ, privacy** |
| [docs/INSTALL.en.md](docs/INSTALL.en.md) · [RU](docs/INSTALL.md) | install over SSH and through LuCI, online feed, updates, uninstall, after a firmware upgrade, installer errors |
| [windows/README.en.md](windows/README.en.md) | zaprett for Windows: install, wizard, every page, command line |
| [docs/CODE_SIGNING.md](docs/CODE_SIGNING.md) | code signing policy of the Windows app |
| [docs/LISTS.md](docs/LISTS.md) | what is in the built-in lists and where each entry came from (Russian) |
| [docs/LUCI.md](docs/LUCI.md) | how the web interface and its rpcd plugin are built (Russian) |
| [docs/BACKEND.md](docs/BACKEND.md) | CLI reference, service internals, debugging (Russian) |
| [docs/BUILD.md](docs/BUILD.md) | building packages for every architecture (Russian) |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | the contract between the parts of the product (Russian) |
| [tests/RESULTS.md](tests/RESULTS.md) | how it was tested and what the results were (Russian) |

## Credits and licenses

- [bol-van/zapret](https://github.com/bol-van/zapret) — the nfqws engine (MIT).
- [CherretGit/zaprett-app](https://github.com/CherretGit/zaprett-app) and
  [egor-white/zaprett](https://github.com/egor-white/zaprett) — the original manager and the strategy/list model.
- [CherretGit/zaprett-repo](https://github.com/CherretGit/zaprett-repo) — strategies and lists (no license stated).
- [Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube),
  [remittor/zapret-openwrt](https://github.com/remittor/zapret-openwrt),
  [1andrevich/Re-filter-lists](https://github.com/1andrevich/Re-filter-lists) — lists and prior OpenWrt work (MIT).

The code in this repository is MIT ([LICENSE](LICENSE)). Lists and strategies keep the terms of their sources; some
sources state no license, which is noted in [docs/LISTS.md](docs/LISTS.md). Licenses of the Windows app's components
are listed in [windows/README.en.md](windows/README.en.md#credits-and-licenses).
