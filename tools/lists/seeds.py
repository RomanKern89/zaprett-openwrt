"""Исходные данные генератора собственных списков zaprett (docs/ARCHITECTURE.md §16).

Здесь только «семена» — то, что взято из первоисточников вручную, — и правила отбора по сервисам.
Всё остальное генератор (generate.py) выводит сам из снимков ответов источников (snapshots/) и пишет
в журнал BUILD_LOG.tsv, почему каждая запись включена или исключена.

Семя домена: (имя, основной?, ключ документа или None). Семя без документа должно подтвердиться
снимком: имя (или его регистрируемый домен) найдено в сертификатах сервиса (CT), в коде его страниц,
среди запросов реальной сессии браузера (sessions) или в коде JS, который эти страницы загружают с
доменов сервиса, либо резолвится в собственные IP-сети сервиса. Иначе оно исключается с причиной.
old_core — прежний основной список (до §16): его записи — только кандидаты, в список они возвращаются,
если подтверждены теми же доказательствами; сравнение «было -> стало» пишется в out/COMPARE_OLD.tsv.
Основной вариант — семена с флагом «основной»: хосты, без которых не работает основной сценарий
(сайт, видео, вход, API клиента). Всё остальное подтверждённое — в расширенный.
"""

# Первоисточники, на которые ссылаются семена (ключ -> URL). Дата — когда страница прочитана.
DOCS = {
    'google-ws-youtube': ('https://knowledge.workspace.google.com/admin/youtube/control-youtube-content-available-to-users',
                          '2026-09-22'),
    'discord-support': ('https://support.discord.com/hc/en-us/articles/360042987951', '2026-09-22'),
    'telegram-site': ('https://telegram.org/', '2026-09-22'),
    'rutracker-site': ('https://rutracker.org/forum/index.php', '2026-09-22'),
    'roblox-edu': ('https://en.help.roblox.com/hc/en-us/articles/115005744663', '2026-09-22'),
    'signal-fw': ('https://support.signal.org/hc/en-us/articles/360007320291', '2026-09-22'),
}

# Официальные опубликованные диапазоны (ключ -> URL); ответ сохраняется в snapshots/official/<ключ>.txt
OFFICIAL = {
    'telegram-cidr': 'https://core.telegram.org/resources/cidr.txt',
    'cloudflare-v4': 'https://www.cloudflare.com/ips-v4',
    'cloudflare-v6': 'https://www.cloudflare.com/ips-v6',
}

# Регистрационные записи сетей (RDAP) — подтверждение владельца сети, если AS чужая
RDAP = {
    'discord-66.22.192.0': 'https://rdap.db.ripe.net/ip/66.22.192.0/18',
}

# Сертификаты, в которых есть такие имена, — общие для многих клиентов провайдера; из них ничего не берём
SHARED_CERT_MARKERS = ('cloudflaressl.com', 'sni.cloudflaressl.com')
CT_MAX_SANS = 150            # сертификат с большим числом имён считаем общим: из него — только имена с ключевыми словами
CT_MAX_FOREIGN = 2           # то же, если на нём больше двух регистрируемых доменов без ключевых слов сервиса
CT_WINDOW_DAYS = 365         # берём сертификаты, действовавшие в последний год до снимка

