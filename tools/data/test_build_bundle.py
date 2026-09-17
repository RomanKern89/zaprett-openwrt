"""Положительные и отрицательные контроли генератора bundle (build_bundle.py, bundle_checks.py).

Запуск (после сборки):  PYTHONUTF8=1 python tools/data/test_build_bundle.py

Каждый отрицательный контроль портит КОПИЮ собранного bundle или presets ровно в одном месте и требует
конкретный код ошибки. Для порчи содержимого sha256 в манифесте пересчитывается — иначе сработала бы
сверка хэша, а не проверяемое правило. Пакет при этом не изменяется.
"""
import copy
import json
import os
import shutil
import sys
import tempfile
import unittest

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.dont_write_bytecode = True
import bundle_checks as bc  # noqa: E402
import build_bundle as bb  # noqa: E402
import presets_data  # noqa: E402

GRAMMAR = bc.load_nfqws_grammar(bb.NFQ_DIR)
NL = bytes([10])
CRLF = bytes([13, 10])


class BundleCase(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix='zaprett-bundle-test-')
        self.bundle = os.path.join(self.tmp, 'bundle')
        shutil.copytree(bb.BUNDLE_DIR, self.bundle)
        self.presets = json.loads(bc.read_bytes(bb.PRESETS_PATH).decode('utf-8'))

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    # -- помощники
    def manifest_path(self, item_id):
        for dirpath, _dirs, files in os.walk(os.path.join(self.bundle, 'manifests')):
            if item_id + '.json' in files:
                return os.path.join(dirpath, item_id + '.json')
        raise KeyError(item_id)

    def manifest(self, item_id):
        return json.loads(bc.read_bytes(self.manifest_path(item_id)).decode('utf-8'))

    def save_manifest(self, item_id, m):
        bb.write_bytes(self.manifest_path(item_id), bb.json_bytes(m))

    def file_path(self, item_id):
        return bc.router_to_local(self.bundle, self.manifest(item_id)['file'])

    def content(self, item_id):
        return bc.read_bytes(self.file_path(item_id))

    def rewrite(self, item_id, data, fix_sha=True):
        bb.write_bytes(self.file_path(item_id), data)
        if fix_sha:
            m = self.manifest(item_id)
            m['sha256'] = bc.sha256_bytes(data)
            self.save_manifest(item_id, m)

    def append_line(self, item_id, line):
        self.rewrite(item_id, self.content(item_id) + line.encode('utf-8') + NL)

    def replace_text(self, item_id, old, new):
        data = self.content(item_id)
        self.assertIn(old.encode('utf-8'), data, 'в %s нет фрагмента для порчи' % item_id)
        self.rewrite(item_id, data.replace(old.encode('utf-8'), new.encode('utf-8'), 1))

    def check(self, presets=None):
        rep = bc.Report()
        items, _content, audits = bc.validate_bundle(self.bundle, GRAMMAR, rep)
        bc.validate_presets(self.presets if presets is None else presets, items, audits,
                            presets_data.REFERENCE_SIZES, rep)
        return rep

    def assertCaught(self, code, presets=None):
        rep = self.check(presets)
        self.assertIn(code, rep.codes(), 'не пойман %s; пойманы: %s' % (code, rep.errors[:4]))
        return rep


class PositiveControls(BundleCase):
    def test_pristine_bundle_and_presets_pass(self):
        rep = self.check()
        self.assertEqual(rep.errors, [])

    def test_installed_bundle_equals_fresh_build(self):
        items = bb.load_list_items(bb.CURATED_DIR, bb.ZREPO_DIR) + bb.load_repo_items(bb.ZREPO_DIR)[0]
        a, b = os.path.join(self.tmp, 'a'), os.path.join(self.tmp, 'b')
        bb.stage_bundle(a, items)
        bb.stage_bundle(b, items)
        self.assertEqual(bb.tree_digest(a), bb.tree_digest(b))
        self.assertEqual(bb.tree_digest(a), bb.tree_digest(bb.BUNDLE_DIR))
        self.assertEqual(bb.json_bytes(presets_data.PRESETS), bc.read_bytes(bb.PRESETS_PATH))

    def test_all_output_files_lf_utf8_no_bom(self):
        paths = [bb.PRESETS_PATH] + [os.path.join(bb.BUNDLE_DIR, *r.split('/')) for r in bc._walk_files(bb.BUNDLE_DIR)
                                     if not r.endswith('.bin')]
        for p in paths:
            data = bc.read_bytes(p)
            self.assertEqual(data.count(bytes([13])), 0, p)
            self.assertFalse(data.startswith(bytes([0xEF, 0xBB, 0xBF])), p)
            data.decode('utf-8')


