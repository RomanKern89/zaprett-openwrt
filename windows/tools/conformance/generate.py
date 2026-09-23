"""Generates the conformance references windows/tests/conformance/NNN-<slug>.json with the router argument generator.

Each case (configuration + strategy) is run through strategy.uc `build` on a test router with zaprett installed, in a
sandbox under /tmp/zaprett-conf (the service configuration is never touched). The generator modules are those of THIS
repository (uploaded to /tmp/zaprett-conf/ucode and put first on the module path with `ucode -L`), so the references
follow the current router source; the bundle and guard files on the router must equal the repository ones. The C# generator of Zaprett.Core must
give the same argv after removing the router base options and mapping the paths (ARCHITECTURE-WIN §4).

The lab router and its account come only from the environment, without defaults: ZAPRETT_CONF_HOST,
ZAPRETT_CONF_USER, ZAPRETT_CONF_PASSWORD (ZAPRETT_CONF_PORT optional). Use a test router only: the script writes to its /tmp.

Usage (Git Bash):
    MSYS_NO_PATHCONV=1 PYTHONUTF8=1 ZAPRETT_CONF_HOST=... ZAPRETT_CONF_USER=... ZAPRETT_CONF_PASSWORD=... \
        python windows/tools/conformance/generate.py [--out DIR] [--check-only] [--keep]

--check-only  only compare the router bundle, guard files and generator modules with the repository.
--keep        keep /tmp/zaprett-conf on the router (removed by default).
The exit code is non-zero when the bundle differs, a case failed to run or the router refused the upload.
"""
import datetime
import hashlib
import json
import os
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
sys.stderr.reconfigure(encoding="utf-8", errors="replace")

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))           # windows/
REPO = os.path.dirname(ROOT)                            # repository root
import paramiko  # noqa: E402


def lab_env(name, required=True):
    value = os.environ.get(name, "")
    if required and not value:
        sys.exit(f"[error] environment variable {name} is not set")
    return value


def connect():
    """One SSH connection with keep-alive to the lab router named by the environment."""
    client = paramiko.SSHClient()
    client.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    client.connect(lab_env("ZAPRETT_CONF_HOST"), port=int(lab_env("ZAPRETT_CONF_PORT", False) or "22"),
                   username=lab_env("ZAPRETT_CONF_USER"), password=lab_env("ZAPRETT_CONF_PASSWORD"), timeout=20,
                   banner_timeout=30, allow_agent=False, look_for_keys=False)
    client.get_transport().set_keepalive(30)
    return client


def run(client, command, timeout=900, data=None):
    stdin, stdout, stderr = client.exec_command(command, timeout=timeout)
    if data is not None:
        stdin.write(data)
    stdin.channel.shutdown_write()
    out = stdout.read().decode("utf-8", errors="replace")
    err = stderr.read().decode("utf-8", errors="replace")
    return stdout.channel.recv_exit_status(), out, err

SHARE = os.path.join(REPO, "packages", "zaprett", "files", "usr", "share")
REMOTE = "/tmp/zaprett-conf"
SANDBOX = REMOTE + "/sbx"

BASE_MAIN = {
    "list_mode": "whitelist",
    "lists": ["zaprett-youtube", "zaprett-discord", "user-hosts"],
    "exclude_lists": ["zaprett-exclude", "user-hosts-exclude"],
    "ipsets": [],
    "exclude_ipsets": ["zaprett-exclude-ipset", "user-ipset-exclude"],
    "ipv6": "0",
    "debug": "0",
    "game_filter": "0",
    "game_ports_tcp": "1024-65535",
    "game_ports_udp": "1024-65535",
}

# One fixed text per user file: the router caches entry counts by path, size and mtime, so the same file must not get
# a different text of the same size within one run.
USER_HOSTS = "# my domains\nexample.com\nexample.org\n"
USER_HOSTS_EXCLUDE = "bank.example\n"
USER_IPSET = "203.0.113.0/24\n2001:db8::/32\n"
USER_IPSET_EXCLUDE = "198.51.100.0/24\n"


def main_with(**over):
    m = json.loads(json.dumps(BASE_MAIN))
    m.update(over)
    return m


