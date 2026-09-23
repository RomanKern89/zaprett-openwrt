#!/bin/bash
# Merge the per-series dist/ trees of a release build into one release and one GitHub Pages site:
#
#   bash tools/ci/merge-dist.sh <out> <version> <dist of series>...
#
#   <out>/dist/     releases/*.tar.gz, keys/, <series>/<arch>/ feeds, verify-*.txt, refused-*.txt, arches.txt,
#                   SHA256SUMS (recomputed over the merged tree)
#   <out>/assets/   files for the GitHub Release: bundles, public keys, SHA256SUMS of these files
#   <out>/site/     GitHub Pages: <series>/<arch>/ signed feeds + keys/ + index.html
#   <out>/notes.md  release notes (bundle table, key fingerprints)
# Every series must have been built with the same keys, and the bundle versions must equal <version>.
set -euo pipefail
. "$(dirname "$0")/lib.sh"

OUTDIR=${1:?usage: merge-dist.sh <out> <version> <dist>...}
VERSION=${2:?usage: merge-dist.sh <out> <version> <dist>...}
shift 2
[ $# -gt 0 ] || ci_die "no dist directories given"
rm -rf "$OUTDIR"
D="$OUTDIR/dist"
mkdir -p "$D/releases" "$D/keys" "$OUTDIR/assets" "$OUTDIR/site/keys"

for src in "$@"; do
	[ -f "$src/SHA256SUMS" ] || ci_die "$src/SHA256SUMS is missing"
	( cd "$src" && sha256sum --quiet -c SHA256SUMS ) || ci_die "$src does not match its SHA256SUMS"
	for k in zaprett.pem zaprett-usign.pub; do
		if [ -f "$D/keys/$k" ]; then
			cmp -s "$D/keys/$k" "$src/keys/$k" || ci_die "series were signed with different keys ($k)"
		else
			cp "$src/keys/$k" "$D/keys/$k"
		fi
	done
	for f in "$src"/verify-*.txt "$src"/refused-*.txt; do
		[ -f "$f" ] || continue
		if grep -q '^FAIL' "$f"; then ci_die "$f has FAIL lines"; fi
		cp "$f" "$D/"
	done
	cp "$src/arches.txt" "$D/arches.txt"
	cp "$src"/releases/*.tar.gz "$D/releases/"
	for s in 25.12 24.10; do
		[ -d "$src/$s" ] && cp -a "$src/$s" "$D/"
	done
done
for b in "$D"/releases/*.tar.gz; do
	case "$(basename "$b")" in
		zaprett-"$VERSION"-*) ;;
		*) ci_die "$(basename "$b") is not version $VERSION" ;;
	esac
done
( cd "$D" && find . -type f ! -name SHA256SUMS | sed 's|^\./||' | LC_ALL=C sort | xargs sha256sum > SHA256SUMS )

cp "$D"/releases/*.tar.gz "$OUTDIR/assets/"
cp "$D/keys/zaprett.pem" "$OUTDIR/assets/zaprett.pem"
cp "$D/keys/zaprett-usign.pub" "$OUTDIR/assets/zaprett-usign.pub"
( cd "$OUTDIR/assets" && find . -maxdepth 1 -type f ! -name SHA256SUMS | sed 's|^\./||' | LC_ALL=C sort | xargs sha256sum > SHA256SUMS )

for s in 25.12 24.10; do
	[ -d "$D/$s" ] && cp -a "$D/$s" "$OUTDIR/site/"
done
cp "$D"/keys/* "$OUTDIR/site/keys/"
{
	echo '<!doctype html><meta charset="utf-8"><title>zaprett OpenWrt feeds</title>'
	echo "<h1>zaprett $VERSION: OpenWrt package feeds</h1>"
	echo '<p>Connect with <code>sh install.sh --feed</code> from a release bundle; see docs/INSTALL.md.</p><ul>'
	for f in "$OUTDIR"/site/*/*/; do
		rel=${f#"$OUTDIR/site/"}
		[ "${rel%%/*}" = keys ] || echo "<li><a href=\"$rel\">$rel</a></li>"
	done
	echo '</ul><p><a href="keys/">keys/</a></p>'
} > "$OUTDIR/site/index.html"

apk_fp=$(sha256sum "$D/keys/zaprett.pem" | cut -d ' ' -f 1)
usign_fp=$(sed -n 2p "$D/keys/zaprett-usign.pub")
{
	echo "zaprett $VERSION for OpenWrt 25.12 (apk) and 24.10 (opkg)."
	echo
	echo "Install: download the bundle for your OpenWrt version and architecture, then \`sh install.sh\` (docs/INSTALL.md)."
	echo "Online feed for updates: \`sh install.sh --feed\`."
	echo
	echo "| OpenWrt | Architecture | Bundle |"
	echo "|---|---|---|"
	for b in "$D"/releases/*.tar.gz; do
		n=$(basename "$b" .tar.gz)
		rest=${n#zaprett-"$VERSION"-}
		echo "| ${rest%%-*} | ${rest#*-} | \`$n.tar.gz\` |"
	done
	echo
	echo "Signing keys: apk \`zaprett.pem\` sha256 \`$apk_fp\`; usign \`$usign_fp\`."
	echo "Checks: $(cat "$D"/verify-*.txt | grep -c '^OK') verify.py checks, 0 FAIL."
} > "$OUTDIR/notes.md"
ci_log "merged: $(ls "$D/releases" | wc -l) bundles, $(find "$OUTDIR/site" -name 'packages.adb' -o -name 'Packages' | wc -l) feeds, $(du -sh "$OUTDIR/site" | cut -f1) site"
