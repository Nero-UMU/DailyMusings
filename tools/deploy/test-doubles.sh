#!/usr/bin/env bash
# 测试替身：模型桩（转写 / 生成 / Embedding）+ 邮件接收端。
#
# 它们只挂在实例自己的 docker 网络上，宿主机不留任何改动；实例以容器名互相访问：
#   http://dm-stub:8077/v1      模型端点（转写 / 生成 / Embedding 都指这里）
#   dm-mailpit:1025             SMTP
#   http://127.0.0.1:8025/      Mailpit 的网页界面（从服务器上看邮件）
#
# 用法（在服务器上，或通过 ssh bash -s 送入）：
#   bash tools/deploy/test-doubles.sh up
#   bash tools/deploy/test-doubles.sh down
set -euo pipefail

NETWORK=dailymusings_default
ACTION="${1:-up}"
ROOT="${DM_ROOT:-$HOME/dailymusings}"

case "$ACTION" in
    down)
        docker rm -f dm-stub dm-mailpit >/dev/null 2>&1 || true
        echo "test doubles removed"
        exit 0
        ;;
    up) ;;
    *) echo "usage: $0 [up|down]" >&2; exit 2 ;;
esac

docker network inspect "$NETWORK" >/dev/null 2>&1 || docker network create "$NETWORK" >/dev/null

docker rm -f dm-stub dm-mailpit >/dev/null 2>&1 || true

docker run -d --name dm-mailpit --network "$NETWORK" --restart no \
    -p 127.0.0.1:8025:8025 \
    axllent/mailpit:latest >/dev/null

# 桩里的转写映射：一个 "*" 条目就是「任何录音都得到这段文字」，因为录音是手机现场选的，无法预先登记。
MAP=/tmp/dm-transcripts.json
if [ ! -f "$MAP" ]; then
    cat > "$MAP" <<'JSON'
{"*": "今天在服务器上做验收测试，顺便试一下录音与转写。"}
JSON
fi

docker run -d --name dm-stub --network "$NETWORK" --restart no \
    -v "$ROOT/tools/acceptance/stub_models.py":/stub.py:ro \
    -v "$MAP":/transcripts.json:ro \
    -e STUB_PORT=8077 \
    -e STUB_HOST=0.0.0.0 \
    -e STUB_TRANSCRIPT_MAP=/transcripts.json \
    -e STUB_STATS=/dev/null \
    python:3-alpine python /stub.py >/dev/null

sleep 2

echo "== containers =="
docker ps --filter name=dm- --format '{{.Names}} | {{.Status}}'

echo
echo "== reachable from the app container =="
for url in http://dm-stub:8077/v1/models; do
    printf '%s -> ' "$url"
    docker exec dailymusings-app-1 curl -fsS --max-time 5 "$url" 2>/dev/null | head -c 120 || echo "(no answer)"
    echo
done

echo
echo "配置用的地址："
echo "  转写 / 生成 / Embedding Base URL : http://dm-stub:8077/v1"
echo "  SMTP                              : dm-mailpit 端口 1025，SSL 与 STARTTLS 都不勾（本机中继）"
echo "  Mailpit 界面                      : http://127.0.0.1:8025/"
