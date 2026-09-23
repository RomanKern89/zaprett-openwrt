'use strict';

// Configuration-changing commands against a sandbox UCI directory (P.uci_confdir) with the package's
// default /etc/config/zaprett. Service actions go to a non-existent init script inside the sandbox.
import * as fs from 'fs';
import * as T from 'ztest';
import { P, try_lock, unlock, NULL_CTX } from 'zaprett.util';
import * as C from 'zaprett.config';
import * as O from 'zaprett.offload';
import * as CMD from 'zaprett.commands';
import * as SRC from 'zaprett.sources';

// contract v1.2 §6.2 + v1.3 §14.4: the closed list of warning codes
const CONTRACT_WARNINGS = [ 'no_active_lists', 'no_wan', 'flow_offload_enabled', 'nft_queue_missing', 'engine_missing', 'strategy_missing',
	'no_strategy', 'generate_failed', 'bad_config', 'config_was_invalid', 'list_missing', 'source_not_downloaded', 'profile_unfiltered',
	'wide_port_range', 'empty_profile_removed', 'strategy_option_ignored', 'test_running', 'not_running', 'nft_not_applied',
	'ipv6_wan_unhandled', 'low_memory', 'monitor_degraded', 'flowtable_failed', 'game_filter_no_ipsets', 'dns_plain' ];
let enabled_of = (name) => filter(C.load_sources(), (s) => s.name == name)[0]?.enabled;

T.begin('uci');
T.selfcheck();
let W = T.sandbox('uci');

/* ---- package defaults ---- */
let cfg = C.load();
T.eq([ cfg.present, cfg.enabled, cfg.engine, cfg.strategy, cfg.list_mode, cfg.warnings ], [ true, false, 'nfqws', 'strategy-general', 'whitelist', [] ],
	'package /etc/config/zaprett parses without warnings');
T.eq([ cfg.lists, cfg.exclude_lists, cfg.ipsets, cfg.exclude_ipsets ],
	[ [ 'zaprett-youtube', 'zaprett-discord', 'user-hosts' ], [ 'zaprett-exclude', 'user-hosts-exclude' ], [], [ 'zaprett-exclude-ipset', 'user-ipset-exclude' ] ],
	'default lists (contract v1.1 §4)');
let srcs = C.load_sources();
T.eq(map(srcs, (s) => [ s.name, s.type, s.enabled, s.valid ]), [
	[ 'refilter_domains', 'list', false, true ], [ 'antifilter_allyouneed', 'ipset', false, true ],
	[ 'cloudflare_v4', 'ipset', false, true ], [ 'cloudflare_v6', 'ipset', false, true ] ], 'default subscriptions: valid and disabled');
T.eq(map(filter(srcs, (s) => substr(s.name, 0, 10) == 'cloudflare'), (s) => [ s.interval_hours, s.min_entries, s.ram_mib ]),
	[ [ 168, 5, 1 ], [ 168, 3, 1 ] ], 'cloudflare subscriptions: 168 h, min 5/3 entries, 1 MiB (contract v1.1)');
T.eq(map(C.DEFAULT_SOURCES, (x) => C.normalize_source(x.name, x.values)), srcs, 'DEFAULT_SOURCES (uci-defaults) equal the package configuration');
T.eq(cfg.deleted_sources, [], 'no deleted default subscriptions initially');
let host_cfg_before = sprintf('%J', fs.stat('/etc/config/zaprett'));

