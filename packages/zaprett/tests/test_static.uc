'use strict';

// Static checks over all ucode sources of the package. Guards against a ucode miscompilation (24.10.8 and
// 25.12.5): `(a ?? obj.fn)(x)`, `(a || obj.fn)(x)`, `(a && obj.fn)(x)` and `(c ? a : obj.fn)(x)` return a wrong
// value and corrupt neighbouring locals. Allowed form: `let fn = a ?? obj.fn; fn(x);`.
import * as fs from 'fs';
import * as T from 'ztest';
import * as UC from 'ucscan';

T.begin('static');
T.selfcheck();

/* ---- the scanner itself: every forbidden form is found, safe code is not ---- */
let forbidden = [
	'(a ?? obj.fn)(x);',
	'let r = (a || obj.fn)(x);',
	'let r = (a && obj.fn)(x);',
	'return (c ? a : obj.fn)(x);',
	'f((opts.probe ?? N.probe)(tasks, {}));',
	'let r = ( a ?? b ) ( x );',
	'x = (a ?? (b || c))(\n1);',
	'let g = (x) => (a || b)(x);',
	'let dl = (opts.probe ?? N.probe)(tasks, { concurrency: 2 });'
];
for (let src in forbidden)
	T.eq(length(UC.find_calls(src)), 1, 'forbidden form found: ' + src);

let allowed = [
	'let fn = a ?? obj.fn; fn(x);',
	'let fn = c ? a : obj.fn; let r = fn(x);',
	'let v = (a ?? b);',
	'let v = (a || b) + (c && d);',
	'print(sprintf("%s", a ?? b));',
	'if (a || b) {\n\tf(x);\n}',
	'while (a && b) (x = 1);',
	'f(a || b)(c);',
	'obj.fn(a ?? b)(c);',
	'let re = /(a|b)?(c)/;',
	'let m = match(s, /^(x|y)$/) ? 1 : 0;',
	'let s = \'(a ?? b)(c)\';',
	'let s = "(a || b)(c)";',
	'let t = `(a && b)(c)`;',
	'// (a ?? b)(c)\nlet z = 1;',
	'/* (c ? a : b)(x) */ let z = 1;',
	'let q = (t1[0] - t0[0]) / 1000 + (a ? 1 : 2);',
	'let o = x?.y(z);'
];
for (let src in allowed)
	T.eq(UC.find_calls(src), [], 'allowed code not flagged: ' + src);
T.eq(UC.find_calls('let a = 1;\n\nlet r = (a ?? obj.fn)(x);\n')[0]?.line, 3, 'line number of a finding');

/* ---- all .uc files of the package ---- */
function collect(dir, out) {
	for (let n in sort(fs.lsdir(dir) ?? [])) {
		let p = dir + '/' + n;
		let st = fs.lstat(p);
		if (st?.type == 'directory')
			collect(p, out);
		else if (st?.type == 'file' && substr(n, -3) == '.uc')
			push(out, p);
	}
	return out;
}
let files = collect(T.ROOT + '/files', []);
collect(T.ROOT + '/tests', files);
T.ok(length(files) >= 30, sprintf('ucode sources found: %d', length(files)));
T.ok(index(files, T.ROOT + '/files/usr/share/zaprett/cli.uc') >= 0 && index(files, T.ROOT + '/files/usr/share/ucode/zaprett/sources.uc') >= 0,
	'CLI and library modules are scanned');
let hits = [];
for (let f in files)
	for (let h in UC.find_calls(fs.readfile(f)))
		push(hits, sprintf('%s:%d: %s', substr(f, length(T.ROOT) + 1), h.line, h.text));
T.eq(hits, [], 'no directly called parenthesized ??/||/&&/?: expression in the package');

// negative control on a real module: the original bug of sources.uc is found again when put back
let src = fs.readfile(T.ROOT + '/files/usr/share/ucode/zaprett/sources.uc');
let broken = replace(src, 'let dl = probe(tasks,', 'let dl = (opts.probe ?? N.probe)(tasks,');
T.ok(broken != src, 'negative control prepared');
T.eq(length(UC.find_calls(broken)), 1, 'negative control: the reverted sources.uc bug is found');

/* ---- every <alias>.<name> of an imported zaprett module must exist in that module ---- */
const MODDIR = T.ROOT + '/files/usr/share/ucode/zaprett';
let exports = {};
for (let n in sort(fs.lsdir(MODDIR) ?? []))
	if (substr(n, -3) == '.uc')
		exports[substr(n, 0, length(n) - 3)] = UC.exports_of(fs.readfile(MODDIR + '/' + n));
T.ok(length(keys(exports)) == 15 && exports.nft?.is_applied && exports.net?.probe && !exports.net?.is_applied,
	sprintf('exports collected from %d modules (is_applied only in nft)', length(keys(exports))));
T.eq(UC.module_imports("import * as N from 'zaprett.net';\nimport * as NF from 'zaprett.nft';\n"), { N: 'net', NF: 'nft' },
	'module aliases parsed');
T.eq(UC.named_imports("import { P, ok, fail } from 'zaprett.util';"), [ { module: 'util', name: 'P' },
	{ module: 'util', name: 'ok' }, { module: 'util', name: 'fail' } ], 'named imports parsed');

let member_hits = [];
for (let f in files)
	for (let h in UC.find_unknown_members(fs.readfile(f), exports))
		push(member_hits, sprintf('%s:%d: %s', substr(f, length(T.ROOT) + 1), h.line, h.text));
T.eq(member_hits, [], 'every call of an imported zaprett module exists in that module');

// negative controls: the real defect found in the tester and a call that simply does not exist
let tester_src = fs.readfile(MODDIR + '/tester.uc');
let bad_import_src = replace(tester_src, 'NF.is_applied()', 'N.is_applied()');
T.ok(bad_import_src != tester_src, 'negative control prepared');
T.eq(length(UC.find_unknown_members(bad_import_src, exports)), 1, 'N.is_applied from zaprett.net is found');
T.eq(length(UC.find_unknown_members("import * as V from 'zaprett.validate';\nlet x = V.no_such_check('a');\n", exports)), 1,
	'a call of a missing export is found');
T.eq(length(UC.find_unknown_members("import { no_such } from 'zaprett.util';\n", exports)), 1, 'a missing named import is found');
T.eq(UC.find_unknown_members("import * as V from 'zaprett.validate';\nlet x = V.domain_valid('a.com') ? 1 : 0;\nlet o = { V: { any: 1 } };\nlet y = o.V.any;\n", exports), [],
	'valid code and a same-named property are not flagged');

exit(T.finish());
