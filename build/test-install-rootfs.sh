#!/bin/bash
# Smoke test of the release bundles and install.sh in official OpenWrt rootfs containers (openwrt/rootfs).
# NOT a router test: no procd/netifd/firewall, but real busybox ash, real apk/opkg, real OpenWrt package feeds.
# Runs on the build VM after build.sh:   bash test-install-rootfs.sh [WORK]
#
# Checks per series (x86_64 bundle):
#   positive: install.sh installs zaprett, zaprett-nfqws, luci-app-zaprett, luci-i18n-zaprett-ru from the bundle feed;
#             the installed nfqws binary starts (--version); second run goes through the upgrade path;
#             --uninstall removes the packages, --purge removes the key.
#   negative: bundle for another architecture, bundle for the other series, corrupted signed index,
#             a different pre-installed zaprett key - each must be refused with a non-zero exit code.
#   --feed:   the online feed line appears (default GitHub Pages URL and a local file:// copy of the feed),
#             the package manager loads the signed local feed and rejects it once corrupted,
#             a later install.sh without --feed keeps the line, --uninstall removes only that line;
#             without --feed no feed line is written.
set -uo pipefail

WORK=${1:-$HOME/zaprett-build}
# Optional: releases/ of a build with higher PKG_RELEASE (x86_64 is enough) to test the upgrade path.
UPGRADE_RELEASES=${2:-}
DIST="$WORK/dist"
T="$WORK/out/rootfs-test"
REPORT="$T/report.txt"
rm -rf "$T"
mkdir -p "$T"
: > "$REPORT"

declare -A IMAGE=([25.12]="openwrt/rootfs:x86_64-v25.12.4" [24.10]="openwrt/rootfs:x86_64-v24.10.8")
FAILS=0

ok() { echo "OK   $*" | tee -a "$REPORT"; }
bad() { echo "FAIL $*" | tee -a "$REPORT"; FAILS=$((FAILS + 1)); }

bundle_of() { # series arch
	ls "$DIST/releases/"zaprett-*-"$1"-"$2".tar.gz 2>/dev/null | head -n 1
}

# run_case <name> <series> <expect: 0|fail> <script>: script runs in a fresh container with /b (bundles) mounted.
run_case() {
	local name="$1" series="$2" expect="$3" script="$4" rc mounts=(-v "$DIST/releases:/b:ro")
	[ -n "$UPGRADE_RELEASES" ] && mounts+=(-v "$UPGRADE_RELEASES:/u:ro")
	docker run --rm --name "zaprett-rootfs-$$" "${mounts[@]}" "${IMAGE[$series]}" /bin/ash -c "$script" \
		> "$T/$series-$name.log" 2>&1
	rc=$?
	if [ "$expect" = 0 ] && [ "$rc" = 0 ]; then
		ok "$series $name (rc=0)"
	elif [ "$expect" = fail ] && [ "$rc" != 0 ]; then
		ok "$series $name refused as expected (rc=$rc): $(grep -m1 'ОШИБКА' "$T/$series-$name.log")"
	else
		bad "$series $name: rc=$rc, expected $expect; log $T/$series-$name.log"
		tail -n 25 "$T/$series-$name.log"
	fi
}

PREP='set -e; mkdir -p /var/lock /var/run /tmp; cd /tmp'
# Flip one byte in the middle of the zaprett-nfqws package and drop it from SHA256SUMS,
# so that only the package manager can notice the change.
TAMPER='f=$(ls feed/zaprett-nfqws[-_]7*); orig=$(sha256sum < $f); half=$(( $(wc -c < $f) / 2 )); printf "\252" | dd of=$f bs=1 seek=$half conv=notrunc 2>/dev/null; [ "$(sha256sum < $f)" != "$orig" ] || printf "\125" | dd of=$f bs=1 seek=$half conv=notrunc 2>/dev/null; [ "$(sha256sum < $f)" != "$orig" ] || exit 7; sed -i "/zaprett-nfqws[-_]7/d" SHA256SUMS'

