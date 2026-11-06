#!/bin/sh
# Creates .env with random local database passwords. Docker Compose reads .env automatically.
# .env is git-ignored, so no password is ever committed. Run once per machine:
#   ./deploy/dev-secrets.sh
set -eu
cd "$(dirname "$0")/.."

if [ -f .env ]; then
  echo ".env already exists; leaving it unchanged."
  exit 0
fi

pw() { openssl rand -hex 24; }

umask 077
cat > .env <<ENV
POSTGRES_SUPERUSER_PASSWORD=$(pw)
IDENTITY_DB_PASSWORD=$(pw)
NETWORK_DB_PASSWORD=$(pw)
CORE_DB_PASSWORD=$(pw)
ENV
echo "Wrote .env with fresh random passwords."
