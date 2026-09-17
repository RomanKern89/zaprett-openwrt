#!/bin/sh
# Runs the zaprett backend unit tests on an OpenWrt host (only ucode, sh and busybox needed).
# Nothing is installed and nothing outside the work directory is written.
#
# Usage: sh tests/run.sh <package root> <work dir under /tmp> [nfqws binary] [test name...]
#   package root  directory with Makefile, files/, tests/
#   nfqws binary  optional; enables `nfqws --dry-run` checks of all fixture strategies

ROOT="$1"
WORK="$2"
NFQWS="$3"
[ -n "$ROOT" ] && [ -n "$WORK" ] || { echo "usage: $0 <root> <work> [nfqws] [tests...]"; exit 2; }
case "$WORK" in /tmp/*) ;; *) echo "work dir must be under /tmp"; exit 2 ;; esac
if [ $# -ge 3 ]; then shift 3; else shift $#; fi

mkdir -p "$WORK" || exit 2
export ZTEST_ROOT="$ROOT" ZTEST_WORK="$WORK" ZTEST_NFQWS="$NFQWS"

LIBS="$ROOT/files/usr/share/ucode/*.uc"
TLIB="$ROOT/tests/lib/*.uc"

if [ $# -gt 0 ]; then
	tests=""
	for t in "$@"; do tests="$tests $ROOT/tests/test_$t.uc"; done
else
	tests=$(ls "$ROOT"/tests/test_*.uc)
fi

total_fail=0
total_pass=0
failed_files=""
for t in $tests; do
	out=$(ucode -S -L "$LIBS" -L "$TLIB" -- "$t" 2>&1)
	rc=$?
	echo "$out" | grep -v '^RESULT '
	res=$(echo "$out" | grep '^RESULT ' | tail -n 1)
	if [ -z "$res" ]; then
		echo "CRASH $(basename "$t") rc=$rc"
		total_fail=$((total_fail + 1))
		failed_files="$failed_files $(basename "$t")"
		continue
	fi
	echo "$res"
	p=$(echo "$res" | sed -n 's/.* pass=\([0-9]*\) fail=\([0-9]*\)$/\1/p')
	f=$(echo "$res" | sed -n 's/.* pass=\([0-9]*\) fail=\([0-9]*\)$/\2/p')
	total_pass=$((total_pass + ${p:-0}))
	total_fail=$((total_fail + ${f:-1}))
	[ "$rc" = 0 ] && [ "${f:-1}" = 0 ] || failed_files="$failed_files $(basename "$t")"
done

echo "TOTAL pass=$total_pass fail=$total_fail${failed_files:+ failed:$failed_files}"
[ "$total_fail" = 0 ] && [ -z "$failed_files" ]
