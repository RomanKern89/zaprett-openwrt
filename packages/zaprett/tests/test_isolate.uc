'use strict';

// Автоподбор без отключения обхода (контракт v1.6 §17): пользователь проверок, правила nftables, выбор режима,
// откат на exclusive, восстановление после падения, отмена. Движок, procd, nft и сеть подменены через hooks:
// на стенде тест не трогает работающую службу.
import * as fs from 'fs';
import * as T from 'ztest';
import { P, set_paths, run } from 'zaprett.util';
import * as C from 'zaprett.config';
import * as N from 'zaprett.nft';
import * as NET from 'zaprett.net';
import * as ISO from 'zaprett.isolate';
import * as TS from 'zaprett.tester';
import * as CMD from 'zaprett.commands';
import * as TXT from 'zaprett.text';

T.begin('isolate');
T.selfcheck();
let W = T.sandbox('isolate');
set_paths({ passwd: W + '/passwd' });

/* ---- пользователь zaprett-test ---- */
let pw = 'root:x:0:0:root:/root:/bin/ash\ndaemon:*:1:1:daemon:/var:/bin/false\nzaprett-test:x:29411:29411:zaprett-test:/var:/bin/false\n';
T.eq(ISO.passwd_entry(pw, 'zaprett-test'), { uid: 29411, gid: 29411 }, 'uid/gid из passwd');
T.eq(ISO.passwd_entry(pw, 'zaprett'), null, 'имя сравнивается целиком, а не префиксом');
T.eq(ISO.passwd_entry('zaprett-test:x:abc:1:x:/:/bin/false\n', 'zaprett-test'), null, 'нечисловой uid не принимается');
fs.writefile(P.passwd, pw);
T.eq(ISO.test_user(), { uid: 29411, gid: 29411 }, 'test_user читает P.passwd');
fs.writefile(P.passwd, 'zaprett-test:x:0:0::/:/bin/false\n');
T.eq(ISO.test_user(), null, 'uid 0 (root) никогда не считается пользователем проверок');
fs.writefile(P.passwd, 'root:x:0:0:root:/root:/bin/ash\n');
T.eq(ISO.test_user(), null, 'нет пользователя — null');

/* ---- выбор режима ---- */
let cfg = C.normalize({ enabled: '1', lists: [ 'list-youtube' ], strategy: 'strategy-general' }, null, { max_domains: '2', settle: '0' });
let info = { enabled: true, running: true, stopped: false, user: { uid: 29411, gid: 29411 } };
T.eq(ISO.refusal(cfg, info, {}), null, 'все условия выполнены — isolated');
T.eq(ISO.refusal(cfg, info, { exclusive: true }), 'forced', '--exclusive');
T.eq(ISO.refusal(cfg, { enabled: true, running: false, stopped: false, user: info.user }, {}), 'engine_not_running', 'движок не работает');
T.eq(ISO.refusal(cfg, { enabled: false, running: true, stopped: false, user: info.user }, {}), 'engine_not_running', 'служба выключена');
T.eq(ISO.refusal(cfg, { enabled: true, running: true, stopped: true, user: info.user }, {}), 'engine_not_running', 'служба остановлена пользователем');
T.eq(ISO.refusal(cfg, { enabled: true, running: true, stopped: false, user: null }, {}), 'no_test_user', 'нет пользователя');
T.eq(ISO.refusal(C.normalize({ qnum: '65535' }, null, null), info, {}), 'qnum_out_of_range', 'qnum 65535: нет места для qnum+1');
T.eq(ISO.refusal(C.normalize({ qnum: '65534' }, null, null), info, {}), null, 'qnum 65534 допустим');
T.eq(ISO.refusal(C.normalize({ desync_mark: '0x04000000' }, null, null), info, {}), 'mark_conflict', 'метка пересекается с TEST_MARK');
T.ok(!(C.TEST_MARK & C.CLIENT_MARK) && !(C.TEST_MARK & 0x40000000) && !(C.TEST_MARK & 0x20000000), 'TEST_MARK не совпадает с метками по умолчанию');

/* ---- состояние изоляции (из файла в текст nft) ---- */
T.eq(ISO.parse_state({ uid: 29411, qnum: 201, ports: { tcp: [ '80', '443', '1-65535' ], udp: [ '443' ] } }),
	{ uid: 29411, qnum: 201, ports: { tcp: [ '80', '443', '1-65535' ], udp: [ '443' ] } }, 'состояние принято');
