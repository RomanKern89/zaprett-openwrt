#!/bin/bash
# Throw-away signing keys for CI builds (same commands as "build/remote.py keys-init", in an SDK container
# without network):
#
#   bash tools/ci/gen-keys.sh <dir> [sdk image]
#
# Writes <dir>/private-key.pem, public-key.pem (apk, ECDSA prime256v1) and key-build, key-build.pub (usign),
# the layout build/build.sh expects in WORK/keys. Refuses to overwrite existing keys. Packages signed with these
# keys are for tests only: routers trust only the release keys (build/keys/*.pub).
set -euo pipefail
. "$(dirname "$0")/lib.sh"

DIR=${1:?usage: gen-keys.sh <dir> [sdk image]}
IMAGE=${2:-openwrt/sdk:x86_64-v25.12.5}
mkdir -p "$DIR"
DIR=$(cd "$DIR" && pwd)
for f in private-key.pem public-key.pem key-build key-build.pub; do
	[ ! -e "$DIR/$f" ] || ci_die "$DIR/$f exists, refusing to overwrite"
done
chmod 700 "$DIR"
docker image inspect "$IMAGE" > /dev/null 2>&1 || docker pull -q "$IMAGE" > /dev/null
# The SDK user (uid 1000) writes into a scratch volume; the files are then copied out with the caller's uid.
docker run --rm --network none --user root -v "$DIR:/out" -e OWNER="$(id -u):$(id -g)" "$IMAGE" bash -c '
	set -e
	umask 077
	cd /tmp
	openssl ecparam -name prime256v1 -genkey -noout -out private-key.pem
	openssl ec -in private-key.pem -pubout -out public-key.pem 2> /dev/null
	/builder/staging_dir/host/bin/usign -G -s key-build -p key-build.pub -c "zaprett CI throw-away key"
	cp private-key.pem public-key.pem key-build key-build.pub /out/
	chmod 600 /out/private-key.pem /out/key-build
	chmod 644 /out/public-key.pem /out/key-build.pub
	chown "$OWNER" /out/private-key.pem /out/public-key.pem /out/key-build /out/key-build.pub'
for f in private-key.pem public-key.pem key-build key-build.pub; do
	[ -s "$DIR/$f" ] || ci_die "$DIR/$f was not created"
done
grep -q 'BEGIN PUBLIC KEY' "$DIR/public-key.pem" || ci_die "public-key.pem is not a PEM public key"
ci_log "one-off keys in $DIR: apk $(sha256sum "$DIR/public-key.pem" | cut -c1-16)..., usign $(sed -n 2p "$DIR/key-build.pub" | cut -c1-16)..."
