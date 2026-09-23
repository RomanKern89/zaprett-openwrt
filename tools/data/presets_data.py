"""Содержимое /usr/share/zaprett/presets.json (docs/ARCHITECTURE.md §9).

Источник фактов — research/03-lists.md §6 и §9. Обоснование выбора быстрых стратегий — docs/LISTS.md.
build_bundle.py проверяет этот набор (bundle_checks.validate_presets) и записывает JSON.
"""
import i18n_zh
import nfqws2_strategies

YT_PAGE = 'https://www.youtube.com/'
YT_THUMB = 'https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg'
YT_VIDEO_HOST = 'https://redirector.googlevideo.com/report_mapping?di=no'
DISCORD_PAGE = 'https://discord.com/'
DISCORD_GATEWAY = 'https://gateway.discord.gg/'
TELEGRAM_PAGE = 'https://telegram.org/'
RUTRACKER_PAGE = 'https://rutracker.org/forum/index.php'
CLOUDFLARE_100K = 'https://speed.cloudflare.com/__down?bytes=102400'
ROBLOX_PAGE = 'https://www.roblox.com/'
SIGNAL_PAGE = 'https://signal.org/'

# Размер тела ответа в байтах: замер 2026-09-17, curl --http1.1 -A uclient-fetch, по 3 запроса
# (разброс указан в docs/LISTS.md). min_bytes > 0 обязан лежать строго между 16384 и этим размером.
REFERENCE_SIZES = {
    YT_PAGE: 928951,
    YT_THUMB: 21011,
    DISCORD_PAGE: 170127,
    TELEGRAM_PAGE: 19918,
    RUTRACKER_PAGE: 96334,
    CLOUDFLARE_100K: 102400,
    ROBLOX_PAGE: 62681,      # замер 2026-09-22: 62 681–62 744 байт в 5 запросах
    SIGNAL_PAGE: 22194,      # замер 2026-09-22: 22 194 байт в 5 запросах
}

NO_BYPASS_IP = ('zaprett здесь не поможет: сервис заблокирован по IP-адресам серверов, а zaprett обходит только '
                'анализ содержимого трафика (DPI). Нужен VPN или прокси.')


def _service(sid, name, description, works, note, lists=(), ipsets=(), sources=(), tier='light', targets=()):
    return {
        'id': sid,
        'name': name,
        'description': description,
        'lists': list(lists),
        'ipsets': list(ipsets),
        'sources': list(sources),
        'tier': tier,
        'test_targets': [{'url': url, 'min_bytes': mb} for url, mb in targets],
        'works': works,
        'note': note,
    }


