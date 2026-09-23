"""Снимки ответов источников для генератора списков (tools/lists/generate.py).

Без --refresh генератор работает только по снимкам из snapshots/ — сборка без сети даёт тот же результат.
С --refresh ответы запрашиваются заново (с паузами между запросами) и снимки перезаписываются.
Сохраняется не сырой ответ, а то, что из него нужно генератору (набор имён сертификата, коды DNS, сети),
в отсортированном виде — чтобы снимок был стабильным и читался в diff.
"""
import datetime
import ipaddress
import json
import os
import random
import re
import string
import time
import urllib.error
import urllib.parse
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
SNAP_DIR = os.path.join(HERE, 'snapshots')
UA = 'zaprett-openwrt-lists/1.0 (+https://github.com/RomanKern89/zaprett-openwrt)'
BROWSER_UA = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0 Safari/537.36'
# publicsuffix.org из этой сети рвёт TLS; тот же файл из репозитория проекта PSL
PSL_URL = 'https://raw.githubusercontent.com/publicsuffix/list/main/public_suffix_list.dat'
DOH = {
    'google': 'https://dns.google/resolve?name=%s&type=A',
    'cloudflare': 'https://cloudflare-dns.com/dns-query?name=%s&type=A',
}
NXDOMAIN, NOERROR = 3, 0
# перед именем не должно быть % и обратной косой черты: «%cyoutube.com» (формат console.log) и «%2Fwww…»
# (URL-кодирование) — не имена хостов
HOST_RE = re.compile(r'(?<![a-z0-9.%\\-])(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z][a-z0-9-]{1,62}(?![a-z0-9-])')


class SnapshotError(Exception):
    """Нужного снимка нет, или он противоречит контролям."""