/* ---- list toggles ---- */
let r = CMD.list_toggle('list-discord', true);
T.eq([ r.ok, r.type, r.changed ], [ true, 'list', true ], 'enable list');
T.has(C.load().lists, 'list-discord', 'list stored in UCI');
T.eq(CMD.list_toggle('list-discord', true).changed, false, 'enabling twice changes nothing');
T.ok(CMD.list_toggle('list-discord', false).ok && index(C.load().lists, 'list-discord') < 0, 'disable list');
T.ok(CMD.list_toggle('list-exclude-general', true).ok && index(C.load().exclude_lists, 'list-exclude-general') >= 0, 'exclude list goes to exclude_lists');
T.eq(CMD.list_toggle('no-such', true).error, 'not_found', 'unknown list');
T.eq(CMD.list_toggle('../x', true).error, 'bad_id', 'bad id');
r = CMD.list_toggle('src-cloudflare_v4', true);
T.eq([ r.ok, r.type ], [ true, 'ipset' ], 'subscription can be enabled before download (type from UCI)');
T.has(C.load().ipsets, 'src-cloudflare_v4', 'subscription id in ipsets');
T.ok(CMD.list_toggle('src-cloudflare_v4', false).ok, 'subscription disabled again');
T.ok(r.reloaded === false, 'field reloaded (false: the engine is not running)');

// the last list can be switched off (LuCI and UCI store an empty list by deleting the option)
C.set({ lists: [ 'zaprett-youtube' ] });
r = CMD.list_toggle('zaprett-youtube', false);
T.ok(r.ok && r.changed, 'last list switched off: ' + (r.message ?? ''));
T.eq(C.load().lists, [], 'an emptied list stays empty, package defaults do not come back');
T.eq(C.normalize({ enabled: '1' }, null, null).lists, [], 'existing section without the option -> empty list');
T.eq(C.normalize(null, null, null).lists, C.DEFAULTS.main.lists, 'negative control: missing section -> package defaults');

// status: an enabled list that is not installed is reported; a subscription not downloaded yet is not
C.set({ lists: [ 'zaprett-youtube', 'no-such-list', 'src-refilter_domains' ] });
let stw = CMD.status().warnings;
T.has(stw, 'list_missing', 'status warns about a missing list');
T.eq(filter(stw, (w) => index(CONTRACT_WARNINGS, w) < 0), [], 'status warnings belong to the closed contract list: ' + join(',', stw));
C.set({ lists: [ 'zaprett-youtube', 'src-refilter_domains' ] });
T.lacks(CMD.status().warnings, 'list_missing', 'negative control: subscription not downloaded yet is not "missing"');
C.set({ lists: [ 'zaprett-youtube', 'zaprett-discord', 'user-hosts' ] });

/* ---- strategy and mode ---- */
if (T.NFQWS) {
	r = CMD.strategy_set('strategy-alt');
	T.ok(r.ok && C.load().strategy == 'strategy-alt', 'strategy set: ' + (r.message ?? ''));
	T.write(W + '/etc/user/strategies/nfqws/user-broken.txt', '--filter-tcp=443 --dpi-desync-fake-tls=${bin:not_there}\n');
	r = CMD.strategy_set('user-broken');
	T.eq([ r.error, C.load().strategy ], [ 'item_not_installed', 'strategy-alt' ], 'broken strategy is not selected');
	T.eq(CMD.strategy_set('nope').error, 'strategy_not_found', 'unknown strategy');
	r = CMD.set_mode('blacklist');
	T.ok(r.ok && C.load().list_mode == 'blacklist', 'mode blacklist');
	T.ok(CMD.set_mode('whitelist').ok, 'mode whitelist');
	// a change that breaks a working configuration is refused
	C.set({ exclude_lists: [ 'zaprett-exclude', 'user-hosts-exclude' ] });
	r = CMD.set_engine('nfqws2');
	T.eq(r.error, 'engine_missing', 'engine without binary refused');
	// an already broken configuration does not block further changes
	C.set({ lists: [ 'zaprett-youtube', 'missing-list' ] });
	r = CMD.list_toggle('zaprett-discord', true);
	T.ok(r.ok && index(r.warnings ?? [], 'config_was_invalid') >= 0, 'change allowed on an already broken configuration');
	C.set({ lists: [ 'zaprett-youtube', 'zaprett-discord', 'user-hosts' ] });
}
else
	print('SKIP [uci] nfqws binary not given: strategy/mode checks skipped\n');
