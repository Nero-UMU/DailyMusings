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

**阶段一（领域骨架与部署）、阶段二（Android 输入闭环）、阶段三（主题与每日生成）、阶段四（通知与发布）与阶段五（Windows、运维与交付）均已完成。**

已实现：

- `DailyMusings.Domain` —— §6 全部领域实体与规则：内容日与「晚到」判定、历史检索的时间边界、三版本轮换不覆盖手工编辑、稿件与发布状态机、失败任务的有界指数退避、主题合并留墓碑等。阶段三补充检索的降级与排序策略（`RetrievalPolicy`）、主题自动识别（`TopicMatcher`）、生成资格与暂缓规则（`GenerationRules`）、引文定位（`SourceQuoteLocator`）。阶段四补充自动发布意图决策（`PublicationPlanner`：草稿 / 公开 / 超窗过期）、Markdown 模板与文件名策略、写入前的「是否我们的文件」判定（`MarkdownWritePolicy`）、远程差异比较，以及通知事件与幂等键。阶段五补充音频保留策略（`AudioRetentionPolicy`：保留 N 天 / 永久 / 立即删除，以及「转写成功且草稿已确认」才可清理的判定）。
- `DailyMusings.Application` —— 端口与用例：管理员初始化 / 登录 / 强制改密、配对码签发与兑换、设备管理、系统健康聚合、输入接收与维护、任务查询与重试、主题、历史检索、生成编排、无来源陈述检查、Embedding 索引；阶段四补充发布目标的增改与自动公开开关（`SetAutomaticPublishUseCase` 强制校验管理员密码）、发布排队 / 执行 / 重试 / 检查远程差异 / 三种处置（拉取、覆盖、保留两边）、通知的排队与发送、内容与发布时间配置；阶段五补充可读导出与完整备份的组装（含清单与「哪些东西按定案不进包」的声明）、恢复暂存与校验、音频清理、调试模式、外部服务探测与索引重建。
- `DailyMusings.Infrastructure` —— SQLite 自动向前迁移、仓储、Docker Secrets、目录约定、PBKDF2-SHA256、健康探针、音频存储、OpenAI 兼容转写 / 生成 / Embedding 适配器、**真正消费任务的后台执行器**（独占认领、指数退避、重启恢复、分批续跑）、**调度器**（生成时刻、逐日补跑、Embedding 指纹变化时重建、发布时刻、执行窗口、过期与失效），阶段四补充 **WordPress REST 客户端**（Application Password、同 slug 收养以避免重试产生重复文章）、**Markdown 安全写入**（临时文件 + 原子改名、只覆盖自己写过且未被改动的文件）、**SMTP 通知发送**；阶段五补充**导出写入器**（先写 `.partial` 再改名、同名时让位而不是覆盖、清单放包根）、**备份写入器**（`VACUUM INTO` 快照 + 剥离设备令牌再 `VACUUM` 重建文件 + zip + 按名字而非文件系统时间裁剪保留份数）、**恢复服务**（包外一律不动：校验 schema 版本、拒绝 zip-slip、拒绝含设备令牌的包，随后写入 `restore-pending.json`，由下次启动应用并保留 `pre-restore-*.db`）、**启动期应用恢复**、音频清理与备份任务、**调试模式与应用设置存储**、**外部服务探测**（转写 / 生成 / Embedding 用 `/models`，SMTP 只读问候语，WordPress 读当前用户）。
- `DailyMusings.Server` —— ASP.NET Core 宿主：启动即迁移与初始化管理员、Cookie 登录、设备 Bearer 令牌认证、强制改密闸门、结构化错误码、统一异常处理，以及输入 / 任务 / 主题 / 稿件 / 来源 / 语义检索 / 发布目标 / 发布记录 / 通知设置 / 内容设置 API；阶段五补充管理员专属的导出、备份、恢复、清理、索引与诊断（测试连接、四小时上限的调试模式）接口，以及 `GET /api/inputs/{id}/audio`。
- `DailyMusings.Admin` —— Blazor 管理页：登录、强制改密、实例状态与配对码、设备管理、**发布目标页**（新增目标、开启 / 关闭自动公开——开启前展示风险说明并要求重新输入管理员密码），以及阶段五的**运维页**（导出与备份列表、一键备份、上传备份恢复、音频清理、索引状态与重建、五项测试连接、临时调试模式——开启前必须确认「可能把私人内容写进日志」）。
- `DailyMusings.Client.Core` / `DailyMusings.Client` —— 与界面无关的客户端逻辑（离线队列、上传与重试、配对与令牌）+ MAUI 界面（今日页、设置页）。阶段五把单一 Android 目标改为 **Android 与 Windows 双目标**：共享部分一行未动，新增的只有平台录音实现（Android `MediaRecorder` / Windows `MediaCapture`，后者输出 96 kbps 单声道 m4a）、Windows 的 WinUI 应用外壳与 `Package.appxmanifest`。真机上发现并修掉的两处状态显示问题（上传完成后仍显示「正在上传」、转写轮询无上限）也已并入。
- `deploy/` —— Dockerfile、Compose、环境与 Secrets 示例。

