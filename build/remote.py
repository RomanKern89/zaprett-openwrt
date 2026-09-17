"""Remote build driver for zaprett OpenWrt packages (runs on Windows, builds on the Docker VM).

Connection settings come only from the environment (nothing secret is stored here):
    ZAPRETT_BUILD_HOST  (fallback SSH_HOST)   build VM address
    ZAPRETT_BUILD_USER  (fallback SSH_USER)   login
    ZAPRETT_BUILD_KEY   (fallback SSH_KEY)    path to the SSH private key
    ZAPRETT_BUILD_PORT  (optional, default 22)
    ZAPRETT_SECRETS     (optional) directory with signing keys,
                        default ~/.claude/projects/C--Zapret/secrets

Commands:
    remote.py run "<command>" [--timeout SEC]
    remote.py put <local> <remote>             SFTP upload + sha256 check
    remote.py get <remote> <local>             SFTP download + sha256 check
    remote.py keys-init                        create signing keys on the VM once and save them to secrets
    remote.py build [--series 25.12,24.10] [--arch all|a,b] [--no-fetch] [--allow-cr] [--extra-feed DIR]
            pack packages/ + build/ + tools/elfcheck.py, upload, run build/build.sh on the build
            machine, download dist and unpack it into dist/ after sha256 verification.
"""
import argparse
import hashlib
import io
import os
import posixpath
import shlex
import shutil
import sys
import tarfile
import time

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
sys.stderr.reconfigure(encoding="utf-8", errors="replace")

import paramiko

PROJECT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REMOTE_DIR_NAME = "zaprett-build"
KEY_FILES = {  # file name in secrets dir -> (remote name, mode)
    "zaprett-apk-private.pem": ("private-key.pem", 0o600),
    "zaprett-apk-public.pem": ("public-key.pem", 0o644),
    "zaprett-usign.key": ("key-build", 0o600),
    "zaprett-usign.pub": ("key-build.pub", 0o644),
}
# Only these trees are shipped to the build VM.
SHIP_DIRS = ("packages", "build")
SHIP_EXTRA = ("tools/elfcheck.py",)
SKIP_PARTS = {"work", "__pycache__", ".git"}
BINARY_SUFFIXES = {".bin", ".gz", ".png", ".ico", ".svg", ".woff", ".woff2", ".lmo", ".apk", ".ipk"}
POLL_SEC = 15


def env(name: str, fallback: str = "", required: bool = True) -> str:
    value = os.environ.get(name) or (os.environ.get(fallback) if fallback else "") or ""
    if required and not value:
        sys.exit(f"[error] environment variable {name}{' or ' + fallback if fallback else ''} is not set")
    return value


def secrets_dir() -> str:
    default = os.path.join(os.path.expanduser("~"), ".claude", "projects", "C--Zapret", "secrets")
    return os.environ.get("ZAPRETT_SECRETS") or default


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


