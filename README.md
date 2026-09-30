<p align="center">
  <img src="docs/images/logo.png" width="120" alt="每日随想">
</p>

<h1 align="center">每日随想 · DailyMusings</h1>

<p align="center">面向个人创作者的自托管「捕捉 → 转写 → 每日成文 → 核验 → 发布 Markdown」闭环</p>

<p align="center">
  <a href="https://github.com/Nero-UMU/DailyMusings/releases"><img src="https://img.shields.io/github/v/release/Nero-UMU/DailyMusings" alt="Release"></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/Nero-UMU/DailyMusings" alt="License"></a>
  <a href="https://github.com/Nero-UMU/DailyMusings/pkgs/container/dailymusings"><img src="https://img.shields.io/badge/ghcr.io-dailymusings-blue" alt="Image"></a>
</p>

---

## 它是什么

随手记下一句话、录一段短语音，剩下的交给它：转写、归类，在当天确实有内容时写成一篇随想草稿，等你在后台核验、改定之后，**输出成一份 Markdown 文件**。这份文件就是本项目的交付物——交给 Hexo 之类的静态站点生成器即可。

它不做会议纪要，也不做备忘录管理。它要的是「此刻想到的那句话」不丢掉，并且在一天结束时变成一段能留下来的文字。

- 数据完全自托管，可完整导出与备份；模型服务由你自己提供，项目不捆绑任何模型。
- 默认只产出私人草稿，**你确认过才会发布**。
- 提供来源追溯与「无来源陈述」检查：AI 只允许整理、重构与润色你的原话，不允许编造事实；产品不声称模型不会出错，而是让你能核对它。

## 能做什么

### 手机端（Android，三个页面）

| 页面 | 用途 |
| --- | --- |
| 今日随想 | 占屏中央的大圆圈录音，一键切换手写；本条可播放、暂停、删除，按下「上传」才发送 |
| 日历 | 一行一条本机记录，必标时间，点开看完整内容与录音；可按新到旧 / 旧到新 / 今日 / 昨日 / 本周 / 本月筛选 |
| 设置 | 服务器地址、测试连接、配对与解除配对、本机占用与清空、实例上的模型名 |

本机记录在**上传成功后不再删除**——手机是往期记录的事实来源；断网也能继续记，回到网络后再同步。

### 服务端

- **转写**：你提供任意 OpenAI 兼容或 DashScope 兼容的转写接口，支持三种显式协议（`openai_transcription` / `openai_chat_audio` / `dashscope_async`）。上传时就在同一次响应里带回识别文本；超时或模型失败不影响上传成功，任务在队列里继续重试。
- **每天一篇**：当天存在有效输入就生成草稿；生成、发布时刻按你设定的时区执行，超窗不补做。
- **历史检索**：默认用主题标签与全文匹配；配置 Embedding 后启用语义检索（可随时关闭，关掉照常工作）。
- **主题**：由模型在写文章时挑选或新建，你可以在后台整理、改名、合并、删除。
- **核验**：三版本轮换不覆盖你的手工编辑；有来源映射可以逐句核对；有「无来源陈述」检查。
- **发布**：写成 `draft: true`（草稿）或 `draft: false`（正式稿）的 Markdown；同一天重新发布时旧稿转为「已取代」，正式稿目录只保留当天最新一份。
- **通知**：生成完成、待核验、未发布等事件可以发邮件；只带元数据（日期、标题、状态），不含正文。
- **运维**：一键备份、可读导出、上传备份恢复、保留策略（录音默认确认后保留 30 天、随想内容默认永不删除）、日志与限时调试模式。

### 后台管理（八个分区）

| 分区 | 管什么 |
| --- | --- |
| 状态 | 今日随想数量、稿件与发布计数、设备与队列、能力状态、存储占用、健康检查、生成配对码 |
| 设备 | 已配对设备、撤回授权、更换令牌、删除已撤销设备 |
| 数据管理 | 从新到旧逐条看随想，修订转写或手写文字，播放录音，永久删除，重试转写 |
| 模型管理 | 转写 / 生成 / Embedding 三块：接口地址、模型名、超时、API Key，并用当前表单值真实测试连接 |
| 通知管理 | SMTP 全套、收件地址、事件开关、真发一封测试邮件 |
| 发布设置 | 立即生成 / 立即发布、每日生成与发布时间、自动发布、博客生成规范（字数 / 人称 / 行文清单）、草稿与正式稿目录、Hexo front matter 模板 |
| 内容管理 | 搜索、筛选、查看、删除稿件，为稿件分配主题，管理主题库 |
| 系统设置 | 监听端口、自动备份、备份与导出 / 恢复、两条保留策略、语义索引、日志与调试、修改账号密码 |

