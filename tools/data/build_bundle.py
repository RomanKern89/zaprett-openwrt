"""Генератор встроенного снимка (bundle) и presets.json для пакета zaprett (OpenWrt).

Запуск:
    PYTHONUTF8=1 python tools/data/build_bundle.py              сборка + проверки + установка в пакет
    PYTHONUTF8=1 python tools/data/build_bundle.py --check-only только проверка того, что лежит в пакете

Входы (зафиксированы sha256, при расхождении генератор останавливается):
  upstream/lists-refs/curated/*.txt   очищенные листы (research/03-lists.md §10)
  upstream/zaprett-repo/              стратегии nfqws и фейки (bin), index.json CherretGit/zaprett-repo
  upstream/zapret/nfq/*.c             имена опций и режимов nfqws v72.13 для проверки стратегий
Выходы:
  packages/zaprett/files/usr/share/zaprett/bundle/{files,manifests}/<dir>/..., bundle/index.json
  packages/zaprett/files/usr/share/zaprett/presets.json
  tools/data/strategy_audit.json      аудит стратегий (устаревшие режимы, --comment, висячие \\, плейсхолдеры)
Сборка сначала пишется в tools/data/.staging, проверяется и только потом копируется в пакет.
"""
import argparse
import calendar
import gzip
import hashlib
import io
import ipaddress
import json
import lzma
import os
import shutil
import sys
import tarfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.dont_write_bytecode = True  # не оставлять __pycache__ в репозитории
import bundle_checks as bc  # noqa: E402
import presets_data  # noqa: E402

PROJECT = os.path.dirname(os.path.dirname(HERE))
UPSTREAM = os.path.join(PROJECT, 'upstream')
CURATED_DIR = os.path.join(UPSTREAM, 'lists-refs', 'curated')
ZREPO_DIR = os.path.join(UPSTREAM, 'zaprett-repo')
NFQ_DIR = os.path.join(UPSTREAM, 'zapret', 'nfq')
SHARE_DIR = os.path.join(PROJECT, 'packages', 'zaprett', 'files', 'usr', 'share', 'zaprett')
BUNDLE_DIR = os.path.join(SHARE_DIR, 'bundle')
PRESETS_PATH = os.path.join(SHARE_DIR, 'presets.json')
STAGING_DIR = os.path.join(HERE, '.staging')
AUDIT_PATH = os.path.join(HERE, 'strategy_audit.json')

SNAPSHOT = (2026, 9, 17)
SNAPSHOT_VERSION = '%04d.%02d.%02d' % SNAPSHOT
SNAPSHOT_EPOCH = calendar.timegm(SNAPSHOT + (0, 0, 0))

ZREPO_RAW = 'https://raw.githubusercontent.com/CherretGit/zaprett-repo/refs/heads/main/'
ZREPO_INDEX_SHA256 = '210206aec68c18e3b206aad9f20a6cb9ece7e4c7ba93d0fbcbc806fe7b004014'
ZREPO_COMMIT = '09b30f0ee80731807eec59d2a5b796681739e654'
COPY_TYPES = ('nfqws', 'bin')
ARTIFACT_EXT = {'nfqws': '.txt', 'bin': '.bin'}

LIST_AUTHOR = 'zaprett для OpenWrt'
CROSS_SOURCES = ('Flowseal/zapret-discord-youtube, itdoginfo/allow-domains, CherretGit/zaprett-repo, '
                 'remittor/zapret-openwrt')

