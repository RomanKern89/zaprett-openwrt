#!/usr/bin/env python3
"""Watch bol-van/zapret and bol-van/zapret2 for releases newer than the ones pinned in the engine Makefiles.

    python3 tools/ci/engine_watch.py [--dry-run] [--pinned zaprett-nfqws=72.0] [--root DIR]

For every engine package (packages/zaprett-nfqws, packages/zaprett-nfqws2) the pinned PKG_VERSION is compared
with releases/latest of the upstream repository. For a newer release the script downloads the
"-openwrt-embedded.tar.gz" asset, computes its sha256 (and compares it with the "digest" the GitHub API reports),
lists its binaries/linux-* directories and maps them to the OpenWrt architectures of build/arches.txt.
It then opens ONE issue per release in $GITHUB_REPOSITORY (never a pull request: the Makefiles are updated by hand,
docs/BUILD.md section 8). An issue with the same title in any state is not created again.

Environment: GITHUB_TOKEN (reads; required to open issues), GITHUB_REPOSITORY (owner/name, required without --dry-run).
--dry-run prints the issue instead of creating it; --pinned pretends another pinned version (tests).
Exit code: 0 - checked (with or without new releases), 1 - an error (network, API, checksum mismatch).
"""
import argparse
import hashlib
import io
import json
import os
import re
import sys
import tarfile
import urllib.error
import urllib.request

API = "https://api.github.com"
ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
ENGINES = {"zaprett-nfqws": "nfqws", "zaprett-nfqws2": "nfqws2"}


def request(url, token, data=None, accept="application/vnd.github+json"):
    headers = {"Accept": accept, "User-Agent": "zaprett-engine-watch", "X-GitHub-Api-Version": "2022-11-28"}
    if token:
        headers["Authorization"] = "Bearer " + token
    body = None
    if data is not None:
        body = json.dumps(data).encode("utf-8")
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(url, data=body, headers=headers, method="POST" if data is not None else "GET")
    with urllib.request.urlopen(req, timeout=120) as resp:
        return resp.read()


def makefile_vars(path):
    text = open(path, encoding="utf-8").read()
    var = dict((k, v.strip()) for k, v in re.findall(r"^(PKG_[A-Z_]+):=(.*)$", text, re.M))
    ver = var["PKG_VERSION"]
    for key in list(var):
        var[key] = var[key].replace("$(PKG_VERSION)", ver)
    var["BINDIRS"] = sorted(set(re.findall(r"^\s*ZAPRETT_BINDIR:=(linux-[\w-]+)", text, re.M)))
    return var


def version_key(v):
    return tuple(int(p) if p.isdigit() else -1 for p in re.split(r"[.\-]", v))


def release_dir(arch, binary):
    """OpenWrt arch -> release directory; mirrors expected_elf() in build/verify.py."""
    if arch.startswith("aarch64_"):
        return "linux-arm64"
    if arch.startswith("arm_") and arch not in ("arm_arm926ej-s", "arm_fa526", "arm_xscale"):
        return "linux-arm"
    for prefix, d in (("mipsel_", "linux-mipsel"), ("mips_", "linux-mips"), ("mips64_", "linux-mips64"),
                      ("i386_", "linux-x86"), ("powerpc_", "linux-ppc"), ("riscv64_", "linux-riscv64")):
        if arch.startswith(prefix):
            return d
    if arch == "x86_64":
        return "linux-x86_64"
    return None


def matrix_arches(root):
    arches = {}
    for line in open(os.path.join(root, "build", "arches.txt"), encoding="utf-8"):
        f = line.split()
        if len(f) == 4 and not line.startswith("#"):
            arches.setdefault(f[1], set()).add(f[0])
    return arches


def check_engine(root, pkg, pinned_override, token):
    var = makefile_vars(os.path.join(root, "packages", pkg, "Makefile"))
    pinned = pinned_override or var["PKG_VERSION"]
    m = re.match(r"^https://github\.com/([^/]+/[^/]+)/releases/download/", var["PKG_SOURCE_URL"])
    if not m:
        raise RuntimeError("%s: unexpected PKG_SOURCE_URL %s" % (pkg, var["PKG_SOURCE_URL"]))
    repo = m.group(1)
    rel = json.loads(request("%s/repos/%s/releases/latest" % (API, repo), token))
    tag = rel["tag_name"]
    latest = tag.lstrip("v")
    print("%s: pinned v%s, %s releases/latest %s" % (pkg, pinned, repo, tag))
    if version_key(latest) <= version_key(pinned):
        return None
    asset_name = var["PKG_SOURCE"].replace(var["PKG_VERSION"], latest)
    asset = next((a for a in rel.get("assets", []) if a["name"] == asset_name), None)
    info = {"pkg": pkg, "repo": repo, "tag": tag, "pinned": pinned, "latest": latest, "url": rel["html_url"],
            "asset": asset_name, "published": rel.get("published_at", "?")}
    if asset is None:
        info["problem"] = "asset %s is missing in the release (assets: %s)" % (
            asset_name, ", ".join(a["name"] for a in rel.get("assets", [])))
        return info
    data = request(asset["browser_download_url"], token, accept="application/octet-stream")
    local = hashlib.sha256(data).hexdigest()
    api_digest = (asset.get("digest") or "").replace("sha256:", "")
    if api_digest and api_digest != local:
        raise RuntimeError("%s: sha256 of the download %s != API digest %s" % (asset_name, local, api_digest))
    info.update(sha256=local, api_digest=api_digest or "(not reported)", size=len(data))
    binary = ENGINES[pkg]
    dirs = set()
    with tarfile.open(fileobj=io.BytesIO(data), mode="r:gz") as tar:
        for name in tar.getnames():
            mm = re.match(r"^[^/]+/binaries/(linux-[\w-]+)/%s$" % re.escape(binary), name)
            if mm:
                dirs.add(mm.group(1))
    info["dirs"] = sorted(dirs)
    info["pinned_dirs"] = var["BINDIRS"]
    return info


