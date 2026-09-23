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
MANIFEST_OPTIONAL_KEYS = ('name_en', 'description_en',   # §14.6, после обязательных и в этом порядке
                          'name_zh', 'description_zh',   # §14.6, zh-CN для Windows-приложения
                          'service', 'variant', 'generated', 'method', 'license')   # §16.2, собственные списки
OWN_LIST_AUTHOR = 'zaprett-openwrt'
OWN_LIST_KEYS = ('name_en', 'description_en', 'name_zh', 'description_zh', 'service', 'variant', 'generated',
                 'method', 'license')
LANG_PAIRS = (('name_en', 'name_zh'), ('description_en', 'description_zh'), ('note_en', 'note_zh'))
CJK_RE = re.compile('[\u4e00-\u9fff]')
LIST_VARIANTS = ('core', 'full', 'ipset', 'ipset6', 'voice')
DATE_RE = re.compile(r'^20[0-9]{2}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12][0-9]|3[01])$')
MAX_NAME_BYTES, MAX_DESCRIPTION_BYTES = 128, 1024   # store.uc обрезает name/description по байтам
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
SERVICE_OPTIONAL_KEYS = ('name_en', 'description_en', 'note_en', 'name_zh', 'description_zh', 'note_zh',
                         'needs_dns', 'variants')   # §14.6, §15.3, §16.3
VARIANT_KEYS = ('id', 'name', 'name_en', 'name_zh', 'description', 'description_en', 'description_zh',
                'lists', 'ipsets', 'tier')   # §16.3, §14.6
DEFAULTS_KEYS = ('services', 'strategy', 'quick_test_strategies')
DEFAULTS_NFQWS2_KEYS = ('strategy_nfqws2', 'quick_test_strategies_nfqws2')       # §15.6, только вместе
QUICK2_MIN, QUICK2_MAX = 6, 10
SERVICE_ID_RE = re.compile(r'^[a-z0-9_]{1,32}$')
KNOWN_SOURCES = ('refilter_domains', 'antifilter_allyouneed', 'cloudflare_v4', 'cloudflare_v6')
FREEZE_BYTES = 16384
QUICK_MIN, QUICK_MAX = 8, 12
CYRILLIC_RE = re.compile('[А-Яа-яЁё]')
# Частные и служебные сети (RFC 6890, реестры IANA): во включающих IP-листах им не место
SPECIAL_NETS = [ipaddress.ip_network(x) for x in (
    '0.0.0.0/8', '10.0.0.0/8', '100.64.0.0/10', '127.0.0.0/8', '169.254.0.0/16', '172.16.0.0/12', '192.0.0.0/24',
    '192.0.2.0/24', '192.88.99.0/24', '192.168.0.0/16', '198.18.0.0/15', '198.51.100.0/24', '203.0.113.0/24',
    '224.0.0.0/4', '240.0.0.0/4', '::/127', '::ffff:0:0/96', '64:ff9b::/96', '100::/64', '2001::/23', '2001:db8::/32',
    '2002::/16', 'fc00::/7', 'fe80::/10', 'ff00::/8')]


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


def _new_audit(sid, info):
    return {'id': sid, 'placeholders': [], 'deprecated_modes': {}, 'comment_blocks': info['comment_blocks'],
            'comment_junk_tokens': len(info['comment_junk']), 'trailing_backslashes': info['trailing_backslashes'],
            'stray_tokens': [], 'profiles': 0, 'empty_profiles': 0, 'trailing_new': False,
            'profiles_all_traffic': [], 'profiles_l7_only': [], 'wide_udp': [], 'syndata': False,
            'autottl': False, 'any_protocol_no_cutoff': False, 'unused_dependencies': []}


def check_strategy(item, text, items, grammar, rep):
    """Проверка стратегии nfqws: плейсхолдеры, зависимости, опции, режимы. Возвращает аудит."""
    sid = item['id']
    tokens, info = tokenize_strategy(text)
    audit = _new_audit(sid, info)
    _check_placeholders(item, text, tokens, info, items, rep, audit)

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
    _audit_profiles(sid, tokens, rep, audit)
    return audit


def _check_placeholders(item, text, tokens, info, items, rep, audit):
    sid = item['id']
    deps = set(item.get('dependencies') or [])
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


def _audit_profiles(sid, tokens, rep, audit):
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


