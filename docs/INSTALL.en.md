# Installing zaprett on an OpenWrt router

[Русский](INSTALL.md) | **English**

zaprett is installed from a ready-made **bundle**, an archive named `zaprett-<version>-<OpenWrt>-<architecture>.tar.gz`.
Inside: the packages (`feed/`), a signed package index, the public key (`keys/`) and the `install.sh` installer.

Supported releases:

| OpenWrt | Package manager | Which bundle to take |
|---|---|---|
| 25.12.x | apk | `zaprett-…-25.12-<architecture>.tar.gz` |
| 24.10.x | opkg | `zaprett-…-24.10-<architecture>.tar.gz` |

Other OpenWrt releases (23.05 and older, snapshots) are not supported.

---

## 1. What you need

- a router with OpenWrt 24.10 or 25.12 and **internet access**: dependencies (`ucode`, `nftables`, `kmod-nft-queue`,
  `luci-base` and others) are downloaded from the official OpenWrt repositories;
- at least **2 MB** of free flash: the zaprett packages take 0.6–0.8 MB (up to 1.7 MB with nfqws2), plus the
  dependencies that are not in the firmware yet (for example `kmod-nft-queue`). Check with `df -h /overlay` or in
  LuCI → System → Software (the free space is shown at the top of the page);
- access to the router: SSH (recommended) or only the LuCI web interface, see section 4.

## 2. Find your OpenWrt release and architecture

**Over SSH:**

```sh
cat /etc/openwrt_release          # DISTRIB_RELEASE: the release, DISTRIB_ARCH: the architecture
cat /etc/apk/arch                 # OpenWrt 25.12: package architecture
opkg print-architecture           # OpenWrt 24.10: the line with the highest number, e.g. "arch mipsel_24kc 10"
```

**Through LuCI:** System → Software → the repository configuration button (apk on 25.12, opkg on 24.10). The
repository addresses contain a `…/packages/<architecture>/…` part: that is the architecture you need (for example
`aarch64_cortex-a53`, `mipsel_24kc`, `x86_64`). The firmware release is on Status → Overview.

Examples: routers on MediaTek Filogic (Xiaomi AX3000T and others) are `aarch64_cortex-a53`; routers on MediaTek MT7621
are `mipsel_24kc`; an x86 virtual machine is `x86_64`.

Download the bundle **for exactly your OpenWrt release and architecture**. The installer checks this itself and refuses
a bundle that does not match.

Not supported: ARMv4/v5 (`arm926ej-s`, `fa526`, `xscale`), `loongarch64`, `mips64el`, `powerpc64` and `riscv64`.

## 3. Installing over SSH (OpenWrt 25.12 and 24.10 alike)

1. Copy the bundle to `/tmp` on the router (it is RAM; the file disappears after a reboot). From Windows 10/11 or
   Linux/macOS in a terminal:

   ```sh
   scp -O zaprett-1.1.0-r1-25.12-aarch64_cortex-a53.tar.gz root@192.168.1.1:/tmp/
   ```

   `-O` is needed because OpenWrt has no sftp server. Instead of `scp` you can use WinSCP (protocol **SCP**).
2. Log in to the router: `ssh root@192.168.1.1`.
3. Unpack the bundle and run the installer:

   ```sh
   cd /tmp
   tar -xzf zaprett-1.1.0-r1-25.12-aarch64_cortex-a53.tar.gz
   cd zaprett-1.1.0-r1-25.12-aarch64_cortex-a53
   sh install.sh
   ```

   The installer:
   - checks that the bundle matches the router's OpenWrt release and architecture and that the bundle files are intact;
   - adds the zaprett public key (`/etc/apk/keys/zaprett.pem` or `/etc/opkg/keys/<fingerprint>`);
   - refreshes the OpenWrt package lists (`apk update` / `opkg update`);
   - installs `zaprett`, `zaprett-nfqws`, `luci-app-zaprett` and `luci-i18n-zaprett-ru` from the bundle's feed; the
     package manager verifies the feed signature with the zaprett key;
   - prints the address to open in the browser.

   The installer's messages are in Russian.
4. Open the address the installer printed, for example `http://192.168.1.1/cgi-bin/luci/admin/services/zaprett`
   (LuCI → **Services → zaprett**). If the menu item is missing, log out of LuCI and log in again.

The optional **nfqws2** engine (zapret2, Lua strategies) is installed with `sh install.sh --with-nfqws2`.

