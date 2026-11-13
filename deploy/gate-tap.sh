#!/bin/sh
# Plays a gate device: sends one tap to TransitPass with the gate's own key from .env.
#   ./deploy/gate-tap.sh <station> <entry|exit> <credential> [tapId]
# The gate at <station> is "gate-<station>-1". Send it twice with the same tapId to see the replay.
set -eu
cd "$(dirname "$0")/.."

station=${1:?station id}; direction=${2:?entry or exit}; credential=${3:?QR credential}
tap=${4:-$(uuidgen | tr 'A-Z' 'a-z')}
url=${GATEWAY_URL:-http://localhost:5100}/api/v1/gate/taps

name=$(printf '%s' "$station" | tr 'a-z-' 'A-Z_')
key=$(grep "^GATE_${name}_KEY=" .env | cut -d= -f2-)
[ -n "$key" ] || { echo "GATE_${name}_KEY missing: run ./deploy/dev-secrets.sh" >&2; exit 1; }

body=$(printf '{"tapId":"%s","gateId":"gate-%s-1","stationId":"%s","direction":"%s","credential":"%s"}' \
  "$tap" "$station" "$station" "$direction" "$credential")
echo "tap $tap ($direction at $station) → $url" >&2
curl -s -w '\nHTTP %{http_code}\n' -X POST "$url" -H 'content-type: application/json' -H "X-Gate-Key: $key" --data-binary "$body"
