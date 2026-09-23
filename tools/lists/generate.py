"""Генератор собственных списков zaprett для OpenWrt (docs/ARCHITECTURE.md §16).

Запуск:
    PYTHONUTF8=1 python tools/lists/generate.py              сборка по снимкам (без сети) в tools/lists/out
    PYTHONUTF8=1 python tools/lists/generate.py --refresh    заново запросить источники, обновить снимки и собрать
    PYTHONUTF8=1 python tools/lists/generate.py --refresh-missing   дозапросить только отсутствующие снимки
    PYTHONUTF8=1 python tools/lists/generate.py --check      собрать во временный каталог и сверить с out/ байт в байт

Метод (для доменов):
  1. семена — домены из первоисточников (seeds.py: документация сервиса, его официальный сайт);
  2. Certificate Transparency: сертификаты якорного домена сервиса (crt.sh и certspotter). Имена, стоящие на
     одном сертификате с якорным доменом, принадлежат тому же владельцу. У Google сертификаты общие для всех его
     сайтов — там берутся только имена с ключевыми словами сервиса;
  3. имена из кода страниц сервиса — только с ключевыми словами сервиса;
  4. реальная сессия в браузере (Chromium, Playwright) по сценариям сервиса: имена хостов всех запросов и имена
     в коде JS, загруженного с доменов сервиса;
  5. семя без документа принимается, только если подтверждено шагами 2–4 или резолвится в собственные сети
     сервиса; прежний основной список (до §16) — тоже только кандидаты, подтверждённые возвращаются в основной;
  6. каждое имя проверяется через DoH dns.google и cloudflare-dns.com (оба обязаны ответить NOERROR), с
     положительным и отрицательным контролем резолверов;
  7. поддомены сворачиваются под родителя (nfqws учитывает поддомены сам), пересечения с исключениями запрещены.
Для IP-сетей: анонсы AS сервиса по RIPEstat (владелец AS сверяется) и официальные списки, только глобальные
адреса, агрегация CIDR. Журнал — out/BUILD_LOG.tsv, сравнение с прежними списками — out/COMPARE_OLD.tsv.
"""
import argparse
import hashlib
import ipaddress
import json
import os
import re
import shutil
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.dont_write_bytecode = True
import seeds  # noqa: E402
import sources  # noqa: E402

PROJECT = os.path.dirname(os.path.dirname(HERE))
OUT_DIR = os.path.join(HERE, 'out')
LOG_NAME = 'BUILD_LOG.tsv'
COMPARE_NAME = 'COMPARE_OLD.tsv'      # прежние основные списки (до §16) -> новые
INDEX_NAME = 'lists.json'
EXCLUDE_PATH = os.path.join(PROJECT, 'upstream', 'lists-refs', 'curated', 'exclude.txt')
EXCLUDE_IPSET_PATH = os.path.join(PROJECT, 'upstream', 'lists-refs', 'curated', 'exclude-ipset.txt')
AUTHOR = 'zaprett-openwrt'
LICENSE = 'MIT'
# оценка памяти nfqws (research/03-lists.md §5): байт на запись, 64 бит
MEM_PER_HOST, MEM_PER_NET = 115, 80

LABEL_RE = re.compile(r'^(?!-)[a-z0-9-]{1,63}(?<!-)$')
TLD_RE = re.compile(r'^(?:[a-z]{2,63}|xn--[a-z0-9-]{1,59})$')

# Сети специального назначения (RFC 6890 и реестры IANA): в списках обхода им не место
SPECIAL_NETS = [ipaddress.ip_network(x) for x in (
    '0.0.0.0/8', '10.0.0.0/8', '100.64.0.0/10', '127.0.0.0/8', '169.254.0.0/16', '172.16.0.0/12', '192.0.0.0/24',
    '192.0.2.0/24', '192.88.99.0/24', '192.168.0.0/16', '198.18.0.0/15', '198.51.100.0/24', '203.0.113.0/24',
    '224.0.0.0/4', '240.0.0.0/4', '::/127', '::ffff:0:0/96', '64:ff9b::/96', '100::/64', '2001::/23', '2001:db8::/32',
    '2002::/16', 'fc00::/7', 'fe80::/10', 'ff00::/8')]


class GenError(Exception):
    """Входные данные нарушают правило генератора."""


# ---------------------------------------------------------------- домены