# Встроенные листы (id фиксированы контрактом §3). sha256 — из research/03-lists.md §10 и манифеста zaprett-repo.
LIST_ITEMS = [
    {'id': 'zaprett-youtube', 'type': 'list', 'curated': 'youtube.txt',
     'sha256': '82adaddbb7f4a91b284444c2ac1f68b726bca4feb08780e63d3524a1f83d7967', 'name': 'YouTube',
     'description': 'Домены YouTube ({n} шт.): сайт, видео (googlevideo.com), превью и API. Очищенный набор '
                    'от 2026-09-17: домен взят, если он есть минимум в двух независимых источниках (%s) '
                    'и существует в DNS. Поддомены учитываются автоматически.' % CROSS_SOURCES},
    {'id': 'zaprett-discord', 'type': 'list', 'curated': 'discord.txt',
     'sha256': '5e3073dfef475ff001e204cc439aa96b44052071e01239ad3824d2801f026194', 'name': 'Discord',
     'description': 'Домены Discord ({n} шт.): сайт, приложение, вложения и голосовые серверы discord.media. '
                    'Отбор как у YouTube (источники: %s), discordstatus.com добавлен по списку Flowseal. '
                    'Голосовые звонки обрабатывает отдельный профиль стратегии по портам.' % CROSS_SOURCES},
    {'id': 'zaprett-telegram', 'type': 'list', 'curated': 'telegram.txt',
     'sha256': '966dc007f4bfb378c460ccae8b5c7f0397d73876060741bca363603e4e0ab460', 'name': 'Telegram',
     'description': 'Домены Telegram ({n} шт.): telegram.org, t.me, telesco.pe и другие сайты Telegram. Взяты '
                    'домены, которые есть и в CherretGit/zaprett-repo, и в itdoginfo/allow-domains и существуют '
                    'в DNS. Приложение Telegram ходит по IP-адресам — для него нужен лист «Telegram: IP-сети».'},
    {'id': 'zaprett-rutracker', 'type': 'list', 'repo_manifest': 'manifests/lists/include/list-rutracker.json',
     'sha256': '82f953b44eda73bf3319c99e8cfab572ce498d68dc7692ad0bcdae3b8256c9ed', 'name': 'RuTracker',
     'author': 'zaprett-devs',
     'description': 'Домены RuTracker ({n} шт.) из списка list-rutracker репозитория CherretGit/zaprett-repo '
                    '(автор zaprett-devs, версия 1.0.0) без изменений.'},
    {'id': 'zaprett-telegram-ipset', 'type': 'ipset', 'curated': 'telegram-ipset.txt',
     'sha256': '53437464eec1ac4396b00e8b6ad427ed0cf54bfac6339575a2cef48d8753e961', 'name': 'Telegram: IP-сети',
     'description': 'IP-сети Telegram ({n} шт., адресов IPv4: {v4}): официальный список '
                    'core.telegram.org/resources/cidr.txt и сети, которые анонсируют автономные системы '
                    'Telegram Messenger Inc (AS62041, AS59930, AS44907, AS211157, AS62014) по данным RIPEstat '
                    'на 2026-09-17. Нужны приложению Telegram, которое соединяется с серверами по IP.'},
    {'id': 'zaprett-exclude', 'type': 'list_exclude', 'curated': 'exclude.txt',
     'sha256': '6bb040ee55b4dd8131c2e0d23b6d48d3558a7dce53621f3219e3c90969eab4fe', 'name': 'Исключения: сайты',
     'description': 'Сайты, которые обход не трогает ({n} доменов): госуслуги и госсайты, банки, Яндекс, VK, '
                    'маркетплейсы, игровые и софтверные сервисы. Объединение списков Flowseal list-exclude, '
                    'CherretGit/zaprett-repo list-exclude-general (плюс gosuslugi.ru и x5.ru) и remittor '
                    'zapret-hosts-user-exclude; домены, которых нет в DNS, убраны.'},
    {'id': 'zaprett-exclude-ipset', 'type': 'ipset_exclude', 'curated': 'exclude-ipset.txt',
     'sha256': '803d59b62d125220dfff3b5da7fac0c2b0f01ccb5a537bbe28f547c47703d53b',
     'name': 'Исключения: локальные сети',
     'description': 'Локальные и служебные сети ({n} шт.), которые обход не трогает: частные сети, loopback, '
                    'link-local, CGNAT, multicast и их IPv6-аналоги. Основа — список исключений bol-van/zapret '
                    'и ipset-exclude из Flowseal/zapret-discord-youtube.'},
]


class SourceError(Exception):
    """Входные данные не совпали с зафиксированными."""