for series in 25.12 24.10; do
	docker pull -q "${IMAGE[$series]}" > /dev/null || { bad "$series: cannot pull ${IMAGE[$series]}"; continue; }
	b=$(bundle_of "$series" x86_64)
	other_arch=$(bundle_of "$series" aarch64_cortex-a53)
	other_series=$(bundle_of "$([ "$series" = 25.12 ] && echo 24.10 || echo 25.12)" x86_64)
	[ -n "$b" ] || { bad "$series: no x86_64 bundle in $DIST/releases"; continue; }
	bn=$(basename "$b" .tar.gz)
	if [ "$series" = 25.12 ]; then
		CHECK='apk info -e zaprett zaprett-nfqws luci-app-zaprett luci-i18n-zaprett-ru'
		GONE='for p in zaprett zaprett-nfqws luci-app-zaprett luci-i18n-zaprett-ru; do if apk info -e $p; then echo still installed $p; exit 1; fi; done'
		KEYGONE='if [ -e /etc/apk/keys/zaprett.pem ]; then echo key left; exit 1; fi'
		FEED_IS='feed_is() { [ "$(cat /etc/apk/repositories.d/zaprett.list 2>/dev/null)" = "$1/25.12/x86_64/packages.adb" ] || { echo "feed line: $(cat /etc/apk/repositories.d/zaprett.list 2>&1)"; exit 1; }; }'
		FEED_NONE='if [ -e /etc/apk/repositories.d/zaprett.list ]; then echo "feed file exists"; exit 1; fi'
		FEED_UPDATE='rc=0; apk update > /tmp/upd.log 2>&1 || rc=$?; echo "apk update rc=$rc"; cat /tmp/upd.log'
		FEED_LOADED='apk policy zaprett | tee /tmp/policy.log; grep -q "file:///tmp/pages/25.12/x86_64/packages.adb" /tmp/policy.log'
		KEEP_PRE=':'
		KEEP_POST='[ -s /etc/apk/repositories.d/distfeeds.list ] || { echo "distfeeds.list was removed"; exit 1; }'
		FEED_CORRUPT='printf "\377" | dd of=/tmp/pages/25.12/x86_64/packages.adb bs=1 seek=300 conv=notrunc 2>/dev/null'
		CORRUPT='ORIG=$(sha256sum < feed/packages.adb); printf "\377" | dd of=feed/packages.adb bs=1 seek=300 conv=notrunc 2>/dev/null; [ "$(sha256sum < feed/packages.adb)" != "$ORIG" ] || exit 7; sed -i "/feed\/packages.adb/d" SHA256SUMS'
		FOREIGN='mkdir -p /etc/apk/keys; printf "%s\n" "-----BEGIN PUBLIC KEY-----" "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE7dbvKJSkqnbe7kf2rlmFWSnHKK3r" "tU5A6UUqUq7CVCDM2ZfDB9ttsG4y4dcSMIqF3dpS9qXh4LG30WJ8Fc2+bg==" "-----END PUBLIC KEY-----" > /etc/apk/keys/zaprett.pem'
	else
		CHECK='for p in zaprett zaprett-nfqws luci-app-zaprett luci-i18n-zaprett-ru; do opkg status $p | grep -q "^Status: install .* installed" || { echo "missing $p"; exit 1; }; done'
		GONE='for p in zaprett zaprett-nfqws luci-app-zaprett luci-i18n-zaprett-ru; do if opkg status $p | grep -q installed; then echo still installed $p; exit 1; fi; done'
		KEYGONE='if [ -e /etc/opkg/keys/$(usign -F -p keys/zaprett-usign.pub) ]; then echo key left; exit 1; fi'
		FEED_IS='feed_is() { [ "$(grep "^src/gz zaprett " /etc/opkg/customfeeds.conf)" = "src/gz zaprett $1/24.10/x86_64" ] || { echo "feed lines: $(grep zaprett /etc/opkg/customfeeds.conf)"; exit 1; }; }'
		FEED_NONE='if grep -q "^src/gz zaprett " /etc/opkg/customfeeds.conf 2>/dev/null; then echo "feed line exists"; exit 1; fi'
		FEED_UPDATE='rc=0; opkg update > /tmp/upd.log 2>&1 || rc=$?; echo "opkg update rc=$rc"; cat /tmp/upd.log'
		FEED_LOADED='L=$(awk "\$1 == \"lists_dir\" { print \$3 }" /etc/opkg.conf); ls -l $L; [ -s $L/zaprett ] && gzip -dc $L/zaprett | grep -q "^Package: zaprett$"'
		KEEP_PRE='echo "# keep-me" >> /etc/opkg/customfeeds.conf'
		KEEP_POST='grep -q "^# keep-me$" /etc/opkg/customfeeds.conf || { echo "other lines of customfeeds.conf were removed"; exit 1; }'
		FEED_CORRUPT='echo "Description: tampered" >> /tmp/pages/24.10/x86_64/Packages; gzip -9nc /tmp/pages/24.10/x86_64/Packages > /tmp/pages/24.10/x86_64/Packages.gz'
		CORRUPT='echo "Description: tampered" >> feed/Packages; gzip -9nc feed/Packages > feed/Packages.gz; sed -i "/feed\/Packages/d" SHA256SUMS'
		FOREIGN='fp=$(usign -F -p keys/zaprett-usign.pub); mkdir -p /etc/opkg/keys; echo "untrusted comment: foreign" > /etc/opkg/keys/$fp; echo "RWSfakefakefakefakefakefakefakefakefakefakefakefake" >> /etc/opkg/keys/$fp'
	fi

	run_case install "$series" 0 "$PREP; tar -xzf /b/$bn.tar.gz; cd $bn; sh install.sh; $CHECK; $FEED_NONE; /usr/libexec/zaprett/nfqws --version; ls -l /usr/libexec/zaprett/nfqws; sh install.sh; $CHECK; sh install.sh --uninstall; $GONE; sh install.sh --uninstall --purge; $KEYGONE; if [ -e /etc/config/zaprett ]; then echo config left; exit 1; fi"
	if [ -n "$UPGRADE_RELEASES" ]; then
		ub=$(ls "$UPGRADE_RELEASES"/zaprett-*-"$series"-x86_64.tar.gz 2>/dev/null | head -n 1)
		if [ -n "$ub" ]; then
			ubn=$(basename "$ub" .tar.gz)
			if [ "$series" = 25.12 ]; then
				VER='awk "/^P:zaprett-nfqws\$/ { f = 1 } f && /^V:/ { print substr(\$0, 3); exit }" /lib/apk/db/installed'
			else
				VER='opkg status zaprett-nfqws | sed -n "s/^Version: //p"'
			fi
			run_case upgrade "$series" 0 "$PREP; tar -xzf /b/$bn.tar.gz; tar -xzf /u/$ubn.tar.gz; cd /tmp/$bn; sh install.sh; v1=\$($VER); cd /tmp/$ubn; sh install.sh; v2=\$($VER); echo \"nfqws version: \$v1 -> \$v2\"; [ \"\$v1\" != \"\$v2\" ] && [ \"\${v2%-r*}\" = \"\${v1%-r*}\" ] && [ \"\${v2##*-r}\" -gt \"\${v1##*-r}\" ]"
			# The same upgrade through the online feed (--feed): the newer build is published as the feed and the
			# plain package manager commands of docs/INSTALL.md must pick it up.
			if [ "$series" = 25.12 ]; then
				UPG='apk update && apk upgrade'
			else
				UPG='opkg update && opkg upgrade zaprett zaprett-nfqws luci-app-zaprett luci-i18n-zaprett-ru'
			fi
			run_case feed-upgrade "$series" 0 "$PREP; $FEED_IS; tar -xzf /b/$bn.tar.gz; tar -xzf /u/$ubn.tar.gz; mkdir -p /tmp/pages/$series/x86_64; cp /tmp/$ubn/feed/* /tmp/pages/$series/x86_64/; cd /tmp/$bn; sh install.sh --feed-url file:///tmp/pages; feed_is file:///tmp/pages; v1=\$($VER); $UPG; v2=\$($VER); echo \"nfqws version via the online feed: \$v1 -> \$v2\"; [ \"\$v1\" != \"\$v2\" ] && [ \"\${v2%-r*}\" = \"\${v1%-r*}\" ] && [ \"\${v2##*-r}\" -gt \"\${v1##*-r}\" ]"
		else
			bad "$series: no x86_64 bundle in $UPGRADE_RELEASES"
		fi
	fi
	PAGES="https://romankern89.github.io/zaprett-openwrt"
	# Online feed with the default URL: the line format is checked; the Pages site may not exist yet, so the
	# package manager's download error is only shown in the log.
	run_case feed-default "$series" 0 "$PREP; $FEED_IS; tar -xzf /b/$bn.tar.gz; cd $bn; sh install.sh --feed; feed_is $PAGES; sh install.sh --feed; feed_is $PAGES; sh install.sh; feed_is $PAGES; $FEED_UPDATE; sh install.sh --uninstall; $FEED_NONE; $GONE"
	# Online feed served from a local copy of the bundle feed (same layout as GitHub Pages: <series>/<arch>/).
	run_case feed-local "$series" 0 "$PREP; $FEED_IS; $KEEP_PRE; mkdir -p /tmp/pages/$series/x86_64; tar -xzf /b/$bn.tar.gz; cp $bn/feed/* /tmp/pages/$series/x86_64/; cd $bn; sh install.sh --feed-url file:///tmp/pages; feed_is file:///tmp/pages; $FEED_UPDATE; $FEED_LOADED; sh install.sh; feed_is file:///tmp/pages; $CHECK; $FEED_CORRUPT; $FEED_UPDATE; if ( $FEED_LOADED ); then echo 'corrupted online feed was accepted'; exit 1; fi; echo 'corrupted online feed rejected'; sh install.sh --uninstall; $FEED_NONE; $GONE; $KEEP_POST"
	run_case nfqws2 "$series" 0 "$PREP; tar -xzf /b/$bn.tar.gz; cd $bn; sh install.sh --with-nfqws2; $CHECK; /usr/libexec/zaprett/nfqws2 --version; ls /usr/share/zaprett/lua"
	if [ -n "$other_arch" ]; then
		run_case wrong-arch "$series" fail "$PREP; tar -xzf /b/$(basename "$other_arch"); cd $(basename "$other_arch" .tar.gz); sh install.sh"
	else
		bad "$series: no aarch64_cortex-a53 bundle for the wrong-arch control"
	fi
	if [ -n "$other_series" ]; then
		run_case wrong-series "$series" fail "$PREP; tar -xzf /b/$(basename "$other_series"); cd $(basename "$other_series" .tar.gz); sh install.sh"
	fi
	run_case corrupt-index "$series" fail "$PREP; tar -xzf /b/$bn.tar.gz; cd $bn; $CORRUPT; sh install.sh"
	# A package file changed after signing (index untouched): the package manager must reject it by hash.
	run_case tampered-package "$series" fail "$PREP; tar -xzf /b/$bn.tar.gz; cd $bn; $TAMPER; sh install.sh"
	run_case foreign-key "$series" fail "$PREP; tar -xzf /b/$bn.tar.gz; cd $bn; $FOREIGN; sh install.sh"
done

echo "rootfs install test: $(grep -c '^OK' "$REPORT") OK, $FAILS FAIL (report $REPORT)"
[ "$FAILS" = 0 ]
