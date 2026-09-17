"""Builds test fixtures for the zaprett backend from a local clone of CherretGit/zaprett-repo.

Usage (host, Windows or Linux):
    python make_fixtures.py <zaprett-repo clone> <tests/fixtures dir>

Layout produced:
    fixtures/repo/index.json, fixtures/repo/manifests/**      copy of the repository metadata
    fixtures/bundle/manifests/<dir>/<id>.json + files/**      a fake bundle built from the repository:
        all 64 nfqws strategies, 6 bin payloads, list-youtube, list-discord, list-general,
        ipset-roblox, list-exclude-general; manifest "file" is relative and dependencies stay URLs
        (exercises URL->id conversion and relative path resolution)
    fixtures/etc/...                                          "installed" items incl. broken manifests
All text files are written with LF line endings.
"""
import hashlib
import json
import os
import shutil
import sys

TYPE_DIR = {
    "list": "lists/include", "list_exclude": "lists/exclude", "ipset": "ipset/include",
    "ipset_exclude": "ipset/exclude", "nfqws": "strategies/nfqws", "nfqws2": "strategies/nfqws2",
    "bin": "bin", "lua_lib": "lua", "byedpi": "strategies/byedpi",
}

BUNDLE_EXTRA = {
    "list": ["list-youtube", "list-discord", "list-general"],
    "ipset": ["ipset-roblox"],
    "list_exclude": ["list-exclude-general"],
}


def write_text(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)