class ListControls(BundleCase):
    def test_crlf(self):
        self.rewrite('zaprett-youtube', self.content('zaprett-youtube').replace(NL, CRLF))
        self.assertCaught('E_CR')

    def test_bom(self):
        self.rewrite('zaprett-discord', bytes([0xEF, 0xBB, 0xBF]) + self.content('zaprett-discord'))
        self.assertCaught('E_BOM')

    def test_nul(self):
        self.append_line('zaprett-discord', 'abc' + chr(0) + '.com')
        self.assertCaught('E_NUL')

    def test_not_utf8(self):
        self.rewrite('zaprett-discord', self.content('zaprett-discord') + bytes([0xFF, 0xFE]) + NL)
        self.assertCaught('E_UTF8')

    def test_no_final_newline(self):
        self.rewrite('zaprett-telegram', self.content('zaprett-telegram')[:-1])
        self.assertCaught('E_NO_EOL')

    def test_empty_line(self):
        self.append_line('zaprett-telegram', '')
        self.assertCaught('E_EMPTY_LINE')

    def test_space_inside_and_trailing(self):
        self.append_line('zaprett-youtube', 'you tube.com')
        self.assertCaught('E_SPACE')
        shutil.rmtree(self.bundle)
        shutil.copytree(bb.BUNDLE_DIR, self.bundle)
        self.append_line('zaprett-youtube', 'example-trailing.com ')
        self.assertCaught('E_SPACE')

    def test_mask(self):
        self.append_line('zaprett-youtube', '*.example.com')
        self.assertCaught('E_MASK')

    def test_invalid_domains(self):
        for bad in ('bad..example.com', '-bad.example.com', 'localhost', 'example.123', 'пример.рф'):
            with self.subTest(bad=bad):
                shutil.rmtree(self.bundle)
                shutil.copytree(bb.BUNDLE_DIR, self.bundle)
                self.append_line('zaprett-rutracker', bad)
                self.assertCaught('E_DOMAIN')

    def test_trailing_dot(self):
        self.append_line('zaprett-rutracker', 'example.com.')
        self.assertCaught('E_TRAILING_DOT')

    def test_uppercase(self):
        self.append_line('zaprett-rutracker', 'Example.com')
        self.assertCaught('E_CASE')

    def test_comment_line(self):
        self.append_line('zaprett-rutracker', '# comment')
        self.assertCaught('E_COMMENT')

    def test_long_line(self):
        self.append_line('zaprett-rutracker', 'a' * 300 + '.com')
        self.assertCaught('E_LONG_LINE')

    def test_duplicate(self):
        self.append_line('zaprett-rutracker', 'rutracker.org')
        self.assertCaught('E_DUP')

    def test_covered_by_parent(self):
        self.append_line('zaprett-youtube', 'm.youtube.com')
        self.assertCaught('E_COVERED')

    def test_include_domain_under_exclude(self):
        self.append_line('zaprett-youtube', 'online.sberbank.ru')
        self.assertCaught('E_INCLUDE_EXCLUDED')

    def test_empty_list(self):
        self.rewrite('zaprett-rutracker', b'')
        self.assertCaught('E_EMPTY_FILE')