def strategy_ids(engine):
    d = os.path.join(SHARE, "zaprett", "bundle", "manifests", "strategies", engine)
    return sorted(n[:-5] for n in os.listdir(d) if n.endswith(".json"))


def case(name, engine="nfqws", main=None, strategy=None, text=None, item_id=None, user_files=None):
    if strategy is None and text is None:
        strategy = "strategy-general" if engine == "nfqws" else "z2-general"
    return {"name": name, "engine": engine, "main": main or main_with(), "strategy": None if text is not None else strategy,
            "text": text, "item_id": item_id if text is not None else None, "user_files": user_files or {}}


def build_cases():
    cases = []
    for engine in ("nfqws", "nfqws2"):
        for sid in strategy_ids(engine):
            cases.append(case(f"base/{engine}/{sid}", engine, strategy=sid))

    all_user = {"hosts-include.txt": USER_HOSTS, "hosts-exclude.txt": USER_HOSTS_EXCLUDE,
                "ipset-include.txt": USER_IPSET, "ipset-exclude.txt": USER_IPSET_EXCLUDE}
    for engine in ("nfqws", "nfqws2"):
        e = engine
        cases += [
            case(f"mode/{e}/blacklist", e, main_with(list_mode="blacklist")),
            case(f"mode/{e}/blacklist-user-excludes", e, main_with(list_mode="blacklist"), user_files=all_user),
            case(f"lists/{e}/user-hosts-and-ipset", e, main_with(ipsets=["user-ipset"]), user_files=all_user),
            case(f"lists/{e}/empty-lists", e, main_with(lists=[])),
            case(f"lists/{e}/no-lists-no-excludes", e, main_with(lists=[], exclude_lists=[], exclude_ipsets=[])),
            case(f"lists/{e}/only-ipsets", e, main_with(lists=[], ipsets=["zaprett-telegram-ipset"])),
            case(f"lists/{e}/lists-and-ipsets", e, main_with(ipsets=["zaprett-telegram-ipset", "zaprett-discord-voice"])),
            case(f"lists/{e}/excludes-nonempty", e, user_files={"hosts-exclude.txt": USER_HOSTS_EXCLUDE,
                                                                "ipset-exclude.txt": USER_IPSET_EXCLUDE}),
            case(f"game/{e}/with-ipset", e, main_with(game_filter="1", ipsets=["zaprett-telegram-ipset"])),
            case(f"game/{e}/no-ipset", e, main_with(game_filter="1")),
            case(f"game/{e}/custom-ports", e, main_with(game_filter="1", ipsets=["zaprett-roblox-ipset"],
                                                         game_ports_tcp="443,50000-50100", game_ports_udp="")),
            case(f"opts/{e}/ipv6", e, main_with(ipv6="1")),
            case(f"opts/{e}/debug", e, main_with(debug="1")),
            case(f"lists/{e}/source-not-downloaded", e, main_with(lists=["zaprett-youtube", "src-refilter_domains"])),
        ]
    cases += [
        case("lists/nfqws/variant-full-lists", main=main_with(lists=["zaprett-youtube-full", "zaprett-discord-full", "zaprett-telegram"])),
        case("config/nfqws/bad-values", main=main_with(list_mode="bogus", lists=["zaprett-youtube", "bad id!"], ipv6="maybe",
                                                       game_ports_tcp="~80")),
    ]

    # own strategies
    own = [
        ("custom/placeholders", "nfqws",
         "--filter-tcp=443 ${hostlists} --dpi-desync=fake,multisplit --dpi-desync-fake-tls=${bin:tls_clienthello_www_google_com} --new\n"
         "--filter-udp=443 ${ipsets} --dpi-desync=fake --dpi-desync-fake-quic=${zaprettdir}/bin/quic_initial_www_google_com.bin --new\n"
         "--filter-tcp=80 --hostlist=${hostlist:zaprett-youtube} --hostlist-exclude=${hostlist_exclude:zaprett-exclude} "
         "--ipset-exclude=${ipset_exclude:zaprett-exclude-ipset} --dpi-desync=fake\n", None),
        ("custom/comment-legacy-backslash", "nfqws",
         "--comment this is a comment --filter-tcp=443 ${hostlists} \\\n  --dpi-desync=split2 \\\n"
         "--new --filter-tcp=80 ${hostlists} --dpi-desync=disorder2,split --comment=keep\t--dpi-desync-ttl=3\r\n", None),
        ("custom/empty-profiles", "nfqws",
         "--new --filter-tcp=443 ${hostlists} --dpi-desync=fake --new --new --filter-udp=443 ${hostlists} --dpi-desync=fake --new\n", None),
        ("custom/reserved-options", "nfqws",
         "--qnum=5 --user=root --debug=1 --filter-tcp=443 ${hostlists} --dpi-desync=fake --dpi-desync-fwmark=0x1 --daemon\n", None),
        ("custom/dual-profile-lists-only", "nfqws", "--filter-tcp=443 ${hostlists} ${ipsets} --dpi-desync=fake\n", None),
        ("custom/dual-profile-ipsets-too", "nfqws", "--filter-tcp=443 ${hostlists} ${ipsets} --dpi-desync=fake\n",
         main_with(ipsets=["zaprett-telegram-ipset"])),
        ("custom/unfiltered-wide-negated", "nfqws",
         "--filter-tcp=1-65535 --dpi-desync=fake --new --filter-udp=~443 ${hostlists} --dpi-desync=fake --new "
         "--dpi-desync=fake --new --filter-tcp=443 --skip\n", None),
        ("custom/nfqws2-own-lua-init", "nfqws2",
         "--lua-init=@${zaprettdir}/bin/quic_initial_www_google_com.bin --filter-tcp=443 ${hostlists} --lua-desync=multisplit\n", None),
        ("custom/nfqws2-placeholders", "nfqws2",
         "--blob=q:@${bin:quic_initial_www_google_com} --filter-udp=443 ${hostlists} --payload=quic_initial "
         "--lua-desync=fake:blob=q:repeats=6 --new --filter-tcp=443 ${ipsets} --lua-desync=multisplit\n", None),
        # negative
        ("neg/unknown-placeholder", "nfqws", "--filter-tcp=443 ${foo} --dpi-desync=fake\n", None),
        ("neg/hostlists-in-token", "nfqws", "--filter-tcp=443 --hostlist=${hostlists} --dpi-desync=fake\n", None),
        ("neg/forbidden-path", "nfqws", "--filter-tcp=443 ${hostlists} --dpi-desync=fake --dpi-desync-fake-tls=/etc/passwd\n", None),
        ("neg/lua-init-inline", "nfqws2", "--lua-init=print(1) --filter-tcp=443 ${hostlists} --lua-desync=multisplit\n", None),
        ("neg/path-dotdot", "nfqws",
         "--filter-tcp=443 ${hostlists} --dpi-desync=fake --dpi-desync-fake-tls=/usr/share/zaprett/../../../etc/shadow\n", None),
        ("neg/bin-not-installed", "nfqws", "--filter-tcp=443 ${hostlists} --dpi-desync-fake-tls=${bin:not_installed}\n", None),
        ("neg/empty-strategy", "nfqws", "--comment nothing here\n--new\n", None),
        ("neg/bad-port-filter", "nfqws", "--filter-tcp=70000 ${hostlists} --dpi-desync=fake\n", None),
        ("neg/unclosed-placeholder", "nfqws", "--filter-tcp=443 ${hostlists} --dpi-desync-fake-tls=${bin:abc\n", None),
        ("neg/bad-placeholder-id", "nfqws", "--filter-tcp=443 ${hostlists} --dpi-desync-fake-tls=${bin:../x}\n", None),
        ("neg/autohostlist-outside", "nfqws", "--filter-tcp=443 --hostlist-auto=/tmp/auto.txt --dpi-desync=fake\n", None),
        # option syntax as getopt_long_only sees it (canonicalization): prefixes, "-name", values without "="
        ("opts/prefix-and-single-dash", "nfqws", "-filter-t=443 ${hostlists} --dpi-desync-r=6 --dpi-desync=fake\n", None),
        ("opts/required-takes-next-word", "nfqws", "--filter-tcp 443 ${hostlists} --dpi-desync fake --dpi-desync-repeats 6\n", None),
        ("opts/required-takes-dash-word", "nfqws",
         "--filter-tcp=443 ${hostlists} --dpi-desync=fake --dpi-desync-fake-tls --dpi-desync-fake-quic=0x01\n", None),
        ("opts/reserved-without-equals", "nfqws", "--qnum 7 --user root --filter-tcp=443 ${hostlists} --dpi-desync=fake\n", None),
        ("opts/nfqws2-named-profiles", "nfqws2",
         "--filter-tcp=443 ${hostlists} --lua-desync=multisplit --new=second --filter-udp=443 ${hostlists} --lua-desync=fake\n", None),
        ("neg/option-no-value-given", "nfqws", "--filter-tcp=443 ${hostlists} --dpi-desync=fake --skip=1\n", None),
        ("neg/option-required-at-end", "nfqws", "--filter-tcp=443 ${hostlists} --dpi-desync\n", None),
        ("neg/option-required-before-placeholder", "nfqws", "--filter-tcp=443 --hostlist ${hostlists} --dpi-desync=fake\n", None),
        ("neg/option-ambiguous", "nfqws", "--filter-tcp=443 ${hostlists} --dpi-desync-fake=x\n", None),
        ("neg/option-unknown", "nfqws", "--filter-tcp=443 ${hostlists} --no-such-option=1\n", None),
        ("neg/option-triple-dash", "nfqws", "--filter-tcp=443 ${hostlists} ---dpi-desync=fake\n", None),
        ("neg/option-empty-name", "nfqws", "--filter-tcp=443 ${hostlists} --=fake\n", None),
        ("neg/option-lone-dash", "nfqws", "--filter-tcp=443 ${hostlists} - --dpi-desync=fake\n", None),
        ("neg/not-an-option-word", "nfqws", "--filter-tcp=443 ${hostlists} fake\n", None),
        ("neg/debug-abbreviated-path", "nfqws", "--debu=@/etc/passwd --filter-tcp=443 ${hostlists} --dpi-desync=fake\n", None),
    ]
    for name, engine, text, main in own:
        cases.append(case(name, engine, main, text=text, item_id="user-conf"))
    cases += [
        case("neg/list-not-found", main=main_with(lists=["zaprett-youtube", "no-such-list"])),
        case("neg/strategy-not-found", strategy="no-such-strategy"),
        case("neg/nfqws2-strategy-on-nfqws", strategy="z2-general"),
    ]
    return cases


