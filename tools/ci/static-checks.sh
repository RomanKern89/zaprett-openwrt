#!/bin/bash
# Static checks of the zaprett sources (no router, no SDK, no docker):
#
#   bash tools/ci/static-checks.sh [root]
#
#   cr            no CR byte in any text file (tools/ci/check_cr.py; *.bin, *.gz and other binaries excluded)
#   check_static  luci-app-zaprett: JS syntax, forbidden constructs, i18n, ACL/menu (needs python3 babel, node)
#   negative      negative controls of check_static (each broken copy must be rejected)
#   check_names   undeclared identifiers / missing methods in LuCI views (acorn; $ACORN or npm install into the cache)
#   node          node --check of every *.js (LuCI views wrapped in a function like LuCI does)
#   bundle        tools/data/test_build_bundle.py - only when upstream/ sources exist (not in git: skipped in CI)
#   sh            sh -n of every shell script in packages/ (by .sh suffix or #!/bin/sh shebang), bash -n of build/, tools/ci/
#   python        py_compile of every *.py (bytecode goes to a temporary directory)
#
# Prints "PASS|FAIL|SKIP <check>" per check and a summary; exit code 1 if any check failed.
set -uo pipefail
. "$(dirname "$0")/lib.sh"

ROOT=$(cd "${1:-$CI_ROOT}" && pwd)
LUCI="$ROOT/packages/luci-app-zaprett"
ACORN_VERSION=8.15.0
TMP=$(mktemp -d "${TMPDIR:-/tmp}/zaprett-static.XXXXXX")
trap 'rm -rf "$TMP"' EXIT

PASSED=0
FAILED=0
SKIPPED=0
FAILED_NAMES=""

# step <name> <command...>: output goes to $TMP/<name>.log, shown in full only on failure.
step() {
	local name="$1" rc
	shift
	"$@" > "$TMP/$name.log" 2>&1
	rc=$?
	if [ "$rc" = 0 ]; then
		echo "PASS $name: $(tail -n 1 "$TMP/$name.log")"
		PASSED=$((PASSED + 1))
	else
		echo "FAIL $name (rc=$rc):"
		tail -n 60 "$TMP/$name.log" | sed 's/^/    /'
		FAILED=$((FAILED + 1))
		FAILED_NAMES="$FAILED_NAMES $name"
	fi
}

skip() {
	echo "SKIP $1: $2"
	SKIPPED=$((SKIPPED + 1))
}

# Every file with an sh/bash shebang or .sh suffix under the given directories.
shell_files() { # interpreter-regex dir...
	local re="$1" f
	shift
	find "$@" -type f ! -name Makefile ! -path '*/node_modules/*' 2>/dev/null | LC_ALL=C sort | while read -r f; do
		if head -n 1 "$f" | grep -qE "^#!($re)"; then
			echo "$f"
		elif [[ "$f" == *.sh ]] && ! head -n 1 "$f" | grep -q '^#!'; then
			echo "$f"
		fi
	done
}

check_sh() {
	local f n=0 bad=0
	while read -r f; do
		n=$((n + 1))
		sh -n "$f" || { echo "sh -n failed: ${f#$ROOT/}"; bad=1; }
	done < <(shell_files '/bin/sh|/bin/ash|/usr/bin/env sh' "$ROOT/packages")
	while read -r f; do
		n=$((n + 1))
		bash -n "$f" || { echo "bash -n failed: ${f#$ROOT/}"; bad=1; }
	done < <(shell_files '/bin/bash|/usr/bin/env bash' "$ROOT/build" "$ROOT/tools/ci")
	[ "$n" -gt 0 ] || { echo "no shell scripts found"; return 1; }
	echo "$n shell scripts parsed"
	return "$bad"
}

check_python() {
	local n
	n=$(find "$ROOT" -name '*.py' ! -path '*/upstream/*' ! -path '*/.git/*' ! -path '*/node_modules/*' | wc -l)
	[ "$n" -gt 0 ] || { echo "no python files found"; return 1; }
	find "$ROOT" -name '*.py' ! -path '*/upstream/*' ! -path '*/.git/*' ! -path '*/node_modules/*' -print0 \
		| PYTHONPYCACHEPREFIX="$TMP/pycache" xargs -0 python3 -m py_compile || return 1
	echo "$n python files compiled"
}

check_node() {
	local f n=0 bad=0 w
	while IFS= read -r -d '' f; do
		n=$((n + 1))
		if [[ "$f" == */htdocs/luci-static/resources/* ]]; then
			# LuCI evaluates a view as the body of a function: a top-level "return" is legal there.
			w="$TMP/wrapped-$n.js"
			{ printf '(function(window, document, L) {\n'; cat "$f"; printf '\n});\n'; } > "$w"
			node --check "$w" || { echo "node --check failed: ${f#$ROOT/}"; bad=1; }
		else
			node --check "$f" || { echo "node --check failed: ${f#$ROOT/}"; bad=1; }
		fi
	done < <(find "$ROOT" -name '*.js' ! -path '*/upstream/*' ! -path '*/.git/*' ! -path '*/node_modules/*' \
		! -path '*/build/work/*' -print0)
	[ "$n" -gt 0 ] || { echo "no js files found"; return 1; }
	echo "$n js files checked"
	return "$bad"
}

acorn_path() {
	if [ -n "${ACORN:-}" ]; then echo "$ACORN"; return 0; fi
	local dir="$CI_CACHE/acorn-$ACORN_VERSION"
	if [ ! -f "$dir/node_modules/acorn/package.json" ]; then
		npm install --silent --no-audit --no-fund --prefix "$dir" "acorn@$ACORN_VERSION" > /dev/null || return 1
	fi
	echo "$dir/node_modules/acorn"
}

check_names() {
	local acorn
	acorn=$(acorn_path) || { echo "cannot install acorn@$ACORN_VERSION with npm"; return 1; }
	ACORN="$acorn" node "$LUCI/tests/check_names.js" "$LUCI"
}

command -v python3 > /dev/null || ci_die "python3 is required"
command -v node > /dev/null || ci_die "node is required"
ci_log "static checks of $ROOT"

step cr python3 "$CI_DIR/check_cr.py" "$ROOT"
if python3 -c 'import babel' 2> /dev/null; then
	step check_static python3 "$LUCI/tests/check_static.py" "$LUCI"
	step negative python3 "$LUCI/tests/negative_controls.py" "$LUCI"
else
	# Not a skip: without babel the i18n checks cannot run, and CI must say so loudly.
	echo "FAIL check_static: python3 module babel is missing (pip install babel)"
	FAILED=$((FAILED + 1))
	FAILED_NAMES="$FAILED_NAMES check_static"
fi
step check_names check_names
step node check_node
if [ -d "$ROOT/upstream/zapret/nfq" ] && [ -d "$ROOT/upstream/lists-refs/curated" ]; then
	step bundle env PYTHONUTF8=1 PYTHONDONTWRITEBYTECODE=1 python3 "$ROOT/tools/data/test_build_bundle.py"
else
	skip bundle "upstream/ (zapret sources, curated lists) is not in git - run tools/data/test_build_bundle.py locally"
fi
step sh check_sh
step python check_python

echo "static-checks: $PASSED passed, $FAILED failed, $SKIPPED skipped${FAILED_NAMES:+ (failed:$FAILED_NAMES)}"
[ "$FAILED" = 0 ]