# ---------------------------------------------------------------- стратегии nfqws2 (zapret2)

ROUTER_LUA_DIR = '/usr/share/zaprett/lua'
LUA_BASE_LIBS = ('zapret-lib', 'zapret-antidpi', 'zapret-auto')   # генератор добавляет их сам, если в стратегии нет --lua-init
BUILTIN_BLOBS = ('fake_default_tls', 'fake_default_http', 'fake_default_quic')
BLOB_ARGS = ('blob', 'seqovl_pattern', 'pattern', 'fallback')
MARKER_ARGS = ('pos', 'midhost', 'disorder_after')
HEX_RE = re.compile(r'^0x(?:[0-9a-fA-F]{2})+$')
AUTOTTL_RE = re.compile(r'^-?[0-9]+,[0-9]+-[0-9]+$')
RANGE_RE = re.compile(r'^(?:a|x|(?:[ndbs][0-9]+)?(?:[-<](?:[ndbs][0-9]+)?)?)$')
BLOB_DEF_RE = re.compile(r'^([A-Za-z_][A-Za-z0-9_]*):(0x(?:[0-9a-fA-F]{2})+|(?:\+[0-9]+)?@\S+)$')


def _marker_ok(value, markers):
    m = re.match(r'^(?:(-?[0-9]+)|([a-z]+)([+-][0-9]+)?)$', value)
    return bool(m) and (m.group(1) is not None or m.group(2) in markers)


def _check_lua_desync(sid, pidx, value, grammar2, funcs, blobs, rep):
    """Один --lua-desync=fn[:k[=v]...]. Возвращает (имя функции, номер strategy или None)."""
    fn, _, rest = value.partition(':')
    if fn not in funcs:
        known = any(fn in f for f in grammar2['lua'].values())
        rep.error('E_LUA_FUNC', sid, 'профиль #%d: функции %r нет%s' % (
            pidx, fn, ' в подключённых --lua-init' if known else ' в Lua-библиотеках zapret2'))
    strategy = None
    for arg in rest.split(':') if rest else []:
        key, eq, val = arg.partition('=')
        if not key:
            rep.error('E_LUA_ARG', sid, 'профиль #%d: пустой аргумент в %s' % (pidx, value))
            continue
        if key in BLOB_ARGS and eq:
            if not (HEX_RE.match(val) or val in blobs or val in BUILTIN_BLOBS):
                rep.error('E_BLOB_UNDEFINED', sid, 'профиль #%d: блоб %r не задан через --blob' % (pidx, val))
        elif key in MARKER_ARGS or (key == 'seqovl' and fn in ('multidisorder', 'multidisorder_legacy', 'fakeddisorder')):
            for mk in val.split(',') if key == 'pos' else [val]:
                if not _marker_ok(mk, grammar2['markers']):
                    rep.error('E_MARKER', sid, 'профиль #%d: неверный маркер %s=%r' % (pidx, key, mk))
        elif key == 'seqovl' and not re.match(r'^[0-9]+$', val):
            rep.error('E_LUA_ARG', sid, 'профиль #%d: seqovl у %s — только число' % (pidx, fn))
        elif key in ('ip_autottl', 'ip6_autottl') and not AUTOTTL_RE.match(val):
            rep.error('E_LUA_ARG', sid, 'профиль #%d: %s=%r не в формате delta,min-max' % (pidx, key, val))
        elif key == 'payload':
            for p in val.lstrip('~').split(','):
                if p not in grammar2['payloads']:
                    rep.error('E_PAYLOAD', sid, 'профиль #%d: пейлоада %r нет' % (pidx, p))
        elif key == 'strategy':
            if not re.match(r'^[1-9][0-9]*$', val):
                rep.error('E_CIRCULAR', sid, 'профиль #%d: strategy=%r не номер' % (pidx, val))
            else:
                strategy = int(val)
    return fn, strategy


