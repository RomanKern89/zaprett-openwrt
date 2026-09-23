"""Контроли генератора собственных списков (tools/lists/generate.py).

Запуск:  PYTHONUTF8=1 python tools/lists/test_generate.py

Все тесты работают без сети: на копии снимков из snapshots/. Каждый отрицательный контроль портит копию
ровно в одном месте и требует конкретного исхода (ошибка, исключение записи с причиной в журнале).
"""
import copy
import ipaddress
import json
import os
import shutil
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.dont_write_bytecode = True
import generate as g  # noqa: E402
import seeds  # noqa: E402
import sources  # noqa: E402


class Case(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix='zaprett-lists-test-')
        self.snap = os.path.join(self.tmp, 'snapshots')
        shutil.copytree(sources.SNAP_DIR, self.snap)
        self.out = os.path.join(self.tmp, 'out')
        self._services = copy.deepcopy(seeds.SERVICES)
        self._ipsets = copy.deepcopy(seeds.IPSETS)

    def tearDown(self):
        seeds.SERVICES[:] = self._services
        seeds.IPSETS[:] = self._ipsets
        shutil.rmtree(self.tmp, ignore_errors=True)

    # -- помощники
    def run_gen(self):
        return g.generate(self.out, sources.Store(root=self.snap))

    def lines_after_run(self, list_id):
        self.run_gen()
        return self.lines(list_id)

    def lines(self, list_id):
        with open(os.path.join(self.out, list_id + '.txt'), 'rb') as f:
            return f.read().decode('utf-8').split('\n')[:-1]

    def log(self):
        with open(os.path.join(self.out, g.LOG_NAME), 'rb') as f:
            return [r.split('\t') for r in f.read().decode('utf-8').split('\n')[1:] if r]

    def edit_json(self, rel, fn):
        path = os.path.join(self.snap, *rel.split('/'))
        with open(path, 'rb') as f:
            d = json.loads(f.read().decode('utf-8'))
        fn(d)
        sources._write(path, sources.dump_json(d))

    def set_dns(self, name, google, cloudflare, ips=('203.0.113.9',)):
        def fn(d):
            d['names'][name] = {'google': [google, list(ips) if google == 0 else []],
                                'cloudflare': [cloudflare, list(ips) if cloudflare == 0 else []]}
        self.edit_json('dns.json', fn)

    def service(self, sid):
        return next(s for s in seeds.SERVICES if s['id'] == sid)


class Positive(Case):
    def test_offline_build_equals_committed_out(self):
        self.run_gen()
        self.assertEqual(g.tree_bytes(self.out), g.tree_bytes(g.OUT_DIR))

    def test_two_offline_builds_identical(self):
        self.run_gen()
        first = g.tree_bytes(self.out)
        self.run_gen()
        self.assertEqual(first, g.tree_bytes(self.out))

    def test_output_format(self):
        index = self.run_gen()
        for e in index['lists']:
            with open(os.path.join(self.out, e['file']), 'rb') as f:
                data = f.read()
            self.assertEqual(data.count(bytes([13])), 0, e['id'])
            self.assertTrue(data.endswith(b'\n'), e['id'])
            lines = data.decode('utf-8').split('\n')[:-1]
            self.assertEqual(len(lines), e['entries'])
            self.assertEqual(len(lines), len(set(lines)), e['id'])
            for x in lines:
                if e['type'] == 'list':
                    self.assertTrue(g.valid_domain(x), (e['id'], x))
                    self.assertNotIn('*', x)
                else:
                    net = ipaddress.ip_network(x, strict=True)
                    self.assertEqual(g.special_overlap(net), [], (e['id'], x))
            if e['type'] == 'list':
                kept, covered = g.fold(lines)
                self.assertEqual((kept, covered), (sorted(lines), {}), e['id'])

    def test_core_subset_of_full(self):
        self.run_gen()
        for svc in seeds.SERVICES:
            if not svc['lists'].get('full'):
                continue
            core = set(self.lines(svc['lists']['core']))
            full = self.lines(svc['lists']['full'])
            covered = {c for c in core if any(g.is_under(c, f) for f in full)}
            self.assertEqual(core, covered, svc['id'])

    def test_core_lists_light(self):
        index = self.run_gen()
        for e in index['lists']:
            limit = 50 if e['variant'] == 'core' else 500
            self.assertLessEqual(e['entries'], limit, e['id'])