def write_bytes(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(data)


def main():
    repo, out = sys.argv[1], sys.argv[2]
    if os.path.isdir(out):
        shutil.rmtree(out)
    index = json.load(open(os.path.join(repo, "index.json"), encoding="utf-8"))
    write_text(os.path.join(out, "repo", "index.json"), json.dumps(index, ensure_ascii=False, indent=2) + "\n")

    wanted = {("nfqws", i["id"]) for i in index["items"] if i["type"] == "nfqws"}
    wanted |= {("bin", i["id"]) for i in index["items"] if i["type"] == "bin"}
    for t, ids in BUNDLE_EXTRA.items():
        wanted |= {(t, x) for x in ids}

    count = 0
    for item in index["items"]:
        t, iid = item["type"], item["id"]
        rel = item["manifest"].split("/refs/heads/main/", 1)[1]
        mpath = os.path.join(repo, *rel.split("/"))
        manifest = json.load(open(mpath, encoding="utf-8"))
        write_text(os.path.join(out, "repo", *rel.split("/")), json.dumps(manifest, ensure_ascii=False, indent=2) + "\n")
        if (t, iid) not in wanted:
            continue
        art_rel = manifest["artifact"]["url"].split("/refs/heads/main/", 1)[1]
        data = open(os.path.join(repo, *art_rel.split("/")), "rb").read()
        # the clone must be byte-exact (no CRLF conversion by git): verify against the repository manifest
        if hashlib.sha256(data).hexdigest() != manifest["artifact"]["sha256"]:
            raise SystemExit(f"sha256 mismatch for {art_rel}: clone is not byte-exact (core.autocrlf?)")
        fname = os.path.basename(art_rel)
        write_bytes(os.path.join(out, "bundle", "files", *TYPE_DIR[t].split("/"), fname), data)
        local = {
            "schema": 1, "id": iid, "name": manifest["name"], "version": manifest["version"],
            "author": manifest["author"], "description": manifest["description"],
            "dependencies": manifest.get("dependencies", []), "file": fname, "source": "bundle",
            "sha256": hashlib.sha256(data).hexdigest(), "installed_at": 1758100000, "manifest_url": item["manifest"],
        }
        write_text(os.path.join(out, "bundle", "manifests", *TYPE_DIR[t].split("/"), iid + ".json"),
                   json.dumps(local, ensure_ascii=False, indent=2) + "\n")
        count += 1

    # bundle ids fixed by the contract (used by the default UCI configuration)
    fixed = [
        ("list", "zaprett-youtube", b"youtube.com\ngooglevideo.com\nytimg.com\n"),
        ("list", "zaprett-discord", b"discord.com\ndiscord.gg\ndiscord.media\n"),
        ("list_exclude", "zaprett-exclude", b"gosuslugi.ru\nsberbank.ru\n"),
        ("ipset_exclude", "zaprett-exclude-ipset", b"10.0.0.0/8\n192.168.0.0/16\n"),
    ]
    for t, iid, data in fixed:
        write_bytes(os.path.join(out, "bundle", "files", *TYPE_DIR[t].split("/"), iid + ".txt"), data)
        write_text(os.path.join(out, "bundle", "manifests", *TYPE_DIR[t].split("/"), iid + ".json"), json.dumps({
            "schema": 1, "id": iid, "type": t, "name": iid, "version": "2026.09.17", "author": "test",
            "description": "fixture", "dependencies": [], "file": iid + ".txt", "source": "bundle",
            "sha256": hashlib.sha256(data).hexdigest(), "installed_at": 1758100000, "manifest_url": None}, indent=2) + "\n")
        count += 1

    # "installed" items in /etc/zaprett
    etc = os.path.join(out, "etc")
    yt = b"youtube.com\ngooglevideo.com\nytimg.com\n# comment\n\nyoutu.be\n"
    write_bytes(os.path.join(etc, "files", "lists", "include", "list-youtube.txt"), yt)
    write_text(os.path.join(etc, "manifests", "lists", "include", "list-youtube.json"), json.dumps({
        "schema": 1, "id": "list-youtube", "name": "YouTube (repo)", "version": "1.0.1", "author": "test",
        "description": "installed copy overriding the bundle", "dependencies": [],
        "file": "list-youtube.txt", "source": "repo",
        "sha256": hashlib.sha256(yt).hexdigest(), "installed_at": 1758100001,
        "manifest_url": "https://example.org/manifests/lists/include/list-youtube.json"}, indent=2) + "\n")
    # broken manifests (negative controls)
    write_text(os.path.join(etc, "manifests", "lists", "include", "list-evil.json"), json.dumps({
        "schema": 1, "id": "list-evil", "name": "evil", "version": "1.0.0", "file": "/etc/shadow"}) + "\n")
    write_text(os.path.join(etc, "manifests", "lists", "include", "list-mismatch.json"), json.dumps({
        "schema": 1, "id": "list-other", "name": "x", "version": "1.0.0", "file": "list-other.txt"}) + "\n")
    zero = "0" * 64
    write_text(os.path.join(etc, "manifests", "lists", "include", "list-nofile.json"), json.dumps({
        "schema": 1, "id": "list-nofile", "name": "x", "version": "1.0.0", "file": "list-nofile.txt", "sha256": zero}) + "\n")
    write_text(os.path.join(etc, "manifests", "lists", "include", "list-nosha.json"), json.dumps({
        "schema": 1, "id": "list-nosha", "name": "x", "version": "1.0.0", "file": "list-youtube.txt"}) + "\n")
    write_text(os.path.join(etc, "manifests", "lists", "include", "list-noschema.json"), json.dumps({
        "id": "list-noschema", "name": "x", "version": "1.0.0", "file": "list-youtube.txt", "sha256": zero}) + "\n")
    # optional fields absent or null (as in the bundle): accepted
    write_text(os.path.join(etc, "manifests", "lists", "include", "list-nulls.json"), json.dumps({
        "schema": 1, "id": "list-nulls", "type": "list", "name": None, "version": None, "author": None,
        "description": None, "dependencies": None, "file": "list-youtube.txt", "sha256": hashlib.sha256(yt).hexdigest(),
        "manifest_url": None}) + "\n")
    write_text(os.path.join(etc, "manifests", "lists", "include", "list-dotdot.json"), json.dumps({
        "schema": 1, "id": "list-dotdot", "name": "x", "version": "1.0.0",
        "file": "../../../../etc/passwd"}) + "\n")
    write_text(os.path.join(etc, "manifests", "lists", "include", "broken-json.json"), "{ not json\n")
    write_text(os.path.join(etc, "manifests", "lists", "include", "list-wrongtype.json"), json.dumps({
        "schema": 1, "id": "list-wrongtype", "type": "ipset", "name": "x", "version": "1.0.0", "file": "list-youtube.txt"}) + "\n")
    # downloaded URL subscription
    src = b"rutracker.org\nnnmclub.to\n"
    write_bytes(os.path.join(etc, "files", "lists", "include", "src-demo.txt"), src)
    write_text(os.path.join(etc, "manifests", "lists", "include", "src-demo.json"), json.dumps({
        "schema": 1, "id": "src-demo", "type": "list", "name": "Demo subscription", "version": "2026.09.17", "author": "",
        "description": "https://example.org/list.txt", "dependencies": [], "file": "src-demo.txt", "source": "url",
        "sha256": hashlib.sha256(src).hexdigest(), "installed_at": 1758100002, "manifest_url": None,
        "url": "https://example.org/list.txt", "entries": 2}, indent=2) + "\n")
    # gzip list (entries must be reported as unknown)
    write_bytes(os.path.join(etc, "files", "lists", "include", "list-gz.txt.gz"),
                bytes([0x1f, 0x8b, 0x08, 0x00]) + b"\x00" * 16)
    gz = bytes([0x1f, 0x8b, 0x08, 0x00]) + b"\x00" * 16
    write_text(os.path.join(etc, "manifests", "lists", "include", "list-gz.json"), json.dumps({
        "schema": 1, "id": "list-gz", "name": "gz", "version": "1.0.0", "file": "list-gz.txt.gz",
        "sha256": hashlib.sha256(gz).hexdigest()}) + "\n")
    # user files
    user = os.path.join(out, "user")
    write_text(os.path.join(user, "hosts-include.txt"), "example.com\n")
    write_text(os.path.join(user, "hosts-exclude.txt"), "")
    write_text(os.path.join(user, "ipset-include.txt"), "")
    write_text(os.path.join(user, "ipset-exclude.txt"), "10.0.0.0/8\n")
    write_text(os.path.join(user, "strategies", "nfqws", "user-test.txt"),
               "--filter-tcp=443 ${hostlists} --dpi-desync=fake --dpi-desync-fake-tls=${bin:tls_clienthello_vk_com}\n")
    write_text(os.path.join(user, "strategies", "nfqws", "not-user.txt"), "--filter-tcp=443\n")

    print(f"bundle items: {count}")


if __name__ == "__main__":
    main()