## 它怎么工作

```
手机录音 / 手写
      │  按下「上传」
      ▼
服务端接收 ──► 转写（你配置的模型）──► 主题归属 ──► 历史检索
      │
      ▼
当天有有效输入 ──► 在生成时刻写成草稿（draft: true）
      │
      ▼
后台核验：看来源、接受或编辑、必要时换版本、跑一次无来源陈述检查
      │
      ▼
发布 ──► 正式的 Markdown（draft: false）──► 交给 Hexo
```

**你不上传、不配置模型，就什么都不会发往任何地方。**

## 快速开始

需要一台装了 Docker 的机器和一部 Android 8.0 以上的手机。**只要一份 compose 文件**：

```bash
mkdir dailymusings && cd dailymusings
curl -fsSLO https://raw.githubusercontent.com/Nero-UMU/DailyMusings/main/compose.yaml
docker compose up -d
docker compose logs app | grep INITIAL-ADMIN-PASSWORD     # 一次性的管理员初始密码
```

然后浏览器打开 `http://<这台机器的IP>:18321/`，用 `admin` 和那串密码登录（首次登录强制改账号名与密码），在「状态」页生成配对码。

完整步骤、参数表与排障对照表见 **[`docs/快速部署.md`](docs/快速部署.md)**。镜像在 `ghcr.io/nero-umu/dailymusings`（`linux/amd64` 与 `linux/arm64`，公开包），发布记录见 [Releases](https://github.com/Nero-UMU/DailyMusings/releases)。

## 部署参数

[`compose.yaml`](compose.yaml) 本身不带注释，可设置的东西全部在这里。**每一项都是可选的**，不设就用默认值。

| 变量 | 默认 | 作用 |
| --- | --- | --- |
| `DM_IMAGE` | `ghcr.io/nero-umu/dailymusings:latest` | 镜像。固定版本写 `:1.0.8`，或换成你自己的仓库 |
| `DM_PORT` | `18321` | 宿主机端口。容器内固定 18321，**只改这一个** |
| `DM_DATA_DIR` | `./data`（与 compose 同级） | **只放 Markdown**：草稿与正式稿。后台「发布设置」里填的目录都以它为根，所以它可以整份交给 Hexo、也可以整份同步走 |
| `DM_CONFIG_DIR` | `./config`（与 compose 同级） | **其余全部状态**：SQLite（含 `-wal`/`-shm`）、录音、可读导出、备份包、`runtime.json`、登录密钥环与管理页保存的凭据。**单独保护，别混进普通内容备份** |
| `DM_ADMIN_PASSWORD` | 不设 | 初始管理员密码（至少 12 位）。不设就随机生成，并只打印一次到容器日志 |
| `DM_TZ` | `Asia/Shanghai` | 容器日志时区（内容的时区在管理页「发布设置」里，与此无关） |
| `DM_LOCK_LISTENING_PORT` | `1` | 让部署独占监听端口：`runtime.json` 里残留的端口覆盖被忽略、`PATCH /api/system/listening-port` 返回 409 `instance.port.locked`、管理页「系统设置」那一节变成只读。设成 `0` 回到老行为（后台可以存端口，但你得自己把映射与容器内监听同步好再重建） |

两个目录**不需要预先创建，也不需要 chown**：容器入口会把它们建好、把属主改成容器用户（uid 1654），随后降权运行。

**目录是怎么对应的**：假设你映射了 `/srv/dailymusings/data:/var/lib/dailymusings`，后台「发布设置」里填 `aaa/bbb/posts`，稿件就写到宿主上的 `/srv/dailymusings/data/aaa/bbb/posts`——**填的就是你在宿主机上看到的那条相对路径**，中间没有别的层级。填 `drafts`、`posts` 这类名字就是数据目录下的一级子目录（也是默认值）。不允许绝对路径、不允许 `..`。

```text
/srv/dailymusings/data                →  /var/lib/dailymusings          # 只放 Markdown
  drafts/2026-09-30-今天的记录.md
  aaa/bbb/posts/2026-09-29-昨天的记录.md
/srv/dailymusings/config       →  /var/lib/dailymusings-config   # 其余全部状态
  dailymusings.db  dailymusings.db-wal
  media/  exports/  backups/
  keys/  runtime.json
```

> **升级到 1.0.8 之前的部署请先挪一次目录**。这是**破坏性变更**：库、录音、备份从数据目录搬进了配置目录，稿件也从 `markdown/` 底下上移了一层。实例**不会自动搬**，直接升级会看到一份空实例（旧的库还在原地，可以随时挪回来）。在宿主机上执行一次：
>
> ```bash
> # 在 compose 所在目录（DM_DATA_DIR / DM_CONFIG_DIR 指向的实际路径）
> mv "$DM_DATA_DIR"/dailymusings.db* "$DM_CONFIG_DIR"/            # 库，含 -wal / -shm
> for d in media exports backups; do mv "$DM_DATA_DIR/$d" "$DM_CONFIG_DIR/"; done
> mv "$DM_DATA_DIR"/markdown/*/ "$DM_DATA_DIR"/                  # 稿件上移一层：去掉 markdown/ 这层
> rmdir "$DM_DATA_DIR"/markdown "$DM_DATA_DIR"/data 2>/dev/null  # 空的旧目录（有残留会报错，正常）
> ```
>
> 挪完再 `docker compose pull && docker compose up -d`。凭据与密钥环本来就在配置目录里，不用动。

**怎么设**，三种都行：

```bash
# 1) 直接改 compose 里的默认值
# 2) 在同目录放一个 .env，里面写 DM_PORT=8080
# 3) 启动前导出：
DM_PORT=8080 docker compose up -d
```

**想把数据放到别处**（例如 NAS 共享目录）：`DM_DATA_DIR=/srv/dm/data DM_CONFIG_DIR=/srv/dm/config docker compose up -d`。

**想用命名卷而不是宿主机目录**：把那两行挂载换成 `dm-data:/var/lib/dailymusings` 与 `dm-config:/var/lib/dailymusings-config`，并在文件末尾补上 `volumes:` 和这两个卷名。

> **部署参数里没有、也不会有 API Key。** 模型密钥与 SMTP 密码只能登录管理页填（加密存放在配置目录里）：
> 一份能被 compose 注入的密钥，就是一份躺在宿主机上、还会出现在 `docker inspect` 里的明文密钥。
> 见下面的「配置模型与邮件」。

## 手机端

去 [Releases](https://github.com/Nero-UMU/DailyMusings/releases) 下载 `DailyMusings-client-<版本>-android.apk` 安装（自签名包，系统会提示「未知来源」；同页给出 SHA-256）。打开 App → 设置页填服务端地址 → 测试连接 → 填配对码 → 配对。

> 手机必须能直达服务端地址：同一个局域网、连同一个 VPN，或者你把端口映射出去。明文 HTTP 可用，但登录页会要求你确认风险——**确认不会让连接变安全**，详见下面的「安全与隐私」。

> **升级安装**：Android 要求覆盖安装的签名与已安装版本一致。**1.0.6 之前发布的 APK 每版签名都不同**（CI 在全新 runner 上自动生成的调试密钥），所以装过旧版的手机需要**先卸载一次**再装 1.0.6 或更新版本；从那以后每一版都能直接覆盖安装。签名指纹与核对方法见 [`docs/发布校验值.md`](docs/发布校验值.md#apk-签名升级安装的前提)。

## 配置模型与邮件

需要生成文章时，在后台「模型管理」填一个生成模型的 API Key（新实例默认 DeepSeek）；转写与 Embedding 同理；邮件在「通知管理」里填。**保存即生效，不需要重启容器**。

Base URL、模型名和 API Key 都由你自己指定——项目不捆绑任何模型，也没有厂商白名单；转写还要显式选一个 API 类型（`openai_transcription` / `openai_chat_audio` / `dashscope_async`），因为各家即使地址写着兼容、协议也不同，实现不按地址猜协议。三个能力（转写 / 生成 / Embedding）的配置相互独立。

**密钥只有一种给法：在管理页里填。** 值加密保存在配置目录的 `keys/` 下，不进配置表、不进日志、不进导出、不进备份，页面也读不回明文，只告诉你「已保存 / 未配置」。部署配置（compose、环境变量、挂载文件）**不能**提供密钥——这是有意的：能被部署注入的密钥就是宿主机上的明文。

## 备份、恢复与升级

- 备份：后台「系统设置」一键备份（数据库快照 + 媒体 + Markdown + 索引元数据 + 非秘密配置，默认保留最近七份）；也可以直接备份数据目录。**配置目录单独留着**——它装着登录密钥环，没有它恢复后所有设备都要重新配对。
- 备份包**不含**设备令牌与 DataProtection 密钥环（A.14），也不含任何 API Key。
- 导出：可读包 = Markdown + JSON + 尚未被清理的音频，随时可以整包拿走。
- 恢复是**暂存 + 重启生效**：上传后先校验（schema 版本、zip-slip、是否含设备令牌），下次启动在迁移之前应用，原数据库保留为 `pre-restore-<时间戳>.db`。
- 升级：`docker compose pull && docker compose up -d`。向前迁移在启动时自动执行，不承诺自动降级——先备份。

## 安全与隐私

- **优先 HTTPS、局域网或 VPN**。通过明文 HTTP 访问时，密码、设备令牌、录音和文章都可能被同一网络中的他人截获；登录页会要求管理员主动确认，但那只是提示，不提供任何保护。
- 初始管理员密码只在启动终端出现一次，不写进日志文件；首次登录必须同时更换账号名与密码。
- 设备令牌以哈希保存，可单独轮换与撤销，撤销在下一次请求即生效。
- 默认日志只记录请求 / 任务 ID、状态变化、耗时、重试次数与脱敏错误码，**不含**转写正文、提示词、模型响应或密钥。调试模式是手动、限时、且开启前必须确认风险的。
- 单一实例：数据库驱动的任务队列假定只有一个执行器，**不要**横向扩容。

## 从源码构建与开发

需要 .NET SDK 10。

```bash
dotnet run --project src/DailyMusings.Server     # 本地起服务（初始密码打印在终端）
dotnet test                                      # 全量测试：596 项
```

本地运行时两个根默认落在仓库下的 `.dailymusings/state`（状态）与 `.dailymusings/markdown`（Markdown，已 gitignore）；可用 `Storage__StatePath` / `Storage__MarkdownRootPath` 覆盖。要真正生成草稿，需要指向一个 OpenAI 兼容端点：

```bash
Generation__Enabled=true \
Generation__BaseUrl=https://api.deepseek.com \
Generation__Model=deepseek-flash \
dotnet run --project src/DailyMusings.Server
```

启动后在管理页「模型管理」里粘贴 API Key（A.27 起密钥只能从页面填，`Generation__SecretName` 只是它存进哪个槽位的名字，默认不用改）。

Android 客户端（需要 `maui-android` 工作负载与 Android SDK；**必须 Release**，Debug 版依赖 Fast Deployment，单独安装会以「No assemblies found」启动失败）：

```bash
dotnet workload install maui-android
dotnet build src/DailyMusings.Client -f net10.0-android -c Release -t:SignAndroidPackage
```

从源码构建服务端镜像（维护者通道，服务定义与对外那份是同一个）：

```bash
docker compose -f compose.yaml -f deploy/compose.yaml up -d --build
```

测试之外还有两层验收脚本：`tools/deploy/` 把工作树部署到远程主机并做部署后自检，`tools/acceptance/` 对着真实部署跑端到端与恢复验证。用法与最近结果见各自目录的 README。

### 目录结构

```
compose.yaml  对外部署入口：一份文件拉镜像起服务
.github/      release.yml：打 tag 时构建多架构镜像与 APK，并用 compose.yaml 起一次实例
src/          Domain → Application → Infrastructure → Server / Admin（依赖方向由测试强制）
              Client.Core → Client（客户端逻辑与 MAUI 界面分离；客户端只有 Android 目标）
tests/        Domain / Application / Infrastructure / Client / Api.IntegrationTests
tools/        deploy/：部署与部署后自检脚本；acceptance/：端到端与恢复验证脚本
deploy/       Dockerfile、docker-entrypoint.sh、compose.yaml（从源码构建的覆盖文件）、.env.example
docs/         开发指导.md（唯一事实源）、快速部署.md、使用手册.md、发布校验值.md
```

## 文档

| 文档 | 内容 |
| --- | --- |
| [`docs/快速部署.md`](docs/快速部署.md) | 新用户最短路径：一份 compose、起服务、装 APK、配对，以及参数表与排障 |
| [`docs/使用手册.md`](docs/使用手册.md) | 面向使用者与实例管理员：部署、密钥、首次登录与配对、每个管理页、备份与八步恢复、升级、排障 |
| [`docs/开发指导.md`](docs/开发指导.md) | 产品约束、领域模型、时间与生成规则、API 边界、测试策略与完成定义。**唯一事实源**，附录 A 是逐条设计定案 |
| [`docs/发布校验值.md`](docs/发布校验值.md) | 产物校验值、复现构建命令与验收状态（如实写明未覆盖的部分） |
| [`CHANGELOG.md`](CHANGELOG.md) | 每一轮改动的动机、备选方案、定案理由与验收结果；仍然有效的实现取舍与历史路径 |
| [`tools/acceptance/README.md`](tools/acceptance/README.md) | 端到端验收与恢复验证脚本的用法与执行结果 |

## 许可

[MIT](LICENSE)
