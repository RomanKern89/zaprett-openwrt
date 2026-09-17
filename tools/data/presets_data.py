"""Содержимое /usr/share/zaprett/presets.json (docs/ARCHITECTURE.md §9).

Источник фактов — research/03-lists.md §6 и §9. Обоснование выбора быстрых стратегий — docs/LISTS.md.
build_bundle.py проверяет этот набор (bundle_checks.validate_presets) и записывает JSON.
"""

YT_PAGE = 'https://www.youtube.com/'
YT_THUMB = 'https://i.ytimg.com/vi/dQw4w9WgXcQ/hqdefault.jpg'
YT_VIDEO_HOST = 'https://redirector.googlevideo.com/report_mapping?di=no'
DISCORD_PAGE = 'https://discord.com/'
DISCORD_GATEWAY = 'https://gateway.discord.gg/'
TELEGRAM_PAGE = 'https://telegram.org/'
RUTRACKER_PAGE = 'https://rutracker.org/forum/index.php'
CLOUDFLARE_100K = 'https://speed.cloudflare.com/__down?bytes=102400'

# Размер тела ответа в байтах: замер 2026-09-17, curl --http1.1 -A uclient-fetch, по 3 запроса
# (разброс указан в docs/LISTS.md). min_bytes > 0 обязан лежать строго между 16384 и этим размером.
REFERENCE_SIZES = {
    YT_PAGE: 928951,
    YT_THUMB: 21011,
    DISCORD_PAGE: 170127,
    TELEGRAM_PAGE: 19918,
    RUTRACKER_PAGE: 96334,
    CLOUDFLARE_100K: 102400,
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
        'без имени сайта, поэтому для него включается список IP-сетей Telegram. Помогает ли обход против '
        'текущего замедления медиа и звонков в Telegram — не проверено. Автоподбор проверяет только сайт '
        'telegram.org, работу самого приложения он не проверяет.',
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
    },
}
