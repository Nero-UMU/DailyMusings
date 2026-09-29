#!/usr/bin/env bash
# §15.2's eight-step restore verification: a brand-new instance, built from the shipped compose file, in a
# brand-new empty directory with freshly generated secrets.
#
# 2026-09-29（附录 A.26）后的形态：对外的那份文件是**仓库根的 compose.yaml**（拉镜像 + 两个持久目录 + 全部默认值），
# 从源码构建的覆盖文件在 deploy/ 下。这一份验证仍然刻意只放一个文件进空目录，但镜像用本地构建出来的那个：
# `DM_IMAGE=dailymusings/server:local`，于是走的就是新用户会走的那条路（一份文件 + 一个镜像），只是镜像来源不同。
set -uo pipefail

VERIFY="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="${DM_ROOT:-$(dirname "$VERIFY")}"
REPO="$ROOT/repo"
WORK="$ROOT/work"
FRESH="$ROOT/inst-b"

# 所有 compose 调用都用本地构建的镜像；否则根 compose.yaml 会去 ghcr 拉发布版，验的就不是这份源码了。
export DM_IMAGE="dailymusings/server:local"

echo "--- step 1: a brand-new empty directory with only the compose file and new secrets ---"
rm -rf "${FRESH:?}"
mkdir -p "$FRESH/secrets"
cp "$REPO/compose.yaml" "$FRESH/compose.yaml"

# 根 compose.yaml 里 secrets 目录那一行是注释掉的（默认走管理页填密钥）。这里显式打开它，因为这一步要验的
# 正是「全新实例 + 新 Secrets」；改不动就直接失败，不要悄悄跑成「Secrets 没挂上」的假通过。
sed -i 's|^      # - \./secrets:/run/secrets:ro|      - ./secrets:/run/secrets:ro|' "$FRESH/compose.yaml"
grep -q '^      - \./secrets:/run/secrets:ro' "$FRESH/compose.yaml" || {
    echo "refusing to continue: could not enable the ./secrets mount in the copied compose.yaml" >&2
    exit 2
}

# Freshly generated, and deliberately different from instance A's: nothing here may be a leftover.
for name in openai-api-key embedding-api-key smtp-password; do
    head -c 24 /dev/urandom | base64 | tr -d '\n=' > "$FRESH/secrets/$name"
    echo >> "$FRESH/secrets/$name"
done
chmod 644 "$FRESH/secrets/"*
echo "contents of the fresh directory:"
ls -la "$FRESH" "$FRESH/secrets"

echo "--- instance A stops first: both instances are the only thing on port 18321 ---"
docker compose -f "$ROOT/inst-a/compose.verify.yaml" down --remove-orphans >/dev/null 2>&1

echo "--- step 2: docker compose up -d ---"
cd "$FRESH"

# A previous run of this verification leaves its named volumes behind; §15.2 step 1 asks for a genuinely empty
# instance, so they are removed first.
docker compose -f compose.yaml down -v --remove-orphans >/dev/null 2>&1

docker compose -f compose.yaml up -d

for _ in $(seq 1 90); do
    curl -fsS http://127.0.0.1:18321/api/system/health >/dev/null 2>&1 && break
    sleep 2
done

PW=$(docker compose -f compose.yaml logs app 2>&1 \
    | grep -o 'INITIAL-ADMIN-PASSWORD=.*' | head -1 | cut -d= -f2 | tr -d '\r')
echo "captured the one-time administrator password (${#PW} characters)"

cd "$VERIFY"
export DM_BASE=http://127.0.0.1:18321
export DM_WORK="$WORK"
export DM_ADMIN_INITIAL_PASSWORD="$PW"
export DM_ADMIN_USER=owner
export DM_ADMIN_PASSWORD="CorrectHorseBattery1"
# 项目名仍是 compose.yaml 里的 name（dailymusings），但状态卷从旧版的三个改成了 dm-data / dm-config 两份。
export DM_VOLUME="dailymusings_dm-data"

echo "--- step 2 and 3: administrator initialized, then the archive is uploaded and staged ---"
python3 restore_verify.py stage "$WORK/restore-source.zip" 2>&1 | tee "$WORK/restore-stage.log"
STAGE_EXIT=${PIPESTATUS[0]}

echo "--- the staged restore is applied at the next start (that is the design, not a workaround) ---"
docker compose -f "$FRESH/compose.yaml" restart app
for _ in $(seq 1 90); do
    curl -fsS http://127.0.0.1:18321/api/system/health >/dev/null 2>&1 && break
    sleep 2
 done
sleep 5
docker compose -f "$FRESH/compose.yaml" logs app 2>&1 | grep -i -E 'restore|pre-restore' | tail -5

echo "--- steps 4 to 8 ---"
python3 restore_verify.py verify 2>&1 | tee "$WORK/restore-verify.log"
VERIFY_EXIT=${PIPESTATUS[0]}

echo "stage exit=$STAGE_EXIT verify exit=$VERIFY_EXIT"
