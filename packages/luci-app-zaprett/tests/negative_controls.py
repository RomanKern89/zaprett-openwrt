#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Negative controls for check_static.py: every deliberately broken copy of
the package must make the checker fail with the expected message.

    python negative_controls.py [package_dir]
"""

import os
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
PKG = os.path.abspath(sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, '..'))
CHECKER = os.path.join(HERE, 'check_static.py')

OVERVIEW = 'htdocs/luci-static/resources/view/zaprett/overview.js'
PLUGIN = 'root/usr/share/rpcd/ucode/luci.zaprett'
PLUGIN_ANCHOR = '\tconst dir = tmpdir();\n'
COMMON = 'htdocs/luci-static/resources/zaprett/common.js'
PO = 'po/ru/zaprett.po'
ACL = 'root/usr/share/rpcd/acl.d/luci-app-zaprett.json'
MENU = 'root/usr/share/luci/menu.d/luci-app-zaprett.json'


def patch(root, rel, old, new):
    path = os.path.join(root, rel)
    data = open(path, 'rb').read()
    if old.encode('utf-8') not in data:
        raise SystemExit('control setup failed: %r not found in %s' % (old, rel))
    open(path, 'wb').write(data.replace(old.encode('utf-8'), new.encode('utf-8'), 1))


CONTROLS = [
    ('untranslated string',
     lambda r: patch(r, OVERVIEW, "_('Try again')", "_('Negative control untranslated string')"),
     "no translation for 'Negative control untranslated string'"),
    ('outdated pot',
     lambda r: patch(r, OVERVIEW, "_('Try again')", "_('Negative control untranslated string')"),
     'zaprett.pot is outdated'),
    ('placeholder mismatch',
     lambda r: patch(r, PO, 'msgstr "Записей: %d, размер: %s"', 'msgstr "Записей: %s, размер: %d"'),
     'placeholders differ'),
    ('empty translation',
     lambda r: patch(r, PO, 'msgstr "Повторить"', 'msgstr ""'),
     "empty translation for 'Try again'"),
    ('duplicate po entry',
     lambda r: patch(r, PO, 'msgid "Try again"', 'msgid "Try again"\nmsgstr "Повторить"\n\nmsgid "Try again"'),
     'duplicate msgid'),
    ('innerHTML',
     lambda r: patch(r, OVERVIEW, "this.status = data[0];", "this.status = data[0]; document.body.innerHTML = '';"),
     'forbidden construct innerHTML'),
    ('JS syntax error',
     lambda r: patch(r, OVERVIEW, 'handleSaveApply: null,', 'handleSaveApply: null,,'),
     'node --check failed'),
    ('_() with a variable',
     lambda r: patch(r, OVERVIEW, "_('Try again')", "_(someVariable)"),
     '_() without a single string literal argument'),
    ('ACL method removed',
     lambda r: patch(r, ACL, '"diag"', '"diag_typo"'),
     'ACL methods differ from plugin'),
    ('RPC declaration removed',
     lambda r: patch(r, COMMON, "callDiag: decl('diag'),", ''),
     'common.js declares'),
    ('menu view without file',
     lambda r: patch(r, MENU, '"zaprett/diagnostics"', '"zaprett/diagnostic"'),
     'menu views'),
    ('CR bytes',
     lambda r: patch(r, OVERVIEW, "'use strict';\n", "'use strict';\r\n"),
     'contains CR bytes'),
    ('ucode call of (a ?? obj.fn)',
     lambda r: patch(r, PLUGIN, PLUGIN_ANCHOR, PLUGIN_ANCHOR + '\t(dir ?? fs.readfile)(1);\n'),
     'direct call of a parenthesised expression with ??'),
    ('ucode call of (a || obj.fn)',
     lambda r: patch(r, PLUGIN, PLUGIN_ANCHOR, PLUGIN_ANCHOR + '\t(dir || fs.readfile)(1);\n'),
     'direct call of a parenthesised expression with ||'),
    ('ucode call of (a && obj.fn)',
     lambda r: patch(r, PLUGIN, PLUGIN_ANCHOR, PLUGIN_ANCHOR + '\t(dir && fs.readfile)(1);\n'),
     'direct call of a parenthesised expression with &&'),
    ('ucode call of (c ? a : obj.fn)',
     lambda r: patch(r, PLUGIN, PLUGIN_ANCHOR, PLUGIN_ANCHOR + '\t(dir ? dir : fs.readfile)(1);\n'),
     'direct call of a parenthesised expression with ?:'),
]

# Patches that must NOT make the checker fail (guards against overreach).
POSITIVE = [
    ('ucode call through a variable',
     lambda r: patch(r, PLUGIN, PLUGIN_ANCHOR, PLUGIN_ANCHOR + '\tconst probe = dir ?? fs.readfile;\n\tprobe(1);\n')),
    ('ucode parentheses without a call',
     lambda r: patch(r, PLUGIN, PLUGIN_ANCHOR, PLUGIN_ANCHOR + '\tconst probe = (dir ?? fs.readfile);\n')),
]


def main():
    failures = 0

    for name, setup, expected in CONTROLS:
        tmp = tempfile.mkdtemp(prefix='zaprett-negctl-')
        root = os.path.join(tmp, 'luci-app-zaprett')
        shutil.copytree(PKG, root, ignore=shutil.ignore_patterns('__pycache__'))

        try:
            setup(root)
            res = subprocess.run([sys.executable, CHECKER, root], capture_output=True, text=True, encoding='utf-8')
            output = res.stdout + res.stderr

            if res.returncode != 0 and expected in output:
                print('PASS control "%s": checker failed with "%s"' % (name, expected))
            else:
                failures += 1
                print('FAIL control "%s": rc=%d, expected text found=%s' % (name, res.returncode, expected in output))
                print(output[-2000:])
        finally:
            shutil.rmtree(tmp, ignore_errors=True)

    for name, setup in POSITIVE:
        tmp = tempfile.mkdtemp(prefix='zaprett-posctl-')
        root = os.path.join(tmp, 'luci-app-zaprett')
        shutil.copytree(PKG, root, ignore=shutil.ignore_patterns('__pycache__'))

        try:
            setup(root)
            res = subprocess.run([sys.executable, CHECKER, root], capture_output=True, text=True, encoding='utf-8')

            if res.returncode == 0:
                print('PASS positive control "%s": checker still passes' % name)
            else:
                failures += 1
                print('FAIL positive control "%s": checker failed' % name)
                print((res.stdout + res.stderr)[-1500:])
        finally:
            shutil.rmtree(tmp, ignore_errors=True)

    base = subprocess.run([sys.executable, CHECKER, PKG], capture_output=True, text=True, encoding='utf-8')
    print('%s positive run on the real package: rc=%d' % ('PASS' if base.returncode == 0 else 'FAIL', base.returncode))

    if base.returncode != 0:
        failures += 1

    print('\nRESULT %d control(s) failed' % failures)
    return 1 if failures else 0


if __name__ == '__main__':
    sys.exit(main())
