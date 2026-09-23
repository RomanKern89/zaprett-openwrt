#!/bin/bash
# Backend (ucode) and rpcd plugin tests of zaprett in an official OpenWrt rootfs container - the same test
# run as on a router (990 backend / 145 plugin checks on 2026-09-22), without a router.
#
#   bash tools/ci/ucode-tests.sh <25.12|24.10> [--ref REF | --tree DIR] [--keep]
#
#   --ref REF   test the committed tree of REF (default HEAD, via "git archive": uncommitted edits are ignored)
#   --tree DIR  test the directory DIR as it is (must contain packages/)
#   --keep      keep the staging directory and print its path
#
# nfqws for the dry-run checks: static linux-x86_64 binary from the release archive pinned in
# packages/zaprett-nfqws/Makefile (sha256 = PKG_HASH). Log: $ZAPRETT_CI_OUT (default ./ci-out)/ucode-<series>.log.
# Exit code 0 only if both suites report 0 failures and exit with 0.
set -euo pipefail
. "$(dirname "$0")/lib.sh"

SERIES=""
REF=HEAD
TREE=""
KEEP=0
while [ $# -gt 0 ]; do
	case "$1" in
		--ref) REF="$2"; shift 2 ;;
		--tree) TREE="$2"; shift 2 ;;
		--keep) KEEP=1; shift ;;
		25.12|24.10) SERIES="$1"; shift ;;
		*) echo "usage: $0 <25.12|24.10> [--ref REF | --tree DIR] [--keep]" >&2; exit 2 ;;
	esac
done
case "$SERIES" in
	25.12) IMAGE="openwrt/rootfs:x86_64-v25.12.4" ;;  # newest 25.12 rootfs on Docker Hub (no v25.12.5 tag)
	24.10) IMAGE="openwrt/rootfs:x86_64-v24.10.8" ;;
	*) echo "usage: $0 <25.12|24.10> [--ref REF | --tree DIR] [--keep]" >&2; exit 2 ;;
esac

OUTDIR=${ZAPRETT_CI_OUT:-$PWD/ci-out}
mkdir -p "$OUTDIR"
LOG="$OUTDIR/ucode-$SERIES.log"
STAGE=$(mktemp -d "${TMPDIR:-/tmp}/zaprett-ucode.XXXXXX")
cleanup() {
	if [ "$KEEP" = 1 ]; then ci_log "staging kept: $STAGE"; else rm -rf "$STAGE"; fi
}
trap cleanup EXIT

if [ -n "$TREE" ]; then
	[ -d "$TREE/packages" ] || ci_die "$TREE/packages does not exist"
	cp -a "$TREE/packages" "$STAGE/"
	ci_log "sources: directory $TREE"
else
	commit=$(git -C "$CI_ROOT" rev-parse --verify "$REF^{commit}") || ci_die "no commit $REF in $CI_ROOT"
	# git archive stamps every file with the commit time (never mtime 0).
	git -C "$CI_ROOT" archive --format=tar "$commit" packages | tar -x -C "$STAGE"
	ci_log "sources: git archive $commit (packages/)"
fi
mkdir -p "$STAGE/engine" "$STAGE/ci"
cp "$CI_DIR/rootfs-tests.sh" "$STAGE/ci/"

mk="$STAGE/packages/zaprett-nfqws/Makefile"
archive=$(engine_archive "$mk")
prefix=$(basename "$archive" -openwrt-embedded.tar.gz)
tar -xzf "$archive" -C "$STAGE/engine" --strip-components=3 "$prefix/binaries/linux-x86_64/nfqws"
[ -x "$STAGE/engine/nfqws" ] || ci_die "nfqws not found in $archive"
ci_log "nfqws: $prefix/binaries/linux-x86_64/nfqws, sha256 $(sha256sum "$STAGE/engine/nfqws" | cut -d ' ' -f 1)"

docker image inspect "$IMAGE" > /dev/null 2>&1 || docker pull -q "$IMAGE" > /dev/null
ci_log "run tests in $IMAGE (log $LOG)"
rc=0
# NET_ADMIN (only inside the container's own network namespace): "nft -c" needs a netlink cache and
# nfqws --dry-run keeps CAP_NET_ADMIN when it drops root (without it: "setpcap: Operation not permitted").
docker run --rm --name "zaprett-ci-ucode-${SERIES/./}-$$" --cap-add NET_ADMIN -v "$STAGE:/src:ro" "$IMAGE" \
	/bin/sh /src/ci/rootfs-tests.sh > "$LOG" 2>&1 || rc=$?

grep -E '^(CI-INFO|FAIL|CRASH|SKIP) ' "$LOG" || true
backend=$(grep -E '^TOTAL pass=' "$LOG" | tail -n 1 || true)
plugin=$(grep -E '^RESULT passed=' "$LOG" | tail -n 1 || true)
brc=$(sed -n 's/^CI-RC backend //p' "$LOG")
prc=$(sed -n 's/^CI-RC plugin //p' "$LOG")
echo "ucode-tests $SERIES backend: ${backend:-no TOTAL line} (rc=${brc:-?})"
echo "ucode-tests $SERIES plugin:  ${plugin:-no RESULT line} (rc=${prc:-?})"
[ "$rc" = 0 ] || ci_die "container exited with $rc"
[[ "$backend" =~ ^TOTAL\ pass=[1-9][0-9]*\ fail=0$ ]] && [ "$brc" = 0 ] || ci_die "backend tests failed"
[[ "$plugin" =~ ^RESULT\ passed=[1-9][0-9]*\ failed=0$ ]] && [ "$prc" = 0 ] || ci_die "plugin tests failed"
ci_log "ucode-tests $SERIES: OK"
