#!/bin/bash
# Runs INSIDE the OpenWrt SDK container (cwd /builder, user buildbot). Started by build/build.sh.
#
# Builds zaprett packages for one OpenWrt series and assembles one signed feed per architecture in /out/feed/<arch>.
# Mounts: /builder  persistent SDK tree          /builder/dl  shared download cache
#         /src      project sources (ro)         /extra       optional extra feed (ro), e.g. build/work/stub
#         /keys     signing keys (ro)            /out         results of this series (emptied by build.sh)
#
# Usage: sdk-build.sh --series 25.12 --arches "x86_64 mipsel_24kc" [--noarch "zaprett luci-app-zaprett"]
#                     [--extra] [--luci] [--fetch]
set -euo pipefail

SERIES=""
ARCHES=""
NOARCH=""
EXTRA=0
LUCI=0
FETCH=0
while [ $# -gt 0 ]; do
	case "$1" in
		--series) SERIES="$2"; shift 2 ;;
		--arches) ARCHES="$2"; shift 2 ;;
		--noarch) NOARCH="$2"; shift 2 ;;
		--extra) EXTRA=1; shift ;;
		--luci) LUCI=1; shift ;;
		--fetch) FETCH=1; shift ;;
		*) echo "sdk-build: unknown argument $1" >&2; exit 2 ;;
	esac
done
[ -n "$SERIES" ] && [ -n "$ARCHES" ] || { echo "sdk-build: --series and --arches are required" >&2; exit 2; }

cd /builder
# staging_dir/host/bin must not come first in PATH before the SDK prerequisite check has run: prereq-build links
# host tools found in PATH into that directory and would create self-referencing links (bash -> bash).
HOSTPATH="/builder/staging_dir/host/bin:$PATH"
MATRIX=/src/build/arches.txt
LOGS=/out/logs
mkdir -p "$LOGS" /out/noarch /out/arch /out/feed
export MKHASH=/builder/staging_dir/host/bin/mkhash

log() { printf '[%s %s] %s\n' "$(date +%H:%M:%S)" "$SERIES" "$*"; }
fail() { log "FAIL: $*"; exit 1; }

show_tail() {
	echo "----- last lines of $1 -----"
	tail -n 40 "$1" || true
	echo "-----"
}

# --------------------------------------------------------------------------- feed sources
mirror_of() {
	case "$1" in
		https://git.openwrt.org/openwrt/openwrt.git) echo "https://github.com/openwrt/openwrt.git" ;;
		https://git.openwrt.org/project/luci.git) echo "https://github.com/openwrt/luci.git" ;;
		*) echo "$1" ;;
	esac
}