SERVICES = [
    _service(
        'youtube', 'YouTube', 'Сайт и приложение YouTube: страницы, видео и превью.', 'yes',
        'Замедление YouTube устроено через анализ имени сайта в зашифрованном соединении (DPI) — именно это '
        'обходит zaprett. Если видео всё равно тормозит, запустите автоподбор стратегии: у разных провайдеров '
        'срабатывают разные.',
        lists=['zaprett-youtube'],
        targets=[(YT_PAGE, 131072), (YT_THUMB, 17000), (YT_VIDEO_HOST, 0)]),
    _service(
        'discord', 'Discord', 'Сайт и приложение Discord: чаты, картинки и голосовые звонки.', 'yes',
        'Сайт и чаты обрабатываются по именам сайтов. Голосовые звонки идут без имени сайта, их обрабатывает '
        'отдельное правило стратегии по портам UDP 19294–19344 и 50000–50100 (значения из набора Flowseal, '
        'официального списка портов у Discord нет). Если чат работает, а голос нет, попробуйте другую '
        'стратегию через автоподбор.',
        lists=['zaprett-discord'],
        targets=[(DISCORD_PAGE, 65536), (DISCORD_GATEWAY, 0)]),
    _service(
        'telegram', 'Telegram', 'Сайт telegram.org, ссылки t.me и приложение Telegram.', 'partial',
        'Сайты Telegram обрабатываются по именам. Приложение соединяется с серверами напрямую по IP-адресам, '
        'без имени сайта, поэтому для него включается список IP-сетей Telegram. По словам автора zapret '
        '(март 2026 года), замедление приложения идёт по IP-адресам, и обход DPI против него не помогает — '
        'нужен VPN или прокси. Автоподбор проверяет только сайт telegram.org, работу самого приложения он не '
        'проверяет.',
        lists=['zaprett-telegram'], ipsets=['zaprett-telegram-ipset'],
        targets=[(TELEGRAM_PAGE, 17000)]),
    _service(
        'rutracker', 'RuTracker', 'Торрент-трекер RuTracker.', 'partial',
        'Помогает, если провайдер блокирует RuTracker анализом трафика. Если же он подменяет ответ DNS, '
        'одного zaprett мало: включите на роутере шифрованный DNS (например, пакет https-dns-proxy). '
        'Работает ли обход у вашего провайдера, покажет автоподбор.',
        lists=['zaprett-rutracker'],
        targets=[(RUTRACKER_PAGE, 32768)]),
    _service(
        'cloudflare', 'Сайты за Cloudflare',
        'Зарубежные сайты, у которых грузится только начало страницы, а дальше загрузка зависает.', 'partial',
        'С июня 2025 года у сайтов за Cloudflare в России загружаются только первые 16 КБ данных, после чего '
        'соединение замирает. Мастер подключит официальные диапазоны IP-адресов Cloudflare: 22 сети, памяти '
        'почти не занимают. Насколько обход помогает против этого ограничения — не проверено. Правило действует '
        'на все сайты за Cloudflare, в том числе российские: если какой-то сайт перестал открываться, выключите '
        'этот пункт. Сайты на Hetzner, DigitalOcean и OVH ограничены так же, но этим пунктом не покрываются.',
        sources=['cloudflare_v4', 'cloudflare_v6'],
        targets=[(CLOUDFLARE_100K, 65536)]),
    _service(
        'roblox', 'Roblox', 'Игровая платформа Roblox: сайт, приложение и игры.', 'partial',
        'Roblox заблокирован в России 3 декабря 2025 года. Сайт и вход обрабатываются по именам сайтов, а сами '
        'игры идут по UDP на серверы Roblox: для них включается список IP-сетей Roblox, и нужна стратегия с '
        'правилом для UDP (strategy-alt2-roblox или strategy-fake-tls-auto-alt3-roblox). Помогает ли обход у '
        'вашего провайдера — не проверено; автоподбор проверяет только сайт.',
        lists=['zaprett-roblox'], ipsets=['zaprett-roblox-ipset'],
        targets=[(ROBLOX_PAGE, 32768)]),
    _service(
        'signal', 'Signal', 'Мессенджер Signal: сообщения и звонки.', 'partial',
        'Signal заблокирован в России 9 августа 2024 года. Как устроено ограничение, в открытых источниках не '
        'описано, поэтому помогает ли обход — не проверено. Звонкам нужны UDP-порты 3478 и 10000, которые '
        'стратегии по умолчанию не обрабатывают. В самом приложении Signal есть поддержка прокси.',
        lists=['zaprett-signal'],
        targets=[(SIGNAL_PAGE, 17000)]),
    _service(
        'rkn_full', 'Прочие заблокированные сайты',
        'Большой список заблокированных в России сайтов (около 81 тыс. доменов и 17 тыс. IP-сетей) '
        'с автообновлением.', 'partial',
        'Списки загружаются из интернета (Re:filter и antifilter.download) и обновляются раз в несколько дней. '
        'Для работы nfqws им нужно около 10 МиБ оперативной памяти (замер на 64-битной системе; на 32-битных '
        'роутерах ожидается меньше, но это не проверено) и около 1,6 МБ свободного места во флеш-памяти. '
        'Не включайте на роутерах, где меньше 200 МБ оперативной памяти. Многие сайты из этих списков '
        'заблокированы по IP-адресу или через DNS — против таких блокировок zaprett не поможет. При автоподборе '
        'проверяются несколько доменов из самого списка.',
        sources=['refilter_domains', 'antifilter_allyouneed'], tier='full'),
    _service(
        'whatsapp', 'WhatsApp', 'Мессенджер WhatsApp: сообщения и звонки.', 'no',
        'zaprett здесь не поможет. В феврале 2026 года домены WhatsApp исключили из национальной системы '
        'доменных имён (НСДИ) — это блокировка на уровне DNS, а не анализа трафика. Нужен шифрованный DNS на '
        'роутере или VPN. Помогает ли обход против ограничения звонков — не проверено.'),
    _service(
        'instagram_facebook', 'Instagram и Facebook', 'Социальные сети Instagram и Facebook.', 'no',
        NO_BYPASS_IP),
    _service(
        'twitter', 'X (Twitter)', 'Социальная сеть X, бывший Twitter.', 'no',
        NO_BYPASS_IP),
    _service(
        'chatgpt_claude', 'ChatGPT и Claude', 'Нейросети ChatGPT (OpenAI) и Claude (Anthropic).', 'no',
        'zaprett здесь не поможет: доступ из России закрывают сами сервисы — России нет в списке поддерживаемых '
        'стран OpenAI и Anthropic. Нужен VPN.'),
    _service(
        'spotify', 'Spotify', 'Музыкальный сервис Spotify.', 'no',
        'zaprett здесь не поможет: Spotify сам прекратил работу в России в апреле 2022 года. Нужен VPN.'),
]

