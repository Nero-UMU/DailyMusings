#!/usr/bin/env bash
# Phase-5 acceptance: instance A on the test host.
#
# Everything here talks to real services: the model endpoint is a stub because there is no provider on this
# host, but transcription, generation, embeddings, SMTP (Mailpit) and the Hexo Markdown output are all
# exercised through their real interfaces.
set -uo pipefail

# The harness is self-locating: the instance directories, the work directory and the clean source checkout sit next
# to these scripts. Copy the directory anywhere and set DM_ROOT if the instances should live elsewhere.
VERIFY="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="${DM_ROOT:-$(dirname "$VERIFY")}"
WORK="$ROOT/work"
INSTANCE="$ROOT/inst-a"
STATE="$INSTANCE/state"

mkdir -p "$WORK" "$STATE" "$INSTANCE/keys"
chmod 777 "$WORK" "$INSTANCE" "$STATE" "$INSTANCE/keys"

# The compose file lives next to the driver scripts but resolves ./state and ./keys relative to itself, so it is
# copied into the instance directory before it is used.
cp "$VERIFY/compose.verify.yaml" "$INSTANCE/compose.verify.yaml"

# Throwaway credentials for the model endpoints and SMTP. Since appendix A.27 the deployment cannot hand the
# instance a secret any more, so these are passed to acceptance.py, which types them in through the admin API —
# the same door an operator uses.
ACCEPTANCE_TRANSCRIPTION_KEY="$(cat "$VERIFY/secrets/openai-api-key")"
ACCEPTANCE_GENERATION_KEY="$(cat "$VERIFY/secrets/openai-api-key")"
ACCEPTANCE_EMBEDDING_KEY="$(cat "$VERIFY/secrets/embedding-api-key")"
ACCEPTANCE_SMTP_PASSWORD="$(cat "$VERIFY/secrets/smtp-password")"

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
# This instance binds 18321 on the host network, so anything else publishing 18321 has to stop first: the restore
# verification's instance does exactly that, and leaving it up makes the health check answer from the wrong server.
docker compose -f "$ROOT/inst-b/compose.yaml" down --remove-orphans >/dev/null 2>&1
docker compose -f "$INSTANCE/compose.verify.yaml" down --remove-orphans >/dev/null 2>&1

if curl -fsS --max-time 3 http://127.0.0.1:18321/api/system/health >/dev/null 2>&1; then
    echo "FATAL: something is already answering on 127.0.0.1:18321; stop it before running this"
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
    curl -fsS http://127.0.0.1:18321/api/system/health >/dev/null 2>&1 && break
    sleep 2
done

if ! curl -fsS http://127.0.0.1:18321/api/system/health >/dev/null 2>&1; then
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
DM_BASE=http://127.0.0.1:18321 \
DM_MAILPIT=http://127.0.0.1:8025 \
DM_STATE="$STATE" \
DM_WORK="$WORK" \
DM_ADMIN_INITIAL_PASSWORD="$PW" \
DM_ADMIN_USER=owner \
DM_ADMIN_PASSWORD="CorrectHorseBattery1" \
DM_ACCEPTANCE_TRANSCRIPTION_KEY="$ACCEPTANCE_TRANSCRIPTION_KEY" \
DM_ACCEPTANCE_GENERATION_KEY="$ACCEPTANCE_GENERATION_KEY" \
DM_ACCEPTANCE_EMBEDDING_KEY="$ACCEPTANCE_EMBEDDING_KEY" \
DM_ACCEPTANCE_SMTP_PASSWORD="$ACCEPTANCE_SMTP_PASSWORD" \
    python3 acceptance.py 2>&1 | tee "$WORK/acceptance.log"

echo "acceptance exit=${PIPESTATUS[0]}"
echo "--- stub request counts ---"
cat "$WORK/stub-stats.json" 2>/dev/null || echo "no stub stats"
echo
