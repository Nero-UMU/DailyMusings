#!/usr/bin/env bash
# Phase-5 acceptance: instance A on the test host.
#
# Everything here talks to real services: the model endpoint is a stub because there is no provider on this
# host, but transcription, generation, embeddings, SMTP (Mailpit), WordPress and the Hexo Markdown output
# are all exercised through their real interfaces.
set -uo pipefail

# The harness is self-locating: the instance directories, the work directory and the clean source checkout sit next
# to these scripts. Copy the directory anywhere and set DM_ROOT if the instances should live elsewhere.
VERIFY="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="${DM_ROOT:-$(dirname "$VERIFY")}"
WORK="$ROOT/work"
INSTANCE="$ROOT/inst-a"
STATE="$INSTANCE/state"

# Application password of the disposable WordPress site. It is a credential, so it comes from the environment
# rather than from this file.
WP_APP_PASSWORD="${DM_WP_APP_PASSWORD:-}"
if [ -z "$WP_APP_PASSWORD" ]; then
    echo "FATAL: set DM_WP_APP_PASSWORD to the throwaway WordPress site's application password"
    exit 1
fi

mkdir -p "$WORK" "$STATE" "$INSTANCE/keys" "$INSTANCE/secrets"
chmod 777 "$WORK" "$INSTANCE" "$STATE" "$INSTANCE/keys" "$INSTANCE/secrets"

# The compose file lives next to the driver scripts but resolves ./state, ./keys and ./secrets relative to
# itself, so it is copied into the instance directory before it is used.
cp "$VERIFY/compose.verify.yaml" "$INSTANCE/compose.verify.yaml"

# Throwaway secret values for the model endpoints and SMTP; the WordPress one is the real application password of
# the disposable blog, taken from the environment.
cp "$VERIFY/secrets/openai-api-key" "$INSTANCE/secrets/openai-api-key"
cp "$VERIFY/secrets/embedding-api-key" "$INSTANCE/secrets/embedding-api-key"
cp "$VERIFY/secrets/smtp-password" "$INSTANCE/secrets/smtp-password"
printf '%s' "$WP_APP_PASSWORD" > "$INSTANCE/secrets/wordpress-application-password"
chmod 644 "$INSTANCE/secrets/"*

echo "--- model stub ---"
pkill -f stub_models.py >/dev/null 2>&1
sleep 1
STUB_TRANSCRIPT_MAP="$WORK/transcripts.json" \
STUB_PORT=8077 \
STUB_STATS="$WORK/stub-stats.json" \
    nohup python3 "$VERIFY/stub_models.py" > "$WORK/stub.log" 2>&1 &
sleep 2
curl -fsS http://127.0.0.1:8077/v1/models && echo

echo "--- instance A: a fresh instance on a clean state directory ---"
# This instance binds 8080 on the host network, so anything else publishing 8080 has to stop first: the restore
# verification's instance does exactly that, and leaving it up makes the health check answer from the wrong server.
docker compose -f "$ROOT/inst-b/compose.yaml" down --remove-orphans >/dev/null 2>&1
docker compose -f "$INSTANCE/compose.verify.yaml" down --remove-orphans >/dev/null 2>&1

if curl -fsS --max-time 3 http://127.0.0.1:8080/api/system/health >/dev/null 2>&1; then
    echo "FATAL: something is already answering on 127.0.0.1:8080; stop it before running this"
    docker ps --format '{{.Names}} {{.Ports}}' | head -10
    exit 1
fi

# The application writes as its own uid inside the container, so the state it created cannot be removed by the
# host user that owns the parent directory: the wipe goes through a root container on the same mounts.
docker run --rm -u 0 -v "$STATE:/state" -v "$INSTANCE/keys:/keys" \
    --entrypoint /bin/sh dailymusings/server:local -c 'rm -rf /state/* /state/.[!.]* /keys/* /keys/.[!.]*'

mkdir -p "$STATE" "$INSTANCE/keys"
chmod 777 "$STATE" "$INSTANCE/keys"
docker compose -f "$INSTANCE/compose.verify.yaml" up -d

for _ in $(seq 1 90); do
    curl -fsS http://127.0.0.1:8080/api/system/health >/dev/null 2>&1 && break
    sleep 2
done

if ! curl -fsS http://127.0.0.1:8080/api/system/health >/dev/null 2>&1; then
    echo "FATAL: the instance never became healthy; container log follows"
    docker compose -f "$INSTANCE/compose.verify.yaml" logs app 2>&1 | tail -40
    exit 1
fi

PW=$(docker compose -f "$INSTANCE/compose.verify.yaml" logs app 2>&1 \
    | grep -o 'INITIAL-ADMIN-PASSWORD=.*' | head -1 | cut -d= -f2 | tr -d '\r')
echo "captured the one-time administrator password (${#PW} characters)"

if [ -z "$PW" ]; then
    echo "FATAL: no INITIAL-ADMIN-PASSWORD in the container log"
    docker compose -f "$INSTANCE/compose.verify.yaml" logs app 2>&1 | tail -40
    exit 1
fi

cd "$VERIFY"
DM_BASE=http://127.0.0.1:8080 \
DM_MAILPIT=http://127.0.0.1:8025 \
DM_WP=http://127.0.0.1:8090 \
DM_WP_USER=owner \
DM_WP_APP_PASSWORD="$WP_APP_PASSWORD" \
DM_STATE="$STATE" \
DM_WORK="$WORK" \
DM_ADMIN_INITIAL_PASSWORD="$PW" \
DM_ADMIN_USER=owner \
DM_ADMIN_PASSWORD="CorrectHorseBattery1" \
    python3 acceptance.py 2>&1 | tee "$WORK/acceptance.log"

echo "acceptance exit=${PIPESTATUS[0]}"
echo "--- stub request counts ---"
cat "$WORK/stub-stats.json" 2>/dev/null || echo "no stub stats"
echo
