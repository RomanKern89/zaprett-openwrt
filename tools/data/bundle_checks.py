"""Проверки встроенного снимка (bundle) и presets.json пакета zaprett для OpenWrt.

Контракт — docs/ARCHITECTURE.md v1.1: §3 (раскладка и локальный манифест), §5 (плейсхолдеры
и токенизация стратегий), §9 (presets.json). Модуль только читает файлы и возвращает список
ошибок с кодами: генератор (build_bundle.py) и отрицательные контроли (test_build_bundle.py)
опираются на коды, а не на текст сообщений.
"""
import hashlib
import ipaddress
import json
import os
import re

ROUTER_BUNDLE_ROOT = '/usr/share/zaprett/bundle'

TYPE_DIRS = {
    'list': 'lists/include',
    'list_exclude': 'lists/exclude',
    'ipset': 'ipset/include',
    'ipset_exclude': 'ipset/exclude',
    'nfqws': 'strategies/nfqws',
    'nfqws2': 'strategies/nfqws2',
    'bin': 'bin',
    'lua_lib': 'lua',
}
DIR_TYPES = {v: k for k, v in TYPE_DIRS.items()}
LIST_TYPES = ('list', 'list_exclude')
IPSET_TYPES = ('ipset', 'ipset_exclude')
STRATEGY_TYPES = ('nfqws', 'nfqws2')

MANIFEST_KEYS = ('schema', 'id', 'type', 'name', 'version', 'author', 'description',
                 'dependencies', 'file', 'source', 'sha256', 'installed_at', 'manifest_url')
ID_RE = re.compile(r'^[A-Za-z0-9._-]{1,96}$')
SHA_RE = re.compile(r'^[0-9a-f]{64}$')
LABEL_RE = re.compile(r'^(?!-)[a-z0-9-]{1,63}(?<!-)$')
TLD_RE = re.compile(r'^(?:[a-z]{2,63}|xn--[a-z0-9-]{1,59})$')
NFQWS_MAX_LINE = 255          # nfqws читает строку fgets в буфер 256 байт

# Встроенные элементы с фиксированными id (§3)
REQUIRED_ITEMS = {
    'zaprett-youtube': 'list',
    'zaprett-discord': 'list',
    'zaprett-telegram': 'list',
    'zaprett-rutracker': 'list',
    'zaprett-telegram-ipset': 'ipset',
    'zaprett-exclude': 'list_exclude',
    'zaprett-exclude-ipset': 'ipset_exclude',
}

# Плейсхолдеры стратегий (§5)
WHOLE_TOKEN_PLACEHOLDERS = ('hostlists', 'ipsets')
PATH_PLACEHOLDERS = ('zaprettdir',)
PLACEHOLDER_KINDS = {
    'hostlist': 'list',
    'hostlist_exclude': 'list_exclude',
    'ipset': 'ipset',
    'ipset_exclude': 'ipset_exclude',
    'bin': 'bin',
    'lua_lib': 'lua_lib',
}
PLACEHOLDER_RE = re.compile(r'\$\{([^{}$]*)\}')
DEPRECATED_MODES = {'split': 'fakedsplit', 'split2': 'multisplit',
                    'disorder': 'fakeddisorder', 'disorder2': 'multidisorder'}
# Опции, которые ограничивают профиль конкретными адресатами (без них профиль действует на весь трафик порта)
HOST_SELECTORS = ('--hostlist=', '--hostlist-domains=', '--ipset=', '--ipset-ip=')
WIDE_UDP_PORTS = 1000

# presets.json (§9)
PRESET_TOP_KEYS = ('schema', 'services', 'always', 'tiers', 'defaults')
SERVICE_KEYS = ('id', 'name', 'description', 'lists', 'ipsets', 'sources', 'tier',
                'test_targets', 'works', 'note')
SERVICE_ID_RE = re.compile(r'^[a-z0-9_]{1,32}$')
KNOWN_SOURCES = ('refilter_domains', 'antifilter_allyouneed', 'cloudflare_v4', 'cloudflare_v6')
FREEZE_BYTES = 16384
QUICK_MIN, QUICK_MAX = 8, 12
CYRILLIC_RE = re.compile('[А-Яа-яЁё]')


class Report:
    """Накопитель ошибок и предупреждений: (код, где, текст)."""

    def __init__(self):
        self.errors = []
        self.warnings = []

    def error(self, code, where, msg):
        self.errors.append((code, where, msg))

    def warn(self, code, where, msg):
        self.warnings.append((code, where, msg))

    def codes(self):
        return {e[0] for e in self.errors}

    def errors_for(self, where):
        return [e for e in self.errors if e[1] == where]