class IpsetControls(BundleCase):
    def test_prefix_too_long(self):
        self.append_line('zaprett-telegram-ipset', '1.2.3.4/33')
        self.assertCaught('E_CIDR')

    def test_host_bits(self):
        self.append_line('zaprett-telegram-ipset', '203.0.113.1/24')
        self.assertCaught('E_CIDR')

    def test_garbage(self):
        self.append_line('zaprett-telegram-ipset', 'telegram.org')
        self.assertCaught('E_CIDR')

    def test_overlap(self):
        self.append_line('zaprett-telegram-ipset', '91.108.4.0/24')
        self.assertCaught('E_OVERLAP')

    def test_duplicate(self):
        self.append_line('zaprett-exclude-ipset', '10.0.0.0/8')
        self.assertCaught('E_DUP')

    def test_include_net_inside_exclude(self):
        self.append_line('zaprett-telegram-ipset', '10.1.0.0/16')
        self.assertCaught('E_INCLUDE_EXCLUDED')


class ManifestControls(BundleCase):
    def test_sha_mismatch(self):
        self.rewrite('zaprett-youtube', self.content('zaprett-youtube') + b'example.org' + NL, fix_sha=False)
        self.assertCaught('E_SHA')

    def test_file_missing(self):
        os.remove(self.file_path('tls_clienthello_vk_com'))
        self.assertCaught('E_FILE_MISSING')

    def test_orphan_file(self):
        bb.write_bytes(os.path.join(self.bundle, 'files', 'bin', 'unknown.bin'), b'x')
        self.assertCaught('E_ORPHAN_FILE')

    def test_duplicate_id(self):
        m = self.manifest('zaprett-rutracker')
        m['type'] = 'list_exclude'
        m['file'] = bc.ROUTER_BUNDLE_ROOT + '/files/lists/exclude/zaprett-rutracker.txt'
        bb.write_bytes(os.path.join(self.bundle, 'files', 'lists', 'exclude', 'zaprett-rutracker.txt'),
                       self.content('zaprett-rutracker'))
        bb.write_bytes(os.path.join(self.bundle, 'manifests', 'lists', 'exclude', 'zaprett-rutracker.json'),
                       bb.json_bytes(m))
        self.assertCaught('E_DUP_ID')

    def test_required_item_missing(self):
        os.remove(self.file_path('zaprett-telegram'))
        os.remove(self.manifest_path('zaprett-telegram'))
        self.assertCaught('E_REQUIRED_MISSING')

    def test_dependency_missing(self):
        m = self.manifest('strategy-general')
        m['dependencies'].append('no_such_fake')
        self.save_manifest('strategy-general', m)
        self.assertCaught('E_DEP_MISSING')

    def test_dependency_is_url(self):
        m = self.manifest('strategy-general')
        m['dependencies'] = [bb.ZREPO_RAW + 'manifests/bin/quic_initial_www_google_com.json']
        self.save_manifest('strategy-general', m)
        self.assertCaught('E_DEP_NOT_ID')

    def test_keys(self):
        m = self.manifest('zaprett-discord')
        del m['manifest_url']
        self.save_manifest('zaprett-discord', m)
        self.assertCaught('E_MANIFEST_KEYS')

    def test_file_outside_bundle(self):
        m = self.manifest('zaprett-discord')
        m['file'] = '/etc/zaprett/files/lists/include/zaprett-discord.txt'
        self.save_manifest('zaprett-discord', m)
        self.assertCaught('E_MANIFEST_FILE')

    def test_wrong_type_for_dir(self):
        m = self.manifest('zaprett-discord')
        m['type'] = 'ipset'
        self.save_manifest('zaprett-discord', m)
        self.assertCaught('E_MANIFEST_TYPE')

    def test_source_not_bundle(self):
        m = self.manifest('zaprett-discord')
        m['source'] = 'repo'
        self.save_manifest('zaprett-discord', m)
        self.assertCaught('E_MANIFEST_FIELD')

    def test_index_mismatch(self):
        path = os.path.join(self.bundle, 'index.json')
        idx = json.loads(bc.read_bytes(path).decode('utf-8'))
        idx['items'].pop()
        bb.write_bytes(path, bb.json_bytes(idx))
        self.assertCaught('E_INDEX')


