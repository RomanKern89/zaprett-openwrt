#!/usr/bin/env python3
"""Fact checks of built zaprett feeds. Runs INSIDE the OpenWrt SDK container (python 3.9+), started by build.sh.

For every architecture feed in /out/feed/<arch>:
  * the expected package files are present (matrix + noarch list);
  * package metadata (apk: adbdump json, ipk: control) - name, version, architecture, files;
  * nfqws/nfqws2 inside the package: ELF class/endianness/machine match the architecture, static,
    sha256 equals the release sha256sum.txt (downloaded from GitHub) and the pinned release archive;
  * Lua libraries of nfqws2 are byte-identical to the release archive;
  * the index signature verifies with our public key (positive control) and fails with a foreign key
    and with a corrupted index (negative controls); apk: tampered package is rejected by hash.
Writes /out/verify-report.txt; exit code 1 if anything failed.

Usage: verify.py --series 25.12 --arches "x86_64 ..." --noarch "zaprett luci-app-zaprett"
"""
import argparse
import gzip
import hashlib
import io
import json
import os
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import urllib.request

SDK = "/builder"
APK = SDK + "/staging_dir/host/bin/apk"
USIGN = SDK + "/staging_dir/host/bin/usign"
MATRIX = "/src/build/arches.txt"
DL = SDK + "/dl"
OUT = "/out"

BINARIES = {"zaprett-nfqws": "nfqws", "zaprett-nfqws2": "nfqws2"}


def load_release(pkg):
    """Expected version and release archive of an nfqws package, taken from its Makefile (single source of truth)."""
    text = open("/src/packages/%s/Makefile" % pkg, encoding="utf-8").read()
    var = dict(re.findall(r"^(PKG_[A-Z_]+):=(.*)$", text, re.M))
    version = var["PKG_VERSION"].strip()
    source = var["PKG_SOURCE"].replace("$(PKG_VERSION)", version).strip()
    url = var["PKG_SOURCE_URL"].replace("$(PKG_VERSION)", version).strip()
    m = re.match(r"^https://github\.com/bol-van/([^/]+)/releases/download/(v[^/]+)/$", url)
    if not m or not source.endswith("-openwrt-embedded.tar.gz"):
        raise RuntimeError("unexpected PKG_SOURCE/PKG_SOURCE_URL in %s: %s %s" % (pkg, source, url))
    return {
        "version": "%s-r%s" % (version, var["PKG_RELEASE"].strip()), "bin": BINARIES[pkg],
        "repo": m.group(1), "tag": m.group(2), "archive": source,
        "prefix": source[:-len("-openwrt-embedded.tar.gz")],
    }


RELEASES = {pkg: load_release(pkg) for pkg in BINARIES}

# arch prefix -> (release dir, ELF class bits, endianness, machine) - independent of the Makefile mapping.
ELF_MACHINES = {3: "i386", 8: "MIPS", 20: "PPC", 40: "ARM", 62: "x86-64", 183: "AArch64", 243: "RISC-V"}


def expected_elf(arch, package):
    if arch.startswith("aarch64_"):
        return "linux-arm64", ("AArch64", 64, "LE")
    if arch.startswith("arm_") and arch not in ("arm_arm926ej-s", "arm_fa526", "arm_xscale"):
        return "linux-arm", ("ARM", 32, "LE")
    if arch.startswith("mipsel_"):
        return "linux-mipsel", ("MIPS", 32, "LE")
    if arch.startswith("mips_"):
        return "linux-mips", ("MIPS", 32, "BE")
    if arch.startswith("mips64_"):
        return "linux-mips64", ("MIPS", 64, "BE")
    if arch.startswith("i386_"):
        return "linux-x86", ("i386", 32, "LE")
    if arch == "x86_64":
        return "linux-x86_64", ("x86-64", 64, "LE")
    if arch.startswith("powerpc_"):
        return "linux-ppc", ("PPC", 32, "BE")
    if arch.startswith("riscv64_") and package == "zaprett-nfqws2":
        return "linux-riscv64", ("RISC-V", 64, "LE")
    return None, None