T.eq(ISO.parse_state({ uid: 29411, qnum: 201, ports: { tcp: [ '443 } ; flush ruleset ; {' , '80' ], udp: 'x' } }).ports,
	{ tcp: [ '80' ], udp: [] }, 'чужой текст в портах отброшен');
T.eq(ISO.parse_state({ uid: 29411, qnum: 201, ports: null }).ports, null, 'базовый прогон: ports null');
T.eq(ISO.parse_state({ uid: 0, qnum: 201 }), null, 'uid 0 отвергнут');
T.eq(ISO.parse_state({ uid: '29411', qnum: 201 }), null, 'uid строкой отвергнут');
T.eq(ISO.parse_state({ uid: 29411, qnum: 65536 }), null, 'qnum вне диапазона отвергнут');
T.eq(ISO.read_state(), null, 'файла нет — null');

/* ---- правила nftables ---- */
let ports = { tcp: [ '80', '443' ], udp: [ '443' ] };
let wan = { v4: [ 'eth1' ], v6: [ 'eth1' ] };
let ccfg = C.normalize({ clients_mode: 'include', clients: [ '192.168.1.10' ] }, null, null);
let iso = { uid: 29411, qnum: 201, ports: { tcp: [ '443' ], udp: [] } };
let text = N.render(ccfg, ports, wan, { isolation: iso });
let lines = map(split(text, '\n'), (l) => trim(l));
let at = (l) => index(lines, l);
T.has(lines, 'meta skuid 29411 ct mark set ct mark or 0x04000000 goto postnat_test', 'трафик проверок уходит из основной цепочки');
T.ok(at('chain postnat {') >= 0 && at('meta skuid 29411 ct mark set ct mark or 0x04000000 goto postnat_test') == at('chain postnat {') + 1,
	'правило исключения стоит первым в postnat');
T.has(lines, 'oifname @wanif tcp dport { 80, 443 } ct original packets 1-9 ip daddr != @nozaprett ct mark and 0x08000000 != 0 meta mark set meta mark or 0x20000000 ct mark set ct mark or 0x40000000 queue num 200 bypass',
	'основная очередь с фильтром клиентов не изменилась');
T.has(lines, 'oifname @wanif tcp dport { 443 } ct original packets 1-9 ip daddr != @nozaprett meta mark set meta mark or 0x20000000 ct mark set ct mark or 0x40000000 queue num 201 bypass',
	'проверки кандидата — в очередь qnum+1, порты кандидата, без фильтра клиентов');
T.ok(index(text, 'udp dport { 443 } ct original packets 1-9 ip daddr != @nozaprett meta mark set meta mark or 0x20000000 ct mark set ct mark or 0x40000000 queue num 201') < 0,
	'порты UDP кандидата пусты — правила UDP для очереди 201 нет');
T.has(lines, 'ct mark and 0x04000000 != 0 goto prenat_test', 'ответы на проверки уходят из основной цепочки prenat');
T.ok(at('ct mark and 0x04000000 != 0 goto prenat_test') == at('type filter hook prerouting priority -101; policy accept;') + 1,
	'исключение ответов стоит первым в prenat');
T.has(lines, 'iifname @wanif tcp sport { 443 } ct reply packets 1-3 ip saddr != @nozaprett ct mark set ct mark or 0x40000000 queue num 201 bypass',
	'ответы проверок — в очередь 201');
T.ok(at('chain postnat_test {') > at('chain postnat {') && at('chain prenat_test {') > at('chain prenat {'), 'цепочки теста объявлены');
let base_text = N.render(ccfg, ports, wan, { isolation: { uid: 29411, qnum: 201, ports: null } });
T.ok(index(base_text, 'queue num 201') < 0 && index(base_text, 'goto postnat_test') > 0 && index(base_text, 'chain postnat_test {\n\t}') > 0,
	'базовый прогон: трафик проверок не идёт ни в одну очередь');
let plain = N.render(ccfg, ports, wan, {});
T.ok(index(plain, 'skuid') < 0 && index(plain, '_test') < 0 && index(plain, '0x04000000') < 0, 'без изоляции цепочек теста нет');
let v6 = N.render(C.normalize({ ipv6: '1' }, null, null), ports, wan, { isolation: iso });
T.ok(index(v6, 'oifname @wanif6 tcp dport { 443 } ct original packets 1-9 ip6 daddr != @nozaprett6 meta mark set meta mark or 0x20000000 ct mark set ct mark or 0x40000000 queue num 201 bypass') > 0,
	'ipv6: проверки кандидата по IPv6 тоже в очередь 201');