def valid_domain(name):
    if len(name) > 253 or '.' not in name:
        return False
    labels = name.split('.')
    return all(LABEL_RE.match(x) for x in labels) and bool(TLD_RE.match(labels[-1]))


class Psl:
    """Public Suffix List: регистрируемый домен (eTLD+1)."""

    def __init__(self, text):
        self.rules, self.wild, self.exc = set(), set(), set()
        for line in text.split('\n'):
            line = line.strip()
            if not line or line.startswith('//'):
                continue
            line = line.split()[0].encode('idna').decode('ascii') if not line.isascii() else line.split()[0]
            if line.startswith('!'):
                self.exc.add(line[1:])
            elif line.startswith('*.'):
                self.wild.add(line[2:])
            else:
                self.rules.add(line)

    def suffix_len(self, labels):
        best = 1
        for i in range(len(labels)):
            cand = '.'.join(labels[i:])
            if cand in self.exc:
                return len(labels) - i - 1
            if cand in self.rules:
                best = max(best, len(labels) - i)
            if i + 1 < len(labels) and '.'.join(labels[i + 1:]) in self.wild:
                best = max(best, len(labels) - i)
        return best

    def is_suffix(self, name):
        return name in self.rules

    def known_suffix(self, name):
        labels = name.split('.')
        return labels[-1] in self.rules or labels[-1] in self.wild

    def registrable(self, name):
        labels = name.split('.')
        n = self.suffix_len(labels)
        if n >= len(labels):
            return None
        return '.'.join(labels[-(n + 1):])


def is_under(name, parent):
    return name == parent or name.endswith('.' + parent)


def fold(names):
    """Убрать имена, покрытые другим именем набора. Возвращает (оставшиеся, {имя: родитель})."""
    s = set(names)
    covered = {}
    for n in sorted(s):
        parts = n.split('.')
        for i in range(len(parts) - 2, 0, -1):       # сначала самый верхний родитель: он и остаётся в списке
            p = '.'.join(parts[i:])
            if p in s:
                covered[n] = p
                break
    return sorted(s - set(covered)), covered


def load_old_core(svc):
    """Прежний основной список сервиса (до §16) — кандидаты и база для сравнения «было -> стало»."""
    path = svc.get('old_core')
    if not path:
        return []
    with open(os.path.join(PROJECT, *path.split('/')), 'rb') as f:
        return [x.strip() for x in f.read().decode('utf-8').replace(chr(13), '').split('\n') if x.strip()]


def compare_rows(svc, out, log):
    """Сравнение с прежним основным списком: для каждой прежней записи — где она теперь и почему."""
    rows = []
    reasons = {}
    for r in log.rows:
        if r[0] == svc['id'] and r[2] == 'excluded':
            reasons.setdefault(r[1], '%s (%s)' % (r[3], r[4]))
    for name in load_old_core(svc):
        if any(is_under(name, x) for x in out['core']):
            where = 'core' if name in out['core'] else 'core (покрыт %s)' % next(x for x in out['core'] if is_under(name, x))
            rows.append((svc['lists']['core'], name, where, ''))
        elif any(is_under(name, x) for x in out.get('full', [])):
            rows.append((svc['lists']['core'], name, 'full', 'только в расширенном: подтверждён, но не нужен основному сценарию'))
        else:
            rows.append((svc['lists']['core'], name, '-', reasons.get(name, 'не подтверждён')))
    old = set(load_old_core(svc))
    for name in out['core']:
        if name not in old:
            rows.append((svc['lists']['core'], name, 'core (новое)', ''))
    return rows


def load_exclude():
    with open(EXCLUDE_PATH, 'rb') as f:
        return {x.lstrip('^') for x in f.read().decode('utf-8').split('\n') if x and x[0] not in '#;/'}


class Log:
    def __init__(self):
        self.rows = set()

    def add(self, service, entry, verdict, reason, source):
        for v in (service, entry, verdict, reason, source):
            if '\t' in v or '\n' in v:
                raise GenError('табуляция или перевод строки в журнале: %r' % v)
        self.rows.add((service, entry, verdict, reason, source))

    def bytes(self):
        head = 'service\tentry\tverdict\treason\tsource\n'
        return (head + ''.join('\t'.join(r) + '\n' for r in sorted(self.rows))).encode('utf-8')


def _has_kw(text, keywords):
    return any(k in text for k in keywords)


