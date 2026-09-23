#!/bin/sh
# Runs INSIDE an openwrt/rootfs container (busybox ash), started by tools/ci/ucode-tests.sh.
# Reproduces the router test run (tools/lab/labtest.py): the whole packages/ tree in /tmp/zta, nfqws at
# /usr/libexec/zaprett/nfqws, local web server on 127.0.0.1:80 serving /www (LuCI's ui.js), then
#   sh packages/zaprett/tests/run.sh ...               -> "TOTAL pass=N fail=M"
#   sh packages/luci-app-zaprett/tests/run-plugin-tests.sh ...  -> "RESULT passed=N failed=M"
# Mounts: /src (ro) with packages/ and engine/nfqws.

BASE=/tmp/zta
NFQWS=/usr/libexec/zaprett/nfqws

mkdir -p /var/lock /var/run /var/log /tmp
rm -rf "$BASE"
mkdir -p "$BASE"
# cp -a keeps mtime: nfqws treats a list file with mtime 0 as inaccessible (ZERR-024).
cp -a /src/packages "$BASE/" || { echo "CI-ERROR copy of packages failed"; exit 2; }
mkdir -p /usr/libexec/zaprett
cp -a /src/engine/nfqws "$NFQWS" || { echo "CI-ERROR copy of nfqws failed"; exit 2; }
echo "CI-INFO OpenWrt $(. /etc/openwrt_release; echo "$DISTRIB_RELEASE $DISTRIB_ARCH")"
echo "CI-INFO nfqws: $("$NFQWS" --version 2>&1 | head -n 1)"
# test_nft.uc adds one parser-only check when the host kernel has no nft_queue module loaded (router: loaded).
if [ -d /sys/module/nft_queue ]; then
	echo "CI-INFO host kernel: nft_queue loaded (as on a router)"
else
	echo "CI-INFO host kernel: nft_queue NOT loaded - test_nft runs its parser-only branch (+1 check)"
fi

# The router has ubusd and uhttpd running; tests use both (ubus module, uclient-fetch against 127.0.0.1).
[ -x /sbin/ubusd ] && { /sbin/ubusd > /dev/null 2>&1 & }
if [ -f /www/luci-static/resources/ui.js ]; then
	uhttpd -f -p 127.0.0.1:80 -h /www > /dev/null 2>&1 &
else
	echo "CI-INFO no /www/luci-static/resources/ui.js: the probe download limit check will be skipped"
fi
i=0
while [ $i -lt 50 ] && ! uclient-fetch -q -O /dev/null http://127.0.0.1/ 2>/dev/null; do
	i=$((i + 1))
	sleep 0.1
done

echo "===== backend tests"
sh "$BASE/packages/zaprett/tests/run.sh" "$BASE/packages/zaprett" "$BASE/work" "$NFQWS" 2>&1
echo "CI-RC backend $?"

echo "===== rpcd plugin tests"
plug="$BASE/packages/luci-app-zaprett/tests"
cp "$BASE/packages/luci-app-zaprett/root/usr/share/rpcd/ucode/luci.zaprett" "$plug/" \
	&& sh "$plug/run-plugin-tests.sh" "$plug" 2>&1
echo "CI-RC plugin $?"