let cands = CMD.fw_candidates(ccfg, ports, wan, false, null, iso);
T.ok(length(cands) == 1 && index(cands[0].text, 'goto postnat_test') > 0, 'fw apply рисует цепочки теста из состояния');
T.ok(index(CMD.fw_candidates(ccfg, ports, wan, false, null, null)[0].text, 'skuid') < 0, 'и не рисует без состояния');

// nft -c на этом роутере (ничего не применяет)
let tables_before = run([ 'nft', 'list', 'tables' ]).stdout;
let have_queue = fs.stat('/sys/module/nft_queue')?.type == 'directory';
function nft_check(t, name) {
	let path = W + '/' + name + '.nft';
	fs.writefile(path, have_queue ? t : replace(t, / queue num [0-9]+ bypass/g, ' accept'));
	return N.check_file(path);
}
for (let v in [ [ 'isolated', text ], [ 'isolated-baseline', base_text ], [ 'isolated-ipv6', v6 ] ]) {
	let r = nft_check(v[1], v[0]);
	T.ok(r.rc == 0, sprintf('nft -c %s: %s', v[0], r.output));
}
T.ok(nft_check(replace(text, 'goto postnat_test', 'goto postnat_tset'), 'broken-goto').rc != 0, 'отрицательный контроль: goto в несуществующую цепочку отвергнут');
T.ok(nft_check(replace(text, 'meta skuid 29411', 'meta skuid zaprett-no-such-user'), 'broken-user').rc != 0, 'отрицательный контроль: неизвестный пользователь отвергнут');
T.eq(run([ 'nft', 'list', 'tables' ]).stdout, tables_before, 'nft -c ничего не изменил');

/* ---- загрузки от имени пользователя: argv start-stop-daemon ---- */
let fake = W + '/fake-ssd.sh';
fs.writefile(fake, '#!/bin/sh\necho "$@" > "' + W + '/ssd-args"\nwhile [ $# -gt 0 ] && [ "$1" != "--" ]; do shift; done\nshift\nexec /bin/sh "' + P.probe + '" "$@"\n');
set_paths({ ssd: fake });
system([ 'chmod', '755', fake ]);
let pr = NET.probe([ { key: 'a', url: 'https://zaprett-no-such-host.invalid/' } ], { concurrency: 1, timeout: 2, user: 'root' });
let sargs = trim(fs.readfile(W + '/ssd-args') ?? '');
T.ok(index(sargs, '-S -c root -x ' + P.probe + ' -- ') == 0, 'probe с user запускается через start-stop-daemon -c: ' + sargs);
T.ok(pr.results.a?.rc > 0, 'загрузка выполнена скриптом (ошибка DNS, rc ' + pr.results.a?.rc + ')');
NET.cleanup(pr.dir);
fs.unlink(W + '/ssd-args');
pr = NET.probe([ { key: 'a', url: 'https://zaprett-no-such-host.invalid/' } ], { concurrency: 1, timeout: 2 });
T.ok(fs.stat(W + '/ssd-args') == null && pr.results.a?.rc > 0, 'отрицательный контроль: без user start-stop-daemon не вызывается');
NET.cleanup(pr.dir);
// the tester's probe_targets must hand the user down to the downloads (a live run once checked everything as root)
let pcfg = C.normalize(null, null, { timeout: '2', concurrency: '1' });
let pt = TS.probe_targets([ { url: 'https://zaprett-no-such-host.invalid/', min_bytes: 0 } ], pcfg, { user: 'root' });
sargs = trim(fs.readfile(W + '/ssd-args') ?? '');
T.ok(index(sargs, '-S -c root -x ' + P.probe + ' -- ') == 0 && pt.total == 1, 'probe_targets передаёт user в загрузки: ' + sargs);
fs.unlink(W + '/ssd-args');
TS.probe_targets([ { url: 'https://zaprett-no-such-host.invalid/', min_bytes: 0 } ], pcfg, null);
T.ok(fs.stat(W + '/ssd-args') == null, 'отрицательный контроль: probe_targets без user — от текущего пользователя');
pr = NET.probe([ { key: 'a', url: 'https://x.invalid/' } ], { concurrency: 1, timeout: 2, user: 'zaprett-no-such-user' });
T.eq(pr.error, 'tmp_failed', 'пользователь, которому нельзя отдать каталог, — ошибка, а не загрузка от root');
NET.cleanup(pr.dir);

