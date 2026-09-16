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

**阶段一（领域骨架与部署）已完成**，其余阶段未开始。

已实现：

- `DailyMusings.Domain` —— §6 全部领域实体与规则：内容日与「晚到」判定、历史检索的时间边界、三版本轮换不覆盖手工编辑、稿件与发布状态机、失败任务的有界指数退避、主题合并留墓碑等。
- `DailyMusings.Application` —— 端口与阶段一用例（管理员初始化 / 登录 / 强制改密、配对码签发与兑换、设备列表 / 撤销 / 令牌轮换、设备令牌认证、系统健康聚合）。
- `DailyMusings.Infrastructure` —— SQLite 自动向前迁移（嵌入式 SQL）、仓储、Docker Secrets 读取、目录约定、PBKDF2-SHA256 密码哈希、随机令牌与配对码、健康探针、单个后台任务执行器。
- `DailyMusings.Server` —— ASP.NET Core 宿主：启动即迁移与初始化管理员、Cookie 登录、设备 Bearer 令牌认证、强制改密闸门、结构化错误码、异常统一处理。
- `DailyMusings.Admin` —— Blazor 管理页：登录、强制改密、实例状态（健康 + 生成配对码）、设备管理（轮换 / 撤销）。
- `deploy/` —— Dockerfile、Compose、环境与 Secrets 示例。

尚未实现（后续阶段）：语音录制与转写、离线队列、MAUI 客户端、主题识别与 Embedding 检索、每日随想生成与来源映射、SMTP 通知、WordPress 与 Hexo 发布、导出与备份恢复、以及 MAUI 客户端测试项目（本机没有 MAUI 工作负载）。

关于领域模型的一个细节：`InputEntry`、`Topic`、`Reflection`、`ReflectionVersion`、`SourceReference`、`ProcessingJob`、`PublishTarget`、`Publication` 都已建模并落库，但除设备与身份相关的用例之外，**还没有任何用例操作它们**——那是阶段二到阶段四的工作。

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

覆盖范围：§17.1 列出的领域规则；迁移与仓储的原子性（含配对码只能被兑换一次的并发用例，以及一个真实的外键顺序回归）；用例层；以及一套走真实 HTTP 的端到端流程（初始化 → 强制改密 → 配对 → 令牌认证 → 轮换 → 撤销 → 重启后状态仍在）。

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
src/        Domain → Application → Infrastructure → Server / Admin（依赖方向由测试强制）
tests/      Domain / Application / Infrastructure / Api.IntegrationTests
deploy/     Dockerfile、compose.yaml、.env.example、secrets.example/
docs/       开发指导.md（唯一事实源，含附录 A 的设计定案记录）
```

## 文档

[`docs/开发指导.md`](docs/开发指导.md) —— 产品约束、领域模型、时间与生成规则、API 边界、测试策略与完成定义。它是本项目的唯一事实源；任何超出其范围的新需求都必须先改这份文档。
