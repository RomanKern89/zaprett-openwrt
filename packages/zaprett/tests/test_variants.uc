'use strict';

// Variants of preset services in the wizard (contract v1.7 §16.4): `wizard apply <service>[:<variant>]`,
// `presets` -> enabled_variant, low_memory of heavy variants, variants in service_active.
import * as fs from 'fs';
import * as T from 'ztest';
import { P, set_paths, try_lock, unlock } from 'zaprett.util';
import * as C from 'zaprett.config';
import * as CMD from 'zaprett.commands';
import * as TS from 'zaprett.tester';

T.begin('variants');
T.selfcheck();
let W = T.sandbox('variants');
set_paths({ meminfo: W + '/meminfo' });
let mem = (mib) => T.write(W + '/meminfo', sprintf('MemTotal:         %d kB\nMemAvailable:     %d kB\n', mib * 1024, mib * 512));
mem(512);

const PRESETS = {
	schema: 1,
	services: [
		{ id: 'youtube', lists: [ 'zaprett-youtube' ], ipsets: [], sources: [], works: 'yes', tier: 'light',
			variants: [
				{ id: 'full', name: 'Расширенный', name_en: 'Extended', description: 'Больше', description_en: 'More', lists: [ 'list-youtube' ], ipsets: [], tier: 'light' },
				{ id: 'heavy', lists: [ 'list-general' ], ipsets: [], tier: 'full' }
			] },
		{ id: 'discord', lists: [ 'zaprett-discord' ], ipsets: [], sources: [], works: 'yes', tier: 'light',
			variants: [
				{ id: 'voice', lists: [ 'zaprett-discord' ], ipsets: [ 'ipset-roblox' ] },
				{ id: 'full', lists: [ 'list-discord' ], ipsets: [] }
			] },
		{ id: 'cf', lists: [], ipsets: [], sources: [ 'cloudflare_v4' ], works: 'partial',
			variants: [ { id: 'bundled', lists: [], ipsets: [ 'ipset-roblox' ] } ] },
		{ id: 'withuser', lists: [ 'list-general' ], ipsets: [], sources: [], works: 'yes',
			variants: [ { id: 'u', lists: [ 'user-hosts' ], ipsets: [] } ] },
		{ id: 'brokenv', lists: [ 'zaprett-youtube' ], ipsets: [], sources: [], works: 'yes',
			variants: [ { id: 'x', lists: [ 'no-such-list' ], ipsets: [] } ] },
		{ id: 'blocked', lists: [ 'list-general' ], ipsets: [], sources: [], works: 'no',
			variants: [ { id: 'full', lists: [ 'list-discord' ], ipsets: [] } ] },
		{ id: 'weird', lists: [ 'zaprett-youtube' ], tier: 'full',
			variants: [ { id: '../x', lists: [ 'list-discord' ] }, 'str', { id: 'full', lists: [ 'list-general' ] }, { id: 'full', lists: [ 'list-youtube' ] } ] }
	],
	always: { exclude_lists: [ 'zaprett-exclude' ], exclude_ipsets: [ 'zaprett-exclude-ipset' ] },
	tiers: { light: { min_ram_mib: 0 }, full: { min_ram_mib: 200 } },
	defaults: { strategy: 'strategy-general' }
};
fs.writefile(P.presets, sprintf('%J', PRESETS));
let svc = (id) => filter(PRESETS.services, (s) => s.id == id)[0];
let cfg_of = (lists, ipsets) => ({ lists: lists, ipsets: ipsets, exclude_lists: [], exclude_ipsets: [] });

/* ---- pure: argument parsing ---- */
T.eq(CMD.parse_service_ref('youtube'), { id: 'youtube', variant: null }, 'service without variant');
T.eq(CMD.parse_service_ref('discord:voice'), { id: 'discord', variant: 'voice' }, 'service with variant');
for (let bad in [ 'a:b:c', 'youtube:', ':full', '../x', '.hidden', 'you tube', 'youtube:fu ll', 'youtube:.x', '', null, 5,
	'a' + join('', map(split(sprintf('%96s', ''), ''), () => 'b')) ])
	T.eq(CMD.parse_service_ref(bad), null, sprintf('negative control: bad reference %J', bad));