class Report:
    def __init__(self):
        self.lines = []
        self.failed = 0

    def ok(self, msg):
        self.lines.append("OK   " + msg)

    def fail(self, msg):
        self.lines.append("FAIL " + msg)
        self.failed += 1
        print("FAIL " + msg, flush=True)

    def check(self, cond, msg):
        (self.ok if cond else self.fail)(msg)
        return cond


def run(cmd, **kw):
    return subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, universal_newlines=True, **kw)


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def elf_info(data):
    import struct
    if data[:4] != b"\x7fELF":
        return None
    is64 = data[4] == 2
    endian = "<" if data[5] == 1 else ">"
    machine = struct.unpack_from(endian + "H", data, 18)[0]
    if is64:
        phoff = struct.unpack_from(endian + "Q", data, 32)[0]
        phentsize, phnum = struct.unpack_from(endian + "HH", data, 54)
    else:
        phoff = struct.unpack_from(endian + "I", data, 28)[0]
        phentsize, phnum = struct.unpack_from(endian + "HH", data, 42)
    types = [struct.unpack_from(endian + "I", data, phoff + i * phentsize)[0] for i in range(phnum)]
    static = 2 not in types and 3 not in types  # PT_DYNAMIC, PT_INTERP
    return (ELF_MACHINES.get(machine, str(machine)), 64 if is64 else 32, "LE" if endian == "<" else "BE"), static


def release_sums(pkg):
    """sha256sum.txt of the release, downloaded once into dl/ (independent source from GitHub)."""
    r = RELEASES[pkg]
    path = os.path.join(DL, "%s-%s-sha256sum.txt" % (r["repo"], r["tag"]))
    if not os.path.exists(path):
        url = "https://github.com/bol-van/%s/releases/download/%s/sha256sum.txt" % (r["repo"], r["tag"])
        data = urllib.request.urlopen(url, timeout=60).read()
        with open(path + ".part", "wb") as fh:
            fh.write(data)
        os.replace(path + ".part", path)
    sums = {}
    for line in open(path, encoding="utf-8"):
        parts = line.split()
        if len(parts) == 2:
            sums[parts[1].lstrip("*")] = parts[0]
    return sums


def archive_members(pkg):
    r = RELEASES[pkg]
    path = os.path.join(DL, r["archive"])
    with tarfile.open(path, "r:gz") as tar:
        return {m.name: tar.extractfile(m).read() for m in tar.getmembers() if m.isfile()}


# ----------------------------------------------------------------------------- package readers
def read_apk(path, workdir):
    res = run([APK, "adbdump", "--format", "json", path])
    if res.returncode != 0:
        raise RuntimeError("adbdump failed: " + res.stdout[-500:])
    meta = json.loads(res.stdout)
    info = meta.get("info", {})
    dest = os.path.join(workdir, "root")
    os.makedirs(dest)
    res = run([APK, "extract", "--allow-untrusted", "--destination", dest, path])
    if res.returncode != 0:
        raise RuntimeError("apk extract failed: " + res.stdout[-500:])
    deps = info.get("depends", [])
    return {"name": info.get("name"), "version": info.get("version"), "arch": info.get("arch"),
            "depends": deps, "root": dest}


def read_ipk(path, workdir):
    with tarfile.open(path, "r:gz") as outer:
        names = [m.name for m in outer.getmembers()]
        control_tgz = outer.extractfile("./control.tar.gz").read()
        data_tgz = outer.extractfile("./data.tar.gz").read()
    with tarfile.open(fileobj=io.BytesIO(control_tgz), mode="r:gz") as ctl:
        control = ctl.extractfile("./control").read().decode("utf-8")
    fields = {}
    for line in control.splitlines():
        m = re.match(r"^([A-Za-z-]+):\s*(.*)$", line)
        if m:
            fields[m.group(1)] = m.group(2)
    dest = os.path.join(workdir, "root")
    os.makedirs(dest)
    with tarfile.open(fileobj=io.BytesIO(data_tgz), mode="r:gz") as data:
        for m in data.getmembers():
            if m.name.startswith("/") or ".." in m.name.split("/"):
                raise RuntimeError("unsafe path in data.tar.gz: " + m.name)
        data.extractall(dest)
    deps = [d.strip() for d in fields.get("Depends", "").split(",") if d.strip()]
    return {"name": fields.get("Package"), "version": fields.get("Version"), "arch": fields.get("Architecture"),
            "depends": deps, "root": dest, "members": names}


