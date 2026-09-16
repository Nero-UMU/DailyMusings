#!/usr/bin/env bash
# Runs the whole phase-five verification in order: the acceptance against a fresh instance A, then the
# §15.2 eight-step restore verification against a brand-new instance B built from the shipped compose file.
set -uo pipefail

VERIFY="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "############ acceptance (instance A) ############"
bash "$VERIFY/run-acceptance.sh"
ACCEPT=$?

if [ "$ACCEPT" -ne 0 ]; then
    echo "acceptance failed with $ACCEPT; not running the restore verification on top of it"
    exit "$ACCEPT"
fi

echo
echo "############ restore verification (fresh instance B) ############"
bash "$VERIFY/run-restore-verify.sh"
RESTORE=$?

echo
echo "acceptance exit=$ACCEPT restore exit=$RESTORE"
exit "$RESTORE"