def discover(svc, store, psl, log):
    """Имена сервиса из CT и страниц: {'own': {регистрируемый: источник}, 'hosts': {имя: источник}}."""
    kw = svc['keywords']
    anchors = svc['anchors']
    own, hosts, covered_count, foreign, cc_variants = {}, {}, {}, {}, {}

    def classify(name, src, need_kw):
        name = name.strip().lower().rstrip('.')
        if name.startswith('*.'):
            name = name[2:]
        if not valid_domain(name) or not psl.known_suffix(name):
            return
        shared = [x for x in svc['shared'] if name != x and is_under(name, x)]
        if shared:
            left = name[:-len(shared[0]) - 1]
            if _has_kw(left, kw):
                hosts.setdefault(name, src)
            elif src.startswith('ct:'):
                foreign.setdefault(shared[0], src)
            return
        reg = psl.registrable(name)
        if reg is None:
            # имя само внесено в PSL как публичный суффикс (так Discord изолирует активности: discordsays.com)
            if psl.is_suffix(name) and _has_kw(name, kw):
                own.setdefault(name, src)
            return
        if svc.get('skip_cc_variants') and reg not in anchors and \
                any(reg.split('.')[0] == a.split('.')[0] for a in anchors):
            if src.startswith('ct:'):
                cc_variants.setdefault(reg, src)
            return
        if need_kw and not _has_kw(reg, kw):
            if src.startswith('ct:'):       # в коде страниц чужих имён и мусора много — в журнал только CT
                foreign.setdefault(reg, src)
            return
        own.setdefault(reg, src)
        if name != reg:
            covered_count[reg] = covered_count.get(reg, 0) + 1

    def foreign_count(sans):
        # сертификат SaaS-площадки (карьерный сайт, CDN) несёт имена многих клиентов: такой сертификат считаем
        # общим и берём из него только имена с ключевыми словами сервиса
        regs = {psl.registrable(n.lstrip('*.')) for n in sans if valid_domain(n.lstrip('*.'))}
        return sum(1 for r in regs if r and not _has_kw(r, kw))

    for anchor in anchors:
        snap = store.ct(anchor, seeds.CT_WINDOW_DAYS)
        src = 'ct:%s' % anchor
        for sans in snap['san_sets']:
            if any(_has_kw(n, seeds.SHARED_CERT_MARKERS) for n in sans):
                continue
            if not any(is_under(n.lstrip('*.'), anchor) for n in sans):
                continue
            need_kw = svc['ct_keyword_required'] or len(sans) > seeds.CT_MAX_SANS or \
                foreign_count(sans) > seeds.CT_MAX_FOREIGN
            for n in sans:
                classify(n, src, need_kw)
    seen = {}                       # имя хоста -> источник: страница, запрос в сессии, код JS

    def see(n, src):
        seen.setdefault(n, src)
        classify(n, src, True)

    for url in svc['pages']:
        snap = store.page(url)
        for n in snap['hosts']:
            see(n, 'page:%s' % url)
    seed_names = [x[0] for x in svc['seeds']]

    def keep(h):
        return valid_domain(h) and psl.known_suffix(h) and (
            _has_kw(h, kw) or any(is_under(h, x) for x in tuple(svc['shared']) + tuple(seed_names)))

    for name, url in svc.get('sessions', ()):
        snap = store.session(svc['id'], name, url, kw, keep)
        for n in snap['requested_hosts']:
            see(n, 'session:%s/%s' % (svc['id'], name))
        for n in snap['code_hosts']:
            see(n, 'code:%s/%s' % (svc['id'], name))
    for reg, src in foreign.items():
        log.add(svc['id'], reg, 'excluded', 'чужой домен (нет признаков принадлежности сервису)', src)
    for reg, src in cc_variants.items():
        log.add(svc['id'], reg, 'excluded', 'региональная копия основного домена (перенаправляет на него)', src)
    for reg, cnt in covered_count.items():
        log.add(svc['id'], '*.' + reg, 'covered', 'поддоменов из CT, страниц и сессий: %d, покрыты %s' % (cnt, reg),
                'ct/page/session')
    return own, hosts, seen


