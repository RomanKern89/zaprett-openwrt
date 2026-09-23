#!/bin/bash
# zaprett package build on the Docker VM (host side). Started by build/remote.py or by hand:
#
#   bash build.sh [--series "25.12 24.10"] [--arch all|"x86_64 mipsel_24kc"] [--extra-feed DIR]
#                 [--no-fetch] [--rc-file FILE] [--out DIR] [--dist DIR] [--no-copy]
#
#   --no-copy  build in the SDK image's own /builder instead of a persistent copy in WORK/sdk/<series>
#              (CI runners: saves ~1.4 GB of disk and the copy; feeds are fetched again on every run)
#
# Layout (WORK = $ZAPRETT_WORK or ~/zaprett-build):
#   WORK/keys/            signing keys (private-key.pem, public-key.pem, key-build, key-build.pub), never in git
#   WORK/sdk/<series>/    persistent SDK tree copied from the image (feeds, host tools, compiled dependencies);
#                         not used with --no-copy
#   WORK/dl/              download cache shared by both SDKs
#   WORK/out/<series>/    raw results of the last run; WORK/out/dist.tar - everything for download
#   WORK/dist/            final tree: <series>/<arch>/ feeds, releases/*.tar.gz bundles, keys/, SHA256SUMS
set -euo pipefail

SELF_DIR=$(cd "$(dirname "$0")" && pwd)
SRC=$(dirname "$SELF_DIR")
WORK=${ZAPRETT_WORK:-$HOME/zaprett-build}

SERIES_LIST="25.12 24.10"
ARCH_ARG="all"
EXTRA_FEED=""
FETCH=1
RC_FILE=""
COPY_SDK=1
OUT="$WORK/out"
DIST="$WORK/dist"
while [ $# -gt 0 ]; do
	case "$1" in
		--out) OUT="$2"; shift 2 ;;
		--dist) DIST="$2"; shift 2 ;;
		--series) SERIES_LIST="$2"; shift 2 ;;
		--arch) ARCH_ARG="$2"; shift 2 ;;
		--extra-feed) EXTRA_FEED="$2"; shift 2 ;;
		--no-fetch) FETCH=0; shift ;;
		--no-copy) COPY_SDK=0; shift ;;
		--rc-file) RC_FILE="$2"; shift 2 ;;
		*) echo "build.sh: unknown argument $1" >&2; exit 2 ;;
	esac
done

finish() {
	local rc=$?
	if declare -F restore_owner > /dev/null; then restore_owner; fi
	[ -n "$RC_FILE" ] && echo "$rc" > "$RC_FILE"
	log "exit code $rc"
}
trap finish EXIT

log() { printf '[%s] %s\n' "$(date '+%F %T')" "$*"; }
fail() { log "FAIL: $*"; exit 1; }

image_of() {
	case "$1" in
		25.12) echo "openwrt/sdk:x86_64-v25.12.5" ;;
		24.10) echo "openwrt/sdk:x86_64-v24.10.8" ;;
		*) fail "unknown series $1 (supported: 25.12, 24.10)" ;;
	esac
}

MATRIX="$SRC/build/arches.txt"
[ -f "$MATRIX" ] || fail "no $MATRIX"
[ -f "$WORK/keys/private-key.pem" ] && [ -f "$WORK/keys/key-build" ] || fail "signing keys are missing in $WORK/keys"

log "sources: $SRC, work: $WORK, series: $SERIES_LIST, arch: $ARCH_ARG"
log "disk before: $(df -h "$WORK" | awk 'NR == 2 { print $4 " free of " $2 }')"
mkdir -p "$WORK/dl" "$WORK/logs" "$OUT" "$WORK/sdk"