# Английские тексты сервисов (§14.6): name_en, description_en, note_en — после note, в этом порядке.
SERVICES_EN = {
    'youtube': (
        'YouTube',
        'YouTube website and app: pages, videos and thumbnails.',
        'YouTube is slowed down by analysing the site name inside the encrypted connection (DPI), and this is'
        ' exactly what zaprett bypasses. If videos still stutter, run the automatic strategy selection: '
        'different providers need different strategies.',
    ),
    'discord': (
        'Discord',
        'Discord website and app: chats, images and voice calls.',
        'The website and chats are handled by site names. Voice calls carry no site name, so a separate rule '
        'of the strategy handles them by UDP ports 19294–19344 and 50000–50100 (values from the Flowseal set;'
        ' Discord publishes no official port list). If chat works but voice does not, try another strategy '
        'with the automatic selection.',
    ),
    'telegram': (
        'Telegram',
        'The telegram.org website, t.me links and the Telegram app.',
        'Telegram websites are handled by names. The app connects to its servers directly by IP address, '
        'without a site name, so the list of Telegram IP networks is switched on for it. According to the author '
        'of zapret (March 2026), the app is throttled by IP address and DPI bypass does not help against it: a '
        'VPN or a proxy is needed. The automatic selection checks only the telegram.org website, not the app '
        'itself.',
    ),
    'rutracker': (
        'RuTracker',
        'The RuTracker torrent tracker.',
        'Helps if your provider blocks RuTracker by analysing traffic. If the provider forges DNS answers '
        'instead, zaprett alone is not enough: enable encrypted DNS on the router (for example, the '
        'https-dns-proxy package). The automatic selection shows whether the bypass works with your provider.',
    ),
    'cloudflare': (
        'Sites behind Cloudflare',
        'Foreign websites that load only the beginning of a page and then hang.',
        'Since June 2025, sites behind Cloudflare in Russia load only the first 16 KB of data, after which '
        'the connection stalls. The wizard adds the official Cloudflare IP ranges: 22 networks that take '
        'almost no memory. How much the bypass helps against this restriction has not been verified. The rule'
        ' applies to every site behind Cloudflare, Russian ones included: if some site stops opening, switch '
        'this item off. Sites on Hetzner, DigitalOcean and OVH are restricted the same way but are not '
        'covered by this item.',
    ),
    'roblox': (
        'Roblox',
        'The Roblox gaming platform: website, app and games.',
        'Roblox has been blocked in Russia since 3 December 2025. The website and login are handled by site '
        'names, while the games themselves use UDP to Roblox servers: the list of Roblox IP networks is switched '
        'on for them, and a strategy with a UDP rule is needed (strategy-alt2-roblox or '
        'strategy-fake-tls-auto-alt3-roblox). Whether the bypass works with your provider has not been verified;'
        ' the automatic selection checks only the website.',
    ),
    'signal': (
        'Signal',
        'The Signal messenger: messages and calls.',
        'Signal has been blocked in Russia since 9 August 2024. How the restriction works is not described in '
        'public sources, so whether the bypass helps has not been verified. Calls need UDP ports 3478 and 10000,'
        ' which the default strategies do not handle. The Signal app itself supports proxies.',
    ),
    'rkn_full': (
        'Other blocked websites',
        'A large list of websites blocked in Russia (about 81 thousand domains and 17 thousand IP networks) '
        'with automatic updates.',
        'The lists are downloaded from the internet (Re:filter and antifilter.download) and updated every few'
        ' days. nfqws needs about 10 MiB of RAM for them (measured on a 64-bit system; less is expected on '
        '32-bit routers, but this has not been verified) and about 1.6 MB of free flash space. Do not enable '
        'this on routers with less than 200 MB of RAM. Many sites in these lists are blocked by IP address or'
        ' through DNS, and zaprett cannot help against such blocking. The automatic selection checks a few '
        'domains from the list itself.',
    ),
    'whatsapp': (
        'WhatsApp',
        'The WhatsApp messenger: messages and calls.',
        'zaprett will not help here. In February 2026 the WhatsApp domains were removed from the national '
        'domain name system (NSDI): this is blocking at the DNS level, not traffic analysis. You need '
        'encrypted DNS on the router or a VPN. Whether the bypass helps against the throttling of calls has '
        'not been verified.',
    ),
    'instagram_facebook': (
        'Instagram and Facebook',
        'The Instagram and Facebook social networks.',
        'zaprett will not help here: the service is blocked by server IP addresses, while zaprett only '
        'bypasses traffic content analysis (DPI). You need a VPN or a proxy.',
    ),
    'twitter': (
        'X (Twitter)',
        'The X social network, formerly Twitter.',
        'zaprett will not help here: the service is blocked by server IP addresses, while zaprett only '
        'bypasses traffic content analysis (DPI). You need a VPN or a proxy.',
    ),
    'chatgpt_claude': (
        'ChatGPT and Claude',
        'The ChatGPT (OpenAI) and Claude (Anthropic) AI assistants.',
        'zaprett will not help here: access from Russia is closed by the services themselves, since Russia is'
        ' not among the countries supported by OpenAI and Anthropic. You need a VPN.',
    ),
    'spotify': (
        'Spotify',
        'The Spotify music service.',
        'zaprett will not help here: Spotify itself stopped operating in Russia in April 2022. You need a '
        'VPN.',
    ),
}