def build_domain_lists(svc, store, psl, log, nets_by_id, exclude):
    own, hosts, seen = discover(svc, store, psl, log)
    discovered = dict(own)
    discovered.update(hosts)
    ip_nets = nets_by_id.get(svc.get('ip_evidence'), [])

    def ip_confirmed(name):
        r = store.resolve(name)
        ips = r['google'][1] + r['cloudflare'][1]
        return bool(ips) and all(any(ipaddress.ip_address(a) in n for n in ip_nets) for a in ips)

    def seen_under(name):
        return next((seen[h] for h in sorted(seen) if is_under(h, name)), None)

    cand = {}                                   # имя -> (основной?, источник)
    seed_list = list(svc['seeds'])
    known = {x[0] for x in seed_list}
    for name in load_old_core(svc):             # прежний основной список — только кандидаты, нужно подтверждение
        if name not in known:
            seed_list.append((name, True, None))
            known.add(name)
    for name, core, doc in seed_list:
        if not valid_domain(name):
            raise GenError('%s: семя %r — невалидное имя' % (svc['id'], name))
        if doc:
            if doc not in seeds.DOCS:
                raise GenError('%s: нет документа %s' % (svc['id'], doc))
            cand[name] = (core, 'doc:' + seeds.DOCS[doc][0])
        elif name in discovered:
            cand[name] = (core, discovered[name])
        elif seen_under(name):
            cand[name] = (core, seen_under(name))
        elif psl.registrable(name) in own and name != psl.registrable(name):
            cand[name] = (core, own[psl.registrable(name)])
        elif ip_nets and ip_confirmed(name):
            cand[name] = (core, 'ip:%s' % svc['ip_evidence'])
        else:
            note = svc.get('notes', {}).get(name)
            log.add(svc['id'], name, 'excluded', 'семя не подтверждено ни документом, ни CT, ни страницей, '
                    'ни сессией браузера, ни кодом клиента, ни IP' + ('; ' + note if note else ''), 'seed')
    for name, src in discovered.items():
        cand.setdefault(name, (False, src))
    # запись прежнего основного списка, подтверждённая новыми доказательствами, возвращается в основной
    for name in load_old_core(svc):
        if name in cand:
            cand[name] = (True, cand[name][1])

    alive = {}
    for name in sorted(cand):
        r = store.resolve(name)
        g, c = r['google'][0], r['cloudflare'][0]
        if g == sources.NOERROR and c == sources.NOERROR:
            alive[name] = cand[name]
        elif g == sources.NXDOMAIN and c == sources.NXDOMAIN:
            log.add(svc['id'], name, 'excluded', 'DNS: NXDOMAIN в обоих DoH', cand[name][1])
        else:
            log.add(svc['id'], name, 'excluded', 'DNS: неясно (google=%s, cloudflare=%s)' % (g, c), cand[name][1])
    for name in sorted(alive):
        hit = [p for p in exclude if is_under(name, p)]
        if hit:
            log.add(svc['id'], name, 'excluded', 'перекрыт исключением %s' % hit[0], alive[name][1])
            del alive[name]

    out = {}
    for variant in ('core', 'full'):
        if not svc['lists'].get(variant):
            continue
        names = [n for n, (core, _src) in alive.items() if core or variant == 'full']
        kept, covered = fold(names)
        out[variant] = kept
        for n, parent in covered.items():
            log.add(svc['id'], n, 'covered', '%s: покрыт родителем %s' % (variant, parent), alive[n][1])
    core_set = set(out['core'])
    if 'full' in out and out['full'] == out['core']:
        raise GenError('%s: расширенный список совпал с основным — уберите вариант full в seeds.py' % svc['id'])
    for n in out.get('full', out['core']):
        verdict = ('core+full' if 'full' in out else 'core') if n in core_set else 'full'
        log.add(svc['id'], n, verdict, 'DNS: NOERROR в обоих DoH', alive[n][1])
    if not out['core']:
        raise GenError('%s: основной список пуст' % svc['id'])
    return out


# ---------------------------------------------------------------- IP-сети

def special_overlap(net):
    return [s for s in SPECIAL_NETS if s.version == net.version and s.overlaps(net)]


