# 把当前工作树部署到远程 Docker 主机（每日随想）。
#
# 这是本项目在 2026-09-24 之后的部署约定：
#   1. 把工作树打包送到服务器（不含 bin/obj/.git/artifacts/.tmp-*）；
#   2. 停容器 → 删掉旧镜像 → 重新构建 → 启动新镜像 → 等健康检查 → 清理悬空镜像。
# 第 2 步是用户明确要求的：不允许旧镜像留在服务器上，每次部署都以新镜像启动。
#
# 用法：
#   pwsh -File tools/deploy/deploy.ps1                       # 用默认主机与端口
#   pwsh -File tools/deploy/deploy.ps1 -Server 100.64.0.3 -Port 18321
param(
    [string]$Server = "nero@100.64.0.3",
    [int]$Port = 18321,
    [string]$RemoteConfigDirectory = "/home/nero/dailymusings-config",
    [string]$RemoteDataDirectory = "/home/nero/dailymusings-data",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$tarball = Join-Path $env:TEMP "dailymusings-$stamp.tar.gz"
$configDirectoryB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($RemoteConfigDirectory))
$dataDirectoryB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($RemoteDataDirectory))

Write-Host "== 1. packaging $repo =="
# tar 是 Windows 10+ 自带的 bsdtar；用 .gitignore 之外的显式排除，避免把构建产物与本地状态送上去。
& tar -czf $tarball `
    --exclude=./.git --exclude=./.vs --exclude=./.tmp-work --exclude=./.tmp-device --exclude=./.tmp-verify `
    --exclude=./artifacts --exclude=./data --exclude=./keys --exclude=./media --exclude=./exports --exclude=./markdown `
    --exclude='*/bin' --exclude='*/obj' --exclude=./deploy/.env `
    -C $repo .
if ($LASTEXITCODE -ne 0) { throw "tar failed" }

$size = [math]::Round((Get-Item $tarball).Length / 1MB, 1)
Write-Host "   $tarball ($size MB)"

Write-Host "== 2. copying to $Server =="
& scp -q $tarball "${Server}:/tmp/dm-$stamp.tar.gz"
if ($LASTEXITCODE -ne 0) { throw "scp failed" }

Write-Host "== 3. stop -> remove old image -> build -> start =="
$remote = @"
set -euo pipefail
ROOT=/home/nero/dailymusings
[ "`$(dirname "`$ROOT")" = "/home/nero" ] && [ "`$(basename "`$ROOT")" = "dailymusings" ] || {
    echo "refusing unsafe project path: `$ROOT" >&2
    exit 2
}
OLD_COMPOSE="docker compose -f `$ROOT/compose.yaml -f `$ROOT/deploy/compose.yaml"
PRESERVED=`$(mktemp -d)
case "`$PRESERVED" in
    /tmp/tmp.*) ;;
    *) echo "refusing unexpected temporary path: `$PRESERVED" >&2; exit 2 ;;
esac
cleanup_preserved() {
    if [ -n "`$PRESERVED" ] && [ -d "`$PRESERVED" ]; then rm -rf -- "`$PRESERVED"; fi
}
trap cleanup_preserved EXIT
DEFAULT_CONFIG_DIR=`$(echo $configDirectoryB64 | base64 -d)
DEFAULT_DATA_DIR=`$(echo $dataDirectoryB64 | base64 -d)

read_env_path() {
    key="`$1"
    file="`$ROOT/deploy/.env"
    [ -f "`$file" ] || return 0
    awk -F= -v key="`$key" '`$1 == key { sub(/^[^=]*=/, ""); gsub(/\r$/, ""); print }' "`$file" | tail -n1
}

# DM_* 是根 compose.yaml 用的名字；旧服务器上的 deploy/.env 里可能还是 DAILYMUSINGS_* 那套，
# 两个都读，避免一次升级把已有的数据目录指错地方。
CONFIG_DIR=`$(read_env_path DM_CONFIG_DIR)
DATA_DIR=`$(read_env_path DM_DATA_DIR)
[ -n "`$CONFIG_DIR" ] || CONFIG_DIR=`$(read_env_path DAILYMUSINGS_CONFIG_DIR)
[ -n "`$DATA_DIR" ] || DATA_DIR=`$(read_env_path DAILYMUSINGS_DATA_DIR)
CONFIG_DIR=`${CONFIG_DIR:-`$DEFAULT_CONFIG_DIR}
DATA_DIR=`${DATA_DIR:-`$DEFAULT_DATA_DIR}

