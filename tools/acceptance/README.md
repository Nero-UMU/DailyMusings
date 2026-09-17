# 阶段五验收脚本

这套脚本是 §17.3「端到端验收」与 §15.2「八步恢复验证」的可重复执行版本。它不属于产品产物：它在**真实服务**上跑，模型端点用桩是因为测试主机上没有可用的模型服务。

## 需要什么

- `python3`（3.9 以上；用到 `zoneinfo`、`wave`、`sqlite3`）、`bash`、`curl`、`docker` + `docker compose`。
- 一个一次性的 **WordPress** 站点（REST API 可用，并有一枚 Application Password）、一个 **SMTP 接收端**（脚本按 Mailpit 的 API 读信）。
- 仓库源码放在 `<root>/repo`（验收会用它自己的 Dockerfile 构建服务端镜像），实例与工作目录与 `repo` 平级：

  ```
  <root>/repo/          仓库源码（docker build 的上下文；不要让它包含实例状态）
  <root>/verify/        本目录（脚本；可放任意位置，`DM_ROOT` 可覆盖 <root>）
  <root>/inst-a/        验收实例（bind mount 的 state / keys / secrets）
  <root>/inst-b/        §15.2 的全新实例（用 `deploy/compose.yaml` 与它自己的命名卷）
  <root>/work/          证据、日志、报告、待恢复的备份包
  ```

- 环境变量：

  | 变量 | 用途 |
  | --- | --- |
  | `DM_WP_APP_PASSWORD` | 一次性 WordPress 站点的 Application Password（**必填**，凭据不入库） |
  | `DM_ROOT` | 覆盖 `<root>`（默认取本目录的父目录） |
  | `DM_BASE` / `DM_MAILPIT` / `DM_WP` | 默认 `http://127.0.0.1:8080` / `:8025` / `:8090` |
  | `DM_IMAGE` | 服务端镜像名，默认 `dailymusings/server:local` |
  | `DM_VOLUME` | 全新实例的状态卷名，默认 `dailymusings_dailymusings-state` |

- `DM_WP_USER`（默认 `owner`）与其余账号默认值见各脚本顶部；`compose.verify.yaml` 里写死了测试主机上的桩模型端点（`127.0.0.1:8077/v1`）、Mailpit（`1025`）与 WordPress（`8090`）。

## 怎么跑

```bash
docker build -f <root>/repo/deploy/Dockerfile -t dailymusings/server:local <root>/repo
DM_WP_APP_PASSWORD=... bash <root>/verify/run-phase5-verification.sh
```

`run-phase5-verification.sh` 先跑验收（`run-acceptance.sh`），通过后再跑恢复验证（`run-restore-verify.sh`）。两者都会把 `PASS/FAIL` 与判定依据逐条打印，并写 JSON 报告：

- `work/acceptance.log` / `work/acceptance-report.json` / `work/acceptance-evidence.json`
- `work/restore-stage.log`、`work/restore-verify.log` / `work/restore-report.json`
- `work/restore-source.zip`：验收过程中**在音频清理之前**取的那份备份，恢复验证用的就是它（否则 §15.2 第 6 步「语音可播放」无从检验）

任一检查失败即非零退出，且验收失败时不会继续跑恢复验证。

## 它验的是什么

`acceptance.py`（88 项）覆盖 §17.3 第 3~6 步与阶段五的运维面：

- 采集 → 上传（含幂等键重放不产生第二条）→ 桩端点转写 → 主题自动归属（并断言**不发明主题**：没提到主题词的条目保持未归类）→ Embedding 索引可用；
- `GET /api/inputs/{id}/audio` 取回的录音与上传字节逐字节相同且可被 `wave` 解码；
- 手动生成 → 历史素材作为来源被引用（`isHistorical`，`drift=exact`）→ 无来源陈述检查标出那一句 → 未确认存疑句时确认被拒 → 手工编辑 → 未接受覆盖时轮换被拒（版本与文本原地不动）→ 接受覆盖后轮换并保留前两版 → 确认；
- 待确认邮件到达 Mailpit，且正文只含元数据、不含草稿文字（§12）；
- 发布到真实 WordPress（草稿 → 手动公开 → `check-remote` 一致 → 站点上确实只有这一篇）与 Hexo Markdown（front matter 与正文落盘）；
- §15.1 可读导出（Markdown / JSON / 尚未清理的音频 / 清单声明保留策略 / 不含 Secret）；
- §15.2 备份（哈希与落盘一致、条数、剥离设备令牌与密钥环、保留份数按配置裁剪、非法包被拒）；
- A.1 音频清理（保留 0 天 → 删除 3 个音频、转写保留、`hasAudio=false`、其后导出不再含音频）；
- §16 调试模式（未确认风险拒开、上限 4 小时、到期自失效、可关闭）、五项「测试连接」、索引重建。