def check_strategy2(item, text, items, grammar2, rep):
    """Проверка стратегии nfqws2: плейсхолдеры, опции, Lua-функции и их библиотеки, блобы, маркеры, пейлоады,
    протоколы, нумерация circular. Ловит то, что `nfqws2 --intercept=0` не видит (блоб по имени проверяется только
    при обработке пакета). Возвращает аудит в формате check_strategy."""
    sid = item['id']
    tokens, info = tokenize_strategy(text)
    audit = _new_audit(sid, info)
    _check_placeholders(item, text, tokens, info, items, rep, audit)

    libs = []
    blobs = set()
    for tk in tokens:
        if tk.startswith('--lua-init='):
            val = tk[len('--lua-init='):]
            m = re.match(r'^@%s/([a-z0-9-]+)\.lua$' % re.escape(ROUTER_LUA_DIR), val)
            if not m or m.group(1) not in grammar2['lua']:
                rep.error('E_LUA_INIT', sid, '--lua-init только @%s/<библиотека zapret2>.lua: %r' % (ROUTER_LUA_DIR, val))
            else:
                libs.append(m.group(1))
        elif tk.startswith('--blob='):
            m = BLOB_DEF_RE.match(tk[len('--blob='):])
            if not m:
                rep.error('E_BLOB_DEF', sid, 'неверный %s' % tk[:60])
            elif m.group(1) in blobs or m.group(1) in BUILTIN_BLOBS:
                rep.error('E_BLOB_DEF', sid, 'блоб %s задан повторно' % m.group(1))
            else:
                blobs.add(m.group(1))
    if libs and not all(b in libs for b in LUA_BASE_LIBS):
        rep.error('E_LUA_INIT', sid, 'свои --lua-init отменяют базовые: подключите и %s' % ', '.join(LUA_BASE_LIBS))
    funcs = set()
    for lib in libs or LUA_BASE_LIBS:
        funcs |= grammar2['lua'].get(lib, set())

    for tk in tokens:
        if not tk.startswith('--'):
            if tk not in ('${hostlists}', '${ipsets}'):
                audit['stray_tokens'].append(tk)
            continue
        opt, eq, val = tk[2:].partition('=')
        if opt not in grammar2['options']:
            rep.error('E_OPTION_UNKNOWN', sid, 'опции --%s нет в nfqws2' % opt)
        elif opt == 'payload':
            for p in val.lstrip('~').split(','):
                if p not in grammar2['payloads']:
                    rep.error('E_PAYLOAD', sid, 'пейлоада %r нет в nfqws2' % p)
        elif opt == 'filter-l7':
            for p in val.split(','):
                if p not in grammar2['l7']:
                    rep.error('E_L7', sid, 'протокола %r нет в nfqws2' % p)
        elif opt in ('in-range', 'out-range') and not RANGE_RE.match(val):
            rep.error('E_RANGE', sid, 'неверный диапазон --%s=%s' % (opt, val))
    if audit['stray_tokens']:
        rep.error('E_STRAY_TOKEN', sid, 'токены вне опций: %s' % ' '.join(audit['stray_tokens'][:5]))

    for pidx, prof in enumerate([p for p in split_profiles(tokens) if p], 1):
        has_circular, numbers = False, set()
        for tk in prof:
            if not tk.startswith('--lua-desync='):
                continue
            fn, num = _check_lua_desync(sid, pidx, tk[len('--lua-desync='):], grammar2, funcs, blobs, rep)
            has_circular |= fn == 'circular'
            if num is not None:
                numbers.add(num)
        if numbers and not has_circular:
            rep.error('E_CIRCULAR', sid, 'профиль #%d: strategy=N без оркестратора circular' % pidx)
        if has_circular and numbers != set(range(1, len(numbers) + 1)):
            rep.error('E_CIRCULAR', sid, 'профиль #%d: номера strategy %s должны идти подряд с 1' % (pidx, sorted(numbers)))
        if has_circular and not numbers:
            rep.error('E_CIRCULAR', sid, 'профиль #%d: circular без инстансов strategy=N' % pidx)
    _audit_profiles(sid, tokens, rep, audit)
    desync = [tk for tk in tokens if tk.startswith('--lua-desync=')]
    audit['autottl'] = any(re.search(r':ip6?_autottl=', tk) for tk in desync)
    audit['syndata'] = any(tk.startswith('--lua-desync=syndata') for tk in desync)
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
    keys = tuple(m.keys()) if isinstance(m, dict) else ()
    extra = keys[len(MANIFEST_KEYS):]
    if (keys[:len(MANIFEST_KEYS)] != MANIFEST_KEYS
            or extra != tuple(k for k in MANIFEST_OPTIONAL_KEYS if k in extra)):
        rep.error('E_MANIFEST_KEYS', where, 'ключи %s, ожидались %s + необязательные %s' % (
            list(m)[:16] if isinstance(m, dict) else type(m), list(MANIFEST_KEYS), list(MANIFEST_OPTIONAL_KEYS)))
        return None
    ok = True
    for key in extra:
        if not isinstance(m[key], str) or not m[key].strip():
            rep.error('E_MANIFEST_FIELD', where, 'пустое поле %s' % key); ok = False
    for key, limit in (('name', MAX_NAME_BYTES), ('description', MAX_DESCRIPTION_BYTES),
                       ('name_en', MAX_NAME_BYTES), ('description_en', MAX_DESCRIPTION_BYTES),
                       ('name_zh', MAX_NAME_BYTES), ('description_zh', MAX_DESCRIPTION_BYTES)):
        if isinstance(m.get(key), str) and len(m[key].encode('utf-8')) > limit:
            rep.error('E_MANIFEST_LONG', where, '%s длиннее %d байт' % (key, limit)); ok = False
    ok = check_lang_pairs(m, where, rep) and ok
    if m.get('author') == OWN_LIST_AUTHOR and m.get('type') in ('list', 'ipset'):
        ok = _check_own_meta(m, extra, where, rep) and ok
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


