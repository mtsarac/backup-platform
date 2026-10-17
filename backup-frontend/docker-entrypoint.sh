#!/bin/sh
set -e

CERT_DIR="/etc/nginx/certs"
mkdir -p "$CERT_DIR"

if [ ! -f "$CERT_DIR/cert.pem" ] || [ ! -f "$CERT_DIR/key.pem" ]; then
    SAN="DNS:localhost,IP:127.0.0.1"
    if [ -n "$PUBLIC_HOST" ]; then
        case "$PUBLIC_HOST" in
            *[!0-9.]*) SAN="$SAN,DNS:$PUBLIC_HOST" ;;
            *) SAN="$SAN,IP:$PUBLIC_HOST" ;;
        esac
    fi
    if [ -n "$EXTRA_SANS" ]; then
        SAN="$SAN,$EXTRA_SANS"
    fi

    echo "Generating persistent self-signed Root CA certificate with SAN: $SAN"
    openssl req -x509 -newkey rsa:2048 -nodes \
        -keyout "$CERT_DIR/key.pem" \
        -out "$CERT_DIR/cert.pem" \
        -days 3650 \
        -subj "/CN=BackupPlatform" \
        -addext "subjectAltName=$SAN" \
        -addext "basicConstraints=critical,CA:TRUE" \
        -addext "keyUsage=critical,digitalSignature,keyEncipherment,keyCertSign"

    chmod 644 "$CERT_DIR/cert.pem"
    chmod 600 "$CERT_DIR/key.pem"
    echo "Certificate generated."
else
    echo "Existing certificate found; preserved."
fi

# Ensure downloadable certificate file on HTTP /cert.crt
cp -f "$CERT_DIR/cert.pem" /usr/share/nginx/html/cert.crt
chmod 644 /usr/share/nginx/html/cert.crt

exec nginx -g "daemon off;"
