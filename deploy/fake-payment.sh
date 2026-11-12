#!/bin/sh
# Plays the payment provider: sends a signed "payment finished" webhook to TransitPass, the way a real
# provider calls us after the user pays on its checkout page. Uses the secret from .env.
#   ./deploy/fake-payment.sh <topupId> <amountPoisha> [succeeded|failed] [eventId]
# Send it twice with the same eventId to see the duplicate being ignored.
set -eu
cd "$(dirname "$0")/.."

topup=${1:?topup id}; amount=${2:?amount in poisha}; outcome=${3:-succeeded}
event=${4:-evt_$(openssl rand -hex 8)}
url=${GATEWAY_URL:-http://localhost:5100}/api/v1/payments/webhook

secret=$(grep '^PAYMENT_WEBHOOK_SECRET=' .env | cut -d= -f2-)
[ -n "$secret" ] || { echo "PAYMENT_WEBHOOK_SECRET missing: run ./deploy/dev-secrets.sh" >&2; exit 1; }

body=$(printf '{"eventId":"%s","type":"payment.%s","topupId":"%s","amountPoisha":%s}' "$event" "$outcome" "$topup" "$amount")
t=$(date +%s)
# HMAC-SHA256 over "<t>.<exact body bytes>"; printf (not echo) so no newline sneaks into the signed bytes.
mac=$(printf '%s.%s' "$t" "$body" | openssl dgst -sha256 -hmac "$secret" -r | cut -d' ' -f1)

echo "event $event → $url"
curl -s -X POST "$url" -H 'content-type: application/json' -H "Payment-Signature: t=$t,v1=$mac" --data-binary "$body"
echo