def build_ipset(spec, store, log, exclude_nets):
    raw = []                                    # (сеть, источник)
    for key in spec['official']:
        text = store.official(key, seeds.OFFICIAL[key])
        for line in text.split('\n'):
            line = line.strip()
            if line and not line.startswith('#'):
                raw.append((ipaddress.ip_network(line, strict=True), 'official:' + seeds.OFFICIAL[key]))
    within = []
    for block, rkey, owner_re in spec.get('within', ()):
        r = store.rdap(rkey, seeds.RDAP[rkey])
        if not any(re.search(owner_re, e, re.I) for e in r['entities']):
            raise GenError('%s: владелец блока %s не подтверждён RDAP: %s' % (spec['id'], block, r['entities']))
        within.append(ipaddress.ip_network(block))
        log.add(spec['service'], block, 'owner', 'RDAP: %s' % '; '.join(e for e in r['entities'] if re.search(owner_re, e, re.I)),
                'rdap:' + seeds.RDAP[rkey])
    for asn in spec['asns']:
        a = store.ripe_as(asn)
        if not re.search(spec['holder_re'], a['holder'], re.I):
            raise GenError('%s: AS%d принадлежит %r, ожидался %s' % (spec['id'], asn, a['holder'], spec['holder_re']))
        src = 'ripestat:AS%d (%s, %s..%s)' % (asn, a['holder'], a['query_starttime'][:10], a['query_endtime'][:10])
        for p in a['prefixes']:
            net = ipaddress.ip_network(p)
            if within and not any(net.version == w.version and net.subnet_of(w) for w in within):
                continue
            raw.append((net, src))
    picked = []
    for net, src in raw:
        if net.version not in spec['families']:
            continue
        bad = special_overlap(net)
        if bad:
            log.add(spec['service'], str(net), 'excluded', 'сеть специального назначения %s' % bad[0], src)
            continue
        ex = [x for x in exclude_nets if x.version == net.version and x.overlaps(net)]
        if ex:
            log.add(spec['service'], str(net), 'excluded', 'пересекается с исключением %s' % ex[0], src)
            continue
        if net.version == 4 and net.prefixlen < 8 or net.version == 6 and net.prefixlen < 19:
            log.add(spec['service'], str(net), 'excluded', 'слишком широкая сеть', src)
            continue
        picked.append((net, src))
    result = []
    for version in (4, 6):
        group = [n for n, _s in picked if n.version == version]
        result += sorted(ipaddress.collapse_addresses(group))
    for net, src in picked:
        top = [r for r in result if r.version == net.version and net.subnet_of(r)][0]
        log.add(spec['service'], str(net), 'included',
                '%s: %s' % (spec['id'], 'в списке' if top == net else 'агрегирована в %s' % top), src)
    if not result:
        raise GenError('%s: список сетей пуст' % spec['id'])
    return result


def load_exclude_nets():
    with open(EXCLUDE_IPSET_PATH, 'rb') as f:
        return [ipaddress.ip_network(x) for x in f.read().decode('utf-8').split('\n') if x]


# ---------------------------------------------------------------- сборка

def _fmt_int(v):
    return '{:,}'.format(v).replace(',', ' ')


def _entry(list_id, ltype, service, variant, lines, generated, method, nets=None):
    data = ''.join(x + '\n' for x in lines).encode('utf-8')
    name, name_en, desc, desc_en = seeds.TEXTS[list_id]
    if list_id not in seeds.TEXTS_ZH:
        raise GenError('%s: нет китайских текстов в seeds.TEXTS_ZH' % list_id)
    name_zh, desc_zh = seeds.TEXTS_ZH[list_id]
    v4 = sum(n.num_addresses for n in (nets or []) if n.version == 4)
    counts = {'n': len(lines), 'v4': _fmt_int(v4), 'v6': sum(1 for n in (nets or []) if n.version == 6)}
    return {
        'id': list_id, 'type': ltype, 'service': service, 'variant': variant, 'name': name, 'name_en': name_en,
        'description': desc.format(**counts), 'description_en': desc_en.format(**counts),
        'name_zh': name_zh, 'description_zh': desc_zh.format(**counts), 'method': method,
        'generated': generated, 'entries': len(lines),
        'mem_estimate_bytes': len(lines) * (MEM_PER_HOST if ltype == 'list' else MEM_PER_NET),
        'file': list_id + '.txt', 'sha256': hashlib.sha256(data).hexdigest(),
    }, data