If the router does not run the OpenWrt release the bundle is built for, the installer stops. `--force` lets it
continue on a release mismatch (for example on a 25.12 snapshot) or when the zaprett key has changed, but that can
leave you with a broken package system. A wrong architecture or package manager is never overridden. All installer
options: `sh install.sh --help`.

## 4. Installing through the web interface only

### OpenWrt 24.10 (opkg)

LuCI can install uploaded `.ipk` files.

1. Unpack the bundle on your computer (7-Zip, WinRAR or `tar -xzf`). The packages are in the `feed/` folder.
2. LuCI → System → Software → **"Update lists…"** (needed for the dependencies).
3. There → **"Upload Package…"** and install the files **strictly in this order**:
   1. `zaprett-nfqws_72.13-r1_<architecture>.ipk`
   2. `zaprett_<version>_all.ipk`
   3. `luci-app-zaprett_<version>_all.ipk`
   4. `luci-i18n-zaprett-ru_<version>_all.ipk` (the Russian translation; optional for an English interface)
4. Log out of LuCI, log in again and open **Services → zaprett**.

This way the feed signature is not checked (opkg does not check signatures of manually uploaded files), so take the
bundle only from the official release.

### OpenWrt 25.12 (apk)

Uploading a package through LuCI on 25.12 runs `apk add` **without** `--allow-untrusted` (checked in the source of
`luci-app-package-manager` 25.12.5), so a single package signed with a foreign key cannot be installed that way, and
the zaprett key cannot be added from LuCI. Without an SSH client use a terminal in the browser:

1. LuCI → System → Software → "Update lists…", find and install **`luci-app-ttyd`**.
2. Open **Services → Terminal** and log in as `root`.
3. Download the bundle straight to the router (if the release has a direct link) and follow steps 3–4 of section 3:

   ```sh
   cd /tmp
   uclient-fetch -O zaprett.tar.gz "<bundle link from the release>"
   tar -xzf zaprett.tar.gz && cd zaprett-*/ && sh install.sh
   ```

   Without a direct link, copy the bundle to the router with WinSCP (section 3, step 1).

## 5. Checking the installation

```sh
apk info -e zaprett zaprett-nfqws luci-app-zaprett luci-i18n-zaprett-ru     # 25.12: prints the installed names
opkg status zaprett | grep Status                                             # 24.10: "install ok installed"
ls -l /usr/libexec/zaprett/nfqws
```

