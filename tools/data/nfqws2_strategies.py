"""Стратегии движка nfqws2 (zapret2 v1.0.5.2) для bundle — docs/ARCHITECTURE.md §15.6, docs/LISTS.md.

Тексты лежат в tools/data/nfqws2/<id>.txt, здесь — метаданные манифестов. build_bundle.py копирует тексты в
bundle/files/strategies/nfqws2/, пишет манифесты и проверяет их (bundle_checks.check_strategy2): опции nfqws2,
имена Lua-функций, типы пейлоадов и протоколов, плейсхолдеры и зависимости. Окончательная проверка —
`nfqws2 --intercept=0` на роутере (tests/test_bundle.uc).

Перевод приёмов nfqws1 (zapret v72.13) в nfqws2:
  --dpi-desync=fake,<split>        два инстанса --lua-desync: fake и multisplit/multidisorder/fakedsplit
  --dpi-desync-fooling=md5sig      tcp_md5
  --dpi-desync-fooling=badseq      tcp_seq=-10000:tcp_ack=-66000 (BADSEQ_INCREMENT_DEFAULT, BADSEQ_ACK_INCREMENT_DEFAULT)
  --dpi-desync-badseq-increment=N  tcp_seq=N
  --dpi-desync-fooling=ts          tcp_ts=-600000 (TS_INCREMENT_DEFAULT)
  --dpi-desync-autottl=2           ip_autottl=-2,3-20 (в nfqws1 число без знака — отрицательная дельта)
  --dpi-desync-autottl             ip_autottl=-1,3-20
  --dpi-desync-repeats=N           repeats=N у инстанса fake
  --dpi-desync-fake-tls=${bin:x}   --blob=<имя>:@${bin:x} и blob=<имя>
  fake для discord/stun/udp        64 нулевых байта (как умолчание nfqws1), блоб z64
  --dpi-desync-any-protocol --dpi-desync-cutoff=d3   --out-range=<d3 и payload=all у fake
  split → fakedsplit, split2 → multisplit (устаревшие режимы nfqws1)
Фулинг в nfqws1 действовал только на фейки; в nfqws2 аргументы у каждого инстанса свои, поэтому фулинг стоит
только у fake и у fakedsplit/hostfakesplit (где он применяется к фейковым частям), но не у multisplit/multidisorder.
"""

AUTHOR = 'zaprett для OpenWrt'
VERSION = '2026.09.22'

QUIC = 'quic_initial_www_google_com'
TLS_GOOGLE = 'tls_clienthello_www_google_com'

_COMMON_RU = ('QUIC (UDP 443) — фейк QUIC Initial Google 6 раз; голос Discord (UDP 19294–19344, 50000–50100) — '
              'фейк из нулей 6 раз; HTTP — фейк с MD5 и autottl, затем разрезание запроса.')
_COMMON_EN = ('QUIC (UDP 443) gets a Google QUIC Initial fake 6 times; Discord voice (UDP 19294–19344, 50000–50100) '
              'gets a zero-filled fake 6 times; HTTP gets an MD5 + autottl fake followed by a split request.')

