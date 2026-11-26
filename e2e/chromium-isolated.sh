#!/usr/bin/env bash
# Runs Chromium in its own user+network namespace (pasta) so host link/address/route events
# (docker, libvirt, WARP, NetBird) never reach Chromium's NetworkChangeNotifier (#306, ERR_NETWORK_CHANGED).
# pasta cannot wrap the command itself: it closes fds 3/4, which Playwright uses as the CDP pipe.
# So create the namespaces first, attach pasta to them, then let Chromium start inside.
# Falls back to plain Chromium when pasta is missing or E2E_NETNS=0.
# Every launch gets its own NetLog (<path>.<pid>) and, when isolated, a timestamped log of the events inside the namespace (<path>.<pid>.ns.log).
chromium="${CHROMIUM_REAL_PATH:-/usr/bin/chromium}"
args=()
for a in "$@"; do
  case "$a" in --log-net-log=*) a="$a.$$"; nl="${a#--log-net-log=}";; esac
  args+=("$a")
done
if [ "${E2E_NETNS:-1}" = 0 ] || ! command -v pasta >/dev/null; then exec "$chromium" "${args[@]}"; fi
unshare -Urn sh -c 'kill -STOP $$; exec "$@"' sh "$chromium" "${args[@]}" &
pid=$!
trap 'kill -TERM $pid 2>/dev/null' TERM INT
while ! grep -q '^State:.*T' /proc/$pid/status 2>/dev/null; do sleep 0.01; done
pasta --config-net -q "$pid" || { kill -KILL "$pid"; exit 1; }
if [ -n "$nl" ]; then
  nsenter --preserve-credentials -U -n -t "$pid" ip -ts monitor link address route > "$nl.ns.log" 2>&1 &
  mon=$!
fi
kill -CONT "$pid"
wait "$pid"; rc=$?
[ -n "$mon" ] && kill "$mon" 2>/dev/null
exit $rc