def sha256_bytes(data):
    return hashlib.sha256(data).hexdigest()


def read_bytes(path):
    with open(path, 'rb') as f:
        return f.read()


# ---------------------------------------------------------------- текст и листы

def check_text_bytes(data, where, rep, require_eol=True):
    """UTF-8 без BOM, без CR (байтово), без NUL, с переводом строки в конце. Возвращает текст или None.

    require_eol=False — для стратегий, которые копируются без изменений: у них перевод строки в конце
    ни на что не влияет (токенизация по пробельным символам), поэтому его отсутствие — предупреждение.
    """
    if data.startswith(b'\xef\xbb\xbf'):
        rep.error('E_BOM', where, 'файл начинается с BOM')
    cr = data.count(bytes([13]))
    if cr:
        rep.error('E_CR', where, 'байтов CR: %d' % cr)
    if bytes([0]) in data:
        rep.error('E_NUL', where, 'в файле есть байт NUL')
    if data and not data.endswith(b'\n'):
        if require_eol:
            rep.error('E_NO_EOL', where, 'последняя строка не завершена переводом строки')
        else:
            rep.warn('W_NO_EOL', where, 'нет перевода строки в конце файла (файл оригинала, не изменяется)')
    try:
        return data.decode('utf-8')
    except UnicodeDecodeError as exc:
        rep.error('E_UTF8', where, 'не UTF-8: %s' % exc)
        return None


def is_valid_domain(name):
    if len(name) > 253 or '.' not in name:
        return False
    labels = name.split('.')
    if not all(LABEL_RE.match(lb) for lb in labels):
        return False
    return bool(TLD_RE.match(labels[-1]))


def _parent_chain(domain):
    parts = domain.split('.')
    return ['.'.join(parts[i:]) for i in range(1, len(parts) - 1)]


def check_hostlist(text, where, rep):
    """Строгий формат листа доменов (research/03 §2). Возвращает список записей."""
    entries = []
    seen = set()
    for n, line in enumerate(text.split('\n')[:-1], 1):
        pos = '%s:%d' % (where, n)
        if line == '':
            rep.error('E_EMPTY_LINE', where, 'пустая строка %d' % n)
            continue
        if line[0] in '#;/':
            rep.error('E_COMMENT', where, 'комментарий в строке %d (во встроенных листах не допускается)' % n)
            continue
        if len(line.encode('utf-8')) > NFQWS_MAX_LINE:
            rep.error('E_LONG_LINE', where, 'строка %d длиннее %d байт' % (n, NFQWS_MAX_LINE))
        if re.search(r'\s', line):
            rep.error('E_SPACE', where, 'пробельный символ в строке %d: %r' % (n, line))
            continue
        if '*' in line:
            rep.error('E_MASK', where, 'маска в строке %d: %r (nfqws не понимает *.)' % (n, line))
            continue
        if line != line.lower():
            rep.error('E_CASE', where, 'заглавные буквы в строке %d: %r' % (n, line))
        if line.endswith('.'):
            rep.error('E_TRAILING_DOT', where, 'точка в конце строки %d: %r' % (n, line))
            continue
        name = line[1:] if line.startswith('^') else line
        if not is_valid_domain(name.lower()):
            rep.error('E_DOMAIN', where, 'невалидный домен в строке %d: %r' % (n, line))
            continue
        if line in seen:
            rep.error('E_DUP', where, 'дубль в строке %d: %r' % (n, line))
            continue
        seen.add(line)
        entries.append(line)
    for d in entries:
        if d.startswith('^'):
            continue
        for parent in _parent_chain(d):
            if parent in seen:
                rep.error('E_COVERED', where, '%s покрыт родительским %s' % (d, parent))
                break
    return entries


