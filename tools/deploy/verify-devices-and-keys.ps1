# 专项自检：两个「界面新增、也必须由服务端真正支持」的能力。
#
# 起因：管理页改完之后才发现两处缺口 —— 模型端点的密钥在后台写不了、「删除已撤销设备」只是再撤回一次。
# 两个都在服务端补上了，这个脚本专门验它们，避免它们只活在界面文案里。
#
# 用法：
#   pwsh -File tools\deploy\verify-devices-and-keys.ps1 -BaseUrl http://100.64.0.3:18321 -AdminUser admin -AdminPassword 'xxx'
param(
    [string]$BaseUrl = "http://100.64.0.3:18321",
    [string]$AdminUser = "admin",
    [string]$AdminPassword = ""
)

$ErrorActionPreference = "Stop"
$BaseUrl = $BaseUrl.TrimEnd('/')
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$failures = @()

function Check {
    param([string]$Name, [scriptblock]$Body)
    try {
        $result = & $Body
        Write-Host ("  [PASS] {0}{1}" -f $Name, $(if ($result) { " -> $result" } else { "" })) -ForegroundColor Green
    }
    catch {
        $failures += "$Name : $($_.Exception.Message)"
        Write-Host ("  [FAIL] {0} -> {1}" -f $Name, $_.Exception.Message) -ForegroundColor Red
    }
}

function Send-Api {
    param([string]$Method, [string]$Path, $Body = $null)

    $params = @{
        Uri         = "$BaseUrl$Path"
        Method      = $Method
        WebSession  = $session
        TimeoutSec  = 60
        ContentType = "application/json; charset=utf-8"
    }

    if ($null -ne $Body) { $params.Body = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 5)) }

    return Invoke-RestMethod @params
}

function Read-Error {
    param($Exception)
    $response = $Exception.Exception.Response
    if ($null -eq $response) { return $null }
    $reader = New-Object System.IO.StreamReader($response.GetResponseStream())
    return ($reader.ReadToEnd() | ConvertFrom-Json)
}

Invoke-WebRequest -Uri "$BaseUrl/api/admin/sign-in" -Method Post -WebSession $session -TimeoutSec 30 -UseBasicParsing `
    -Body @{ username = $AdminUser; password = $AdminPassword; acknowledgeRisk = "yes" } | Out-Null

Write-Host "== 模型端点的密钥可以在这里填写、也可以清除 ==" -ForegroundColor Cyan

Check "起点：记录的密钥来源" {
    $endpoint = (Send-Api GET "/api/system/model-endpoints").items | Where-Object { $_.service -eq "transcription" }
    "source=$($endpoint.passwordSource) has=$($endpoint.hasPassword)"
} | Out-Null

$originalSource = ((Send-Api GET "/api/system/model-endpoints").items |
    Where-Object { $_.service -eq "transcription" }).passwordSource

Check "写入密钥后来源变成 ui" {
    $updated = Send-Api PATCH "/api/system/model-endpoints/transcription" @{ password = "dm-selftest-key-12345" }
    if ($updated.passwordSource -ne "ui") { throw "写入后来源是 $($updated.passwordSource)，不是 ui" }
    if (-not $updated.hasPassword) { throw "hasPassword 是 false" }
    "source=ui"
}

Check "接口不把密钥读回（响应里没有 password 字段）" {
    $endpoint = (Send-Api GET "/api/system/model-endpoints").items | Where-Object { $_.service -eq "transcription" }
    if ($null -ne $endpoint.PSObject.Properties["password"]) { throw "响应里出现了 password 字段" }
    "只有 passwordSource/hasPassword"
} | Out-Null

Check "清除后回到原来的来源" {
    Send-Api PATCH "/api/system/model-endpoints/transcription" @{ clearPassword = $true } | Out-Null
    $endpoint = (Send-Api GET "/api/system/model-endpoints").items | Where-Object { $_.service -eq "transcription" }
    if ($endpoint.passwordSource -ne $originalSource) {
        throw "清除后来源是 $($endpoint.passwordSource)，原来是 $originalSource"
    }
    "source=$($endpoint.passwordSource)"
}

Write-Host "== 删除设备：只对已撤销的设备可用 ==" -ForegroundColor Cyan

$deviceId = $null

Check "签发配对码并配一台临时设备" {
    $code = (Send-Api POST "/api/pairing/codes" @{}).code
    $redeemed = Invoke-RestMethod -Uri "$BaseUrl/api/pairing/redeem" -Method Post -TimeoutSec 30 `
        -ContentType "application/json; charset=utf-8" `
        -Body ([Text.Encoding]::UTF8.GetBytes((@{ code = $code; deviceName = "临时设备"; platform = "android" } | ConvertTo-Json)))
    $script:deviceId = $redeemed.deviceId
    "id=$($script:deviceId)"
}

Check "还在授权中的设备不允许删除（409 device.not_revoked）" {
    try {
        Send-Api DELETE "/api/devices/$script:deviceId/record" | Out-Null
        throw "居然删成功了 —— 守卫没生效"
    }
    catch [System.Net.WebException] {
        $error = Read-Error $_
        if ($error.code -ne "device.not_revoked") { throw "错误码是 $($error.code)" }
        "被拒绝：$($error.code)"
    }
} | Out-Null

Check "撤回之后可以真正删除它的记录" {
    Send-Api DELETE "/api/devices/$script:deviceId" | Out-Null
    Send-Api DELETE "/api/devices/$script:deviceId/record" | Out-Null

    $stillThere = (Send-Api GET "/api/devices").items | Where-Object { $_.id -eq $script:deviceId }
    if ($stillThere) { throw "设备行还在" }
    "已删除"
}

Write-Host ""
Write-Host ("== {0} 项失败 ==" -f $failures.Count) -ForegroundColor Cyan
foreach ($failure in $failures) { Write-Host "  - $failure" -ForegroundColor Red }
if ($failures.Count -gt 0) { exit 1 }