def _write(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    tmp = path + '.tmp'
    with open(tmp, 'wb') as f:
        f.write(data)
    os.replace(tmp, path)


def _read(path):
    with open(path, 'rb') as f:
        return f.read()


def dump_json(obj):
    return (json.dumps(obj, ensure_ascii=False, indent=1, sort_keys=True) + '\n').encode('utf-8')


def _http(url, headers=None, timeout=60, tries=3):
    req = urllib.request.Request(url, headers=dict({'User-Agent': UA}, **(headers or {})))
    for attempt in range(tries):
        try:
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                return resp.status, resp.read()
        except urllib.error.HTTPError:
            raise
        except (urllib.error.URLError, OSError):
            if attempt + 1 == tries:
                raise
            time.sleep(5)


class Store:
    """Снимки на диске. refresh=True — запрашивать источники и перезаписывать снимки."""

    def __init__(self, root=SNAP_DIR, refresh=False, today=None, missing_only=False):
        self.root = root
        self.refresh = refresh
        self.missing_only = missing_only     # дозапросить только отсутствующие снимки (продолжение после сбоя)
        self.today = today or datetime.date.today().isoformat()
        self.dns = None
        self.fetched = {}

    def path(self, *parts):
        return os.path.join(self.root, *parts)

    def _fetch(self, *parts):
        """Нужно ли запрашивать источник для этого снимка."""
        return self.refresh and not (self.missing_only and os.path.isfile(self.path(*parts)))

    def _load(self, *parts):
        p = self.path(*parts)
        if not os.path.isfile(p):
            raise SnapshotError('нет снимка %s — запустите generate.py --refresh' % '/'.join(parts))
        return _read(p)

    # ------------------------------------------------------------ дата снимка
    def meta(self):
        if self.refresh:
            m = {'fetched': self.today}
            _write(self.path('meta.json'), dump_json(m))
            return m
        return json.loads(self._load('meta.json').decode('utf-8'))

    # ------------------------------------------------------------ Public Suffix List
    def psl(self):
        if self._fetch('public_suffix_list.dat'):
            _st, body = _http(PSL_URL, timeout=120)
            _write(self.path('public_suffix_list.dat'), body.replace(bytes([13]), b''))
        return self._load('public_suffix_list.dat').decode('utf-8')

    # ------------------------------------------------------------ Certificate Transparency
    def ct(self, anchor, window_days):
        rel = ('ct', anchor + '.json')
        if not self._fetch(*rel):
            return json.loads(self._load(*rel).decode('utf-8'))
        since = (datetime.date.fromisoformat(self.today) - datetime.timedelta(days=window_days)).isoformat()
        sets, info = set(), {}
        # crt.sh: все сертификаты с именем домена (и поддоменов); берём действовавшие после since
        for attempt in range(3):
            try:
                _st, body = _http('https://crt.sh/?q=%s&output=json' % urllib.parse.quote(anchor), timeout=240)
                rows = json.loads(body.decode('utf-8'))
                n = 0
                for r in rows:
                    if r.get('not_after', '')[:10] >= since:
                        sets.add(tuple(sorted({x.strip().lower() for x in r['name_value'].split('\n') if x.strip()})))
                        n += 1
                info['crt.sh'] = {'ok': True, 'certs': n}
                break
            except (urllib.error.URLError, ValueError, OSError) as exc:
                info['crt.sh'] = {'ok': False, 'error': str(exc)[:200]}
                time.sleep(30)
        time.sleep(3)
        # certspotter: только действующие сертификаты, постранично
        after, pages, n = None, 0, 0
        try:
            while pages < 5:
                url = ('https://api.certspotter.com/v1/issuances?domain=%s&include_subdomains=true&expand=dns_names'
                       % urllib.parse.quote(anchor)) + ('&after=%s' % after if after else '')
                _st, body = _http(url, timeout=120)
                rows = json.loads(body.decode('utf-8'))
                pages += 1
                if not rows:
                    break
                for r in rows:
                    sets.add(tuple(sorted({x.lower() for x in r['dns_names']})))
                    n += 1
                after = rows[-1]['id']
                time.sleep(2)
            info['certspotter'] = {'ok': True, 'certs': n, 'pages': pages}
        except (urllib.error.URLError, ValueError, OSError) as exc:
            info['certspotter'] = {'ok': n > 0, 'certs': n, 'pages': pages, 'error': str(exc)[:200]}
        if not any(v['ok'] for v in info.values()):
            if os.path.isfile(self.path(*rel)):
                print('ПРЕДУПРЕЖДЕНИЕ: CT для %s не ответил (%s), оставлен прежний снимок' % (anchor, info))
                return json.loads(self._load(*rel).decode('utf-8'))
            raise SnapshotError('CT: ни один источник не ответил для %s: %s' % (anchor, info))
        snap = {'anchor': anchor, 'fetched': self.today, 'since': since, 'sources': info,
                'san_sets': sorted(list(s) for s in sets)}
        _write(self.path(*rel), dump_json(snap))
        return snap

    # ------------------------------------------------------------ имена в коде страниц сервиса
    def page(self, url):
        slug = re.sub(r'[^a-z0-9]+', '_', url.lower().split('://', 1)[1]).strip('_')
        rel = ('pages', slug + '.json')
        if not self._fetch(*rel):
            return json.loads(self._load(*rel).decode('utf-8'))
        try:
            status, body = _http(url, headers={'User-Agent': BROWSER_UA, 'Accept-Language': 'en'}, timeout=60)
            text = body.decode('utf-8', 'replace').lower()
            text = text.replace('\\u002f', '/').replace('\\x2f', '/').replace('\\/', '/')
            hosts = sorted(set(HOST_RE.findall(text)))
            snap = {'url': url, 'fetched': self.today, 'status': status, 'bytes': len(body), 'hosts': hosts}
        except (urllib.error.URLError, OSError) as exc:
            snap = {'url': url, 'fetched': self.today, 'status': None, 'error': str(exc)[:200], 'hosts': []}
        time.sleep(2)
        _write(self.path(*rel), dump_json(snap))
        return snap

    # ------------------------------------------------------------ реальная сессия браузера
    def session(self, service, name, url, keywords, keep):
        """Сценарий в настоящем браузере (Chromium, Playwright): имена хостов всех запросов страницы и имена
        хостов в коде JS, загруженного с доменов сервиса. keep(host) — фильтр имён (известный публичный суффикс).
        Сохраняются только имена хостов, сценарий и дата."""
        rel = ('sessions', '%s__%s.json' % (service, name))
        if not self._fetch(*rel):
            return json.loads(self._load(*rel).decode('utf-8'))
        from playwright.sync_api import sync_playwright   # нужен только для --refresh
        requested, code, scripts, err = set(), set(), 0, None
        with sync_playwright() as p:
            browser = p.chromium.launch(headless=True)
            try:
                page = browser.new_page(user_agent=BROWSER_UA, locale='ru-RU')
                page.on('request', lambda r: requested.add(r.url.split('/')[2].split(':')[0].lower()))
                bodies = []

                def on_response(resp):
                    host = resp.url.split('/')[2].split(':')[0].lower()
                    if resp.request.resource_type == 'script' and any(k in host for k in keywords):
                        bodies.append(resp)
                page.on('response', on_response)
                try:
                    page.goto(url, wait_until='load', timeout=90000)
                    page.wait_for_timeout(10000)
                except Exception as exc:          # таймаут или сетевая ошибка: берём то, что успело загрузиться
                    err = str(exc).split('\n')[0][:200]
                for resp in bodies:
                    try:
                        text = resp.body().decode('utf-8', 'replace').lower()
                    except Exception:
                        continue
                    scripts += 1
                    text = text.replace('\\u002f', '/').replace('\\x2f', '/').replace('\\/', '/')
                    code.update(HOST_RE.findall(text))
            finally:
                browser.close()
        snap = {'service': service, 'scenario': name, 'url': url, 'fetched': self.today, 'scripts_scanned': scripts,
                'requested_hosts': sorted(h for h in requested if keep(h)),
                'code_hosts': sorted(h for h in code if keep(h))}
        if err:
            snap['error'] = err
        _write(self.path(*rel), dump_json(snap))
        time.sleep(2)
        return snap

    # ------------------------------------------------------------ RIPEstat
    def ripe_as(self, asn):
        rel = ('ripe', 'AS%d.json' % asn)
        if not self._fetch(*rel):
            return json.loads(self._load(*rel).decode('utf-8'))
        _st, body = _http('https://stat.ripe.net/data/as-overview/data.json?resource=AS%d' % asn)
        holder = json.loads(body.decode('utf-8'))['data']['holder']
        time.sleep(1)
        _st, body = _http('https://stat.ripe.net/data/announced-prefixes/data.json?resource=AS%d' % asn, timeout=120)
        data = json.loads(body.decode('utf-8'))['data']
        end = data['query_endtime']
        end_dt = datetime.datetime.fromisoformat(end)
        current, stale = set(), set()
        for p in data['prefixes']:
            last = max(datetime.datetime.fromisoformat(t['endtime']) for t in p['timelines'])
            (current if end_dt - last <= datetime.timedelta(hours=12) else stale).add(p['prefix'])
        key = lambda s: (ipaddress.ip_network(s).version, ipaddress.ip_network(s))
        snap = {'asn': asn, 'holder': holder, 'fetched': self.today, 'query_starttime': data['query_starttime'],
                'query_endtime': end, 'prefixes': sorted(current, key=key), 'stale_prefixes': sorted(stale, key=key)}
        time.sleep(1)
        _write(self.path(*rel), dump_json(snap))
        return snap

    # ------------------------------------------------------------ официальные списки и RDAP
    def official(self, key, url):
        rel = ('official', key + '.txt')
        if self._fetch(*rel):
            _st, body = _http(url)
            _write(self.path(*rel), body.replace(bytes([13]), b''))
            time.sleep(1)
        return self._load(*rel).decode('utf-8')

    def rdap(self, key, url):
        rel = ('rdap', key + '.json')
        if self._fetch(*rel):
            _st, body = _http(url, headers={'Accept': 'application/rdap+json'})
            d = json.loads(body.decode('utf-8'))
            orgs = []
            for e in d.get('entities', []):
                for field in (e.get('vcardArray') or [None, []])[1]:
                    if field[0] == 'fn':
                        orgs.append('%s: %s' % (','.join(e.get('roles', [])), field[3]))
            snap = {'url': url, 'fetched': self.today, 'handle': d.get('handle'), 'name': d.get('name'),
                    'start': d.get('startAddress'), 'end': d.get('endAddress'), 'entities': sorted(orgs)}
            _write(self.path(*rel), dump_json(snap))
            time.sleep(1)
        return json.loads(self._load(*rel).decode('utf-8'))

    # ------------------------------------------------------------ DNS через два DoH
    def _dns_file(self):
        if self.dns is None:
            p = self.path('dns.json')
            keep = os.path.isfile(p) and (not self.refresh or self.missing_only)
            self.dns = json.loads(_read(p).decode('utf-8')) if keep else \
                {'fetched': self.today, 'controls': {}, 'names': {}}
        return self.dns

    @staticmethod
    def _doh(which, name):
        url = DOH[which] % urllib.parse.quote(name)
        for attempt in range(3):
            try:
                _st, body = _http(url, headers={'Accept': 'application/dns-json'}, timeout=30)
                d = json.loads(body.decode('utf-8'))
                ips = sorted({a['data'] for a in d.get('Answer', []) if a.get('type') == 1})
                return [d['Status'], ips]
            except (urllib.error.URLError, ValueError, OSError, KeyError):
                time.sleep(2)
        return [None, []]

    def dns_controls(self):
        """Положительный (google.com) и отрицательный (случайное имя) контроли резолверов."""
        d = self._dns_file()
        if self.refresh and not d['controls']:
            bad = 'zaprett-nx-' + ''.join(random.choice(string.ascii_lowercase + string.digits) for _ in range(20)) + '.com'
            d['controls'] = {'positive': {'name': 'google.com'}, 'negative': {'name': bad}}
            for kind in ('positive', 'negative'):
                for which in DOH:
                    d['controls'][kind][which] = self._doh(which, d['controls'][kind]['name'])
        c = d.get('controls') or {}
        if not c:
            raise SnapshotError('в снимке DNS нет контролей')
        for which in DOH:
            if c['positive'][which][0] != NOERROR or not c['positive'][which][1]:
                raise SnapshotError('DoH %s: положительный контроль google.com не прошёл: %s' % (which, c['positive'][which]))
            if c['negative'][which][0] != NXDOMAIN:
                raise SnapshotError('DoH %s: несуществующее имя %s не дало NXDOMAIN: %s'
                                    % (which, c['negative']['name'], c['negative'][which]))
        return c

    def resolve(self, name):
        d = self._dns_file()
        self.fetched[name] = True               # имена, которые понадобились этой сборке
        if name not in d['names']:
            if not self.refresh:
                raise SnapshotError('нет снимка DNS для %s — запустите generate.py --refresh' % name)
            d['names'][name] = {which: self._doh(which, name) for which in DOH}
            time.sleep(0.2)
        return d['names'][name]

    def save_dns(self):
        if self.refresh and self.dns is not None:
            # в снимке остаются только имена, которые понадобились этой сборке
            self.dns['names'] = {k: v for k, v in self.dns['names'].items() if k in self.fetched}
            _write(self.path('dns.json'), dump_json(self.dns))