class StrategyControls(BundleCase):
    def test_placeholder_target_missing(self):
        self.replace_text('strategy-general', '${bin:quic_initial_www_google_com}', '${bin:no_such_fake}')
        self.assertCaught('E_PLACEHOLDER_MISSING')

    def test_placeholder_not_in_dependencies(self):
        self.rewrite('strategy-fix-v3', self.content('strategy-fix-v3')
                     + b'--new --filter-udp=443 --dpi-desync=fake --dpi-desync-fake-quic=${bin:quic_initial_www_google_com}' + NL)
        self.assertCaught('E_PLACEHOLDER_NOT_DEP')

    def test_placeholder_unknown(self):
        self.replace_text('strategy-general', '${hostlists}', '${hostlistz}')
        self.assertCaught('E_PLACEHOLDER_UNKNOWN')

    def test_placeholder_unknown_kind(self):
        self.replace_text('strategy-general', '${bin:quic_initial_www_google_com}', '${fake:quic_initial_www_google_com}')
        self.assertCaught('E_PLACEHOLDER_UNKNOWN')

    def test_placeholder_glued(self):
        self.replace_text('strategy-general', ' ${hostlists}', ' --x${hostlists}')
        self.assertCaught('E_PLACEHOLDER_GLUED')

    def test_placeholder_syntax(self):
        self.replace_text('strategy-general', '${bin:quic_initial_www_google_com}', '${bin:quic_initial_www_google_com')
        self.assertCaught('E_PLACEHOLDER_SYNTAX')

    def test_unknown_option(self):
        self.replace_text('strategy-general', '--dpi-desync-repeats=6', '--dpi-desync-foobar=6')
        self.assertCaught('E_OPTION_UNKNOWN')

    def test_unknown_mode(self):
        self.replace_text('strategy-general', '--dpi-desync=fake ', '--dpi-desync=bogusmode ')
        self.assertCaught('E_MODE_UNKNOWN')

    def test_stray_token(self):
        self.replace_text('strategy-general', ' --new', ' stray --new')
        self.assertCaught('E_STRAY_TOKEN')

    def test_comment_eats_placeholder(self):
        self.replace_text('strategy-general', '--filter-udp=443 ${hostlists}', '--filter-udp=443 --comment QUIC ${hostlists}')
        self.assertCaught('E_COMMENT_EATS_PLACEHOLDER')

    def test_strategy_crlf(self):
        self.rewrite('strategy-alt', self.content('strategy-alt').replace(NL, CRLF))
        self.assertCaught('E_CR')

    def test_empty_strategy(self):
        self.rewrite('strategy-alt', b'--new --new' + NL)
        self.assertCaught('E_EMPTY_STRATEGY')


