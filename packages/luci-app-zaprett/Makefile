# SPDX-License-Identifier: MIT
#
# LuCI web interface for zaprett (DPI bypass based on nfqws).

include $(TOPDIR)/rules.mk

PKG_NAME:=luci-app-zaprett
PKG_VERSION:=1.1.0
PKG_RELEASE:=1

PKG_LICENSE:=MIT

LUCI_TITLE:=LuCI support for zaprett (DPI bypass)
LUCI_DESCRIPTION:=Web interface for zaprett: service control, strategies, \
	domain and IP network lists, repository, automatic strategy selection \
	and diagnostics.
LUCI_DEPENDS:=+luci-base +rpcd-mod-ucode +zaprett
LUCI_PKGARCH:=all

include $(TOPDIR)/feeds/luci/luci.mk

# call BuildPackage - OpenWrt buildroot signature