/* ---- pure: item sets ---- */
let sets = CMD.service_sets(svc('weird'));
T.eq(map(sets, (x) => x.id), [ null, 'full' ], 'bad and repeated variant ids ignored, the first one of a repeated id kept');
T.eq([ sets[1].lists, sets[1].tier, sets[0].tier ], [ [ 'list-general' ], 'full', 'full' ], 'variant without tier inherits the tier of the service');
T.eq(CMD.service_sets(svc('youtube'))[2].tier, 'full', 'own tier of a variant');
T.eq(map(CMD.service_sets({ id: 'x', lists: [ 'a' ] }), (x) => x.id), [ null ], 'service without variants: only the core set');

/* ---- pure: enabled_variant ---- */
let d = svc('discord');
T.eq(CMD.enabled_variant(d, cfg_of([ 'zaprett-discord' ], [ 'ipset-roblox' ]), {}), 'voice', 'voice variant: all its items on');
T.eq(CMD.enabled_variant(d, cfg_of([ 'zaprett-discord' ], []), {}), null, 'negative control: only the core list -> null');
T.eq(CMD.enabled_variant(d, cfg_of([ 'list-discord' ], []), {}), 'full', 'full variant on');
T.eq(CMD.enabled_variant(d, cfg_of([ 'zaprett-discord', 'list-discord' ], [ 'ipset-roblox' ]), {}), 'voice', 'two variants on: the larger one wins');
T.eq(CMD.enabled_variant({ id: 't', lists: [], variants: [ { id: 'a', lists: [ 'x' ] }, { id: 'b', lists: [ 'y' ] } ] }, cfg_of([ 'y', 'x' ], []), {}), 'a',
	'tie: the first variant wins');
T.eq(CMD.enabled_variant(svc('cf'), cfg_of([], [ 'src-cloudflare_v4' ]), { cloudflare_v4: { name: 'cloudflare_v4', type: 'ipset', enabled: true } }), null,
	'core set on through a subscription: no variant');
T.eq(CMD.enabled_variant({ id: 't', variants: [ { id: 'e', lists: [] } ] }, cfg_of([], []), {}), null, 'negative control: an empty variant is never "on"');

/* ---- pure: service_active sees variants ---- */
T.eq(TS.service_active(svc('youtube'), cfg_of([ 'list-youtube' ], [])), true, 'service is on through the list of its variant');
T.eq(TS.service_active(svc('discord'), cfg_of([], [ 'ipset-roblox' ])), true, 'service is on through the ipset of its variant');
T.eq(TS.service_active(svc('youtube'), cfg_of([ 'list-discord' ], [])), false, 'negative control: list of another service');
T.eq(map(TS.preset_services({ services: [ svc('youtube'), svc('discord') ] }, cfg_of([ 'list-youtube' ], []), null).services, (s) => s.id), [ 'youtube' ],
	'probe/tester check the service whose variant is on');

/* ---- pure: memory_heavy with heavy variants ---- */
let yt = svc('youtube');
let pr1 = { tiers: PRESETS.tiers, services: [ yt ] };
T.eq(CMD.memory_heavy(cfg_of([ 'list-general' ], []), pr1, [], 128, 100), true, 'variant of tier full on a 128 MiB router -> low_memory');
T.eq(CMD.memory_heavy(cfg_of([ 'list-general' ], []), pr1, [], 512, 400), false, 'negative control: the same on 512 MiB');
T.eq(CMD.memory_heavy(cfg_of([ 'list-youtube' ], []), pr1, [], 128, 100), false, 'negative control: light variant on a small router');
T.eq(CMD.memory_heavy(cfg_of([ 'zaprett-youtube' ], []), pr1, [], 128, 100), false, 'negative control: light core set on a small router');
T.eq(CMD.memory_heavy(cfg_of([ 'zaprett-youtube' ], []), { tiers: PRESETS.tiers, services: [ svc('weird') ] }, [], 128, 100), true,
	'core set of a service of tier full still counts');