/* ---- полный прогон автоподбора с подменами ---- */
// пользователь подменён hook-ом; в passwd песочницы его нет, так что настоящая очистка не трогает процессы стенда
fs.writefile(P.passwd, 'root:x:0:0:root:/root:/bin/ash\n');
function make_hooks(o) {
	o = o ?? {};
	let log = { actions: [], probes: [], fw: [], cleanup: 0, overrides: 0 };
	let hooks = {
		init_action: (a) => {
			push(log.actions, a);
			if (fs.stat(TS.override_path()))
				log.overrides++;
			return { rc: 0, output: '' };
		},
		wait_running: (want, ms) => ({ running: want, pid: 4242 }),
		instance_state: () => ({ running: o.main_running ?? true, pid: 4242 }),
		test_user: () => ('user' in o) ? o.user : { uid: 29411, gid: 29411 },
		wait_test: (want, ms) => ({ running: o.test_starts ?? true, pid: 5151 }),
		fw_apply: () => {
			push(log.fw, ISO.read_state());
			return o.fw_fail ? { ok: false, error: 'nft_check_failed', message: 'нет' } : { ok: true };
		},
		cleanup: (fw) => {
			log.cleanup++;
			return ISO.cleanup(fw);
		},
		check_ms: 0,
		probe: (targets, c, popts) => {
			let st = ISO.read_state();
			push(log.probes, { user: popts?.user ?? null, ports: st ? st.ports : 'none',
				args: fs.readfile(ISO.args_path()), override: fs.stat(TS.override_path()) != null });
			let n = length(log.probes);
			let okc = (n == 1) ? 0 : ((n == o.good_probe) ? length(targets) : 1);
			return { ok: okc, total: length(targets), avg_ms: 10, targets: map(targets, (t) => ({ url: t.url, ok: okc > 0, ms: 10 })) };
		}
	};
	return { hooks: hooks, log: log };
}
function leftovers() {
	return filter([ TS.state_path(), TS.override_path(), ISO.state_path(), ISO.args_path(), ISO.engine_file() ], (p) => fs.stat(p) != null);
}