class Helpers(unittest.TestCase):
    def test_psl(self):
        with open(os.path.join(sources.SNAP_DIR, 'public_suffix_list.dat'), 'rb') as f:
            psl = g.Psl(f.read().decode('utf-8'))
        self.assertEqual(psl.registrable('a.b.example.co.uk'), 'example.co.uk')
        self.assertEqual(psl.registrable('www.youtube.com'), 'youtube.com')
        self.assertIsNone(psl.registrable('discordsays.com'))       # публичный суффикс (частный раздел PSL)
        self.assertTrue(psl.is_suffix('discordsays.com'))
        self.assertFalse(psl.known_suffix('window.addeventlistener'))

    def test_fold(self):
        self.assertEqual(g.fold(['a.x.com', 'x.com', 'b.a.x.com', 'y.com']),
                         (['x.com', 'y.com'], {'a.x.com': 'x.com', 'b.a.x.com': 'x.com'}))
        self.assertEqual(g.fold(['notx.com', 'x.com'])[0], ['notx.com', 'x.com'])

    def test_valid_domain(self):
        for bad in ('*.x.com', 'x', '-a.com', 'a..com', 'a.c0m1', 'A.com'.lower() + ' '):
            self.assertFalse(g.valid_domain(bad), bad)
        self.assertTrue(g.valid_domain('xn--e1afmkfd.com'))