SERVICES = [
    {
        'id': 'youtube',
        'anchors': ('youtube.com',),
        # Google выпускает общие сертификаты на все свои сайты — из них берём только имена YouTube
        'ct_keyword_required': True,
        'keywords': ('youtube', 'youtu', 'ytimg', 'ggpht', 'googlevideo', 'yt.be'),
        'skip_cc_variants': True,      # youtube.de, youtube.co.uk… только перенаправляют на youtube.com
        'shared': ('googleapis.com', 'googleusercontent.com', 'google.com', 'gstatic.com'),
        'pages': ('https://www.youtube.com/', 'https://www.youtube.com/embed/dQw4w9WgXcQ',
                  'https://www.youtube.com/@YouTube', 'https://www.youtube.com/watch?v=dQw4w9WgXcQ'),
        'sessions': (('home', 'https://www.youtube.com/'),
                     ('watch', 'https://www.youtube.com/watch?v=dQw4w9WgXcQ'),
                     ('channel', 'https://www.youtube.com/@YouTube'),
                     ('embed', 'https://www.youtube.com/embed/dQw4w9WgXcQ'),
                     ('login', 'https://accounts.google.com/ServiceLogin?service=youtube')),
        'old_core': 'upstream/lists-refs/curated/youtube.txt',
        'seeds': [
            ('youtube.com', True, 'google-ws-youtube'),
            ('youtu.be', True, 'google-ws-youtube'),
            ('youtube-nocookie.com', True, 'google-ws-youtube'),
            ('youtubei.googleapis.com', True, 'google-ws-youtube'),
            ('youtube.googleapis.com', True, 'google-ws-youtube'),
            ('ytimg.com', True, 'google-ws-youtube'),          # в документе — s.ytimg.com
            ('googlevideo.com', True, None),                    # видеопоток
            ('ggpht.com', True, None),                          # аватары каналов
            ('youtubekids.com', True, None),
            ('yt.be', True, None),
            ('youtubeembeddedplayer.googleapis.com', True, None),
            ('jnn-pa.googleapis.com', True, None),
            ('yt3.googleusercontent.com', True, None),
            ('youtubeeducation.com', False, 'google-ws-youtube'),
        ],
        'lists': {'core': 'zaprett-youtube', 'full': 'zaprett-youtube-full'},
    },
    {
        'id': 'discord',
        'anchors': ('discord.com',),
        'ct_keyword_required': False,
        'keywords': ('discord',),
        'shared': ('googleapis.com',),
        'pages': ('https://discord.com/app', 'https://discord.com/'),
        'sessions': (('home', 'https://discord.com/'),
                     ('login', 'https://discord.com/login'),
                     ('app', 'https://discord.com/app')),
        'old_core': 'upstream/lists-refs/curated/discord.txt',
        'seeds': [
            ('discord.com', True, 'discord-support'),
            ('discord.gg', True, None),
            ('discordapp.com', True, 'discord-support'),
            ('discordapp.net', True, None),
            ('discord.media', True, None),
            ('discordcdn.com', True, None),
            ('dis.gd', True, None),
            ('discordstatus.com', True, None),
            ('discordsays.com', True, None),
            ('discord-attachments-uploads-prd.storage.googleapis.com', True, None),
            ('discord.co', False, None),
            ('discord.dev', False, None),
            ('discord.design', False, None),
            ('discord.store', False, None),
            ('discordmerch.com', False, None),
            ('discordactivities.com', False, None),
            ('discord-activities.com', False, None),
            ('discord.gift', False, None),
            ('discord.gifts', False, None),
            ('discord.new', False, None),
        ],
        'lists': {'core': 'zaprett-discord', 'full': 'zaprett-discord-full'},
        'notes': {
            'discord-attachments-uploads-prd.storage.googleapis.com':
                'в коде discord.com/app и /login (677 скриптов) имени нет; загрузка вложений видна только после '
                'входа, сессии с учётной записью нет',
        },
    },
    {
        'id': 'telegram',
        'anchors': ('telegram.org',),
        'ct_keyword_required': False,
        'keywords': ('telegram', 'tdesktop', 'telesco', 'telegra'),
        'shared': (),
        'pages': ('https://telegram.org/', 'https://telegram.org/apps', 'https://core.telegram.org/'),
        'sessions': (('site', 'https://telegram.org/'),
                     ('web_k', 'https://web.telegram.org/k/'),
                     ('web_a', 'https://web.telegram.org/a/')),
        'old_core': 'upstream/lists-refs/curated/telegram.txt',
        'ip_evidence': 'zaprett-telegram-ipset',   # домен Telegram, если резолвится в его собственные сети
        'seeds': [
            ('telegram.org', True, 'telegram-site'),
            ('t.me', True, None),
            ('telegram.me', True, None),
            ('telesco.pe', True, None),
            ('cdn-telegram.org', True, None),
            ('tdesktop.com', True, None),
            ('telegra.ph', True, None),
            ('graph.org', True, None),
            ('tx.me', True, None),
            ('telegram.dog', True, None),
            ('fragment.com', False, None),
            ('contest.com', False, None),
            ('comments.app', False, None),
            ('quiz.directory', False, None),
            ('tg.dev', False, None),
            ('usercontent.dev', False, None),
            ('telegram.space', False, None),
        ],
        # расширенного варианта нет: всё подтверждённое (включая прежний список) уже в основном
        'lists': {'core': 'zaprett-telegram', 'full': None},
    },
    {
        'id': 'rutracker',
        'anchors': ('rutracker.org',),
        'ct_keyword_required': False,
        'keywords': ('rutracker', 't-ru'),
        'shared': (),
        'pages': ('https://rutracker.org/forum/index.php',),
        'sessions': (('index', 'https://rutracker.org/forum/index.php'),
                     ('forum', 'https://rutracker.org/forum/viewforum.php?f=7')),
        'old_core': 'upstream/zaprett-repo/files/lists/include/list-rutracker.txt',
        'seeds': [
            ('rutracker.org', True, 'rutracker-site'),
            ('rutracker.net', True, None),
            ('rutracker.cc', True, None),
            ('t-ru.org', True, None),                 # статика и анонсеры трекера
            ('rutracker.ru', False, None),
            ('rutracker.wiki', False, None),
        ],
        'lists': {'core': 'zaprett-rutracker', 'full': 'zaprett-rutracker-full'},
    },
    {
        'id': 'roblox',
        'anchors': ('roblox.com',),
        'ct_keyword_required': False,
        'keywords': ('roblox', 'rbx'),
        'shared': ('arkoselabs.com',),
        'pages': ('https://www.roblox.com/',),
        'sessions': (('home', 'https://www.roblox.com/'),
                     ('login', 'https://www.roblox.com/login'),
                     ('game', 'https://www.roblox.com/games/920587237')),
        'ip_evidence': 'zaprett-roblox-ipset',
        'seeds': [
            ('roblox.com', True, 'roblox-edu'),
            ('rbxcdn.com', True, 'roblox-edu'),
            ('roblox-api.arkoselabs.com', True, 'roblox-edu'),   # капча при входе (Arkose Labs)
            ('cdn.arkoselabs.com', False, 'roblox-edu'),
        ],
        'lists': {'core': 'zaprett-roblox', 'full': 'zaprett-roblox-full'},
    },
    {
        'id': 'signal',
        'anchors': ('signal.org',),
        'ct_keyword_required': False,
        'keywords': ('signal',),
        'shared': (),
        'pages': ('https://signal.org/',),
        'sessions': (('home', 'https://signal.org/'),
                     ('download', 'https://signal.org/download/')),
        'seeds': [
            ('signal.org', True, 'signal-fw'),
            ('signal.me', True, 'signal-fw'),
            ('signal.group', True, 'signal-fw'),
            ('signal.link', True, 'signal-fw'),
            ('signal.art', False, 'signal-fw'),
            ('signal.tube', False, 'signal-fw'),
        ],
        'lists': {'core': 'zaprett-signal', 'full': 'zaprett-signal-full'},
    },
]