尚未实现：客户端只有今日页与设置页，**没有** §9.3 的主题页、日历页和草稿页；客户端也还没有播放录音的界面（服务端已能取回音频，见下）。Windows 客户端只做过「可执行文件能启动并保持运行」的冒烟，**没有**做过交互式界面验证（输入、录音），因此 §19「跨平台功能还需在 Windows 验证」这一条只部分满足。

阶段五由验收脚本发现并修掉的四处问题（都不是脚本自身的问题，脚本的判定依据写进了 [`tools/acceptance/README.md`](tools/acceptance/README.md)）：

- **未接受覆盖的重新生成会把同一轮的幂等键占住**：用户第一次请求重新生成、却还没接受「会覆盖手工修改」（§6.4 要求显式接受），队列里会留下一条拒绝轮换的任务；此后同一天同一轮再请求时就命中同一个幂等键，队列把那条已完成的任务还回来，用户接受覆盖的决定被静默丢弃。修法是把「已接受覆盖」并入幂等键，使两次不同意图各自成键。
- **同一秒内的两个导出（或两个备份）会互相覆盖**：包名用秒级时间戳，写入器遇到同名目录/文件是删掉再建。对一个承诺「导出是你自己的副本」「备份保留最近七份」的功能来说，一键两下就丢掉一份是错的，因此同名时改为加后缀让位。
- **备份裁剪按文件系统时间排序，同秒并列时可能删掉最新的那份**：`PruneAsync` 用创建时间倒序，而同秒写入的多份归档时间相同，排序结果不稳定。改为按写入器自己发出去的名字（时间戳 + 后缀）排序。
- **备份快照删掉设备令牌后，文件里仍留着该哈希的字节**：`DELETE` 只把行标为空闲页，字节还在，`grep` 备份包仍能找到设备令牌的摘要。摘要本身不能通过认证（接受它的行已不存在），但「备份不含设备令牌」这条承诺必须经得起**翻文件**式的检查，因此在剥离之后再做一次 `VACUUM` 重建快照文件。

三个已知的实现取舍：

- **模型与 SMTP 配置通过部署配置提供**，还没有管理页编辑界面。§8.1 要求的界面属于后续阶段；配置的字段形状已经是那个界面将要写入的形状（`Transcription:` / `Generation:` / `Embedding:` / `Smtp:` / `Notification:` / `Publishing:WordPress:`），而**内容时区与生成 / 发布时刻已经可以在管理页配置**（§4.1、§7）。转写、生成、Embedding、SMTP 默认**全部关闭**，未配置时不会把任何内容发往任何地方；生成关闭时调度器不会往队列里塞任务。
- **§13 的接口表里没有「创建主题」**，而没有它主题功能无法使用（自动识别只把内容归入已存在的主题）。因此增加了 `POST /api/topics`，按名称创建且幂等（忽略大小写、空格与标点）。
- **WordPress 没有幂等键**，而 §17.2 要求重试不产生重复文章。除了「先记下远端 id 再更新」之外，创建前还会按 slug 查一次：一次响应丢失的 POST 与一次没到达的 POST 无法区分，而 slug 在同一 post type 内唯一，所以同 slug 的文章就是这次请求创建的。
- **§13 的接口表里没有「取回录音」**，而 A.1 保留音频 30 天的理由正是「用户事后能核对转写是否有误」，§15.2 第 6 步也要求恢复后的实例里语音可播放。在补上读取路径之前，音频只能从宿主机卷上取，这两条承诺在产品内部都无法验证，因此增加了 `GET /api/inputs/{id}/audio`（与其它输入接口同权限，支持 Range 请求以便播放器拖动）。客户端还没有播放界面。
- **恢复是「暂存 + 下次启动生效」**，不是当场替换运行中的数据库：备份包先被校验（schema 版本、zip-slip、是否含设备令牌、是否来自更新的版本），写入 `restore-pending.json`，由下次启动在迁移之前应用，并把原数据库保留为 `pre-restore-<时间戳>.db`。这样做的好处是恢复失败不会留下半应用的实例；代价是操作者必须重启一次，管理页会明确这么写。