if (T.NFQWS) {
	let m = make_hooks();
	let res = TS.run(cfg, { strategies: [ 'strategy-general', 'strategy-alt' ], hooks: m.hooks }, null);
	let rres = json(fs.readfile(TS.results_path()) ?? 'null');
	T.ok(res.ok, 'isolated: прогон завершён: ' + (res.message ?? res.error ?? ''));
	T.eq([ res.mode, res.mode_reason, rres?.mode, rres?.mode_reason ], [ 'isolated', null, 'isolated', null ], 'режим isolated в ответе и в test-results.json');
	T.ok(index(m.log.actions, 'stop') < 0, 'основной движок ни разу не останавливался: ' + join(',', m.log.actions));
	T.eq(m.log.overrides, 0, 'test-override не писался: основной движок остаётся на рабочей стратегии');
	T.eq(m.log.probes[0]?.user, ISO.USER, 'базовый прогон — от пользователя проверок');
	T.eq(m.log.probes[0]?.ports, null, 'базовый прогон — без очереди кандидата');
	T.ok(length(m.log.probes) == 3 && m.log.probes[1].user == ISO.USER && m.log.probes[2].user == ISO.USER, 'проверки кандидатов — от пользователя проверок');
	T.ok(type(m.log.probes[1].ports?.tcp) == 'array' && length(m.log.probes[1].ports.tcp) > 0, 'у кандидата порты в правилах теста');
	T.ok(index(m.log.probes[1].args ?? '', '--qnum=201') >= 0 && index(m.log.probes[1].args ?? '', '--qnum=200') < 0,
		'второй движок слушает qnum+1');
	T.ok(!m.log.probes[1].override && !m.log.probes[2].override, 'во время проверок override нет');
	T.ok(m.log.cleanup >= 1 && m.log.fw[length(m.log.fw) - 1] == null, 'в конце правила теста сняты (последний fw apply без состояния)');
	T.eq(leftovers(), [], 'после прогона не осталось файлов теста');
	T.ok(index(m.log.actions, 'start') >= 0 && length(filter(m.log.actions, (a) => a == 'start')) == 3,
		'init start: подготовка + по одному на стратегию, без перезапуска в конце: ' + join(',', m.log.actions));

	// принудительный прежний режим
	m = make_hooks();
	res = TS.run(cfg, { strategies: [ 'strategy-general' ], hooks: m.hooks, exclusive: true }, null);
	T.eq([ res.mode, res.mode_reason ], [ 'exclusive', 'forced' ], '--exclusive: прежний режим');
	T.ok(index(m.log.actions, 'stop') >= 0 && m.log.overrides >= 1 && m.log.probes[0].user == null,
		'exclusive: движок остановлен, стратегия через override, проверки от root');
	T.eq(leftovers(), [], 'exclusive: следов не осталось');

	// nft отверг правила теста — откат на exclusive
	m = make_hooks({ fw_fail: true });
	res = TS.run(cfg, { strategies: [ 'strategy-general' ], hooks: m.hooks }, null);
	T.eq([ res.mode, res.mode_reason ], [ 'exclusive', 'nft_rejected' ], 'nft -c отверг правила — exclusive с причиной');
	T.ok(m.log.cleanup >= 1 && index(m.log.actions, 'stop') >= 0 && m.log.probes[0].user == null, 'откат: изоляция снята, движок остановлен');
	T.eq(leftovers(), [], 'откат nft: следов не осталось');

	// второй инстанс не стартовал — откат на exclusive
	m = make_hooks({ test_starts: false });
	res = TS.run(cfg, { strategies: [ 'strategy-general' ], hooks: m.hooks }, null);
	T.eq([ res.mode, res.mode_reason ], [ 'exclusive', 'instance_failed' ], 'второй инстанс не стартовал — exclusive');
	T.eq(json(fs.readfile(TS.results_path()) ?? 'null')?.mode_reason, 'instance_failed', 'причина записана в test-results.json');

	// нет пользователя, движок не работает
	m = make_hooks({ user: null });
	T.eq(TS.run(cfg, { strategies: [ 'strategy-general' ], hooks: m.hooks }, null).mode_reason, 'no_test_user', 'нет пользователя — exclusive');
	m = make_hooks({ main_running: false });
	T.eq(TS.run(cfg, { strategies: [ 'strategy-general' ], hooks: m.hooks }, null).mode_reason, 'engine_not_running', 'движок не работал — exclusive');

	// --apply-if-better в isolated: победитель в UCI, основной движок перезапускается один раз в конце
	C.set({ strategy: 'strategy-general' });
	let acfg = C.load();
	acfg.enabled = true;
	acfg.lists = [ 'list-youtube' ];
	acfg.test.settle = 0;
	acfg.test.max_domains = 2;
	T.eq([ TS.isolated_cfg(acfg).qnum, acfg.qnum ], [ 201, 200 ], 'isolated_cfg меняет только копию');
	m = make_hooks({ good_probe: 3 });	// 1 — базовый прогон, 2 — strategy-general, 3 — strategy-alt
	res = TS.run(acfg, { strategies: [ 'strategy-general', 'strategy-alt' ], apply_if_better: true, hooks: m.hooks }, null);
	T.eq([ res.mode, res.applied, C.load().strategy ], [ 'isolated', 'strategy-alt', 'strategy-alt' ], 'apply-if-better в isolated: победитель в UCI');
	T.eq(m.log.actions[length(m.log.actions) - 1], 'start', 'основной движок перезапущен с победителем в конце');
	T.ok(index(m.log.actions, 'stop') < 0, 'и ни разу не останавливался');
	T.eq(leftovers(), [], 'apply-if-better: следов не осталось');
	C.set({ strategy: 'strategy-general' });
	m = make_hooks();
	res = TS.run(acfg, { strategies: [ 'strategy-general', 'strategy-alt' ], apply_if_better: true, hooks: m.hooks }, null);
	T.eq([ res.applied, C.load().strategy, m.log.actions[length(m.log.actions) - 1] ], [ null, 'strategy-general', 'start' ],
		'отрицательный контроль: не лучше исходной — не применено, основной движок в конце не перезапускается (последний start — кандидат)');
	T.eq(length(filter(m.log.actions, (a) => a == 'start')), 3, 'без победителя start только для подготовки и кандидатов');

	// отмена до первой стратегии
	m = make_hooks();
	let cctx = { progress: () => null, log: () => null, cancelled: () => true, partial: () => null };
	res = TS.run(cfg, { strategies: [ 'strategy-general', 'strategy-alt' ], hooks: m.hooks }, cctx);
	let cres = json(fs.readfile(TS.results_path()) ?? 'null');
	T.eq([ cres?.state, length(cres?.results ?? []), cres?.mode ], [ 'cancelled', 0, 'isolated' ], 'отмена в isolated');
	T.ok(index(m.log.actions, 'stop') < 0 && m.log.cleanup >= 1, 'отмена: основной движок не тронут, изоляция снята');
	T.eq(leftovers(), [], 'отмена: следов не осталось');
}
else
	print('SKIP [isolate] nfqws binary not given: полный прогон isolated не проверен\n');

