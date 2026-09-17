// Static check for a ucode miscompilation (24.10 and 25.12): a parenthesized expression with a top-level
// `??`, `||`, `&&` or `?:` that is called directly, e.g. `(a ?? obj.fn)(x)`, returns a wrong value and corrupts
// neighbouring locals. Only `let fn = a ?? obj.fn; fn(x);` is safe.
'use strict';

const KEYWORDS_BEFORE_EXPR = [ 'return', 'typeof', 'delete', 'in', 'of', 'case', 'else', 'void', 'throw', 'do' ];
const OPERAND = 41;	// ')' — what a masked string or regex literal counts as for the next '/'

function ident(c) {
	return (c >= 48 && c <= 57) || (c >= 65 && c <= 90) || (c >= 97 && c <= 122) || c == 95 || c == 36;
}

function space(c) {
	return c == 32 || c == 9 || c == 10 || c == 13;
}

// Replaces comments, string/template literals and regex literals with spaces (newlines kept), so that the
// remaining text contains only code and positions/line numbers do not move.
export function mask(src) {
	let out = [], n = length(src), i = 0;
	let lastsig = 0, word = '', in_word = false;
	let blank = (from, to) => {
		for (let k = from; k < to; k++)
			push(out, (ord(src, k) == 10) ? '\n' : ' ');
	};
	while (i < n) {
		let c = ord(src, i), c2 = (i + 1 < n) ? ord(src, i + 1) : 0;
		if (c == 47 && c2 == 47) {	// line comment
			let e = index(substr(src, i), '\n');
			e = (e < 0) ? n : i + e;
			blank(i, e);
			i = e;
			continue;
		}
		if (c == 47 && c2 == 42) {	// block comment
			let e = index(substr(src, i + 2), '*/');
			e = (e < 0) ? n : i + 2 + e + 2;
			blank(i, e);
			i = e;
			continue;
		}
		if (c == 39 || c == 34 || c == 96) {	// ' " `
			let k = i + 1;
			while (k < n && ord(src, k) != c)
				k += (ord(src, k) == 92) ? 2 : 1;
			k = (k < n) ? k + 1 : n;
			blank(i, k);
			i = k;
			lastsig = OPERAND;
			in_word = false;
			word = '';
			continue;
		}
		if (c == 47) {
			let regex = (lastsig == 0) || index('(,=:[!&|?{};+-*%<>~^', chr(lastsig)) >= 0 ||
				(ident(lastsig) && index(KEYWORDS_BEFORE_EXPR, word) >= 0);
			if (regex) {
				let k = i + 1, cls = false;
				while (k < n) {
					let ck = ord(src, k);
					if (ck == 92)
						k++;
					else if (ck == 91)
						cls = true;
					else if (ck == 93)
						cls = false;
					else if (ck == 47 && !cls)
						break;
					else if (ck == 10)
						break;
					k++;
				}
				k++;
				while (k < n && ident(ord(src, k)))
					k++;
				blank(i, k);
				i = k;
				lastsig = OPERAND;
				in_word = false;
				word = '';
				continue;
			}
		}
		push(out, chr(c));
		if (space(c))
			in_word = false;
		else {
			if (ident(c)) {
				if (!in_word)
					word = '';
				word += chr(c);
				in_word = true;
			}
			else {
				in_word = false;
				word = '';
			}
			lastsig = c;
		}
		i++;
	}
	return join('', out);
};

// Pure: names a module exports (`export function f`, `export const C`, `export let x`).
export function exports_of(src) {
	let names = {};
	for (let l in split(mask(src), '\n')) {
		let m = match(trim(l), /^export[ \t]+(function|const|let)[ \t]+([A-Za-z_$][A-Za-z0-9_$]*)/);
		if (m)
			names[m[2]] = true;
	}
	return names;
};

// Pure: `import * as <alias> from 'zaprett.<module>'` -> { alias: module }.
// Читается сырой текст: mask() затирает строковые литералы, а имя модуля — как раз литерал.
export function module_imports(src) {
	let aliases = {};
	for (let l in split(src, '\n')) {
		// только настоящие операторы импорта в начале строки, иначе попадут строковые литералы тестов
		let m = match(l, /^[ \t]*import[ \t]+\*[ \t]+as[ \t]+([A-Za-z_$][A-Za-z0-9_$]*)[ \t]+from[ \t]+'zaprett\.([a-z_]+)'/);
		if (m)
			aliases[m[1]] = m[2];
	}
	return aliases;
};