T.eq(CMD.set_mode('all').error, 'bad_value', 'bad mode');

/* ---- wizard ---- */
fs.writefile(P.presets, sprintf('%J', {
	schema: 1,
	services: [
		{ id: 'youtube', lists: [ 'zaprett-youtube' ], ipsets: [], sources: [], works: 'yes' },
		{ id: 'discord', lists: [ 'zaprett-discord' ], ipsets: [], sources: [], works: 'yes' },
		{ id: 'blocked', lists: [ 'list-general' ], ipsets: [], sources: [], works: 'no' },
		{ id: 'cf', lists: [], ipsets: [], sources: [ 'cloudflare_v4' ], works: 'partial' },
		{ id: 'cfboth', lists: [], ipsets: [], sources: [ 'cloudflare_v4', 'cloudflare_v6' ], works: 'partial' },
		{ id: 'broken', lists: [ 'no-such-list' ], ipsets: [], sources: [], works: 'yes' }
	],
	always: { exclude_lists: [ 'zaprett-exclude' ], exclude_ipsets: [ 'zaprett-exclude-ipset' ] },
	defaults: { strategy: 'strategy-general' }
}));
C.set({ exclude_lists: [ 'user-hosts-exclude' ], exclude_ipsets: [] });
if (T.NFQWS) {
	r = CMD.wizard_apply([ 'youtube', 'blocked' ]);
	T.ok(r.ok, 'wizard apply: ' + (r.message ?? ''));
	T.eq(r.skipped, [ { id: 'blocked', reason: 'works_no' } ], 'service with works=no skipped');
	let c = C.load();
	T.eq([ c.lists, c.list_mode ], [ [ 'zaprett-youtube', 'user-hosts' ], 'whitelist' ], 'lists of unselected preset services removed, user lists kept');
	T.eq([ c.exclude_lists, c.exclude_ipsets ], [ [ 'user-hosts-exclude', 'zaprett-exclude' ], [ 'zaprett-exclude-ipset' ] ], '"always" exclusions added');
	r = CMD.wizard_apply([ 'youtube', 'discord' ]);
	T.eq(C.load().lists, [ 'zaprett-youtube', 'user-hosts', 'zaprett-discord' ], 'second wizard run adds without duplicates');
	// the job lock is held so that the side job `sources-update` is refused instead of being spawned
	let jl = try_lock(P.lock_job);
	r = CMD.wizard_apply([ 'cf' ]);
	T.ok(r.ok && index(C.load().ipsets, 'src-cloudflare_v4') >= 0, 'wizard enables subscription id: ' + (r.message ?? ''));
	T.eq(enabled_of('cloudflare_v4'), true, 'wizard switches the subscription on');
	T.eq(r.sources, [ 'cloudflare_v4' ], 'wizard reports subscriptions to download');
	T.eq(C.load().lists, [ 'user-hosts' ], 'lists of unselected services removed');
	T.eq([ r.job, r.job_error?.code ], [ null, 'job_busy' ], 'side job refused -> job_error {code, message}');
	T.ok(type(r.job_error?.message) == 'string' && length(r.job_error.message) > 0, 'job_error carries a message');
	T.lacks(r.warnings, 'job_busy', 'job errors are not put into warnings');
	T.eq(filter(r.warnings, (w) => index(CONTRACT_WARNINGS, w) < 0), [], 'wizard warnings belong to the closed contract list');

	r = CMD.wizard_apply([ 'cfboth' ]);
	T.ok(r.ok && enabled_of('cloudflare_v4') && enabled_of('cloudflare_v6'), 'both subscriptions of the service switched on');
	r = CMD.wizard_apply([ 'cf' ]);
	T.eq([ enabled_of('cloudflare_v4'), enabled_of('cloudflare_v6'), r.sources_disabled ], [ true, false, [ 'cloudflare_v6' ] ],
		'subscription of a removed service switched off; the one still used by a selected service stays on');
	T.ok(index(C.load().ipsets, 'src-cloudflare_v6') < 0 && index(C.load().ipsets, 'src-cloudflare_v4') >= 0, 'subscription ids follow');
	r = CMD.wizard_apply([ 'youtube' ]);
	T.eq([ enabled_of('cloudflare_v4'), enabled_of('cloudflare_v6'), r.sources_disabled, r.job_error ], [ false, false, [ 'cloudflare_v4' ], null ],
		'no selected service uses the subscriptions: all off, no side job');
	unlock(jl);
}
T.eq(CMD.wizard_apply([ 'blocked' ]).error, 'preset_unavailable', 'only works=no services -> nothing to do');
T.eq(CMD.wizard_apply([ 'broken' ]).error, 'preset_item_missing', 'missing preset list');
T.eq(CMD.wizard_apply([ 'nope' ]).error, 'unknown_service', 'unknown service');

