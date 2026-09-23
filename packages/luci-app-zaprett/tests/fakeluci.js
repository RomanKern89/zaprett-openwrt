// SPDX-License-Identifier: MIT
'use strict';
// Minimal fake of the LuCI runtime for smoke tests of luci-app-zaprett views.
// Records: RPC calls, modals, notifications, and every non-empty string that
// would reach innerHTML (E()/dom.content with a plain string child).

const fs = require('fs');
const path = require('path');

const RES = path.join(__dirname, '..', 'htdocs/luci-static/resources');

const state = { calls: [], modals: [], notes: [], innerHTML: [], timers: [], polls: [], errors: [] };

class Node {
	constructor(tag) {
		this.tagName = tag;
		this.attrs = {};
		this.childNodes = [];
		this.style = {};
		this.listeners = {};
		this.className = '';
		this.value = '';
		this.checked = false;
		this.disabled = false;
	}
	setAttribute(k, v) {
		this.attrs[k] = String(v);
		if (k == 'class') this.className = String(v);
		if (k == 'value') this.value = String(v);
		if (k == 'checked') this.checked = true;
		if (k == 'disabled') this.disabled = true;
	}
	getAttribute(k) { return (k == 'class') ? this.className : (this.attrs[k] ?? null); }
	removeAttribute(k) { delete this.attrs[k]; }
	appendChild(n) { this.childNodes.push(n); n.parentNode = this; return n; }
	removeChild(n) { this.childNodes = this.childNodes.filter(c => c !== n); return n; }
	replaceChild(a, b) { const i = this.childNodes.indexOf(b); if (i < 0) throw new Error('replaceChild: not a child'); this.childNodes[i] = a; return b; }
	get firstChild() { return this.childNodes[0] ?? null; }
	get lastChild() { return this.childNodes[this.childNodes.length - 1] ?? null; }
	addEventListener(ev, fn) { (this.listeners[ev] = this.listeners[ev] ?? []).push(fn); }
	removeEventListener() {}
	set innerHTML(v) { if (v !== '') state.innerHTML.push(String(v)); this.childNodes = []; }
	get textContent() { return this.childNodes.map(c => (c instanceof Node) ? c.textContent : String(c.data ?? '')).join(''); }
	select() {}
	click() { for (const fn of this.listeners.click ?? []) fn({ target: this }); }
}

class Text { constructor(d) { this.data = d; } get textContent() { return this.data; } }

function append(node, children) {
	if (children == null)
		return;

	if (Array.isArray(children)) {
		for (const c of children) {
			if (c == null)
				continue;
			if (c instanceof Node || c instanceof Text)
				node.appendChild(c);
			else if (Array.isArray(c))
				append(node, c);
			else
				node.appendChild(new Text(String(c)));
		}
		return;
	}

	if (children instanceof Node || children instanceof Text) {
		node.appendChild(children);
		return;
	}

	/* LuCI: a plain string child goes to innerHTML */
	node.innerHTML = String(children);
}

function E(tag, attrs, children) {
	if (Array.isArray(tag)) {
		const frag = new Node('#fragment');
		append(frag, tag.length ? tag : attrs);
		return frag;
	}

	if (Array.isArray(attrs) || typeof(attrs) == 'string' || attrs instanceof Node) {
		children = attrs;
		attrs = {};
	}

	const n = new Node(tag);

	for (const [k, v] of Object.entries(attrs ?? {})) {
		if (v == null)
			continue;
		if (typeof(v) == 'function')
			n.addEventListener(k, v);
		else if (k == 'class')
			n.className = String(v);
		else
			n.setAttribute(k, v);
	}

	append(n, children);

	return n;
}

const dom = {
	content(node, children) { node.childNodes = []; append(node, children); return node; },
	append(node, children) { append(node, children); return node; }
};

/* String.prototype.format: enough of LuCI's for %s %d %% %02d and %1024.1mB */
String.prototype.format = function(...args) {
	let i = 0;
	return this.replace(/%(%|[0-9.]*[a-zA-Z])/g, (m, spec) => {
		if (spec == '%') return '%';
		const v = args[i++];
		if (/m$/.test(spec)) return String(+v || 0);
		if (/d$/.test(spec)) { const n = String(Math.floor(+v || 0)); const w = spec.match(/^0(\d+)/); return w ? n.padStart(+w[1], '0') : n; }
		return String(v);
	}).replace(/(\d)B$/, '$1 B');
};