class Negative(Case):
    def test_missing_snapshot(self):
        os.remove(os.path.join(self.snap, 'ct', 'signal.org.json'))
        with self.assertRaises(sources.SnapshotError):
            self.run_gen()

    def test_dns_negative_control_broken(self):
        self.edit_json('dns.json', lambda d: d['controls']['negative']['google'].__setitem__(0, 0))
        with self.assertRaises(sources.SnapshotError):
            self.run_gen()

    def test_dns_positive_control_broken(self):
        self.edit_json('dns.json', lambda d: d['controls']['positive']['cloudflare'].__setitem__(0, 2))
        with self.assertRaises(sources.SnapshotError):
            self.run_gen()

    def test_as_holder_mismatch(self):
        self.edit_json('ripe/AS22697.json', lambda d: d.update(holder='EVIL-NET Some Other Company'))
        with self.assertRaises(g.GenError):
            self.run_gen()

    def test_rdap_owner_mismatch(self):
        self.edit_json('rdap/discord-66.22.192.0.json', lambda d: d.update(entities=['registrant: Other Ltd']))
        with self.assertRaises(g.GenError):
            self.run_gen()

    def test_special_prefix_dropped(self):
        self.edit_json('ripe/AS22697.json', lambda d: d['prefixes'].append('10.0.0.0/8'))
        self.run_gen()
        self.assertNotIn('10.0.0.0/8', self.lines('zaprett-roblox-ipset'))
        rows = [r for r in self.log() if r[1] == '10.0.0.0/8']
        self.assertTrue(rows and rows[0][2] == 'excluded', rows)

    def test_prefix_outside_owned_block_dropped(self):
        self.edit_json('ripe/AS49544.json', lambda d: d['prefixes'].append('185.179.200.0/22'))
        self.run_gen()
        self.assertFalse(any(ipaddress.ip_network(x).overlaps(ipaddress.ip_network('185.179.200.0/22'))
                             for x in self.lines('zaprett-discord-voice')))

    def test_nxdomain_excluded(self):
        self.set_dns('rbxcdn.com', 3, 3)
        self.run_gen()
        self.assertNotIn('rbxcdn.com', self.lines('zaprett-roblox'))
        self.assertIn(['roblox', 'rbxcdn.com', 'excluded'], [r[:3] for r in self.log()])

    def test_dns_disagreement_excluded(self):
        self.set_dns('signal.me', 0, 2)
        self.run_gen()
        self.assertNotIn('signal.me', self.lines('zaprett-signal'))

    def test_unconfirmed_seed_excluded(self):
        self.service('signal')['seeds'].append(('signal-unconfirmed-example.com', True, None))
        self.run_gen()
        self.assertNotIn('signal-unconfirmed-example.com', self.lines('zaprett-signal'))
        reasons = [r[3] for r in self.log() if r[1] == 'signal-unconfirmed-example.com']
        self.assertTrue(reasons and reasons[0].startswith('семя не подтверждено'), reasons)

    def test_seed_under_exclusion_dropped(self):
        self.service('signal')['seeds'].append(('online.sberbank.ru', True, 'signal-fw'))
        self.set_dns('online.sberbank.ru', 0, 0)
        self.run_gen()
        self.assertNotIn('online.sberbank.ru', self.lines('zaprett-signal'))
        self.assertTrue(any(r[1] == 'online.sberbank.ru' and 'исключением' in r[3] for r in self.log()))

    def test_ip_evidence_requires_service_networks(self):
        # graph.org подтверждён только тем, что резолвится в сети Telegram: чужой адрес — и он исключается
        self.assertIn('graph.org', self.lines_after_run('zaprett-telegram'))
        self.set_dns('graph.org', 0, 0, ips=('8.8.8.8',))
        self.run_gen()
        self.assertNotIn('graph.org', self.lines('zaprett-telegram'))

    def test_multitenant_certificate_ignored(self):
        foreign = ['roblox.com', 'foreign-one-example.com', 'foreign-two-example.com', 'foreign-three-example.com']
        self.edit_json('ct/roblox.com.json', lambda d: d['san_sets'].append(foreign))
        self.run_gen()              # чужие имена даже не резолвятся: иначе была бы ошибка «нет снимка DNS»
        self.assertFalse([x for x in self.lines('zaprett-roblox-full') if 'foreign' in x])

    def test_own_certificate_name_accepted(self):
        self.edit_json('ct/rutracker.org.json', lambda d: d['san_sets'].append(['rutracker.org', 'newmirror-example.org']))
        with self.assertRaises(sources.SnapshotError):   # кандидат принят и требует проверки DNS
            self.run_gen()
        self.set_dns('newmirror-example.org', 0, 0)
        self.run_gen()
        self.assertIn('newmirror-example.org', self.lines('zaprett-rutracker-full'))
        self.assertNotIn('newmirror-example.org', self.lines('zaprett-rutracker'))

    def test_shared_google_certificate_needs_keyword(self):
        self.edit_json('ct/youtube.com.json', lambda d: d['san_sets'].append(['youtube.com', 'android-example.com']))
        self.run_gen()
        self.assertNotIn('android-example.com', self.lines('zaprett-youtube-full'))

    def test_wildcard_san_becomes_parent(self):
        self.edit_json('ct/rutracker.org.json', lambda d: d['san_sets'].append(['*.rutracker.org', '*.rt-example.org']))
        self.set_dns('rt-example.org', 0, 0)
        self.run_gen()
        full = self.lines('zaprett-rutracker-full')
        self.assertIn('rt-example.org', full)
        self.assertFalse([x for x in full if '*' in x])

    def test_empty_core_is_error(self):
        self.service('signal')['seeds'][:] = [('signal-unconfirmed-example.com', True, None)]
        with self.assertRaises(g.GenError):
            self.run_gen()


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    unittest.main(verbosity=1)
