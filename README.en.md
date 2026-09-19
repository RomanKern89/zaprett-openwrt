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
  strategies take a few minutes; all 64 take longer.
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
  packets skip the firewall entirely — is switched off while the service runs and restored afterwards.
- **Works without GitHub access:** the engine, 64 strategies and the base lists ship inside the package.

| | |
|---|---|
| [![Strategies and auto-selection](docs/screenshots/en-02-strategies.png)](docs/screenshots/en-02-strategies.png) | [![Lists, exclusions and subscriptions](docs/screenshots/en-03-lists.png)](docs/screenshots/en-03-lists.png) |
| **Strategies:** current strategy, auto-selection, custom strategies | **Lists:** domains, IP networks, exclusions, subscriptions |

### Interface language

The web interface is **English by default** — the source strings are English and the Russian
translation ships as a separate package (`luci-i18n-zaprett-ru`), which LuCI uses only when the
interface language is Russian. Verified on the test bench: with `LuCI language = English` all six
pages render in English.

**One honest gap:** metadata that comes from the data bundle — the names, descriptions and notes of
the built-in services in Quick setup and of the built-in lists — is currently **Russian only**. The
list contents themselves are domains and IP networks, so they are language-neutral, and your own
lists and subscriptions are named by you. Translating the bundled catalogue is an open task.

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
tar -xzf zaprett-1.0.0-r1-25.12-x86_64.tar.gz
cd zaprett-1.0.0-r1-25.12-x86_64
sh install.sh
```

The installer checks that the bundle matches your release and architecture, adds the zaprett public
key, attaches the bundle's package feed with signature verification and installs four packages. Add
`--with-nfqws2` for the optional second engine (zapret2, Lua strategies). English-only setup: the
Russian translation package is installed as well but is inactive unless the interface language is
Russian; remove it with `apk del luci-i18n-zaprett-ru` / `opkg remove luci-i18n-zaprett-ru` if you
prefer.

Then open LuCI → **Services → zaprett** → *Quick setup* → tick the services → **Apply the
selection** → *Strategies* → **Start selection** → **Apply the best strategy**.

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

**Not verified:** a full sweep of all 64 strategies (the result file lived in tmpfs and was lost);
live downloads of the external list subscriptions; package upgrades in place (r1 → r2) on a
router; browsers other than Chromium. The complete list is in
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
