#!/usr/bin/env bash
# Runs the real nginx.conf against a stub API and checks the security headers that a forged X-Forwarded-For never reaches the API, and that X-Client-Cert is always rewritten from the TLS handshake (any client-supplied value is dropped).
set -euo pipefail
cd "$(dirname "$0")/.."
IMAGE=$(sed -n 's/^FROM \(nginx:alpine@sha256:[0-9a-f]*\).*/\1/p' Dockerfile)
tmp=$(mktemp -d); net="nginx-smoke-$$"
cleanup() { docker rm -f "$net-api" "$net-web" >/dev/null 2>&1 || true; docker network rm "$net" >/dev/null 2>&1 || true; rm -rf "$tmp"; }
trap cleanup EXIT
mkdir -p "$tmp/certs" "$tmp/html"
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$tmp/certs/key.pem" -out "$tmp/certs/cert.pem" -days 1 -subj /CN=localhost 2>/dev/null
echo ok > "$tmp/html/index.html"
cat > "$tmp/api.conf" <<'C'
server { listen 5000; location / { add_header X-Seen-Xff "$http_x_forwarded_for" always; add_header X-Seen-Cert "$http_x_client_cert" always; return 200 "api $request_uri"; } }
C
docker network create "$net" >/dev/null
docker run -d --name "$net-api" --network "$net" --network-alias api -v "$tmp/api.conf:/etc/nginx/conf.d/default.conf:ro" "$IMAGE" >/dev/null
docker run -d --name "$net-web" --network "$net" -p 127.0.0.1:0:443 \
  -v "$PWD/nginx.conf:/etc/nginx/conf.d/default.conf:ro" -v "$PWD/snippets:/etc/nginx/snippets:ro" \
  -v "$tmp/certs:/etc/nginx/certs:ro" -v "$tmp/html:/usr/share/nginx/html:ro" "$IMAGE" >/dev/null
port=$(docker port "$net-web" 443/tcp | head -1 | sed 's/.*://')
for _ in $(seq 30); do curl -ksf "https://127.0.0.1:$port/" >/dev/null && break; sleep 0.5; done

fail=0
check() { if ! grep -qi "$2" <<<"$1"; then echo "FAIL: missing $2"; fail=1; fi; }
spa=$(curl -ksi "https://127.0.0.1:$port/")
for h in "content-security-policy:.*frame-ancestors 'none'" "strict-transport-security:" "x-content-type-options: nosniff" "x-frame-options: DENY" "referrer-policy: no-referrer"; do check "$spa" "$h"; done
api=$(curl -ksi -H 'X-Forwarded-For: 6.6.6.6' "https://127.0.0.1:$port/api/features")
seen=$(grep -i '^x-seen-xff:' <<<"$api" | tr -d '\r' | sed 's/^[^:]*: *//')
if [ -z "$seen" ] || grep -q 6.6.6.6 <<<"$seen" || [[ "$seen" == *,* ]]; then echo "FAIL: forwarded-for reached API as '$seen'"; fail=1; fi
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$tmp/c.key" -out "$tmp/c.pem" -days 1 -subj /CN=agent 2>/dev/null
seen_cert() { { grep -i '^x-seen-cert:' <<<"$1" || true; } | tr -d '\r' | sed 's/^[^:]*: *//'; }
for path in /api/agent/me /hubs/agent; do
  forged=$(seen_cert "$(curl -ksi -H 'X-Client-Cert: forged' "https://127.0.0.1:$port$path")")
  [ -z "$forged" ] || { echo "FAIL: forged X-Client-Cert reached API on $path as '$forged'"; fail=1; }
  real=$(seen_cert "$(curl -ksi -H 'X-Client-Cert: forged' --cert "$tmp/c.pem" --key "$tmp/c.key" "https://127.0.0.1:$port$path")")
  [[ "$real" == *BEGIN%20CERTIFICATE* ]] && [[ "$real" != *forged* ]] || { echo "FAIL: handshake certificate not forwarded on $path: '$real'"; fail=1; }
done
[ "$fail" = 0 ] && echo "nginx smoke ok (api saw X-Forwarded-For=$seen)"
exit "$fail"