def slug(name):
    return "".join(c if c.isalnum() or c in "-." else "-" for c in name.replace("/", "--")).lower()


def local_hashes():
    res = {}
    for rel_dir in ("zaprett/bundle", "zaprett/guard"):
        base = os.path.join(SHARE, *rel_dir.split("/"))
        for d, _, files in os.walk(base):
            for f in files:
                p = os.path.join(d, f)
                rel = "/usr/share/" + os.path.relpath(p, SHARE).replace(os.sep, "/")
                res[rel] = hashlib.sha256(open(p, "rb").read()).hexdigest()
    return res


def remote_hashes(client):
    rc, out, err = run(client, "find /usr/share/zaprett/bundle /usr/share/zaprett/guard -type f | sort | xargs sha256sum", 120)
    if rc != 0:
        raise RuntimeError(f"sha256sum on the router failed: {err}")
    res = {}
    for line in out.splitlines():
        h, _, path = line.partition("  ")
        res[path.strip()] = h
    return res


def compare_bundle(client):
    loc, rem = local_hashes(), remote_hashes(client)
    diffs = []
    for p in sorted(set(loc) | set(rem)):
        if loc.get(p) != rem.get(p):
            diffs.append((p, "repo" if p not in rem else ("router" if p not in loc else "content")))
    print(f"bundle check: {len(loc)} files in the repository, {len(rem)} on the router, differences: {len(diffs)}")
    for p, kind in diffs:
        print(f"  DIFF {kind}: {p}")
    return diffs