def package_files(feed, fmt):
    suffix = ".apk" if fmt == "apk" else ".ipk"
    return sorted(f for f in os.listdir(feed) if f.endswith(suffix))


def file_for(files, name, fmt):
    # ipk: name_version_arch.ipk, and the arch itself may contain "_" (aarch64_cortex-a53, x86_64).
    rx = re.compile(r"^%s-[0-9][^/]*\.apk$" % re.escape(name)) if fmt == "apk" else \
        re.compile(r"^%s_[0-9][^_]*_[A-Za-z0-9_-]+\.ipk$" % re.escape(name))
    found = [f for f in files if rx.match(f)]
    return found[0] if len(found) == 1 else None


# ----------------------------------------------------------------------------- checks
def check_arch_package(rep, feed, fname, pkg, arch, fmt, tmp):
    label = "%s %s/%s" % (fmt, arch, pkg)
    meta = read_apk(os.path.join(feed, fname), tmp) if fmt == "apk" else read_ipk(os.path.join(feed, fname), tmp)
    r = RELEASES[pkg]
    rep.check(meta["name"] == pkg, "%s: name %s" % (label, meta["name"]))
    rep.check(meta["version"] == r["version"], "%s: version %s (expected %s)" % (label, meta["version"], r["version"]))
    rep.check(meta["arch"] == arch, "%s: architecture field %s (expected %s)" % (label, meta["arch"], arch))
    rep.check(all(d.split()[0] in ("libc",) for d in meta["depends"]), "%s: depends %s" % (label, meta["depends"]))

    binpath = os.path.join(meta["root"], "usr/libexec/zaprett", r["bin"])
    if not rep.check(os.path.isfile(binpath), "%s: contains /usr/libexec/zaprett/%s" % (label, r["bin"])):
        return
    rep.check(os.stat(binpath).st_mode & 0o111 == 0o111, "%s: binary is executable (mode %o)" % (label, os.stat(binpath).st_mode & 0o777))
    data = open(binpath, "rb").read()
    bindir, want = expected_elf(arch, pkg)
    got = elf_info(data)
    if not rep.check(got is not None, "%s: binary is ELF" % label):
        return
    rep.check(got[0] == want, "%s: ELF %s %d-bit %s, expected %s %d-bit %s" % ((label,) + got[0] + want))
    rep.check(got[1], "%s: ELF is static (no PT_INTERP/PT_DYNAMIC)" % label)
    digest = sha256(data)
    key = "%s/binaries/%s/%s" % (r["prefix"], bindir, r["bin"])
    sums = release_sums(pkg)
    rep.check(sums.get(key) == digest, "%s: sha256 %s == release sha256sum.txt[%s] %s" % (label, digest[:16], key, (sums.get(key) or "missing")[:16]))
    members = archive_members(pkg)
    rep.check(members.get(key) is not None and sha256(members[key]) == digest, "%s: identical to %s in pinned archive" % (label, key))

    if pkg == "zaprett-nfqws2":
        luadir = os.path.join(meta["root"], "usr/share/zaprett/lua")
        shipped = sorted(os.listdir(luadir)) if os.path.isdir(luadir) else []
        expected = sorted(os.path.basename(k) for k in members if k.startswith(r["prefix"] + "/lua/") and k.endswith(".lua.gz"))
        rep.check(shipped == expected and expected, "%s: lua files %s" % (label, shipped))
        same = all(sha256(open(os.path.join(luadir, f), "rb").read()) == sha256(members["%s/lua/%s" % (r["prefix"], f)]) for f in shipped)
        rep.check(same, "%s: lua files identical to the release archive" % label)


