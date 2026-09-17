#
# zaprett-nfqws2: static nfqws2 and its Lua libraries from the bol-van/zapret2 release, packaged for zaprett.
#
# The binary is not compiled: it is taken from the release "openwrt-embedded" archive (pinned by PKG_HASH).
# One x86_64 SDK can produce the package for any architecture:
#   make package/zaprett-nfqws2/compile ZAPRETT_ARCH=aarch64_cortex-a53
# Without ZAPRETT_ARCH the SDK architecture is used.
#

include $(TOPDIR)/rules.mk

PKG_NAME:=zaprett-nfqws2
PKG_VERSION:=1.0.5.2
PKG_RELEASE:=1

PKG_SOURCE:=zapret2-v$(PKG_VERSION)-openwrt-embedded.tar.gz
PKG_SOURCE_URL:=https://github.com/bol-van/zapret2/releases/download/v$(PKG_VERSION)/
PKG_HASH:=9a2b08ec8c4b40f908e55684ea29d40b2316a1f2eebb08ea6afcb1e5c9c0fc89

PKG_LICENSE:=MIT
PKG_MAINTAINER:=zaprett for OpenWrt

ZAPRETT_ARCH ?= $(ARCH_PACKAGES)

# OpenWrt package architecture -> directory in binaries/ and expected ELF "<class> <data> <machine>".
# linux-arm is built for ARMv6KZ (readelf -A: Tag_CPU_arch v6KZ), so ARMv4/ARMv5 cores are not supported.
# No binaries in the release: armeb, mips64el, powerpc64, loongarch64.
ZAPRETT_BINDIR:=
ifneq ($(filter aarch64_%,$(ZAPRETT_ARCH)),)
  ZAPRETT_BINDIR:=linux-arm64
  ZAPRETT_ELF:=2 1 183
endif
ifneq ($(filter arm_%,$(filter-out arm_arm926ej-s arm_fa526 arm_xscale,$(ZAPRETT_ARCH))),)
  ZAPRETT_BINDIR:=linux-arm
  ZAPRETT_ELF:=1 1 40
endif
ifneq ($(filter mipsel_%,$(ZAPRETT_ARCH)),)
  ZAPRETT_BINDIR:=linux-mipsel
  ZAPRETT_ELF:=1 1 8
endif
ifneq ($(filter mips_%,$(ZAPRETT_ARCH)),)
  ZAPRETT_BINDIR:=linux-mips
  ZAPRETT_ELF:=1 2 8
endif
ifneq ($(filter mips64_%,$(ZAPRETT_ARCH)),)
  ZAPRETT_BINDIR:=linux-mips64
  ZAPRETT_ELF:=2 2 8
endif
ifneq ($(filter i386_%,$(ZAPRETT_ARCH)),)
  ZAPRETT_BINDIR:=linux-x86
  ZAPRETT_ELF:=1 1 3
endif
ifeq ($(ZAPRETT_ARCH),x86_64)
  ZAPRETT_BINDIR:=linux-x86_64
  ZAPRETT_ELF:=2 1 62
endif
ifneq ($(filter powerpc_%,$(ZAPRETT_ARCH)),)
  ZAPRETT_BINDIR:=linux-ppc
  ZAPRETT_ELF:=1 2 20
endif
ifneq ($(filter riscv64_%,$(ZAPRETT_ARCH)),)
  ZAPRETT_BINDIR:=linux-riscv64
  ZAPRETT_ELF:=2 1 243
endif

# Separate build directory per architecture: stamps of one architecture never satisfy another.
PKG_BUILD_DIR:=$(BUILD_DIR)/$(PKG_NAME)-$(PKG_VERSION)/$(ZAPRETT_ARCH)

include $(INCLUDE_DIR)/package.mk

ifeq ($(DUMP),)
  ifeq ($(ZAPRETT_BINDIR),)
    $(error $(PKG_NAME): zapret2 v$(PKG_VERSION) has no static nfqws2 for architecture '$(ZAPRETT_ARCH)')
  endif
endif

# Foreign-architecture UPX-packed binaries must not go through the SDK strip.
STRIP:=:
RSTRIP:=:

define Package/zaprett-nfqws2
  SECTION:=net
  CATEGORY:=Network
  SUBMENU:=zaprett
  TITLE:=nfqws2 Lua DPI bypass engine for zaprett (zapret2 v$(PKG_VERSION), static)
  URL:=https://github.com/bol-van/zapret2
  PKGARCH:=$(ZAPRETT_ARCH)
endef

define Package/zaprett-nfqws2/description
  Static nfqws2 binary from the bol-van/zapret2 v$(PKG_VERSION) release
  (openwrt-embedded archive, directory binaries/$(ZAPRETT_BINDIR)) and its gzip-compressed
  Lua libraries: /usr/libexec/zaprett/nfqws2, /usr/share/zaprett/lua/*.lua.gz.
endef

define Build/Prepare
	rm -rf $(PKG_BUILD_DIR)
	mkdir -p $(PKG_BUILD_DIR)
	$(HOST_TAR) -C $(PKG_BUILD_DIR) --strip-components=1 -xzf $(DL_DIR)/$(PKG_SOURCE) \
		zapret2-v$(PKG_VERSION)/binaries/$(ZAPRETT_BINDIR)/nfqws2 \
		zapret2-v$(PKG_VERSION)/lua
endef

define Build/Configure
endef

define Build/Compile
	sh $(CURDIR)/elf-check.sh $(PKG_BUILD_DIR)/binaries/$(ZAPRETT_BINDIR)/nfqws2 "$(ZAPRETT_ELF)"
	ls $(PKG_BUILD_DIR)/lua/zapret-lib.lua.gz $(PKG_BUILD_DIR)/lua/zapret-antidpi.lua.gz
endef

define Package/zaprett-nfqws2/install
	$(INSTALL_DIR) $(1)/usr/libexec/zaprett
	$(INSTALL_BIN) $(PKG_BUILD_DIR)/binaries/$(ZAPRETT_BINDIR)/nfqws2 $(1)/usr/libexec/zaprett/nfqws2
	$(INSTALL_DIR) $(1)/usr/share/zaprett/lua
	$(INSTALL_DATA) $(PKG_BUILD_DIR)/lua/*.lua.gz $(1)/usr/share/zaprett/lua/
endef

# opkg and apk delete files but keep directories: after removing the engine /usr/libexec/zaprett and
# /usr/share/zaprett/lua stayed behind empty. rmdir without -p removes them only when they are
# empty, so while the other engine (zaprett-nfqws) is installed /usr/libexec/zaprett is kept, and
# /usr/share/zaprett itself belongs to the zaprett package and is never touched here.
define Package/zaprett-nfqws2/postrm
#!/bin/sh
if [ -z "$${IPKG_INSTROOT}" ]; then
	rmdir /usr/share/zaprett/lua 2>/dev/null
	rmdir /usr/libexec/zaprett 2>/dev/null
fi
exit 0
endef

$(eval $(call BuildPackage,zaprett-nfqws2))