# IP-сети. asns — анонсы по RIPEstat (владелец AS обязан совпасть с holder_re), official — опубликованные
# диапазоны, within — брать из анонсов только сети внутри этих блоков, владелец которых подтверждён RDAP.
IPSETS = [
    {'id': 'zaprett-telegram-ipset', 'service': 'telegram', 'variant': 'ipset', 'families': (4, 6),
     'official': ('telegram-cidr',), 'asns': (62041, 59930, 44907, 211157, 62014), 'holder_re': r'telegram'},
    {'id': 'zaprett-discord-voice', 'service': 'discord', 'variant': 'voice', 'families': (4, 6),
     'official': (), 'asns': (49544,), 'holder_re': r'i3d',
     'within': (('66.22.192.0/18', 'discord-66.22.192.0', r'discord inc'),)},
    {'id': 'zaprett-roblox-ipset', 'service': 'roblox', 'variant': 'ipset', 'families': (4, 6),
     'official': (), 'asns': (22697,), 'holder_re': r'roblox'},
    {'id': 'zaprett-cloudflare-ipset', 'service': 'cloudflare', 'variant': 'ipset', 'families': (4,),
     'official': ('cloudflare-v4',), 'asns': (), 'holder_re': None},
    {'id': 'zaprett-cloudflare-ipset6', 'service': 'cloudflare', 'variant': 'ipset6', 'families': (6,),
     'official': ('cloudflare-v6',), 'asns': (), 'holder_re': None},
]

