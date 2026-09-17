'use strict';

import * as fs from 'fs';
import * as T from 'ztest';
import { P } from 'zaprett.util';
import * as C from 'zaprett.config';
import * as S from 'zaprett.store';

T.begin('store');
T.selfcheck();
let W = T.sandbox('store');

T.eq(S.dep_to_id('https://raw.githubusercontent.com/CherretGit/zaprett-repo/refs/heads/main/manifests/bin/quic_initial_www_google_com.json'),
	'quic_initial_www_google_com', 'dependency URL -> id');
T.eq(S.dep_to_id('tls_clienthello_vk_com'), 'tls_clienthello_vk_com', 'dependency id kept');
T.ok(S.dep_to_id('https://x.org/manifests/../evil json.json') == null, 'bad dependency rejected');
T.ok(S.path_under('/etc/zaprett/files/lists/include/a.txt', '/etc/zaprett/files'), 'path under root');
for (let p in [ '/etc/zaprett/files', '/etc/zaprett/filesX/a', '/etc/zaprett/files/../x', '/etc/zaprett/files//a', '/etc/shadow', '/etc/zaprett/files/./a' ])
	T.ok(!S.path_under(p, '/etc/zaprett/files'), 'path not under root: ' + p);

let idx = S.scan();
let errs = {};
for (let e in idx.errors)
	errs[fs.basename(e.path)] = e.error;
T.eq(errs, {
	'broken-json.json': 'bad_json',
	'list-dotdot.json': 'file_outside_root',
	'list-evil.json': 'file_outside_root',
	'list-mismatch.json': 'id_mismatch',
	'list-nofile.json': 'file_missing',
	'list-noschema.json': 'bad_schema',
	'list-nosha.json': 'no_sha256',
	'list-wrongtype.json': 'type_mismatch'
}, 'broken manifests rejected with reasons');
let nulls = idx.items.list['list-nulls'];
T.eq([ nulls?.name, nulls?.version, nulls?.dependencies, nulls?.manifest_url, nulls?.installed_at ], [ 'list-nulls', '0', [], null, null ],
	'manifest with null/absent optional fields accepted (bundle format)');
T.eq([ idx.items.list['src-demo']?.source, idx.items.list['src-demo']?.name ], [ 'url', 'Demo subscription' ], 'subscription item source url');
T.eq(idx.items.list['zaprett-youtube']?.source, 'bundle', 'fixed bundle id present');
for (let bad in [ 'list-evil', 'list-other', 'list-mismatch', 'list-nofile', 'list-dotdot' ])
	T.ok(idx.items.list[bad] == null, 'rejected item absent: ' + bad);

let yt = idx.items.list['list-youtube'];
T.eq([ yt.source, yt.name, yt.version, yt.file ], [ 'repo', 'YouTube (repo)', '1.0.1', W + '/etc/files/lists/include/list-youtube.txt' ], '/etc/zaprett overrides bundle');
T.eq(idx.items.list['list-discord'].source, 'bundle', 'bundle item');
T.eq(length(keys(idx.items.nfqws)), 65, '64 bundle strategies + 1 user strategy');
T.eq(length(keys(idx.items.bin)), 6, '6 bin items');
T.eq(idx.items.nfqws['strategy-general'].dependencies, [ 'quic_initial_www_google_com' ], 'manifest dependency URLs converted to ids');
T.eq(idx.items.nfqws['user-test'].source, 'user', 'user strategy');
T.ok(idx.items.nfqws['not-user'] == null, 'user strategy without prefix ignored');
T.eq(idx.items.list['user-hosts'].file, W + '/etc/user/hosts-include.txt', 'virtual user list');
T.eq(idx.items.ipset_exclude['user-ipset-exclude'].type, 'ipset_exclude', 'virtual user ipset exclude');

