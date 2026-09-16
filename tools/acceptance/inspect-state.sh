#!/bin/sh
# Runs inside the instance's own image with its state volume mounted at /state, so the checks below look
# at the restored volume itself rather than at a copy of it. No output here is a decision: the driver
# reads the KEY=VALUE lines.
set -u

count() {
    find "$1" ${3:-} -type f 2>/dev/null | wc -l | tr -d ' '
}

echo "MARKDOWN_FILES=$(count /state/markdown)"
echo "MEDIA_FILES=$(count /state/media)"
echo "BACKUP_FILES=$(find /state/backups -type f -name '*.zip' 2>/dev/null | wc -l | tr -d ' ')"
echo "EXPORT_DIRS=$(find /state/exports -mindepth 1 -maxdepth 1 -type d 2>/dev/null | wc -l | tr -d ' ')"
echo "EXPORT_FILES=$(count /state/exports)"
echo "EXPORT_AUDIO_FILES=$(find /state/exports -path '*/audio/*' -type f 2>/dev/null | wc -l | tr -d ' ')"

FOUND=0
if [ -n "${NEEDLE_0:-}" ] && grep -rqF "$NEEDLE_0" /state 2>/dev/null; then
    FOUND=1
fi
echo "TOKEN_HASH_FOUND=$FOUND"

find /state/markdown -type f 2>/dev/null | sed 's/^/MARKDOWN_FILE=/'
