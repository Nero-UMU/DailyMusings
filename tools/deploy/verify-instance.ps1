# 部署后的功能自检（不是产品的一部分，是「改完之后我凭什么说它能用」）。
#
# 覆盖 2026-09-24 这一轮改造新增/变更的后端能力：
#   录音上传 → 服务器落盘 → 同步识别 → 文本随响应回传；
#   统计接口；主题列表/使用情况/删除保护/文章主题迁移；
#   稿件分页；SMTP 真实测试邮件；日志查看；内容保留设置。
#
# 用法：
#   pwsh -File tools\deploy\verify-instance.ps1 -BaseUrl http://100.64.0.3:18321 -AdminUser admin -AdminPassword 'xxx'
#
# 两个 PowerShell 5.1 的坑，这里都已经绕开，改这个脚本时别踩回去：
#   * 用 `-Body <string>` 发中文 JSON 会按 ANSI 编码，中文变成 "????" —— 一律发 UTF-8 字节。
#   * `Invoke-WebRequest` 不带 `-UseBasicParsing` 会在非交互会话里尝试提示 IE 首次运行配置并直接失败。
param(
    [string]$BaseUrl = "http://100.64.0.3:18321",
    [string]$AdminUser = "admin",
    [string]$AdminPassword = "",
    [string]$MailTo = "operator@example.test"
)

$ErrorActionPreference = "Stop"
$BaseUrl = $BaseUrl.TrimEnd('/')
$script:Failures = @()
$script:Checks = 0

function Check {
    param([string]$Name, [scriptblock]$Body)
    $script:Checks++
    try {
        $result = & $Body
        Write-Host ("  [PASS] {0}{1}" -f $Name, $(if ($result) { " -> $result" } else { "" })) -ForegroundColor Green
        return $result
    }
    catch {
        $script:Failures += "$Name : $($_.Exception.Message)"
        Write-Host ("  [FAIL] {0} -> {1}" -f $Name, $_.Exception.Message) -ForegroundColor Red
        return $null
    }
}

$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession

function Send-Api {
    param(
        [string]$Method,
        [string]$Path,
        $Body = $null,
        [hashtable]$Headers = $null,
        [switch]$WithSession
    )

    $params = @{
        Uri         = "$BaseUrl$Path"
        Method      = $Method
        TimeoutSec  = 90
        ContentType = "application/json; charset=utf-8"
    }

    if ($WithSession) { $params.WebSession = $session }
    if ($Headers) { $params.Headers = $Headers }

    if ($null -ne $Body) {
        $params.Body = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 6))
    }

    return Invoke-RestMethod @params
}

function New-Wav {
    param([string]$Path, [int]$Milliseconds = 800)

    $sampleRate = 16000
    $samples = [int]($sampleRate * $Milliseconds / 1000)
    $dataBytes = $samples * 2

    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($stream)
    $writer.Write([Text.Encoding]::ASCII.GetBytes("RIFF"))
    $writer.Write([int](36 + $dataBytes))
    $writer.Write([Text.Encoding]::ASCII.GetBytes("WAVEfmt "))
    $writer.Write([int]16); $writer.Write([int16]1); $writer.Write([int16]1)
    $writer.Write([int]$sampleRate); $writer.Write([int]($sampleRate * 2))
    $writer.Write([int16]2); $writer.Write([int16]16)
    $writer.Write([Text.Encoding]::ASCII.GetBytes("data"))
    $writer.Write([int]$dataBytes)
    for ($i = 0; $i -lt $samples; $i++) { $writer.Write([int16]0) }
    $writer.Flush()
    [IO.File]::WriteAllBytes($Path, $stream.ToArray())
    $writer.Dispose(); $stream.Dispose()
}

Write-Host "== 目标实例 $BaseUrl ==" -ForegroundColor Cyan

$health = Check "健康检查" { Invoke-RestMethod -Uri "$BaseUrl/api/system/health" -TimeoutSec 20 }
if ($null -eq $health) { Write-Host "实例不可达，后面的检查没有意义。" -ForegroundColor Red; exit 1 }
Write-Host ("      healthy={0}，探针 {1} 项" -f $health.healthy, @($health.probes).Count)