class Remote:
    def __init__(self) -> None:
        self.client = paramiko.SSHClient()
        self.client.set_missing_host_key_policy(paramiko.AutoAddPolicy())
        self.client.connect(
            env("ZAPRETT_BUILD_HOST", "SSH_HOST"),
            port=int(os.environ.get("ZAPRETT_BUILD_PORT", "22")),
            username=env("ZAPRETT_BUILD_USER", "SSH_USER"),
            key_filename=env("ZAPRETT_BUILD_KEY", "SSH_KEY"),
            timeout=20, banner_timeout=30, auth_timeout=30,
            allow_agent=False, look_for_keys=False,
        )
        transport = self.client.get_transport()
        if transport is not None:
            transport.set_keepalive(30)
        self._sftp = None
        rc, out, _ = self.run("printf %s \"$HOME\"")
        if rc != 0 or not out.startswith("/"):
            sys.exit("[error] cannot determine remote $HOME")
        self.workdir = posixpath.join(out, REMOTE_DIR_NAME)

    @property
    def sftp(self) -> paramiko.SFTPClient:
        if self._sftp is None:
            self._sftp = self.client.open_sftp()
        return self._sftp

    def run(self, command: str, timeout: int = 120, echo: bool = False) -> tuple:
        chan = self.client.get_transport().open_session()
        chan.settimeout(timeout)
        chan.exec_command(command)
        chan.shutdown_write()  # stdin EOF: nothing on the remote side may wait for input
        out_chunks, err_chunks = [], []
        deadline = time.time() + timeout
        while True:
            if chan.recv_ready():
                data = chan.recv(65536)
                out_chunks.append(data)
                if echo:
                    sys.stdout.write(data.decode("utf-8", "replace"))
            if chan.recv_stderr_ready():
                data = chan.recv_stderr(65536)
                err_chunks.append(data)
                if echo:
                    sys.stderr.write(data.decode("utf-8", "replace"))
            if chan.exit_status_ready() and not chan.recv_ready() and not chan.recv_stderr_ready():
                break
            if time.time() > deadline:
                chan.close()
                raise TimeoutError(f"remote command timed out after {timeout}s: {command[:120]}")
            time.sleep(0.05)
        rc = chan.recv_exit_status()
        return rc, b"".join(out_chunks).decode("utf-8", "replace"), b"".join(err_chunks).decode("utf-8", "replace")

    def remote_sha256(self, path: str) -> str:
        rc, out, err = self.run("sha256sum " + shlex.quote(path))
        if rc != 0:
            raise RuntimeError(f"sha256sum failed for {path}: {err.strip()}")
        return out.split()[0]

    def put_bytes(self, data: bytes, path: str, mode: int = 0o644) -> None:
        tmp = path + ".part"
        with self.sftp.open(tmp, "wb") as fh:
            fh.set_pipelined(True)
            fh.write(data)
        self.sftp.chmod(tmp, mode)
        self.sftp.posix_rename(tmp, path)
        remote = self.remote_sha256(path)
        local = sha256_bytes(data)
        if remote != local:
            raise RuntimeError(f"upload checksum mismatch for {path}: local {local} remote {remote}")
        print(f"[put] {path}: {len(data)} bytes, sha256 OK")

    def get_file(self, path: str, local: str) -> None:
        remote = self.remote_sha256(path)
        tmp = local + ".part"
        self.sftp.get(path, tmp)
        got = sha256_file(tmp)
        if got != remote:
            os.remove(tmp)
            raise RuntimeError(f"download checksum mismatch for {path}: remote {remote} local {got}")
        os.replace(tmp, local)
        print(f"[get] {path} -> {local}: {os.path.getsize(local)} bytes, sha256 OK")

    def close(self) -> None:
        if self._sftp is not None:
            self._sftp.close()
        self.client.close()


def is_text_candidate(path: str, data: bytes) -> bool:
    if os.path.splitext(path)[1].lower() in BINARY_SUFFIXES:
        return False
    return b"\0" not in data[:8192]


def walk_files(base: str) -> list:
    found = []
    for root, dirs, files in os.walk(base):
        dirs[:] = sorted(d for d in dirs if d not in SKIP_PARTS)
        found += [os.path.join(root, name) for name in sorted(files)]
    return found


def pack_sources(allow_cr: bool, extra_feed: str = "") -> bytes:
    buf = io.BytesIO()
    cr_files = []
    count = 0
    with tarfile.open(fileobj=buf, mode="w:gz", format=tarfile.PAX_FORMAT) as tar:
        entries = []  # (full path, name in archive)
        for top in SHIP_DIRS:
            base = os.path.join(PROJECT, top)
            if os.path.isdir(base):
                entries += [(f, os.path.relpath(f, PROJECT)) for f in walk_files(base)]
        entries += [(os.path.join(PROJECT, p), p) for p in SHIP_EXTRA]
        if extra_feed:
            entries += [(f, os.path.join("extra", os.path.relpath(f, extra_feed))) for f in walk_files(extra_feed)]
        for full, rel in entries:
            rel = rel.replace(os.sep, "/")
            data = open(full, "rb").read()
            if is_text_candidate(rel, data) and bytes([13]) in data:
                cr_files.append(rel)
            info = tarfile.TarInfo(rel)
            info.size = len(data)
            info.mtime = int(os.path.getmtime(full))
            executable = data.startswith(b"#!") or rel.endswith(".sh")
            info.mode = 0o755 if executable else 0o644
            info.uname = info.gname = "root"
            tar.addfile(info, io.BytesIO(data))
            count += 1
    if cr_files:
        print("[error] CR bytes found in text files (must be LF):")
        for f in cr_files:
            print("   ", f)
        if not allow_cr:
            sys.exit(3)
    print(f"[pack] {count} files, {len(buf.getvalue())} bytes")
    return buf.getvalue()


