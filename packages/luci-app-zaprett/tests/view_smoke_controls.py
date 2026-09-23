#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Negative controls of tests/view-smoke.js: each mutation of a real view must make view-smoke.js fail
with the expected check. The original file is restored and its sha256 verified."""
import hashlib
import os
import shutil
import subprocess
import sys
import tempfile

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
HERE = os.path.dirname(os.path.abspath(__file__))
RES = os.path.join(HERE, '..', 'htdocs', 'luci-static', 'resources')

MUTATIONS = [
    ('string child reaches innerHTML', 'view/zaprett/overview.js',
     "this.row(_('zaprett version'), st.version || _('unknown'))",
     "E('tr', {}, st.version)", 'FAIL overview: no string reached innerHTML'),
    ('wizard does not start the service', 'view/zaprett/overview.js',
     "const action = !running ? 'start' :", "const action = !running ? null :", 'FAIL wizard: service start when stopped'),
    ('selection without --apply-if-better', 'view/zaprett/overview.js',
     "zc.run(zc.callTestStart, undefined, true, true)", "zc.run(zc.callTestStart, undefined, true, false)", 'FAIL wizard: selection asks --apply-if-better quick'),
    ('poll ignores hidden tab', 'zaprett/common.js',
     "if (stopped || busy || document.hidden)", "if (stopped || busy)", 'FAIL overview: no poll while the tab is hidden'),
    ('localized ignores language', 'zaprett/common.js',
     "if (typeof(en) == 'string' && en !== '' && !this.isRussianUI())", "if (typeof(en) == 'string' && en !== '')", 'FAIL localized: ru keeps the original'),
    ('diagnostics polls the log every tick', 'view/zaprett/diagnostics.js',
     "this.showJob({ job: job }, undefined);", "this.handleJob();", 'FAIL diagnostics: one job_status per tick'),
    ('wizard does not wait for the subscription download', 'view/zaprett/overview.js',
     "if (!L.isObject(res.job))\n\t\t\t\treturn res;", "if (true)\n\t\t\t\treturn res;", 'FAIL wizard: probe waits for the subscription download'),
    ('repository keeps no stop function', 'view/zaprett/repo.js',
     "\t\tif (previous)\n\t\t\tprevious();\n", "", 'FAIL repo: a second job does not add a second poll'),
    ('strategies poll without brief', 'view/zaprett/strategies.js',
     "return zc.run(zc.callTestStatus, true).catch(err => {", "return zc.run(zc.callTestStatus).catch(err => {", 'FAIL strategies: poll falls back'),
    # contract v1.4
    ('diagnosed host as a string child', 'zaprett/health.js',
     "'style': 'word-break:break-all' }, txt(t.host || t.url)),", "'style': 'word-break:break-all' }, String(t.host || t.url)),",
     'FAIL v1.4 diagnostics: no string reached innerHTML'),
    ('DNS address as a string child', 'zaprett/health.js',
     "E('div', {}, txt(this.dnsText(t.dns))),", "E('div', {}, this.dnsText(t.dns)),",
     'FAIL wizard: diagnosis puts no string into innerHTML'),
    ('dns_plain shown as a problem', 'zaprett/common.js',
     "'test_running', 'dns_plain' ];", "'test_running' ];", 'FAIL v1.4 overview: dns_plain is informational'),
    ('DNS setup without confirmation', 'zaprett/health.js',
     "if (!ok)\n\t\t\t\treturn null;", "if (false)\n\t\t\t\treturn null;", 'FAIL v1.4 overview: cancel installs nothing'),
    ('DNS card shown for an older service', 'view/zaprett/overview.js',
     "return (!(page instanceof Error) && L.isObject(page?.dns)) ? page.dns : null;", "return { encrypted: false };",
     'FAIL v1.3 overview: no DNS card'),
    ('running diagnosis restarted', 'view/zaprett/diagnostics.js',
     "this.handleDiagnose(null, true);", "this.handleDiagnose(null, false);", 'FAIL v1.4 diagnostics: running diagnosis followed'),
    ('port range order not checked', 'view/zaprett/settings.js',
     "|| to > 65535 || from > to)", "|| to > 65535)", 'FAIL settings: invalid ports rejected'),
    ('wizard diagnoses all services', 'view/zaprett/overview.js',
     "return zh.runDiagnose(ids, box)", "return zh.runDiagnose([], box)", 'FAIL wizard: diagnosis of the failed services'),
    # contract v1.7: variants in the quick setup
    ('wizard ignores the chosen variant', 'view/zaprett/overview.js',
     "return (radio && zc.isId(radio.value)) ? '%s:%s'.format(id, radio.value) : id;", "return id;",
     'FAIL variants: wizard gets id:variant'),
    ('variant name as a string child', 'view/zaprett/overview.js',
     "E('label', {}, [ radio, ' ', E('strong', {}, txt(title)) ]),", "E('label', {}, [ radio, ' ', E('strong', {}, title) ]),",
     'FAIL variants: router names and descriptions never reach innerHTML'),
    ('current variant ignored', 'view/zaprett/overview.js',
     "const current = this.enabledVariant(svc) ?? '';", "const current = '';",
     'FAIL variants: current variant from enabled_variant'),
    ('service with a variant on counts as off', 'view/zaprett/overview.js',
     "s.partially_enabled === true || this.enabledVariant(s) != null;", "s.partially_enabled === true;",
     'FAIL variants: service with the variant on counts as enabled'),
    ('variant ids not checked', 'view/zaprett/overview.js',
     ".filter(v => L.isObject(v) && zc.isId(v.id));", ".filter(v => L.isObject(v));",
     'FAIL variants: radios only for the service with variants'),
    ('check gets id:variant', 'view/zaprett/overview.js',
     "return this.wizardProbe(steps, Array.isArray(res.services) ? res.services : selected);", "return this.wizardProbe(steps, refs);",
     'FAIL variants: the check gets plain service ids'),
]


def sha(path):
    return hashlib.sha256(open(path, 'rb').read()).hexdigest()


failures = 0
for name, rel, old, new, expected in MUTATIONS:
    path = os.path.join(RES, rel)
    backup = os.path.join(tempfile.gettempdir(), 'zaprett-view-smoke-backup.js')
    before = sha(path)
    shutil.copyfile(path, backup)
    try:
        data = open(path, encoding='utf-8').read()
        if data.count(old) != 1:
            print('SETUP FAIL', name, 'anchor count', data.count(old))
            failures += 1
            continue
        open(path, 'w', encoding='utf-8', newline='\n').write(data.replace(old, new))
        res = subprocess.run(['node', os.path.join(HERE, 'view-smoke.js')], capture_output=True, text=True, encoding='utf-8')
        out = res.stdout + res.stderr
        if res.returncode != 0 and expected in out:
            print('PASS control "%s" -> %s' % (name, expected))
        else:
            failures += 1
            print('FAIL control "%s": rc=%d expected found=%s' % (name, res.returncode, expected in out))
            print(out[-1500:])
    finally:
        shutil.copyfile(backup, path)
        after = sha(path)
        if after != before:
            print('RESTORE FAILED', rel)
            sys.exit(3)

print('RESULT %d control(s) failed' % failures)
sys.exit(1 if failures else 0)
