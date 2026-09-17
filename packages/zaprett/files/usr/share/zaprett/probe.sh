#!/bin/sh
# zaprett: parallel HTTP(S) downloads with uclient-fetch (repository loader and strategy tester).
#
# Usage: probe.sh <tasks> <outdir> <concurrency> <timeout_s> <ipv4only:0|1> [max_bytes]
#   <tasks>  one "<key> <url>" per line; key must match [A-Za-z0-9_.-]+, url must be http(s)
#   max_bytes  optional body size limit (0 = none). A larger download is cut by `ulimit -f` (busybox ash
#            counts 512-byte blocks): uclient-fetch dies with SIGXFSZ (exit 153) and the body is left
#            longer than max_bytes, so the caller can tell it apart from a complete file.
# Results per key:
#   <outdir>/<key>.body  response body (created only for successful responses)
#   <outdir>/<key>.err   uclient-fetch stderr (status and error messages)
#   <outdir>/<key>.res   "<exit code> <elapsed ms>"
# All variable data is passed quoted; nothing from the task file is evaluated by the shell.
#
# The `wait` builtin is not used on purpose: ucode system() with a timeout blocks SIGCHLD in the child,
# and busybox ash `wait` then never returns (checked on OpenWrt 24.10.8 and 25.12.5). A batch is run as
# one pipeline instead: all stages start together and the shell collects them with waitpid.

tasks="$1"
out="$2"
conc="$3"
tmo="$4"
v4="$5"
max="$6"

UA='Mozilla/5.0 (X11; Linux x86_64; rv:128.0) Gecko/20100101 Firefox/128.0'

[ -f "$tasks" ] && [ -d "$out" ] || exit 2
case "$conc" in ''|*[!0-9]*) conc=4 ;; esac
case "$tmo" in ''|*[!0-9]*) tmo=10 ;; esac
[ "$conc" -ge 1 ] || conc=1
case "$max" in ''|*[!0-9]*) max=0 ;; esac
fblocks=unlimited
[ "$max" -gt 0 ] && fblocks=$(( max / 512 + 1 ))

# centiseconds since boot; "1${frac} - 100" avoids octal parsing of fractions like "05"
uptime_cs() {
	local up rest
	read -r up rest < /proc/uptime
	echo $(( ${up%.*} * 100 + 1${up#*.} - 100 ))
}

fetch_one() {
	local key="$1" url="$2" t0 t1 rc
	t0=$(uptime_cs)
	if [ "$v4" = 1 ]; then
		(ulimit -f "$fblocks"; exec uclient-fetch -4 -T "$tmo" -U "$UA" -O "$out/$key.body" "$url") </dev/null >/dev/null 2>"$out/$key.err"
	else
		(ulimit -f "$fblocks"; exec uclient-fetch -T "$tmo" -U "$UA" -O "$out/$key.body" "$url") </dev/null >/dev/null 2>"$out/$key.err"
	fi
	rc=$?
	t1=$(uptime_cs)
	echo "$rc $(( (t1 - t0) * 10 ))" > "$out/$key.res"
}

# run_batch key1 url1 key2 url2 ... — all fetches of the batch in one pipeline
run_batch() {
	[ $# -ge 2 ] || return 0
	local k="$1" u="$2"
	shift 2
	if [ $# -ge 2 ]; then
		fetch_one "$k" "$u" </dev/null | run_batch "$@"
	else
		fetch_one "$k" "$u" </dev/null
	fi
}

set --
n=0
while read -r key url rest; do
	case "$key" in
		''|*[!A-Za-z0-9_.-]*) continue ;;
	esac
	case "$url" in
		http://*|https://*) ;;
		*) continue ;;
	esac
	set -- "$@" "$key" "$url"
	n=$((n + 1))
	if [ "$n" -ge "$conc" ]; then
		run_batch "$@" </dev/null
		set --
		n=0
	fi
done < "$tasks"
run_batch "$@" </dev/null
exit 0