# ------------------------------------------------------------------ package set
NOARCH=""
LUCI=0
EXTRA=0
declare -A SEEN=()
scan_feed() { # dir
	local mk name
	for mk in "$1"/*/Makefile; do
		[ -f "$mk" ] || continue
		name=$(basename "$(dirname "$mk")")
		case "$name" in zaprett-nfqws|zaprett-nfqws2) continue ;; esac
		[ -z "${SEEN[$name]:-}" ] || fail "package $name exists twice (packages/ and extra feed)"
		SEEN[$name]=1
		NOARCH="$NOARCH $name"
		if grep -q 'luci\.mk' "$mk"; then LUCI=1; fi
	done
}
scan_feed "$SRC/packages"
if [ -n "$EXTRA_FEED" ]; then
	[ -d "$EXTRA_FEED" ] || fail "extra feed $EXTRA_FEED does not exist"
	EXTRA=1
	scan_feed "$EXTRA_FEED"
fi
NOARCH=${NOARCH# }
[ -f "$SRC/packages/zaprett-nfqws/Makefile" ] && [ -f "$SRC/packages/zaprett-nfqws2/Makefile" ] || fail "nfqws package Makefiles are missing"
log "noarch packages: ${NOARCH:-none} (luci.mk: $LUCI, extra feed: $EXTRA)"

VERSION="dev"
if [ -f "$SRC/packages/zaprett/Makefile" ]; then
	v=$(sed -n 's/^PKG_VERSION:=//p' "$SRC/packages/zaprett/Makefile" | head -n 1)
	r=$(sed -n 's/^PKG_RELEASE:=//p' "$SRC/packages/zaprett/Makefile" | head -n 1)
	[ -n "$v" ] && VERSION="$v-r${r:-1}"
elif [ -n "$EXTRA_FEED" ] && [ -f "$EXTRA_FEED/zaprett/Makefile" ]; then
	VERSION="$(sed -n 's/^PKG_VERSION:=//p' "$EXTRA_FEED/zaprett/Makefile" | head -n 1)-stub"
fi
log "bundle version: $VERSION"

arches_for() { # series
	if [ "$ARCH_ARG" = all ]; then
		awk -v s="$1" '$1 == s { print $2 }' "$MATRIX" | tr '\n' ' '
	else
		local a out=""
		for a in $ARCH_ARG; do
			if awk -v s="$1" -v a="$a" '$1 == s && $2 == a { f = 1 } END { exit !f }' "$MATRIX"; then
				out="$out $a"
			else
				log "architecture $a does not exist in OpenWrt $1, skipped for this series" >&2
			fi
		done
		echo "$out"
	fi
}

# ------------------------------------------------------------------ SDK trees
# The SDK containers run as the image user (buildbot, uid 1000). When the host user has another uid (GitHub
# runners: 1001), the mounted directories are handed over to the SDK uid and back after the build, as root in a
# throw-away container: no sudo on the host, keys never become world-readable.
HOST_IDS="$(id -u):$(id -g)"
SDK_IDS=""
OWNED=()
own() { # image owner dir...
	local image="$1" owner="$2"
	shift 2
	local d args=()
	for d in "$@"; do args+=(-v "$d:/own$(printf '%s' "$d" | tr -c 'A-Za-z0-9\n' _)"); done
	docker run --rm --user root --network none "${args[@]}" "$image" sh -c "chown -R $owner /own*"
}
align_owner() { # image dir...
	local image="$1"
	shift
	[ -n "$SDK_IDS" ] || SDK_IDS=$(docker run --rm "$image" sh -c 'echo "$(id -u):$(id -g)"')
	[ "$SDK_IDS" != "$HOST_IDS" ] || return 0
	log "host uid:gid $HOST_IDS, SDK $SDK_IDS: handing over $*"
	own "$image" "$SDK_IDS" "$@"
	OWNED+=("$@")
	OWNER_IMAGE="$image"
}
restore_owner() {
	[ "${#OWNED[@]}" -gt 0 ] || return 0
	own "$OWNER_IMAGE" "$HOST_IDS" "${OWNED[@]}" || log "could not give ${OWNED[*]} back to $HOST_IDS"
	OWNED=()
}

prepare_sdk() { # series
	local s="$1" image id dir
	image=$(image_of "$s")
	docker image inspect "$image" > /dev/null 2>&1 || docker pull "$image"
	[ "$COPY_SDK" = 1 ] || return 0
	id=$(docker image inspect --format '{{.Id}}' "$image")
	dir="$WORK/sdk/$s"
	if [ -f "$dir/.zaprett-image" ] && [ "$(cat "$dir/.zaprett-image")" = "$id" ]; then
		return 0
	fi
	log "copying SDK $image into $dir"
	rm -rf "$dir"
	mkdir -p "$dir"
	align_owner "$image" "$dir"
	docker run --rm -v "$dir:/out" "$image" bash -c 'cp -a /builder/. /out/'
	restore_owner
	echo "$id" > "$dir/.zaprett-image"
}

run_series() { # series arches
	local s="$1" arches="$2" image out args=()
	image=$(image_of "$s")
	out="$OUT/$s"
	args=(--series "$s" --arches "$arches")
	[ -n "$NOARCH" ] && args+=(--noarch "$NOARCH")
	[ "$EXTRA" = 1 ] && args+=(--extra)
	[ "$LUCI" = 1 ] && args+=(--luci)
	[ "$FETCH" = 1 ] && args+=(--fetch)
	local mounts=(-v "$WORK/dl:/builder/dl" -v "$SRC:/src:ro" -v "$WORK/keys:/keys:ro" -v "$out:/out")
	[ "$COPY_SDK" = 1 ] && mounts=(-v "$WORK/sdk/$s:/builder" "${mounts[@]}")
	[ "$EXTRA" = 1 ] && mounts+=(-v "$EXTRA_FEED:/extra:ro")
	docker run --rm --name "zaprett-sdk-${s/./}-$$" "${mounts[@]}" "$image" bash /src/build/sdk-build.sh "${args[@]}" \
		&& docker run --rm --name "zaprett-verify-${s/./}-$$" "${mounts[@]}" "$image" \
			python3 /src/build/verify.py --series "$s" --arches "$arches" --noarch "$NOARCH"
}

declare -A PIDS=() ARCHES=()
for s in $SERIES_LIST; do
	image_of "$s" > /dev/null
	prepare_sdk "$s"
	ARCHES[$s]=$(arches_for "$s")
	[ -n "${ARCHES[$s]// /}" ] || fail "no architectures selected for $s"
	rm -rf "${OUT:?}/$s"
	mkdir -p "$OUT/$s"
done
for s in $SERIES_LIST; do
	dirs=("$OUT/$s")
	[ "$COPY_SDK" = 1 ] && dirs+=("$WORK/sdk/$s")
	align_owner "$(image_of "$s")" "${dirs[@]}"
done
align_owner "$(image_of "${SERIES_LIST%% *}")" "$WORK/dl" "$WORK/keys"
for s in $SERIES_LIST; do
	log "start $s: $(echo ${ARCHES[$s]} | wc -w) architectures, log $WORK/logs/sdk-$s.log"
	run_series "$s" "${ARCHES[$s]}" > "$WORK/logs/sdk-$s.log" 2>&1 &
	PIDS[$s]=$!
done
FAILED=""
for s in $SERIES_LIST; do
	if wait "${PIDS[$s]}"; then
		log "series $s: build and verification OK"
	else
		FAILED="$FAILED $s"
		log "series $s FAILED, last lines of $WORK/logs/sdk-$s.log:"
		tail -n 60 "$WORK/logs/sdk-$s.log" || true
	fi
	grep -E '^\[|^verify|^FAIL' "$WORK/logs/sdk-$s.log" | tail -n 25 || true
done
restore_owner
[ -z "$FAILED" ] || fail "series failed:$FAILED"

# ------------------------------------------------------------------ dist
rm -rf "$DIST"
mkdir -p "$DIST/releases" "$DIST/keys"
cp "$WORK/keys/public-key.pem" "$DIST/keys/zaprett.pem"
cp "$WORK/keys/key-build.pub" "$DIST/keys/zaprett-usign.pub"
cp "$MATRIX" "$DIST/arches.txt"

bundle() { # series arch
	local s="$1" a="$2" pm name dir
	pm=apk
	[ "$s" = 24.10 ] && pm=opkg
	name="zaprett-$VERSION-$s-$a"
	dir="$OUT/bundles/$name"
	rm -rf "$dir"
	mkdir -p "$dir/feed" "$dir/keys"
	cp "$DIST/$s/$a"/* "$dir/feed/"
	if [ "$pm" = apk ]; then
		cp "$DIST/keys/zaprett.pem" "$dir/keys/zaprett.pem"
	else
		cp "$DIST/keys/zaprett-usign.pub" "$dir/keys/zaprett-usign.pub"
	fi
	cp "$SRC/build/install.sh" "$dir/install.sh"
	chmod 755 "$dir/install.sh"
	printf 'version=%s\nseries=%s\narch=%s\npm=%s\nbuilt=%s\n' "$VERSION" "$s" "$a" "$pm" "$(date -u +%FT%TZ)" > "$dir/bundle.info"
	( cd "$dir" && find . -type f ! -name SHA256SUMS | sed 's|^\./||' | LC_ALL=C sort | xargs sha256sum > SHA256SUMS )
	tar -C "$OUT/bundles" --owner=0 --group=0 -czf "$DIST/releases/$name.tar.gz" "$name"
}

rm -rf "$OUT/bundles"
for s in $SERIES_LIST; do
	mkdir -p "$DIST/$s"
	cp -a "$OUT/$s/feed/." "$DIST/$s/"
	cp "$OUT/$s/verify-report.txt" "$DIST/verify-$s.txt"
	cp "$OUT/$s/refused.txt" "$DIST/refused-$s.txt"
	for a in ${ARCHES[$s]}; do
		[ -d "$DIST/$s/$a" ] || continue
		if [ "$(awk -v s="$s" -v a="$a" '$1 == s && $2 == a { print $3 }' "$MATRIX")" != y ]; then
			log "no bundle for $s/$a: zaprett requires zaprett-nfqws, which has no binary for $a"
			continue
		fi
		if [ -n "$NOARCH" ]; then
			bundle "$s" "$a"
		fi
	done
done
( cd "$DIST" && find . -type f ! -name SHA256SUMS | sed 's|^\./||' | LC_ALL=C sort | xargs sha256sum > SHA256SUMS )
log "dist: $(find "$DIST" -type f | wc -l) files, $(du -sh "$DIST" | cut -f1), bundles: $(ls "$DIST/releases" | wc -l)"
rm -f "$OUT/dist.tar"
tar -C "$(dirname "$DIST")" -cf "$OUT/dist.tar" --transform "s|^$(basename "$DIST")|dist|" "$(basename "$DIST")"
log "archive: $OUT/dist.tar $(du -h "$OUT/dist.tar" | cut -f1)"
log "disk after: $(df -h "$WORK" | awk 'NR == 2 { print $4 " free of " $2 }')"