# Тексты манифестов: id -> (name, name_en, description, description_en). {n} — число записей,
# {v4} — число адресов IPv4, {v6} — число сетей IPv6.
TEXTS = {
    'zaprett-youtube': (
        'YouTube', 'YouTube',
        'Домены YouTube ({n} шт.): сайт, видео (googlevideo.com), превью, аватары и API. Основной набор: '
        'домены из документации Google и имена из сертификатов и страниц YouTube, каждое проверено в DNS. '
        'Поддомены учитываются автоматически.',
        'YouTube domains ({n}): website, videos (googlevideo.com), thumbnails, avatars and API. The core set: '
        'domains from the Google documentation and names from YouTube certificates and pages, each checked in '
        'DNS. Subdomains are covered automatically.'),
    'zaprett-youtube-full': (
        'YouTube (расширенный)', 'YouTube (extended)',
        'Расширенный набор YouTube ({n} шт.): основной плюс служебные узлы API, домены приложений (YouTube Go, '
        'YouTube для образования) и блог YouTube, найденные в сертификатах Google и в коде страниц YouTube. '
        'Включайте вместо основного, если с основным что-то не грузится.',
        'Extended YouTube set ({n}): the core set plus auxiliary API hosts, app domains (YouTube Go, YouTube for '
        'Education) and the YouTube blog found in Google certificates and in the code of YouTube pages. Enable it '
        'instead of the core set if something does not load with the core set.'),
    'zaprett-discord': (
        'Discord', 'Discord',
        'Домены Discord ({n} шт.): сайт, приложение, вложения, активности, голосовые серверы discord.media, '
        'приглашения и подарки. Взяты из документации Discord, сертификатов discord.com, запросов и кода его '
        'страниц, проверены в DNS. Голос обрабатывает отдельное правило стратегии по портам.',
        'Discord domains ({n}): website, app, attachments, activities and the discord.media voice servers. '
        'Taken from the Discord documentation, discord.com certificates and the code of its pages, checked in '
        'DNS. Voice is handled by a separate strategy rule by ports.'),
    'zaprett-discord-full': (
        'Discord (расширенный)', 'Discord (extended)',
        'Расширенный набор Discord ({n} шт.): основной плюс служебный домен discord.tools из кода клиента '
        'Discord. Включайте вместо основного.',
        'Extended Discord set ({n}): the core set plus the auxiliary discord.tools domain from the Discord client '
        'code. Enable it instead of the core set.'),
    'zaprett-discord-voice': (
        'Discord: голосовые серверы (IP)', 'Discord: voice servers (IP)',
        'IP-сети голосовых серверов Discord ({n} шт., адресов IPv4: {v4}): сети из блока 66.22.192.0/18, '
        'который принадлежит Discord Inc. (RIPE), и которые сейчас анонсирует AS49544 (по RIPEstat). '
        'Нужны стратегиям, у которых есть правило для UDP по IP-спискам.',
        'IP networks of the Discord voice servers ({n}, IPv4 addresses: {v4}): networks from the 66.22.192.0/18 '
        'block owned by Discord Inc. (RIPE) that AS49544 currently announces (per RIPEstat). Needed by strategies '
        'that have a UDP rule by IP lists.'),
    'zaprett-telegram': (
        'Telegram', 'Telegram',
        'Домены Telegram ({n} шт.): telegram.org, t.me, telesco.pe, telegra.ph, Fragment и другие сайты Telegram. Домен '
        'взят, если он резолвится в собственные сети Telegram или найден в сертификатах telegram.org; проверен '
        'в DNS. Приложение Telegram ходит по IP-адресам — для него нужен лист «Telegram: IP-сети».',
        'Telegram domains ({n}): telegram.org, t.me, telesco.pe, telegra.ph and other Telegram sites. A domain '
        'is taken if it resolves into Telegram\'s own networks or is found in telegram.org certificates; checked '
        'in DNS. The Telegram app connects by IP address, so it needs the "Telegram: IP networks" list.'),
    'zaprett-telegram-ipset': (
        'Telegram: IP-сети', 'Telegram: IP networks',
        'IP-сети Telegram ({n} шт., адресов IPv4: {v4}): официальный список core.telegram.org/resources/cidr.txt '
        'и сети, которые анонсируют автономные системы Telegram Messenger Inc (AS62041, AS59930, AS44907, '
        'AS211157, AS62014) по данным RIPEstat. Нужны приложению Telegram, которое соединяется с серверами по IP.',
        'Telegram IP networks ({n}, IPv4 addresses: {v4}): the official list core.telegram.org/resources/cidr.txt '
        'and the networks announced by the autonomous systems of Telegram Messenger Inc (AS62041, AS59930, AS44907, '
        'AS211157, AS62014) according to RIPEstat. Needed by the Telegram app, which connects to its servers by IP.'),
    'zaprett-rutracker': (
        'RuTracker', 'RuTracker',
        'Домены RuTracker ({n} шт.): форум, зеркало rutracker.net, rutracker.cc (статика, API и лента) и вики. '
        'Взяты с сайта RuTracker, из его сертификатов и запросов его страниц, проверены в DNS.',
        'RuTracker domains ({n}): the forum, the rutracker.net mirror, rutracker.cc (static files, API and feed) '
        'and the wiki. Taken from the RuTracker website, its certificates and the requests of its pages, checked '
        'in DNS.'),
    'zaprett-rutracker-full': (
        'RuTracker (расширенный)', 'RuTracker (extended)',
        'Расширенный набор RuTracker ({n} шт.): основной плюс служебный домен rutrk.org из сертификатов '
        'RuTracker. Включайте вместо основного.',
        'Extended RuTracker set ({n}): the core set plus the auxiliary rutrk.org domain from RuTracker '
        'certificates. Enable it instead of the core set.'),
    'zaprett-roblox': (
        'Roblox', 'Roblox',
        'Домены Roblox ({n} шт.): сайт, API, CDN rbxcdn.com и капча входа. Взяты из справки Roblox для '
        'администраторов сетей, проверены в DNS. Сами игры идут по UDP на IP-адреса — для них нужен лист '
        '«Roblox: IP-сети» и стратегия с правилом для UDP.',
        'Roblox domains ({n}): website, API, the rbxcdn.com CDN and the login captcha. Taken from the Roblox help '
        'article for network administrators, checked in DNS. The games themselves use UDP to IP addresses, which '
        'needs the "Roblox: IP networks" list and a strategy with a UDP rule.'),
    'zaprett-roblox-full': (
        'Roblox (расширенный)', 'Roblox (extended)',
        'Расширенный набор Roblox ({n} шт.): основной плюс прочие домены Roblox из его сертификатов и кода '
        'страниц. Включайте вместо основного.',
        'Extended Roblox set ({n}): the core set plus other Roblox domains from its certificates and page code. '
        'Enable it instead of the core set.'),
    'zaprett-roblox-ipset': (
        'Roblox: IP-сети', 'Roblox: IP networks',
        'IP-сети Roblox ({n} шт., адресов IPv4: {v4}): сети, которые анонсирует автономная система Roblox '
        '(AS22697, ROBLOX-PRODUCTION) по данным RIPEstat. Игровые серверы работают по UDP 49152–65535; '
        'нужна стратегия с правилом для UDP по IP-спискам (например, strategy-alt2-roblox).',
        'Roblox IP networks ({n}, IPv4 addresses: {v4}): the networks announced by the Roblox autonomous system '
        '(AS22697, ROBLOX-PRODUCTION) according to RIPEstat. Game servers use UDP 49152-65535; a strategy with a '
        'UDP rule by IP lists is needed (for example, strategy-alt2-roblox).'),
    'zaprett-signal': (
        'Signal', 'Signal',
        'Домены Signal ({n} шт.): signal.org (сообщения, звонки, обновления) и домены ссылок. Взяты из справки '
        'Signal о настройке файрвола, проверены в DNS.',
        'Signal domains ({n}): signal.org (messages, calls, updates) and the link domains. Taken from the Signal '
        'help article on firewall settings, checked in DNS.'),
    'zaprett-signal-full': (
        'Signal (расширенный)', 'Signal (extended)',
        'Расширенный набор Signal ({n} шт.): основной плюс домены стикеров, видео и прочие домены Signal из '
        'сертификатов и страниц. Включайте вместо основного.',
        'Extended Signal set ({n}): the core set plus the sticker, video and other Signal domains from '
        'certificates and pages. Enable it instead of the core set.'),
    'zaprett-cloudflare-ipset': (
        'Cloudflare: IP-сети (IPv4)', 'Cloudflare: IP networks (IPv4)',
        'Официальные диапазоны IPv4 Cloudflare ({n} сетей, адресов: {v4}) с www.cloudflare.com/ips-v4 — снимок '
        'в пакете, работает без загрузки из интернета. Действует на все сайты за Cloudflare.',
        'The official Cloudflare IPv4 ranges ({n} networks, addresses: {v4}) from www.cloudflare.com/ips-v4 — a '
        'snapshot inside the package that works without downloading. Applies to every site behind Cloudflare.'),
    'zaprett-cloudflare-ipset6': (
        'Cloudflare: IP-сети (IPv6)', 'Cloudflare: IP networks (IPv6)',
        'Официальные диапазоны IPv6 Cloudflare ({n} сетей) с www.cloudflare.com/ips-v6 — снимок в пакете. '
        'Нужен, если у вас есть IPv6.',
        'The official Cloudflare IPv6 ranges ({n} networks) from www.cloudflare.com/ips-v6 — a snapshot inside '
        'the package. Needed if you have IPv6.'),
}