def check_noarch_package(rep, feed, fname, name, fmt, tmp):
    label = "%s noarch/%s" % (fmt, name)
    meta = read_apk(os.path.join(feed, fname), tmp) if fmt == "apk" else read_ipk(os.path.join(feed, fname), tmp)
    want = "noarch" if fmt == "apk" else "all"
    rep.check(meta["name"] == name, "%s: name %s" % (label, meta["name"]))
    rep.check(meta["arch"] == want, "%s: architecture field %s (expected %s)" % (label, meta["arch"], want))


def gen_foreign_keys(tmp):
    """A key pair that is NOT ours: negative control for signature checks."""
    d = os.path.join(tmp, "foreign")
    os.makedirs(d)
    run(["openssl", "ecparam", "-name", "prime256v1", "-genkey", "-noout", "-out", d + "/ec.pem"])
    run(["openssl", "ec", "-in", d + "/ec.pem", "-pubout", "-out", d + "/ec.pub.pem"])
    run([USIGN, "-G", "-s", d + "/usign.key", "-p", d + "/usign.pub", "-c", "foreign"])
    return d


def apk_repo_query(feed_index, keys_dir, arch, pkg, tmp, tag):
    """Load the index as a repository like the router does (no --allow-untrusted) and fetch one package."""
    root = os.path.join(tmp, "root-" + tag)
    outdir = os.path.join(tmp, "fetch-" + tag)
    os.makedirs(root)
    os.makedirs(outdir)
    cmd = [APK, "--root", root, "--arch", arch, "--keys-dir", keys_dir, "--repositories-file", "/dev/null",
           "--repository", "file://" + feed_index, "--no-cache", "fetch", "--output", outdir, pkg]
    return run(cmd), outdir