validate_bind_dir() {
    label="`$1"
    value="`$2"
    case "`$value" in
        /*) ;;
        *) echo "`$label must be an absolute path: `$value" >&2; exit 2 ;;
    esac

    resolved=`$(realpath -m "`$value")
    case "`$resolved" in
        /|/home|/home/nero|"`$ROOT"|"`$ROOT"/*)
            echo "refusing unsafe `$label: `$resolved" >&2
            exit 2
            ;;
    esac
    printf '%s' "`$resolved"
}

CONFIG_DIR=`$(validate_bind_dir DAILYMUSINGS_CONFIG_DIR "`$CONFIG_DIR")
DATA_DIR=`$(validate_bind_dir DAILYMUSINGS_DATA_DIR "`$DATA_DIR")
[ "`$CONFIG_DIR" != "`$DATA_DIR" ] || { echo "config and data directories must differ" >&2; exit 2; }

echo "-- keep the deployment settings out of the source-tree wipe --"
if [ -f "`$ROOT/deploy/.env" ]; then cp -a "`$ROOT/deploy/.env" "`$PRESERVED/.env"; fi

echo "-- stop and remove the running instance --"
if [ -f "`$ROOT/deploy/.env" ]; then
    docker compose --env-file "`$ROOT/deploy/.env" -f "`$ROOT/compose.yaml" -f "`$ROOT/deploy/compose.yaml" down --remove-orphans || true
else
    `$OLD_COMPOSE down --remove-orphans || true
fi

echo "-- migrate named volumes to the two bind-mounted directories (first bind-mount deploy only) --"
mkdir -p "`$CONFIG_DIR/keys" "`$DATA_DIR"
if [ ! -f "`$CONFIG_DIR/.named-volumes-migrated" ]; then
    copy_volume() {
        volume="`$1"
        destination="`$2"
        if docker volume inspect "`$volume" >/dev/null 2>&1; then
            docker run --rm \
                --user 0 \
                -v "`$volume:/source:ro" \
                -v "`$destination:/target" \
                --entrypoint sh dailymusings/server:local \
                -c 'cp -a /source/. /target/'
        fi
    }

    copy_volume dailymusings_dailymusings-state "`$DATA_DIR"
    copy_volume dailymusings_dailymusings-config "`$CONFIG_DIR"
    copy_volume dailymusings_dailymusings-keys "`$CONFIG_DIR/keys"

    source_files=0
    if docker volume inspect dailymusings_dailymusings-state >/dev/null 2>&1; then
        source_files=`$(docker run --rm --user 0 -v dailymusings_dailymusings-state:/source:ro --entrypoint sh dailymusings/server:local -c 'find /source -type f | wc -l')
    fi
    target_files=`$(find "`$DATA_DIR" -type f | wc -l)
    [ "`$target_files" -ge "`$source_files" ] || {
        echo "data migration verification failed: source=`$source_files target=`$target_files" >&2
        exit 3
    }
    # cp -a deliberately preserves the container uid/mode from the old volumes, so the SSH user may no longer
    # be able to create the marker directly. Create it through the same narrowly mounted root helper instead.
    docker run --rm --user 0 \
        -v "`$CONFIG_DIR:/config" \
        --entrypoint sh dailymusings/server:local \
        -c 'touch /config/.named-volumes-migrated'
fi

# The image runs as uid 1654. Chown only the two exact, validated deployment directories through a root helper
# container; the SSH account needs Docker access but does not need host-root privileges.
docker run --rm --user 0 \
    -v "`$CONFIG_DIR:/config" \
    -v "`$DATA_DIR:/data" \
    --entrypoint sh dailymusings/server:local \
    -c 'chown -R 1654:1654 /config /data'

echo "-- remove the old image (user rule: never start a new image beside the old one) --"
docker image rm -f dailymusings/server:local || true

echo "-- lay down the new tree --"
rm -rf "`$ROOT"
mkdir -p "`$ROOT"
tar -xzf /tmp/dm-$stamp.tar.gz -C "`$ROOT"
# 密钥不再随部署落盘：A.27 起凭据只能从管理页填（见 tools/deploy/README.md）。
# Always write back the resolved and validated directories. An older .env may contain only the port settings;
# restoring it verbatim would leave the two required bind sources undefined in the new Compose file.
# 可选项（初始管理员密码、时区、转写协议默认值）从旧 .env 里原样带过来，不在这里凭空生成。
ADMIN_PASSWORD=`$(read_env_path DM_ADMIN_PASSWORD)
TZ_VALUE=`$(read_env_path DM_TZ)
TRANSCRIPTION_TYPE=`$(read_env_path DM_TRANSCRIPTION_API_TYPE)
{
    printf 'DM_CONFIG_DIR=%s\nDM_DATA_DIR=%s\nDM_PORT=%s\n' "`$CONFIG_DIR" "`$DATA_DIR" "$Port"
    [ -z "`$TZ_VALUE" ] || printf 'DM_TZ=%s\n' "`$TZ_VALUE"
    [ -z "`$TRANSCRIPTION_TYPE" ] || printf 'DM_TRANSCRIPTION_API_TYPE=%s\n' "`$TRANSCRIPTION_TYPE"
    [ -z "`$ADMIN_PASSWORD" ] || printf 'DM_ADMIN_PASSWORD=%s\n' "`$ADMIN_PASSWORD"
} > "`$ROOT/deploy/.env"
cleanup_preserved
trap - EXIT

COMPOSE="docker compose --env-file `$ROOT/deploy/.env -f `$ROOT/compose.yaml -f `$ROOT/deploy/compose.yaml"

echo "-- validate resolved compose configuration --"
`$COMPOSE config --quiet

echo "-- build --"
`$COMPOSE build app

echo "-- start --"
`$COMPOSE up -d

echo "-- wait for health --"
for _ in `$(seq 1 90); do
    if curl -fsS "http://127.0.0.1:$Port/api/system/health" >/dev/null 2>&1; then break; fi
    sleep 2
done
curl -fsS "http://127.0.0.1:$Port/api/system/health"; echo

echo "-- prune what the build left dangling --"
docker image prune -f | tail -1
docker ps --filter name=dailymusings --format '{{.Names}} | {{.Status}} | {{.Ports}}'

# 显式成功退出：`set -o pipefail` 会把上面最后一条管道的退出码带出来，于是「部署成功」被报成失败。
# 这个脚本的成败由健康检查决定，不由最后一条命令决定。
exit 0
"@

$b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($remote -replace "`r`n", "`n")))
& ssh -o BatchMode=yes $Server "echo $b64 | base64 -d | bash -s"
if ($LASTEXITCODE -ne 0) { throw "remote deploy failed" }

Remove-Item $tarball -Force
Write-Host "== done =="
