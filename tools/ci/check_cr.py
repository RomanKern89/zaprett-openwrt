#!/usr/bin/env python3
"""Byte-level check that no text file of the project contains CR (0x0D): every file goes to Linux/OpenWrt.

    python3 tools/ci/check_cr.py [root]

Files: "git ls-files" when root is a git work tree, otherwise a directory walk (dist/, upstream/, build/work/,
.git/, __pycache__/, node_modules/ skipped). Binary files are excluded by name (*.bin, *.gz, images, packages)
and by content (a NUL byte in the first 8 KiB). Exit code 1 if any text file has CR.
"""
import os
import subprocess
import sys

BINARY_EXTS = {
    ".bin", ".gz", ".xz", ".zst", ".tar", ".zip", ".png", ".jpg", ".jpeg", ".gif", ".ico",
    ".woff", ".woff2", ".ttf", ".lmo", ".mo", ".apk", ".ipk", ".pdf", ".so", ".o",
}
SKIP_DIRS = {".git", "dist", "upstream", "__pycache__", "node_modules", "work", ".staging", "ci-out"}
CR = bytes([13])


def list_files(root):
    try:
        out = subprocess.run(["git", "-C", root, "ls-files", "-z"], stdout=subprocess.PIPE,
                             stderr=subprocess.DEVNULL, check=True).stdout
        names = [n.decode("utf-8") for n in out.split(b"\0") if n]
        if names:
            return names, "git ls-files"
    except (OSError, subprocess.CalledProcessError):
        pass
    names = []
    for base, dirs, files in os.walk(root):
        dirs[:] = sorted(d for d in dirs if d not in SKIP_DIRS)
        names += [os.path.relpath(os.path.join(base, f), root).replace(os.sep, "/") for f in sorted(files)]
    return names, "directory walk"


def main():
    root = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), "..", ".."))
    names, how = list_files(root)
    checked = skipped = 0
    bad = []
    for rel in names:
        path = os.path.join(root, rel)
        if not os.path.isfile(path):
            continue
        if os.path.splitext(rel)[1].lower() in BINARY_EXTS:
            skipped += 1
            continue
        data = open(path, "rb").read()
        if b"\0" in data[:8192]:
            skipped += 1
            continue
        checked += 1
        count = data.count(CR)
        if count:
            bad.append((rel, count))
    for rel, count in bad:
        print("CR=%d %s" % (count, rel))
    print("check_cr: %d text files checked (%s), %d binary skipped, %d with CR" % (checked, how, skipped, len(bad)))
    return 1 if bad or checked == 0 else 0


if __name__ == "__main__":
    sys.exit(main())
