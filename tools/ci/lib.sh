#!/bin/bash
# Shared helpers of the tools/ci scripts (sourced, not executed). Same code runs in GitHub Actions
# (ubuntu-24.04 runners) and on the build VM: bash, coreutils, curl, tar, git, docker.

CI_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
# shellcheck disable=SC2034  # used by the scripts that source this file
CI_ROOT=$(cd "$CI_DIR/../.." && pwd)
CI_CACHE=${ZAPRETT_CI_CACHE:-$HOME/.cache/zaprett-ci}

ci_log() { printf '[%s] %s\n' "$(date '+%H:%M:%S')" "$*"; }
ci_die() { ci_log "FAIL: $*" >&2; exit 1; }

# Value of "NAME:=value" in an OpenWrt package Makefile, with $(PKG_VERSION) expanded.
mk_var() { # makefile name
	local ver val
	ver=$(sed -n 's/^PKG_VERSION:=//p' "$1" | head -n 1)
	val=$(sed -n "s/^$2:=//p" "$1" | head -n 1)
	printf '%s\n' "${val//\$(PKG_VERSION)/$ver}"
}

# Download the pinned release archive of an engine package (zaprett-nfqws, zaprett-nfqws2) into the cache and
# check it against PKG_HASH of the Makefile. Prints the archive path. A cached file is re-checked every time.
engine_archive() { # makefile
	local mk="$1" name url hash file got
	name=$(mk_var "$mk" PKG_SOURCE)
	url=$(mk_var "$mk" PKG_SOURCE_URL)
	hash=$(mk_var "$mk" PKG_HASH)
	[ -n "$name" ] && [ -n "$url" ] && [ ${#hash} = 64 ] || ci_die "PKG_SOURCE/PKG_SOURCE_URL/PKG_HASH not found in $mk"
	mkdir -p "$CI_CACHE"
	file="$CI_CACHE/$name"
	if [ -f "$file" ] && [ "$(sha256sum "$file" | cut -d ' ' -f 1)" = "$hash" ]; then
		printf '%s\n' "$file"
		return 0
	fi
	ci_log "download $url$name" >&2
	curl -fsSL --retry 3 --retry-delay 5 -o "$file.part" "$url$name" || ci_die "download of $url$name failed"
	got=$(sha256sum "$file.part" | cut -d ' ' -f 1)
	if [ "$got" != "$hash" ]; then
		rm -f "$file.part"
		ci_die "sha256 of $name is $got, Makefile PKG_HASH is $hash"
	fi
	mv "$file.part" "$file"
	ci_log "sha256 $name = PKG_HASH ($hash)" >&2
	printf '%s\n' "$file"
}
