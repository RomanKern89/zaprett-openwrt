#!/bin/bash
# The release tag must name exactly the version the packages are built with:
#
#   bash tools/ci/check-release-tag.sh v1.0.0-r1
#
# Expected: "v<PKG_VERSION>-r<PKG_RELEASE>" of packages/zaprett/Makefile, and packages/luci-app-zaprett/Makefile
# must carry the same PKG_VERSION and PKG_RELEASE (bundles are named after zaprett, docs/BUILD.md section 7).
# Prints the version (without "v") on success.
set -euo pipefail
. "$(dirname "$0")/lib.sh"

TAG=${1:?usage: check-release-tag.sh <tag>}
want=""
for pkg in zaprett luci-app-zaprett; do
	mk="$CI_ROOT/packages/$pkg/Makefile"
	[ -f "$mk" ] || ci_die "$mk is missing"
	v="$(mk_var "$mk" PKG_VERSION)-r$(mk_var "$mk" PKG_RELEASE)"
	[[ "$v" =~ ^[0-9]+(\.[0-9]+)*-r[0-9]+$ ]] || ci_die "$pkg: version '$v' is not <digits>(.<digits>)*-r<N>"
	if [ -z "$want" ]; then
		want="$v"
	elif [ "$v" != "$want" ]; then
		ci_die "zaprett is $want but $pkg is $v: bump both Makefiles"
	fi
done
[ "$TAG" = "v$want" ] || ci_die "tag $TAG does not match the package version v$want"
echo "$want"