class PresetControls(BundleCase):
    def mutated(self, fn):
        p = copy.deepcopy(self.presets)
        fn(p)
        return p

    def svc(self, p, sid):
        return next(s for s in p['services'] if s['id'] == sid)

    def test_list_not_in_bundle(self):
        self.assertCaught('E_PRESET_REF', self.mutated(lambda p: self.svc(p, 'youtube').update(lists=['zaprett-nope'])))

    def test_ipset_given_as_list(self):
        self.assertCaught('E_PRESET_REF', self.mutated(
            lambda p: self.svc(p, 'telegram').update(lists=['zaprett-telegram-ipset'])))

    def test_always_wrong_type(self):
        self.assertCaught('E_PRESET_REF', self.mutated(
            lambda p: p['always'].update(exclude_lists=['zaprett-exclude-ipset'])))

    def test_min_bytes_below_freeze(self):
        self.assertCaught('E_TARGET_MIN', self.mutated(
            lambda p: self.svc(p, 'telegram')['test_targets'][0].update(min_bytes=16000)))

    def test_min_bytes_above_real_size(self):
        self.assertCaught('E_TARGET_MIN', self.mutated(
            lambda p: self.svc(p, 'telegram')['test_targets'][0].update(min_bytes=20000)))

    def test_target_without_reference_size(self):
        self.assertCaught('E_TARGET_REF', self.mutated(
            lambda p: self.svc(p, 'discord')['test_targets'].append({'url': 'https://example.com/', 'min_bytes': 20000})))

    def test_target_not_https(self):
        self.assertCaught('E_TARGET_FIELD', self.mutated(
            lambda p: self.svc(p, 'discord')['test_targets'][1].update(url='http://gateway.discord.gg/')))

    def test_list_service_without_targets(self):
        self.assertCaught('E_TARGET_NONE', self.mutated(lambda p: self.svc(p, 'rutracker').update(test_targets=[])))

    def test_unknown_source(self):
        self.assertCaught('E_PRESET_SOURCE', self.mutated(lambda p: self.svc(p, 'rkn_full').update(sources=['nope'])))

    def test_works_no_with_lists(self):
        self.assertCaught('E_PRESET_NO', self.mutated(lambda p: self.svc(p, 'whatsapp').update(lists=['zaprett-youtube'])))

    def test_service_without_data(self):
        self.assertCaught('E_PRESET_EMPTY', self.mutated(lambda p: self.svc(p, 'cloudflare').update(sources=[])))

    def test_note_not_russian(self):
        self.assertCaught('E_PRESET_NOT_RU', self.mutated(lambda p: self.svc(p, 'spotify').update(note='No bypass.')))

    def test_unknown_tier(self):
        self.assertCaught('E_PRESET_FIELD', self.mutated(lambda p: self.svc(p, 'youtube').update(tier='medium')))

    def test_default_service_works_no(self):
        self.assertCaught('E_PRESET_DEFAULTS', self.mutated(lambda p: p['defaults'].update(services=['youtube', 'spotify'])))

    def test_quick_too_few(self):
        self.assertCaught('E_QUICK_COUNT', self.mutated(
            lambda p: p['defaults'].update(quick_test_strategies=p['defaults']['quick_test_strategies'][:7])))

    def test_quick_too_many(self):
        extra = ['strategy-alt6', 'strategy-alt7']
        self.assertCaught('E_QUICK_COUNT', self.mutated(
            lambda p: p['defaults']['quick_test_strategies'].extend(extra[:13 - len(p['defaults']['quick_test_strategies'])])))

    def test_quick_missing_strategy(self):
        self.assertCaught('E_QUICK_REF', self.mutated(
            lambda p: p['defaults']['quick_test_strategies'].__setitem__(1, 'strategy-nope')))

    def test_quick_global_profile(self):
        self.assertCaught('E_QUICK_GLOBAL', self.mutated(
            lambda p: p['defaults']['quick_test_strategies'].__setitem__(1, 'strategy-alt5')))

    def test_quick_broken_strategy(self):
        self.replace_text('strategy-alt', '--dpi-desync-repeats=6', '--dpi-desync-foobar=6')
        self.assertCaught('E_QUICK_BROKEN')

    def test_default_strategy_not_in_quick(self):
        self.assertCaught('E_QUICK_DEFAULT', self.mutated(
            lambda p: p['defaults']['quick_test_strategies'].__setitem__(0, 'strategy-alt6')))


class TokenizerControls(unittest.TestCase):
    def test_comment_words_dropped(self):
        tokens, info = bc.tokenize_strategy('--comment Telegram (WebRTC) [W.I.P.] --filter-udp=1400 --new' + chr(10))
        self.assertEqual(tokens, ['--filter-udp=1400', '--new'])
        self.assertEqual(info['comment_junk'], ['Telegram', '(WebRTC)', '[W.I.P.]'])

    def test_comment_with_equals_kept(self):
        tokens, info = bc.tokenize_strategy('--comment=x --filter-tcp=443')
        self.assertEqual(tokens, ['--comment=x', '--filter-tcp=443'])
        self.assertEqual(info['comment_blocks'], 0)

    def test_trailing_backslash(self):
        text = '--filter-udp=443 --new ' + chr(92) + chr(10) + '--filter-tcp=443' + chr(10)
        tokens, info = bc.tokenize_strategy(text)
        self.assertEqual(tokens, ['--filter-udp=443', '--new', '--filter-tcp=443'])
        self.assertEqual(info['trailing_backslashes'], 1)

    def test_profile_scope(self):
        self.assertEqual(bc.analyze_profile(['--filter-tcp=443', '--dpi-desync=syndata'])['scope'], 'all')
        self.assertEqual(bc.analyze_profile(['--filter-tcp=443', '${hostlists}'])['scope'], 'hosts')
        self.assertEqual(bc.analyze_profile(['--filter-udp=443', '--ipset-ip=1.1.1.1'])['scope'], 'hosts')
        self.assertEqual(bc.analyze_profile(['--filter-udp=0-65535', '--filter-l7=wireguard'])['scope'], 'l7:wireguard')
        self.assertEqual(bc.analyze_profile(['--filter-tcp=443', '--hostlist-exclude-domains=a.com'])['scope'], 'all')

    def test_bare_flags(self):
        a = bc.analyze_profile(['--filter-tcp=443', '--dpi-desync-autottl', '--dpi-desync-any-protocol'])
        self.assertTrue(a['autottl'])
        self.assertTrue(a['any_protocol_no_cutoff'])
        b = bc.analyze_profile(['--filter-udp=443', '--dpi-desync-any-protocol', '--dpi-desync-cutoff=d3'])
        self.assertFalse(b['any_protocol_no_cutoff'])

    def test_grammar_from_source(self):
        options, modes = GRAMMAR
        self.assertIn('dpi-desync-fake-tls-mod', options)
        self.assertNotIn('dpi-desync-foobar', options)
        self.assertIn('disorder2', modes)
        self.assertNotIn('bogusmode', modes)