def write_bytes(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    tmp = path + '.tmp'
    with open(tmp, 'wb') as f:
        f.write(data)
    os.replace(tmp, path)


def json_bytes(obj):
    return (json.dumps(obj, ensure_ascii=False, indent=2) + '\n').encode('utf-8')


def local_from_raw(zrepo_dir, url):
    if not url.startswith(ZREPO_RAW):
        raise SourceError('URL вне zaprett-repo: %s' % url)
    parts = url[len(ZREPO_RAW):].split('/')
    if any(p in ('', '.', '..') for p in parts):
        raise SourceError('недопустимый путь в URL: %s' % url)
    return os.path.join(zrepo_dir, *parts)


# ---------------------------------------------------------------- входные данные

def load_list_items(curated_dir, zrepo_dir):
    out = []
    for spec in LIST_ITEMS:
        if 'curated' in spec:
            path = os.path.join(curated_dir, spec['curated'])
        else:
            m = json.loads(bc.read_bytes(os.path.join(zrepo_dir, *spec['repo_manifest'].split('/'))).decode('utf-8'))
            path = local_from_raw(zrepo_dir, m['artifact']['url'])
            if m['artifact']['sha256'] != spec['sha256']:
                raise SourceError('%s: sha256 в манифесте zaprett-repo изменился' % spec['id'])
        data = bc.read_bytes(path)
        if bc.sha256_bytes(data) != spec['sha256']:
            raise SourceError('%s: sha256 файла %s не совпадает с зафиксированным' % (spec['id'], path))
        text = data.decode('utf-8')
        lines = [x for x in text.split('\n') if x]
        v4 = 0
        if spec['type'] in bc.IPSET_TYPES:
            v4 = sum(ipaddress.ip_network(x).num_addresses for x in lines if ':' not in x)
        out.append({
            'id': spec['id'], 'type': spec['type'], 'name': spec['name'], 'version': SNAPSHOT_VERSION,
            'author': spec.get('author', LIST_AUTHOR),
            'description': spec['description'].format(n=len(lines), v4='{:,}'.format(v4).replace(',', ' ')),
            'dependencies': [], 'data': data, 'fname': spec['id'] + '.txt', 'manifest_url': None,
        })
    return out


def load_repo_items(zrepo_dir, index_sha256=ZREPO_INDEX_SHA256):
    index_bytes = bc.read_bytes(os.path.join(zrepo_dir, 'index.json'))
    if bc.sha256_bytes(index_bytes) != index_sha256:
        raise SourceError('index.json zaprett-repo изменился: обновите ZREPO_INDEX_SHA256 после ревизии')
    idx = json.loads(index_bytes.decode('utf-8'))
    by_url = {it['manifest']: it for it in idx['items']}
    skipped = {}
    out = []
    for it in idx['items']:
        if it['type'] not in COPY_TYPES:
            skipped[it['type']] = skipped.get(it['type'], 0) + 1
            continue
        rel_dir = bc.TYPE_DIRS[it['type']]
        if it['manifest'] != '%smanifests/%s/%s.json' % (ZREPO_RAW, rel_dir, it['id']):
            raise SourceError('%s: манифест не в manifests/%s' % (it['id'], rel_dir))
        m = json.loads(bc.read_bytes(local_from_raw(zrepo_dir, it['manifest'])).decode('utf-8'))
        if m.get('schema') != 1 or m.get('id') != it['id']:
            raise SourceError('%s: schema/id манифеста не совпадают с index.json' % it['id'])
        fname = it['id'] + ARTIFACT_EXT[it['type']]
        if m['artifact']['url'] != '%sfiles/%s/%s' % (ZREPO_RAW, rel_dir, fname):
            raise SourceError('%s: артефакт не files/%s/%s' % (it['id'], rel_dir, fname))
        data = bc.read_bytes(local_from_raw(zrepo_dir, m['artifact']['url']))
        if bc.sha256_bytes(data) != m['artifact']['sha256']:
            raise SourceError('%s: sha256 файла не совпадает с манифестом zaprett-repo' % it['id'])
        deps = []
        for url in m['dependencies']:
            dep = by_url.get(url)
            if dep is None:
                raise SourceError('%s: зависимость %s не найдена в index.json' % (it['id'], url))
            if dep['type'] not in COPY_TYPES:
                raise SourceError('%s: зависимость %s типа %s в bundle не входит' % (it['id'], dep['id'], dep['type']))
            deps.append(dep['id'])
        out.append({
            'id': it['id'], 'type': it['type'], 'name': m['name'], 'version': m['version'], 'author': m['author'],
            'description': m['description'], 'dependencies': deps, 'data': data, 'fname': fname,
            'manifest_url': it['manifest'],
        })
    return out, skipped


# ---------------------------------------------------------------- сборка

def make_manifest(item):
    rel_dir = bc.TYPE_DIRS[item['type']]
    values = {
        'schema': 1, 'id': item['id'], 'type': item['type'], 'name': item['name'], 'version': item['version'],
        'author': item['author'], 'description': item['description'], 'dependencies': item['dependencies'],
        'file': '%s/files/%s/%s' % (bc.ROUTER_BUNDLE_ROOT, rel_dir, item['fname']), 'source': 'bundle',
        'sha256': bc.sha256_bytes(item['data']), 'installed_at': SNAPSHOT_EPOCH, 'manifest_url': item['manifest_url'],
    }
    return {k: values[k] for k in bc.MANIFEST_KEYS}


def stage_bundle(bundle_dir, items):
    ids = [x['id'] for x in items]
    dups = sorted({x for x in ids if ids.count(x) > 1})
    if dups:
        raise SourceError('повторяющиеся id: %s' % dups)
    index_items = []
    for item in items:
        rel_dir = bc.TYPE_DIRS[item['type']]
        write_bytes(os.path.join(bundle_dir, 'files', *rel_dir.split('/'), item['fname']), item['data'])
        write_bytes(os.path.join(bundle_dir, 'manifests', *rel_dir.split('/'), item['id'] + '.json'),
                    json_bytes(make_manifest(item)))
        index_items.append({'id': item['id'], 'type': item['type'],
                            'manifest': '%s/manifests/%s/%s.json' % (bc.ROUTER_BUNDLE_ROOT, rel_dir, item['id'])})
    index = {'schema': 1, 'snapshot': SNAPSHOT_VERSION, 'zaprett_repo_commit': ZREPO_COMMIT, 'items': index_items}
    write_bytes(os.path.join(bundle_dir, 'index.json'), json_bytes(index))


def tree_digest(root):
    h = hashlib.sha256()
    for rel in bc._walk_files(root):
        h.update(rel.encode('utf-8') + bytes([0]))
        h.update(bc.sha256_bytes(bc.read_bytes(os.path.join(root, *rel.split('/')))).encode('ascii'))
    return h.hexdigest()


def archive_sizes(root):
    """Оценка места: детерминированный tar, сжатый gzip -9 и xz (squashfs использует xz)."""
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode='w', format=tarfile.USTAR_FORMAT) as tar:
        for rel in bc._walk_files(root):
            data = bc.read_bytes(os.path.join(root, *rel.split('/')))
            info = tarfile.TarInfo(rel)
            info.size, info.mtime, info.mode = len(data), 0, 0o644
            tar.addfile(info, io.BytesIO(data))
    raw = buf.getvalue()
    return len(raw), len(gzip.compress(raw, 9, mtime=0)), len(lzma.compress(raw, preset=9 | lzma.PRESET_EXTREME))


