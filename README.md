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

**阶段一（领域骨架与部署）、阶段二（Android 输入闭环）、阶段三（主题与每日生成）与阶段四（通知与发布）已完成**，阶段五未开始。

已实现：

- `DailyMusings.Domain` —— §6 全部领域实体与规则：内容日与「晚到」判定、历史检索的时间边界、三版本轮换不覆盖手工编辑、稿件与发布状态机、失败任务的有界指数退避、主题合并留墓碑等。阶段三补充检索的降级与排序策略（`RetrievalPolicy`）、主题自动识别（`TopicMatcher`）、生成资格与暂缓规则（`GenerationRules`）、引文定位（`SourceQuoteLocator`）。阶段四补充自动发布意图决策（`PublicationPlanner`：草稿 / 公开 / 超窗过期）、Markdown 模板与文件名策略、写入前的「是否我们的文件」判定（`MarkdownWritePolicy`）、远程差异比较，以及通知事件与幂等键。
- `DailyMusings.Application` —— 端口与用例：管理员初始化 / 登录 / 强制改密、配对码签发与兑换、设备管理、系统健康聚合、输入接收与维护、任务查询与重试、主题、历史检索、生成编排、无来源陈述检查、Embedding 索引；阶段四补充发布目标的增改与自动公开开关（`SetAutomaticPublishUseCase` 强制校验管理员密码）、发布排队 / 执行 / 重试 / 检查远程差异 / 三种处置（拉取、覆盖、保留两边）、通知的排队与发送、内容与发布时间配置。
- `DailyMusings.Infrastructure` —— SQLite 自动向前迁移、仓储、Docker Secrets、目录约定、PBKDF2-SHA256、健康探针、音频存储、OpenAI 兼容转写 / 生成 / Embedding 适配器、**真正消费任务的后台执行器**（独占认领、指数退避、重启恢复、分批续跑）、**调度器**（生成时刻、逐日补跑、Embedding 指纹变化时重建、发布时刻、执行窗口、过期与失效），阶段四补充 **WordPress REST 客户端**（Application Password、同 slug 收养以避免重试产生重复文章）、**Markdown 安全写入**（临时文件 + 原子改名、只覆盖自己写过且未被改动的文件）、**SMTP 通知发送**。
- `DailyMusings.Server` —— ASP.NET Core 宿主：启动即迁移与初始化管理员、Cookie 登录、设备 Bearer 令牌认证、强制改密闸门、结构化错误码、统一异常处理，以及输入 / 任务 / 主题 / 稿件 / 来源 / 语义检索 / 发布目标 / 发布记录 / 通知设置 / 内容设置 API。
- `DailyMusings.Admin` —— Blazor 管理页：登录、强制改密、实例状态与配对码、设备管理，以及**发布目标页**（新增目标、开启 / 关闭自动公开——开启前展示风险说明并要求重新输入管理员密码）。
- `DailyMusings.Client.Core` / `DailyMusings.Client` —— 见下。
- `deploy/` —— Dockerfile、Compose、环境与 Secrets 示例。

尚未实现（阶段五）：导出与备份恢复、Windows 客户端、音频保留清理任务、日志与索引重建的管理界面。客户端也还没有主题页、日历页和草稿页（§9.3），目前只有今日页与设置页。

三个已知的实现取舍：

- **模型与 SMTP 配置通过部署配置提供**，还没有管理页编辑界面。§8.1 要求的界面属于后续阶段；配置的字段形状已经是那个界面将要写入的形状（`Transcription:` / `Generation:` / `Embedding:` / `Smtp:` / `Notification:` / `Publishing:WordPress:`），而**内容时区与生成 / 发布时刻已经可以在管理页配置**（§4.1、§7）。转写、生成、Embedding、SMTP 默认**全部关闭**，未配置时不会把任何内容发往任何地方；生成关闭时调度器不会往队列里塞任务。
- **§13 的接口表里没有「创建主题」**，而没有它主题功能无法使用（自动识别只把内容归入已存在的主题）。因此增加了 `POST /api/topics`，按名称创建且幂等（忽略大小写、空格与标点）。
- **WordPress 没有幂等键**，而 §17.2 要求重试不产生重复文章。除了「先记下远端 id 再更新」之外，创建前还会按 slug 查一次：一次响应丢失的 POST 与一次没到达的 POST 无法区分，而 slug 在同一 post type 内唯一，所以同 slug 的文章就是这次请求创建的。

已在真实环境验证过的路径：

- **阶段一**：在远程 Docker 主机上完成两轮 Compose 部署验收，包括从空目录起服务、自动迁移与管理员初始化，以及 A.14 的密钥环独立卷（重建容器后同一 Cookie 仍有效）。
- **阶段二**：在真实 Android 设备（Xiaomi 23127PN0CC / Android 16）上跑通录音 → 上传 → 转写 → 中文转写结果回到界面并显示「已转写」，上传的 312,237 字节与落盘文件逐字节一致。
- **阶段三**：以真实服务进程 + 桩模型端点跑通采集 → 主题自动归属 → Embedding 建索引 → 生成 → 来源映射 → 无来源陈述检查 → 确认。其中特意让被引用的历史记录与当天记录**既无共同主题也无共同措辞，只有向量相近**，因此它出现在来源映射里，就只可能来自语义检索。
- **阶段四**：以仓库自己的 Dockerfile 构建的容器 + 真实 WordPress 9 站点（Application Password）、真实 SMTP 接收端（Mailpit）与真实 Hexo 草稿目录跑通：超窗未执行 → 转 Expired 且不发布任何内容并发出提醒；窗口内由调度器自动上传为**私人草稿**；手动公开后文章在站点上确实为公开；重复导出**更新同一篇文章**而不是新建；在站点侧改动文章后 `check-remote` 能识别差异，`pull` 把远端正文取回为受保护的新版本；Markdown 导出落在真实的 Hexo `source/_drafts` 且 front matter 可被 Hexo 解析；以及权限边界——设备令牌不能开启自动公开，密码错误时开关仍然关闭。


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

要真正生成草稿，需要指向一个 OpenAI 兼容端点：

```bash
Generation__Enabled=true \
Generation__BaseUrl=https://api.example.com/v1 \
Generation__Model=gpt-4o-mini \
Generation__SecretName=openai-api-key \
Storage__SecretsPath=/run/secrets \
dotnet run --project src/DailyMusings.Server
```

`Embedding__Enabled=true` 需另行开启；关掉它产品照常工作，历史检索退化为主题标签与全文匹配（§8.3）。

## 运行测试

```bash
dotnet test
```

覆盖范围：§17.1 列出的领域规则；迁移与仓储的原子性（含配对码只能被兑换一次的并发用例、一个真实的外键顺序回归，以及文件存储的「先落盘后引用」）；用例层；客户端离线队列（含「本地副本只在服务端确认后才删除」的顺序断言与幂等键复用）；以及走真实 HTTP 的端到端流程——阶段一的完整流程，阶段二的上传 → 执行器认领 → 转写端点 → 结果落库，阶段三的采集 → 主题识别 → 语义检索 → 生成 → 来源映射 → 无来源陈述检查 → 确认，以及两项拒绝（任意历史日期不可生成、未确认存疑句不可确认）。外部服务一律使用可控桩端点。

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