/* ---- subscriptions ---- */
r = CMD.sources_save('mylist', '{"title":"Мой список","type":"list","url":"https://example.org/x.txt","interval_hours":24,"min_entries":5,"enabled":true}');
T.ok(r.ok && r.item_id == 'src-mylist', 'sources save: ' + (r.message ?? ''));
let my = filter(C.load_sources(), (s) => s.name == 'mylist')[0];
T.eq([ my?.title, my?.type, my?.interval_hours, my?.min_entries, my?.enabled, my?.valid ], [ 'Мой список', 'list', 24, 5, true, true ], 'saved subscription fields');
T.eq(CMD.sources_save('mylist', '{"url":"http://example.org/x.txt"}').error, 'bad_value', 'http url refused');
T.eq(CMD.sources_save('Bad-Name', '{"type":"list","url":"https://a.b/"}').error, 'bad_id', 'bad subscription name');
T.eq(CMD.sources_save('mylist', '[1,2]').error, 'bad_value', 'JSON must be an object');
T.eq(CMD.sources_save('mylist', '{"interval_hours":0}').error, 'bad_value', 'interval range checked');
T.eq(CMD.sources_save('main', '{"type":"list","url":"https://a.b/"}').error, 'uci_failed', 'name of another section refused');
// type change removes the downloaded item and the id from the old UCI option
T.ok(CMD.list_toggle('src-mylist', true).ok && index(C.load().lists, 'src-mylist') >= 0, 'subscription enabled as list');
T.write(W + '/etc/files/lists/include/src-mylist.txt', 'a.com\n');
T.write(W + '/etc/manifests/lists/include/src-mylist.json', '{"schema":1,"id":"src-mylist","type":"list","file":"src-mylist.txt","source":"url","version":"2026.09.17"}');
r = CMD.sources_save('mylist', '{"type":"ipset"}');
T.ok(r.ok && r.type == 'ipset' && r.type_changed && r.reloaded === false, 'type changed');
T.ok(index(C.load().lists, 'src-mylist') < 0, 'old list option cleaned');
T.has(C.load().ipsets, 'src-mylist', 'id moved to the option of the new type: the subscription stays switched on');
T.ok(fs.stat(W + '/etc/files/lists/include/src-mylist.txt') == null, 'old downloaded file removed');
r = CMD.sources_list();
T.eq(filter(r.sources, (s) => s.name == 'mylist')[0]?.status, 'never', 'sources list: never downloaded');
T.eq(CMD.list_toggle('src-mylist', true).changed, false, 'already switched on as ipset');
// negative control: a type change does not switch on a subscription that was off
T.ok(CMD.sources_save('other', '{"type":"list","url":"https://example.org/o.txt"}').ok, 'second subscription saved');
r = CMD.sources_save('other', '{"type":"ipset_exclude"}');
T.ok(r.ok && r.type_changed && index(C.load().exclude_ipsets, 'src-other') < 0 && index(C.load().lists, 'src-other') < 0,
	'type change of a switched-off subscription adds it nowhere');
