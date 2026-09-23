#!/bin/bash
# Start every nfqws / nfqws2 binary of the architecture matrix under qemu-user and check "--version":
#
#   bash tools/ci/qemu-smoke.sh [--dist DIR]
#
# Binaries: the release archives pinned in packages/zaprett-nfqws*/Makefile (sha256 = PKG_HASH); with --dist also
# the binaries inside the built 24.10 packages (DIR/24.10/<arch>/*.ipk). 25.12 .apk files cannot be unpacked without
# apk-tools; build/verify.py proves their binaries are byte-identical to the release ones.
# Every "y" cell of build/arches.txt is run with the qemu CPU model closest to the OpenWrt target (table below).
# qemu: qemu-<arch>-static or qemu-<arch> from PATH (apt install qemu-user-static); if there is none, the script
# re-runs itself in an ubuntu:24.04 container with qemu-user-static installed.
# This is a smoke test of the ISA/ABI (the binary loads and runs its startup code), not a functional test.
set -euo pipefail
. "$(dirname "$0")/lib.sh"

DIST=""
while [ $# -gt 0 ]; do
	case "$1" in
		--dist) DIST=$(cd "$2" && pwd); shift 2 ;;
		*) echo "usage: $0 [--dist DIR]" >&2; exit 2 ;;
	esac
done

find_qemu() { # qemu arch name
	command -v "qemu-$1-static" 2> /dev/null || command -v "qemu-$1" 2> /dev/null || return 1
}

if ! find_qemu aarch64 > /dev/null; then
	command -v docker > /dev/null || ci_die "no qemu-user in PATH and no docker: apt install qemu-user-static"
	ci_log "no qemu-user in PATH: running in ubuntu:24.04 with qemu-user-static"
	mkdir -p "$CI_CACHE"
	mounts=(-v "$CI_ROOT:/repo:ro" -v "$CI_CACHE:/cache")
	args=()
	if [ -n "$DIST" ]; then mounts+=(-v "$DIST:/dist:ro"); args=(--dist /dist); fi
	# Runs as root for apt; files it downloads into the cache are handed back to the caller afterwards.
	exec docker run --rm --name "zaprett-ci-qemu-$$" "${mounts[@]}" -e ZAPRETT_CI_CACHE=/cache \
		-e OWNER="$(id -u):$(id -g)" ubuntu:24.04 bash -c \
		'set -e; apt-get update -qq > /dev/null; DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends qemu-user-static curl ca-certificates > /dev/null; rc=0; bash /repo/tools/ci/qemu-smoke.sh "$@" || rc=$?; chown -R "$OWNER" /cache; exit $rc' \
		qemu-smoke "${args[@]}"
fi

# OpenWrt arch -> "<release dir> <qemu arch> <qemu cpu>". CPU models that qemu lacks are replaced by the nearest
# one of the same ISA level: cortex-a5 -> cortex-a7 (ARMv7 + VFPv4), 74Kc -> 74Kf, pentium4 -> coreduo,
# 464fp -> 440epx (440 core with FPU), mips32 -> 4Kc. powerpc_8548 is an e500v2 (SPE, no classic FPU).
target_of() {
	case "$1" in
		aarch64_cortex-a53|aarch64_generic) echo "linux-arm64 aarch64 cortex-a53" ;;
		aarch64_cortex-a72) echo "linux-arm64 aarch64 cortex-a72" ;;
		aarch64_cortex-a76) echo "linux-arm64 aarch64 cortex-a76" ;;
		arm_arm1176jzf-s_vfp) echo "linux-arm arm arm1176" ;;
		arm_cortex-a5_vfpv4|arm_cortex-a7|arm_cortex-a7_*) echo "linux-arm arm cortex-a7" ;;
		arm_cortex-a8_vfpv3) echo "linux-arm arm cortex-a8" ;;
		arm_cortex-a9|arm_cortex-a9_*) echo "linux-arm arm cortex-a9" ;;
		arm_cortex-a15_neon-vfpv4) echo "linux-arm arm cortex-a15" ;;
		mipsel_24kc) echo "linux-mipsel mipsel 24Kc" ;;
		mipsel_24kc_24kf) echo "linux-mipsel mipsel 24Kf" ;;
		mipsel_74kc) echo "linux-mipsel mipsel 74Kf" ;;
		mipsel_mips32) echo "linux-mipsel mipsel 4Kc" ;;
		mips_24kc) echo "linux-mips mips 24Kc" ;;
		mips_4kec) echo "linux-mips mips 4KEc" ;;
		mips_mips32) echo "linux-mips mips 4Kc" ;;
		mips64_mips64r2) echo "linux-mips64 mips64 MIPS64R2-generic" ;;
		mips64_octeonplus) echo "linux-mips64 mips64 Octeon68XX" ;;
		i386_pentium-mmx) echo "linux-x86 i386 pentium" ;;
		i386_pentium4) echo "linux-x86 i386 coreduo" ;;
		x86_64) echo "linux-x86_64 x86_64 qemu64" ;;
		powerpc_464fp) echo "linux-ppc ppc 440epx" ;;
		powerpc_8548) echo "linux-ppc ppc mpc8548_v10" ;;
		riscv64_*) echo "linux-riscv64 riscv64 rv64" ;;
		*) return 1 ;;
	esac
}