def upload(client, remote_path, data):
    rc, out, err = run(client, f"mkdir -p '{os.path.dirname(remote_path)}' && cat > '{remote_path}' && "
                                       f"chmod 644 '{remote_path}' && sha256sum '{remote_path}'", 120, data)
    if rc != 0 or not out.startswith(hashlib.sha256(data).hexdigest()):
        raise RuntimeError(f"upload of {remote_path} failed: rc={rc} {err}")


def rewrite_sandbox(value):
    """Sandbox paths -> the real router layout (P.etc = /etc/zaprett, P.user = /etc/zaprett/user, P.run = /var/run/zaprett)."""
    if isinstance(value, str):
        return value.replace(SANDBOX + "/etc", "/etc/zaprett").replace(SANDBOX + "/run", "/var/run/zaprett")
    if isinstance(value, list):
        return [rewrite_sandbox(v) for v in value]
    if isinstance(value, dict):
        return {k: rewrite_sandbox(v) for k, v in value.items()}
    return value


def write_case(out_dir, n, c, res, meta):
    doc = {
        "schema": 1,
        "name": c["name"],
        "engine": c["engine"],
        "main": c["main"],
        "strategy": c["strategy"],
        "text": c["text"],
        "item_id": c["item_id"],
        "user_files": c["user_files"],
        "config_bad_options": res["bad_options"],
        "generated_by": meta,
        "expected": rewrite_sandbox(res["expected"]),
    }
    if SANDBOX in json.dumps(doc["expected"]):
        raise RuntimeError(f"sandbox path left in {c['name']}")
    path = os.path.join(out_dir, f"{n:03d}-{slug(c['name'])}.json")
    data = (json.dumps(doc, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
    with open(path, "wb") as fh:
        fh.write(data)
    return path


def main(argv):
    out_dir, check_only, keep = os.path.join(ROOT, "tests", "conformance"), False, False
    i = 1
    while i < len(argv):
        if argv[i] == "--out":
            out_dir = argv[i + 1]; i += 2
        elif argv[i] == "--check-only":
            check_only = True; i += 1
        elif argv[i] == "--keep":
            keep = True; i += 1
        else:
            print(__doc__); return 2
    client = connect()
    try:
        diffs = compare_bundle(client)
        if check_only:
            return 1 if diffs else 0
        cases = build_cases()
        upload(client, REMOTE + "/gen.uc", open(os.path.join(HERE, "gen.uc"), "rb").read())
        mod_dir = os.path.join(SHARE, "ucode", "zaprett")
        for m in sorted(os.listdir(mod_dir)):
            if m.endswith(".uc"):
                upload(client, f"{REMOTE}/ucode/zaprett/{m}", open(os.path.join(mod_dir, m), "rb").read())
        strategy_sha = hashlib.sha256(open(os.path.join(mod_dir, "strategy.uc"), "rb").read()).hexdigest()
        upload(client, REMOTE + "/in.json", json.dumps(cases, ensure_ascii=False).encode("utf-8"))
        rc, out, err = run(client, f"rm -rf {SANDBOX} && ucode -L {REMOTE}/ucode -S {REMOTE}/gen.uc {REMOTE}/in.json {SANDBOX}", 600)
        if rc != 0:
            raise RuntimeError(f"gen.uc failed rc={rc}: {err[-2000:]}")
        doc = json.loads(out)
        results = doc["results"]
        if len(results) != len(cases):
            raise RuntimeError(f"{len(results)} results for {len(cases)} cases")
        meta = {"openwrt": doc["openwrt"], "zaprett": doc["zaprett"], "generator": "repository strategy.uc " + strategy_sha[:16],
                "path_rewrite": {SANDBOX + "/etc": "/etc/zaprett", SANDBOX + "/run": "/var/run/zaprett"},
                "at": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")}
        os.makedirs(out_dir, exist_ok=True)
        for old in os.listdir(out_dir):
            if old.endswith(".json"):
                os.remove(os.path.join(out_dir, old))
        n_ok = n_fail = 0
        for n, (c, res) in enumerate(zip(cases, results), start=1):
            if res["name"] != c["name"]:
                raise RuntimeError(f"order mismatch: {res['name']} != {c['name']}")
            write_case(out_dir, n, c, res, meta)
            if res["expected"]["ok"]:
                n_ok += 1
            else:
                n_fail += 1
        print(f"written {len(cases)} cases to {out_dir}: ok={n_ok} expected-error={n_fail}")
        return 1 if diffs else 0
    finally:
        if not keep:
            run(client, f"rm -rf {REMOTE}", 60)
        client.close()


if __name__ == "__main__":
    sys.exit(main(sys.argv))
