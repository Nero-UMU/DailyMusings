# 每日随想 · DailyMusings

面向个人创作者的自托管「捕捉 → 转写 → 每日成文 → 核验 → 发布」闭环。

你随时用 Android 或 Windows 客户端录一段短语音或写一句话；服务端负责转写、主题识别、历史检索，在当天存在有效输入时生成一篇随想草稿，等你核验后再发布到 WordPress 或写成 Hexo Markdown。

核心原则（完整表述见 [`docs/开发指导.md`](docs/开发指导.md)）：

- 捕捉瞬间想法，而不是做会议录音或备忘录管理。
- 默认只生成私人草稿，由你核验后才发布。
- 模型服务由你自己提供，项目不捆绑任何模型。
- 数据完全自托管，并可完整导出。
- AI 只整理、重构和润色来源内容，不得故意补充不存在的事实；因此产品提供**来源追溯**与**无来源陈述检查**，而不是声称模型不会出错。

## 当前进度

**阶段一（领域骨架与部署）与阶段二（Android 输入闭环）已完成**，阶段三到阶段五未开始。

已实现：

- `DailyMusings.Domain` —— §6 全部领域实体与规则：内容日与「晚到」判定、历史检索的时间边界、三版本轮换不覆盖手工编辑、稿件与发布状态机、失败任务的有界指数退避、主题合并留墓碑等。
- `DailyMusings.Application` —— 端口与用例：管理员初始化 / 登录 / 强制改密、配对码签发与兑换、设备列表 / 撤销 / 令牌轮换、设备令牌认证、系统健康聚合、输入接收与维护、任务查询与重试。
- `DailyMusings.Infrastructure` —— SQLite 自动向前迁移（嵌入式 SQL）、仓储、Docker Secrets 读取、目录约定、PBKDF2-SHA256 密码哈希、随机令牌与配对码、健康探针、音频文件存储、OpenAI 兼容转写适配器、**真正消费任务的后台执行器**（独占认领、指数退避、重启恢复）。
- `DailyMusings.Server` —— ASP.NET Core 宿主：启动即迁移与初始化管理员、Cookie 登录、设备 Bearer 令牌认证、强制改密闸门、结构化错误码、异常统一处理、输入接收与任务状态 API。
- `DailyMusings.Admin` —— Blazor 管理页：登录、强制改密、实例状态（健康 + 生成配对码）、设备管理（轮换 / 撤销）。
- `DailyMusings.Client.Core` —— 平台中立的客户端逻辑：**离线队列**（先安全落盘再报告保存成功）、幂等上传、失败原因与手动重试、面向服务端的 HTTP 客户端。不依赖 MAUI，因此可完整单测，Windows 客户端将来可原样复用。
- `DailyMusings.Client` —— MAUI Android 应用：录音（MediaRecorder → AAC/MP4）、文字输入、待上传队列与失败重试、今天的输入、设置（服务器地址、测试连接、配对、解除配对）。最低 Android 8.0 / API 26。
- `deploy/` —— Dockerfile、Compose、环境与 Secrets 示例。

尚未实现（阶段三到五）：主题识别与 Embedding 语义检索、每日随想生成与来源映射、SMTP 通知、WordPress 与 Hexo 发布、导出与备份恢复、Windows 客户端、音频保留清理任务。

两个已知的实现取舍：

- **模型配置目前通过部署配置（环境变量 + Docker Secrets）提供**，还没有管理页编辑界面。§8.1 要求的界面属于后续阶段；现在配置的字段形状已经是那个界面将要写入的形状（`Transcription:BaseUrl` / `Model` / `SecretName` / `TimeoutSeconds`）。转写默认**关闭**，未配置时不会把音频发往任何地方。
- **录音路径尚未在真实 Android 设备上验证**。已在本机模拟器上验证应用能安装、启动、渲染、配对并完成一次文字输入的完整闭环；录音依赖平台 `MediaRecorder`，需要一台真实设备做最终确认（§19 的完成定义对此有明确要求）。


## 本地运行

需要 .NET SDK 10。首次启动会创建管理员账号，并把随机初始密码**只打印在终端**：

