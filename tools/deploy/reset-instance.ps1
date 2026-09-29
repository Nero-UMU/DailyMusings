# DailyMusings destructive instance reset.
#
# Deletes all persisted application data and runtime configuration, including legacy named volumes, then starts
# the existing Compose image as a brand-new instance. Deployment source and deploy/secrets are intentionally kept.
param(
    [string]$Server = "nero@100.64.0.3",
    [int]$Port = 18321,
    [string]$RemoteRoot = "/home/nero/dailymusings",
    [string]$RemoteConfigDirectory = "/home/nero/dailymusings-config",
    [string]$RemoteDataDirectory = "/home/nero/dailymusings-data"
)

$ErrorActionPreference = "Stop"

function ConvertTo-Base64([string]$Value) {
    [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Value))
}

$rootB64 = ConvertTo-Base64 $RemoteRoot
$configB64 = ConvertTo-Base64 $RemoteConfigDirectory
$dataB64 = ConvertTo-Base64 $RemoteDataDirectory

$remote = @'
set -euo pipefail

ROOT=$(printf '%s' '__ROOT_B64__' | base64 -d)
CONFIG_DIR=$(printf '%s' '__CONFIG_B64__' | base64 -d)
DATA_DIR=$(printf '%s' '__DATA_B64__' | base64 -d)
PORT=__PORT__

ROOT=$(realpath -m "$ROOT")
CONFIG_DIR=$(realpath -m "$CONFIG_DIR")
DATA_DIR=$(realpath -m "$DATA_DIR")

[ "$ROOT" = /home/nero/dailymusings ] || { echo "refusing unexpected project root: $ROOT" >&2; exit 2; }
[ "$CONFIG_DIR" = /home/nero/dailymusings-config ] || { echo "refusing unexpected config directory: $CONFIG_DIR" >&2; exit 2; }
[ "$DATA_DIR" = /home/nero/dailymusings-data ] || { echo "refusing unexpected data directory: $DATA_DIR" >&2; exit 2; }
[ "$CONFIG_DIR" != "$DATA_DIR" ]
[ -f "$ROOT/compose.yaml" ]
[ -f "$ROOT/deploy/compose.yaml" ]
[ -f "$ROOT/deploy/.env" ]

COMPOSE="docker compose --env-file $ROOT/deploy/.env -f $ROOT/compose.yaml -f $ROOT/deploy/compose.yaml"

echo "-- stop instance --"
$COMPOSE down --remove-orphans || true

echo "-- erase bind-mounted configuration and data --"
docker run --rm --user 0 \
    -v "$CONFIG_DIR:/reset-config" \
    -v "$DATA_DIR:/reset-data" \
    --entrypoint sh dailymusings/server:local \
    -c 'find /reset-config -mindepth 1 -delete && find /reset-data -mindepth 1 -delete && chown -R 1654:1654 /reset-config /reset-data'

echo "-- remove legacy named volumes --"
for volume in dailymusings_dailymusings-state dailymusings_dailymusings-keys dailymusings_dailymusings-config dailymusings_dm-data dailymusings_dm-config; do
    if docker volume inspect "$volume" >/dev/null 2>&1; then docker volume rm "$volume"; fi
done

[ "$(find "$CONFIG_DIR" -mindepth 1 | wc -l)" -eq 0 ]
[ "$(find "$DATA_DIR" -mindepth 1 | wc -l)" -eq 0 ]

echo "-- start clean instance --"
$COMPOSE up -d

for _ in $(seq 1 90); do
    if curl -fsS "http://127.0.0.1:$PORT/api/system/health" >/dev/null 2>&1; then break; fi
    sleep 2
done
curl -fsS "http://127.0.0.1:$PORT/api/system/health"
echo
'@

$remote = $remote.Replace("__ROOT_B64__", $rootB64).
    Replace("__CONFIG_B64__", $configB64).
    Replace("__DATA_B64__", $dataB64).
    Replace("__PORT__", $Port.ToString([Globalization.CultureInfo]::InvariantCulture))

$payload = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($remote -replace "`r`n", "`n")))
& ssh -o BatchMode=yes $Server "echo $payload | base64 -d | bash -s"
if ($LASTEXITCODE -ne 0) { throw "remote reset failed" }

Write-Host "Instance data and configuration were reset successfully."