def check_ipset(text, where, rep):
    """Лист IP-сетей: IPv4/IPv6 CIDR без битов хоста, без дублей и перекрытий. Возвращает сети."""
    nets = []
    seen = set()
    for n, line in enumerate(text.split('\n')[:-1], 1):
        if line == '':
            rep.error('E_EMPTY_LINE', where, 'пустая строка %d' % n)
            continue
        if line[0] in '#;/':
            rep.error('E_COMMENT', where, 'комментарий в строке %d (во встроенных листах не допускается)' % n)
            continue
        if re.search(r'\s', line):
            rep.error('E_SPACE', where, 'пробельный символ в строке %d: %r' % (n, line))
            continue
        try:
            net = ipaddress.ip_network(line, strict=True)
        except ValueError as exc:
            rep.error('E_CIDR', where, 'невалидная сеть в строке %d: %r (%s)' % (n, line, exc))
            continue
        if net in seen:
            rep.error('E_DUP', where, 'дубль в строке %d: %r' % (n, line))
            continue
        seen.add(net)
        nets.append(net)
    for version in (4, 6):
        group = [x for x in nets if x.version == version]
        if len(list(ipaddress.collapse_addresses(group))) != len(group):
            rep.error('E_OVERLAP', where, 'сети IPv%d перекрываются или сливаются' % version)
    return nets


# ---------------------------------------------------------------- стратегии

def tokenize_strategy(text):
    """Токенизация по §5 шаг 1. Возвращает (токены, сведения о мусоре)."""
    info = {'trailing_backslashes': 0, 'comment_blocks': 0, 'comment_junk': [], 'comment_eats_placeholder': False}
    lines = []
    for line in text.split('\n'):
        stripped = re.sub(r'\\[ \t]*$', '', line)
        if stripped != line:
            info['trailing_backslashes'] += 1
        lines.append(stripped)
    raw = ' '.join(lines).split()
    tokens = []
    i = 0
    while i < len(raw):
        if raw[i] == '--comment':
            info['comment_blocks'] += 1
            i += 1
            while i < len(raw) and not raw[i].startswith('--'):
                info['comment_junk'].append(raw[i])
                if '${' in raw[i]:
                    info['comment_eats_placeholder'] = True
                i += 1
            continue
        tokens.append(raw[i])
        i += 1
    return tokens, info


def split_profiles(tokens):
    profiles = [[]]
    for tk in tokens:
        if tk == '--new':
            profiles.append([])
        else:
            profiles[-1].append(tk)
    return profiles


def _opt_values(profile, name):
    prefix = name + '='
    return [tk[len(prefix):] for tk in profile if tk.startswith(prefix)]


def _has_opt(profile, name):
    """Опция задана со значением или без него (например, --dpi-desync-autottl)."""
    return name in profile or bool(_opt_values(profile, name))


def _port_ranges(spec):
    """'80,443,50000-50100' -> [(80,80),(443,443),(50000,50100)]; '~' (отрицание) -> None."""
    if spec.startswith('~'):
        return None
    out = []
    for part in spec.split(','):
        lo, _, hi = part.partition('-')
        out.append((int(lo), int(hi or lo)))
    return out


def analyze_profile(profile):
    """Чем ограничен профиль и какие особенности он требует от nft (§5 шаг 6)."""
    has_host = any(tk in ('${hostlists}', '${ipsets}') or tk.startswith(HOST_SELECTORS)
                   or tk.startswith(('${hostlist:', '${ipset:')) for tk in profile)
    l7 = _opt_values(profile, '--filter-l7')
    tcp = _opt_values(profile, '--filter-tcp')
    udp = _opt_values(profile, '--filter-udp')
    modes = [m for v in _opt_values(profile, '--dpi-desync') for m in v.split(',')]
    wide_udp = []
    for spec in udp:
        rng = _port_ranges(spec)
        if rng is None:
            wide_udp.append(spec)
            continue
        if sum(hi - lo + 1 for lo, hi in rng) > WIDE_UDP_PORTS:
            wide_udp.append(spec)
    return {
        'scope': 'hosts' if has_host else ('l7:' + ','.join(l7) if l7 else 'all'),
        'tcp': tcp, 'udp': udp, 'modes': modes, 'wide_udp': wide_udp,
        'syndata': 'syndata' in modes,
        'autottl': _has_opt(profile, '--dpi-desync-autottl'),
        'any_protocol_no_cutoff': (_has_opt(profile, '--dpi-desync-any-protocol')
                                   and not _has_opt(profile, '--dpi-desync-cutoff')),
        'no_port_filter': not tcp and not udp,
    }