```bash
dotnet run --project src/DailyMusings.Server
```

打开启动日志里显示的地址（默认 `http://localhost:5xxx/`），用 `admin` 与终端里的初始密码登录。首次登录会强制你同时更换账号名和密码。

本地运行时实例状态默认落在当前目录下的 `data/`、`media/`、`exports/`、`markdown/`、`backups/`（已被 gitignore）。可用配置覆盖：

```bash
Storage__RootPath=/var/lib/dailymusings dotnet run --project src/DailyMusings.Server
```

## 运行测试

```bash
dotnet test
```

覆盖范围：§17.1 列出的领域规则；迁移与仓储的原子性（含配对码只能被兑换一次的并发用例、一个真实的外键顺序回归，以及文件存储的「先落盘后引用」）；用例层；客户端离线队列（含「本地副本只在服务端确认后才删除」的顺序断言与幂等键复用）；以及走真实 HTTP 的端到端流程——阶段一的完整流程，加上阶段二的上传 → 执行器认领 → 转写端点 → 结果落库（用一个可控的桩端点冒充 OpenAI 兼容服务），覆盖瞬时失败退避重试与永久失败后的人工恢复。

## 构建 Android 客户端

需要 `maui-android` 工作负载与 Android SDK：

```bash
dotnet workload install maui-android
dotnet build src/DailyMusings.Client -f net10.0-android -c Release -t:SignAndroidPackage
# 产物：src/DailyMusings.Client/bin/Release/net10.0-android/dev.dailymusings.client-Signed.apk
```

必须是 **Release**：Debug 版依赖 Fast Deployment，APK 里不含程序集，单独安装会以「No assemblies found」启动失败。

## Docker Compose 部署

```bash
cp -r deploy/secrets.example deploy/secrets     # 填入真实密钥（该目录已被 gitignore）
docker compose -f deploy/compose.yaml up -d --build
docker compose -f deploy/compose.yaml logs app | grep INITIAL-ADMIN-PASSWORD
```

- 单一实例。数据库驱动的任务队列假定只有一个执行器，**不要**横向扩容。
- 全部持久状态都在 `dailymusings-state` 卷里（数据库、音频、导出、Markdown 输出、备份），备份与迁移只需处理它。
- 另有 `dailymusings-keys` 卷存放 DataProtection 密钥环。它放在实例根目录**之外**是有意的：这个密钥环签发管理员登录 Cookie，属凭据等价物，放进实例卷就等于让每份备份都带着伪造会话的能力。删掉它会让所有管理员登出，但**不要**把它纳入备份。
- 密钥通过 Docker Secrets 以**文件名**引用，不进配置文件、不进日志、不进备份。

## 安全须知

- **允许通过公网 HTTP 访问，但那样不安全。** 密码、设备令牌、录音和文章都可能被同一网络中的他人截获。登录页会明确说明并要求你主动确认，管理页会持续显示不安全连接状态——**这个确认不会让连接变安全**。请优先使用 HTTPS、局域网或 VPN。
- 初始密码只在启动终端出现一次，不写入日志文件；首次登录必须同时更换账号名与密码。
- 设备令牌以哈希保存，可单独轮换与撤销，撤销在下一次请求即生效。
- 默认日志只记录请求/任务 ID、状态变化、耗时、重试次数和脱敏错误码，不记录转写正文、提示词、模型响应或认证信息。

## 目录结构

```
src/        Domain → Application → Infrastructure → Server / Admin（服务端依赖方向由测试强制）
            Client.Core → Client（客户端逻辑与 MAUI 界面分离，便于单测与复用）
tests/      Domain / Application / Infrastructure / Client / Api.IntegrationTests
deploy/     Dockerfile、compose.yaml、.env.example、secrets.example/
docs/       开发指导.md（唯一事实源，含附录 A 的设计定案记录）
```

## 文档

[`docs/开发指导.md`](docs/开发指导.md) —— 产品约束、领域模型、时间与生成规则、API 边界、测试策略与完成定义。它是本项目的唯一事实源；任何超出其范围的新需求都必须先改这份文档。