T.eq(S.entries_of(yt), 4, 'entries of text list (comments and blank lines skipped)');
T.ok(S.is_gzip(idx.items.list['list-gz'].file), 'gzip detected by magic bytes');
T.ok(!S.is_gzip(yt.file), 'plain text is not gzip');
T.ok(S.entries_of(idx.items.list['list-gz']) == null, 'gzip list entries unknown');
T.ok(S.entries_of(idx.items.nfqws['strategy-general']) == null, 'strategies have no entry count');
S.flush_cache();
T.ok(fs.stat(P.run + '/cache/entries.json')?.type == 'file', 'entries cache written');
// cache must notice file changes (size/mtime)
fs.writefile(yt.file, 'a.com\nb.com\n');
T.eq(S.entries_of(yt), 2, 'entries cache invalidated by file change');

T.has(S.used_by(idx, 'quic_initial_www_google_com'), 'strategy-general', 'used_by');
T.eq(S.used_by(idx, 'list-youtube'), [], 'used_by empty for lists');

let cfg = C.normalize({ lists: [ 'list-youtube' ], strategy: 'strategy-general' }, null, null);
T.ok(S.is_active(yt, cfg, idx), 'active list');
T.ok(!S.is_active(idx.items.list['list-discord'], cfg, idx), 'inactive list');
T.ok(S.is_active(idx.items.nfqws['strategy-general'], cfg, idx), 'active strategy');
T.ok(S.is_active(idx.items.bin['quic_initial_www_google_com'], cfg, idx), 'bin used by active strategy is active');
T.ok(!S.is_active(idx.items.bin['tls_clienthello_vk_com'], cfg, idx), 'unused bin inactive');

let d = S.describe(idx, cfg, 'list');
T.ok(length(d) > 0 && length(filter(d, (x) => x.type != 'list')) == 0, 'describe filters by type');
let dyt = filter(d, (x) => x.id == 'list-youtube')[0];
T.eq([ dyt.active, dyt.entries, dyt.source, dyt.supported ], [ true, 2, 'repo', true ], 'describe fields');

T.ok(S.bundle_item('bin', 'quic_initial_www_google_com') != null, 'bundle_item found');
T.ok(S.bundle_item('list', 'list-youtube')?.source == 'bundle', 'bundle_item ignores /etc override');
T.ok(S.bundle_item('list', 'list-evil') == null, 'bundle_item missing');
T.eq(S.user_list_path('user-ipset'), W + '/etc/user/ipset-include.txt', 'user list path');
T.ok(S.user_list_path('list-youtube') == null, 'not a user list');

// manifest with an absolute path inside /etc/zaprett/files is accepted
let SHA = sprintf('%064d', 0);
let abs = S.parse_manifest({ schema: 1, id: 'x1', file: W + '/etc/files/lists/include/list-youtube.txt', version: '1', sha256: SHA }, 'list', 'repo', 'x1', W + '/etc');
T.ok(abs.error == null && abs.file == W + '/etc/files/lists/include/list-youtube.txt', 'absolute path inside root accepted');
T.eq(S.parse_manifest({ schema: 1, id: 'x1', file: W + '/bundle/files/bin/quic_initial_www_google_com.bin', sha256: SHA }, 'list', 'repo', 'x1', W + '/etc').error,
	'file_outside_root', 'absolute path of another root rejected');
T.eq(S.parse_manifest({ schema: 1, id: 'x1', file: 'a.txt', version: 'bad version!', sha256: SHA }, 'list', 'repo', 'x1', W + '/etc').version, '0', 'bad version normalized');
T.eq(S.parse_manifest({ schema: 1, id: 'x1', file: 'a.txt', sha256: 'XYZ' }, 'list', 'repo', 'x1', W + '/etc').error, 'no_sha256', 'bad sha256 rejected');
T.eq(S.parse_manifest({ schema: 2, id: 'x1', file: 'a.txt', sha256: SHA }, 'list', 'repo', 'x1', W + '/etc').error, 'bad_schema', 'unknown schema rejected');

exit(T.finish());