# Сервисы, которым нужен шифрованный DNS (§15.3): поле needs_dns идёт после *_en.
NEEDS_DNS = ('rutracker', 'rkn_full', 'whatsapp')



def _variant(vid, name, name_en, description, description_en, lists=(), ipsets=(), tier='light'):
    return {'id': vid, 'name': name, 'name_en': name_en, 'name_zh': None, 'description': description,
            'description_en': description_en, 'description_zh': None, 'lists': list(lists), 'ipsets': list(ipsets),
            'tier': tier}


FULL_RU, FULL_EN = 'Расширенный', 'Extended'

# §16.3: альтернативные наборы списков сервиса; основной набор — lists/ipsets самого сервиса
VARIANTS = {
    'youtube': [
        _variant('full', FULL_RU, FULL_EN,
                 'Больше доменов: служебные узлы API и домены приложений YouTube.',
                 'More domains: auxiliary API hosts and YouTube app domains.',
                 lists=['zaprett-youtube-full']),
    ],
    'discord': [
        _variant('full', FULL_RU, FULL_EN,
                 'Больше доменов: служебный домен discord.tools из кода клиента Discord.',
                 'More domains: the auxiliary discord.tools domain from the Discord client code.',
                 lists=['zaprett-discord-full']),
        _variant('voice', 'С голосовыми серверами', 'With voice servers',
                 'Основные домены плюс IP-сети голосовых серверов Discord — для стратегий с правилом UDP по '
                 'IP-спискам.',
                 'The core domains plus the IP networks of the Discord voice servers, for strategies with a UDP '
                 'rule by IP lists.',
                 lists=['zaprett-discord'], ipsets=['zaprett-discord-voice']),
    ],
    'rutracker': [
        _variant('full', FULL_RU, FULL_EN,
                 'Больше доменов: служебный домен RuTracker rutrk.org.',
                 'More domains: the auxiliary RuTracker domain rutrk.org.',
                 lists=['zaprett-rutracker-full']),
    ],
    'cloudflare': [
        _variant('bundled', 'Снимок в пакете', 'Built-in snapshot',
                 'Те же официальные диапазоны Cloudflare из снимка в пакете: работает без загрузки из интернета, '
                 'но не обновляется сам.',
                 'The same official Cloudflare ranges from the snapshot inside the package: works without '
                 'downloading, but is not updated automatically.',
                 ipsets=['zaprett-cloudflare-ipset', 'zaprett-cloudflare-ipset6']),
    ],
    'roblox': [
        _variant('full', FULL_RU, FULL_EN,
                 'Больше доменов: CDN, сайты для разработчиков и прочие домены Roblox, плюс IP-сети.',
                 'More domains: CDN, developer sites and other Roblox domains, plus the IP networks.',
                 lists=['zaprett-roblox-full'], ipsets=['zaprett-roblox-ipset']),
    ],
    'signal': [
        _variant('full', FULL_RU, FULL_EN,
                 'Больше доменов: стикеры, видео и форум Signal.',
                 'More domains: stickers, video and the Signal forum.',
                 lists=['zaprett-signal-full']),
    ],
}

