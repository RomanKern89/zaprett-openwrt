'use strict';

import * as T from 'ztest';
import * as C from 'zaprett.config';

T.begin('config');
T.selfcheck();

let d = C.normalize(null, null, null);
T.eq([ d.enabled, d.engine, d.strategy, d.list_mode, d.qnum, d.desync_mark, d.postnat_mark, d.ipv6, d.user, d.debug ],
	[ false, 'nfqws', 'strategy-general', 'whitelist', 200, 0x40000000, 0x20000000, false, 'daemon', false ], 'defaults');
T.eq([ d.tcp_pkt_out, d.tcp_pkt_in, d.udp_pkt_out, d.udp_pkt_in, d.flow_offload, d.clients_mode ], [ 9, 3, 9, 0, 'auto', 'all' ], 'default packet limits');
T.eq(d.repo, { url: 'https://raw.githubusercontent.com/CherretGit/zaprett-repo/refs/heads/main/index.json', autoupdate: true, autoupdate_hour: 4 }, 'repo defaults');
T.eq(d.test, { timeout: 5, concurrency: 6, max_domains: 20, settle: 2 }, 'test defaults');
T.eq(d.warnings, [], 'no warnings for defaults');
// inside an existing section a missing list option is an empty list (UCI/LuCI delete empty lists)
let e = C.normalize({ enabled: '1' }, {}, {});
T.eq([ e.lists, e.exclude_lists, e.ipsets, e.exclude_ipsets, e.wan, e.clients4 ], [ [], [], [], [], [], [] ], 'missing list options are empty');
T.eq([ e.strategy, e.qnum ], [ 'strategy-general', 200 ], 'missing scalar options take defaults');

// values as UCI delivers them (strings, lists with empty strings)
let c = C.normalize({ enabled: '1', engine: 'nfqws2', list_mode: 'blacklist', lists: [ 'a', '', 'a', 'b' ], ipsets: '',
	wan: [ '', 'wan' ], qnum: '300', desync_mark: '1073741824', clients_mode: 'exclude',
	clients: [ '192.168.1.5', 'aa-bb-cc-dd-ee-ff' ] }, { autoupdate: '0', autoupdate_hour: '23' }, { timeout: '10' });
T.eq([ c.enabled, c.engine, c.list_mode, c.lists, c.ipsets, c.wan, c.qnum, c.desync_mark ],
	[ true, 'nfqws2', 'blacklist', [ 'a', 'b' ], [], [ 'wan' ], 300, 1073741824 ], 'UCI values normalized');
T.eq([ c.clients4, c.clients_mac ], [ [ '192.168.1.5' ], [ 'aa:bb:cc:dd:ee:ff' ] ], 'clients split');
T.eq([ c.repo.autoupdate, c.repo.autoupdate_hour, c.test.timeout ], [ false, 23, 10 ], 'repo/test sections');
T.eq(c.warnings, [], 'valid values -> no warnings');

// invalid values fall back to defaults and are reported
let b = C.normalize({ engine: 'tpws', list_mode: 'all', qnum: '70000', desync_mark: '0', postnat_mark: 'x',
	tcp_pkt_out: '-1', flow_offload: 'yes', clients_mode: 'some', user: 'root;id', lists: [ 'ok', '../x', 'bad id' ],
	strategy: '../../etc/passwd', ipv6: 'maybe', wan: [ 'eth0; rm' ] }, { url: 'file:///etc/passwd', autoupdate_hour: '24' }, { concurrency: '0' });
T.has(b.warnings, 'bad_config', 'bad_config warning');
T.eq([ b.engine, b.list_mode, b.qnum, b.desync_mark, b.postnat_mark, b.tcp_pkt_out, b.flow_offload, b.clients_mode, b.user ],
	[ 'nfqws', 'whitelist', 200, 0x40000000, 0x20000000, 9, 'auto', 'all', 'daemon' ], 'invalid values replaced by defaults');
T.eq([ b.lists, b.strategy, b.ipv6, b.wan ], [ [ 'ok' ], '', false, [] ], 'invalid ids, strategy, bool and wan dropped');
T.eq([ b.repo.url, b.repo.autoupdate_hour, b.test.concurrency ], [ C.DEFAULTS.repo.url, 4, 6 ], 'invalid repo/test values');
for (let o in [ 'engine', 'list_mode', 'qnum', 'desync_mark', 'postnat_mark', 'tcp_pkt_out', 'flow_offload', 'clients_mode', 'user', 'lists', 'strategy', 'ipv6', 'wan', 'url', 'autoupdate_hour', 'concurrency' ])
	T.has(b.bad_options, o, 'bad option reported: ' + o);

// mark collisions
let m = C.normalize({ desync_mark: '0x20000000', postnat_mark: '0x20000000' }, null, null);
T.eq([ m.desync_mark, m.postnat_mark ], [ 0x40000000, 0x20000000 ], 'colliding marks reset');
T.has(m.bad_options, 'desync_mark', 'mark collision reported');
m = C.normalize({ desync_mark: '0x08000000' }, null, null);
T.has(m.bad_options, 'desync_mark', 'client mark collision reported');
m = C.normalize({ desync_mark: '0x48000000' }, null, null);
T.has(m.bad_options, 'desync_mark', 'overlapping mark bits reported');
m = C.normalize({ desync_mark: '0x10000000', postnat_mark: '0x01000000' }, null, null);
T.eq([ m.desync_mark, m.postnat_mark, m.bad_options ], [ 0x10000000, 0x01000000, [] ], 'distinct mark bits accepted');

T.eq(C.current_strategy_id(C.normalize({ engine: 'nfqws2', strategy_nfqws2: 'user-lua' }, null, null)), 'user-lua', 'current strategy for nfqws2');
T.eq([ C.strategy_option('nfqws'), C.strategy_option('nfqws2') ], [ 'strategy', 'strategy_nfqws2' ], 'strategy option names');

exit(T.finish());