def ensure_keys(r: Remote) -> None:
    sdir = secrets_dir()
    keys_dir = posixpath.join(r.workdir, "keys")
    r.run(f"mkdir -p {shlex.quote(keys_dir)} && chmod 700 {shlex.quote(keys_dir)}")
    for local_name, (remote_name, mode) in KEY_FILES.items():
        local = os.path.join(sdir, local_name)
        if not os.path.isfile(local):
            sys.exit(f"[error] signing key {local} is missing (run: remote.py keys-init)")
        data = open(local, "rb").read()
        path = posixpath.join(keys_dir, remote_name)
        rc, out, _ = r.run(f"sha256sum {shlex.quote(path)} 2>/dev/null")
        if rc == 0 and out.split()[0] == sha256_bytes(data):
            continue
        r.put_bytes(data, path, mode)


def cmd_keys_init(r: Remote) -> int:
    sdir = secrets_dir()
    existing = [n for n in KEY_FILES if os.path.exists(os.path.join(sdir, n))]
    if existing:
        print(f"[error] keys already exist in {sdir}: {existing}; refusing to overwrite")
        return 1
    os.makedirs(sdir, exist_ok=True)
    gen = posixpath.join(r.workdir, "keys-gen")
    script = (
        "set -e; umask 077; mkdir -p /out; cd /out; "
        "openssl ecparam -name prime256v1 -genkey -noout -out private-key.pem; "
        "openssl ec -in private-key.pem -pubout -out public-key.pem; "
        "/builder/staging_dir/host/bin/usign -G -s key-build -p key-build.pub -c 'zaprett OpenWrt repository'; "
        "ls -l /out"
    )
    cmd = (
        f"rm -rf {shlex.quote(gen)}; mkdir -p {shlex.quote(gen)}; chmod 700 {shlex.quote(gen)}; "
        f"docker run --rm --network none -v {shlex.quote(gen)}:/out openwrt/sdk:x86_64-v25.12.5 "
        f"bash -c {shlex.quote(script)}"
    )
    rc, out, err = r.run(cmd, timeout=300)
    print(out, err)
    if rc != 0:
        return rc
    for local_name, (remote_name, _) in KEY_FILES.items():
        r.get_file(posixpath.join(gen, remote_name), os.path.join(sdir, local_name))
    ensure_keys(r)
    r.run(f"rm -rf {shlex.quote(gen)}")
    pub_dir = os.path.join(PROJECT, "build", "keys")
    os.makedirs(pub_dir, exist_ok=True)
    for local_name, public_name in (("zaprett-apk-public.pem", "zaprett-apk.pub"), ("zaprett-usign.pub", "zaprett-usign.pub")):
        data = open(os.path.join(sdir, local_name), "rb").read()
        with open(os.path.join(pub_dir, public_name), "wb") as fh:
            fh.write(data)
        print(f"[keys] public key saved: build/keys/{public_name}")
    return 0


def cmd_build(r: Remote, args: argparse.Namespace) -> int:
    stamp = time.strftime("%Y%m%d-%H%M%S")
    w = r.workdir
    extra = os.path.abspath(args.extra_feed) if args.extra_feed else ""
    if extra and not os.path.isdir(extra):
        print(f"[error] extra feed {extra} is not a directory")
        return 2
    data = pack_sources(args.allow_cr, extra)
    ensure_keys(r)
    r.run(f"mkdir -p {w}/incoming {w}/logs {w}/out")
    tar_path = f"{w}/incoming/src-{stamp}.tar.gz"
    r.put_bytes(data, tar_path)
    rc, out, err = r.run(
        f"rm -rf {w}/src.new && mkdir -p {w}/src.new && tar -xzf {tar_path} -C {w}/src.new "
        f"&& rm -rf {w}/src && mv {w}/src.new {w}/src && find {w}/src -type f | wc -l"
    )
    if rc != 0:
        print(out, err)
        return rc
    print(f"[src] unpacked on VM: {out.strip()} files")

    build_args = ["--series", args.series.replace(",", " "), "--arch", args.arch.replace(",", " ")]
    if args.no_fetch:
        build_args.append("--no-fetch")
    if extra:
        build_args += ["--extra-feed", f"{w}/src/extra"]
    log = f"{w}/logs/build-{stamp}.log"
    rcfile = f"{w}/logs/build-{stamp}.rc"
    launch = (
        f"cd {w}; setsid nohup bash {w}/src/build/build.sh {' '.join(shlex.quote(a) for a in build_args)} "
        f"--rc-file {rcfile} > {log} 2>&1 < /dev/null & echo $!"
    )
    rc, out, err = r.run(launch)
    pid = out.strip()
    if rc != 0 or not pid.isdigit():
        print("[error] launch failed:", out, err)
        return 1
    print(f"[build] started pid {pid}, log {log}")

    offset = 0
    while True:
        time.sleep(POLL_SEC)
        alive, _, _ = r.run(f"kill -0 {pid} 2>/dev/null")
        offset = print_log_tail(r, log, offset)
        if alive != 0:
            print_log_tail(r, log, offset)
            break
    rc, out, _ = r.run(f"cat {rcfile} 2>/dev/null")
    if rc != 0 or not out.strip().isdigit():
        print(f"[error] build finished without rc file {rcfile}")
        return 1
    build_rc = int(out.strip())
    print(f"[build] finished, rc={build_rc}")
    if build_rc != 0:
        return build_rc

    local_dist = os.path.join(PROJECT, "dist")
    os.makedirs(local_dist, exist_ok=True)
    local_tar = os.path.join(local_dist, f"dist-{stamp}.tar")
    r.get_file(f"{w}/out/dist.tar", local_tar)
    rc = unpack_dist(local_tar, local_dist)
    if rc == 0:
        os.remove(local_tar)
    return rc


