#!/bin/bash
# Release signing keys from the environment (GitHub environment "release" secrets) into the layout build.sh expects:
#
#   ZAPRETT_APK_PRIVATE_KEY  ZAPRETT_APK_PUBLIC_KEY  ZAPRETT_USIGN_SECRET  ZAPRETT_USIGN_PUBLIC
#   bash tools/ci/release-keys.sh <dir> [sdk image]
#
# Writes <dir>/private-key.pem, public-key.pem, key-build, key-build.pub and refuses to continue unless
#   * both public keys are byte-identical to the committed build/keys/zaprett-apk.pub and zaprett-usign.pub
#     (routers already trust those: another key would break every update);
#   * the apk private key derives exactly that public key (openssl);
#   * the usign secret key signs a test message that verifies with that public key (usign in the SDK image),
#     and a modified message does not verify (negative control).
set -euo pipefail
. "$(dirname "$0")/lib.sh"

DIR=${1:?usage: release-keys.sh <dir> [sdk image]}
IMAGE=${2:-openwrt/sdk:x86_64-v25.12.5}
for v in ZAPRETT_APK_PRIVATE_KEY ZAPRETT_APK_PUBLIC_KEY ZAPRETT_USIGN_SECRET ZAPRETT_USIGN_PUBLIC; do
	[ -n "${!v:-}" ] || ci_die "secret $v is empty (environment \"release\" of the repository)"
done
mkdir -p "$DIR"
chmod 700 "$DIR"
umask 077
printf '%s\n' "$ZAPRETT_APK_PRIVATE_KEY" | sed '/^$/d' > "$DIR/private-key.pem"
printf '%s\n' "$ZAPRETT_USIGN_SECRET" | sed '/^$/d' > "$DIR/key-build"
umask 022
printf '%s\n' "$ZAPRETT_APK_PUBLIC_KEY" | sed '/^$/d' > "$DIR/public-key.pem"
printf '%s\n' "$ZAPRETT_USIGN_PUBLIC" | sed '/^$/d' > "$DIR/key-build.pub"

cmp -s "$DIR/public-key.pem" "$CI_ROOT/build/keys/zaprett-apk.pub" \
	|| ci_die "ZAPRETT_APK_PUBLIC_KEY differs from build/keys/zaprett-apk.pub"
cmp -s "$DIR/key-build.pub" "$CI_ROOT/build/keys/zaprett-usign.pub" \
	|| ci_die "ZAPRETT_USIGN_PUBLIC differs from build/keys/zaprett-usign.pub"
derived=$(openssl ec -in "$DIR/private-key.pem" -pubout 2> /dev/null) || ci_die "ZAPRETT_APK_PRIVATE_KEY is not an EC private key"
[ "$derived" = "$(cat "$DIR/public-key.pem")" ] || ci_die "ZAPRETT_APK_PRIVATE_KEY does not belong to the committed public key"

docker image inspect "$IMAGE" > /dev/null 2>&1 || docker pull -q "$IMAGE" > /dev/null
docker run --rm --network none --user root -v "$DIR:/k:ro" "$IMAGE" bash -c '
	set -e
	u=/builder/staging_dir/host/bin/usign
	cd /tmp
	echo "zaprett release key check" > msg
	$u -S -m msg -s /k/key-build -x msg.sig
	$u -V -m msg -p /k/key-build.pub -x msg.sig -q
	echo "tampered" >> msg
	if $u -V -m msg -p /k/key-build.pub -x msg.sig -q 2> /dev/null; then echo "usign accepted a modified message"; exit 1; fi' \
	|| ci_die "ZAPRETT_USIGN_SECRET does not sign for the committed usign public key"
ci_log "release keys OK: apk $(sha256sum "$DIR/public-key.pem" | cut -c1-16)..., usign $(sed -n 2p "$DIR/key-build.pub" | cut -c1-16)..."