def check_strategy(item, text, items, grammar, rep):
    """Проверка стратегии nfqws: плейсхолдеры, зависимости, опции, режимы. Возвращает аудит."""
    sid = item['id']
    deps = set(item.get('dependencies') or [])
    tokens, info = tokenize_strategy(text)
    audit = {'id': sid, 'placeholders': [], 'deprecated_modes': {}, 'comment_blocks': info['comment_blocks'],
             'comment_junk_tokens': len(info['comment_junk']), 'trailing_backslashes': info['trailing_backslashes'],
             'stray_tokens': [], 'profiles': 0, 'empty_profiles': 0, 'trailing_new': False,
             'profiles_all_traffic': [], 'profiles_l7_only': [], 'wide_udp': [], 'syndata': False,
             'autottl': False, 'any_protocol_no_cutoff': False, 'unused_dependencies': []}

    if info['comment_eats_placeholder']:
        rep.error('E_COMMENT_EATS_PLACEHOLDER', sid, '--comment без = поглощает плейсхолдер')

    # плейсхолдеры: синтаксис
    dollar = text.count('$')
    found = list(PLACEHOLDER_RE.finditer(text))
    if dollar != len(found):
        rep.error('E_PLACEHOLDER_SYNTAX', sid, 'символов $: %d, корректных ${...}: %d' % (dollar, len(found)))
    used_ids = set()
    names = sorted({m.group(1) for m in found})
    audit['placeholders'] = ['${%s}' % n for n in names]
    for name in names:
        if name in WHOLE_TOKEN_PLACEHOLDERS:
            glued = [tk for tk in tokens if ('${%s}' % name) in tk and tk != '${%s}' % name]
            if glued:
                rep.error('E_PLACEHOLDER_GLUED', sid, '${%s} внутри токена: %s' % (name, glued[0]))
            continue
        if name in PATH_PLACEHOLDERS:
            continue
        kind, sep, ref = name.partition(':')
        if not sep or kind not in PLACEHOLDER_KINDS:
            rep.error('E_PLACEHOLDER_UNKNOWN', sid, 'неизвестный плейсхолдер ${%s}' % name)
            continue
        if not ID_RE.match(ref):
            rep.error('E_PLACEHOLDER_ID', sid, 'некорректный id в ${%s}' % name)
            continue
        used_ids.add(ref)
        target = items.get(ref)
        if target is None or target['type'] != PLACEHOLDER_KINDS[kind]:
            rep.error('E_PLACEHOLDER_MISSING', sid, '${%s}: нет элемента типа %s' % (name, PLACEHOLDER_KINDS[kind]))
        if ref not in deps:
            rep.error('E_PLACEHOLDER_NOT_DEP', sid, '${%s}: id не объявлен в dependencies' % name)
    audit['unused_dependencies'] = sorted(deps - used_ids)

    # опции и режимы
    options, modes = grammar
    for tk in tokens:
        if not tk.startswith('--'):
            if tk not in ('${hostlists}', '${ipsets}'):
                audit['stray_tokens'].append(tk)
            continue
        opt = tk[2:].split('=', 1)[0]
        if opt not in options:
            rep.error('E_OPTION_UNKNOWN', sid, 'опции --%s нет в nfqws' % opt)
        if opt == 'dpi-desync':
            for mode in tk.split('=', 1)[1].split(',') if '=' in tk else ['']:
                if mode not in modes:
                    rep.error('E_MODE_UNKNOWN', sid, 'режима %r нет в nfqws' % mode)
                elif mode in DEPRECATED_MODES:
                    audit['deprecated_modes'][mode] = DEPRECATED_MODES[mode]
    if audit['stray_tokens']:
        rep.error('E_STRAY_TOKEN', sid, 'токены вне опций: %s' % ' '.join(audit['stray_tokens'][:5]))

    # профили
    profiles = split_profiles(tokens)
    audit['trailing_new'] = len(profiles) > 1 and not profiles[-1]
    audit['empty_profiles'] = sum(1 for p in profiles[:-1] if not p)
    real = [p for p in profiles if p]
    audit['profiles'] = len(real)
    if not real:
        rep.error('E_EMPTY_STRATEGY', sid, 'в стратегии нет ни одного профиля')
    for idx, prof in enumerate(real, 1):
        a = analyze_profile(prof)
        ports = ['tcp=' + ','.join(a['tcp'])] if a['tcp'] else []
        ports += ['udp=' + ','.join(a['udp'])] if a['udp'] else []
        label = ' '.join(['#%d' % idx] + ports)
        if a['scope'] == 'all':
            audit['profiles_all_traffic'].append(label)
        elif a['scope'].startswith('l7:'):
            audit['profiles_l7_only'].append('%s %s' % (label, a['scope']))
        if a['wide_udp']:
            audit['wide_udp'].append('#%d udp=%s' % (idx, ','.join(a['wide_udp'])))
        if a['no_port_filter']:
            rep.warn('W_NO_PORT_FILTER', sid, 'профиль #%d без --filter-tcp/--filter-udp (nft: tcp 80,443 и udp 443)' % idx)
        audit['syndata'] |= a['syndata']
        audit['autottl'] |= a['autottl']
        audit['any_protocol_no_cutoff'] |= a['any_protocol_no_cutoff']
    return audit


