#!/usr/bin/env node
// SPDX-License-Identifier: MIT
//
// Name checks for the LuCI views of luci-app-zaprett (beyond `node --check`):
//   * identifiers that are neither declared in the file nor known globals;
//   * zc.<member> uses that common.js does not define;
//   * ui.createHandlerFn(this, '<method>') names that the view does not define;
//   * this.<method>(...) calls of view methods that do not exist.
//
// Needs the "acorn" parser: `npm install acorn` next to this file or
// ACORN=/path/to/acorn node check_names.js [package_dir]

'use strict';

const fs = require('fs');
const path = require('path');

let acorn;

try {
	acorn = require(process.env.ACORN || 'acorn');
}
catch (e) {
	console.error('acorn is not available: ' + e.message);
	process.exit(2);
}

const pkg = path.resolve(process.argv[2] || path.join(__dirname, '..'));
const res = path.join(pkg, 'htdocs/luci-static/resources');

const GLOBALS = new Set([
	'window', 'document', 'L', 'E', '_', 'N_', 'Promise', 'Object', 'Array', 'String', 'Number', 'Math',
	'JSON', 'Blob', 'URL', 'Date', 'isNaN', 'Error', 'RegExp', 'navigator', 'undefined', 'NaN', 'Infinity',
	'CustomEvent', 'Set', 'Map'
]);

let problems = 0;

function problem(file, node, text) {
	problems++;
	console.log('FAIL %s:%d: %s', path.relative(pkg, file), node.loc.start.line, text);
}

function wrap(source) {
	const names = [];
	const re = /^'require ([\w.]+)(?: as (\w+))?';$/gm;
	let m;

	while ((m = re.exec(source)) != null)
		names.push(m[2] || m[1].replace(/\./g, '_'));

	return { names, code: '(function(window, document, L' + names.map(n => ', ' + n).join('') + ') {\n' + source + '\n})' };
}

function walk(node, visit, parent, key) {
	if (!node || typeof node.type != 'string')
		return;

	visit(node, parent, key);

	for (const k of Object.keys(node)) {
		if (k == 'loc' || k == 'start' || k == 'end')
			continue;

		const v = node[k];

		if (Array.isArray(v))
			v.forEach(c => walk(c, visit, node, k));
		else if (v && typeof v.type == 'string')
			walk(v, visit, node, k);
	}
}

function declaredNames(ast) {
	const names = new Set();
	const addPattern = p => {
		if (!p)
			return;

		switch (p.type) {
		case 'Identifier': names.add(p.name); break;
		case 'AssignmentPattern': addPattern(p.left); break;
		case 'RestElement': addPattern(p.argument); break;
		case 'ArrayPattern': p.elements.forEach(addPattern); break;
		case 'ObjectPattern': p.properties.forEach(pr => addPattern(pr.value || pr.argument)); break;
		}
	};

	walk(ast, n => {
		if (n.type == 'VariableDeclarator')
			addPattern(n.id);
		else if (n.type == 'FunctionDeclaration' || n.type == 'FunctionExpression' || n.type == 'ArrowFunctionExpression') {
			if (n.id)
				names.add(n.id.name);

			n.params.forEach(addPattern);
		}
		else if (n.type == 'CatchClause')
			addPattern(n.param);
	});

	return names;
}

function isReference(node, parent, key) {
	if (!parent)
		return true;

	if (parent.type == 'MemberExpression' && key == 'property' && !parent.computed)
		return false;

	if ((parent.type == 'Property' || parent.type == 'MethodDefinition') && key == 'key' && !parent.computed)
		return false;

	if (parent.type == 'VariableDeclarator' && key == 'id')
		return false;

	if ((parent.type == 'FunctionDeclaration' || parent.type == 'FunctionExpression' || parent.type == 'ArrowFunctionExpression') && (key == 'params' || key == 'id'))
		return false;

	if (parent.type == 'CatchClause' && key == 'param')
		return false;

	if (parent.type == 'LabeledStatement' || parent.type == 'BreakStatement' || parent.type == 'ContinueStatement')
		return false;

	return true;
}

/* Keys of the object literal passed to <x>.extend({...}) returned by the module. */
function extendKeys(ast) {
	const keys = new Set();

	walk(ast, n => {
		if (n.type == 'CallExpression' && n.callee.type == 'MemberExpression' &&
		    !n.callee.computed && n.callee.property.name == 'extend' && n.arguments[0]?.type == 'ObjectExpression')
			for (const p of n.arguments[0].properties)
				if (p.key && !p.computed)
					keys.add(p.key.name ?? p.key.value);
	});

	return keys;
}

function parse(file) {
	const source = fs.readFileSync(file, 'utf8');
	const wrapped = wrap(source);

	return { wrapped, ast: acorn.parse(wrapped.code, { ecmaVersion: 2022, locations: true }) };
}

const common = parse(path.join(res, 'zaprett/common.js'));
const commonKeys = extendKeys(common.ast);
const files = [ path.join(res, 'zaprett/common.js') ].concat(
	fs.readdirSync(path.join(res, 'view/zaprett')).filter(f => f.endsWith('.js')).map(f => path.join(res, 'view/zaprett', f)));

for (const file of files) {
	const { ast } = parse(file);
	const declared = declaredNames(ast);
	const viewKeys = extendKeys(ast);
	const isView = file.includes(path.sep + 'view' + path.sep);
	/* methods inherited from LuCI's view class */
	const inherited = new Set([ 'load', 'render', 'handleSave', 'handleSaveApply', 'handleReset', 'addFooter', 'super' ]);

	walk(ast, (n, parent, key) => {
		if (n.type == 'Identifier' && isReference(n, parent, key) && !declared.has(n.name) && !GLOBALS.has(n.name))
			problem(file, n, 'undeclared identifier "%s"'.replace('%s', n.name));

		if (n.type == 'MemberExpression' && !n.computed && n.object.type == 'Identifier' && n.object.name == 'zc' && !commonKeys.has(n.property.name))
			problem(file, n, 'zc.%s is not defined in common.js'.replace('%s', n.property.name));

		if (isView && n.type == 'CallExpression' && n.callee.type == 'MemberExpression' && !n.callee.computed &&
		    n.callee.object.type == 'Identifier' && n.callee.object.name == 'ui' && n.callee.property.name == 'createHandlerFn' &&
		    n.arguments[0]?.type == 'ThisExpression' && n.arguments[1]?.type == 'Literal' && !viewKeys.has(n.arguments[1].value))
			problem(file, n, 'createHandlerFn refers to missing method "%s"'.replace('%s', n.arguments[1].value));

		if (n.type == 'CallExpression' && n.callee.type == 'MemberExpression' && !n.callee.computed &&
		    n.callee.object.type == 'ThisExpression' && !viewKeys.has(n.callee.property.name) && !inherited.has(n.callee.property.name))
			problem(file, n, 'this.%s() is not a method of the module'.replace('%s', n.callee.property.name));
	});

	console.log('checked %s', path.relative(pkg, file));
}

console.log('\nRESULT %s, %d problem(s)', problems ? 'FAILED' : 'OK', problems);
process.exit(problems ? 1 : 0);
