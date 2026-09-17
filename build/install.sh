#!/bin/sh
# Установщик zaprett из релизного бандла для OpenWrt 25.12 (apk) и 24.10 (opkg).
#
#   sh install.sh                 установить или обновить zaprett, zaprett-nfqws, luci-app-zaprett, русский перевод
#   sh install.sh --with-nfqws2   дополнительно поставить движок nfqws2
#   sh install.sh --uninstall     удалить пакеты zaprett (настройки остаются)
#   sh install.sh --uninstall --purge   удалить пакеты, настройки и ключ репозитория zaprett
#   --force                       не останавливаться на несовпадении версии OpenWrt или смене ключа
#
# Пакеты ставятся из подписанного фида внутри бандла как из обычного репозитория:
# apk получает его через --repository (файл packages.adb), opkg — через временный конфиг с src/gz file://.
# Поэтому apk не считает их «non-repository», подпись индекса проверяется ключом из /etc/apk/keys
# или /etc/opkg/keys, а сами файлы бандла в системе не остаются.

set -u

FEED_NAME="zaprett_bundle"
KEY_APK="/etc/apk/keys/zaprett.pem"
MENU_PATH="cgi-bin/luci/admin/services/zaprett"

say() { printf '%s\n' "$*"; }
warn() { printf 'ВНИМАНИЕ: %s\n' "$*" >&2; }
die() { printf 'ОШИБКА: %s\n' "$*" >&2; exit 1; }

usage() {
	sed -n '2,10p' "$0" | sed 's/^# \{0,1\}//'
	exit "${1:-0}"
}

MODE="install"
WITH_NFQWS2=0
PURGE=0
FORCE=0
while [ $# -gt 0 ]; do
	case "$1" in
		--uninstall) MODE="uninstall" ;;
		--purge) PURGE=1 ;;
		--with-nfqws2) WITH_NFQWS2=1 ;;
		--force) FORCE=1 ;;
		-h|--help) usage 0 ;;
		*) warn "неизвестный параметр: $1"; usage 2 ;;
	esac
	shift
done

[ "$(id -u)" = "0" ] || die "запустите от root"

SELF_DIR=$(cd "$(dirname "$0")" && pwd) || die "не удалось определить каталог бандла"
INFO="$SELF_DIR/bundle.info"
[ -f "$INFO" ] || die "нет файла bundle.info рядом с install.sh — распакуйте бандл целиком"

info_get() {
	sed -n "s/^$1=//p" "$INFO" | head -n 1
}

B_SERIES=$(info_get series)
B_ARCH=$(info_get arch)
B_PM=$(info_get pm)
B_VERSION=$(info_get version)
[ -n "$B_SERIES" ] && [ -n "$B_ARCH" ] && [ -n "$B_PM" ] || die "bundle.info повреждён"

release_get() {
	sed -n "s/^$1=['\"]\{0,1\}\([^'\"]*\)['\"]\{0,1\}\$/\1/p" /etc/openwrt_release 2>/dev/null | head -n 1
}

if [ -x /usr/bin/apk ] || command -v apk >/dev/null 2>&1; then
	PM="apk"
elif command -v opkg >/dev/null 2>&1; then
	PM="opkg"
else
	die "не найден ни apk, ни opkg — это не OpenWrt 24.10/25.12?"
fi

detect_arch() {
	if [ "$PM" = "apk" ] && [ -s /etc/apk/arch ]; then
		head -n 1 /etc/apk/arch
		return
	fi
	if [ "$PM" = "opkg" ]; then
		a=$(opkg print-architecture 2>/dev/null | awk '$1 == "arch" && $2 != "all" && $2 != "noarch" { if ($3 + 0 >= best) { best = $3 + 0; name = $2 } } END { print name }')
		if [ -n "$a" ]; then
			echo "$a"
			return
		fi
	fi
	release_get DISTRIB_ARCH
}

ARCH=$(detect_arch)
RELEASE=$(release_get DISTRIB_RELEASE)

is_installed() {
	if [ "$PM" = "apk" ]; then
		apk info -e "$1" >/dev/null 2>&1
	else
		opkg status "$1" 2>/dev/null | grep -q '^Status: install .* installed'
	fi
}

say "zaprett: бандл $B_VERSION для OpenWrt $B_SERIES ($B_PM), архитектура $B_ARCH"
say "Роутер: OpenWrt ${RELEASE:-?}, менеджер пакетов $PM, архитектура ${ARCH:-?}"

[ "$PM" = "$B_PM" ] || die "бандл для $B_PM (OpenWrt $B_SERIES), а на роутере $PM. Скачайте бандл для своей версии OpenWrt."

