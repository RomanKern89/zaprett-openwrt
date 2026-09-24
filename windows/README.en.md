# zaprett for Windows

[Русский](README.md) · [简体中文](README.zh-CN.md) · [zaprett for OpenWrt routers](../README.en.md)

DPI bypass on a Windows PC: what [zaprett for OpenWrt](../README.en.md) does on a router, but for one computer.
Inside are the [zapret](https://github.com/bol-van/zapret) engine (`winws`) with the WinDivert driver, ready-made site
lists and strategies, a first-run wizard, site checks, automatic strategy selection, a monitor and diagnostics. The
interface is available in Russian, English and Simplified Chinese.

![zaprett for Windows: home page](docs/screenshots/light-en-01-home.png)

> **Version 0.1.1.** The installer (MSI) is not signed with a code-signing certificate, so Windows shows a
> SmartScreen warning when you run it — see below for how to verify the file and what to click.
>
> The screenshots in this guide were taken in the interface's demo mode (the "DEMO" badge next to the name): the numbers
> on them are examples, yours will differ.

---

## Contents

1. [What it is and what it is not](#1-what-it-is-and-what-it-is-not)
2. [Requirements](#2-requirements)
3. [Download and verify](#3-download-and-verify)
4. [Install](#4-install)
5. [First run: the setup wizard](#5-first-run-the-setup-wizard)
6. [Pages of the app](#6-pages-of-the-app)
7. [Notification area icon](#7-notification-area-icon)
8. [Update and uninstall](#8-update-and-uninstall)
9. [For administrators: silent install](#9-for-administrators-silent-install)
10. [Command line: `zaprett.exe`](#10-command-line-zaprettexe)
11. [Troubleshooting](#11-troubleshooting)
12. [Privacy](#12-privacy)
13. [What was verified and known limitations](#13-what-was-verified-and-known-limitations)

---

## 1. What it is and what it is not

Some providers run DPI equipment that reads the site name from the first packets of a connection and then throttles
or drops that connection. zaprett changes **only the first packets of connections to the sites you selected**, so the
provider's equipment cannot recognise the name while the site itself still can. All other traffic goes as usual.

**What zaprett does:**

- opens sites and apps that the provider throttles or blocks with traffic inspection (DPI): YouTube, Discord and
  others;
- works only for the services you selected; government services, banks and other mandatory exclusions are left alone;
- checks by itself whether the sites open and finds a strategy that works with your provider.

**What zaprett is not:**

- **it is not a VPN or a proxy.** Your IP address does not change and your traffic is not relayed anywhere. Services
  that refuse to serve your country stay closed;
- zaprett **does not help** against blocking by IP address (such sites need a VPN) or against DNS spoofing alone
  (encrypted DNS helps with that — on Windows 11 zaprett can turn it on);
- zaprett **does not unblock traffic of other devices** that this computer shares its connection with (mobile
  hotspot, Internet Connection Sharing). To cover every device at home, use
  [zaprett for an OpenWrt router](../README.en.md).

## 2. Requirements

| What | Requirement |
|---|---|
| System | Windows 10 version 2004 (build 19041) or later, including **Windows 10 LTSC 2021**; Windows 11 |
| Architecture | x64 only (64-bit Windows on Intel/AMD); ARM64 is not supported |
| Rights | administrator rights **only to install and uninstall**; using the app does not need them (section 11) |
| Installer | one MSI of about 55 MB |

**Nothing else to install** — everything is inside the MSI:

- the .NET 10 runtime and the Windows App SDK (WinUI 3 interface) ship with the app;
- the Visual C++ Redistributable is not needed;
- the `winws` engine (zapret v72.13) and the WinDivert driver are included; the files are taken unchanged from the
  official zapret release and their checksums are verified during the build;
- the second engine `winws2` (zapret2 1.0.5.2) is installed by default and can be selected in Settings.

## 3. Download and verify

1. Open [Releases](https://github.com/RomanKern89/zaprett-openwrt/releases) and find **zaprett for Windows 0.1.1**
   (tag `win-v0.1.1`). Router releases have different tags (`v1.1.0-r1` and so on).
2. Download `zaprett-0.1.1-x64.msi` and `SHA256SUMS`.
3. Check that the file is neither damaged nor replaced. Open PowerShell in the downloads folder and run:

   ```powershell
   Get-FileHash .\zaprett-0.1.1-x64.msi -Algorithm SHA256
   ```

   The `Hash` value must equal the line for `zaprett-0.1.1-x64.msi` in `SHA256SUMS` (letter case does not matter).
   In the classic command prompt: `certutil -hashfile zaprett-0.1.1-x64.msi SHA256`.

If the checksums differ, do not run the file — download it again.

## 4. Install

1. **Double-click** `zaprett-0.1.1-x64.msi`.
2. **If a blue "Windows protected your PC" window appears** (SmartScreen), click **More info**, then **Run anyway**.
   Windows does this for any installer that has no code-signing certificate and few downloads yet. zaprett 0.1.1 has no
   such signature: the certificate costs money and the project is non-commercial. You have verified the file with its
   SHA256 checksum in the previous step.
3. **The installer language follows the Windows regional format** (Settings → Time & language → Region →
   Regional format): an English format gives the English installer, a Chinese one the Chinese installer, any other
   the Russian one. The app opens in the same language; change it later in Settings → Language. To pick the
   language explicitly, run it from the command line:

   ```powershell
   msiexec /i zaprett-0.1.1-x64.msi TRANSFORMS=:1033 LANG=en       # English
   msiexec /i zaprett-0.1.1-x64.msi TRANSFORMS=:2052 LANG=zh-CN    # Chinese
   ```
4. Go through the installer pages:
   - **welcome** and **license** (MIT);
   - **Before you install** — an explanation that zaprett intercepts network packets with the WinDivert driver, and
     that some antivirus products flag WinDivert and `winws.exe` as a "hack tool" or a "potentially unwanted
     application" (the driver can intercept traffic). The same page has the checkbox
     **Show the zaprett icon in the notification area at sign-in**;
   - **installation folder** — `C:\Program Files\zaprett` by default, another one can be chosen (spaces and non-Latin
     letters are fine);
   - **install**. Windows asks for administrator confirmation (UAC); the publisher is shown as unknown — the same
     consequence of the missing signature.
5. After installation open **zaprett** from the Start menu. On the first run the setup wizard opens.

What the installer does: it adds the Windows service **zaprett** (starts with Windows and does the bypass; the window
is only a control panel for it), creates the local group **"zaprett Operators"** and adds the installing user to it, and
adds a Start menu shortcut. Settings and lists live in `C:\ProgramData\zaprett`.

## 5. First run: the setup wizard

The wizard opens by itself while the bypass is not configured yet. You can run it again at any time: **Setup wizard** in
the left menu or **Settings → Setup wizard → Run the wizard**. The button at the bottom right moves to the next step;
**Skip setup** closes the wizard without changes.

### Step 1 of 6. What it is

A short explanation of what zaprett does and does not do. Click **Begin**.

![Step 1: what it is](docs/screenshots/light-en-28-wizard-1-intro.png)

### Step 2 of 6. Services

Tick the services that work poorly for you. Each one shows whether zaprett can help: **Helps**, **Helps partially** or
**Will not help** (those are blocked by address or have closed access themselves, and cannot be enabled). **List set**
lets you choose the main or the extended set, and **What to expect** explains what exactly will work. Click **Next**.

![Step 2: choosing services](docs/screenshots/light-en-29-wizard-2-services.png)

### Step 3 of 6. Conflicts

zaprett looks for programs that also intercept traffic and may break the bypass: GoodbyeDPI, another copy of zapret,
AdGuard, some VPNs and network utilities. Each finding is marked **Blocks the bypass**, **May interfere** or
**For information** and comes with **What to do:** advice. You can continue anyway. Click **Apply and start**.

![Step 3: checking other programs](docs/screenshots/light-en-30-wizard-3-conflicts.png)

### Step 4 of 6. Start and check

zaprett enables the lists of the selected services, starts the bypass and checks whether the sites open. For every
service you see how many test addresses opened. If everything opened, click **Next**. If a service did not open, click
**Find out and fix**.

![Step 4: start and check](docs/screenshots/light-en-31-wizard-4-check.png)

### Step 5 of 6. If something does not open

Providers block in different ways, and the default strategy may not suit yours.

1. **Find out** — in about half a minute zaprett determines **how the provider blocks**: DNS spoofing, blocking the
   address, dropping the connection by site name, or throttling — and tells you what will help.
2. **Find a strategy** — quick automatic selection: about 12 strategies in a few minutes. The bypass keeps working
   meanwhile, and the best strategy is applied by itself.
3. **Check again** — re-check the sites.

![Step 5: how the provider blocks](docs/screenshots/light-en-32-wizard-5-fix.png)

### Step 6 of 6. Done

The **Turn the bypass on when Windows starts** switch (recommended) decides whether the bypass is on after a restart:
when it is on, the bypass turns itself on with the current settings. It does not turn off the bypass that runs now —
that is the button on the home page. Click **Finish**.

![Step 6: done](docs/screenshots/light-en-35-wizard-6-done.png)

## 6. Pages of the app

### Home

The big button turns the bypass on and off. Next to it: the state (works, off, sites stopped opening and so on), the
current strategy, the engine, the uptime and the number of processed packets. Below:

- **Do the sites open?** with **Check now** — checks the test addresses of the enabled services through the running
  bypass;
- **Availability monitor** — history of scheduled checks (green: the sites opened; red: less than half opened);
- **What is processed** — the list mode and links to services, lists and strategies;
- warnings when something needs attention (for example, that DNS requests go without encryption), with a button that
  leads where it can be fixed.

![Home: site check and availability monitor](docs/screenshots/light-en-02-home-part2.png)

### Services

The same service choice as in the wizard: tick what you need and click **Apply**. zaprett enables the ready-made site
lists of these services and the mandatory exclusions.

![Services](docs/screenshots/light-en-03-services.png)

### Strategies and automatic selection

A strategy is a set of techniques that hide the site name from the provider's equipment. Different providers need
different strategies.

**Automatic selection** tries the strategies one by one on the test addresses of the enabled services and shows a
table: how many addresses opened with each strategy and how fast.

- **Quick** — about 12 strategies, a few minutes; **Full** — all installed strategies, may take 10–20 minutes.
- **Apply the best one if it is better than the current** — apply the result automatically. Otherwise pick a strategy
  yourself with **Apply** in its row or **Apply the best**.
- **The bypass is not interrupted during selection:** candidates are tested by a separate test engine, and your bypass
  keeps working with the current strategy. **Select with the bypass stopped** is the old way that turns the bypass off
  for the duration of the test; use it only if the normal selection does not work.
- **Stop** interrupts the selection; the previous strategy stays.

Below is the list of **Installed** strategies: open any of them and **Make current**. **New** creates your own
strategy (its name starts with `user-`); **Save and check** immediately asks the engine whether it accepts it.

![Automatic selection results](docs/screenshots/light-en-08-strategies-part2.png)

### Lists

**Which sites are processed:**

- **Only sites from the enabled lists (recommended)** — everything else goes as usual, so banks, games and other
  programs are not affected;
- **All sites except the exclusions** — helps with sites that are in no list, but more sites may break.

Tabs: **Domains**, **IP networks**, **Exclusions** — built-in lists that can be turned on and off; **Own lists** —
an editor for "My sites", "My site exclusions", "My IP networks" and "My IP network exclusions" (one entry per line,
import and export to a file); **Subscriptions** — external lists by https link that zaprett downloads and refreshes
(**Update now**, **New subscription**).

![Own lists](docs/screenshots/light-en-12-lists-own.png)

![Subscriptions](docs/screenshots/light-en-13-lists-subscriptions.png)

### Diagnostics

- **How the provider blocks** → **Find out**: for every test address, the result (opens, DNS spoofing, blocking by IP
  address, blocking by site name, throttling, provider block page) and a conclusion about what helps: a strategy,
  encrypted DNS, or only a VPN. When DNS spoofing is found, a **Turn on encrypted DNS** button appears (Windows 11).
- **Conflicting programs** — the same checks as in the wizard, with **Check again**.
- **Configuration check** → **Check**: the engine validates the current settings without restarting anything.
- **Log** of the service and the engine; **Detailed engine log** for hard cases.
- **Diagnostic report** — versions, state, conflicts, the last check and the log, to copy or save to a file and attach
  to a question. Parameters of subscription URLs are removed from the report.

![Diagnostics](docs/screenshots/light-en-14-diagnostics.png)

### Settings

| Group | What is there |
|---|---|
| **Bypass** | Turn the bypass on when Windows starts (only what happens after a restart; the button on the home page turns the bypass on or off right now); Engine (zapret (winws) or zapret2 (winws2)); Watchdog (restarts the engine if it stops unexpectedly); Process IPv6 |
| **DNS, QUIC and games** | Encrypted DNS (DNS over HTTPS — **Windows 11 only**; not available on Windows 10 in this version); Block QUIC (helps when YouTube works in one browser but not in another); Game filter with TCP/UDP ports |
| **Networks** | Where the bypass works: in all networks or only in chosen Wi-Fi networks; Do not work in a corporate network; the monitor — Check the sites on schedule, How often, Failed checks before a warning, Repair automatically (quick selection when the sites stop opening, at most once in 6 hours) |
| **Updates** | in 0.1.1 — a note that automatic updates will come in later versions and a button that opens the releases page; how to update — section 8 |
| **Interface** | Language (Russian, English, Chinese — the service and the command line use the same language), Theme (light, dark, as in Windows), Notifications, Keep in the notification area when closed, Setup wizard |

> **Windows Fast Startup.** "Turn the bypass on when Windows starts" is applied when Windows boots. With Fast Startup on (the default in Windows 10 and 11), Shut down is not a full boot: after you power the PC on, the bypass keeps the state it had before the shutdown, and the setting applies at the next **Restart**. A restart of the service (an update, a repair) does not change the state of the bypass either.

![Settings: bypass](docs/screenshots/light-en-18-settings.png)

![Settings: interface](docs/screenshots/light-en-21-settings-part4.png)

The dark theme looks like this:

![Home in the dark theme](docs/screenshots/dark-en-01-home.png)

## 7. Notification area icon

The zaprett icon next to the clock changes with the bypass state: on, off, needs attention, error.

- **Click** — open the zaprett window.
- **Right-click** — menu: **Turn the bypass on** / **Turn the bypass off**, **Check the sites now** (the result comes
  as a notification), **Open zaprett**, **Exit the interface (the service keeps working)**.

Closing the window or the interface does **not** turn the bypass off: the Windows service does the work. Turn it off
with the button on the home page or from the icon menu. The icon starts at sign-in if that checkbox was ticked during
installation.

## 8. Update and uninstall

**Update.** 0.1.1 has no automatic updates — they will come in later versions. To update, download the new MSI from
[Releases](https://github.com/RomanKern89/zaprett-openwrt/releases) (the button in Settings → Updates opens that page)
and run it: the old version is
replaced, your settings, own lists and strategies are kept. Installing an older version over a newer one is refused.

**Uninstall.** Settings → Apps → **zaprett** → Uninstall (or Control Panel → Programs and Features). The uninstaller
stops the service and the engine, unloads the WinDivert driver (only if it was loaded by zaprett's own copy — a driver
of another program is left alone; no restart needed), removes zaprett's firewall rules and the "zaprett Operators"
group, and restores the DNS servers of the adapters if zaprett changed them.

A normal uninstall **keeps** settings and lists in `C:\ProgramData\zaprett` in case you install again. To remove them
too:

```powershell
msiexec /x zaprett-0.1.1-x64.msi REMOVEDATA=1
```

## 9. For administrators: silent install

Run these from an elevated command prompt.

```powershell
# silent install: enable YouTube and Discord and turn the bypass on, installer log to a file
msiexec /i zaprett-0.1.1-x64.msi /qn SERVICES=youtube,discord AUTOSTART=1 LANG=en /l*v install.log

# no notification area icon at user sign-in
msiexec /i zaprett-0.1.1-x64.msi /qn TRAYAUTOSTART=0

# English installer and English app (Chinese: TRANSFORMS=:2052 LANG=zh-CN)
msiexec /i zaprett-0.1.1-x64.msi TRANSFORMS=:1033 LANG=en

# silent uninstall including settings and lists
msiexec /x zaprett-0.1.1-x64.msi /qn REMOVEDATA=1
```

| Property | Value |
|---|---|
| `SERVICES` | comma-separated services, as in the wizard: `youtube`, `discord`, `telegram`, `rutracker`, `cloudflare`, `roblox`, `signal`, `rkn_full`; a list set after a colon, e.g. `discord:full` |
| `AUTOSTART` | `1` — turn the bypass on right away and at every Windows start; default `0` |
| `LANG` | app language: `ru`, `en` or `zh-CN`; defaults to the installer language (`ru`; `en` with `TRANSFORMS=:1033`, `zh-CN` with `:2052`). The installer's own language is set with `TRANSFORMS=:1033` (English) or `TRANSFORMS=:2052` (Chinese) — pass it together with `LANG` |
| `TRAYAUTOSTART` | `0` — do not start the icon at user sign-in; default `1` |
| `INSTALLFOLDER` | installation folder; default `C:\Program Files\zaprett` |
| `REMOVEDATA` | on uninstall: `1` — also delete `C:\ProgramData\zaprett` |

`SERVICES`, `AUTOSTART` and `LANG` apply only to the **first** installation: an upgrade does not change the user's
settings.

**Who can control the bypass.** Any user of the computer can see the state; changing settings and turning the bypass
on or off is allowed to administrators and members of the local group "zaprett Operators" (the user who installed the
app is added automatically). To add another user:

```powershell
net localgroup "zaprett Operators" UserName /add
```

## 10. Command line: `zaprett.exe`

`zaprett.exe` is in the installation folder and is not added to `PATH`. Call it by its full path:

```powershell
& "C:\Program Files\zaprett\zaprett.exe" status
```

Main commands (full reference: `zaprett.exe help`):

```text
status                          state
start | stop | restart          the bypass now (autostart unchanged)
enable | disable                turn on/off now and after Windows starts
autostart on|off                turn the bypass on after Windows starts or not (now unchanged)
check                           check the settings without starting
wizard apply youtube discord    enable services, as in the wizard
list enable|disable <id>        turn a list on/off
strategy set|show <id>          select / show a strategy
mode whitelist|blacklist        list mode
engine winws|winws2             engine
test start --quick [--apply-if-better]   automatic selection;  test status — progress and results
probe | probe status            check the sites now
diagnose | diagnose status      how the provider blocks
conflicts                       interfering programs
dns status | dns setup | dns off   encrypted DNS (Windows 11)
log --tail 50                   service and engine log
diag [--full]                   diagnostic report
settings get | settings set < json
```

Flags: `--json` — JSON output, `--quiet` — print nothing on success, `--lang ru|en|zh-CN` — output language (default:
the language from the settings). Exit codes: `0` success, `1` command error, `2` invalid arguments, `3` service
unavailable.

**Input through stdin.** `settings set` and `sources save` take a JSON object; `strategy save <id>` and
`user set <id>` take text. In PowerShell:

```powershell
$z = "C:\Program Files\zaprett\zaprett.exe"
'{"main":{"quic_block":true}}' | & $z settings set                    # turn QUIC blocking on
'{"ui":{"language":"en"}}' | & $z settings set                        # change the language
Get-Content .\my-strategy.txt -Raw | & $z strategy save user-my        # own strategy (name starts with user-)
Get-Content .\sites.txt -Raw | & $z user set user-hosts                # the "My sites" list
```

No need to pick an input encoding: UTF-8 (with or without BOM), UTF-16 and the console code page are recognised
automatically. Commands that change something need administrator rights or membership in "zaprett Operators".

## 11. Troubleshooting

**A site does not open although the bypass is on.**

1. On the home page click **Check now**.
2. Open **Strategies** and run automatic selection (**Quick**, then **Full** if needed). The bypass keeps working during
   selection.
3. If no strategy helps — **Diagnostics → How the provider blocks → Find out**. For DNS spoofing turn on encrypted DNS
   (Windows 11); for blocking by IP address zaprett cannot help — you need a VPN.
4. If the site is not in any list, add it to **Lists → Own lists → My sites**.

**"Not enough rights: administrator rights or membership in the "zaprett Operators" group are needed."** Your account
is not in that group — ask an administrator to add it (section 9). If you have just been added and the message stays,
sign out of Windows and sign in again.

**"The zaprett service is not running".** Click **Start the service** in the zaprett window (Windows asks for
administrator confirmation) or start the "zaprett" service in the Services console (`services.msc`). If the service
keeps stopping, reinstall zaprett.

**The antivirus flags WinDivert or `winws.exe`, or deletes them.** The WinDivert driver can intercept traffic, so some
antivirus products flag it as a "potentially unwanted application" or a "hack tool". The engine files are taken
unchanged from the official zapret release. If the antivirus deleted them (zaprett warns that the bypass engine is not
installed), add the installation folder (`C:\Program Files\zaprett`) to the antivirus exclusions and reinstall zaprett.

**Conflicting programs.** **GoodbyeDPI**, another copy of **zapret** and other programs with their own WinDivert must
not run together with zaprett: two interceptors get in each other's way. Stop and remove their services (for
GoodbyeDPI, its `remove_service.cmd`) and restart the computer. **A VPN** may route traffic around the bypass or bypass
blocks itself, so zaprett's checks may give different results while it is on. What was found is listed in
**Diagnostics → Conflicting programs**.

**YouTube opens in one browser but not in another.** Turn on **Settings → Block QUIC**.

**You need to send details for analysis.** **Diagnostics → Diagnostic report** → copy or save. The service log is also
at `C:\ProgramData\zaprett\logs\zaprett.log`.

## 12. Privacy

- zaprett **sends nothing** about you or your computer: no telemetry, no accounts, no registration.
- The app goes online by itself only to open the test addresses of the enabled services (site checks, monitor,
  automatic selection, diagnostics), to download the subscriptions you enabled, and to refresh the zaprett catalogue of
  strategies and lists on GitHub.
- In the diagnostic report the parameters of subscription URLs are removed, so tokens do not get into it. The report is
  never sent anywhere by itself — you decide whom to give it to.

## 13. What was verified and known limitations

**Verified** on clean systems: **Windows 10 LTSC 2021 (build 19044)** and **Windows 11 24H2** — installation from a
single MSI with no extra components, the setup wizard, the bypass itself, isolated automatic selection (the main bypass
is not restarted), uninstallation.

**Limitations of 0.1.1:**

- the MSI is not signed with a code-signing certificate — SmartScreen shows a warning (section 4);
- no automatic updates yet — install a new version with a new MSI (section 8);
- encrypted DNS can be turned on from zaprett only on Windows 11;
- x64 only; ARM64 and Windows 7/8.1 are not supported;
- no per-program filter: the bypass applies to the listed sites whichever program opens them;
- traffic this computer shares with other devices is not processed;
- coexistence with the various VPNs and antivirus products has not been fully tested.

Building from source and releasing: [docs/BUILD-WIN.md](docs/BUILD-WIN.md) (in Russian); design:
[docs/ARCHITECTURE-WIN.md](docs/ARCHITECTURE-WIN.md) (in Russian).

## Credits and licenses

- [bol-van/zapret](https://github.com/bol-van/zapret) and [bol-van/zapret2](https://github.com/bol-van/zapret2) — the
  `winws` and `winws2` engines;
- [WinDivert](https://github.com/basil00/WinDivert) — the packet interception driver (LGPL v3 / GPL v2);
- [CherretGit/zaprett-app](https://github.com/CherretGit/zaprett-app) — the original manager and the model of strategies
  and lists; for strategies and lists see the [main README](../README.en.md#credits-and-licenses).

zaprett code is MIT licensed ([LICENSE](../LICENSE)).