已在真实环境验证过的路径：

- **阶段一**：在远程 Docker 主机上完成两轮 Compose 部署验收，包括从空目录起服务、自动迁移与管理员初始化，以及 A.14 的密钥环独立卷（重建容器后同一 Cookie 仍有效）。
- **阶段二**：在真实 Android 设备（Xiaomi 23127PN0CC / Android 16）上跑通录音 → 上传 → 转写 → 中文转写结果回到界面并显示「已转写」，上传的 312,237 字节与落盘文件逐字节一致。
- **阶段三**：以真实服务进程 + 桩模型端点跑通采集 → 主题自动归属 → Embedding 建索引 → 生成 → 来源映射 → 无来源陈述检查 → 确认。其中特意让被引用的历史记录与当天记录**既无共同主题也无共同措辞，只有向量相近**，因此它出现在来源映射里，就只可能来自语义检索。
- **阶段四**：以仓库自己的 Dockerfile 构建的容器 + 真实 WordPress 9 站点（Application Password）、真实 SMTP 接收端（Mailpit）与真实 Hexo 草稿目录跑通：超窗未执行 → 转 Expired 且不发布任何内容并发出提醒；窗口内由调度器自动上传为**私人草稿**；手动公开后文章在站点上确实为公开；重复导出**更新同一篇文章**而不是新建；在站点侧改动文章后 `check-remote` 能识别差异，`pull` 把远端正文取回为受保护的新版本；Markdown 导出落在真实的 Hexo `source/_drafts` 且 front matter 可被 Hexo 解析；以及权限边界——设备令牌不能开启自动公开，密码错误时开关仍然关闭。
- **阶段五**：同样以仓库自己的 Dockerfile 构建的容器，在测试主机上跑完整验收（**88/88 通过**）与 §15.2 八步恢复验证（**21/21 通过**）：采集 → 幂等重放不产生第二条 → 桩端点转写 → 主题自动归属且不发明主题 → Embedding 索引可用 → 取回录音逐字节一致且可解码 → 生成（历史素材被引用为 `isHistorical`、`drift=exact`）→ 无来源陈述标出那一句 → 未确认存疑句时确认被拒 → 手工编辑后未接受覆盖时轮换被拒、接受后轮换并保留前两版 → 确认 → 待确认邮件到达 Mailpit 且不含草稿文字 → 真实 WordPress 草稿、手动公开、`check-remote` 一致、站点上仅此一篇 → Hexo Markdown 落盘 → 可读导出（含音频与保留策略声明、不含 Secret）→ 完整备份（哈希一致、剥离设备令牌与密钥环、按配置裁剪）→ 音频清理（转写保留、`hasAudio=false`）→ 调试模式与五项测试连接 → 在**全新空目录 + 新 Secrets + 自带命名卷**的实例上恢复同一份备份：自动迁移与管理员初始化、上传校验、重启应用、健康全绿、队列无 `Running`、三版本与来源映射完整、录音逐字节一致、原始转写与修订稿都在、整卷扫描无设备令牌、旧令牌 401 且重新配对后可继续采集。中间产出的四处修正如上。
- 同一轮的全量自动化测试：**379 项通过**（Domain 208、Application 16、Client 30、Infrastructure 95、Api.Integration 30）。


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

覆盖范围：§17.1 列出的领域规则；迁移与仓储的原子性（含配对码只能被兑换一次的并发用例、一个真实的外键顺序回归，以及文件存储的「先落盘后引用」）；用例层；客户端离线队列（含「本地副本只在服务端确认后才删除」的顺序断言与幂等键复用）；以及走真实 HTTP 的端到端流程——阶段一的完整流程，阶段二的上传 → 执行器认领 → 转写端点 → 结果落库，阶段三的采集 → 主题识别 → 语义检索 → 生成 → 来源映射 → 无来源陈述检查 → 确认，阶段四的发布排队 / 执行 / 远程差异，以及阶段五的取回录音（含 Range 与未配对者被拒）、导出 / 备份 / 裁剪 / 恢复校验 / 音频清理。外部服务一律使用可控桩端点。