# ---------------------------------------------------------------- проверка и отчёт

def validate_all(bundle_dir, presets_bytes):
    rep = bc.Report()
    grammar = bc.load_nfqws_grammar(NFQ_DIR)
    items, content, audits = bc.validate_bundle(bundle_dir, grammar, rep)
    try:
        presets = json.loads(presets_bytes.decode('utf-8'))
    except (UnicodeDecodeError, ValueError) as exc:
        rep.error('E_PRESET_JSON', 'presets', str(exc))
        presets = None
    bc.check_text_bytes(presets_bytes, 'presets.json', rep)
    if presets is not None:
        bc.validate_presets(presets, items, audits, presets_data.REFERENCE_SIZES, rep)
    return rep, items, content, audits


def print_summary(bundle_dir, items, content, audits, rep):
    print('== Состав bundle')
    by_type = {}
    for m in items.values():
        by_type.setdefault(m['type'], []).append(m)
    for t in sorted(by_type):
        size = sum(os.path.getsize(m['_local']) for m in by_type[t])
        print('  %-14s элементов %3d, файлы %7d байт' % (t, len(by_type[t]), size))
    for sid in sorted(content):
        print('  %-24s записей %4d' % (sid, len(content[sid])))
    files = bc._walk_files(bundle_dir)
    total = sum(os.path.getsize(os.path.join(bundle_dir, *f.split('/'))) for f in files)
    tar_raw, tar_gz, tar_xz = archive_sizes(bundle_dir)
    print('  всего файлов %d, %d байт; tar %d, tar.gz -9 %d, tar.xz -9e %d байт' % (len(files), total, tar_raw, tar_gz, tar_xz))
    print('  дайджест дерева: %s' % tree_digest(bundle_dir))

    print('== Стратегии nfqws: особенности (нормализует генератор аргументов бэкенда, §5)')
    groups = [
        ('устаревшие режимы', lambda a: ','.join('%s->%s' % kv for kv in sorted(a['deprecated_modes'].items()))),
        ('--comment без =', lambda a: a['comment_blocks'] and '%d блоков, %d слов' % (a['comment_blocks'], a['comment_junk_tokens'])),
        ('висячие \\ в концах строк', lambda a: a['trailing_backslashes']),
        ('завершающий --new', lambda a: a['trailing_new']),
        ('профили на весь трафик порта', lambda a: '; '.join(a['profiles_all_traffic'])),
        ('профили только по протоколу (l7)', lambda a: '; '.join(a['profiles_l7_only'])),
        ('широкие UDP-диапазоны', lambda a: '; '.join(a['wide_udp'])),
        ('syndata (нужен SYN в очереди)', lambda a: a['syndata']),
        ('autottl (нужны входящие пакеты)', lambda a: a['autottl']),
        ('any-protocol без cutoff', lambda a: a['any_protocol_no_cutoff']),
    ]
    for title, fn in groups:
        hits = [(sid, fn(a)) for sid, a in sorted(audits.items()) if fn(a)]
        print('  %s: %d' % (title, len(hits)))
        flags = [sid for sid, val in hits if val is True]
        if flags:
            print('    ' + ', '.join(flags))
        for sid, val in hits:
            if val is not True:
                print('    %s: %s' % (sid, val))
    placeholders = {}
    for a in audits.values():
        for p in a['placeholders']:
            placeholders[p] = placeholders.get(p, 0) + 1
    print('  плейсхолдеры (стратегий): %s' % ', '.join('%s=%d' % kv for kv in sorted(placeholders.items())))

    for code, where, msg in rep.warnings:
        print('  ПРЕДУПРЕЖДЕНИЕ %s %s: %s' % (code, where, msg))
    if rep.errors:
        print('== ОШИБКИ: %d' % len(rep.errors))
        for code, where, msg in rep.errors:
            print('  %s %s: %s' % (code, where, msg))
    else:
        print('== Проверки: ошибок 0, предупреждений %d' % len(rep.warnings))