def check_lang_pairs(obj, where, rep):
    """§14.6: у каждого поля *_en есть *_zh и наоборот; *_zh — по-китайски, без ASCII-кавычек (глоссарий)."""
    ok = True
    for en, zh in LANG_PAIRS:
        if (en in obj) != (zh in obj):
            rep.error('E_LANG_PAIR', where, 'есть %s, но нет %s' % ((en, zh) if en in obj else (zh, en)))
            ok = False
        elif zh in obj and isinstance(obj[zh], str):
            # имя может быть латиницей (YouTube, nfqws2：general), описание и заметка — только по-китайски
            if zh != 'name_zh' and not CJK_RE.search(obj[zh]):
                rep.error('E_ZH_TEXT', where, '%s без китайских символов' % zh)
                ok = False
            if '"' in obj[zh]:
                rep.error('E_ZH_TEXT', where, '%s содержит ASCII-кавычки' % zh)
                ok = False
    return ok


def _check_own_meta(m, extra, where, rep):
    """Собственный список проекта (§16.2): обязательны service, variant, generated, method, license, *_en."""
    missing = [k for k in OWN_LIST_KEYS if k not in extra]
    if missing:
        rep.error('E_OWN_META', where, 'у собственного списка нет полей %s' % missing)
        return False
    ok = True
    if not SERVICE_ID_RE.match(m['service']):
        rep.error('E_OWN_META', where, 'service %r некорректен' % m['service']); ok = False
    if m['variant'] not in LIST_VARIANTS:
        rep.error('E_OWN_META', where, 'variant %r не из %s' % (m['variant'], LIST_VARIANTS)); ok = False
    if not DATE_RE.match(m['generated']):
        rep.error('E_OWN_META', where, 'generated %r не YYYY-MM-DD' % m['generated']); ok = False
    if m['license'] != 'MIT':
        rep.error('E_OWN_META', where, 'license %r, ожидалась MIT' % m['license']); ok = False
    base = 'zaprett-' + m['service'].replace('_', '-')
    if m['id'] != base and not m['id'].startswith(base + '-'):
        rep.error('E_OWN_META', where, 'id %s не вида %s или %s-<вариант>' % (m['id'], base, base)); ok = False
    return ok


def _walk_files(root):
    out = []
    for dirpath, _dirs, files in os.walk(root):
        for f in files:
            out.append(os.path.relpath(os.path.join(dirpath, f), root).replace(os.sep, '/'))
    return sorted(out)


