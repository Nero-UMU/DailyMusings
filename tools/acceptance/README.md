# 阶段五验收脚本

这套脚本是 §17.3「端到端验收」与 §15.2「八步恢复验证」的可重复执行版本。它不属于产品产物：它在**真实服务**上跑，模型端点用桩是因为测试主机上没有可用的模型服务。

> **2026-09-24 已按新的项目方向改写**（见 [`docs/开发指导.md`](../../docs/开发指导.md) 附录 A.17）：删掉了真实 WordPress 环节与 `DM_WP_*` 相关参数，改成对**导出目录里的 Markdown 文件**做完整断言（草稿 → 公开 → 手工改动 → 差异与处置）。下面「执行结果」一节记录的两次成功执行都发生在改写**之前**，属于旧范围，因此它们**不能**用来证明当前范围的验收已通过——**按新范围的重跑尚未进行**。

## 需要什么

- `python3`（3.9 以上；用到 `zoneinfo`、`wave`、`sqlite3`）、`bash`、`curl`、`docker` + `docker compose`。
- 一个 **SMTP 接收端**（脚本按 Mailpit 的 API 读信）。**不再需要 WordPress**。
- 仓库源码放在 `<root>/repo`（验收会用它自己的 Dockerfile 构建服务端镜像），实例与工作目录与 `repo` 平级：

  ```
  <root>/repo/          仓库源码（docker build 的上下文；不要让它包含实例状态）
  <root>/verify/        本目录（脚本；可放任意位置，`DM_ROOT` 可覆盖 <root>）
  <root>/inst-a/        验收实例（bind mount 的 state / keys / secrets）
  <root>/inst-b/        §15.2 的全新实例（用仓库根的 `compose.yaml` 与它自己的命名卷 `dm-data` / `dm-config`）
  <root>/work/          证据、日志、报告、待恢复的备份包
  ```

- 环境变量：

  | 变量 | 用途 |
  | --- | --- |
  | `DM_ROOT` | 覆盖 `<root>`（默认取本目录的父目录） |
  | `DM_BASE` / `DM_MAILPIT` | 默认 `http://127.0.0.1:18321` / `:8025` |
  | `DM_IMAGE` | 服务端镜像名，默认 `dailymusings/server:local` |
  | `DM_VOLUME` | 全新实例的状态卷名，默认 `dailymusings_dailymusings-state`（2026-09-29 后是 `dailymusings_dm-data`，见 `run-restore-verify.sh`） |

- 其余账号默认值见各脚本顶部；`compose.verify.yaml` 里写死了测试主机上的桩模型端点（`127.0.0.1:8077/v1`）与 Mailpit（`1025`）。

## 怎么跑

```bash
docker build -f <root>/repo/deploy/Dockerfile -t dailymusings/server:local <root>/repo
bash <root>/verify/run-phase5-verification.sh
```

单独跑验收是 `bash <root>/verify/run-acceptance.sh`；`run-phase5-verification.sh` 先跑验收，通过后再跑恢复验证。两者都会把 `PASS/FAIL` 与判定依据逐条打印，并写 JSON 报告：

- `work/acceptance.log` / `work/acceptance-report.json` / `work/acceptance-evidence.json`
- `work/restore-stage.log`、`work/restore-verify.log` / `work/restore-report.json`
- `work/restore-source.zip`：验收过程中**在音频清理之前**取的那份备份，恢复验证用的就是它（否则 §15.2 第 6 步「语音可播放」无从检验）

任一检查失败即非零退出，且验收失败时不会继续跑恢复验证。

## 它验的是什么

`acceptance.py` 覆盖 §17.3 第 3~6 步与阶段五的运维面：

