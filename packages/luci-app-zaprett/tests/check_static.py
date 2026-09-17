#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Static checks of luci-app-zaprett (no router needed).

    python check_static.py [--write-pot] [package_dir]

Checks:
  * every file: UTF-8, LF line endings (no CR bytes), no trailing NUL;
  * JS: syntax via "node --check" of the file wrapped like LuCI does
    (function(window, document, L, ...) { ... });
  * JS: forbidden constructs (innerHTML, outerHTML, insertAdjacentHTML,
    document.write, eval, new Function, fs.exec, file.exec);
  * JS: every _() call has a single string literal argument;
  * JSON files: valid JSON; ACL methods == methods of the rpcd plugin;
    menu views == view files;
  * i18n: msgids extracted by two independent methods (own tokenizer and
    Babel) are identical; po/templates/zaprett.pot and po/ru/zaprett.po
    contain exactly these msgids; every msgstr is non-empty, not fuzzy and
    has the same printf placeholders as its msgid.
Exit code 0 when everything passes.
"""

import io
import json
import os
import re
import subprocess
import sys
import tempfile

from babel.messages.extract import extract_javascript
from babel.messages.pofile import read_po

FORBIDDEN = [
    (re.compile(r'\binnerHTML\b'), 'innerHTML'),
    (re.compile(r'\bouterHTML\b'), 'outerHTML'),
    (re.compile(r'\binsertAdjacentHTML\b'), 'insertAdjacentHTML'),
    (re.compile(r'\bdocument\.write\b'), 'document.write'),
    (re.compile(r'(?<![.\w])eval\s*\('), 'eval'),
    (re.compile(r'\bnew\s+Function\b'), 'new Function'),
    (re.compile(r'\bfs\.exec'), 'fs.exec'),
    (re.compile(r'file\.exec|"exec"'), 'file exec'),
]

PLACEHOLDER_RE = re.compile(r"%(?:\{[^}]+\}|'.|0|\x20)?-?\d*(?:\.\d+)?[bcdufoxXsqhjtm%]")

errors = []

# ucode compiler bug (checked on 24.10.8 and 25.12.5): a direct call of a
# parenthesised conditional expression corrupts the stack when the branch that
# was not taken is a member access, e.g. (a ?? obj.fn)(x). Any operator of this
# kind before a call is forbidden.
UCODE_CALL_OPS = ('??', '||', '&&', '?')


def error(msg):
    errors.append(msg)
    print('FAIL', msg)


def luci_ws(s):
    """Whitespace normalisation of LuCI _() lookups (cbi.js trimws)."""
    return re.sub(r'[ \t\n]+', ' ', s.strip())


def read_text(path):
    data = open(path, 'rb').read()
    if b'\r' in data:
        error('%s: contains CR bytes' % path)
    if b'\x00' in data:
        error('%s: contains NUL bytes' % path)
    try:
        return data.decode('utf-8')
    except UnicodeDecodeError as exc:
        error('%s: not UTF-8: %s' % (path, exc))
        return data.decode('utf-8', 'replace')


# --- own JS tokenizer ---------------------------------------------------

REGEX_PREV = set('(,=:[!&|?{};+-*%<>~^')


def js_tokens(src):
    """Yields (kind, value, line) with kinds: str, tpl, name, punct."""
    i, n, line = 0, len(src), 1
    prev_sig = ''
    prev_word = ''
    while i < n:
        c = src[i]
        if c == '\n':
            line += 1
            i += 1
            continue
        if c.isspace():
            i += 1
            continue
        if src.startswith('//', i):
            j = src.find('\n', i)
            i = n if j < 0 else j
            continue
        if src.startswith('/*', i):
            j = src.find('*/', i + 2)
            j = n if j < 0 else j + 2
            line += src.count('\n', i, j)
            i = j
            continue
        if c in '\'"':
            j, buf = i + 1, []
            while j < n and src[j] != c:
                if src[j] == '\\':
                    esc = src[j + 1]
                    if esc == 'n':
                        buf.append('\n')
                    elif esc == 't':
                        buf.append('\t')
                    elif esc == 'u':
                        buf.append(chr(int(src[j + 2:j + 6], 16)))
                        j += 4
                    elif esc == '\n':
                        line += 1
                    else:
                        buf.append(esc)
                    j += 2
                    continue
                if src[j] == '\n':
                    raise ValueError('line %d: newline in string' % line)
                buf.append(src[j])
                j += 1
            yield ('str', ''.join(buf), line)
            i = j + 1
            prev_sig, prev_word = 'str', ''
            continue
        if c == '`':
            j = i + 1
            depth = 0
            while j < n:
                if src[j] == '\\':
                    j += 2
                    continue
                if depth == 0 and src[j] == '`':
                    break
                if src.startswith('${', j):
                    depth += 1
                    j += 2
                    continue
                if depth and src[j] == '}':
                    depth -= 1
                if src[j] == '\n':
                    line += 1
                j += 1
            yield ('tpl', src[i + 1:j], line)
            i = j + 1
            prev_sig, prev_word = 'tpl', ''
            continue
        if c == '/' and (prev_sig in REGEX_PREV or prev_sig == '' or prev_word in ('return', 'typeof')):
            j, in_class = i + 1, False
            while j < n:
                if src[j] == '\\':
                    j += 2
                    continue
                if src[j] == '[':
                    in_class = True
                elif src[j] == ']':
                    in_class = False
                elif src[j] == '/' and not in_class:
                    break
                elif src[j] == '\n':
                    raise ValueError('line %d: newline in regex' % line)
                j += 1
            j += 1
            while j < n and src[j].isalpha():
                j += 1
            yield ('regex', src[i:j], line)
            i = j
            prev_sig, prev_word = 'regex', ''
            continue
        if c.isalnum() or c in '_$':
            j = i
            while j < n and (src[j].isalnum() or src[j] in '_$'):
                j += 1
            word = src[i:j]
            yield ('name', word, line)
            i = j
            prev_sig, prev_word = 'name', word
            continue
        yield ('punct', c, line)
        prev_sig, prev_word = c, ''
        i += 1


def extract_own(path, src):
    msgs = {}
    toks = list(js_tokens(src))
    for k in range(len(toks) - 1):
        kind, val, line = toks[k]
        if kind != 'name' or val != '_':
            continue
        if k > 0 and toks[k - 1] == ('punct', '.', toks[k - 1][2]):
            continue
        if toks[k + 1][:2] != ('punct', '('):
            continue
        arg = toks[k + 2] if k + 2 < len(toks) else None
        close = toks[k + 3] if k + 3 < len(toks) else None
        if not arg or arg[0] != 'str' or not close or close[1] not in (')', ','):
            error('%s:%d: _() without a single string literal argument' % (path, line))
            continue
        msgs.setdefault(arg[1], []).append(line)
    return msgs


def check_ucode_calls(path, src):
    """Finds `(… ?? | || | && | ?: …)(` in ucode sources (see UCODE_CALL_OPS)."""
    tokens = list(js_tokens(src))
    opens = []      # stack of open parentheses with the operators seen inside
    i = 0

    def punct(index, char):
        return 0 <= index < len(tokens) and tokens[index][0] == 'punct' and tokens[index][1] == char

    while i < len(tokens):
        kind, value, line = tokens[i]

        if kind != 'punct':
            i += 1
            continue

        if value == '(':
            opens.append({'line': line, 'ops': set()})
        elif value == ')':
            group = opens.pop() if opens else None

            if group and group['ops'] and punct(i + 1, '('):
                error('%s:%d: direct call of a parenthesised expression with %s '
                      '(ucode stack bug, assign it to a variable first)'
                      % (path, group['line'], ', '.join(sorted(group['ops']))))
        elif opens:
            if value == '?':
                if punct(i + 1, '?'):
                    opens[-1]['ops'].add('??')
                    i += 1
                elif not punct(i + 1, '.'):
                    opens[-1]['ops'].add('?:')
            elif value in ('|', '&') and punct(i + 1, value):
                opens[-1]['ops'].add(value * 2)
                i += 1

        i += 1


def extract_babel(src):
    msgs = set()
    fileobj = io.BytesIO(src.encode('utf-8'))
    for lineno, func, message, comments in extract_javascript(fileobj, {'_': None}, [], {'encoding': 'utf-8'}):
        if func != '_':
            continue
        if isinstance(message, tuple):
            message = message[0]
        if message is not None:
            msgs.add(message)
    return msgs


# --- po helpers ---------------------------------------------------------

def placeholders(s):
    return [p for p in PLACEHOLDER_RE.findall(s) if p != '%%']


def pot_escape(s):
    return s.replace('\\', '\\\\').replace('"', '\\"').replace('\n', '\\n').replace('\t', '\\t')


def build_pot(refs):
    out = ['msgid ""', 'msgstr "Content-Type: text/plain; charset=UTF-8"', '']
    for msgid in sorted(refs):
        for ref in sorted(set(refs[msgid])):
            out.append('#: %s' % ref)
        out.append('msgid "%s"' % pot_escape(msgid))
        out.append('msgstr ""')
        out.append('')
    return '\n'.join(out)


def own_po_parse(text):
    """Minimal independent .po parser: returns {msgid: (msgstr, fuzzy)}."""
    entries = {}
    cur = {'fuzzy': False}
    field = None

    def unq(s):
        s = s.strip()
        if not (s.startswith('"') and s.endswith('"')):
            raise ValueError('bad quoted string: %r' % s)
        return json.loads(s)

    def flush():
        nonlocal cur
        if 'msgid' in cur:
            if cur['msgid'] in entries:
                raise ValueError('duplicate msgid: %r' % cur['msgid'])
            if 'msgstr' not in cur:
                raise ValueError('msgid without msgstr: %r' % cur['msgid'])
            entries[cur['msgid']] = (cur['msgstr'], cur['fuzzy'])
        cur = {'fuzzy': False}

    for raw in text.split('\n'):
        line = raw.strip()
        if not line:
            flush()
            field = None
        elif line.startswith('#,'):
            cur['fuzzy'] = cur['fuzzy'] or 'fuzzy' in line
        elif line.startswith('#'):
            continue
        elif line.startswith('msgid_plural') or line.startswith('msgstr['):
            raise ValueError('plural forms are not used by this package')
        elif line.startswith('msgid '):
            if 'msgid' in cur:
                flush()
            cur['msgid'] = unq(line[6:])
            field = 'msgid'
        elif line.startswith('msgstr '):
            cur['msgstr'] = unq(line[7:])
            field = 'msgstr'
        elif line.startswith('"') and field:
            cur[field] += unq(line)
        else:
            raise ValueError('unexpected line: %r' % raw)
    flush()
    return entries


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    write_pot = '--write-pot' in sys.argv
    pkg = os.path.abspath(args[0] if args else os.path.join(os.path.dirname(__file__), '..'))

    files = []
    for root, _dirs, names in os.walk(pkg):
        for name in names:
            files.append(os.path.join(root, name))

    refs = {}

    for path in sorted(files):
        rel = os.path.relpath(path, pkg).replace(os.sep, '/')
        if rel.endswith(('.pyc',)) or '__pycache__' in rel:
            continue
        text = read_text(path)

        if rel.endswith('.js') and rel.startswith('htdocs/'):
            requires = re.findall(r"^'require ([\w.]+)(?: as (\w+))?';$", text, re.M)
            names = ', '.join((alias or name.replace('.', '_')) for name, alias in requires)
            wrapped = '(function(window, document, L%s) {\n%s\n});\n' % ((', ' + names) if names else '', text)
            with tempfile.NamedTemporaryFile('w', suffix='.js', delete=False, encoding='utf-8', newline='\n') as tmp:
                tmp.write(wrapped)
            res = subprocess.run(['node', '--check', tmp.name], capture_output=True, text=True)
            os.unlink(tmp.name)
            if res.returncode != 0:
                error('%s: node --check failed:\n%s' % (rel, res.stderr))
            else:
                print('PASS node --check', rel)

            code_only = re.sub(r'//[^\n]*', '', text)
            for rx, label in FORBIDDEN:
                if rx.search(code_only):
                    error('%s: forbidden construct %s' % (rel, label))

            try:
                own = extract_own(rel, text)
            except ValueError as exc:
                error('%s: tokenizer: %s' % (rel, exc))
                own = {}
            babel = extract_babel(text)
            if set(own) != babel:
                error('%s: extractors disagree: only own=%r only babel=%r' % (rel, sorted(set(own) - babel), sorted(babel - set(own))))
            for msgid, lines in own.items():
                if luci_ws(msgid) != msgid:
                    error('%s: msgid has irregular whitespace: %r' % (rel, msgid))
                for ln in lines:
                    refs.setdefault(msgid, []).append('%s:%d' % (rel, ln))

        if rel.endswith('.uc') or rel.endswith('/luci.zaprett'):
            try:
                check_ucode_calls(rel, text)
                print('PASS ucode call pattern', rel)
            except ValueError as exc:
                error('%s: tokenizer: %s' % (rel, exc))

        if rel.endswith('.json'):
            try:
                data = json.loads(text)
            except ValueError as exc:
                error('%s: invalid JSON: %s' % (rel, exc))
                continue
            print('PASS json', rel)
            for m in re.finditer(r'"(title|description)"\s*:\s*"((?:[^"\\]|\\.)*)"', text):
                msgid = json.loads('"%s"' % m.group(2))
                line = text.count('\n', 0, m.start()) + 1
                refs.setdefault(msgid, []).append('%s:%d' % (rel, line))

    # --- ACL, plugin, menu consistency
    plugin = open(os.path.join(pkg, 'root/usr/share/rpcd/ucode/luci.zaprett'), encoding='utf-8').read()
    body = plugin[plugin.index('const methods = {'):]
    methods = set(re.findall(r'^\t(\w+): \{$', body, re.M))
    acl = json.load(open(os.path.join(pkg, 'root/usr/share/rpcd/acl.d/luci-app-zaprett.json'), encoding='utf-8'))['luci-app-zaprett']
    read = acl['read']['ubus']['luci.zaprett']
    write = acl['write']['ubus']['luci.zaprett']
    if len(read) != len(set(read)) or len(write) != len(set(write)) or set(read) & set(write):
        error('ACL: duplicate methods or overlap of read and write')
    if set(read) | set(write) != methods:
        error('ACL methods differ from plugin: acl-only=%r plugin-only=%r' % (sorted((set(read) | set(write)) - methods), sorted(methods - (set(read) | set(write)))))
    else:
        print('PASS ACL covers exactly the %d plugin methods' % len(methods))
    if 'file' in json.dumps(acl) or '"exec"' in json.dumps(acl):
        error('ACL grants file access')
    common = open(os.path.join(pkg, 'htdocs/luci-static/resources/zaprett/common.js'), encoding='utf-8').read()
    declared = set(re.findall(r"decl\('(\w+)'", common))
    if declared != methods:
        error('common.js declares %r, plugin has %r' % (sorted(declared), sorted(methods)))
    else:
        print('PASS common.js declares exactly the plugin methods')

    menu = json.load(open(os.path.join(pkg, 'root/usr/share/luci/menu.d/luci-app-zaprett.json'), encoding='utf-8'))
    views = {v['action']['path'] for v in menu.values() if v['action']['type'] == 'view'}
    view_files = {'zaprett/' + f[:-3] for f in os.listdir(os.path.join(pkg, 'htdocs/luci-static/resources/view/zaprett')) if f.endswith('.js')}
    if views != view_files:
        error('menu views %r differ from files %r' % (sorted(views), sorted(view_files)))
    else:
        print('PASS menu views match %d view files' % len(views))

    # --- i18n
    pot_path = os.path.join(pkg, 'po/templates/zaprett.pot')
    pot_text = build_pot(refs)
    if write_pot:
        os.makedirs(os.path.dirname(pot_path), exist_ok=True)
        with open(pot_path, 'w', encoding='utf-8', newline='\n') as fh:
            fh.write(pot_text)
        print('WROTE', pot_path, len(refs), 'msgids')
    elif not os.path.exists(pot_path) or open(pot_path, encoding='utf-8').read() != pot_text:
        error('po/templates/zaprett.pot is outdated (run with --write-pot)')
    else:
        print('PASS zaprett.pot is up to date (%d msgids)' % len(refs))

    po_path = os.path.join(pkg, 'po/ru/zaprett.po')
    if not os.path.exists(po_path):
        error('po/ru/zaprett.po is missing')
        print('\nRESULT FAILED, %d problem(s)' % len(errors))
        return 1
    po_text = read_text(po_path)
    try:
        own_po = own_po_parse(po_text)
    except ValueError as exc:
        error('ru po (own parser): %s' % exc)
        own_po = {}
    with open(po_path, 'rb') as fh:
        catalog = read_po(fh, locale='ru', abort_invalid=True)
    babel_po = {m.id: (m.string, m.fuzzy) for m in catalog if m.id}
    if {k: v[0] for k, v in own_po.items() if k} != {k: v[0] for k, v in babel_po.items()}:
        error('ru po: own parser and Babel disagree')
    else:
        print('PASS ru po parsed identically by two parsers (%d entries)' % len(babel_po))

    missing = sorted(set(refs) - set(babel_po))
    extra = sorted(set(babel_po) - set(refs))
    for msgid in missing:
        error('ru po: no translation for %r (%s)' % (msgid, refs[msgid][0]))
    for msgid in extra:
        error('ru po: obsolete entry %r' % msgid)
    for msgid, (msgstr, fuzzy) in babel_po.items():
        if msgid not in refs:
            continue
        if not msgstr or not msgstr.strip():
            error('ru po: empty translation for %r' % msgid)
        if fuzzy:
            error('ru po: fuzzy translation for %r' % msgid)
        if placeholders(msgid) != placeholders(msgstr):
            error('ru po: placeholders differ for %r: %r vs %r' % (msgid, placeholders(msgid), placeholders(msgstr)))
        if luci_ws(msgstr) != msgstr:
            error('ru po: irregular whitespace in translation of %r' % msgid)
    if not missing and not extra:
        print('PASS ru po covers exactly the %d msgids' % len(refs))

    print('\nRESULT %s, %d problem(s)' % ('OK' if not errors else 'FAILED', len(errors)))
    return 1 if errors else 0


if __name__ == '__main__':
    sys.exit(main())