const document = {
	hidden: false,
	body: new Node('body'),
	documentElement: { getAttribute: k => (k == 'lang') ? state.lang : null },
	addEventListener() {},
	removeEventListener() {},
	execCommand() { return true; }
};

const window = {
	setTimeout(fn) { state.timers.push(fn); return state.timers.length; },
	addEventListener() {},
	removeEventListener() {},
	location: { href: '' },
	isSecureContext: false
};

const L = {
	env: {},
	isObject: v => v != null && typeof(v) == 'object' && !Array.isArray(v),
	url: (...p) => '/cgi-bin/luci/' + p.join('/'),
	bind: (fn, self) => fn.bind(self),
	toArray: v => Array.isArray(v) ? v : (v == null ? [] : [ v ]),
	resolveDefault: (p, d) => Promise.resolve(p).catch(() => d)
};

let replies = {};

const rpc = {
	declare(opts) {
		return (...args) => {
			const params = {};
			(opts.params ?? []).forEach((p, i) => { if (args[i] !== undefined) params[p] = args[i]; });
			state.calls.push({ method: opts.method, params, nobatch: opts.nobatch });
			const r = replies[opts.method];
			if (r === undefined)
				return Promise.reject(new Error('no fake reply for ' + opts.method));
			return Promise.resolve(typeof(r) == 'function' ? r(params) : JSON.parse(JSON.stringify(r)));
		};
	}
};

const ui = {
	createHandlerFn(ctx, fn, ...args) {
		const f = (typeof(fn) == 'string') ? ctx[fn] : fn;
		if (typeof(f) != 'function')
			throw new Error('createHandlerFn: no method ' + fn);
		return ev => f.call(ctx, ...args, ev);
	},
	showModal(title, children) { const n = E('div', {}, children); state.modals.push({ title, node: n }); return n; },
	hideModal() {},
	addNotification(title, children, cls) { state.notes.push({ cls, node: E('div', {}, children) }); },
	addTimeLimitedNotification(title, children, t, cls) { state.notes.push({ cls, node: E('div', {}, children) }); },
	tabs: { initTabGroup() {} },
	changes: { apply(checked) { state.applied = checked; } }
};

const poll = {
	add(fn, interval) { state.polls.push(fn); },
	remove(fn) { state.polls = state.polls.filter(f => f !== fn); }
};

const uciData = { zaprett: { main: { '.type': 'main', debug: '0' } } };
const uci = {
	load: () => Promise.resolve(),
	get: (c, s, o) => { const sec = uciData[c]?.[s]; return (o == null) ? sec : sec?.[o]; },
	set: (c, s, o, v) => { uciData[c][s][o] = v; },
	save: () => Promise.resolve(),
	add: () => {}
};

const baseclass = { extend: obj => obj };
const view = { extend: obj => obj };

const modules = { baseclass, rpc, ui, dom, poll, view, uci };

function load(rel) {
	const src = fs.readFileSync(path.join(RES, rel), 'utf8');
	const req = [];
	const re = /^'require ([\w.]+)(?: as (\w+))?';$/gm;
	let m;
	while ((m = re.exec(src)) != null)
		req.push({ name: m[1], alias: m[2] || m[1].replace(/\./g, '_') });
	const fn = new Function('window', 'document', 'L', 'E', '_', ...req.map(r => r.alias), src);
	return fn(window, document, L, E, s => s, ...req.map(r => {
		if (modules[r.name])
			return modules[r.name];
		throw new Error('module not loaded: ' + r.name);
	}));
}

function useModule(name, rel) {
	modules[name] = load(rel);
	return modules[name];
}

/* Runs queued timers and poll ticks until nothing is left (bounded). */
async function settle(rounds) {
	for (let i = 0; i < (rounds ?? 30); i++) {
		await new Promise(r => setImmediate(r));
		const timers = state.timers.splice(0);
		for (const t of timers) t();
		for (const p of state.polls.slice()) await p();
		await new Promise(r => setImmediate(r));
	}
}

function text(node) {
	return (node instanceof Node || node instanceof Text) ? node.textContent : String(node ?? '');
}

module.exports = { state, E, dom, L, rpc, ui, uci, uciData, useModule, load, settle, text, setReplies: r => { replies = r; }, Node, document };
