#!/bin/sh
# SPDX-License-Identifier: MIT
#
# Self-check of the rpcd plugin luci.zaprett with the mock CLI.
# Runs on an OpenWrt test system in a scratch directory only; it does not
# install anything and does not touch rpcd, /etc, /usr or /www.
#
# Usage: sh run-plugin-tests.sh <dir>
# <dir> must contain luci.zaprett, mock-zaprett.uc and plugin-test.uc.

set -eu

DIR=${1:?usage: run-plugin-tests.sh <dir>}
cd "$DIR"

rm -rf run mock-state PWNED1 PWNED2 PWNED3 luci.zaprett.test luci.zaprett.nocli zaprett-mock
mkdir -m 700 run

printf '#!/bin/sh\nMOCK_STATE=%s/mock-state/state.json exec ucode %s/mock-zaprett.uc "$@"\n' "$DIR" "$DIR" > zaprett-mock
chmod 755 zaprett-mock

sed -e "s|^const CLI = '/usr/bin/zaprett';|const CLI = '$DIR/zaprett-mock';|" \
    -e "s|^const TMPDIR = '/var/run/luci-zaprett';|const TMPDIR = '$DIR/run';|" \
    luci.zaprett > luci.zaprett.test

# Both substitutions must succeed, otherwise the tests would talk to the real CLI.
[ "$(grep -c "^const CLI = '$DIR/zaprett-mock';" luci.zaprett.test)" = 1 ] || { echo "FAIL patch CLI"; exit 2; }
[ "$(grep -c "^const TMPDIR = '$DIR/run';" luci.zaprett.test)" = 1 ] || { echo "FAIL patch TMPDIR"; exit 2; }
[ "$(grep -v '^[[:space:]]*//' luci.zaprett.test | grep -c "/usr/bin/zaprett\|/var/run/luci-zaprett")" = 0 ] || { echo "FAIL real paths left"; exit 2; }

sed -e "s|^const CLI = '$DIR/zaprett-mock';|const CLI = '/nonexistent/zaprett';|" luci.zaprett.test > luci.zaprett.nocli
[ "$(grep -c "^const CLI = '/nonexistent/zaprett';" luci.zaprett.nocli)" = 1 ] || { echo "FAIL patch nocli"; exit 2; }

rc=0
ucode plugin-test.uc "$DIR" || rc=$?
exit "$rc"
