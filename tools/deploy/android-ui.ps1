# Android 端的 UI 驱动小工具（MuMu 模拟器上用），不是产品的一部分。
#
# 存在的理由：手机端改版后要在真机（这里是 MuMu 模拟器）上按人的方式点一遍，而 adb 本身只会给你
# 原始 XML。这里把「dump → 找节点 → 点它 / 输入 / 截图」包成一条命令，方便反复用。
#
# 用法（每次调用都是新进程，所以动作走参数）：
#   pwsh -File tools\deploy\android-ui.ps1 -Dump
#   pwsh -File tools\deploy\android-ui.ps1 -Nodes "记录随想"
#   pwsh -File tools\deploy\android-ui.ps1 -Tap "记录随想"
#   pwsh -File tools\deploy\android-ui.ps1 -TapXY "540,1200"
#   pwsh -File tools\deploy\android-ui.ps1 -Text "今天想说的"
#   pwsh -File tools\deploy\android-ui.ps1 -Back
#   pwsh -File tools\deploy\android-ui.ps1 -Screenshot shot.png
#
# 注意（踩过的坑，别重复）：
#   * 界面里有计时器/动画在刷新时，`uiautomator dump` 会等不到 idle 而超时；这时先停掉动态界面再 dump，
#     或者改用别的证据（服务端收到的条目、日志）来判断点击是否生效。dump 返回的**可能是上一次**的层级，
#     所以「点完立刻 dump」不能用来判断点击是否生效。
#   * 中文输入法会把 `adb shell input text` 的 ASCII 标点转成全角。需要输入含标点的文本时，
#     先 `adb shell ime list -s -a` 找一个 ASCII 输入法并 `ime set` 切过去。
#   * 不要为了收键盘按 BACK（在根页面会直接退出应用）；改用点击仍可见的按钮，或 am force-stop + am start。
param(
    [string]$Device = "127.0.0.1:16384",
    [string]$Adb = "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe",
    [switch]$Dump,
    [string]$Nodes = "",
    [string]$Tap = "",
    [string]$TapXY = "",
    [switch]$Back,
    [string]$Text = "",
    [string]$Screenshot = "",
    [switch]$Kill,
    [string]$Start = "",
    [int]$Wait = 1
)

$ErrorActionPreference = "Stop"
$remote = "/sdcard/dm-ui.xml"
$local = Join-Path $env:TEMP "dm-ui.xml"

function Invoke-Adb {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Args)

    # adb 会把「1 file pulled」这类正常信息写到 stderr，在 $ErrorActionPreference='Stop' 下会变成终止性
    # 错误。这里临时放开，只把它当普通输出收下来。
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        return (& $Adb -s $Device @Args 2>&1 | Out-String)
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

function Get-Tree {
    # dump 偶尔会因为界面在动而失败，重试两次再放弃。
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        $result = Invoke-Adb shell uiautomator dump $remote
        if ($result -match "UI hierchary dumped|dumped to") {
            Invoke-Adb pull $remote $local | Out-Null
            if (Test-Path $local) {
                return [xml](Get-Content -Raw -Encoding UTF8 $local)
            }
        }
        Start-Sleep -Milliseconds 800
    }
    throw "uiautomator dump 失败：界面可能还在动。"
}

function Get-AllNodes {
    param($Tree)
    return $Tree.SelectNodes("//node")
}

function Format-Node {
    param($Node)
    $text = $Node.GetAttribute("text")
    $desc = $Node.GetAttribute("content-desc")
    $cls = $Node.GetAttribute("class")
    $bounds = $Node.GetAttribute("bounds")
    $clickable = $Node.GetAttribute("clickable")
    "{0,-28} | {1,-24} | {2,-40} | clickable={3} | {4}" -f $cls.Replace("android.widget.", ""), $text, $desc, $clickable, $bounds
}

if ($Kill) {
    Invoke-Adb shell am force-stop dev.dailymusings.client | Out-Null
    Write-Host "app stopped"
    exit 0
}

if ($Start) {
    Invoke-Adb shell am start -n "$Start" | Out-Null
    Start-Sleep -Seconds $Wait
    Write-Host "started $Start"
    exit 0
}

if ($Dump) {
    $tree = Get-Tree
    foreach ($node in Get-AllNodes $tree) {
        $line = Format-Node $node
        if ($line -match "\|\s+\|\s+\|") { continue }
        Write-Host $line
    }
    exit 0
}

if ($Nodes) {
    $tree = Get-Tree
    foreach ($node in Get-AllNodes $tree) {
        $text = $node.GetAttribute("text") + " " + $node.GetAttribute("content-desc")
        if ($text -like "*$Nodes*") { Write-Host (Format-Node $node) }
    }
    exit 0
}

function Tap-Bounds {
    param([string]$Bounds)
    if ($Bounds -notmatch "\[(\d+),(\d+)\]\[(\d+),(\d+)\]") { throw "无法解析 bounds：$Bounds" }
    $x = [int](([int]$Matches[1] + [int]$Matches[3]) / 2)
    $y = [int](([int]$Matches[2] + [int]$Matches[4]) / 2)
    Invoke-Adb shell input tap $x $y | Out-Null
    Write-Host "tapped ($x,$y)"
}

if ($Tap) {
    $tree = Get-Tree
    $target = $null
    foreach ($node in Get-AllNodes $tree) {
        $text = $node.GetAttribute("text") + " " + $node.GetAttribute("content-desc")
        if ($text -like "*$Tap*") { $target = $node; break }
    }
    if ($null -eq $target) { throw "界面上没有找到包含「$Tap」的节点。" }
    Tap-Bounds $target.GetAttribute("bounds")
    Start-Sleep -Seconds $Wait
    exit 0
}

if ($TapXY) {
    $parts = $TapXY -split ","
    Invoke-Adb shell input tap ([int]$parts[0]) ([int]$parts[1]) | Out-Null
    Write-Host "tapped ($($parts[0]),$($parts[1]))"
    Start-Sleep -Seconds $Wait
    exit 0
}

if ($Back) {
    Invoke-Adb shell input keyevent 4 | Out-Null
    Start-Sleep -Seconds $Wait
    exit 0
}

if ($Text) {
    # 空格与中文用 input text 都有坑，先换成不含空格的写法；调用方负责切换输入法。
    $escaped = $Text -replace " ", "%s"
    Invoke-Adb shell input text $escaped | Out-Null
    Write-Host "typed"
    Start-Sleep -Seconds $Wait
    exit 0
}

if ($Screenshot) {
    Invoke-Adb shell screencap -p /sdcard/dm-shot.png | Out-Null
    Invoke-Adb pull /sdcard/dm-shot.png $Screenshot | Out-Null
    Write-Host "saved $Screenshot"
    exit 0
}

Write-Host "没有指定动作，见脚本头部的用法。"
