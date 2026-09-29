#!/bin/sh
# 容器入口：以 root 起步，只做一件事——把两个挂载目录的属主纠正到容器用户 uid 1654（app），然后用
# setpriv 立刻降权。服务进程本身始终不是 root，root 只存在于启动的头几毫秒。
#
# 为什么需要这一步：绑定挂载（DM_DATA_DIR / DM_CONFIG_DIR 指向宿主机目录）时，目录由 Docker 创建或由
# 用户创建，属主是 root 或宿主机用户，而镜像里的进程跑成 uid 1654，写不进去。让用户自己去
# `sudo mkdir` + `sudo chown -R 1654:1654` 是上一版部署里最容易出错的一步——漏掉它，实例会在迁移阶段
# 就失败，或者更糟：Secret 文件读不到，所有模型调用都报「没有密钥」（真踩过）。
#
# 只在属主不对时才递归 chown：第一次启动之后目录已经是 1654，后续每次启动都只是一次 stat。这样既不会
# 让启动变慢，也不会每次重启都遍历整个数据目录。
set -eu

APP_UID=1654
DATA_ROOT=/var/lib/dailymusings
CONFIG_ROOT=/var/lib/dailymusings-config

fix_owner() {
    target="$1"

    [ -d "$target" ] || mkdir -p "$target"

    current="$(stat -c '%u' "$target" 2>/dev/null || echo unknown)"
    if [ "$current" = "$APP_UID" ]; then
        return 0
    fi

    if chown -R "$APP_UID:$APP_UID" "$target" 2>/dev/null; then
        echo "[entrypoint] $target 的属主已从 uid $current 纠正为 uid $APP_UID。"
    else
        # Docker Desktop（Windows / macOS）上绑定挂载的属主由虚拟机透明处理，chown 可能无效但也不影响写入；
        # 在 Linux 上失败才是真的有问题，所以这里警告而不是直接退出——让实例自己的写入错误来报出真正的原因。
        echo "[entrypoint] 警告：无法纠正 $target 的属主（当前 uid $current）。" >&2
        echo "[entrypoint] 若实例随后报出写入失败，请在宿主机上执行：sudo chown -R $APP_UID:$APP_UID $target" >&2
    fi
}

if [ "$(id -u)" = "0" ]; then
    fix_owner "$DATA_ROOT"
    fix_owner "$CONFIG_ROOT"

    exec setpriv --reuid="$APP_UID" --regid="$APP_UID" --clear-groups -- "$@"
fi

# 已经以非 root 启动（例如 compose 里指定了 user:）时不再尝试改属主，直接运行。
exec "$@"
