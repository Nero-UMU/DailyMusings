# 把一台刚部署好的实例配置成「可以验收」的状态（不是产品的一部分，是运维/验收用的脚本）。
#
# 做四件事：
#   1. 用初始密码登录并完成强制改密（初始密码只在容器启动日志里出现一次）；
#   2. 把三块模型端点指向模型桩、把 SMTP 指向邮件接收端、把通知收件地址与事件开关打开；
#   3. 写内容与时区设置（内容时区、生成/导出时刻、保留策略）；
#   4. 签发一个配对码并打印出来，供手机端使用。
#
# 用法：
#   pwsh -File tools\deploy\configure-instance.ps1 -BaseUrl http://100.64.0.3:18321 `
#        -InitialPassword FGLXRC266SP3N95E54T7RPP9 -NewAdminUser admin -NewAdminPassword 'xxxxx'
param(
    [string]$BaseUrl = "http://100.64.0.3:18321",
    [Parameter(Mandatory = $true)][string]$InitialPassword,
    [string]$NewAdminUser = "admin",
    [Parameter(Mandatory = $true)][string]$NewAdminPassword,
    [string]$ModelBaseUrl = "http://dm-stub:8077/v1",
    [string]$SmtpHost = "dm-mailpit",
    [int]$SmtpPort = 1025,
    [string]$MailTo = "operator@example.test",
    [string]$FromAddress = "dailymusings@example.test",
    [string]$TimeZoneId = "Asia/Shanghai"
)

$ErrorActionPreference = "Stop"
$BaseUrl = $BaseUrl.TrimEnd('/')
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession

function Post-Json {
    param([string]$Path, $Body)
    Invoke-RestMethod -Uri "$BaseUrl$Path" -Method Post -WebSession $session -TimeoutSec 60 `
        -ContentType "application/json" -Body ($Body | ConvertTo-Json -Depth 6)
}

function Patch-Json {
    param([string]$Path, $Body)
    Invoke-RestMethod -Uri "$BaseUrl$Path" -Method Patch -WebSession $session -TimeoutSec 60 `
        -ContentType "application/json" -Body ($Body | ConvertTo-Json -Depth 6)
}

Write-Host "== 1. 登录并改密 ==" -ForegroundColor Cyan
# 登录是表单 POST（不是 JSON）：字段 username / password，明文 HTTP 还要带 acknowledgeRisk=yes。
$signIn = Invoke-WebRequest -Uri "$BaseUrl/api/admin/sign-in" -Method Post -WebSession $session -TimeoutSec 30 -UseBasicParsing `
    -Body @{ username = "admin"; password = $InitialPassword; acknowledgeRisk = "yes" }
Write-Host "  登录响应 $($signIn.StatusCode)，最终地址 $($signIn.BaseResponse.ResponseUri)"

$change = Invoke-WebRequest -Uri "$BaseUrl/api/admin/credentials" -Method Post -WebSession $session -TimeoutSec 30 -UseBasicParsing `
    -Body @{
        currentPassword = $InitialPassword
        newUsername     = $NewAdminUser
        newPassword     = $NewAdminPassword
        confirmPassword = $NewAdminPassword
    }
Write-Host "  改密响应 $($change.StatusCode)，最终地址 $($change.BaseResponse.ResponseUri)"

# 改密会登出，重新登录一次拿到可用的会话。
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
Invoke-WebRequest -Uri "$BaseUrl/api/admin/sign-in" -Method Post -WebSession $session -TimeoutSec 30 -UseBasicParsing `
    -Body @{ username = $NewAdminUser; password = $NewAdminPassword; acknowledgeRisk = "yes" } | Out-Null

$who = Invoke-RestMethod -Uri "$BaseUrl/api/system/instance-settings" -WebSession $session -TimeoutSec 30
Write-Host "  已登录，实例设置读取成功" -ForegroundColor Green

Write-Host "== 2. 模型端点 ==" -ForegroundColor Cyan
Patch-Json "/api/system/model-endpoints/transcription" @{
    enabled = $true; baseUrl = $ModelBaseUrl; model = "stub-whisper"; secretName = "openai-api-key"; timeoutSeconds = 60
} | Out-Null
Patch-Json "/api/system/model-endpoints/generation" @{
    enabled = $true; baseUrl = $ModelBaseUrl; model = "stub-chat"; secretName = "openai-api-key"; timeoutSeconds = 60
} | Out-Null
Patch-Json "/api/system/model-endpoints/embedding" @{
    enabled = $true; baseUrl = $ModelBaseUrl; model = "stub-embed"; secretName = "embedding-api-key";
    timeoutSeconds = 60; dimensions = 64
} | Out-Null
$models = Invoke-RestMethod -Uri "$BaseUrl/api/system/model-endpoints" -WebSession $session -TimeoutSec 30
$models.items | ForEach-Object { Write-Host ("  {0,-14} enabled={1} {2} {3}" -f $_.service, $_.enabled, $_.baseUrl, $_.model) }

Write-Host "== 3. 邮件与通知 ==" -ForegroundColor Cyan
# 密码走后台填写（加密存放在密钥环卷下，不进备份）；这里用邮件接收端，不需要密码，也不需要加密：
# 两个加密开关都关着正好是本机中继的用法（SSL=465 / STARTTLS=587 各自对应一个开关，不能同时勾）。
Patch-Json "/api/system/smtp-settings" @{
    enabled = $true; host = $SmtpHost; port = $SmtpPort; useSsl = $false; useStartTls = $false;
    fromAddress = $FromAddress; toAddress = $MailTo
} | Out-Null
Patch-Json "/api/notification-settings" @{
    toAddress = $MailTo; draftReady = $true; jobFailed = $true; automaticPublication = $true
} | Out-Null
$smtp = Invoke-RestMethod -Uri "$BaseUrl/api/system/smtp-settings" -WebSession $session -TimeoutSec 30
Write-Host ("  smtp enabled={0} {1}:{2} ssl={3} starttls={4} from={5} -> {6} password={7}" -f `
    $smtp.enabled, $smtp.host, $smtp.port, $smtp.useSsl, $smtp.useStartTls, $smtp.fromAddress, `
    $smtp.toAddress, $smtp.passwordSource)

$test = Post-Json "/api/system/smtp-settings/test" @{ toAddress = $MailTo }
Write-Host ("  测试邮件 sent={0} code={1} {2}" -f $test.sent, $test.code, $test.detail)

Write-Host "== 4. 内容与时间 ==" -ForegroundColor Cyan
Patch-Json "/api/content-settings" @{
    timeZoneId = $TimeZoneId; generationLocalTime = "23:00"; publishLocalTime = "08:00";
    publishWindowMinutes = 120; audioRetentionDays = 30; contentRetentionDays = -1
} | Out-Null
$content = Invoke-RestMethod -Uri "$BaseUrl/api/content-settings" -WebSession $session -TimeoutSec 30
Write-Host ("  时区={0} 生成={1} 导出={2} 音频保留={3} 天 内容保留={4}" -f `
    $content.timeZoneId, $content.generationLocalTime, $content.publishLocalTime, `
    $content.audioRetentionDays, $content.contentRetentionDays)

Write-Host "== 5. 配对码 ==" -ForegroundColor Cyan
$code = Post-Json "/api/pairing/codes" @{}
Write-Host ("  配对码：{0}（到期 {1}）" -f $code.code, $code.expiresAtUtc) -ForegroundColor Yellow