`restore_verify.py`（21 项）按 §15.2 八步：全新空目录 + 新 Secrets → `docker compose up -d` 自动迁移与管理员初始化 → 上传备份并暂存 → 重启后生效（保留 `pre-restore-*.db`）→ 健康全绿且队列无 `Running` → 草稿可读、三版本位置完整、来源映射 `QuoteHash` 匹配、历史引用仍在 → 录音逐字节一致且可解码、原始转写与修订稿都在 → 整卷扫描不含设备令牌、导出仍完整 → 旧设备令牌 401、重新配对与再次采集成功。

## 2026-09-16 的执行结果

在测试主机（Docker 29.7，无 .NET SDK，服务端镜像由仓库自己的 `deploy/Dockerfile` 构建）上：

- 验收 **88/88 通过**；恢复验证 **21/21 通过**。
- 同一轮全量单元与集成测试：379 项通过（Domain 208、Application 16、Client 30、Infrastructure 95、Api.Integration 30）。
- 这一轮里脚本发现并推动了四处产品修正（详见仓库 README 的「阶段五」小节）：未接受覆盖的重新生成会把同一轮的幂等键占住、同名（同一秒）导出与备份会互相覆盖、按文件系统时间裁剪备份可能在同秒并列时删掉最新那份、备份快照删除设备令牌后**文件里仍留有该哈希的字节**。

脚本无法覆盖、因此没有声称验证过的部分：Windows 客户端的交互式界面（只做了可执行文件启动冒烟），以及客户端界面上不存在的功能（§9.3 的主题页 / 日历页 / 草稿页未实现，所以 §17.3 第 4 步只能在 API 层验证）。

## 真机与双端验收（另行进行，不属于本目录）

2026-09-17 先做了 Android 真机（Xiaomi 23127PN0CC / Android 16）的 §17.3 第 1~2 步，再做了手机 + Windows 客户端**同一个服务端**的双端验收：

- 装 Release APK → 设置页填地址（HTTP 会先要求确认风险）→ 测试连接 → 用管理页的十分钟配对码配对 → 切断与服务器的通路（先摘 adb 反向隧道、关蜂窝数据，设备侧 `curl` 得到 code=000）→ 录两段语音（4.1 秒 / 2.1 秒）并写一条文字 → 界面显示「3 条失败（连不上服务器）」且服务端条目数不变 → 恢复通路后点「立即同步」→「已上传 3 条」，服务端 5 条（4 语音 + 1 文字）、转写与主题归属齐全、磁盘 4 个真实 m4a。
- 之后手机改用 **Tailscale** 地址走真实网络路径，电脑端跑未打包的 Windows 客户端（用 UI Automation 驱动，等同人手点击）：两端都配对、都看到同一批条目；电脑端成功用 PC 麦克风录了 9.7 秒并上传转写；在电脑端 `tailscale down` 真正切断路径后，手机录的 5.0 秒被如实标为「上传失败（连不上服务器）」，恢复后重试一次成功且**没有产生重复条目**；最终两端都显示「共 8 条」。

这几轮抓到四个只有真机/双端才暴露的客户端缺陷（详见仓库 README 的「阶段五」小节）：服务器不可达时今日页崩溃、未分类的上传异常逃逸且队列状态不同步、未打包的 Windows 版用 `ApplicationData.Current` 取临时目录导致录音失败、平台名写死成 `android`。修复后共 386 项测试通过。

三个限制值得记下：`adb shell input text` 在这台设备上**无法注入 CJK**（`InputShellCommand.sendText` 抛 NPE），所以设备上那几条文字是 ASCII 写下的；小米自带搜狗输入法会把 ASCII 标点转成全角（`:`→`：`、`/`→`、`、`.`→`。`），输入 URL 前必须 `ime set` 到 ASCII 输入法（本仓库用 `io.appium.settings/.UnicodeIME`）；Tailscale 处于 DERP 中继时 RTT 可达数百毫秒且会短时丢包，因此重试与幂等键的行为是被真实网络逼出来的，而不是构造出来的。