此外还有一层**不在 `dotnet test` 之内**的验收脚本：它对着真实 WordPress、真实 SMTP 接收端和真实 Docker 部署跑 §17.3 与 §15.2，见 [`tools/acceptance/`](tools/acceptance/README.md)。

## 构建 Android 客户端

需要 `maui-android` 工作负载与 Android SDK：

```bash
dotnet workload install maui-android
dotnet build src/DailyMusings.Client -f net10.0-android -c Release -t:SignAndroidPackage
# 产物：src/DailyMusings.Client/bin/Release/net10.0-android/dev.dailymusings.client-Signed.apk
```

必须是 **Release**：Debug 版依赖 Fast Deployment，APK 里不含程序集，单独安装会以「No assemblies found」启动失败。

## 构建 Windows 客户端

需要 `maui-windows` 工作负载：

```powershell
# 免安装包（自包含，解压即用）
dotnet publish src\DailyMusings.Client -f net10.0-windows10.0.19041.0 -c Release `
  -p:WindowsPackageType=None -p:SelfContained=true -p:UseMonoRuntime=false `
  -p:WindowsAppSDKSelfContained=true -p:RuntimeIdentifier=win-x64 -o artifacts\windows

# MSIX 安装包（用自签名证书签名；证书指纹与导入方式见 docs/发布校验值.md）
dotnet publish src\DailyMusings.Client -f net10.0-windows10.0.19041.0 -c Release `
  -p:WindowsPackageType=MSIX -p:GenerateAppxPackageOnBuild=true `
  -p:PackageCertificateThumbprint=<指纹>
```

`-p:UseMonoRuntime=false` 不能省：`SelfContained=true` 会沿 MAUI 的默认值去还原并不存在的 `Microsoft.NETCore.App.Runtime.Mono.win-x64`，还原会直接失败。1.0.0 的产物大小与 SHA-256 见 [`docs/发布校验值.md`](docs/发布校验值.md)。

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
tools/      acceptance/：§17.3 端到端验收与 §15.2 八步恢复验证的可重复执行脚本
deploy/     Dockerfile、compose.yaml、.env.example、secrets.example/
docs/       开发指导.md（唯一事实源，含附录 A 的设计定案记录）、发布校验值.md
```

## 备份、恢复与运维

- 管理页的运维页可以：导出可读包（Markdown + JSON + 尚未被清理的音频）、一键备份、上传备份包恢复、手动跑一次音频清理、查看与重建语义索引、对转写 / 生成 / Embedding / SMTP / WordPress 做「测试连接」，以及开启**有上限、会自到期**的临时调试模式（开启前必须确认「可能把私人内容写进日志」）。
- 恢复是**暂存 + 重启生效**：上传后先校验（schema 版本、zip-slip、是否含设备令牌、是否来自更新版本），写入 `restore-pending.json`，下次启动在迁移之前应用，原数据库保留为 `pre-restore-<时间戳>.db`。恢复后**所有设备都需要重新配对**（备份里没有设备令牌），这是 A.13 的定案。
- 备份包含数据库、媒体、Markdown、索引元数据与非秘密配置；**不含**设备令牌及其派生会话状态，也**不含** DataProtection 密钥环（A.14）。默认保留最近七份。
- 音频保留策略在运维页 / 内容设置里配置：默认草稿确认后保留 30 天，可设为保留更久、永久或立即删除；清理只删音频文件，转写与输入记录保留。
- 升级按 §15.3：先备份，再换镜像；向前迁移在启动时自动执行，不承诺自动降级。

## 文档

[`docs/开发指导.md`](docs/开发指导.md) —— 产品约束、领域模型、时间与生成规则、API 边界、测试策略与完成定义。它是本项目的唯一事实源；任何超出其范围的新需求都必须先改这份文档。

[`docs/发布校验值.md`](docs/发布校验值.md) —— 1.0.0 的 Android APK / Windows MSIX / Windows 免安装包的 SHA-256、复现构建命令与安装说明。

[`tools/acceptance/README.md`](tools/acceptance/README.md) —— 端到端验收与恢复验证脚本的用法、它逐条验的是什么，以及最近一次执行的结果。