T.ok(CMD.sources_delete('other').ok, 'second subscription deleted');

// a new URL forgets the download state (next update downloads at once); other changes keep it
fs.writefile(SRC.state_path(), sprintf('%J', { mylist: { last_update: time(), sha256: sprintf('%064d', 0), status: 'ok' } }));
r = CMD.sources_save('mylist', '{"title":"Новое имя"}');
T.ok(r.ok && !r.url_changed && json(fs.readfile(SRC.state_path())).mylist?.last_update != null, 'negative control: title change keeps the state');
r = CMD.sources_save('mylist', '{"url":"https://example.org/y.txt"}');
T.ok(r.ok && r.url_changed && json(fs.readfile(SRC.state_path())).mylist == null, 'URL change resets last_update and sha256');

// subscription changes wait for no job: a running job (update, upgrade, test) makes them fail with job_busy
let jlock = try_lock(P.lock_job);
T.eq(CMD.sources_save('mylist', '{"title":"Во время задачи"}').error, 'job_busy', 'sources save refused while a job holds the lock');
T.ok(filter(C.load_sources(), (s) => s.name == 'mylist')[0]?.title != 'Во время задачи', 'nothing saved while busy');
T.eq(CMD.sources_delete('mylist').error, 'job_busy', 'sources delete refused while a job holds the lock');
T.eq(length(filter(C.load_sources(), (s) => s.name == 'mylist')), 1, 'nothing deleted while busy');
unlock(jlock);
T.ok(CMD.sources_save('mylist', '{"title":"После задачи"}').ok, 'negative control: saved once the lock is free');

r = CMD.sources_delete('mylist');
T.ok(r.ok, 'sources delete');
T.ok(!length(filter(C.load_sources(), (s) => s.name == 'mylist')) && index(C.load().ipsets, 'src-mylist') < 0, 'section and ids removed');
T.eq(C.load().deleted_sources, [], 'deleting an own subscription leaves deleted_sources alone');
T.eq(CMD.sources_delete('mylist').error, 'not_found', 'delete twice');
T.eq(CMD.sources_delete('main').error, 'not_found', 'main section is not a subscription');
T.eq(C.load().present, true, 'main section survives');

// default subscriptions: deleted ones are remembered, missing ones are restored by uci-defaults
T.ok(CMD.sources_delete('cloudflare_v6').ok, 'default subscription deleted');
T.eq(C.load().deleted_sources, [ 'cloudflare_v6' ], 'deletion remembered in main.deleted_sources');
C.delete_source('refilter_domains');	// a kept configuration from an older package without this section
r = CMD.sources_defaults();
T.eq([ r.ok, r.added ], [ true, [ 'refilter_domains' ] ], 'uci-defaults restores the missing default subscription, not the deleted one');
let rf = filter(C.load_sources(), (s) => s.name == 'refilter_domains')[0];
T.eq([ rf?.type, rf?.interval_hours, rf?.min_entries, rf?.ram_mib, rf?.enabled, rf?.valid ], [ 'list', 72, 1000, 9, false, true ], 'restored with package values');
T.eq(CMD.sources_defaults().added, [], 'second run adds nothing');
C.set({ deleted_sources: [] });
T.eq(CMD.sources_defaults().added, [ 'cloudflare_v6' ], 'negative control: without the mark the deleted subscription comes back');
T.ok(CMD.sources_delete('cloudflare_v6').ok && CMD.sources_delete('cloudflare_v6').error == 'not_found', 'deleted again');
r = CMD.sources_save('cloudflare_v6', '{"type":"ipset","url":"https://www.cloudflare.com/ips-v6"}');
T.ok(r.ok && r.created && index(C.load().deleted_sources, 'cloudflare_v6') < 0, 'creating it again by hand clears the mark');