# ---------------------------------------------------------------- манифесты и bundle

def router_to_local(bundle_dir, router_path):
    prefix = ROUTER_BUNDLE_ROOT + '/'
    if not router_path.startswith(prefix):
        return None
    return os.path.join(bundle_dir, *router_path[len(prefix):].split('/'))


def check_manifest(path, rel_dir, bundle_dir, rep):
    where = 'manifests/%s/%s' % (rel_dir, os.path.basename(path))
    try:
        m = json.loads(read_bytes(path).decode('utf-8'))
    except (UnicodeDecodeError, ValueError) as exc:
        rep.error('E_MANIFEST_JSON', where, 'не JSON: %s' % exc)
        return None
    if not isinstance(m, dict) or tuple(m.keys()) != MANIFEST_KEYS:
        rep.error('E_MANIFEST_KEYS', where, 'ключи %s, ожидались %s' % (list(m)[:14] if isinstance(m, dict) else type(m), list(MANIFEST_KEYS)))
        return None
    ok = True
    stem = os.path.basename(path)[:-5]
    if m['schema'] != 1:
        rep.error('E_MANIFEST_FIELD', where, 'schema != 1'); ok = False
    if not isinstance(m['id'], str) or not ID_RE.match(m['id']) or m['id'] != stem:
        rep.error('E_MANIFEST_ID', where, 'id %r не совпадает с именем файла или некорректен' % m['id']); ok = False
    if m['type'] != DIR_TYPES.get(rel_dir):
        rep.error('E_MANIFEST_TYPE', where, 'type %r не соответствует каталогу %s' % (m['type'], rel_dir)); ok = False
    for key in ('name', 'version', 'author', 'description'):
        if not isinstance(m[key], str) or not m[key].strip():
            rep.error('E_MANIFEST_FIELD', where, 'пустое поле %s' % key); ok = False
    if m['source'] != 'bundle':
        rep.error('E_MANIFEST_FIELD', where, 'source должен быть bundle'); ok = False
    if not isinstance(m['installed_at'], int) or m['installed_at'] <= 0:
        rep.error('E_MANIFEST_FIELD', where, 'installed_at не положительное целое'); ok = False
    if m['manifest_url'] is not None and not (isinstance(m['manifest_url'], str) and m['manifest_url'].startswith('https://')):
        rep.error('E_MANIFEST_FIELD', where, 'manifest_url не https и не null'); ok = False
    deps = m['dependencies']
    if not isinstance(deps, list) or not all(isinstance(d, str) for d in deps):
        rep.error('E_MANIFEST_FIELD', where, 'dependencies не список строк'); ok = False
    else:
        for d in deps:
            if '/' in d or not ID_RE.match(d):
                rep.error('E_DEP_NOT_ID', where, 'зависимость не id: %r' % d); ok = False
    if not isinstance(m['sha256'], str) or not SHA_RE.match(m['sha256']):
        rep.error('E_MANIFEST_FIELD', where, 'sha256 не 64 hex в нижнем регистре'); ok = False
    expected_file = '%s/files/%s/%s' % (ROUTER_BUNDLE_ROOT, rel_dir, os.path.basename(str(m['file'])))
    local = router_to_local(bundle_dir, str(m['file']))
    if m['file'] != expected_file or local is None:
        rep.error('E_MANIFEST_FILE', where, 'file %r вне files/%s' % (m['file'], rel_dir)); ok = False
    elif not os.path.isfile(local):
        rep.error('E_FILE_MISSING', where, 'нет файла %s' % m['file']); ok = False
    elif isinstance(m['sha256'], str) and sha256_bytes(read_bytes(local)) != m['sha256']:
        rep.error('E_SHA', where, 'sha256 файла не совпадает с манифестом'); ok = False
    if ok:
        m['_local'] = local
        m['_manifest'] = where
    return m if ok else None


def _walk_files(root):
    out = []
    for dirpath, _dirs, files in os.walk(root):
        for f in files:
            out.append(os.path.relpath(os.path.join(dirpath, f), root).replace(os.sep, '/'))
    return sorted(out)