for _svc in SERVICES:
    _svc['name_en'], _svc['description_en'], _svc['note_en'] = SERVICES_EN[_svc['id']]
    _svc['name_zh'], _svc['description_zh'], _svc['note_zh'] = i18n_zh.SERVICES_ZH[_svc['id']]
    if _svc['id'] in NEEDS_DNS:
        _svc['needs_dns'] = True
    if _svc['id'] in VARIANTS:
        _svc['variants'] = VARIANTS[_svc['id']]
        for _v in _svc['variants']:
            _v['name_zh'], _v['description_zh'] = i18n_zh.VARIANTS_ZH[(_svc['id'], _v['id'])]

# Обоснование состава и порядка — docs/LISTS.md, раздел «Быстрый автоподбор».
QUICK_TEST_STRATEGIES = [
    'strategy-general',
    'strategy-alt',
    'strategy-alt2',
    'strategy-alt3',
    'strategy-alt4',
    'strategy-alt8',
    'strategy-alt11',
    'strategy-fake-tls-auto-alt',
    'strategy-fake-tls-auto-alt3',
    'strategy-simple-fake',
    'strategy-discord-fix',
    'strategy-youtubefix-alt',
]

PRESETS = {
    'schema': 1,
    'services': SERVICES,
    'always': {'exclude_lists': ['zaprett-exclude'], 'exclude_ipsets': ['zaprett-exclude-ipset']},
    'tiers': {'light': {'min_ram_mib': 0}, 'full': {'min_ram_mib': 200}},
    'defaults': {
        'services': ['youtube', 'discord'],
        'strategy': 'strategy-general',
        'quick_test_strategies': QUICK_TEST_STRATEGIES,
        # §15.6: стратегии nfqws2, обоснование — docs/LISTS.md §6.4
        'strategy_nfqws2': nfqws2_strategies.DEFAULT_STRATEGY,
        'quick_test_strategies_nfqws2': list(nfqws2_strategies.QUICK_TEST_STRATEGIES),
    },
}