- 采集 → 上传（含幂等键重放不产生第二条）→ 桩端点转写 → 主题自动归属（并断言**不发明主题**：没提到主题词的条目保持未归类）→ Embedding 索引可用；
- `GET /api/inputs/{id}/audio` 取回的录音与上传字节逐字节相同且可被 `wave` 解码；
- 手动生成 → 历史素材作为来源被引用（`isHistorical`，`drift=exact`）→ 无来源陈述检查标出那一句 → 未确认存疑句时确认被拒 → 手工编辑 → 未接受覆盖时轮换被拒（版本与文本原地不动）→ 接受覆盖后轮换并保留前两版 → 确认；
- 待确认邮件到达 Mailpit，且正文只含元数据、不含草稿文字（§12）；
- **Markdown 导出（§17.3 第 6 步）**：只接受 `type: "markdown"` 的目标（用 `type: "wordPress"` 建目标必须被 400 拒绝）；导出为草稿后文件落在实例的 markdown 根目录下、带 front matter 与正文、且 `draft: true`；同一版本再次导出为公开是**同一记录的下一轮**（`exportRound` 递增），文件被改写成 `draft: false`，状态到 `published`；`check-remote` 对刚写出的文件报一致；在导出目录里手工改写那份文件后 `check-remote` 报出差异，`resolve` 的 `keepBoth` 被接受（随后把文件还原，以免影响后续检查）；
- §15.1 可读导出（Markdown / JSON / 尚未清理的音频 / 清单声明保留策略 / 不含 Secret）；
- §15.2 备份（哈希与落盘一致、条数、剥离设备令牌与密钥环、保留份数按配置裁剪、非法包被拒）；
- A.1 音频清理（保留 0 天 → 删除 3 个音频、转写保留、`hasAudio=false`、其后导出不再含音频）；
- §16 调试模式（未确认风险拒开、上限 4 小时、到期自失效、可关闭）、四项「测试连接」、索引重建；
- 通知设置：收件地址与「草稿待确认」开关确实生效（后面的邮件检查靠它），以及**部分更新只改它点名的字段**。

`restore_verify.py` 按 §15.2 八步：全新空目录 + 新 Secrets → `docker compose up -d` 自动迁移与管理员初始化 → 上传备份并暂存 → 重启后生效（保留 `pre-restore-*.db`）→ 健康全绿且队列无 `Running` → 草稿可读、三版本位置完整、来源映射 `QuoteHash` 匹配、历史引用仍在 → 录音逐字节一致且可解码、原始转写与修订稿都在 → 整卷扫描不含设备令牌、导出仍完整 → 旧设备令牌 401、重新配对与再次采集成功。

## 执行结果

### 2026-09-17（**旧范围**，含 WordPress 与 Windows 客户端）

收尾轮（客户端 §9.3 五个页面、管理页 §8.1 配置界面、被中断任务的重启恢复修复）之后，把当时源码整棵复制到测试主机、用仓库自己的 `deploy/Dockerfile` 重新构建镜像，整套跑一遍：

- 验收 **89/89 通过**；§15.2 八步恢复验证 **21/21 通过**。两项都是 `exit=0`。
- 同一轮全量自动化测试：**441 项通过**。
- 这一轮脚本自己修掉一处**脚本缺陷**：§17.3 第 5 步（待确认邮件）在第一次复跑时失败，原因是脚本只写了收件地址、三个事件开关一个都没开，而产品的语义是「没开就不发」（§12 的「沉默是默认」）。判定：产品行为正确，脚本假设错了。修法是 S0 显式打开 `draftReady` 并断言 PATCH 返回的开关为真，同时新增一条「部分更新只改它点名的字段」的检查。

### 2026-09-16（**旧范围**）

在测试主机（Docker 29.7，无 .NET SDK，服务端镜像由仓库自己的 `deploy/Dockerfile` 构建）上：

- 验收 **88/88 通过**；恢复验证 **21/21 通过**。
- 同一轮全量单元与集成测试：379 项通过。
- 这一轮里脚本发现并推动了四处产品修正：未接受覆盖的重新生成会把同一轮的幂等键占住、同名（同一秒）导出与备份会互相覆盖、按文件系统时间裁剪备份可能在同秒并列时删掉最新那份、备份快照删除设备令牌后**文件里仍留有该哈希的字节**。