class SourceControls(unittest.TestCase):
    """Входные данные: подмена хоть одного байта обязана остановить генератор."""

    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix='zaprett-src-test-')
        self.repo = os.path.join(self.tmp, 'zaprett-repo')
        for rel in ('manifests/strategies/nfqws', 'files/strategies/nfqws', 'manifests/bin', 'files/bin',
                    'manifests/lists/include', 'files/lists/include'):
            src = os.path.join(bb.ZREPO_DIR, *rel.split('/'))
            dst = os.path.join(self.repo, *rel.split('/'))
            if rel == 'files/lists/include':
                os.makedirs(dst)
                shutil.copy2(os.path.join(src, 'list-rutracker.txt'), dst)
            else:
                shutil.copytree(src, dst)
        shutil.copy2(os.path.join(bb.ZREPO_DIR, 'index.json'), self.repo)
        self.curated = os.path.join(self.tmp, 'curated')
        shutil.copytree(bb.CURATED_DIR, self.curated)

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def flip_last_byte(self, *parts):
        path = os.path.join(*parts)
        data = bytearray(bc.read_bytes(path))
        data[-2] ^= 1
        bb.write_bytes(path, bytes(data))

    def test_positive_copy_loads(self):
        items, _skipped = bb.load_repo_items(self.repo)
        self.assertEqual(len(items), 70)
        self.assertEqual(len(bb.load_list_items(self.curated, self.repo)), 7)

    def test_curated_list_changed(self):
        self.flip_last_byte(self.curated, 'youtube.txt')
        with self.assertRaises(bb.SourceError):
            bb.load_list_items(self.curated, self.repo)

    def test_rutracker_changed(self):
        self.flip_last_byte(self.repo, 'files', 'lists', 'include', 'list-rutracker.txt')
        with self.assertRaises(bb.SourceError):
            bb.load_list_items(self.curated, self.repo)

    def test_strategy_changed(self):
        self.flip_last_byte(self.repo, 'files', 'strategies', 'nfqws', 'strategy-general.txt')
        with self.assertRaises(bb.SourceError):
            bb.load_repo_items(self.repo)

    def test_fake_changed(self):
        self.flip_last_byte(self.repo, 'files', 'bin', 'tls_clienthello_www_google_com.bin')
        with self.assertRaises(bb.SourceError):
            bb.load_repo_items(self.repo)

    def test_index_changed(self):
        path = os.path.join(self.repo, 'index.json')
        bb.write_bytes(path, bc.read_bytes(path) + NL)
        with self.assertRaises(bb.SourceError):
            bb.load_repo_items(self.repo)

    def test_dependency_not_in_index(self):
        path = os.path.join(self.repo, 'manifests', 'strategies', 'nfqws', 'strategy-general.json')
        m = json.loads(bc.read_bytes(path).decode('utf-8'))
        m['dependencies'] = [bb.ZREPO_RAW + 'manifests/bin/no_such_fake.json']
        bb.write_bytes(path, bb.json_bytes(m))
        with self.assertRaises(bb.SourceError):
            bb.load_repo_items(self.repo)


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    unittest.main(verbosity=1)