def check_apk_index(rep, feed, arch, tmp, foreign, pkg):
    index = os.path.join(feed, "packages.adb")
    if not rep.check(os.path.isfile(index), "apk %s: packages.adb present" % arch):
        return
    good_keys = os.path.join(tmp, "keys-good")
    os.makedirs(good_keys)
    shutil.copy("/keys/public-key.pem", os.path.join(good_keys, "zaprett.pem"))
    bad_keys = os.path.join(tmp, "keys-bad")
    os.makedirs(bad_keys)
    shutil.copy(foreign + "/ec.pub.pem", os.path.join(bad_keys, "zaprett.pem"))

    dump = run([APK, "--keys-dir", good_keys, "adbdump", index])
    rep.check(dump.returncode == 0 and re.search(r"# sig v\d+ h\d+ .*: OK", dump.stdout) is not None,
              "apk %s: adbdump reports index signature OK with our key" % arch)
    dump_bad = run([APK, "--keys-dir", bad_keys, "adbdump", index])
    rep.check(re.search(r"# sig v\d+ h\d+ .*: OK", dump_bad.stdout) is None,
              "apk %s: adbdump does NOT report OK with a foreign key (negative control)" % arch)

    # Every package file is listed in the index with matching name/version.
    listing = run([APK, "--keys-dir", good_keys, "adbdump", "--format", "json", index])
    listed = set()
    if listing.returncode == 0:
        try:
            for p in json.loads(listing.stdout).get("packages", []):
                listed.add("%s-%s.apk" % (p.get("name"), p.get("version")))
        except ValueError:
            pass
    files = set(package_files(feed, "apk"))
    rep.check(listed == files and files, "apk %s: index lists exactly the feed files (%d)" % (arch, len(files)))

    ok, outdir = apk_repo_query(index, good_keys, arch, pkg, tmp, "good")
    fetched = os.listdir(outdir)
    rep.check(ok.returncode == 0 and len(fetched) == 1,
              "apk %s: repository load + fetch %s with our key rc=%d %s" % (arch, pkg, ok.returncode, fetched))

    # Negative controls must fail for the right reason: the index is rejected as untrusted/broken.
    # (A tampered .apk is not checked here: "apk fetch" only copies files; that case is covered by
    # build/test-install-rootfs.sh with a real "apk add".)
    bad, outdir = apk_repo_query(index, bad_keys, arch, pkg, tmp, "bad")
    rep.check(bad.returncode != 0 and not os.listdir(outdir) and "UNTRUSTED" in bad.stdout,
              "apk %s: foreign key -> index UNTRUSTED, fetch refused rc=%d (negative control): %s"
              % (arch, bad.returncode, [l for l in bad.stdout.splitlines() if "UNTRUSTED" in l][:1]))

    corrupt_feed = os.path.join(tmp, "corrupt-index")
    shutil.copytree(feed, corrupt_feed)
    blob = bytearray(open(index, "rb").read())
    blob[len(blob) // 2] ^= 0xFF
    with open(os.path.join(corrupt_feed, "packages.adb"), "wb") as fh:
        fh.write(bytes(blob))
    cor, outdir = apk_repo_query(os.path.join(corrupt_feed, "packages.adb"), good_keys, arch, pkg, tmp, "corrupt")
    rep.check(cor.returncode != 0 and not os.listdir(outdir),
              "apk %s: corrupted index -> fetch refused rc=%d (negative control): %s"
              % (arch, cor.returncode, [l for l in cor.stdout.splitlines() if "WARNING" in l or "ERROR" in l][:1]))


def usign_verify(keydir_or_pub, packages_bytes, sig, tmp, tag):
    msg = os.path.join(tmp, "msg-" + tag)
    open(msg, "wb").write(packages_bytes)
    if os.path.isdir(keydir_or_pub):
        cmd = [USIGN, "-V", "-q", "-P", keydir_or_pub, "-x", sig, "-m", msg]
    else:
        cmd = [USIGN, "-V", "-q", "-p", keydir_or_pub, "-x", sig, "-m", msg]
    return run(cmd).returncode


def check_ipk_index(rep, feed, arch, tmp, foreign):
    pk = os.path.join(feed, "Packages")
    sig = os.path.join(feed, "Packages.sig")
    gz = os.path.join(feed, "Packages.gz")
    if not all(rep.check(os.path.isfile(p), "ipk %s: %s present" % (arch, os.path.basename(p))) for p in (pk, sig, gz)):
        return
    raw = open(pk, "rb").read()
    rep.check(gzip.decompress(open(gz, "rb").read()) == raw, "ipk %s: Packages.gz == Packages" % arch)

    # Router-like check: key stored under its fingerprint, message = zcat Packages.gz (opkg-key verify).
    fp = run([USIGN, "-F", "-p", "/keys/key-build.pub"]).stdout.strip()
    keydir = os.path.join(tmp, "opkg-keys")
    os.makedirs(keydir)
    shutil.copy("/keys/key-build.pub", os.path.join(keydir, fp))
    rep.check(usign_verify(keydir, gzip.decompress(open(gz, "rb").read()), sig, tmp, "good") == 0,
              "ipk %s: usign verify (key %s) of zcat Packages.gz OK" % (arch, fp))
    rep.check(usign_verify(foreign + "/usign.pub", raw, sig, tmp, "foreign") != 0,
              "ipk %s: foreign usign key -> verify fails (negative control)" % arch)
    rep.check(usign_verify(keydir, raw + b"Package: evil\n\n", sig, tmp, "modified") != 0,
              "ipk %s: modified Packages -> verify fails (negative control)" % arch)

    entries = [e for e in raw.decode("utf-8").split("\n\n") if e.strip()]
    files = set(package_files(feed, "ipk"))
    seen = set()
    for e in entries:
        f = dict(re.findall(r"^([A-Za-z0-9-]+): (.*)$", e, re.M))
        fn = f.get("Filename", "")
        seen.add(fn)
        path = os.path.join(feed, fn)
        good = os.path.isfile(path) and str(os.path.getsize(path)) == f.get("Size") and \
            sha256(open(path, "rb").read()) == f.get("SHA256sum")
        rep.check(good, "ipk %s: index entry %s size/sha256 match" % (arch, fn))
    rep.check(seen == files and files, "ipk %s: index lists exactly the feed files (%d)" % (arch, len(files)))


def matrix(series):
    rows = {}
    for line in open(MATRIX, encoding="utf-8"):
        p = line.split()
        if len(p) == 4 and p[0] == series:
            rows[p[1]] = {"zaprett-nfqws": p[2] == "y", "zaprett-nfqws2": p[3] == "y"}
    return rows


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--series", required=True)
    ap.add_argument("--arches", required=True)
    ap.add_argument("--noarch", default="")
    args = ap.parse_args()
    fmt = "apk" if args.series == "25.12" else "ipk"
    rows = matrix(args.series)
    rep = Report()
    noarch = args.noarch.split()
    if "luci-app-zaprett" in noarch:
        noarch.append("luci-i18n-zaprett-ru")
    tmp_root = tempfile.mkdtemp(prefix="zaprett-verify-")
    foreign = gen_foreign_keys(tmp_root)
    for arch in args.arches.split():
        feed = os.path.join(OUT, "feed", arch)
        if not any(rows[arch].values()):
            rep.check(not os.path.exists(feed), "%s %s: no feed (no binaries for this architecture)" % (fmt, arch))
            continue
        if not rep.check(os.path.isdir(feed), "%s %s: feed directory exists" % (fmt, arch)):
            continue
        files = package_files(feed, fmt)
        expected_count = len(noarch) + sum(1 for v in rows[arch].values() if v)
        rep.check(len(files) == expected_count, "%s %s: %d package files (expected %d): %s" % (fmt, arch, len(files), expected_count, files))
        for pkg in ("zaprett-nfqws", "zaprett-nfqws2"):
            fname = file_for(files, pkg, fmt)
            if rows[arch][pkg]:
                if rep.check(fname is not None, "%s %s: %s file present" % (fmt, arch, pkg)):
                    tmp = tempfile.mkdtemp(dir=tmp_root)
                    try:
                        check_arch_package(rep, feed, fname, pkg, arch, fmt, tmp)
                    except Exception as exc:  # report and continue with other checks
                        rep.fail("%s %s/%s: exception %s" % (fmt, arch, pkg, exc))
            else:
                rep.check(fname is None, "%s %s: no %s (no release binary)" % (fmt, arch, pkg))
        for name in noarch:
            fname = file_for(files, name, fmt)
            if rep.check(fname is not None, "%s %s: %s file present" % (fmt, arch, name)):
                tmp = tempfile.mkdtemp(dir=tmp_root)
                try:
                    check_noarch_package(rep, feed, fname, name, fmt, tmp)
                except Exception as exc:
                    rep.fail("%s %s/%s: exception %s" % (fmt, arch, name, exc))
        tmp = tempfile.mkdtemp(dir=tmp_root)
        try:
            if fmt == "apk":
                probe = "zaprett-nfqws" if rows[arch]["zaprett-nfqws"] else "zaprett-nfqws2"
                check_apk_index(rep, feed, arch, tmp, foreign, probe)
            else:
                check_ipk_index(rep, feed, arch, tmp, foreign)
        except Exception as exc:
            rep.fail("%s %s: index check exception %s" % (fmt, arch, exc))
    shutil.rmtree(tmp_root, ignore_errors=True)
    with open(os.path.join(OUT, "verify-report.txt"), "w", encoding="utf-8") as fh:
        fh.write("\n".join(rep.lines) + "\n")
    total = len(rep.lines)
    print("verify %s: %d checks, %d failed" % (args.series, total, rep.failed), flush=True)
    return 1 if rep.failed else 0


if __name__ == "__main__":
    sys.exit(main())