# ---------------------------------------------------------------- удаление
if [ "$MODE" = "uninstall" ]; then
	present=""
	for p in luci-i18n-zaprett-ru luci-app-zaprett zaprett zaprett-nfqws2 zaprett-nfqws; do
		is_installed "$p" && present="$present $p"
	done
	if [ -n "$present" ]; then
		say "Удаляю пакеты:$present"
		if [ -x /etc/init.d/zaprett ]; then
			/etc/init.d/zaprett stop >/dev/null 2>&1
		fi
		if [ "$PM" = "apk" ]; then
			# shellcheck disable=SC2086
			apk del $present || die "apk del завершился с ошибкой"
		else
			# shellcheck disable=SC2086
			opkg remove $present || die "opkg remove завершился с ошибкой"
		fi
		for p in $present; do
			is_installed "$p" && die "пакет $p всё ещё установлен"
		done
	else
		say "Пакеты zaprett не установлены."
	fi
	if [ "$PURGE" = 1 ]; then
		say "Удаляю настройки и ключ репозитория zaprett (--purge)"
		rm -f /etc/config/zaprett /etc/config/zaprett-opkg /etc/config/zaprett.apk-new
		rm -rf /etc/zaprett
		if [ "$PM" = "apk" ]; then
			rm -f "$KEY_APK"
		elif [ -f "$SELF_DIR/keys/zaprett-usign.pub" ]; then
			opkg-key remove "$SELF_DIR/keys/zaprett-usign.pub" >/dev/null 2>&1
		fi
	elif [ -e /etc/config/zaprett ] || [ -d /etc/zaprett ]; then
		say "Настройки оставлены (/etc/config/zaprett, /etc/zaprett). Удалить и их: sh install.sh --uninstall --purge"
	fi
	say "Готово."
	exit 0
fi

# ---------------------------------------------------------------- проверки перед установкой
[ "$ARCH" = "$B_ARCH" ] || die "бандл собран для архитектуры $B_ARCH, а роутер — ${ARCH:-не определена}. Скачайте бандл zaprett-*-$B_SERIES-${ARCH:-<arch>}.tar.gz."

case "$RELEASE" in
	"$B_SERIES"|"$B_SERIES".*) ;;
	*)
		if [ "$FORCE" = 1 ]; then
			warn "бандл для OpenWrt $B_SERIES, а на роутере ${RELEASE:-неизвестная версия}; продолжаю из-за --force"
		else
			die "бандл для OpenWrt $B_SERIES, а на роутере ${RELEASE:-неизвестная версия}. Если уверены — добавьте --force."
		fi
		;;
esac

# Целостность бандла (защита от битой распаковки; подлинность проверяет подпись индекса).
[ -f "$SELF_DIR/SHA256SUMS" ] || die "нет SHA256SUMS в бандле"
bad=0
while read -r sum name; do
	[ -n "$name" ] || continue
	actual=$(sha256sum "$SELF_DIR/$name" 2>/dev/null | cut -d ' ' -f 1)
	if [ "$actual" != "$sum" ]; then
		warn "файл бандла повреждён или отсутствует: $name"
		bad=1
	fi
done < "$SELF_DIR/SHA256SUMS"
[ "$bad" = 0 ] || die "бандл повреждён — скачайте и распакуйте заново"

PKGS="zaprett-nfqws zaprett luci-app-zaprett luci-i18n-zaprett-ru"
[ "$WITH_NFQWS2" = 1 ] && PKGS="zaprett-nfqws2 $PKGS"