def validate_bundle(bundle_dir, grammar, rep):
    """Полная проверка bundle. Возвращает (items по id, листы, аудит стратегий)."""
    items = {}
    manifests_root = os.path.join(bundle_dir, 'manifests')
    files_root = os.path.join(bundle_dir, 'files')
    referenced = set()
    for rel in _walk_files(manifests_root):
        rel_dir, _, fname = rel.rpartition('/')
        if rel_dir not in DIR_TYPES or not fname.endswith('.json'):
            rep.error('E_UNKNOWN_PATH', 'manifests/' + rel, 'файл вне известных каталогов типов')
            continue
        m = check_manifest(os.path.join(manifests_root, *rel.split('/')), rel_dir, bundle_dir, rep)
        if m is None:
            continue
        if m['id'] in items:
            rep.error('E_DUP_ID', m['id'], 'id встречается в %s и %s' % (items[m['id']]['_manifest'], m['_manifest']))
            continue
        items[m['id']] = m
        referenced.add(m['file'][len(ROUTER_BUNDLE_ROOT) + len('/files/'):])
    for rel in _walk_files(files_root):
        if rel not in referenced:
            rep.error('E_ORPHAN_FILE', 'files/' + rel, 'файл не описан ни одним манифестом')

    for rid, rtype in REQUIRED_ITEMS.items():
        if rid not in items or items[rid]['type'] != rtype:
            rep.error('E_REQUIRED_MISSING', rid, 'нет обязательного элемента типа %s' % rtype)
    for m in items.values():
        for d in m['dependencies']:
            if d not in items:
                rep.error('E_DEP_MISSING', m['id'], 'зависимость %s не найдена в bundle' % d)

    content = {}
    audits = {}
    for sid in sorted(items):
        m = items[sid]
        data = read_bytes(m['_local'])
        if not data:
            rep.error('E_EMPTY_FILE', sid, 'пустой файл')
            continue
        if m['type'] in ('bin', 'lua_lib'):
            continue
        text = check_text_bytes(data, sid, rep, require_eol=m['type'] not in STRATEGY_TYPES)
        if text is None:
            continue
        if m['type'] in LIST_TYPES:
            content[sid] = check_hostlist(text, sid, rep)
        elif m['type'] in IPSET_TYPES:
            content[sid] = check_ipset(text, sid, rep)
        elif m['type'] == 'nfqws':
            audits[sid] = check_strategy(m, text, items, grammar, rep)
        if m['type'] in LIST_TYPES + IPSET_TYPES and not content[sid]:
            rep.error('E_EMPTY_LIST', sid, 'в листе нет ни одной записи')

    _check_include_vs_exclude(items, content, rep)
    _check_index(bundle_dir, items, rep)
    return items, content, audits


def _check_include_vs_exclude(items, content, rep):
    excl_domains = set()
    for sid, m in items.items():
        if m['type'] == 'list_exclude':
            excl_domains.update(d.lstrip('^') for d in content.get(sid, []))
    excl_nets = [n for sid, m in items.items() if m['type'] == 'ipset_exclude' for n in content.get(sid, [])]
    for sid, m in items.items():
        if m['type'] == 'list':
            for d in content.get(sid, []):
                name = d.lstrip('^')
                hit = [p for p in [name] + _parent_chain(name) if p in excl_domains]
                if hit:
                    rep.error('E_INCLUDE_EXCLUDED', sid, '%s перекрыт исключением %s' % (d, hit[0]))
        elif m['type'] == 'ipset':
            for n in content.get(sid, []):
                hit = [x for x in excl_nets if x.version == n.version and x.overlaps(n)]
                if hit:
                    rep.error('E_INCLUDE_EXCLUDED', sid, '%s пересекается с исключением %s' % (n, hit[0]))


def _check_index(bundle_dir, items, rep):
    path = os.path.join(bundle_dir, 'index.json')
    if not os.path.isfile(path):
        rep.error('E_INDEX', 'index.json', 'нет index.json')
        return
    data = read_bytes(path)
    check_text_bytes(data, 'index.json', rep)
    try:
        idx = json.loads(data.decode('utf-8'))
        listed = {(x['id'], x['type'], x['manifest']) for x in idx['items']}
        count = len(idx['items'])
    except (ValueError, KeyError, TypeError) as exc:
        rep.error('E_INDEX', 'index.json', 'не разбирается: %s' % exc)
        return
    actual = {(m['id'], m['type'], '%s/%s' % (ROUTER_BUNDLE_ROOT, m['_manifest'])) for m in items.values()}
    if idx.get('schema') != 1 or listed != actual or count != len(actual):
        diff = sorted(listed ^ actual)[:3]
        rep.error('E_INDEX', 'index.json', 'не совпадает с манифестами: %s' % diff)


