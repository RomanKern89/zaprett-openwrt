// SPDX-License-Identifier: MIT
// zaprett: settings page - all UCI options of /etc/config/zaprett.

'use strict';
'require view';
'require form';
'require uci';
'require network';
'require tools.widgets as widgets';
'require zaprett.common as zc';

/* Same rules as the backend (config.uc, validate.uc). */
const MARK_RE = /^(0x[0-9A-Fa-f]{1,8}|[1-9][0-9]{0,9})$/;
const CLIENT_MARK = 0x08000000;
const USER_RE = /^[a-z_][a-z0-9_-]{0,31}$/;
/* The catalog is downloaded only over https (ARCHITECTURE §14.1). */
const URL_RE = /^https:\/\/[A-Za-z0-9.-]+(:[0-9]{1,5})?(\/[A-Za-z0-9._~%!$&'()*+,;=:@\/?#-]*)?$/;

/* Check intervals of the availability monitor, minutes (§14.1). */
const MONITOR_INTERVALS = [ 10, 15, 20, 30, 60 ];

/* Ports of the game filter: "N" or "N-M", 1..65535, separated by commas
 * without spaces (§15.2). */
const PORT_RANGE_RE = /^([1-9][0-9]{0,4})(?:-([1-9][0-9]{0,4}))?$/;

function validatePorts(section_id, value) {
	if (value == null || value === '')
		return true;

	for (const part of String(value).split(',')) {
		const m = part.match(PORT_RANGE_RE);
		const from = m ? +m[1] : 0, to = (m && m[2] != null) ? +m[2] : from;

		if (!m || from < 1 || to > 65535 || from > to)
			return _('Expecting ports or ranges from 1 to 65535 separated by commas, for example 1024-65535 or 3478,50000-50100');
	}

	return true;
}

function markValue(value, fallback) {
	const v = (value == null || value === '') ? fallback : value;

	return MARK_RE.test(v) ? Number(v) : NaN;
}

function numeric(s, tab, name, title, description, min, max, placeholder) {
	const o = tab ? s.taboption(tab, form.Value, name, title, description) : s.option(form.Value, name, title, description);

	o.datatype = 'and(uinteger,range(%d,%d))'.format(min, max);
	o.placeholder = placeholder;
	o.rmempty = true;

	return o;
}

return view.extend({
	load() {
		return Promise.all([
			uci.load('zaprett'),
			zc.run(zc.callItems).then(res => Array.isArray(res.items) ? res.items : [], () => []),
			L.resolveDefault(network.getHostHints(), null),
			zc.run(zc.callStatus).catch(() => null)
		]).then(data => {
			/* sections created by the package; recreate them if they were deleted */
			for (const name of [ 'main', 'repo', 'test', 'monitor' ])
				if (!uci.get('zaprett', name))
					uci.add('zaprett', name, name);

			return data;
		});
	},

	render(data) {
		const items = data[1], hosts = data[2], status = data[3];
		/* Options of contract v1.4 (§15.1, §15.2) are offered when the service
		 * reports status.flow_offload.own, or when its state is unknown; an
		 * older service would ignore them. Values already set stay visible. */
		const supportsV14 = !L.isObject(status?.flow_offload) || ('own' in status.flow_offload);
		const configured = name => uci.get('zaprett', 'main', name) != null;
		const byType = type => items.filter(it => it.type == type);
		/* Choices from installed items plus every value already configured:
		 * a value missing from the choices would be silently dropped on save. */
		const addItems = (o, type) => {
			const known = {};

			for (const it of byType(type)) {
				known[it.id] = true;
				const name = zc.localized(it, 'name');

				o.value(it.id, (name && name != it.id) ? '%s (%s)'.format(name, it.id) : it.id);
			}

			for (const id of L.toArray(uci.get('zaprett', 'main', o.option)))
				if (id && !known[id])
					o.value(id, _('%s (not installed)').format(id));

			if (!items.length) {
				o.readonly = true;
				o.description = _('The list of installed items could not be loaded, so this field cannot be changed here.');
			}
		};

		const m = new form.Map('zaprett', _('zaprett settings'),
			_('Settings are applied after pressing "Save & Apply". Default values suit most users; the "Advanced" tab is needed only in rare cases.'));

		let s, o;

		/* --- main --- */
		s = m.section(form.NamedSection, 'main', 'main');
		s.addremove = false;
		s.anonymous = true;

		s.tab('general', _('General'));
		s.tab('clients', _('LAN clients'));
		s.tab('lists', _('Strategy and lists'));
		s.tab('advanced', _('Advanced'),
			_('Change these parameters only if you know what you are doing: wrong values can break the bypass or the internet access. An empty field means the default value, shown in grey.'));

		o = s.taboption('general', form.Flag, 'enabled', _('Run zaprett'),
			_('Start the bypass now and after every router reboot.'));
		o.default = '0';
		o.rmempty = false;

		o = s.taboption('general', form.ListValue, 'engine', _('Engine'),
			_('nfqws is the main engine: all repository strategies are written for it. nfqws2 is the newer engine with Lua scripts: 14 built-in z2- strategies are made for it, one of them switches tricks by itself when a site stops opening; it needs the zaprett-nfqws2 package.'));
		o.value('nfqws', 'nfqws');
		o.value('nfqws2', 'nfqws2');
		o.default = 'nfqws';

		o = s.taboption('general', form.ListValue, 'list_mode', _('Which sites to process'),
			_('Whitelist: only sites and networks from the enabled lists. Blacklist: all sites. Exclusion lists work in both modes; a domain exclusion does not cover connections that are recognised only by address, so such sites also need IP network exclusions. The whitelist is safer and is recommended.'));
		o.value('whitelist', _('Whitelist: only the enabled lists (recommended)'));
		o.value('blacklist', _('Blacklist: everything except the exclusions'));
		o.default = 'whitelist';

		o = s.taboption('general', widgets.NetworkSelect, 'wan', _('Internet interfaces'),
			_('Interfaces through which the router reaches the internet. Leave empty to detect them automatically by the default route.'));
		o.multiple = true;
		o.nocreate = true;
		o.optional = true;
		o.rmempty = true;

		o = s.taboption('general', form.Flag, 'ipv6', _('Process IPv6'),
			_('Enable if your provider gives you IPv6 internet access. Otherwise leave it off.'));
		o.default = '0';

		o = s.taboption('general', form.Flag, 'watchdog', _('Engine watchdog'),
			_('Every 5 minutes zaprett checks that the engine is running and its firewall rules are in place, and restores them if they are not (for example after the engine crashed).'));
		o.default = '1';
		o.rmempty = false;

		const ownOffload = supportsV14 || uci.get('zaprett', 'main', 'flow_offload') == 'own';

		o = s.taboption('general', form.ListValue, 'flow_offload', _('Flow offloading'),
			ownOffload
				? _('Flow offloading speeds up the router, but offloaded packets bypass the firewall, and then the bypass does not work. "Own acceleration table": zaprett turns off the offloading of the firewall and accelerates connections itself, but only after the engine has seen their first packets, so both the bypass and the speed are kept. "Turn off automatically": no acceleration while zaprett runs; the previous state is restored when it stops. "Do not change": the firewall setting stays as it is, choose it only if offloading is off anyway.')
				: _('Software and hardware flow offloading make packets bypass the firewall, and then the bypass does not work. In the automatic mode zaprett turns offloading off while it runs and restores the previous state when stopped.'));

		if (ownOffload)
			o.value('own', _('Own acceleration table (faster, recommended)'));

		o.value('auto', ownOffload ? _('Turn off automatically while zaprett runs') : _('Turn off automatically while zaprett runs (recommended)'));
		o.value('keep', _('Do not change'));
		o.default = 'auto';

		if (supportsV14 || configured('quic_block')) {
			o = s.taboption('general', form.Flag, 'quic_block', _('Block QUIC'),
				_('Many sites and apps (YouTube, Google services and others) load over QUIC, which runs on UDP port 443. Not every strategy can bypass the blocking of QUIC, while ordinary TCP connections are bypassed reliably. With this option the router drops UDP 443 from the local network to the internet, and browsers and apps switch to ordinary TCP connections, where the bypass works. It affects ALL sites, not only those from the lists: every site that used QUIC goes over TCP, which may be a bit slower on a good connection, and apps that work only over QUIC stop working. Traffic of the router itself is not affected; the device filter of the "LAN clients" tab applies.'));
			o.default = '0';
		}

		/* --- clients --- */
		o = s.taboption('clients', form.ListValue, 'clients_mode', _('Which devices get the bypass'),
			_('Traffic of the router itself is not processed in the "Only the listed devices" mode.'));
		o.value('all', _('All devices'));
		o.value('include', _('Only the listed devices'));
		o.value('exclude', _('All devices except the listed ones'));
		o.default = 'all';

		o = s.taboption('clients', form.DynamicList, 'clients', _('Devices'),
			_('IPv4 address, network (for example 192.168.1.0/24) or MAC address of a device in the local network. It is better to give devices static addresses in "Network → DHCP and DNS" first.'));
		o.datatype = 'or(ip4addr("nomask"),cidr4,macaddr)';
		o.depends('clients_mode', 'include');
		o.depends('clients_mode', 'exclude');
		o.retain = true;

		if (hosts) {
			for (const hint of hosts.getMACHints(false)) {
				const mac = hint[0], name = hint[1];
				const ip = hosts.getIPAddrByMACAddr(mac);

				o.value(mac, name ? '%s (%s)'.format(mac, name) : mac);

				if (ip)
					o.value(ip, name && name != ip ? '%s (%s)'.format(ip, name) : ip);
			}
		}

		/* --- strategy and lists --- */
		o = s.taboption('lists', form.ListValue, 'strategy', _('Strategy for nfqws'),
			_('The same choice as on the "Strategies" page.'));
		addItems(o, 'nfqws');
		o.rmempty = true;
		o.optional = true;

		o = s.taboption('lists', form.ListValue, 'strategy_nfqws2', _('Strategy for nfqws2'),
			_('Used only with the nfqws2 engine.'));
		o.value('', _('not selected'));
		addItems(o, 'nfqws2');
		o.optional = true;
		o.rmempty = true;

		o = s.taboption('lists', form.MultiValue, 'lists', _('Domain lists'),
			_('The same switches as on the "Lists" page.'));
		addItems(o, 'list');
		o.rmempty = true;

		o = s.taboption('lists', form.MultiValue, 'exclude_lists', _('Domain exclusions'));
		addItems(o, 'list_exclude');
		o.rmempty = true;

		o = s.taboption('lists', form.MultiValue, 'ipsets', _('IP network lists'));
		addItems(o, 'ipset');
		o.rmempty = true;

		o = s.taboption('lists', form.MultiValue, 'exclude_ipsets', _('IP network exclusions'));
		addItems(o, 'ipset_exclude');
		o.rmempty = true;

		if (supportsV14 || configured('game_filter')) {
			o = s.taboption('lists', form.Flag, 'game_filter', _('Game filter'),
				_('Adds a separate engine profile for online games: it processes the game ports below, but only for addresses from the enabled IP network lists, so the rest of the traffic stays untouched. It works only when an IP network list of the game is enabled above ("IP network lists"); without such a list the profile is not added and the "Overview" page shows a warning. Turn it on only if a game is blocked or slowed down.'));
			o.default = '0';

			o = s.taboption('lists', form.Value, 'game_ports_tcp', _('Game TCP ports'),
				_('Ports or ranges separated by commas, for example 1024-65535 or 3478,50000-50100.'));
			o.placeholder = '1024-65535';
			o.rmempty = true;
			o.validate = validatePorts;
			o.depends('game_filter', '1');

			o = s.taboption('lists', form.Value, 'game_ports_udp', _('Game UDP ports'),
				_('Ports or ranges separated by commas, for example 1024-65535 or 3478,50000-50100.'));
			o.placeholder = '1024-65535';
			o.rmempty = true;
			o.validate = validatePorts;
			o.depends('game_filter', '1');
		}

		/* --- advanced --- */
		numeric(s, 'advanced', 'qnum', _('Queue number'),
			_('Number of the NFQUEUE queue between the firewall and the engine. Change it only if the number is already used by another program.'), 0, 65535, '200');

		/* both marks are checked together: different from each other and
		 * from the client filter mark 0x08000000 */
		const validateMarks = function(section_id) {
			const desync = markValue(this.section.formvalue(section_id, 'desync_mark'), '0x40000000');
			const postnat = markValue(this.section.formvalue(section_id, 'postnat_mark'), '0x20000000');

			if (isNaN(desync) || isNaN(postnat) || desync == 0 || postnat == 0 || desync > 0xffffffff || postnat > 0xffffffff)
				return _('Expecting a non-zero mark: hexadecimal like 0x40000000 or decimal');

			if (desync == postnat)
				return _('The marks must be different');

			if (desync == CLIENT_MARK || postnat == CLIENT_MARK)
				return _('The mark 0x08000000 is used by the client filter');

			return true;
		};

		o = s.taboption('advanced', form.Value, 'desync_mark', _('Mark of engine packets'),
			_('Firewall mark of packets sent by the engine, so that they are not processed again. Hexadecimal, one bit.'));
		o.placeholder = '0x40000000';
		o.rmempty = true;
		o.validate = validateMarks;

		o = s.taboption('advanced', form.Value, 'postnat_mark', _('Mark of processed connections'),
			_('Firewall mark used to skip already processed packets after NAT. Must differ from the mark of engine packets.'));
		o.placeholder = '0x20000000';
		o.rmempty = true;
		o.validate = validateMarks;

		numeric(s, 'advanced', 'tcp_pkt_out', _('Outgoing TCP packets'),
			_('How many first packets of every outgoing TCP connection go to the engine. 0 turns off processing of outgoing TCP.'), 0, 1000, '9');
		numeric(s, 'advanced', 'tcp_pkt_in', _('Incoming TCP packets'),
			_('How many first reply packets of a TCP connection go to the engine. Some strategies need them to detect the server answer.'), 0, 1000, '3');
		numeric(s, 'advanced', 'udp_pkt_out', _('Outgoing UDP packets'),
			_('How many first packets of every outgoing UDP flow (QUIC, voice) go to the engine. 0 turns off processing of UDP.'), 0, 1000, '9');
		numeric(s, 'advanced', 'udp_pkt_in', _('Incoming UDP packets'),
			_('How many first reply UDP packets go to the engine. Usually 0.'), 0, 1000, '0');

		o = s.taboption('advanced', form.Value, 'user', _('Engine user'),
			_('After start the engine drops root privileges and works as this system user.'));
		o.placeholder = 'daemon';
		o.rmempty = true;
		o.validate = (section_id, value) => (!value || USER_RE.test(value)) ? true : _('Expecting a system user name');

		o = s.taboption('advanced', form.Flag, 'debug', _('Debug log'),
			_('The engine writes a detailed log to the system log ("Status → System Log"). It loads the router, keep it off normally.'));
		o.default = '0';

		/* --- repository --- */
		s = m.section(form.NamedSection, 'repo', 'repo', _('Repository and updates'));
		s.addremove = false;

		o = s.option(form.Value, 'url', _('Catalog address'),
			_('Address of index.json of the zaprett repository, only https://. Change it only to use a mirror.'));
		o.placeholder = 'https://raw.githubusercontent.com/CherretGit/zaprett-repo/refs/heads/main/index.json';
		o.rmempty = true;
		o.validate = (section_id, value) => (!value || URL_RE.test(value)) ? true : _('Expecting an https:// address');

		o = s.option(form.Flag, 'autoupdate', _('Update automatically'),
			_('Once a day check the repository and update the installed strategies and lists.'));
		o.default = '1';
		o.rmempty = false;

		o = s.option(form.ListValue, 'autoupdate_hour', _('Update hour'),
			_('The hour of the day (router time) when the update starts.'));
		for (let h = 0; h < 24; h++)
			o.value(String(h), '%02d:00'.format(h));
		o.default = '4';
		o.depends('autoupdate', '1');

		/* --- automatic selection --- */
		s = m.section(form.NamedSection, 'test', 'test', _('Automatic strategy selection'),
			_('Parameters of the check. Larger timeouts make the check slower but more reliable on slow connections.'));
		s.addremove = false;

		numeric(s, null, 'timeout', _('Request timeout, s'),
			_('How long to wait for one site to answer.'), 1, 60, '5');
		numeric(s, null, 'concurrency', _('Parallel requests'),
			_('How many sites are checked at the same time. Lower it on weak routers.'), 1, 16, '6');
		numeric(s, null, 'max_domains', _('Sites from lists'),
			_('How many domains from the enabled lists are added to the check of every strategy. More sites make the check longer.'), 0, 100, '20');
		numeric(s, null, 'settle', _('Pause after restart, s'),
			_('Pause after the engine restart before the check starts.'), 0, 30, '2');

		/* --- availability monitor --- */
		s = m.section(form.NamedSection, 'monitor', 'monitor', _('Availability monitor'),
			_('The monitor checks on schedule whether the sites of the enabled services open through zaprett. A check fails when less than half of the checked sites opened. The results are on the "Overview" page.'));
		s.addremove = false;

		o = s.option(form.Flag, 'enabled', _('Check the sites on schedule'),
			_('Works only while zaprett is running.'));
		o.default = '1';
		o.rmempty = false;

		o = s.option(form.ListValue, 'interval', _('Check every'));
		for (const minutes of MONITOR_INTERVALS)
			o.value(String(minutes), _('%d min').format(minutes));
		o.default = '30';
		o.depends('enabled', '1');

		o = numeric(s, null, 'threshold', _('Failed checks before a warning'),
			_('How many checks in a row must fail before the state becomes "sites stopped opening".'), 1, 20, '3');
		o.depends('enabled', '1');

		o = s.option(form.Flag, 'auto_repair', _('Repair automatically'),
			_('When the sites stop opening, zaprett runs the quick strategy selection by itself and applies a strategy only if it opens more sites than the current one. Not more often than once in 6 hours; the bypass keeps working during the selection.'));
		o.default = '0';
		o.depends('enabled', '1');

		o = numeric(s, null, 'max_targets', _('Sites per check'),
			_('How many check addresses are opened in one check.'), 1, 20, '5');
		o.depends('enabled', '1');

		o = numeric(s, null, 'timeout', _('Wait for a site, s'),
			_('How long to wait for one address to answer.'), 2, 30, '8');
		o.depends('enabled', '1');

		return m.render();
	}
});