/* ---- presets ---- */
C.set({ lists: [ 'list-youtube', 'zaprett-discord', 'user-hosts' ], ipsets: [ 'ipset-roblox' ] });
let pr = CMD.presets();
T.ok(pr.ok, 'presets: ' + (pr.message ?? ''));
let ps = (id) => filter(pr.services, (s) => s.id == id)[0];
T.eq([ ps('youtube').enabled, ps('youtube').enabled_variant ], [ false, 'full' ], 'presets: youtube runs the full variant');
T.eq([ ps('discord').enabled, ps('discord').enabled_variant ], [ true, 'voice' ], 'presets: discord core and voice on -> voice');
T.eq(map(ps('youtube').variants, (v) => [ v.id, v.enabled, v.available, v.tier ]), [ [ 'full', true, true, 'light' ], [ 'heavy', false, true, 'full' ] ],
	'presets: variant fields');
T.eq([ ps('youtube').variants[0].name, ps('youtube').variants[0].name_en, ps('youtube').variants[0].description_en ], [ 'Расширенный', 'Extended', 'More' ],
	'presets: variant names and descriptions');
T.eq(ps('brokenv').variants[0].available, false, 'negative control: variant with a missing list is not available');
T.eq(ps('blocked').variants[0].available, false, 'negative control: variant of a works=no service is not available');
T.eq(ps('cf').enabled_variant, 'bundled', 'presets: variant with an ipset only');
T.eq(ps('withuser').enabled_variant, 'u', 'presets: variant of a user list');
T.eq(ps('brokenv').enabled_variant, null, 'negative control: variant with a missing list is not on');

/* ---- wizard ---- */
C.set({ lists: [ 'zaprett-youtube', 'zaprett-discord', 'user-hosts' ], ipsets: [], exclude_lists: [ 'user-hosts-exclude' ], exclude_ipsets: [] });
let snap = () => { let c = C.load(); return [ c.lists, c.ipsets, c.exclude_lists, c.exclude_ipsets ]; };
let before = snap();
T.eq(CMD.wizard_apply([ 'youtube:nope' ]).error, 'unknown_variant', 'unknown variant');
T.eq(CMD.wizard_apply([ 'youtube', 'discord:nope' ]).error, 'unknown_variant', 'unknown variant after a valid service');
T.eq(CMD.wizard_apply([ 'nope:full' ]).error, 'unknown_service', 'unknown service with a variant');
T.eq(CMD.wizard_apply([ 'blocked:nope' ]).error, 'unknown_variant', 'unknown variant of a works=no service');
T.eq(CMD.wizard_apply([ 'youtube:full', 'youtube' ]).error, 'bad_args', 'one service with two different variants');
T.eq(CMD.wizard_apply([ 'a:b:c' ]).error, 'bad_args', 'malformed reference');
T.eq(CMD.wizard_apply([ 'blocked:full' ]).error, 'preset_unavailable', 'variant of a works=no service is skipped');
T.eq(CMD.wizard_apply([ 'brokenv:x' ]).error, 'preset_item_missing', 'variant with a missing list');
T.eq(snap(), before, 'refused wizard runs changed nothing');