// Pure: names taken from a module by `import { a, b } from 'zaprett.<module>'` -> [ { module, name } ].
export function named_imports(src) {
	let out = [];
	let lines = split(src, '\n');
	for (let i = 0; i < length(lines); i++) {
		if (!match(lines[i], /^[ \t]*import[ \t]*\{/))
			continue;
		// импорт может занимать несколько строк: склеиваем до строки с from '...'
		let stmt = lines[i];
		for (let k = i + 1; k < length(lines) && index(stmt, '}') < 0; k++)
			stmt += ' ' + lines[k];
		let m = match(stmt, /^[ \t]*import[ \t]*\{([^}]*)\}[ \t]*from[ \t]*'zaprett\.([a-z_]+)'/);
		if (!m)
			continue;
		for (let n in split(m[1], ','))
			if (trim(n) != '')
				push(out, { module: m[2], name: trim(n) });
	}
	return out;
};

// Every `<alias>.<name>` of an imported zaprett module must exist in that module. Catches the whole class of
// "wrong alias" errors, e.g. `import * as N from 'zaprett.net'` plus `N.is_applied()` (that one lives in
// zaprett.nft). exports: { module: { name: true } }. Returns [ { line, text } ].
export function find_unknown_members(src, exports) {
	let code = mask(src);
	let aliases = module_imports(src);
	let hits = [];
	let lines = split(code, '\n'), raw = split(src, '\n');
	for (let i = 0; i < length(lines); i++) {
		let l = lines[i], pos = 0;
		while (true) {
			let rest = substr(l, pos);
			let m = match(rest, /([A-Za-z_$][A-Za-z0-9_$]*)\.([A-Za-z_$][A-Za-z0-9_$]*)/);
			if (!m)
				break;
			let start = pos + index(rest, m[0]);
			pos = start + length(m[0]);
			let before = (start > 0) ? substr(l, start - 1, 1) : '';
			if (before == '.' || before == '?')		// obj.alias.name / optional chaining — not our alias
				continue;
			let mod = aliases[m[1]];
			if (mod == null)
				continue;
			if (exports[mod] == null)
				push(hits, { line: i + 1, text: sprintf('%s: неизвестный модуль zaprett.%s', m[0], mod) });
			else if (!exports[mod][m[2]])
				push(hits, { line: i + 1, text: sprintf('%s: в zaprett.%s нет %s (строка: %s)', m[0], mod, m[2], trim(raw[i])) });
		}
	}
	for (let ni in named_imports(src)) {
		if (exports[ni.module] != null && !exports[ni.module][ni.name])
			push(hits, { line: 0, text: sprintf('import { %s } from zaprett.%s: такого экспорта нет', ni.name, ni.module) });
	}
	return hits;
};

// Pure: top-level `??`, `||`, `&&` or ternary `?` inside code[from, to).
function has_top_level_choice(code, from, to) {
	let depth = 0;
	for (let k = from; k < to; k++) {
		let c = ord(code, k), c2 = (k + 1 < to) ? ord(code, k + 1) : 0;
		if (c == 40 || c == 91 || c == 123)
			depth++;
		else if (c == 41 || c == 93 || c == 125)
			depth--;
		else if (depth == 0) {
			if ((c == 124 && c2 == 124) || (c == 38 && c2 == 38) || (c == 63 && c2 == 63))
				return true;
			if (c == 63 && c2 != 46)	// ternary, not optional chaining `?.`
				return true;
		}
	}
	return false;
}

// Returns [ { line, text } ] for every directly called parenthesized choice expression in src.
export function find_calls(src) {
	let code = mask(src), n = length(code);
	let stack = [], hits = [];
	for (let i = 0; i < n; i++) {
		let c = ord(code, i);
		if (c == 40) {
			push(stack, i);
			continue;
		}
		if (c != 41 || !length(stack))
			continue;
		let open = pop(stack);
		let j = i + 1;
		while (j < n && space(ord(code, j)))
			j++;
		if (j >= n || ord(code, j) != 40 || !has_top_level_choice(code, open + 1, i))
			continue;
		// what stands before '(': an operand or a non-keyword name means a call `f(...)(...)`, not the trap
		let p = open - 1;
		while (p >= 0 && space(ord(code, p)))
			p--;
		if (p >= 0) {
			let pc = ord(code, p);
			if (pc == 41 || pc == 93 || pc == 46)
				continue;
			if (ident(pc)) {
				let s = p;
				while (s > 0 && ident(ord(code, s - 1)))
					s--;
				if (index(KEYWORDS_BEFORE_EXPR, substr(code, s, p - s + 1)) < 0)
					continue;
			}
		}
		let line = length(split(substr(src, 0, open), '\n'));
		push(hits, { line: line, text: trim(substr(src, open, j - open + 1)) });
	}
	return hits;
};