/* ---- восстановление после падения задачи (kill -9) ---- */
function crash_state(o) {
	T.write(TS.state_path(), sprintf('%J', { started: 1, was_running: o.was_running ?? true, engine: 'nfqws', strategy: 'strategy-general',
		mode: 'isolated', uid: 29411, applied: null }));
	ISO.write_state(29411, 201, { tcp: [ '443' ], udp: [] });
	ISO.write_candidate('nfqws', [ '--qnum=201' ]);
}
let calls = [], cleaned = 0;
let rhooks = (running) => ({
	init_action: (a) => { push(calls, a); return { rc: 0, output: '' }; },
	instance_state: () => ({ running: running }),
	cleanup: (fw) => { cleaned++; return ISO.cleanup(null); }
});
crash_state({});
T.write(TS.results_path(), '{"state":"running","mode":"isolated","results":[]}');
TS.restore(null, rhooks(true));
T.eq([ calls, cleaned, leftovers() ], [ [], 1, [] ], 'упавший isolated: правила и инстанс сняты, работающий движок не тронут');
T.eq(json(fs.readfile(TS.results_path()) ?? 'null')?.state, 'failed', 'результаты упавшего теста помечены failed, а не running');
calls = [];
crash_state({});
TS.restore(null, rhooks(false));
T.eq(calls, [ 'start' ], 'упавший isolated: движок, умерший за время теста, запущен');
calls = [];
crash_state({});
T.write(P.run + '/stopped', '');
TS.restore(null, rhooks(false));
T.eq(calls, [], 'остановленная пользователем служба не запускается');
fs.unlink(P.run + '/stopped');
calls = [];
ISO.write_state(29411, 201, null);
TS.restore(null, rhooks(true));
T.eq([ calls, leftovers() ], [ [], [] ], 'только файлы изоляции без состояния — сняты без запуска движка');
T.eq(TS.restore(null, rhooks(true)), null, 'отрицательный контроль: снимать нечего — null');
calls = [];
cleaned = 0;
T.write(TS.override_path(), '{"engine":"nfqws","strategy":"strategy-alt"}');
T.write(TS.state_path(), '{"was_running":false,"mode":"exclusive"}');
TS.restore(null, rhooks(true));
T.eq([ calls, cleaned, leftovers() ], [ [ 'stop' ], 0, [] ], 'exclusive-восстановление по-старому, без снятия изоляции');

// test stop без задачи снимает следы isolated (настоящий ISO.cleanup: инстанса test нет, пользователя в песочнице нет)
fs.writefile(P.passwd, 'root:x:0:0:root:/root:/bin/ash\n');
crash_state({});
let ts = CMD.test_stop();
T.eq([ ts.ok, ts.state, leftovers() ], [ true, 'restored', [] ], 'test stop снимает следы упавшего isolated');
T.eq(CMD.test_stop().error, 'no_job', 'отрицательный контроль: второй test stop — нечего останавливать');
crash_state({});
let st = CMD.test_status({});
T.eq([ st.ok, leftovers() ], [ true, [] ], 'test status (любой опрос) восстанавливает упавший isolated');

/* ---- статус и текст ---- */
T.write(TS.results_path(), '{"mode":"exclusive","mode_reason":"nft_rejected","results":[{"id":"a","status":"done","ok":1,"total":1,"targets":[]}],"baseline":{"ok":0,"total":1,"targets":[]}}');
st = CMD.test_status({ brief: true });
T.eq([ st.mode, st.mode_reason, st.results?.mode, st.results?.results[0]?.targets ], [ 'exclusive', 'nft_rejected', 'exclusive', null ],
	'test status --brief: mode и mode_reason есть, targets нет');
let txt = TXT.render('test status', st);
T.ok(index(txt, 'останавливался') >= 0 && index(txt, 'nftables не принял') >= 0, 'текст test status называет режим и причину');
T.write(TS.results_path(), '{"mode":"isolated","mode_reason":null,"results":[]}');
T.ok(index(TXT.render('test status', CMD.test_status({})), 'не отключался') >= 0, 'текст для isolated');

exit(T.finish());