def write_audit(audits):
    data = {'schema': 1, 'snapshot': SNAPSHOT_VERSION, 'strategies': [audits[k] for k in sorted(audits)]}
    write_bytes(AUDIT_PATH, json_bytes(data))


def build():
    list_items = load_list_items(CURATED_DIR, ZREPO_DIR)
    repo_items, skipped = load_repo_items(ZREPO_DIR)
    print('zaprett-repo: взято nfqws/bin %d, пропущено по типам %s' % (len(repo_items), skipped))
    staging_bundle = os.path.join(STAGING_DIR, 'bundle')
    if os.path.isdir(STAGING_DIR):
        shutil.rmtree(STAGING_DIR)
    stage_bundle(staging_bundle, list_items + repo_items)
    presets_bytes = json_bytes(presets_data.PRESETS)
    write_bytes(os.path.join(STAGING_DIR, 'presets.json'), presets_bytes)

    rep, items, content, audits = validate_all(staging_bundle, presets_bytes)
    print_summary(staging_bundle, items, content, audits, rep)
    if rep.errors:
        print('Сборка остановлена: пакет не изменён.')
        return 1

    if not BUNDLE_DIR.replace(os.sep, '/').endswith('packages/zaprett/files/usr/share/zaprett/bundle'):
        raise SourceError('неожиданный путь bundle: %s' % BUNDLE_DIR)
    if os.path.isdir(BUNDLE_DIR):
        shutil.rmtree(BUNDLE_DIR)
    shutil.copytree(staging_bundle, BUNDLE_DIR)
    write_bytes(PRESETS_PATH, presets_bytes)
    write_audit(audits)

    # повторная проверка уже установленных файлов и сверка с промежуточной сборкой
    rep2, _items, _content, _audits = validate_all(BUNDLE_DIR, bc.read_bytes(PRESETS_PATH))
    same_tree = tree_digest(BUNDLE_DIR) == tree_digest(staging_bundle)
    same_presets = bc.read_bytes(PRESETS_PATH) == presets_bytes
    print('== Установлено в пакет: ошибок %d, дерево совпадает %s, presets совпадает %s'
          % (len(rep2.errors), same_tree, same_presets))
    if rep2.errors or not same_tree or not same_presets:
        return 1
    shutil.rmtree(STAGING_DIR)
    return 0


def check_only():
    rep, items, content, audits = validate_all(BUNDLE_DIR, bc.read_bytes(PRESETS_PATH))
    print_summary(BUNDLE_DIR, items, content, audits, rep)
    return 1 if rep.errors else 0


def main():
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    parser = argparse.ArgumentParser(description='Сборка bundle и presets.json для zaprett (OpenWrt)')
    parser.add_argument('--check-only', action='store_true', help='только проверить то, что лежит в пакете')
    args = parser.parse_args()
    try:
        return check_only() if args.check_only else build()
    except SourceError as exc:
        print('ОШИБКА ВХОДНЫХ ДАННЫХ: %s' % exc)
        return 2


if __name__ == '__main__':
    sys.exit(main())
