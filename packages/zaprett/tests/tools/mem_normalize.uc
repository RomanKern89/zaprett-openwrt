'use strict';

// Manual measurement (not a unit test): peak memory (VmHWM) of subscription normalization.
// Usage: ucode -S -L '<root>/files/usr/share/ucode/*.uc' -- mem_normalize.uc <file|text> <kind> <input> <output>
//   file  streaming normalize_file() as used by `sources update`
//   text  in-memory normalize_text() of the whole input (control: shows what streaming avoids)
import * as fs from 'fs';
import * as SRC from 'zaprett.sources';

function hwm_kib() {
	let m = match(fs.readfile('/proc/self/status') ?? '', /VmHWM:[ \t]+([0-9]+) kB/);
	return m ? int(m[1]) : -1;
}

let mode = ARGV[0], kind = ARGV[1], input = ARGV[2], output = ARGV[3];
if ((mode != 'file' && mode != 'text') || (kind != 'hosts' && kind != 'ipset') || !input || !output) {
	warn('usage: mem_normalize.uc <file|text> <hosts|ipset> <input> <output>\n');
	exit(2);
}
let before = hwm_kib();
let t0 = clock(true);
let r;
if (mode == 'file')
	r = SRC.normalize_file(kind, input, output);
else {
	r = SRC.normalize_text(kind, fs.readfile(input));
	fs.writefile(output, r.text);
}
let t1 = clock(true);
print(sprintf('%J\n', {
	mode: mode, input_bytes: fs.stat(input)?.size, valid: r?.valid, invalid: r?.invalid, output_bytes: r?.bytes,
	hwm_before_kib: before, hwm_after_kib: hwm_kib(),
	ms: (t1[0] - t0[0]) * 1000 + int((t1[1] - t0[1]) / 1000000)
}));