# ---------------------------------------------------------------- presets.json

def _is_str_list(v):
    return isinstance(v, list) and all(isinstance(x, str) for x in v)


def _check_service(svc, items, tiers, ref_sizes, rep):
    sid = svc.get('id') if isinstance(svc, dict) else None
    where = 'presets:%s' % sid
    if not isinstance(svc, dict) or tuple(svc.keys()) != SERVICE_KEYS:
        rep.error('E_PRESET_KEYS', where, 'ключи сервиса не по схеме §9')
        return
    if not isinstance(sid, str) or not SERVICE_ID_RE.match(sid):
        rep.error('E_PRESET_FIELD', where, 'некорректный id')
    for key in ('name', 'description', 'note'):
        if not isinstance(svc[key], str) or not svc[key].strip():
            rep.error('E_PRESET_FIELD', where, 'пустое поле %s' % key)
    for key in ('description', 'note'):
        if isinstance(svc[key], str) and not CYRILLIC_RE.search(svc[key]):
            rep.error('E_PRESET_NOT_RU', where, 'поле %s не на русском' % key)
    for key, rtype in (('lists', 'list'), ('ipsets', 'ipset')):
        if not _is_str_list(svc[key]):
            rep.error('E_PRESET_FIELD', where, '%s не список строк' % key)
            continue
        for ref in svc[key]:
            if ref not in items or items[ref]['type'] != rtype:
                rep.error('E_PRESET_REF', where, '%s: нет элемента %s типа %s в bundle' % (key, ref, rtype))
    if not _is_str_list(svc['sources']) or any(s not in KNOWN_SOURCES for s in svc['sources']):
        rep.error('E_PRESET_SOURCE', where, 'неизвестная подписка в sources: %s' % svc['sources'])
    if svc['tier'] not in tiers:
        rep.error('E_PRESET_FIELD', where, 'tier %r нет в tiers' % svc['tier'])
    if svc['works'] not in ('yes', 'partial', 'no'):
        rep.error('E_PRESET_FIELD', where, 'works %r' % svc['works'])
    has_data = bool(svc['lists'] or svc['ipsets'] or svc['sources'])
    if svc['works'] == 'no' and (has_data or svc['test_targets']):
        rep.error('E_PRESET_NO', where, 'works=no, но заданы листы, подписки или цели')
    if svc['works'] != 'no' and not has_data:
        rep.error('E_PRESET_EMPTY', where, 'сервис без листов и подписок')
    targets = svc['test_targets']
    if not isinstance(targets, list):
        rep.error('E_PRESET_FIELD', where, 'test_targets не список')
        return
    if svc['works'] != 'no' and (svc['lists'] or svc['ipsets']) and not targets:
        rep.error('E_TARGET_NONE', where, 'у сервиса со встроенными листами нет целей проверки')
    for t in targets:
        if not isinstance(t, dict) or tuple(t.keys()) != ('url', 'min_bytes'):
            rep.error('E_TARGET_FIELD', where, 'цель не {url, min_bytes}')
            continue
        url, mb = t['url'], t['min_bytes']
        if not isinstance(url, str) or not url.startswith('https://') or re.search(r'\s', url):
            rep.error('E_TARGET_FIELD', where, 'url не https: %r' % url)
        if not isinstance(mb, int) or isinstance(mb, bool) or mb < 0:
            rep.error('E_TARGET_FIELD', where, 'min_bytes не целое >= 0')
            continue
        if mb == 0:
            continue
        ref = ref_sizes.get(url)
        if ref is None:
            rep.error('E_TARGET_REF', where, 'для %s нет справочного размера ответа' % url)
        elif not FREEZE_BYTES < mb < ref:
            rep.error('E_TARGET_MIN', where, '%s: min_bytes %d вне (%d, %d)' % (url, mb, FREEZE_BYTES, ref))


