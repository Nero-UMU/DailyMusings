# 把当前工作树部署到远程 Docker 主机（每日随想）。
#
# 这是本项目在 2026-09-24 之后的部署约定：
#   1. 把工作树打包送到服务器（不含 bin/obj/.git/artifacts/.tmp-*）；
#   2. 保留服务器上的 deploy/secrets（那是真实密钥，不进版本库也不进包）；
#   3. 停容器 → 删掉旧镜像 → 重新构建 → 启动新镜像 → 等健康检查 → 清理悬空镜像。
# 第 3 步是用户明确要求的：不允许旧镜像留在服务器上，每次部署都以新镜像启动。
#
# 用法：
#   pwsh -File tools/deploy/deploy.ps1                       # 用默认主机与端口
#   pwsh -File tools/deploy/deploy.ps1 -Server 100.64.0.3 -Port 18321
param(
    [string]$Server = "100.64.0.3",
    [int]$Port = 18321,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$tarball = Join-Path $env:TEMP "dailymusings-$stamp.tar.gz"

Write-Host "== 1. packaging $repo =="
# tar 是 Windows 10+ 自带的 bsdtar；用 .gitignore 之外的显式排除，避免把构建产物与本地状态送上去。
& tar -czf $tarball `
    --exclude=./.git --exclude=./.vs --exclude=./.tmp-work --exclude=./.tmp-device --exclude=./.tmp-verify `
    --exclude=./artifacts --exclude=./data --exclude=./keys --exclude=./media --exclude=./exports --exclude=./markdown `
    --exclude='*/bin' --exclude='*/obj' --exclude=./deploy/secrets `
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
ROOT=`$HOME/dailymusings
COMPOSE="docker compose -f `$ROOT/deploy/compose.yaml"
SECRETS=`$(mktemp -d)

echo "-- keep the real secrets out of the wipe --"
if [ -d "`$ROOT/deploy/secrets" ]; then cp -a "`$ROOT/deploy/secrets" "`$SECRETS/secrets"; fi

echo "-- stop and remove the running instance --"
`$COMPOSE down --remove-orphans || true

echo "-- remove the old image (user rule: never start a new image beside the old one) --"
docker image rm -f dailymusings/server:local || true

echo "-- lay down the new tree --"
rm -rf "`$ROOT"
mkdir -p "`$ROOT"
tar -xzf /tmp/dm-$stamp.tar.gz -C "`$ROOT"
if [ -d "`$SECRETS/secrets" ]; then cp -a "`$SECRETS/secrets" "`$ROOT/deploy/secrets"; fi
chmod 700 "`$ROOT/deploy/secrets" 2>/dev/null || true
chmod 644 "`$ROOT/deploy/secrets/"* 2>/dev/null || true
rm -rf "`$SECRETS"

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