WORKDIR=$(mktemp -d "${TMPDIR:-/tmp}/zaprett-qemu.XXXXXX")
trap 'rm -rf "$WORKDIR"' EXIT
OKS=0
FAILS=0

# run_one <label> <binary> <qemu arch> <cpu> <expected version>
run_one() {
	local label="$1" bin="$2" qa="$3" cpu="$4" want="$5" qemu out rc
	qemu=$(find_qemu "$qa") || { echo "FAIL $label: no qemu-$qa"; FAILS=$((FAILS + 1)); return; }
	rc=0
	out=$(timeout 60 "$qemu" -cpu "$cpu" "$bin" --version 2>&1) || rc=$?
	if grep -q "version v$want\b" <<< "$out"; then
		echo "OK   $label: qemu-$qa -cpu $cpu: $(head -n 1 <<< "$out")"
		OKS=$((OKS + 1))
	else
		echo "FAIL $label: qemu-$qa -cpu $cpu rc=$rc: $(head -n 3 <<< "$out" | tr '\n' ' ')"
		FAILS=$((FAILS + 1))
	fi
}

# Unpack one engine binary of every release dir.
declare -A VERSION=()
for pkg in zaprett-nfqws zaprett-nfqws2; do
	mk="$CI_ROOT/packages/$pkg/Makefile"
	VERSION[$pkg]=$(mk_var "$mk" PKG_VERSION)
	archive=$(engine_archive "$mk")
	mkdir -p "$WORKDIR/$pkg"
	tar -xzf "$archive" -C "$WORKDIR/$pkg" --strip-components=1 --wildcards "*/binaries/linux-*/${pkg#zaprett-}"
done
ci_log "qemu: $(find_qemu arm), $("$(find_qemu arm)" --version | head -n 1)"

MATRIX="$CI_ROOT/build/arches.txt"
while read -r arch n1 n2; do
	t=$(target_of "$arch") || { echo "FAIL $arch: no qemu mapping in tools/ci/qemu-smoke.sh"; FAILS=$((FAILS + 1)); continue; }
	read -r dir qa cpu <<< "$t"
	if [ "$n1" = y ]; then
		run_one "$arch nfqws (release $dir)" "$WORKDIR/zaprett-nfqws/binaries/$dir/nfqws" "$qa" "$cpu" "${VERSION[zaprett-nfqws]}"
	fi
	if [ "$n2" = y ]; then
		run_one "$arch nfqws2 (release $dir)" "$WORKDIR/zaprett-nfqws2/binaries/$dir/nfqws2" "$qa" "$cpu" "${VERSION[zaprett-nfqws2]}"
	fi
done < <(awk '!/^#/ && NF == 4 && ($3 == "y" || $4 == "y") && !seen[$2]++ { print $2, $3, $4 }' "$MATRIX")

# Negative controls: each must NOT print the version. arm926 = ARMv5TE (arm_arm926ej-s, excluded in the matrix
# because linux-arm is ARMv6KZ/Thumb-2 - qemu raises SIGILL); a MIPS binary under qemu-arm is not even loaded.
run_neg() { # label binary qemu-arch cpu
	local qemu out rc=0
	qemu=$(find_qemu "$3") || { echo "FAIL negative $1: no qemu-$3"; FAILS=$((FAILS + 1)); return; }
	out=$(timeout 60 "$qemu" -cpu "$4" "$2" --version 2>&1) || rc=$?
	if [ "$rc" != 0 ] && ! grep -q 'github version' <<< "$out"; then
		echo "OK   negative $1: refused (rc=$rc) $(head -n 1 <<< "$out")"
		OKS=$((OKS + 1))
	else
		echo "FAIL negative $1: rc=$rc, the binary ran: $(head -n 1 <<< "$out")"
		FAILS=$((FAILS + 1))
	fi
}
run_neg "arm_arm926ej-s nfqws (linux-arm on ARMv5)" "$WORKDIR/zaprett-nfqws/binaries/linux-arm/nfqws" arm arm926
run_neg "linux-mips nfqws under qemu-arm" "$WORKDIR/zaprett-nfqws/binaries/linux-mips/nfqws" arm cortex-a7

if [ -n "$DIST" ]; then
	n=0
	for ipk in "$DIST"/24.10/*/zaprett-nfqws_*.ipk "$DIST"/24.10/*/zaprett-nfqws2_*.ipk; do
		[ -f "$ipk" ] || continue
		arch=$(basename "$(dirname "$ipk")")
		pkg=$(basename "$ipk"); pkg=${pkg%%_*}
		t=$(target_of "$arch") || { echo "FAIL $arch: no qemu mapping"; FAILS=$((FAILS + 1)); continue; }
		read -r dir qa cpu <<< "$t"
		d="$WORKDIR/ipk/$arch-$pkg"
		mkdir -p "$d"
		tar -xzOf "$ipk" ./data.tar.gz | tar -xz -C "$d" "./usr/libexec/zaprett/${pkg#zaprett-}"
		run_one "$arch $pkg (package $(basename "$ipk"))" "$d/usr/libexec/zaprett/${pkg#zaprett-}" "$qa" "$cpu" "${VERSION[$pkg]}"
		n=$((n + 1))
	done
	[ "$n" -gt 0 ] || { echo "FAIL no zaprett-nfqws*.ipk in $DIST/24.10"; FAILS=$((FAILS + 1)); }
fi

echo "qemu-smoke: $OKS OK, $FAILS FAIL"
[ "$FAILS" = 0 ]