def issue_body(info, arches):
    lines = [
        "A newer release of the engine for `%s` is available." % info["pkg"],
        "",
        "| | |", "|---|---|",
        "| Pinned (`packages/%s/Makefile`) | v%s |" % (info["pkg"], info["pinned"]),
        "| Latest release | [%s](%s), published %s |" % (info["tag"], info["url"], info["published"]),
        "| Archive | `%s` |" % info["asset"],
    ]
    if "problem" in info:
        lines += ["", "**Problem:** " + info["problem"]]
        return "\n".join(lines) + "\n"
    lines += [
        "| sha256 (downloaded by the workflow) | `%s` |" % info["sha256"],
        "| sha256 (GitHub API `digest`) | `%s` |" % info["api_digest"],
        "| Size | %d bytes |" % info["size"],
        "",
        "Makefile lines (check the hash a second time yourself before committing):",
        "",
        "```make",
        "PKG_VERSION:=%s" % info["latest"],
        "PKG_RELEASE:=1",
        "PKG_HASH:=%s" % info["sha256"],
        "```",
        "",
        "### Architectures",
        "",
        "| Release directory | In Makefile | OpenWrt architectures (build/arches.txt) |",
        "|---|---|---|",
    ]
    for d in sorted(set(info["dirs"]) | set(info["pinned_dirs"])):
        mapped = sorted(a for a in arches if release_dir(a, info["pkg"]) == d)
        if d in info["pinned_dirs"]:
            state = "yes"
        elif mapped:
            state = "**no: new binary for these architectures**"
        else:
            state = "not used (no OpenWrt architecture in the matrix)"
        if d not in info["dirs"]:
            state = "**MISSING in the new release**"
        lines.append("| `%s` | %s | %s |" % (d, state, ", ".join("`%s`" % a for a in mapped) or "-"))
    lines += [
        "",
        "### Checklist (docs/BUILD.md, section 8)",
        "",
        "- [ ] sha256 checked twice (API digest and local `sha256sum`)",
        "- [ ] set of `binaries/linux-*` unchanged, or Makefiles, `build/arches.txt` and `expected_elf()` in `build/verify.py` updated",
        "- [ ] ISA of `linux-arm` checked (`upx -d`, `readelf -A`)",
        "- [ ] build + `verify.py` 0 FAIL, `tools/ci/qemu-smoke.sh`, `build/test-install-rootfs.sh`",
        "",
        "_Opened by the engine-watch workflow._",
    ]
    return "\n".join(lines) + "\n"


def title_of(info):
    return "Engine update: %s %s (pinned v%s)" % (info["repo"], info["tag"], info["pinned"])


def issue_exists(repo, title, token):
    for page in range(1, 11):
        items = json.loads(request("%s/repos/%s/issues?state=all&per_page=100&page=%d" % (API, repo, page), token))
        if any(i.get("title") == title for i in items):
            return True
        if len(items) < 100:
            return False
    return False


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--root", default=ROOT)
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--pinned", action="append", default=[], help="pkg=version: pretend this version is pinned")
    args = ap.parse_args()
    overrides = dict(p.split("=", 1) for p in args.pinned)
    token = os.environ.get("GITHUB_TOKEN", "")
    target = os.environ.get("GITHUB_REPOSITORY", "")
    if not args.dry_run and not (token and target):
        sys.exit("GITHUB_TOKEN and GITHUB_REPOSITORY are required without --dry-run")
    arches = matrix_arches(args.root)
    found = 0
    try:
        for pkg in ENGINES:
            info = check_engine(args.root, pkg, overrides.get(pkg), token)
            if info is None:
                continue
            found += 1
            title, body = title_of(info), issue_body(info, arches)
            if args.dry_run:
                print("---- issue (dry run): " + title)
                print(body)
                continue
            if issue_exists(target, title, token):
                print("issue already exists: " + title)
                continue
            created = json.loads(request("%s/repos/%s/issues" % (API, target), token, {"title": title, "body": body}))
            print("issue opened: %s %s" % (title, created.get("html_url")))
    except (urllib.error.URLError, RuntimeError, KeyError, ValueError) as e:
        print("engine-watch: ERROR %s" % e)
        return 1
    print("engine-watch: %d newer release(s)" % found)
    return 0


if __name__ == "__main__":
    sys.exit(main())