### 2026-09-24（新范围）

**尚未执行。** 脚本已按 A.17 改写，改动落在：删除 `wp_request` / `wp_reset` / `wp_posts` 与全部 WordPress 判定；不再要求 `DM_WP_APP_PASSWORD`；验收实例的 compose 文件去掉 `Publishing__WordPress__*` 与对应 Secret；S5 整段换成上面写的 Markdown 生命周期，并新增「旧目标类型被拒绝」这条断言。重跑需要一台能跑 Docker 的 Linux 测试主机与一个 Mailpit，因此没有在本机完成；重跑后请把结果补到这一节，并同步更新 [`CHANGELOG.md`](../../CHANGELOG.md) 里的数字。

### 2026-09-29：脚本已适配「一份 compose + 一个镜像」，但**仍未重跑**

附录 A.26 把对外部署形态换成「仓库根的 `compose.yaml` + 已发布镜像 + 入口脚本纠正属主后降权」。相应地改了 `run-restore-verify.sh`：

- 空目录里放的是**仓库根的** `compose.yaml`（不再是 `deploy/compose.yaml` 那个覆盖文件），并用 `DM_IMAGE=dailymusings/server:local` 指回本地构建的镜像——否则它会去 ghcr 拉发布版，验的就不是这份源码。
- 那一步还要「新 Secrets」，所以脚本用 `sed` 打开根文件里注释掉的 `./secrets:/run/secrets:ro` 挂载，并且**打不开就直接失败**（否则会静默跑成「Secrets 根本没挂上」的假通过）。
- 全新实例的状态卷名从 `dailymusings_dailymusings-state` 改为 `dailymusings_dm-data`（项目名仍是 `dailymusings`）。

这些是**结构上的适配，不是执行证据**：本节写的仍然是「未重跑」，跑之前不要引用上面的旧结果。

脚本无法覆盖、因此没有声称验证过的部分：Android 客户端的交互式界面（本目录只到 API 层；界面另有真机验收），以及需要真实模型之外的判断。

## Android 真机验收（另行进行，不属于本目录）

2026-09-17 在 Xiaomi 23127PN0CC / Android 16 上做了 §17.3 第 1~2 步：

- 装 Release APK（`adb install`）→ 设置页填地址（HTTP 会先要求确认风险）→ 测试连接 → 用管理页的十分钟配对码配对 → **切断与服务器的通路**（先摘 adb 反向隧道、关蜂窝数据，设备侧 `curl` 得到 code=000）→ 录两段语音（4.1 秒 / 2.1 秒）并写一条文字 → 界面显示「3 条失败（连不上服务器）」且服务端条目数不变 → 恢复通路后点「立即同步」→「已上传 3 条」，服务端 5 条（4 语音 + 1 文字）、转写与主题归属齐全、磁盘 4 个真实 m4a。

这一轮抓到四个客户端缺陷（详见 [`CHANGELOG.md`](../../CHANGELOG.md) 的「历史记录：验证过的路径」一节）：服务器不可达时今日页崩溃、未分类的上传异常逃逸且队列状态不同步、平台名写死成 `android`，以及一个只在**当时的 Windows 客户端**上出现的录音失败——最后这一条随电脑端一起撤销了。修复后共 386 项测试通过。

三个限制值得记下：`adb shell input text` 在这台设备上**无法注入 CJK**（`InputShellCommand.sendText` 抛 NPE），所以设备上那几条文字是 ASCII 写下的；小米自带搜狗输入法会把 ASCII 标点转成全角（`:`→`：`、`/`→`、`、`.`→`。`），输入 URL 前必须 `ime set` 到 ASCII 输入法（本仓库用 `io.appium.settings/.UnicodeIME`）；Tailscale 处于 DERP 中继时 RTT 可达数百毫秒且会短时丢包，因此重试与幂等键的行为是被真实网络逼出来的，而不是构造出来的。
