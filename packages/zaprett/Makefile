#
# zaprett for OpenWrt: backend (CLI, ucode library, procd service, nftables, repository client,
# strategy tester) and the built-in bundle of lists, strategies and fake payloads.
#
# Architecture-independent package (PKGARCH:=all); the nfqws binaries come from zaprett-nfqws*.
#

include $(TOPDIR)/rules.mk

PKG_NAME:=zaprett
PKG_VERSION:=1.0.0
PKG_RELEASE:=1

PKG_MAINTAINER:=zaprett-openwrt
PKG_LICENSE:=MIT

include $(INCLUDE_DIR)/package.mk

define Package/zaprett
  SECTION:=net
  CATEGORY:=Network
  TITLE:=zaprett - DPI bypass manager based on nfqws
  URL:=https://github.com/CherretGit/zaprett-app
  PKGARCH:=all
  DEPENDS:=+ucode +ucode-mod-fs +ucode-mod-uci +ucode-mod-ubus +nftables +kmod-nft-queue +uclient-fetch +ca-bundle +zaprett-nfqws
endef

define Package/zaprett/description
  Port of the Android DPI bypass manager zaprett to OpenWrt 24.10 and 25.12:
  nfqws strategies with hostlists and ipsets, table inet zaprett for nftables,
  zaprett-repo client with sha256 checks and automatic strategy selection.
endef

define Package/zaprett/conffiles
/etc/config/zaprett
/etc/zaprett/user/hosts-include.txt
/etc/zaprett/user/hosts-exclude.txt
/etc/zaprett/user/ipset-include.txt
/etc/zaprett/user/ipset-exclude.txt
endef

define Build/Configure
endef

define Build/Compile
endef

define Package/zaprett/install
	$(INSTALL_DIR) $(1)
	$(CP) ./files/. $(1)/
	find $(1) -type d -exec chmod 0755 {} +
	find $(1) -type f -exec chmod 0644 {} +
	chmod 0755 $(1)/usr/bin/zaprett
	chmod 0755 $(1)/etc/init.d/zaprett
	chmod 0755 $(1)/etc/uci-defaults/90-zaprett
	chmod 0755 $(1)/etc/hotplug.d/iface/60-zaprett
	chmod 0755 $(1)/usr/share/zaprett/probe.sh
	chmod 0600 $(1)/etc/config/zaprett
endef

define Package/zaprett/postinst
#!/bin/sh
if [ -z "$${IPKG_INSTROOT}" ]; then
	rm -rf /var/run/zaprett/cache
fi
exit 0
endef

define Package/zaprett/prerm
#!/bin/sh
if [ -z "$${IPKG_INSTROOT}" ] && [ "$${PKG_UPGRADE}" != "1" ] && [ "$$1" != "upgrade" ]; then
	/usr/bin/zaprett job cancel --quiet >/dev/null 2>&1
	/etc/init.d/zaprett stop >/dev/null 2>&1
	/usr/bin/zaprett offload restore --quiet >/dev/null 2>&1
	nft delete table inet zaprett >/dev/null 2>&1
	if [ "$$(uci -q get firewall.zaprett)" = "include" ]; then
		uci -q delete firewall.zaprett
		uci -q commit firewall
	fi
	if [ -f /etc/crontabs/root ] && grep -q '# zaprett-autoupdate' /etc/crontabs/root; then
		sed -i '/# zaprett-autoupdate/d' /etc/crontabs/root
		/etc/init.d/cron restart >/dev/null 2>&1
	fi
	rm -rf /var/run/zaprett
fi
exit 0
endef

# opkg and apk delete files but keep directories: after removal the whole tree of empty directories stayed
# behind (23 of them under /usr/share/zaprett). Children come before parents (`sort -r`), and
# `rmdir` removes a directory only when it is empty, so a directory that still holds files of another
# package (/usr/share/zaprett/lua of zaprett-nfqws2) and its parents are kept.
# `find -empty` is not available in busybox on 24.10.8 and 25.12.5 — checked, it silently matches nothing.
# /etc/zaprett holds user data and is removed by install.sh --purge, not here.
define Package/zaprett/postrm
#!/bin/sh
if [ -z "$${IPKG_INSTROOT}" ] && [ "$${PKG_UPGRADE}" != "1" ] && [ "$$1" != "upgrade" ]; then
	for dir in /usr/share/zaprett /usr/share/ucode/zaprett; do
		[ -d "$$dir" ] || continue
		find "$$dir" -type d | sort -r | while read -r d; do
			rmdir "$$d" 2>/dev/null
		done
	done
fi
exit 0
endef

$(eval $(call BuildPackage,zaprett))