def validate_bundle(bundle_dir, grammar, rep, grammar2=None):
    """Полная проверка bundle. Возвращает (items по id, листы, аудит стратегий nfqws и nfqws2).

    grammar2 — грамматика nfqws2 (load_nfqws2_grammar); без неё стратегии nfqws2 считаются ошибкой."""
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
        elif m['type'] == 'nfqws2':
            if grammar2 is None:
                rep.error('E_NO_GRAMMAR2', sid, 'нет грамматики nfqws2 для проверки')
            else:
                audits[sid] = check_strategy2(m, text, items, grammar2, rep)
        if m['type'] in LIST_TYPES + IPSET_TYPES and not content[sid]:
            rep.error('E_EMPTY_LIST', sid, 'в листе нет ни одной записи')
        if m['type'] == 'ipset':
            for net in content[sid]:
                bad = [x for x in SPECIAL_NETS if x.version == net.version and x.overlaps(net)]
                if bad:
                    rep.error('E_CIDR_SPECIAL', sid, '%s пересекается с сетью специального назначения %s' % (net, bad[0]))

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
    keys = tuple(svc.keys()) if isinstance(svc, dict) else ()
    extra = keys[len(SERVICE_KEYS):]
    if (keys[:len(SERVICE_KEYS)] != SERVICE_KEYS or len(set(extra)) != len(extra)
            or any(k not in SERVICE_OPTIONAL_KEYS for k in extra)):
        rep.error('E_PRESET_KEYS', where, 'ключи сервиса не по схеме §9')
        return
    check_lang_pairs(svc, where, rep)
    for key in extra:
        if key == 'needs_dns':
            if not isinstance(svc[key], bool):
                rep.error('E_PRESET_FIELD', where, 'needs_dns не true/false')
        elif key == 'variants':
            _check_variants(svc, items, tiers, where, rep)
        elif not isinstance(svc[key], str) or not svc[key].strip():
            rep.error('E_PRESET_FIELD', where, 'пустое поле %s' % key)
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


def _check_variants(svc, items, tiers, where, rep):
    """Необязательные варианты сервиса (§16.3): альтернативные наборы листов."""
    variants = svc['variants']
    if not isinstance(variants, list) or not variants:
        rep.error('E_PRESET_VARIANT', where, 'variants не непустой список')
        return
    if svc.get('works') == 'no':
        rep.error('E_PRESET_VARIANT', where, 'варианты у сервиса works=no')
    seen = set()
    main = (sorted(svc.get('lists') or []), sorted(svc.get('ipsets') or []))
    for v in variants:
        vw = '%s/variant:%s' % (where, v.get('id') if isinstance(v, dict) else None)
        if not isinstance(v, dict) or tuple(v.keys()) != VARIANT_KEYS:
            rep.error('E_PRESET_VARIANT', vw, 'ключи варианта не %s' % list(VARIANT_KEYS))
            continue
        if not isinstance(v['id'], str) or not SERVICE_ID_RE.match(v['id']) or v['id'] in seen:
            rep.error('E_PRESET_VARIANT', vw, 'id варианта некорректен или повторяется')
        seen.add(v['id'])
        check_lang_pairs(v, vw, rep)
        for key in ('name', 'name_en', 'name_zh', 'description', 'description_en', 'description_zh'):
            if not isinstance(v[key], str) or not v[key].strip():
                rep.error('E_PRESET_VARIANT', vw, 'пустое поле %s' % key)
        if isinstance(v['description'], str) and not CYRILLIC_RE.search(v['description']):
            rep.error('E_PRESET_NOT_RU', vw, 'описание варианта не на русском')
        ok_refs = True
        for key, rtype in (('lists', 'list'), ('ipsets', 'ipset')):
            if not _is_str_list(v[key]):
                rep.error('E_PRESET_VARIANT', vw, '%s не список строк' % key)
                ok_refs = False
                continue
            for ref in v[key]:
                if ref not in items or items[ref]['type'] != rtype:
                    rep.error('E_PRESET_REF', vw, '%s: нет элемента %s типа %s в bundle' % (key, ref, rtype))
        if ok_refs and not (v['lists'] or v['ipsets']):
            rep.error('E_PRESET_VARIANT', vw, 'вариант без листов')
        if ok_refs and (sorted(v['lists']), sorted(v['ipsets'])) == main:
            rep.error('E_PRESET_VARIANT', vw, 'вариант совпадает с основным набором')
        if v['tier'] not in tiers:
            rep.error('E_PRESET_VARIANT', vw, 'tier %r нет в tiers' % v['tier'])