/* ---- offload (sandbox firewall config, firewall reload goes to a missing script) ---- */
let oc = C.normalize({ flow_offload: 'auto' }, null, null);
r = O.apply(oc);
T.eq([ r.ok, r.changed ], [ true, true ], 'offload switched off');
let d = C.fw_defaults_section();
T.eq([ d.flow_offloading, d.flow_offloading_hw ], [ '0', '0' ], 'firewall defaults changed in UCI');
T.ok(fs.stat(W + '/etc/offload-saved.json')?.type == 'file', 'original values saved');
T.eq(O.apply(oc).changed, false, 'second apply changes nothing');
r = O.restore();
d = C.fw_defaults_section();
T.eq([ r.changed, d.flow_offloading, d.flow_offloading_hw ], [ true, '1', '1' ], 'offload restored');
T.ok(fs.stat(W + '/etc/offload-saved.json') == null, 'saved values removed');
T.eq(O.apply(C.normalize({ flow_offload: 'keep' }, null, null)).changed, false, 'mode keep leaves offload alone');
O.apply(oc);
C.fw_set_offload({ flow_offloading: '1' });
r = O.restore();
T.eq([ r.changed, fs.stat(W + '/etc/offload-saved.json') ], [ false, null ], 'manual change wins over restore');

/* ---- cron ---- */
let norepo = { repo: { autoupdate: false } };
T.ok(!CMD.cron_needed(norepo, [ { enabled: false, valid: true } ]), 'no repo autoupdate and no enabled subscription -> no cron line');
T.ok(CMD.cron_needed(norepo, [ { enabled: false, valid: true }, { enabled: true, valid: true } ]), 'an enabled subscription needs the cron line');
T.ok(!CMD.cron_needed(norepo, [ { enabled: true, valid: false } ]), 'an invalid subscription does not');
T.ok(CMD.cron_needed({ repo: { autoupdate: true } }, []), 'repo autoupdate needs it');
for (let s in C.load_sources())
	C.set_source(s.name, { enabled: '0' });
fs.writefile(P.crontab, '0 3 * * * /bin/true\n');
r = CMD.cron_sync();
let ct = fs.readfile(P.crontab);
T.ok(r.ok && r.changed && match(ct, /^0 3 \* \* \* \/bin\/true\n[0-9]{1,2} 4 \* \* \* \/usr\/bin\/zaprett repo upgrade --all --foreground --quiet # zaprett-autoupdate\n$/), 'cron line added: ' + ct);
T.eq(CMD.cron_sync().changed, false, 'cron sync idempotent');
T.eq(fs.stat(P.crontab).mode & 511, 384, 'crontab mode 0600');
C.set({ autoupdate: '0' }, 'repo');
C.set_source('cloudflare_v4', { enabled: '1' });
r = CMD.cron_sync();
T.ok(r.ok && !r.changed && index(fs.readfile(P.crontab), CMD.CRON_MARK) >= 0, 'cron line kept for an enabled subscription with repo autoupdate off');
C.set_source('cloudflare_v4', { enabled: '0' });
T.ok(CMD.cron_sync().changed && fs.readfile(P.crontab) == '0 3 * * * /bin/true\n', 'cron line removed when nothing needs it');

// the cron job itself: subscriptions by their intervals, repository items only with repo.autoupdate=1
r = CMD.JOB_HANDLERS.autoupdate(NULL_CTX, [], {});
T.eq([ r.ok, r.repo?.skipped, r.sources?.ok ], [ true, true, true ], 'autoupdate job with repo.autoupdate=0: repository step skipped, subscriptions step ran');

T.eq(sprintf('%J', fs.stat('/etc/config/zaprett')), host_cfg_before, 'host /etc/config/zaprett untouched (all UCI writes went to the sandbox)');

exit(T.finish());