# Кратко: как собран список (поле method манифеста, §16.2)
METHODS = {
    'core': 'семена из первоисточников + CT (crt.sh/certspotter) + ссылки со страниц сервиса; DoH dns.google и '
            'cloudflare-dns.com; свёртка поддоменов',
    'full': 'основной + домены сервиса из CT (общие имена на сертификатах якорного домена) и страниц; DoH; свёртка',
    'ipset': 'анонсы AS сервиса (RIPEstat announced-prefixes, владелец AS проверен) + официальный список; агрегация CIDR',
    'voice': 'анонсы AS49544 внутри блока Discord Inc. (RDAP RIPE) по RIPEstat; агрегация CIDR',
    'ipset6': 'официальный список IPv6; агрегация CIDR',
}

# Китайские тексты манифестов (zh-CN, Windows-приложение; §14.6): id -> (name_zh, description_zh).
# Термины — по windows/docs/GLOSSARY-zh.md; {n}, {v4} — как в TEXTS.
TEXTS_ZH = {
    'zaprett-youtube': (
        'YouTube',
        'YouTube 域名（{n} 个）：网站、视频（googlevideo.com）、缩略图、头像和 API。基本列表：来自 Google 文档的域名，'
        '以及 YouTube 证书和页面中的名称，均已在 DNS 中验证。子域名会自动匹配。'),
    'zaprett-youtube-full': (
        'YouTube（扩展）',
        '扩展的 YouTube 列表（{n} 个）：基本列表，另加在 Google 证书和 YouTube 页面代码中找到的辅助 API 主机、应用域名'
        '（YouTube Go、YouTube 教育版）和 YouTube 博客。如果使用基本列表时有内容加载不出来，请改用此列表。'),
    'zaprett-discord': (
        'Discord',
        'Discord 域名（{n} 个）：网站、应用、附件、活动、discord.media 语音服务器、邀请和礼物。取自 Discord 文档、'
        'discord.com 证书以及其页面的请求和代码，均已在 DNS 中验证。语音由策略中单独的按端口规则处理。'),
    'zaprett-discord-full': (
        'Discord（扩展）',
        '扩展的 Discord 列表（{n} 个）：基本列表，另加 Discord 客户端代码中的辅助域名 discord.tools。请用它替代基本列表。'),
    'zaprett-discord-voice': (
        'Discord：语音服务器（IP）',
        'Discord 语音服务器的 IP 网段（{n} 个，IPv4 地址：{v4}）：属于 Discord Inc.（RIPE）的 66.22.192.0/18 地址块中、'
        '目前由 AS49544 宣告的网段（据 RIPEstat）。供带有按 IP 列表的 UDP 规则的策略使用。'),
    'zaprett-telegram': (
        'Telegram',
        'Telegram 域名（{n} 个）：telegram.org、t.me、telesco.pe、telegra.ph、Fragment 和其他 Telegram 网站。域名解析到 '
        'Telegram 自有网段，或出现在 telegram.org 证书或其页面中，才会被收录；均已在 DNS 中验证。Telegram 应用按 IP '
        '地址连接，因此需要“Telegram：IP 网段”列表。'),
    'zaprett-telegram-ipset': (
        'Telegram：IP 网段',
        'Telegram 的 IP 网段（{n} 个，IPv4 地址：{v4}）：官方列表 core.telegram.org/resources/cidr.txt，以及 Telegram '
        'Messenger Inc 自治系统（AS62041、AS59930、AS44907、AS211157、AS62014）宣告的网段（据 RIPEstat）。Telegram '
        '应用按 IP 连接服务器，需要此列表。'),
    'zaprett-rutracker': (
        'RuTracker',
        'RuTracker 域名（{n} 个）：论坛、镜像 rutracker.net、rutracker.cc（静态文件、API 和订阅源）以及维基。取自 '
        'RuTracker 网站、其证书和页面请求，均已在 DNS 中验证。'),
    'zaprett-rutracker-full': (
        'RuTracker（扩展）',
        '扩展的 RuTracker 列表（{n} 个）：基本列表，另加 RuTracker 证书中的辅助域名 rutrk.org。请用它替代基本列表。'),
    'zaprett-roblox': (
        'Roblox',
        'Roblox 域名（{n} 个）：网站、API、CDN rbxcdn.com 和登录验证码。取自 Roblox 面向网络管理员的帮助文章，均已在 '
        'DNS 中验证。游戏本身通过 UDP 连接 IP 地址，需要“Roblox：IP 网段”列表和带有 UDP 规则的策略。'),
    'zaprett-roblox-full': (
        'Roblox（扩展）',
        '扩展的 Roblox 列表（{n} 个）：基本列表，另加 Roblox 证书和页面代码中的其他 Roblox 域名。请用它替代基本列表。'),
    'zaprett-roblox-ipset': (
        'Roblox：IP 网段',
        'Roblox 的 IP 网段（{n} 个，IPv4 地址：{v4}）：Roblox 自治系统（AS22697，ROBLOX-PRODUCTION）宣告的网段'
        '（据 RIPEstat）。游戏服务器使用 UDP 49152–65535；需要带有按 IP 列表的 UDP 规则的策略（例如 '
        'strategy-alt2-roblox）。'),
    'zaprett-signal': (
        'Signal',
        'Signal 域名（{n} 个）：signal.org（消息、通话、更新）和链接域名。取自 Signal 关于防火墙设置的帮助文章，均已在 '
        'DNS 中验证。'),
    'zaprett-signal-full': (
        'Signal（扩展）',
        '扩展的 Signal 列表（{n} 个）：基本列表，另加证书和页面中的贴纸、视频及其他 Signal 域名。请用它替代基本列表。'),
    'zaprett-cloudflare-ipset': (
        'Cloudflare：IP 网段（IPv4）',
        '来自 www.cloudflare.com/ips-v4 的 Cloudflare 官方 IPv4 网段（{n} 个，地址：{v4}）——软件包内的快照，无需下载'
        '即可使用。作用于所有 Cloudflare 后的网站。'),
    'zaprett-cloudflare-ipset6': (
        'Cloudflare：IP 网段（IPv6）',
        '来自 www.cloudflare.com/ips-v6 的 Cloudflare 官方 IPv6 网段（{n} 个）——软件包内的快照。如果您有 IPv6，'
        '需要此列表。'),
}