def _check_own_lists(services, items, rep):
    """Собственные списки (§16.2): service существует в presets, у сервиса минимум два списка."""
    ids = {s.get('id') for s in services if isinstance(s, dict)}
    per_service = {}
    for m in items.values():
        if m.get('author') == OWN_LIST_AUTHOR and m['type'] in ('list', 'ipset') and 'service' in m:
            per_service.setdefault(m['service'], []).append(m['id'])
            if m['service'] not in ids:
                rep.error('E_LIST_SERVICE', m['id'], 'service %r нет в presets.json' % m['service'])
    for sid, lst in sorted(per_service.items()):
        if len(lst) < 2:
            rep.error('E_LIST_SERVICE', sid, 'у сервиса меньше двух собственных списков: %s' % lst)


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
    _check_own_lists(services, items, rep)

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
    if not isinstance(defaults, dict) or tuple(defaults.keys()) not in (DEFAULTS_KEYS, DEFAULTS_KEYS + DEFAULTS_NFQWS2_KEYS):
        rep.error('E_PRESET_KEYS', where, 'ключи defaults не по схеме')
        return
    if 'strategy_nfqws2' in defaults:
        _check_defaults_nfqws2(defaults, items, audits, rep)
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


def _check_defaults_nfqws2(defaults, items, audits, rep):
    where = 'presets:defaults'
    strategy = defaults['strategy_nfqws2']
    if strategy not in items or items[strategy]['type'] != 'nfqws2':
        rep.error('E_PRESET_DEFAULTS', where, 'стратегии nfqws2 по умолчанию %r нет в bundle' % strategy)
    quick = defaults['quick_test_strategies_nfqws2']
    if not _is_str_list(quick) or not QUICK2_MIN <= len(quick) <= QUICK2_MAX or len(set(quick)) != len(quick):
        rep.error('E_QUICK_COUNT', where, 'quick_test_strategies_nfqws2: нужно %d–%d уникальных id' % (QUICK2_MIN, QUICK2_MAX))
        return
    if strategy not in quick:
        rep.error('E_QUICK_DEFAULT', where, 'стратегия nfqws2 по умолчанию не входит в быстрый набор')
    for sid in quick:
        if sid not in items or items[sid]['type'] != 'nfqws2':
            rep.error('E_QUICK_REF', where, '%s: нет стратегии nfqws2 в bundle' % sid)
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


def _c_string_array(src, name):
    body = src.split(name + '[] = {', 1)[1].split('};', 1)[0]
    return set(re.findall(r'"([a-z0-9_]+)"', body))


def load_nfqws2_grammar(nfq2_dir, lua_dir):
    """Грамматика nfqws2 (zapret2): опции из long_options[] (nfq2/nfqws.c), типы пейлоадов, протоколы и маркеры
    позиций (nfq2/protocol.c), desync-функции `function <имя>(ctx, desync)` по Lua-библиотекам lua/zapret-*.lua."""
    src = read_bytes(os.path.join(nfq2_dir, 'nfqws.c')).decode('utf-8', 'replace')
    block = src.split('long_options[] = {', 1)[1].split('\n};', 1)[0]
    options = set(re.findall(r'\{"([a-z0-9-]+)",\s*(?:no|required|optional)_argument', block))
    psrc = read_bytes(os.path.join(nfq2_dir, 'protocol.c')).decode('utf-8', 'replace')
    lua = {}
    for fname in sorted(os.listdir(lua_dir)):
        m = re.match(r'^(zapret-[a-z0-9]+)\.lua$', fname)
        if m:
            text = read_bytes(os.path.join(lua_dir, fname)).decode('utf-8', 'replace')
            lua[m.group(1)] = set(re.findall(r'(?m)^function\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(\s*ctx\s*,\s*desync\s*\)', text))
    grammar = {'options': options, 'payloads': _c_string_array(psrc, 'l7payload_name'),
               'l7': _c_string_array(psrc, 'l7proto_name'), 'markers': _c_string_array(psrc, 'posmarker_names'),
               'lua': lua}
    for must in ('lua-desync', 'lua-init', 'blob', 'payload', 'new', 'hostlist', 'filter-l7', 'out-range'):
        if must not in options:
            raise RuntimeError('разбор long_options nfqws2 сломан: нет --%s' % must)
    for key, must in (('payloads', 'tls_client_hello'), ('l7', 'quic'), ('markers', 'midsld')):
        if must not in grammar[key]:
            raise RuntimeError('разбор protocol.c сломан: нет %s в %s' % (must, key))
    for lib, fn in (('zapret-antidpi', 'multisplit'), ('zapret-auto', 'circular'), ('zapret-lib', 'luaexec')):
        if fn not in lua.get(lib, ()):
            raise RuntimeError('разбор Lua сломан: нет %s в %s' % (fn, lib))
    return grammar
