#!/usr/bin/env bash
# The whole TransitPass story in one command, against the running Docker stack, through the gateway:
#   new user → login → wallet → top-up (signed webhook) → fare quote → buy a ticket (Idempotency-Key + replay)
#   → signed QR → tap in → tap out farther (surcharge) → history → the books balance (balance == SUM(ledger)).
#
#   POSTGRES_HOST_PORT=55432 docker compose up -d --build   # first, once
#   ./deploy/demo.sh
#
# Every call states the HTTP status it expects; anything else stops the demo with the response body.
# Needs: bash, curl, openssl, python3. Secrets (webhook secret, gate keys) come from .env (./deploy/dev-secrets.sh).
set -euo pipefail
cd "$(dirname "$0")/.."

BASE=${GATEWAY_URL:-http://localhost:5100}
FROM=uttara-north   # the ticket is bought for uttara-north → agargaon ...
TO=agargaon
EXIT=motijheel      # ... but the rider gets off farther, at motijheel, so the exit gate charges the difference
TOPUP_POISHA=50000  # ৳500

for tool in curl openssl python3; do
  command -v "$tool" >/dev/null || { echo "demo.sh needs $tool" >&2; exit 1; }
done
[ -f .env ] || { echo ".env missing: run ./deploy/dev-secrets.sh and start the stack first" >&2; exit 1; }

env_value() { # env_value NAME → the value from .env, or fail loudly
  local v
  v=$(grep "^$1=" .env | cut -d= -f2- || true)
  [ -n "$v" ] || { echo "$1 missing in .env: run ./deploy/dev-secrets.sh" >&2; exit 1; }
  printf '%s' "$v"
}
gate_key() { env_value "GATE_$(printf '%s' "$1" | tr 'a-z-' 'A-Z_')_KEY"; }

if [ -t 1 ]; then B=$'\033[1m'; G=$'\033[32m'; R=$'\033[31m'; D=$'\033[2m'; N=$'\033[0m'; else B='' G='' R='' D='' N=''; fi

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
STEP=0
step() { STEP=$((STEP + 1)); printf '\n%s%2d. %s%s\n' "$B" "$STEP" "$1" "$N"; }
ok() { printf '    %s✓%s %s\n' "$G" "$N" "$1"; }
fail() { printf '    %s✗ %s%s\n' "$R" "$1" "$N" >&2; exit 1; }

# json EXPR: evaluates a Python expression over the last response body, bound to `j`. Prints str/int plainly.
json() {
  python3 -c '
import json, sys
j = json.load(open(sys.argv[1]))
v = eval(sys.argv[2], {"j": j})
print(json.dumps(v) if isinstance(v, (dict, list, bool)) or v is None else v)' "$WORK/body" "$1"
}
show() { python3 -c 'import json,sys; print("\n".join("    " + l for l in json.dumps(json.load(open(sys.argv[1])), indent=2, ensure_ascii=False).splitlines()))' "$WORK/body" | sed "s/^/$D/;s/\$/$N/"; }
header() { grep -i "^$1:" "$WORK/headers" | head -1 | cut -d: -f2- | tr -d ' \r' || true; }

# call EXPECTED METHOD PATH [BODY] [curl args...]: one request; the status must be EXPECTED.
call() {
  local expected=$1 method=$2 path=$3 body=${4:-}
  shift 3; [ $# -gt 0 ] && shift
  local args=(-sS -X "$method" "$BASE$path" -o "$WORK/body" -D "$WORK/headers" -w '%{http_code}')
  [ -n "${TOKEN:-}" ] && args+=(-H "Authorization: Bearer $TOKEN")
  [ -n "$body" ] && args+=(-H 'content-type: application/json' --data-binary "$body")
  local status
  status=$(curl "${args[@]}" "$@") || fail "$method $path: no answer from $BASE (is the stack up?)"
  printf '    %s%s %s → %s%s\n' "$D" "$method" "$path" "$status" "$N"
  if [ "$status" != "$expected" ]; then
    cat "$WORK/body" >&2; echo >&2
    fail "expected HTTP $expected, got $status"
  fi
}

# webhook EVENT_ID TOPUP_ID AMOUNT: plays the payment provider, signing "<t>.<body>" with HMAC-SHA256.
webhook() {
  local body t mac
  body=$(printf '{"eventId":"%s","type":"payment.succeeded","topupId":"%s","amountPoisha":%s}' "$1" "$2" "$3")
  t=$(date +%s)
  mac=$(printf '%s.%s' "$t" "$body" | openssl dgst -sha256 -hmac "$(env_value PAYMENT_WEBHOOK_SECRET)" -r | cut -d' ' -f1)
  TOKEN='' call 200 POST /api/v1/payments/webhook "$body" -H "Payment-Signature: t=$t,v1=$mac"
}

# tap STATION DIRECTION: plays the gate at STATION, sending the QR credential with the gate's own key.
tap() {
  local body
  body=$(printf '{"tapId":"%s","gateId":"gate-%s-1","stationId":"%s","direction":"%s","credential":"%s"}' \
    "$(python3 -c 'import uuid; print(uuid.uuid4())')" "$1" "$1" "$2" "$CREDENTIAL")
  TOKEN='' call "$3" POST /api/v1/gate/taps "$body" -H "X-Gate-Key: $(gate_key "$1")"
}

taka() { python3 -c 'import sys; p = int(sys.argv[1]); print(f"৳{p // 100:,}.{p % 100:02d} ({p} poisha)")' "$1"; }

printf '%sTransitPass demo%s against %s\n' "$B" "$N" "$BASE"

# Right after `docker compose up -d` the containers run but the services may still be starting, and the gateway
# answers 502/503 for them (000 = the gateway itself is not up yet). Wait up to WAIT_SECONDS for every service to
# answer anything else, then start. Probes: Network's station list, Core's gate JWKS, and Identity's login route
# (a GET there is 405 from Identity itself, so any 4xx means the service is up).
wait_for_stack() {
  local deadline=$((SECONDS + ${WAIT_SECONDS:-60})) path code pending
  while :; do
    pending=
    for path in /api/v1/network/stations /api/v1/identity/sessions /api/v1/gate/.well-known/jwks.json; do
      code=$(curl -s -o /dev/null -m 5 -w '%{http_code}' "$BASE$path" || true)
      case $code in 000 | 5??) pending="$pending $path ($code)" ;; esac
    done
    [ -z "$pending" ] && return 0
    [ "$SECONDS" -lt "$deadline" ] || fail "the stack is not ready after ${WAIT_SECONDS:-60} s:$pending (is it up? docker compose ps)"
    printf '    %swaiting for:%s%s\n' "$D" "$pending" "$N"
    sleep 2
  done
}
wait_for_stack

step "The gateway is up (Network service lists the stations)"
call 200 GET /api/v1/network/stations
ok "stations: $(json '", ".join(s["id"] for s in j)')"

step "Register a brand-new user (random phone number)"
PASSWORD="demo-$(openssl rand -hex 6)"
for _ in 1 2 3; do
  PHONE="017$(python3 -c 'import secrets; print(f"{secrets.randbelow(10**8):08d}")')"
  status=$(curl -sS -o "$WORK/body" -w '%{http_code}' -X POST "$BASE/api/v1/identity/users" \
    -H 'content-type: application/json' --data-binary "{\"phone\":\"$PHONE\",\"name\":\"Demo Rider\",\"password\":\"$PASSWORD\"}")
  [ "$status" = 409 ] || break # 409 = that random number is taken already; try another
done
printf '    %sPOST /api/v1/identity/users → %s%s\n' "$D" "$status" "$N"
[ "$status" = 201 ] || { cat "$WORK/body" >&2; fail "expected HTTP 201, got $status"; }
show
ok "user $(json 'j["id"]') with phone $(json 'j["phone"]') (stored in E.164 form)"

step "Log in (Identity signs an ES256 JWT, valid 15 minutes)"
call 200 POST /api/v1/identity/sessions "{\"phone\":\"$PHONE\",\"password\":\"$PASSWORD\"}"
TOKEN=$(json 'j["accessToken"]')
ok "token type $(json 'j["tokenType"]'), expires in $(json 'j["expiresIn"]') s: ${TOKEN:0:32}…"

step "No token, no wallet (Core checks the JWT itself)"
TOKEN='' call 401 GET /api/v1/wallet/me
ok "401 $(json 'j["code"]')"

step "Open the wallet (created on first use)"
call 200 GET /api/v1/wallet/me
show
[ "$(json 'j["balancePoisha"]')" = 0 ] || fail "a new wallet should start at 0"
ok "balance $(taka 0)"

step "Top up $(taka $TOPUP_POISHA): create the top-up (Pending until the provider says it is paid)"
call 201 POST /api/v1/wallet/topups "{\"amountPoisha\":$TOPUP_POISHA}"
TOPUP=$(json 'j["id"]')
ok "top-up $TOPUP is $(json 'j["status"]'); the app would send the user to $(json 'j["checkoutUrl"]')"

step "The payment provider calls our webhook (HMAC-SHA256 signed), then retries the same event"
EVENT="evt_$(openssl rand -hex 8)"
webhook "$EVENT" "$TOPUP" "$TOPUP_POISHA"
ok "first delivery: $(json 'j["result"]')"
webhook "$EVENT" "$TOPUP" "$TOPUP_POISHA"
[ "$(json 'j["result"]')" = duplicate ] || fail "the retried event should be a duplicate"
ok "retry: $(json 'j["result"]') (the inbox table saw this event id already: no second credit)"
call 200 GET "/api/v1/wallet/topups/$TOPUP"
ok "top-up is now $(json 'j["status"]')"
call 200 GET /api/v1/wallet/me
[ "$(json 'j["balancePoisha"]')" = "$TOPUP_POISHA" ] || fail "balance should be exactly one top-up"
ok "balance $(taka "$TOPUP_POISHA")"

step "Quote the fare $FROM → $TO (Network: Dijkstra, precomputed)"
call 200 GET "/api/v1/network/fares/quote?from=$FROM&to=$TO"
FARE=$(json 'j["farePoisha"]')
ok "$(json 'j["distanceM"]') m, fare $(taka "$FARE")"

step "Buy the ticket with an Idempotency-Key"
KEY=$(python3 -c 'import uuid; print(uuid.uuid4())')
call 201 POST /api/v1/tickets "{\"from\":\"$FROM\",\"to\":\"$TO\"}" -H "Idempotency-Key: $KEY"
show
TICKET=$(json 'j["id"]')
[ "$(json 'j["farePoisha"]')" = "$FARE" ] || fail "the ticket fare should match the quote"
ok "ticket $TICKET, $(json 'j["status"]'), valid until $(json 'j["expiresAt"]') (Dhaka midnight)"

step "The phone lost the answer and sends the SAME request again (same key)"
call 201 POST /api/v1/tickets "{\"from\":\"$FROM\",\"to\":\"$TO\"}" -H "Idempotency-Key: $KEY"
[ "$(header Idempotent-Replayed)" = true ] || fail "expected Idempotent-Replayed: true"
[ "$(json 'j["id"]')" = "$TICKET" ] || fail "the replay should return the same ticket"
ok "Idempotent-Replayed: true, same ticket $TICKET"
call 200 GET /api/v1/wallet/me
BALANCE=$(json 'j["balancePoisha"]')
[ "$BALANCE" = $((TOPUP_POISHA - FARE)) ] || fail "the fare should be charged exactly once"
ok "charged once: balance $(taka "$BALANCE")"

step "Get the signed QR credential (ES256 JWS from Core's own key)"
call 200 GET "/api/v1/tickets/$TICKET/credential"
CREDENTIAL=$(json 'j["credential"]')
ok "${CREDENTIAL:0:48}… ($(printf '%s' "$CREDENTIAL" | wc -c | tr -d ' ') chars, goes into the QR)"
python3 -c '
import base64, json, sys
part = lambda s: json.loads(base64.urlsafe_b64decode(s + "=" * (-len(s) % 4)))
h, c, _ = sys.argv[1].split(".")
print("    header:", json.dumps(part(h)))
print("    claims:", json.dumps(part(c)))' "$CREDENTIAL"
call 200 GET /api/v1/gate/.well-known/jwks.json
ok "anyone can verify it with only the public key from this JWKS (kid $(json 'j["keys"][0]["kid"]'))"

step "Tap in at $FROM (the gate sends the QR with its own gate key)"
tap "$FROM" entry 200
ok "$(json 'j["code"]'): $(json 'j["message"]') → ticket $(json 'j["ticketStatus"]')"

step "The same QR at the entry gate again (a copied QR) stays closed"
tap "$FROM" entry 409
ok "409 $(json 'j["code"]'): $(json 'j["title"]')"

step "Tap out farther than paid for, at $EXIT"
call 200 GET "/api/v1/network/fares/quote?from=$FROM&to=$EXIT"
TRIP_FARE=$(json 'j["farePoisha"]')
tap "$EXIT" exit 200
show
SURCHARGE=$(json 'j["surchargePoisha"]')
[ "$SURCHARGE" = $((TRIP_FARE - FARE)) ] || fail "surcharge should be fare($FROM→$EXIT) − fare($FROM→$TO)"
ok "gate opens, ticket $(json 'j["ticketStatus"]'), surcharge $(taka "$SURCHARGE") = $(taka "$TRIP_FARE") − $(taka "$FARE")"

step "History: my tickets and my wallet transactions (newest first)"
call 200 GET "/api/v1/tickets?limit=5"
python3 -c '
import json, sys
for t in json.load(open(sys.argv[1]))["items"]:
    print("    ticket {id}  {from} → {to}  {farePoisha:>6} poisha  {status}".format(**t))' "$WORK/body"
call 200 GET "/api/v1/wallet/me/transactions?limit=10"
python3 -c '
import json, sys
for e in json.load(open(sys.argv[1]))["items"]:
    print("    {createdAt:.19}  {type:<9} {amountPoisha:>+8} poisha  ref {refId}".format(**e))' "$WORK/body"

step "The books balance: wallet balance == SUM of every ledger row (paged through the API)"
call 200 GET /api/v1/wallet/me
BALANCE=$(json 'j["balancePoisha"]')
SUM=0; ROWS=0; CURSOR=
while :; do
  call 200 GET "/api/v1/wallet/me/transactions?limit=100${CURSOR:+&after=$CURSOR}"
  SUM=$((SUM + $(json 'sum(e["amountPoisha"] for e in j["items"])')))
  ROWS=$((ROWS + $(json 'len(j["items"])')))
  CURSOR=$(json 'j["nextCursor"] or ""')
  [ -n "$CURSOR" ] || break
done
EXPECTED=$((TOPUP_POISHA - FARE - SURCHARGE))
[ "$BALANCE" = "$SUM" ] || fail "balance $BALANCE != SUM(ledger) $SUM"
[ "$BALANCE" = "$EXPECTED" ] || fail "balance $BALANCE != top-up − fare − surcharge = $EXPECTED"
ok "balance $(taka "$BALANCE") == SUM of $ROWS ledger rows $(taka "$SUM") == $TOPUP_POISHA − $FARE − $SURCHARGE"

printf '\n%s%sDemo passed:%s %d steps, user %s, ticket %s.\n' "$B" "$G" "$N" "$STEP" "$PHONE" "$TICKET"