In LuCI, **Services → zaprett** opens. The zaprett pages use the LuCI language (System → System → "Language and
Style"); with "auto" they follow the browser. The source strings are English; the Russian translation is used only
when the interface language is Russian. To remove it: `apk del luci-i18n-zaprett-ru` /
`opkg remove luci-i18n-zaprett-ru`.

## 6. Updating

### From a new bundle

Download the new bundle and run `sh install.sh` from it, just like during installation. The installed zaprett
packages are updated and the settings are kept.

### Through the online feed (`--feed`)

If you add `--feed` during installation, the zaprett feed stays configured, and zaprett is then updated with the
regular package manager commands, like OpenWrt's own packages:

```sh
sh install.sh --feed                    # install from the bundle and add the online feed
                                        # (also later: running again with --feed breaks nothing)
sh install.sh --feed-url https://…      # the same with another feed address (for example a mirror)
```

The feed is the site `https://romankern89.github.io/zaprett-openwrt/<OpenWrt release>/<architecture>/` with the same
signed indexes as in the bundle. It is added like this:

| OpenWrt | Where it is written | Line |
|---|---|---|
| 25.12 (apk) | `/etc/apk/repositories.d/zaprett.list` | `https://romankern89.github.io/zaprett-openwrt/25.12/<arch>/packages.adb` |
| 24.10 (opkg) | a line in `/etc/opkg/customfeeds.conf` | `src/gz zaprett https://romankern89.github.io/zaprett-openwrt/24.10/<arch>` |

The zaprett key is the same as for a normal installation, so the package manager accepts only an index signed with
the zaprett key: a tampered or damaged feed is rejected.

Updating afterwards:

```sh
# OpenWrt 25.12
apk update && apk upgrade
# OpenWrt 24.10
opkg update && opkg upgrade zaprett zaprett-nfqws luci-app-zaprett luci-i18n-zaprett-ru
```

(Through LuCI: System → Software → "Update lists…", then the "Updates" tab.)

**If the feed site is unreachable** (no internet, github.io does not open): on 24.10 `opkg update` only reports a
download error. On 25.12 apk is stricter: while the feed is unreachable, `apk add` and `apk upgrade` of **any**
packages stop with "Not continuing due to stale/unavailable repositories". The way out: wait, temporarily remove
`/etc/apk/repositories.d/zaprett.list`, or add `--force-missing-repositories`, which apk itself suggests. The bundle
installer takes care of this: during installation it disables the online feed, installs only from the bundle and
then puts the feed line back.

To remove the online feed: `sh install.sh --uninstall` (removes the packages too), or delete
`/etc/apk/repositories.d/zaprett.list` (25.12) or the `src/gz zaprett …` line from `/etc/opkg/customfeeds.conf`
(24.10) by hand.

Without `--feed` the installer does not add the feed: the bundle's feed exists only during installation, and
`apk upgrade` / `opkg upgrade` do not update zaprett; only a new bundle does.

## 7. Uninstalling

```sh
cd /tmp/zaprett-*/                  # any unpacked bundle for your OpenWrt release
sh install.sh --uninstall           # remove the packages, keep the settings
sh install.sh --uninstall --purge   # remove the packages, /etc/config/zaprett, /etc/zaprett and the zaprett key
```

`--uninstall` also removes the online zaprett feed if it was added with `--feed`.

Without a bundle:

```sh
# OpenWrt 25.12
apk del luci-i18n-zaprett-ru luci-app-zaprett zaprett zaprett-nfqws zaprett-nfqws2
rm -f /etc/apk/keys/zaprett.pem /etc/apk/repositories.d/zaprett.list
# OpenWrt 24.10
opkg remove luci-i18n-zaprett-ru luci-app-zaprett zaprett zaprett-nfqws2 zaprett-nfqws
sed -i '/^src\/gz zaprett /d' /etc/opkg/customfeeds.conf
```

(Name `zaprett-nfqws2` only if you installed it; `opkg`/`apk` report a missing package otherwise.)
Through LuCI: System → Software → the "Installed" tab → remove the same packages in the same order.

## 8. After a firmware upgrade (sysupgrade)

The OpenWrt firmware does not contain the zaprett packages, so after `sysupgrade` or an upgrade through LuCI ("Flash
new firmware image…") **the zaprett packages are gone**. The settings (`/etc/config/zaprett`, `/etc/zaprett`) are kept
if "Keep settings" was checked during flashing.

What to do after the upgrade:

1. If OpenWrt was upgraded within the same branch (for example 25.12.4 → 25.12.5), copy the same bundle to the router
   again and run `sh install.sh`. The settings are picked up.
2. If the branch changed (24.10 → 25.12), take the bundle for the new branch: 25.12 has a different package manager.
3. The zaprett key may be gone too; the installer adds it again.
4. If the online feed was configured (`--feed`), run the installer with `--feed` again: the feed line is written again
   even if the firmware did not keep it.

**Attended Sysupgrade** (LuCI "Firmware upgrade", `owut`) builds the image on the OpenWrt server from official packages
only. There are no zaprett packages there: before such an upgrade uninstall zaprett (`sh install.sh --uninstall`, the
settings stay) or use a regular `sysupgrade`, and install zaprett again after the upgrade.

## 9. Common errors

The installer prints its messages in Russian; the table gives their meaning.

| Installer message (meaning) | What to do |
|---|---|
| "the bundle is built for architecture X, the router is Y" | Download the bundle for architecture Y (section 2). |
| "the bundle is for apk (OpenWrt 25.12), the router has opkg" | You need the `…-24.10-…` bundle (or the other way round). |
| "apk add / opkg install failed", with `kmod-nft-queue`, `ucode` or similar missing | The router has no internet or the lists are not updated. Check `ping downloads.openwrt.org` and repeat. |
| "UNTRUSTED signature" / "the feed signature check failed" | The bundle is damaged or tampered with. Download it again from the official release. |
| "the router already has a different key" | The bundle is signed with a different key than the one installed before. If you trust the source, `sh install.sh --force`. |
| `apk update`: "wget: exited with error 8" / `opkg update`: "Failed to download … zaprett-openwrt …" | The online feed is unreachable (no internet or the feed site is not published yet). Other repositories are not affected; the zaprett packages stay installed. |
| "No space left on device" | Not enough flash: remove packages you do not need or use extroot. |
