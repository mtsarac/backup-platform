#!/usr/bin/env bash
# Runs Chromium in its own user+network namespace (pasta) so host link/address/route events
# (docker, libvirt, WARP, NetBird) never reach Chromium's NetworkChangeNotifier (#306, ERR_NETWORK_CHANGED).
# pasta cannot wrap the command itself: it closes fds 3/4, which Playwright uses as the CDP pipe.
# So create the namespaces first, attach pasta to them, then let Chromium start inside.
# Falls back to plain Chromium when pasta is missing or E2E_NETNS=0.
chromium="${CHROMIUM_REAL_PATH:-/usr/bin/chromium}"
if [ "${E2E_NETNS:-1}" = 0 ] || ! command -v pasta >/dev/null; then exec "$chromium" "$@"; fi
unshare -Urn sh -c 'kill -STOP $$; exec "$@"' sh "$chromium" "$@" &
pid=$!
trap 'kill -TERM $pid 2>/dev/null' TERM INT
while ! grep -q '^State:.*T' /proc/$pid/status 2>/dev/null; do sleep 0.01; done
pasta --config-net -q "$pid" || { kill -KILL "$pid"; exit 1; }
kill -CONT "$pid"
wait "$pid"