def generate(out_dir, store):
    meta = store.meta()
    generated = meta['fetched']
    store.dns_controls()
    psl = Psl(store.psl())
    log = Log()
    exclude = load_exclude()
    exclude_nets = load_exclude_nets()
    entries, files = [], {}

    nets_by_id = {}
    for spec in seeds.IPSETS:
        nets = build_ipset(spec, store, log, exclude_nets)
        nets_by_id[spec['id']] = nets
        method = seeds.METHODS['voice'] if spec['variant'] == 'voice' else (
            seeds.METHODS['ipset'] if spec['asns'] else 'официальный список %s; агрегация CIDR'
            % ', '.join(seeds.OFFICIAL[k] for k in spec['official']))
        e, data = _entry(spec['id'], 'ipset', spec['service'], spec['variant'], [str(n) for n in nets], generated,
                         method, nets)
        entries.append(e)
        files[e['file']] = data

    compare = []
    for svc in seeds.SERVICES:
        out = build_domain_lists(svc, store, psl, log, nets_by_id, exclude)
        compare += compare_rows(svc, out, log)
        for variant in [v for v in ('core', 'full') if v in out]:
            e, data = _entry(svc['lists'][variant], 'list', svc['id'], variant, out[variant], generated,
                             seeds.METHODS[variant])
            entries.append(e)
            files[e['file']] = data
    store.save_dns()

    ids = [e['id'] for e in entries]
    if len(ids) != len(set(ids)):
        raise GenError('повторяющиеся id списков')
    entries.sort(key=lambda e: e['id'])
    index = {'schema': 1, 'generated': generated, 'author': AUTHOR, 'license': LICENSE, 'lists': entries}
    if os.path.isdir(out_dir):
        shutil.rmtree(out_dir)
    for fname, data in files.items():
        sources._write(os.path.join(out_dir, fname), data)
    sources._write(os.path.join(out_dir, INDEX_NAME), sources.dump_json(index))
    sources._write(os.path.join(out_dir, LOG_NAME), log.bytes())
    sources._write(os.path.join(out_dir, COMPARE_NAME), (
        'list\tentry\tnow\treason\n' + ''.join('\t'.join(r) + '\n' for r in compare)).encode('utf-8'))
    return index


def tree_bytes(root):
    out = {}
    for f in sorted(os.listdir(root)):
        with open(os.path.join(root, f), 'rb') as fh:
            out[f] = fh.read()
    return out


def load_index(out_dir=OUT_DIR):
    """Для build_bundle.py: метаданные и содержимое списков, sha256 сверяется."""
    with open(os.path.join(out_dir, INDEX_NAME), 'rb') as f:
        index = json.loads(f.read().decode('utf-8'))
    for e in index['lists']:
        with open(os.path.join(out_dir, e['file']), 'rb') as f:
            data = f.read()
        if hashlib.sha256(data).hexdigest() != e['sha256']:
            raise GenError('%s: sha256 файла не совпадает с lists.json' % e['id'])
        e['data'] = data
    return index


def print_summary(index):
    print('Списки (снимок %s):' % index['generated'])
    for e in index['lists']:
        print('  %-28s %-6s %-7s записей %4d  память ~%6.1f КиБ' % (
            e['id'], e['type'], e['variant'], e['entries'], e['mem_estimate_bytes'] / 1024.0))


def main():
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    ap = argparse.ArgumentParser(description='Генератор собственных списков zaprett (§16)')
    ap.add_argument('--refresh', action='store_true', help='запросить источники заново и обновить снимки')
    ap.add_argument('--refresh-missing', action='store_true',
                    help='запросить только отсутствующие снимки (продолжить после сбоя источника)')
    ap.add_argument('--check', action='store_true', help='собрать по снимкам во временный каталог и сверить с out/')
    args = ap.parse_args()
    try:
        if args.check:
            tmp = tempfile.mkdtemp(prefix='zaprett-lists-')
            try:
                generate(os.path.join(tmp, 'out'), sources.Store())
                same = tree_bytes(os.path.join(tmp, 'out')) == tree_bytes(OUT_DIR)
            finally:
                shutil.rmtree(tmp, ignore_errors=True)
            print('out/ совпадает со сборкой по снимкам: %s' % same)
            return 0 if same else 1
        store = sources.Store(refresh=args.refresh or args.refresh_missing, missing_only=args.refresh_missing)
        index = generate(OUT_DIR, store)
        print_summary(index)
        return 0
    except (GenError, sources.SnapshotError) as exc:
        print('ОШИБКА: %s' % exc)
        return 2


if __name__ == '__main__':
    sys.exit(main())
