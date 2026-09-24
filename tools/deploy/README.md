# tools/deploy —— 部署、自检与手机验收

这些脚本不是产品的一部分，是「改完之后我凭什么说它能用」的那一半。产品代码之外的所有重复劳动都在这里，
因为上一轮的教训是：**在真实部署的实例上跑一遍，才能发现只在部署里出现的问题**（Secret 文件权限让整条模型
链路静默失效、`_framework/blazor.web.js` 没发布导致所有按钮失效——两条都不是单元测试能抓到的）。

| 脚本 | 干什么 |
| --- | --- |
| `deploy.ps1` | 把当前工作树部署到远程 Docker 主机 |
| `configure-instance.ps1` | 把刚部署好的实例配成「可以验收」的状态（改密、指模型端点与 SMTP、写内容设置、发配对码） |
| `verify-instance.ps1` | 对部署好的实例跑 25 项端到端自检 |
| `test-doubles.sh` | 在实例自己的 Docker 网络上起模型桩与邮件接收端 |
| `android-ui.ps1` | 用 adb 驱动 Android 界面（dump / 找节点 / 点 / 输入 / 截图） |

## 部署

```powershell
pwsh -File tools/deploy/deploy.ps1 -Server 100.64.0.3 -Port 18321
```

它做的事，顺序是有意为之：

1. 打包工作树（排除 `.git`、构建产物、本地状态、`deploy/secrets`——那是真实密钥，不进包）；
2. 把服务器上已有的 `deploy/secrets` 先拷出来；
3. **停容器 → 删掉旧镜像 → 重新构建 → 启动新镜像**（用户明确要求的部署纪律：不允许旧镜像与新镜像并存）；
4. 等 `/api/system/health` 变绿，清理构建留下的悬空镜像，最后 `exit 0`（健康检查才是成败判据，
   不能让 `set -o pipefail` 把最后一条管道的退出码当成部署失败）。

密钥在打包时被排除、部署时从备份恢复，所以 `deploy/secrets/` 不会因为一次部署而丢失或泄漏。

## 部署后自检

```powershell
pwsh -File tools/deploy/verify-instance.ps1 -BaseUrl http://<主机>:18321 -AdminUser admin -AdminPassword '<密码>'
```

覆盖的是这一轮改动最容易悄悄坏掉的地方：录音上传**在同一次响应里**带回识别文本、音频确实落在实例上、
主题的新建 / 删除保护（`topic.in_use`）/ 迁移后删除、稿件与随想分页、统计字段齐全、**真发一封测试邮件**、
日志接口、内容保留设置。任何一项失败都会以非零退出码结束。

给这个脚本改代码时注意两个 PowerShell 5.1 的坑（脚本头部也写了）：中文 JSON 必须发 UTF-8 字节
（直接发字符串会按 ANSI 编码，中文变成 `????`），`Invoke-WebRequest` 必须带 `-UseBasicParsing`
（否则非交互会话里它会去提示 IE 首次运行配置然后失败）。

## 测试替身

```bash
bash tools/deploy/test-doubles.sh up     # 在服务器上跑
bash tools/deploy/test-doubles.sh down
```

起两个容器并挂到 `dailymusings_default` 网络上：

- `dm-stub`（`http://dm-stub:8077/v1`）：OpenAI 兼容的模型桩，转写、生成、Embedding 都指它。
  转写映射放 `/tmp/dm-transcripts.json`，`{"*": "..."}` 表示「任何录音都得到这段文字」——录音是手机上
  现场选的，没法预先登记。生成会把提示词里「已有主题」清单的第一个抄回来当作选中的主题；清单为空时
  返回一个 `newTopics`，所以「复用已有主题」和「新建主题」两条路都能跑到。
- `dm-mailpit`（SMTP 在 `dm-mailpit:1025`，界面 `http://127.0.0.1:8025/`）：收信端。

§17.2 允许外部服务使用可控替身，所以用它们验收是合规的；但**验收完成后不要在实例里把它们留成长期配置**。

## 手机端验收

```powershell
pwsh -File tools/deploy/android-ui.ps1 -Dump                 # 打印当前界面的可见节点
pwsh -File tools/deploy/android-ui.ps1 -Nodes "记录随想"      # 找节点
pwsh -File tools/deploy/android-ui.ps1 -Tap "上传本录音"      # 按文字点
pwsh -File tools/deploy/android-ui.ps1 -TapXY "360,358"      # 按坐标点（大圆圈这种没有文字的）
pwsh -File tools/deploy/android-ui.ps1 -Text "note"          # 输入（含标点前先切 ASCII 输入法）
pwsh -File tools/deploy/android-ui.ps1 -Screenshot shot.png
```

默认目标是本机 MuMu 模拟器的 `127.0.0.1:16384`（先 `adb connect 127.0.0.1:16384`）。

踩过的坑（脚本注释里也有）：

- 界面有计时器 / 动画在刷新时，`uiautomator dump` 会等不到 idle 而超时；`dump` 返回的**可能是上一次**的层级，
  所以不能用「点完立刻 dump」判断点击是否生效——用侧信道证据（服务端收到的条目、logcat、文件）。
- 键盘弹出时界面会上移，按坐标点之前先 `-Dump` 取当前 bounds，或先收起键盘再点。
- 中文输入法会把 `adb shell input text` 的标点转成全角；MuMu 禁用了 `ime set`，所以含标点的文本
  （例如 URL）要用 `input text` 逐字符输并核对结果，别假设它一定对。
- `adb shell input text` 遇到空格会截断，多词文本要自己转义或改成一个词。