def validate_presets(presets, items, audits, ref_sizes, rep):
    if not isinstance(presets, dict) or tuple(presets.keys()) != PRESET_TOP_KEYS or presets['schema'] != 1:
        rep.error('E_PRESET_KEYS', 'presets', 'верхний уровень не по схеме §9')
        return
    tiers = presets['tiers']
    if (not isinstance(tiers, dict) or set(tiers) != {'light', 'full'}
            or any(not isinstance(v, dict) or tuple(v) != ('min_ram_mib',) or not isinstance(v['min_ram_mib'], int)
                   for v in tiers.values())):
        rep.error('E_PRESET_TIERS', 'presets', 'tiers не по схеме')
        tiers = {'light': {}, 'full': {}}
    services = presets['services']
    if not isinstance(services, list) or not services:
        rep.error('E_PRESET_KEYS', 'presets', 'services пуст')
        return
    ids = [s.get('id') for s in services if isinstance(s, dict)]
    if len(ids) != len(set(ids)):
        rep.error('E_PRESET_DUP', 'presets', 'повторяющиеся id сервисов')
    for svc in services:
        _check_service(svc, items, tiers, ref_sizes, rep)

    always = presets['always']
    if not isinstance(always, dict) or tuple(always.keys()) != ('exclude_lists', 'exclude_ipsets'):
        rep.error('E_PRESET_KEYS', 'presets:always', 'ключи always не по схеме')
    else:
        for key, rtype in (('exclude_lists', 'list_exclude'), ('exclude_ipsets', 'ipset_exclude')):
            for ref in always[key] if _is_str_list(always[key]) else [None]:
                if ref not in items or items[ref]['type'] != rtype:
                    rep.error('E_PRESET_REF', 'presets:always', '%s: нет элемента %s типа %s' % (key, ref, rtype))

    _check_defaults(presets['defaults'], services, items, audits, rep)


def _check_defaults(defaults, services, items, audits, rep):
    where = 'presets:defaults'
    if not isinstance(defaults, dict) or tuple(defaults.keys()) != ('services', 'strategy', 'quick_test_strategies'):
        rep.error('E_PRESET_KEYS', where, 'ключи defaults не по схеме')
        return
    by_id = {s['id']: s for s in services if isinstance(s, dict) and 'id' in s}
    for sid in defaults['services'] if _is_str_list(defaults['services']) else [None]:
        if sid not in by_id or by_id[sid].get('works') == 'no':
            rep.error('E_PRESET_DEFAULTS', where, 'сервис по умолчанию %r не существует или works=no' % sid)
    strategy = defaults['strategy']
    if strategy not in items or items[strategy]['type'] != 'nfqws':
        rep.error('E_PRESET_DEFAULTS', where, 'стратегии по умолчанию %r нет в bundle' % strategy)
    quick = defaults['quick_test_strategies']
    if not _is_str_list(quick) or not QUICK_MIN <= len(quick) <= QUICK_MAX or len(set(quick)) != len(quick):
        rep.error('E_QUICK_COUNT', where, 'quick_test_strategies: нужно %d–%d уникальных id' % (QUICK_MIN, QUICK_MAX))
        return
    if strategy not in quick:
        rep.error('E_QUICK_DEFAULT', where, 'стратегия по умолчанию не входит в быстрый набор')
    for sid in quick:
        if sid not in items or items[sid]['type'] != 'nfqws':
            rep.error('E_QUICK_REF', where, '%s: нет стратегии nfqws в bundle' % sid)
            continue
        if rep.errors_for(sid):
            rep.error('E_QUICK_BROKEN', where, '%s: у стратегии есть ошибки проверки' % sid)
        if audits.get(sid, {}).get('profiles_all_traffic'):
            rep.error('E_QUICK_GLOBAL', where, '%s: есть профиль на весь трафик порта' % sid)


# ---------------------------------------------------------------- грамматика nfqws из исходников

def load_nfqws_grammar(nfq_dir):
    """Имена опций из long_options[] (nfqws.c) и режимов из desync_mode_from_string (desync.c)."""
    src = read_bytes(os.path.join(nfq_dir, 'nfqws.c')).decode('utf-8', 'replace')
    block = src.split('long_options[] = {', 1)[1].split('\n};', 1)[0]
    options = set(re.findall(r'\{"([a-z0-9-]+)",\s*(?:no|required|optional)_argument', block))
    dsrc = read_bytes(os.path.join(nfq_dir, 'desync.c')).decode('utf-8', 'replace')
    body = dsrc.split('enum dpi_desync_mode desync_mode_from_string(const char *s)', 1)[1].split('\n}', 1)[0]
    modes = set(re.findall(r'strcmp\(s, "([a-z0-9]+)"\)', body))
    for must in ('qnum', 'dpi-desync', 'new', 'hostlist', 'ipset', 'filter-udp', 'comment'):
        if must not in options:
            raise RuntimeError('разбор long_options сломан: нет --%s' % must)
    for must in ('fake', 'multisplit', 'split2', 'disorder2'):
        if must not in modes:
            raise RuntimeError('разбор режимов desync сломан: нет %s' % must)
    return options, modes
