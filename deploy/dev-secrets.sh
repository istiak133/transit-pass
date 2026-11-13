#!/bin/sh
# Creates or completes .env with random local secrets. Docker Compose reads .env automatically.
# .env is git-ignored, so no secret is ever committed. Safe to re-run: existing values are kept,
# and only missing ones are added (for example the JWT key, added after the passwords).
#   ./deploy/dev-secrets.sh
set -eu
cd "$(dirname "$0")/.."

umask 077
touch .env

add() { # add NAME VALUE, unless NAME is already in .env
  if ! grep -q "^$1=" .env; then
    printf '%s=%s\n' "$1" "$2" >> .env
    echo "added $1"
  fi
}

pw() { openssl rand -hex 24; }

add POSTGRES_SUPERUSER_PASSWORD "$(pw)"
add IDENTITY_DB_PASSWORD "$(pw)"
add NETWORK_DB_PASSWORD "$(pw)"
add CORE_DB_PASSWORD "$(pw)"
add PAYMENT_WEBHOOK_SECRET "$(openssl rand -hex 32)" # shared with the (fake) payment provider for HMAC

# ES256 signing key for Identity (EC P-256, PKCS#8 PEM). Stored on one line with \n escapes,
# which Compose turns back into newlines inside double quotes.
if ! grep -q "^JWT_SIGNING_KEY_PEM=" .env; then
  key=$(openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 2>/dev/null | awk '{printf "%s\\n", $0}')
  add JWT_SIGNING_KEY_PEM "\"$key\""
fi

# A SECOND, separate ES256 key: Core signs ticket QR credentials with it. Never the same as the JWT key,
# so a leaked login key can't mint tickets and a leaked QR key can't mint logins.
if ! grep -q "^QR_SIGNING_KEY_PEM=" .env; then
  key=$(openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 2>/dev/null | awk '{printf "%s\\n", $0}')
  add QR_SIGNING_KEY_PEM "\"$key\""
fi

# One key per gate device (one gate per station locally). The gate keeps the key (deploy/gate-tap.sh reads it);
# Core is given only its SHA-256, so Core's config holds nothing a thief could tap with.
for station in uttara-north agargaon karwan-bazar motijheel; do
  name=$(printf '%s' "$station" | tr 'a-z-' 'A-Z_')
  add "GATE_${name}_KEY" "$(openssl rand -hex 32)"
  key=$(grep "^GATE_${name}_KEY=" .env | cut -d= -f2-)
  add "GATE_${name}_KEY_SHA256" "$(printf '%s' "$key" | openssl dgst -sha256 -r | cut -d' ' -f1)"
done

echo ".env is ready."