TMPD=$(mktemp -d /tmp/zaprett-install.XXXXXX) || die "не удалось создать временный каталог в /tmp"
cleanup() {
	[ -n "${LISTS_DIR:-}" ] && rm -f "$LISTS_DIR/$FEED_NAME" "$LISTS_DIR/$FEED_NAME.sig"
	rm -rf "$TMPD"
}
trap cleanup EXIT
trap 'exit 1' INT TERM
if ! mkdir -p "$TMPD/feed" || ! cp "$SELF_DIR"/feed/* "$TMPD/feed/"; then
	die "не удалось скопировать фид во временный каталог (мало места в /tmp?)"
fi

# ---------------------------------------------------------------- apk (OpenWrt 25.12)
install_apk() {
	key_src="$SELF_DIR/keys/zaprett.pem"
	[ -f "$key_src" ] || die "в бандле нет ключа keys/zaprett.pem"
	if [ -f "$KEY_APK" ] && ! cmp -s "$key_src" "$KEY_APK"; then
		[ "$FORCE" = 1 ] || die "на роутере уже есть другой ключ $KEY_APK. Бандл подписан не тем ключом, что раньше. Если это ожидаемо — добавьте --force."
		warn "ключ репозитория zaprett заменён (--force)"
	fi
	if ! mkdir -p /etc/apk/keys || ! cp "$key_src" "$KEY_APK"; then
		die "не удалось записать $KEY_APK"
	fi
	say "Ключ репозитория: $KEY_APK (sha256 $(sha256sum "$KEY_APK" | cut -d ' ' -f 1))"

	say "Обновляю списки пакетов OpenWrt (apk update)..."
	apk update || warn "apk update завершился с ошибкой — зависимости из репозиториев OpenWrt могут не найтись (нужен интернет)"

	repo="file://$TMPD/feed/packages.adb"
	installed=""
	for p in $PKGS; do
		is_installed "$p" && installed="$installed $p"
	done
	if [ -n "$installed" ]; then
		say "Обновляю установленные пакеты zaprett:$installed"
		# shellcheck disable=SC2086
		apk upgrade --no-self-upgrade --repository "$repo" $installed || die "apk upgrade завершился с ошибкой"
	fi
	say "Устанавливаю: $PKGS"
	# shellcheck disable=SC2086
	apk add --repository "$repo" $PKGS || die "apk add завершился с ошибкой (см. сообщения выше)"
}

# ---------------------------------------------------------------- opkg (OpenWrt 24.10)
install_opkg() {
	key_src="$SELF_DIR/keys/zaprett-usign.pub"
	[ -f "$key_src" ] || die "в бандле нет ключа keys/zaprett-usign.pub"
	command -v usign >/dev/null 2>&1 || die "нет утилиты usign — проверить подпись фида нельзя"
	fp=$(usign -F -p "$key_src") || die "ключ в бандле повреждён"
	key_dst="/etc/opkg/keys/$fp"
	if [ -f "$key_dst" ] && ! cmp -s "$key_src" "$key_dst"; then
		[ "$FORCE" = 1 ] || die "на роутере уже есть другой ключ $key_dst. Если это ожидаемо — добавьте --force."
		warn "ключ репозитория zaprett заменён (--force)"
	fi
	opkg-key add "$key_src" || die "opkg-key add завершился с ошибкой"
	[ -f "$key_dst" ] || die "ключ не появился в /etc/opkg/keys"
	say "Ключ репозитория: $key_dst"

	# Подпись проверяем сами — даже если в /etc/opkg.conf отключён check_signature.
	opkg-key verify "$TMPD/feed/Packages.sig" "$TMPD/feed/Packages.gz" \
		|| die "подпись фида бандла не прошла проверку — бандл повреждён или подменён"

	LISTS_DIR=$(awk '$1 == "lists_dir" { print $3 }' /etc/opkg.conf 2>/dev/null | head -n 1)
	[ -n "$LISTS_DIR" ] || LISTS_DIR="/var/opkg-lists"
	conf="$TMPD/opkg.conf"
	cat /etc/opkg.conf > "$conf" || die "не удалось прочитать /etc/opkg.conf"
	printf 'src/gz %s file://%s/feed\n' "$FEED_NAME" "$TMPD" >> "$conf"

	say "Обновляю списки пакетов (opkg update)..."
	opkg -f "$conf" update || warn "opkg update сообщил об ошибках — зависимости из репозиториев OpenWrt могут не найтись (нужен интернет)"
	[ -s "$LISTS_DIR/$FEED_NAME" ] || die "opkg не принял фид бандла (ошибка подписи или чтения), см. сообщения выше"

	say "Устанавливаю: $PKGS"
	# shellcheck disable=SC2086
	opkg -f "$conf" install $PKGS || die "opkg install завершился с ошибкой (см. сообщения выше)"
}

if [ "$PM" = "apk" ]; then
	install_apk
else
	install_opkg
fi

missing=""
for p in $PKGS; do
	is_installed "$p" || missing="$missing $p"
done
[ -z "$missing" ] || die "после установки не найдены пакеты:$missing"

lan=$(uci -q get network.lan.ipaddr 2>/dev/null)
lan=${lan%% *}
lan=${lan%%/*}
[ -n "$lan" ] || lan="<адрес роутера>"

say ""
say "Готово: zaprett установлен."
say "Откройте в браузере http://$lan/$MENU_PATH"
say "(LuCI -> Службы -> zaprett). Если пункта меню нет — выйдите из LuCI и войдите снова."
