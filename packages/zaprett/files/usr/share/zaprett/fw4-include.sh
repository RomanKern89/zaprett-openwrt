# zaprett: fw4 script include (firewall.zaprett, fw4_compatible 1).
# fw4 sources this file after every firewall start/reload. `service firewall stop` deletes all nft
# tables, including inet zaprett; this restores the last applied script while the engine runs.
# The script is idempotent: it recreates the whole table in one transaction.
#
# The engine state is read from procd over ubus, not with `/etc/init.d/zaprett running`: every init
# action takes procd_lock (/var/lock/procd_zaprett.lock), fw4 closes the inherited lock fd before running
# includes, and zaprett's own start_service reloads the firewall (flow_offload=auto) while holding that
# lock — the include would block until fw4 kills it after 30 s.

if [ -s /var/run/zaprett/zaprett.nft ] && \
   [ "$(ubus call service list '{"name":"zaprett"}' 2>/dev/null | jsonfilter -e '@.zaprett.instances.engine.running' 2>/dev/null)" = "true" ]; then
	nft -f /var/run/zaprett/zaprett.nft || logger -t zaprett -p daemon.err "fw4 include: не удалось восстановить таблицу inet zaprett"
fi
