# Code signing policy

**English** | [Русский](CODE_SIGNING.ru.md)

This page is the code signing policy of **zaprett for Windows**, the Windows part of the
[zaprett-openwrt](https://github.com/RomanKern89/zaprett-openwrt) project.

Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by
[SignPath Foundation](https://signpath.org/).

> **Status.** The project has applied, or is going to apply, for free code signing from SignPath Foundation. Until the
> first signed release is published, the MSI files on the releases page are **not** Authenticode-signed; Windows
> SmartScreen may warn about them. Each release says in its notes whether it is signed. Releases up to and including
> `win-v0.1.2` are not signed.

## What is signed

A release of zaprett for Windows (`zaprett-<version>-x64.msi`, tag `win-v<version>`) is signed in one pass:

1. our own program files inside the MSI: `zaprett.exe`, `zaprett.dll`, `zaprett-svc.exe`, `zaprett-svc.dll`,
   `zaprett-ui.exe`, `zaprett-ui.dll`, `Zaprett.Core.dll`, `Zaprett.Ipc.dll`;
2. then the MSI itself.

Every signed file carries the product name `zaprett for Windows` and the version of the release. SignPath refuses to sign
a file whose version information does not match (see `.signpath/artifact-configuration.xml` in the repository).

## What is not signed

The installer also contains files from other projects. They are shipped exactly as their authors publish them and are
**not** signed with our certificate:

| Files | Project | Notes |
|-------|---------|-------|
| `engine\winws.exe`, `engine\cygwin1.dll` | [bol-van/zapret](https://github.com/bol-van/zapret) | pinned release, checked by SHA-256 at build time |
| `engine2\winws2.exe`, `engine2\cygwin1.dll`, `engine2\lua\*` | [bol-van/zapret2](https://github.com/bol-van/zapret2) | pinned release, checked by SHA-256 at build time |
| `WinDivert.dll`, `WinDivert64.sys` | [WinDivert](https://github.com/basil00/WinDivert) | the driver `WinDivert64.sys` is signed by its author |
| .NET runtime, Windows App SDK, other libraries | Microsoft and the .NET Foundation | most of them carry Microsoft's own signatures |

The exact versions and SHA-256 hashes of the engine files are pinned in `windows/engine/engine.lock.json`.

The OpenWrt packages of this project are not Windows programs and are not covered by this policy.

## How a release is built and signed

- The MSI is built from the source code in this repository by GitHub Actions on GitHub-hosted runners
  (`.github/workflows/windows.yml`), started by pushing a `win-v<version>` tag.
- The workflow submits the unsigned MSI to SignPath. SignPath checks that it came from that workflow run of this
  repository before signing it.
- **Every signing request is approved manually** by an approver (see below). Nothing is signed without that approval.
- The workflow then checks the signatures of the MSI and of our files inside it, computes `SHA256SUMS` and the update
  manifest `update.json` from the signed MSI, and publishes the release.

## Team roles

The project currently has **one maintainer**, so the roles below are held by the same person.

| Role | Members |
|------|---------|
| Committers and reviewers | [RomanKern89](https://github.com/RomanKern89) (maintainer) |
| Approvers | [RomanKern89](https://github.com/RomanKern89) (maintainer) |

Changes from anyone else come as pull requests and are reviewed by the maintainer before they are merged. All members use
multi-factor authentication for GitHub and SignPath.

## Privacy policy

zaprett for Windows has **no telemetry, no analytics, no crash reporting and no accounts**. It does not send any
information about you, your computer or your traffic to the project or to anyone else. Its settings and logs stay on the
computer (`C:\ProgramData\zaprett`).

The program only makes the network requests that are needed for the functions it offers. Each of them is an ordinary
download or connection; none of them carries personal data:

| When | Where to | Default |
|------|----------|---------|
| Daily update of strategies and lists installed from the repository | the repository index set in the settings (default: `raw.githubusercontent.com`, repository `CherretGit/zaprett-repo`) and the files it lists | **on** by default; the person installing the program sees this switch on the last step of the setup wizard and can turn it off there or later in **Settings**, group **Updates** (setting `repo.autoupdate`) |
| Checking for a new version of the program | the releases of this project on GitHub | on, can be switched off in the settings; the in-app check is not active yet in version 0.1.2 |
| Updating list subscriptions | the addresses of the subscriptions you switch on | off; every subscription is off until you enable it |
| Site checks, diagnostics, automatic strategy selection | the sites of the services you select, and the DNS-over-HTTPS resolvers `dns.google` and `cloudflare-dns.com` to compare DNS answers | only when you start them |
| Connection monitoring while the bypass is running | the sites of the services you selected and a few domains of the lists you use | on while the bypass is on; can be switched off |
| Encrypted DNS (Windows 11) | `cloudflare-dns.com`, through the Windows DNS client | off until you enable it |

Like any download, these requests reveal your IP address to the server you connect to; the privacy policies of those
services (GitHub, Cloudflare, Google, the list providers) apply to them.

In other words: this program will not transfer any information to other networked systems unless specifically requested
by the user or the person installing or operating it.

## How to verify a signed release

1. Check the hash: download `SHA256SUMS` from the same release and compare, for example in PowerShell:

   ```powershell
   (Get-FileHash .\zaprett-<version>-x64.msi -Algorithm SHA256).Hash
   ```

2. Check the signature of the MSI: right-click the file, **Properties**, **Digital Signatures**. The signer is
   **SignPath Foundation**. Or in PowerShell:

   ```powershell
   Get-AuthenticodeSignature .\zaprett-<version>-x64.msi | Format-List Status, SignerCertificate, TimeStamperCertificate
   ```

   `Status` must be `Valid`.

3. After installation, the program files in `C:\Program Files\zaprett` (`zaprett.exe`, `zaprett-svc.exe`,
   `zaprett-ui.exe` and their DLLs) carry the same signature.

In-app updates are additionally protected: the update manifest `update.json` is signed with the project's own ed25519
key (`windows/tools/update-sign/keys/update-ed25519.pub`) and contains the SHA-256 of the MSI.

## Reporting a problem

If you think a file signed for this project violates this policy, open an issue in the
[repository](https://github.com/RomanKern89/zaprett-openwrt/issues). Violations of the SignPath Foundation terms can
also be reported to SignPath: [support@signpath.io](mailto:support@signpath.io).