if (T.NFQWS) {
	let r = CMD.wizard_apply([ 'youtube:full', 'discord' ]);
	T.ok(r.ok, 'wizard youtube:full discord: ' + (r.message ?? ''));
	T.eq([ r.services, r.variants ], [ [ 'youtube', 'discord' ], { youtube: 'full', discord: null } ], 'result: services and chosen variants');
	T.eq(C.load().lists, [ 'user-hosts', 'list-youtube', 'zaprett-discord' ], 'core list of youtube replaced by its full variant');
	T.eq(CMD.presets().services[0].enabled_variant, 'full', 'presets after the wizard: enabled_variant');
	T.lacks(r.warnings, 'low_memory', 'negative control: light variant, 512 MiB -> no low_memory');

	r = CMD.wizard_apply([ 'youtube', 'discord' ]);
	T.eq(C.load().lists, [ 'user-hosts', 'zaprett-youtube', 'zaprett-discord' ], 'back to the core set: the full list is switched off');
	r = CMD.wizard_apply([ 'youtube:full', 'discord' ]);
	r = CMD.wizard_apply([ 'youtube', 'discord' ]);
	T.eq(C.load().lists, [ 'user-hosts', 'zaprett-youtube', 'zaprett-discord' ], 'core -> full -> core leaves no extra lists');
	T.eq(CMD.wizard_apply([ 'youtube', 'youtube' ]).services, [ 'youtube' ], 'the same reference twice is taken once');

	r = CMD.wizard_apply([ 'youtube:full', 'discord:voice' ]);
	T.eq([ C.load().lists, C.load().ipsets ], [ [ 'user-hosts', 'list-youtube', 'zaprett-discord' ], [ 'ipset-roblox' ] ], 'voice variant: its list and ipset');
	r = CMD.wizard_apply([ 'youtube', 'discord:voice' ]);
	T.eq([ C.load().lists, C.load().ipsets ], [ [ 'user-hosts', 'zaprett-youtube', 'zaprett-discord' ], [ 'ipset-roblox' ] ],
		'a variant change of youtube does not touch the lists of discord');
	r = CMD.wizard_apply([ 'youtube', 'discord:full' ]);
	T.eq([ C.load().lists, C.load().ipsets ], [ [ 'user-hosts', 'zaprett-youtube', 'list-discord' ], [] ], 'voice -> full: the voice ipset and core list off');

	// user lists are never switched off, even when a preset (wrongly) names one
	C.set({ lists: [ 'zaprett-youtube', 'user-hosts' ] });
	r = CMD.wizard_apply([ 'youtube' ]);
	T.ok(r.ok && index(C.load().lists, 'user-hosts') >= 0, 'user list of a set that is not chosen stays on');
	r = CMD.wizard_apply([ 'withuser' ]);
	T.eq(C.load().lists, [ 'user-hosts', 'list-general' ], 'user list stays when its service runs the core set');

	// subscription of the core set vs a bundled variant; the job lock refuses the side download job
	let jl = try_lock(P.lock_job);
	r = CMD.wizard_apply([ 'cf', 'discord:voice' ]);
	T.ok(r.ok && index(C.load().ipsets, 'src-cloudflare_v4') >= 0, 'cf core set: subscription item on: ' + (r.message ?? ''));
	T.eq(filter(C.load_sources(), (s) => s.name == 'cloudflare_v4')[0]?.enabled, true, 'subscription switched on');
	r = CMD.wizard_apply([ 'cf:bundled', 'discord:voice' ]);
	T.eq([ C.load().ipsets, r.sources_disabled, r.sources, r.variants ], [ [ 'ipset-roblox' ], [ 'cloudflare_v4' ], [], { cf: 'bundled', discord: 'voice' } ],
		'bundled variant: subscription item and subscription off, the shared ipset stays');
	T.eq(filter(C.load_sources(), (s) => s.name == 'cloudflare_v4')[0]?.enabled, false, 'subscription switched off');
	r = CMD.wizard_apply([ 'cf:bundled', 'discord' ]);
	T.eq(C.load().ipsets, [ 'ipset-roblox' ], 'ipset shared by two chosen sets stays when one of them is left');
	r = CMD.wizard_apply([ 'cf', 'discord' ]);
	T.eq(C.load().ipsets, [ 'src-cloudflare_v4' ], 'no chosen set uses the ipset any more: switched off');
	unlock(jl);

	// heavy variant on a small router: applied with low_memory (contract v1.7 §16.4)
	mem(128);
	r = CMD.wizard_apply([ 'youtube:heavy' ]);
	T.ok(r.ok && index(r.warnings, 'low_memory') >= 0, 'variant of tier full on 128 MiB -> low_memory');
	r = CMD.wizard_apply([ 'youtube:full' ]);
	T.ok(r.ok && index(r.warnings, 'low_memory') < 0, 'negative control: light variant on 128 MiB');
	mem(512);
	r = CMD.wizard_apply([ 'youtube:heavy' ]);
	T.ok(r.ok && index(r.warnings, 'low_memory') < 0, 'negative control: variant of tier full on 512 MiB');
	T.eq(CMD.wizard_apply([ 'brokenv' ]).ok, true, 'core set of a service whose variant is broken still applies');
}
else
	print('SKIP [variants] nfqws binary not given: wizard apply checks skipped\n');

exit(T.finish());