# 登录是表单 POST（不是 JSON），明文 HTTP 还要带 acknowledgeRisk=yes。
Check "管理员登录" {
    $response = Invoke-WebRequest -Uri "$BaseUrl/api/admin/sign-in" -Method Post -WebSession $session `
        -TimeoutSec 30 -UseBasicParsing `
        -Body @{ username = $AdminUser; password = $AdminPassword; acknowledgeRisk = "yes" }

    $final = $response.BaseResponse.ResponseUri.AbsolutePath
    if ($final -like "/login*") { throw "登录被拒（最终落在 $final）：账号或密码不对" }
    if ($final -eq "/change-credentials") { throw "这个账号还处在强制改密状态，请先改密再跑本脚本" }
    "已登录"
} | Out-Null

$statistics = Send-Api GET "/api/system/statistics" -WithSession

Check "统计接口字段齐全" {
    $missing = @()
    foreach ($field in @(
            "today", "todayInputCount", "todayVoiceCount", "todayTextCount", "todayReflectionStatus",
            "totalInputCount", "totalReflectionCount", "confirmedReflectionCount", "draftReflectionCount",
            "publishedCount", "pendingPublicationCount", "activeDeviceCount", "revokedDeviceCount",
            "topicCount", "queuePending", "queueRunning", "queueFailed",
            "audioRetentionDays", "contentRetentionDays", "semanticSearchAvailable",
            "generationEnabled", "smtpConfigured", "mediaBytes", "databaseBytes")) {
        if ($null -eq $statistics.PSObject.Properties[$field]) { $missing += $field }
    }
    if ($missing.Count -gt 0) { throw "缺少字段：$($missing -join ', ')" }
    "今日随想={0}（录音 {1} / 手写 {2}）稿件={3} 已发布={4} 主题={5} 设备={6}" -f `
        $statistics.todayInputCount, $statistics.todayVoiceCount, $statistics.todayTextCount, `
        $statistics.totalReflectionCount, $statistics.publishedCount, $statistics.topicCount, `
        $statistics.activeDeviceCount
}

Check "主题列表带来源与用量" {
    $topics = Send-Api GET "/api/topics" -WithSession
    $sample = @($topics.items) | Select-Object -First 1
    if ($sample -and ($null -eq $sample.PSObject.Properties["origin"] -or $null -eq $sample.PSObject.Properties["articleCount"])) {
        throw "TopicDto 缺少 origin/articleCount/inputCount"
    }
    "共 {0} 个主题" -f @($topics.items).Count
} | Out-Null

$scratchTopic = Check "新建主题（中文要能正确落库）" {
    $name = "自检主题-" + (Get-Date -Format "HHmmss")
    $created = Send-Api POST "/api/topics" @{ name = $name } -WithSession
    if ($created.name -ne $name) { throw "落库的名字是「$($created.name)」，不是「$name」——编码有问题" }
    $created.id
}

if ($scratchTopic) {
    Check "删除未被使用的主题（应成功）" {
        Send-Api DELETE "/api/topics/$scratchTopic" -WithSession | Out-Null
        "已删除"
    } | Out-Null
}

Check "生成配对码" {
    $issued = Send-Api POST "/api/pairing/codes" @{} -WithSession
    "code=$($issued.code)"
} | Out-Null

$deviceToken = Check "兑换配对码（模拟手机）" {
    $issued = Send-Api POST "/api/pairing/codes" @{} -WithSession
    $redeemed = Send-Api POST "/api/pairing/redeem" @{
        code = $issued.code; deviceName = "自检设备"; platform = "android"
    }
    $redeemed.token
}

$deviceHeaders = @{ Authorization = "Bearer $deviceToken" }

Check "文字随想上传（设备令牌）" {
    $result = Send-Api POST "/api/inputs/text" @{
        text                 = "自检：这一条是脚本写进来的手写随想。"
        createdAtUtc         = (Get-Date).ToUniversalTime().ToString("o")
        createdOffsetMinutes = 480
        idempotencyKey       = [guid]::NewGuid().ToString("N")
    } -Headers $deviceHeaders
    "id=$($result.input.id)"
} | Out-Null

$wav = Join-Path $env:TEMP "dm-selftest.wav"
New-Wav -Path $wav -Milliseconds 900

Check "录音上传 → 同一次响应就带回识别文本" {
    $curlArgs = @(
        "-sS", "-X", "POST", "$BaseUrl/api/inputs/voice",
        "-H", "Authorization: Bearer $deviceToken",
        "-F", "audio=@$wav;type=audio/wav",
        "-F", "createdAtUtc=$((Get-Date).ToUniversalTime().ToString('o'))",
        "-F", "createdOffsetMinutes=480",
        "-F", "durationMilliseconds=900",
        "-F", "idempotencyKey=$([guid]::NewGuid().ToString('N'))"
    )
    $raw = & curl.exe @curlArgs
    $parsed = $raw | ConvertFrom-Json
    if (-not $parsed.input.transcript) { throw "响应里没有 transcript（识别文本没有随上传回传）" }
    if ($parsed.input.transcriptionStatus -ne "succeeded") { throw "transcriptionStatus=$($parsed.input.transcriptionStatus)" }
    $parsed.input.transcript
}

Check "录音确实落在服务器上（能取回音频、能取回文本）" {
    $list = Send-Api GET "/api/inputs?page=1&pageSize=10" -Headers $deviceHeaders
    $voiceRow = @($list.items) | Where-Object { $_.sourceType -eq "voice" } | Select-Object -First 1
    if (-not $voiceRow) { throw "列表里没有录音条目" }
    if (-not $voiceRow.hasAudio) { throw "条目说没有音频" }
    if (-not $voiceRow.transcript) { throw "条目上没有识别文本" }

    $audio = Invoke-WebRequest -Uri "$BaseUrl/api/inputs/$($voiceRow.id)/audio" -Headers $deviceHeaders `
        -TimeoutSec 60 -UseBasicParsing
    "音频 {0} 字节，状态={1}，文本前 20 字={2}" -f `
        $audio.RawContentLength, $voiceRow.transcriptionStatus, $voiceRow.transcript.Substring(0, [Math]::Min(20, $voiceRow.transcript.Length))
}

Check "随想分页列表（从新到旧）" {
    $page = Send-Api GET "/api/inputs?page=1&pageSize=5" -Headers $deviceHeaders
    if ($null -eq $page.PSObject.Properties["total"]) { throw "响应缺少 total/page/pageSize" }
    $stamps = @($page.items | ForEach-Object { $_.createdAtUtc })
    $sorted = @($stamps | Sort-Object -Descending)
    if ($stamps.Count -gt 1 -and ($stamps -join '|') -ne ($sorted -join '|')) { throw "没有从新到旧排序" }
    "total=$($page.total) page=$($page.page) size=$($page.pageSize) 返回=$($page.items.Count)"
} | Out-Null

$today = (Get-Date).ToString("yyyy-MM-dd")

Check "立即生成今日随想博客" {
    $result = Send-Api POST "/api/reflections/$today/generate" @{
        ignoreTranscriptionFailures = $true; allowOverwriteOfManualEdits = $true
    } -WithSession
    "queued=$($result.queued) code=$($result.code)"
} | Out-Null

Start-Sleep -Seconds 10

$reflection = Check "今日稿件已生成，并带上了模型选的主题" {
    $result = Send-Api GET "/api/reflections/$today" -WithSession
    $names = @($result.workingVersion.topics | ForEach-Object { $_.name })
    if ($result.status -eq "pendingInputs" -or $result.status -eq "ready") { throw "稿件还没生成：status=$($result.status)" }
    if ($names.Count -eq 0) { throw "稿件没有主题（模型桩应当给出至少一个）" }
    "status=$($result.status) 主题=$($names -join ',')"
} | Out-Null

# 注意：Check 返回的是它打印的那行文字，不是请求对象 —— 下面的守卫检查要的是对象，所以重新取一次。
$reflection = Send-Api GET "/api/reflections/$today" -WithSession

$usedTopic = $null
if ($reflection -and $reflection.workingVersion -and @($reflection.workingVersion.topics).Count -gt 0) {
    $usedTopic = @($reflection.workingVersion.topics)[0].id

    Check "删除被文章使用的主题（应被拒绝，409 topic.in_use）" {
        try {
            Send-Api DELETE "/api/topics/$usedTopic" -WithSession | Out-Null
            throw "居然删成功了 —— 删除保护没有生效"
        }
        catch [System.Net.WebException] {
            $stream = $_.Exception.Response.GetResponseStream()
            $reader = New-Object System.IO.StreamReader($stream)
            $parsed = $reader.ReadToEnd() | ConvertFrom-Json
            if ($parsed.code -ne "topic.in_use") { throw "错误码不是 topic.in_use，而是 $($parsed.code)" }
            "被拒绝：$($parsed.code)"
        }
    } | Out-Null

    Check "主题使用情况接口（与删除守卫同口径）" {
        $usage = Send-Api GET "/api/topics/$usedTopic/usage" -WithSession
        if (@($usage.articles).Count -eq 0) { throw "usage 说没有文章在用，但删除守卫拒绝了" }
        "文章 {0} 篇，随想 {1} 条" -f @($usage.articles).Count, $usage.inputCount
    } | Out-Null

    $spare = Check "再建一个主题用于迁移" {
        (Send-Api POST "/api/topics" @{ name = "迁移目标-" + (Get-Date -Format "HHmmss") } -WithSession).id
    }

    if ($spare) {
        Check "把文章迁移到别的主题" {
            Send-Api PATCH "/api/reflections/$today/topics" @{
                primaryTopicId = $spare; secondaryTopicIds = @()
            } -WithSession | Out-Null
            "已迁移"
        } | Out-Null

        Check "迁移之后原主题可以删除" {
            # 只删原主题：迁移目标此刻正被那篇文章使用，删它当然应该失败 —— 那正是上一条检查验的事。
            Send-Api DELETE "/api/topics/$usedTopic" -WithSession | Out-Null
            "已删除"
        } | Out-Null
    }
}
else {
    Write-Host "  [SKIP] 主题删除保护：今天的稿件没有主题" -ForegroundColor Yellow
}

Check "稿件分页列表" {
    $page = Send-Api GET "/api/reflections?page=1&pageSize=5" -WithSession
    if ($null -eq $page.PSObject.Properties["total"]) { throw "响应缺少 total/page/pageSize" }
    "total=$($page.total) 返回=$($page.items.Count)"
} | Out-Null

Check "发送测试邮件（真实投递到 SMTP）" {
    $result = Send-Api POST "/api/system/smtp-settings/test" @{ toAddress = $MailTo } -WithSession
    if (-not $result.sent) { throw "没有发出去：$($result.code) $($result.detail)" }
    "sent=true"
}

Check "SMTP 视图带收件地址与密码来源" {
    $smtp = Send-Api GET "/api/system/smtp-settings" -WithSession
    if ($null -eq $smtp.PSObject.Properties["toAddress"]) { throw "缺少 toAddress" }
    if ($null -eq $smtp.PSObject.Properties["passwordSource"]) { throw "缺少 passwordSource" }
    "to={0} passwordSource={1} hasPassword={2}" -f $smtp.toAddress, $smtp.passwordSource, $smtp.hasPassword
}

Check "日志接口" {
    $logs = Send-Api GET "/api/system/logs?lines=20" -WithSession
    "返回 {0} 条，最低级别={1}" -f @($logs.items).Count, $logs.minimumLevel
} | Out-Null

Check "内容保留设置可见" {
    $settings = Send-Api GET "/api/content-settings" -WithSession
    "audioDays=$($settings.audioRetentionDays) contentDays=$($settings.contentRetentionDays)"
} | Out-Null

Check "内容清理接口可入队" {
    Send-Api POST "/api/maintenance/content-cleanup/run" @{} -WithSession | Out-Null
    "已入队"
} | Out-Null

Remove-Item $wav -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host ("== {0} 项检查，{1} 项失败 ==" -f $script:Checks, $script:Failures.Count) -ForegroundColor Cyan
foreach ($failure in $script:Failures) { Write-Host "  - $failure" -ForegroundColor Red }
if ($script:Failures.Count -gt 0) { exit 1 }
