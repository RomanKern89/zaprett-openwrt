// zaprett conformance: runs the router argument generator (strategy.uc build) for a list of cases and prints the
// results as one JSON array. Run on a test router with zaprett installed:
//   ucode -S /tmp/zaprett-conf/gen.uc /tmp/zaprett-conf/in.json /tmp/zaprett-conf/sbx
// The sandbox replaces /etc/zaprett, the user lists and the runtime directory; bundle, guard files and the modules are
// the installed ones. The service configuration (/etc/config/zaprett) is never read or written.
'use strict';

import * as fs from 'fs';
import { P, VERSION, set_paths, mkdir_p } from 'zaprett.util';
import * as C from 'zaprett.config';
import * as S from 'zaprett.store';
import * as G from 'zaprett.strategy';

const USER_FILES = [ 'hosts-include.txt', 'hosts-exclude.txt', 'ipset-include.txt', 'ipset-exclude.txt' ];

let input = ARGV[0], sbx = ARGV[1];
if (!input || !sbx || substr(sbx, 0, 5) != '/tmp/') {
	warn('usage: gen.uc <in.json> </tmp/sandbox>\n');
	exit(2);
}

set_paths({
	etc: sbx + '/etc',
	user: sbx + '/etc/user',
	run: sbx + '/run',
	tmp: sbx + '/run/tmp',
	syslog: false
});
mkdir_p(P.user);
mkdir_p(P.tmp);

let cases = json(fs.readfile(input));
let out = [];

for (let c in cases) {
	let uf = c.user_files ?? {};
	for (let n in USER_FILES) {
		let path = P.user + '/' + n;
		if (uf[n] != null)
			fs.writefile(path, uf[n]);
		else
			fs.unlink(path);
	}
	let cfg = C.normalize(c.main, null, null, null);
	let opts = { engine: c.engine, index: S.scan() };
	if (c.text != null) {
		opts.text = c.text;
		opts.item = { id: c.item_id, name: c.item_id, source: 'user', dependencies: [], type: c.engine };
	}
	else
		opts.strategy = c.strategy;
	let r = G.build(cfg, opts);
	let exp;
	if (r.ok)
		exp = { ok: true, args: r.args, ports: r.ports, warnings: r.warnings, details: r.details };
	else
		exp = { ok: false, error: r.error, message: r.message };
	push(out, { name: c.name, expected: exp, bad_options: cfg.bad_options });
}

let rel = fs.readfile('/etc/openwrt_release', 4096) ?? '';
let m = match(rel, /DISTRIB_DESCRIPTION='([^']*)'/);
print(sprintf('%J\n', { zaprett: VERSION, openwrt: m ? m[1] : null, results: out }));