# Shallow checkout of the exact commit/tag the SDK was released with (from feeds.conf.default).
fetch_feed() {
	local name="$1" dir="$2" line spec url ref
	line=$(grep -E "^src-git(-full)? (--root=[^ ]+ )?$name " feeds.conf.default) || fail "feed $name not in feeds.conf.default"
	spec=${line##* }
	url=${spec%%[;^]*}
	ref=${spec#"$url"}
	ref=${ref#?}
	if [ -f "$dir/.zaprett-ref" ] && [ "$(cat "$dir/.zaprett-ref")" = "$url $ref" ]; then
		log "feed source $name is up to date ($ref)"
		return 0
	fi
	[ "$FETCH" = 1 ] || fail "feed source $name is missing or outdated; run without --no-fetch"
	log "fetching feed source $name: $url $ref"
	rm -rf "$dir"
	mkdir -p "$dir"
	git -C "$dir" init -q
	if ! git -C "$dir" fetch -q --depth 1 "$url" "$ref"; then
		log "fetch from $url failed, trying $(mirror_of "$url")"
		git -C "$dir" fetch -q --depth 1 "$(mirror_of "$url")" "$ref" || fail "cannot fetch feed $name"
	fi
	git -C "$dir" -c advice.detachedHead=false checkout -q FETCH_HEAD
	echo "$url $ref" > "$dir/.zaprett-ref"
	log "feed source $name: $(git -C "$dir" log -1 --format='%H %cd')"
}

setup_feeds() {
	if [ "$LUCI" = 1 ]; then
		fetch_feed base /builder/feeds-src/openwrt
		fetch_feed luci /builder/feeds-src/luci
	fi
	{
		echo "src-link zaprett /src/packages"
		if [ "$EXTRA" = 1 ]; then echo "src-link zaprettextra /extra"; fi
		if [ "$LUCI" = 1 ]; then
			echo "src-link base /builder/feeds-src/openwrt/package"
			echo "src-link luci /builder/feeds-src/luci"
		fi
	} > feeds.conf
	./scripts/feeds update -a > "$LOGS/feeds.log" 2>&1 || { show_tail "$LOGS/feeds.log"; fail "feeds update"; }
	# Remove stale package links (e.g. a package deleted from the sources) before installing again.
	rm -rf package/feeds/zaprett package/feeds/zaprettextra
	./scripts/feeds install -a -p zaprett >> "$LOGS/feeds.log" 2>&1 || { show_tail "$LOGS/feeds.log"; fail "feeds install zaprett"; }
	if [ "$EXTRA" = 1 ]; then
		./scripts/feeds install -a -p zaprettextra >> "$LOGS/feeds.log" 2>&1 || { show_tail "$LOGS/feeds.log"; fail "feeds install extra"; }
	fi
	if [ "$LUCI" = 1 ]; then
		./scripts/feeds install luci-base >> "$LOGS/feeds.log" 2>&1 || { show_tail "$LOGS/feeds.log"; fail "feeds install luci-base"; }
	fi
	make defconfig > "$LOGS/defconfig.log" 2>&1 || { show_tail "$LOGS/defconfig.log"; fail "make defconfig"; }
}

# --------------------------------------------------------------------------- package format
check_format() {
	FORMAT=ipk
	if grep -q '^CONFIG_USE_APK=y' .config; then FORMAT=apk; fi
	case "$SERIES:$FORMAT" in
		25.12:apk|24.10:ipk) ;;
		*) fail "series $SERIES must not produce $FORMAT packages" ;;
	esac
	BIN_DIR="bin/packages/$(sed -n 's/^CONFIG_TARGET_ARCH_PACKAGES="\(.*\)"$/\1/p' .config)"
	log "package format: $FORMAT, SDK output: $BIN_DIR"
}

built_files() {
	[ -d bin/packages ] || return 0
	find bin/packages -type f \( -name '*.apk' -o -name '*.ipk' \) | sort
}

require_selected() {
	grep -q "^CONFIG_PACKAGE_$1=[my]$" .config || fail "package $1 is not selectable in .config (missing dependency or broken Makefile), see $LOGS/defconfig.log"
}

# --------------------------------------------------------------------------- builds
# noarch packages are built by calling their Makefile directly: the top-level "make package/X/compile" would first
# compile every runtime dependency present in the tree (ucode, libubox, all kernel modules for kmod-nft-queue, ...),
# which a noarch package does not need. Only the host tools luci.mk uses are built explicitly.
build_noarch() {
	local p dir f
	rm -rf bin/packages
	if [ "$LUCI" = 1 ]; then
		log "build host tools for luci.mk (luci-base/host: po2lmo, jsmin)"
		make package/luci-base/host/compile V=s > "$LOGS/luci-base-host.log" 2>&1 || { show_tail "$LOGS/luci-base-host.log"; fail "luci-base/host"; }
	fi
	for p in $NOARCH; do
		require_selected "$p"
		dir=$(find package/feeds -mindepth 2 -maxdepth 2 -name "$p" | head -n 1)
		[ -n "$dir" ] || fail "package $p is not installed in package/feeds"
		log "build $p (noarch, $dir)"
		if ! PATH="$HOSTPATH" make -r -C "$dir" TOPDIR=/builder clean > "$LOGS/$p.log" 2>&1 \
			|| ! PATH="$HOSTPATH" make -r -C "$dir" TOPDIR=/builder compile V=s >> "$LOGS/$p.log" 2>&1; then
			show_tail "$LOGS/$p.log"
			fail "build of $p"
		fi
	done
	for f in $(built_files); do
		mv "$f" /out/noarch/
	done
	rm -rf bin/packages
	log "noarch packages: $(ls /out/noarch | tr '\n' ' ')"
}

matrix_value() { # arch column(3=nfqws 4=nfqws2)
	awk -v s="$SERIES" -v a="$1" -v c="$2" '$1 == s && $2 == a { print $c }' "$MATRIX"
}

build_arch_pkg() { # package arch
	local p="$1" a="$2" logf="$LOGS/$1-$2.log" files
	rm -rf bin/packages
	if ! make "package/$p/clean" ZAPRETT_ARCH="$a" > "$logf" 2>&1 || ! make "package/$p/compile" ZAPRETT_ARCH="$a" V=s >> "$logf" 2>&1; then
		show_tail "$logf"
		fail "build of $p for $a"
	fi
	files=$(built_files)
	[ "$(echo "$files" | grep -c .)" = 1 ] || fail "expected exactly one package file for $p/$a, got: $files"
	grep -q "elf-check: .* OK" "$logf" || fail "no elf-check confirmation in $logf"
	mkdir -p "/out/arch/$a"
	mv "$files" "/out/arch/$a/"
	rm -rf bin/packages
}

# The Makefile must refuse architectures without a release binary and produce nothing.
expect_refusal() { # package arch
	local p="$1" a="$2" logf="$LOGS/$1-$2.refused.log"
	rm -rf bin/packages
	if make "package/$p/compile" ZAPRETT_ARCH="$a" V=s > "$logf" 2>&1; then
		fail "$p was built for $a, but the matrix says there is no binary"
	fi
	grep -q "has no static" "$logf" || { show_tail "$logf"; fail "$p for $a failed for an unexpected reason"; }
	[ -z "$(built_files)" ] || fail "$p for $a left package files behind"
	echo "$SERIES $a $p refused" >> /out/refused.txt
}

build_arches() {
	local a p col v
	: > /out/refused.txt
	for a in $ARCHES; do
		[ -n "$(matrix_value "$a" 2)" ] || fail "architecture $a is not in $MATRIX for $SERIES"
		for p in zaprett-nfqws zaprett-nfqws2; do
			col=3
			[ "$p" = zaprett-nfqws2 ] && col=4
			v=$(matrix_value "$a" "$col")
			if [ "$v" = y ]; then
				log "build $p for $a"
				build_arch_pkg "$p" "$a"
			else
				log "check that $p is refused for $a"
				expect_refusal "$p" "$a"
			fi
		done
	done
}

# --------------------------------------------------------------------------- feeds and signatures
index_apk() { # dir
	( cd "$1" && /builder/staging_dir/host/bin/apk mkndx \
		--root /builder --keys-dir /keys --allow-untrusted \
		--sign /keys/private-key.pem --output packages.adb ./*.apk ) > "$LOGS/index-$(basename "$1").log" 2>&1
}

index_ipk() { # dir
	(
		cd "$1"
		/builder/scripts/ipkg-make-index.sh . > Packages.manifest
		grep -vE '^(Maintainer|LicenseFiles|Source|SourceName|Require|SourceDateEpoch)' Packages.manifest > Packages
		rm -f Packages.manifest
		# Same usign SHA-512 padding workaround as OpenWrt package/Makefile.
		case "$(((64 + $(stat -L -c%s Packages)) % 128))" in 110|111) { echo ""; echo ""; } >> Packages ;; esac
		gzip -9nc Packages > Packages.gz
		/builder/staging_dir/host/bin/usign -S -m Packages -s /keys/key-build
	) > "$LOGS/index-$(basename "$1").log" 2>&1
}

make_feeds() {
	local a d
	for a in $ARCHES; do
		[ -d "/out/arch/$a" ] || continue
		d="/out/feed/$a"
		rm -rf "$d"
		mkdir -p "$d"
		if [ -n "$(ls /out/noarch)" ]; then cp /out/noarch/* "$d/"; fi
		cp "/out/arch/$a"/* "$d/"
		if [ "$FORMAT" = apk ]; then
			index_apk "$d" || { show_tail "$LOGS/index-$a.log"; fail "apk mkndx for $a"; }
		else
			index_ipk "$d" || { show_tail "$LOGS/index-$a.log"; fail "ipk index for $a"; }
		fi
	done
	log "feeds: $(ls /out/feed | wc -l) architectures"
}

# --------------------------------------------------------------------------- main
# bash/file links must resolve (a pristine image already has other dangling links, e.g. gcc before prereq).
for t in bash file; do
	if [ -L "staging_dir/host/bin/$t" ] && [ ! -e "staging_dir/host/bin/$t" ]; then
		fail "SDK tree is damaged (staging_dir/host/bin/$t -> $(readlink "staging_dir/host/bin/$t")); delete ~/zaprett-build/sdk/$SERIES and rerun"
	fi
done
log "arches: $(echo $ARCHES | wc -w), noarch: ${NOARCH:-none}"
setup_feeds
check_format
if [ -n "$NOARCH" ]; then build_noarch; fi
build_arches
make_feeds
log "done"