STRATEGIES = [
    {
        'id': 'z2-general', 'deps': [QUIC],
        'name': 'nfqws2: general',
        'description': 'Перенос стратегии strategy-general (Flowseal) на nfqws2. TLS: фейковый ClientHello с '
                       'фулингом MD5 и неверным номером последовательности (8 повторов), затем настоящий '
                       'ClientHello режется посередине домена и отправляется частями в обратном порядке '
                       '(multidisorder). ' + _COMMON_RU + ' Рекомендуется начать с неё.',
        'name_en': 'nfqws2: general',
        'description_en': 'Port of strategy-general (Flowseal) to nfqws2. TLS: a fake ClientHello with MD5 and '
                          'bad-sequence fooling (8 repeats), then the real ClientHello is cut in the middle of the '
                          'domain and sent in reverse order (multidisorder). ' + _COMMON_EN + ' Start with this one.',
    },
    {
        'id': 'z2-alt', 'deps': [QUIC, TLS_GOOGLE],
        'name': 'nfqws2: alt',
        'description': 'Перенос strategy-alt. TLS: фейк — ClientHello www.google.com с испорченной меткой времени '
                       'TCP (6 повторов), затем fakedsplit: настоящие части ClientHello перемешаны с фейковыми '
                       'такого же размера. ' + _COMMON_RU + ' Для IP-списков действует и на TLS.',
        'name_en': 'nfqws2: alt',
        'description_en': 'Port of strategy-alt. TLS: a www.google.com ClientHello fake with a broken TCP '
                          'timestamp (6 repeats), then fakedsplit mixes the real ClientHello parts with fake parts '
                          'of the same size. ' + _COMMON_EN + ' IP lists get the TLS rule too.',
    },
    {
        'id': 'z2-alt2', 'deps': [QUIC, TLS_GOOGLE],
        'name': 'nfqws2: alt2 (seqovl)',
        'description': 'Перенос strategy-alt2. TLS без отдельных фейков: ClientHello режется после 2-го байта, к '
                       'первой части слева приклеиваются 652 байта ClientHello www.google.com за пределами окна '
                       'TCP (seqovl) — сервер их отбрасывает, а DPI видит чужое имя сайта. ' + _COMMON_RU +
                       ' Подходит, когда фейки с фулингом не проходят.',
        'name_en': 'nfqws2: alt2 (seqovl)',
        'description_en': 'Port of strategy-alt2. TLS without separate fakes: the ClientHello is cut after byte 2 and '
                          '652 bytes of a www.google.com ClientHello are prepended to the first part outside the TCP '
                          'window (seqovl): the server drops them while DPI sees another site name. ' + _COMMON_EN +
                          ' Useful when fooled fakes do not get through.',
    },
    {
        'id': 'z2-alt3', 'deps': [QUIC],
        'name': 'nfqws2: alt3',
        'description': 'Перенос strategy-alt3. TLS: fakedsplit по 1-му байту — части ClientHello вперемешку с '
                       'фейками, фейки с неверными номерами последовательности и TTL, подобранным по расстоянию '
                       'до сервера (autottl), 8 повторов. ' + _COMMON_RU,
        'name_en': 'nfqws2: alt3',
        'description_en': 'Port of strategy-alt3. TLS: fakedsplit at byte 1, ClientHello parts mixed with fakes '
                          'that carry bad sequence numbers and a TTL guessed from the distance to the server '
                          '(autottl), 8 repeats. ' + _COMMON_EN,
    },
    {
        'id': 'z2-simple-fake', 'deps': [QUIC, TLS_GOOGLE],
        'name': 'nfqws2: simple fake',
        'description': 'Перенос strategy-simple-fake. TLS: только фейковый ClientHello www.google.com с испорченной '
                       'меткой времени TCP, 6 повторов; настоящий ClientHello уходит целиком. ' + _COMMON_RU +
                       ' Самая лёгкая для процессора роутера.',
        'name_en': 'nfqws2: simple fake',
        'description_en': 'Port of strategy-simple-fake. TLS: only a www.google.com ClientHello fake with a broken '
                          'TCP timestamp, 6 repeats; the real ClientHello is sent whole. ' + _COMMON_EN +
                          ' The lightest one for the router CPU.',
    },
    {
        'id': 'z2-fake-tls-auto', 'deps': [QUIC],
        'name': 'nfqws2: fake TLS auto',
        'description': 'Перенос strategy-general-fake-tls-auto. TLS и HTTP: фейк с MD5 и TTL по расстоянию до '
                       'сервера (autottl), затем fakedsplit с тем же фулингом. QUIC для IP-списков — 11 повторов '
                       'фейка. Голос Discord — как в general. Если TTL до сервера определить не удалось, фейки '
                       'доходят до сервера и соединение может сломаться (так же ведёт себя исходная стратегия nfqws).',
        'name_en': 'nfqws2: fake TLS auto',
        'description_en': 'Port of strategy-general-fake-tls-auto. TLS and HTTP: a fake with MD5 and a TTL guessed '
                          'from the distance to the server (autottl), then fakedsplit with the same fooling. QUIC '
                          'for IP lists gets 11 fake repeats. Discord voice as in general. If the TTL to the server '
                          'cannot be found, the fakes reach the server and may break the connection (the original '
                          'nfqws strategy behaves the same way).',
    },
    {
        'id': 'z2-fake-tls-auto-alt', 'deps': [QUIC],
        'name': 'nfqws2: fake TLS auto alt',
        'description': 'Перенос strategy-fake-tls-auto-alt. TLS: фейковый ClientHello с изменёнными полями '
                       '(случайные данные, копия идентификатора сессии, имя www.google.com) и сдвигом номера '
                       'последовательности на 10 000 000, 8 повторов, затем fakedsplit по 1-му байту. QUIC — 11 '
                       'повторов фейка. HTTP и голос Discord — как в fake TLS auto.',
        'name_en': 'nfqws2: fake TLS auto alt',
        'description_en': 'Port of strategy-fake-tls-auto-alt. TLS: a fake ClientHello with modified fields (random '
                          'data, copied session id, www.google.com name) and the sequence number shifted by '
                          '10,000,000, 8 repeats, then fakedsplit at byte 1. QUIC gets 11 fake repeats. HTTP and '
                          'Discord voice as in fake TLS auto.',
    },
    {
        'id': 'z2-discord', 'deps': [QUIC, TLS_GOOGLE],
        'name': 'nfqws2: Discord',
        'description': 'Перенос strategy-discord с добавленным правилом голоса по протоколу. TLS: фейк www.google.com '
                       'с неверными номерами последовательности и autottl (6 повторов) и fakedsplit; голос: фейк '
                       'из нулей для пакетов Discord IP Discovery и STUN на портах 19294–19344 и 50000–50100, а для '
                       'адресов из IP-списков — фейк на первые 2 пакета любого UDP на 50000–50100. HTTP не трогает.',
        'name_en': 'nfqws2: Discord',
        'description_en': 'Port of strategy-discord with an extra protocol-based voice rule. TLS: a www.google.com fake '
                          'with bad sequence numbers and autottl (6 repeats) plus fakedsplit; voice: a zero-filled '
                          'fake for Discord IP Discovery and STUN packets on ports 19294–19344 and 50000–50100, and for '
                          'addresses from IP lists a fake on the first 2 packets of any UDP on 50000–50100. HTTP is '
                          'left alone.',
    },
    {
        'id': 'z2-ultimatefix', 'deps': [QUIC, TLS_GOOGLE],
        'name': 'nfqws2: ultimatefix',
        'description': 'Перенос strategy-ultimatefix. TLS: фейк www.google.com с неверными номерами '
                       'последовательности и autottl (6 повторов) и fakedsplit; HTTP: фейк с MD5 и разрезание; для '
                       'адресов из IP-списков — фейк на первые 2 пакета любого UDP на портах 50000–65535 (голос '
                       'и игры; широкий диапазон портов нагружает процессор).',
        'name_en': 'nfqws2: ultimatefix',
        'description_en': 'Port of strategy-ultimatefix. TLS: a www.google.com fake with bad sequence numbers and '
                          'autottl (6 repeats) plus fakedsplit; HTTP: an MD5 fake and a split; for addresses from IP '
                          'lists a fake on the first 2 packets of any UDP on ports 50000–65535 (voice and games; the '
                          'wide port range costs CPU).',
    },
    {
        'id': 'z2-ultimatefix-universal', 'deps': [QUIC],
        'name': 'nfqws2: ultimatefix universal',
        'description': 'Перенос strategy-ultimatefix-universal. TLS: только разрезание ClientHello после 1-го байта, '
                       'без фейков; HTTP: фейк с MD5 и разрезание; для адресов из IP-списков — фейк на первые 2 '
                       'пакета любого UDP на портах 50000–65535. Подходит там, где фейки ломают соединения.',
        'name_en': 'nfqws2: ultimatefix universal',
        'description_en': 'Port of strategy-ultimatefix-universal. TLS: only a split of the ClientHello after byte 1, '
                          'no fakes; HTTP: an MD5 fake and a split; for addresses from IP lists a fake on the first 2 '
                          'packets of any UDP on ports 50000–65535. Useful where fakes break connections.',
    },
    {
        'id': 'z2-hostfakesplit', 'deps': [QUIC],
        'name': 'nfqws2: hostfakesplit',
        'description': 'Приём, которого нет в nfqws1. TLS и HTTP: запрос режется по границам имени сайта, имя '
                       'дополнительно режется посередине домена, а рядом с настоящими частями отправляются фейки со '
                       'случайным именем, фулингом MD5 и неверными номерами последовательности (2 повтора). QUIC и '
                       'голос Discord — как в general.',
        'name_en': 'nfqws2: hostfakesplit',
        'description_en': 'A technique nfqws1 does not have. TLS and HTTP: the request is cut at the boundaries of the '
                          'site name, the name is also cut in the middle of the domain, and fakes with a random name '
                          'with MD5 fooling and bad sequence numbers are sent next to the real parts (2 repeats). QUIC '
                          'and Discord voice as in general.',
    },
    {
        'id': 'z2-disorder-seqovl', 'deps': [QUIC, TLS_GOOGLE],
        'name': 'nfqws2: disorder + seqovl',
        'description': 'Приём nfqws2 без фейков с фулингом. TLS и HTTP: запрос режется посередине домена, части '
                       'уходят в обратном порядке, а вторая часть начинается с перекрытия, заполненного данными '
                       'ClientHello www.google.com, — сервер перезаписывает его настоящими данными первой части. '
                       'Не работает с серверами на Windows. QUIC и голос Discord — как в general.',
        'name_en': 'nfqws2: disorder + seqovl',
        'description_en': 'An nfqws2 technique without fooled fakes. TLS and HTTP: the request is cut in the middle of '
                          'the domain, parts go in reverse order, and the second part starts with an overlap filled '
                          'with www.google.com ClientHello data that the server overwrites with the real first part. '
                          'Does not work with Windows servers. QUIC and Discord voice as in general.',
    },
    {
        'id': 'z2-circular', 'deps': [QUIC, TLS_GOOGLE],
        'name': 'nfqws2: auto-switch (circular)',
        'description': 'Оркестратор circular из zapret2: для каждого сайта отдельно считает неудачи (повторные '
                       'отправки ClientHello, сброс соединения) и после 3 неудач за минуту переключает сайт на '
                       'следующий набор приёмов по кругу: 1 — как general (фейк MD5 + multidisorder), 2 — как alt2 '
                       '(seqovl), 3 — как alt (фейк + fakedsplit), 4 — hostfakesplit. Действует на TLS (TCP 443) '
                       'по спискам сайтов; QUIC, HTTP и голос Discord — как в general. Запоминание сбрасывается '
                       'при перезапуске службы. Для учёта ответов сервера полезно поднять tcp_pkt_in до 10.',
        'name_en': 'nfqws2: auto-switch (circular)',
        'description_en': 'The zapret2 circular orchestrator: counts failures per site (ClientHello retransmissions, '
                          'connection resets) and after 3 failures within a minute moves the site to the next set of '
                          'techniques in a circle: 1 like general (MD5 fake + multidisorder), 2 like alt2 (seqovl), '
                          '3 like alt (fake + fakedsplit), 4 hostfakesplit. Applies to TLS (TCP 443) for site lists; '
                          'QUIC, HTTP and Discord voice as in general. The state is lost when the service restarts. '
                          'Raising tcp_pkt_in to 10 helps it see server replies.',
    },
    {
        'id': 'z2-circular-nofake', 'deps': [QUIC, TLS_GOOGLE],
        'name': 'nfqws2: auto-switch without fakes',
        'description': 'Оркестратор circular с приёмами без фейков с фулингом (для провайдеров, где фейки ломают '
                       'соединения): 1 — seqovl 652 байта (как alt2), 2 — multidisorder посередине домена с '
                       'перекрытием, 3 — разрезание на 3 части. Переключение сайта после 3 неудач за минуту. '
                       'Действует на TLS и HTTP по спискам сайтов и IP-спискам; QUIC — фейк 6 раз. Голос Discord '
                       'не обрабатывает.',
        'name_en': 'nfqws2: auto-switch without fakes',
        'description_en': 'The circular orchestrator with techniques that use no fooled fakes (for providers where '
                          'fakes break connections): 1 seqovl of 652 bytes (like alt2), 2 multidisorder in the middle '
                          'of the domain with an overlap, 3 a split into 3 parts. A site is switched after 3 failures '
                          'within a minute. Applies to TLS and HTTP for site and IP lists; QUIC gets a fake 6 times. '
                          'Discord voice is not handled.',
    },
]

# Рекомендации для presets.json (§15.6). Обоснование — docs/LISTS.md, раздел о стратегиях nfqws2.
DEFAULT_STRATEGY = 'z2-general'
QUICK_TEST_STRATEGIES = [
    'z2-general',
    'z2-alt',
    'z2-alt2',
    'z2-alt3',
    'z2-simple-fake',
    'z2-fake-tls-auto-alt',
    'z2-hostfakesplit',
    'z2-disorder-seqovl',
]