def print_log_tail(r: Remote, log: str, offset: int) -> int:
    with r.sftp.open(log, "rb") as fh:
        fh.seek(offset)
        data = fh.read()
    if data:
        sys.stdout.write(data.decode("utf-8", "replace"))
        sys.stdout.flush()
    return offset + len(data)


def unpack_dist(local_tar: str, local_dist: str) -> int:
    with tarfile.open(local_tar, "r") as tar:
        members = tar.getmembers()
        for m in members:
            norm = posixpath.normpath(m.name)
            if norm.startswith("..") or norm.startswith("/") or not (m.isfile() or m.isdir()):
                print(f"[error] unsafe member in dist archive: {m.name}")
                return 1
        sums = tar.extractfile("dist/SHA256SUMS")
        if sums is None:
            print("[error] dist/SHA256SUMS is missing")
            return 1
        expected = {}
        for line in sums.read().decode("utf-8").splitlines():
            digest, name = line.split(None, 1)
            name = name.lstrip("*")
            if name.startswith("./"):
                name = name[2:]
            expected["dist/" + name] = digest
        files = [m for m in members if m.isfile() and m.name != "dist/SHA256SUMS"]
        bad = []
        for m in files:
            digest = sha256_bytes(tar.extractfile(m).read())
            if expected.get(m.name) != digest:
                bad.append(m.name)
        missing = set(expected) - {m.name for m in files}
        if bad or missing:
            print(f"[error] SHA256SUMS mismatch: bad={bad[:10]} missing={sorted(missing)[:10]}")
            return 1
        print(f"[dist] {len(files)} files match SHA256SUMS")
        for top in {m.name.split("/")[1] for m in files if m.name.count("/") >= 2}:
            target = os.path.join(local_dist, top)
            if os.path.isdir(target):
                shutil.rmtree(target)
        tar.extractall(os.path.dirname(local_dist), members=[m for m in members if m.name.startswith("dist/")], filter="data")
    print(f"[dist] unpacked into {local_dist}")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="cmd", required=True)
    p_run = sub.add_parser("run")
    p_run.add_argument("command")
    p_run.add_argument("--timeout", type=int, default=120)
    p_put = sub.add_parser("put")
    p_put.add_argument("local")
    p_put.add_argument("remote")
    p_get = sub.add_parser("get")
    p_get.add_argument("remote")
    p_get.add_argument("local")
    sub.add_parser("keys-init")
    p_build = sub.add_parser("build")
    p_build.add_argument("--series", default="25.12,24.10")
    p_build.add_argument("--arch", default="all")
    p_build.add_argument("--no-fetch", action="store_true", help="do not refresh feed sources")
    p_build.add_argument("--allow-cr", action="store_true", help="only warn about CR bytes in text files")
    p_build.add_argument("--extra-feed", default="", help="additional local feed directory (pipeline tests with stubs)")
    args = parser.parse_args()

    r = Remote()
    try:
        if args.cmd == "run":
            rc, out, err = r.run(args.command, timeout=args.timeout, echo=True)
            print(f"[rc={rc}]")
            return rc
        if args.cmd == "put":
            r.put_bytes(open(args.local, "rb").read(), args.remote)
            return 0
        if args.cmd == "get":
            r.get_file(args.remote, args.local)
            return 0
        if args.cmd == "keys-init":
            return cmd_keys_init(r)
        if args.cmd == "build":
            return cmd_build(r, args)
    finally:
        r.close()
    return 2


if __name__ == "__main__":
    sys.exit(main())
