#Requires -Version 5.1
<#
.SYNOPSIS
    试运行取证：按周期登录并采集只读观测端点，逐行追加 JSONL 证据。

.DESCRIPTION
    每次采样：新建 WebRequestSession -> GET /api/v1/auth/session 取 csrfToken
    -> 带 X-CSRF-TOKEN POST /api/v1/auth/login -> 再 GET session 刷新 token
    -> GET /api/v1/ops/metrics、/api/v1/ops/alerts、/api/v1/dashboard。

    证据文件：traceflow/tools/trial/.runtime/evidence/YYYY-MM-DD.jsonl
    每行：{ tsUtc, ok, status, metrics?, alerts?, alertsCount?, jobsFailed24h?, backlog?, dashboard?, error? }

    约束：单个周期失败只记录、不中断；日志绝不写入密码/cookie/token；失败等待下个周期
    （不做登录重试风暴）。

.PARAMETER BaseUrl
    试运行宿主地址，默认 http://127.0.0.1:5080。

.PARAMETER User
    登录用户名，缺省读环境变量 TRACEFLOW_TRIAL_ADMIN。

.PARAMETER Password
    登录密码，缺省读环境变量 TRACEFLOW_TRIAL_ADMIN_PASSWORD（绝不打印/落盘）。

.PARAMETER IntervalMinutes
    采样周期（分钟），默认 60。

.PARAMETER Days
    连续采集天数，默认 7；配合 -Once 忽略。

.PARAMETER Once
    只采样一次后退出。
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://127.0.0.1:5080',
    [string]$User = $env:TRACEFLOW_TRIAL_ADMIN,
    [string]$Password = $env:TRACEFLOW_TRIAL_ADMIN_PASSWORD,
    [int]$IntervalMinutes = 60,
    [int]$Days = 7,
    [switch]$Once
)

$ErrorActionPreference = 'Stop'
# 控制台输出统一 UTF-8（脚本文件本身为 UTF-8 BOM），避免重定向到日志/管道时中文乱码。
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
$ProgressPreference = 'SilentlyContinue'

$TrialRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$EvidenceDir = Join-Path (Join-Path $TrialRoot '.runtime') 'evidence'
$BaseUrl = $BaseUrl.TrimEnd('/')

if ([string]::IsNullOrWhiteSpace($User)) {
    Write-Host '[失败] 缺少用户名：请传 -User 或设置环境变量 TRACEFLOW_TRIAL_ADMIN。' -ForegroundColor Red
    exit 2
}
if ([string]::IsNullOrEmpty($Password)) {
    Write-Host '[失败] 缺少密码：请设置环境变量 TRACEFLOW_TRIAL_ADMIN_PASSWORD。' -ForegroundColor Red
    exit 2
}
if ($IntervalMinutes -lt 1) { $IntervalMinutes = 1 }
if ($Days -lt 1) { $Days = 1 }

if (-not (Test-Path -LiteralPath $EvidenceDir)) {
    New-Item -ItemType Directory -Path $EvidenceDir -Force | Out-Null
}

function Get-UtcNowText { return (Get-Date).ToUniversalTime().ToString('o') }

# 只移除密码与可能被异常文本带出的会话材料；不输出任何 cookie/token。
function Sanitize-Text([string]$Text) {
    if ([string]::IsNullOrEmpty($Text)) { return $Text }
    $t = $Text
    if ($Password) { $t = $t.Replace($Password, '***') }
    $t = [regex]::Replace($t, '(?i)(password|pwd)\s*=\s*[^;&]*', '$1=***')
    $t = [regex]::Replace($t, '(?i)(X-CSRF-TOKEN|cookie|set-cookie|authorization)\s*[:=]\s*[^\r\n]*', '$1: ***')
    if ($t.Length -gt 500) { $t = $t.Substring(0, 500) }
    return $t
}

function Get-ErrorText($ErrorRecord) {
    $msg = $ErrorRecord.Exception.Message
    $resp = $ErrorRecord.Exception.Response
    if ($resp) {
        try {
            $stream = $resp.GetResponseStream()
            if ($stream) {
                $reader = New-Object System.IO.StreamReader($stream)
                $body = $reader.ReadToEnd()
                if (-not [string]::IsNullOrWhiteSpace($body)) { $msg = $body }
            }
        } catch { }
    }
    return (Sanitize-Text ([string]$msg))
}

function Get-StatusCode($ErrorRecord) {
    $resp = $ErrorRecord.Exception.Response
    if ($resp -and $resp.StatusCode) { return [int]$resp.StatusCode }
    return 0
}

function Invoke-TrialSample {
    $record = [ordered]@{
        tsUtc = Get-UtcNowText
        ok    = $false
        status = 0
    }
    try {
        $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession

        $sessionUrl = $BaseUrl + '/api/v1/auth/session'
        $first = Invoke-RestMethod -Uri $sessionUrl -Method Get -WebSession $session -TimeoutSec 30
        $token = $null
        if ($first -and $first.data) { $token = $first.data.csrfToken }
        if ([string]::IsNullOrWhiteSpace($token)) { throw '会话未返回 csrfToken' }

        $payload = @{ username = $User; password = $Password } | ConvertTo-Json -Compress
        $null = Invoke-RestMethod -Uri ($BaseUrl + '/api/v1/auth/login') -Method Post -WebSession $session `
            -Headers @{ 'X-CSRF-TOKEN' = $token } -ContentType 'application/json' -Body $payload -TimeoutSec 30

        # 登录后刷新会话（antiforgery token 轮换）；只读取，不落盘。
        $null = Invoke-RestMethod -Uri $sessionUrl -Method Get -WebSession $session -TimeoutSec 30

        $metrics = Invoke-RestMethod -Uri ($BaseUrl + '/api/v1/ops/metrics') -Method Get -WebSession $session -TimeoutSec 60
        $alerts = Invoke-RestMethod -Uri ($BaseUrl + '/api/v1/ops/alerts') -Method Get -WebSession $session -TimeoutSec 60
        $dashboard = Invoke-RestMethod -Uri ($BaseUrl + '/api/v1/dashboard') -Method Get -WebSession $session -TimeoutSec 60

        $metricList = [System.Collections.Generic.List[object]]::new()
        if ($alerts -and $alerts.data -and $alerts.data.alerts) {
            foreach ($a in @($alerts.data.alerts)) {
                $metricList.Add([ordered]@{ code = [string]$a.code; severity = [string]$a.severity; count = [int]$a.count })
            }
        }

        $m = $null
        if ($metrics -and $metrics.data) { $m = $metrics.data }
        $d = $null
        if ($dashboard -and $dashboard.data) { $d = $dashboard.data }

        $summary = [ordered]@{}
        if ($m -and $m.jobs) {
            $summary.env = [string]$m.env
            $summary.backlog = [int]$m.jobs.backlog
            $summary.pending = [int]$m.jobs.pending
            $summary.running = [int]$m.jobs.running
            $summary.failed = [int]$m.jobs.failed
            $summary.failureRate24h = $m.jobs.failureRate24h
            $summary.oldestPendingAgeSeconds = [int]$m.jobs.oldestPendingAgeSeconds
            $summary.leaseRecoveries24h = [int]$m.leaseRecoveries24h
            if ($m.recon) {
                $summary.reconIncomplete24h = [int]$m.recon.incomplete24h
                $summary.openDifferences = [int]$m.recon.openDifferences
            }
        }

        $record.ok = $true
        $record.status = 200
        if ($summary.Count -gt 0) { $record.metrics = $summary }
        $record.alerts = $metricList
        $record.alertsCount = $metricList.Count
        if ($summary.Contains('failed')) { $record.jobsFailed24h = $summary.failed }
        if ($summary.Contains('backlog')) { $record.backlog = $summary.backlog }
        if ($d) {
            $record.dashboard = [ordered]@{
                env            = [string]$d.env
                alertsCount    = [int]$d.alertsCount
                failedJobs24h  = [int]$d.failedJobs24h
                pendingJobs    = [int]$d.pendingJobs
                openDifferences = [int]$d.openDifferences
            }
        }
    } catch {
        $record.ok = $false
        $record.status = Get-StatusCode $_
        $record.error = Get-ErrorText $_
    }
    return $record
}

function Write-EvidenceRecord($Record) {
    # 日期边界按本地日期归档，便于"每日检查"。
    $today = (Get-Date).ToString('yyyy-MM-dd')
    $path = Join-Path $EvidenceDir ($today + '.jsonl')
    $line = ($Record | ConvertTo-Json -Compress -Depth 8)
    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::AppendAllText($path, $line + [Environment]::NewLine, $encoding)
    return $path
}

Write-Host ("取证开始：" + $BaseUrl + "，用户 " + $User + "，周期 " + $IntervalMinutes + " 分钟，天数 " + $Days + $(if ($Once) { '（单次）' } else { '' }))
Write-Host ("证据目录：" + $EvidenceDir)

$deadline = (Get-Date).AddDays($Days)
$sampleIndex = 0
while ($true) {
    $sampleIndex++
    $record = Invoke-TrialSample
    $path = Write-EvidenceRecord $record
    $flag = 'OK '
    if (-not $record.ok) { $flag = 'ERR' }
    $detail = ''
    if ($record.ok) {
        $detail = ('backlog=' + $record.backlog + ' alerts=' + $record.alertsCount + ' failed24h=' + $record.jobsFailed24h)
    } else {
        $detail = ('status=' + $record.status + ' ' + $record.error)
    }
    Write-Host ("[" + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss') + "] #" + $sampleIndex + " " + $flag + " " + $detail + " -> " + [System.IO.Path]::GetFileName($path))

    if ($Once) { break }
    if ((Get-Date) -ge $deadline) { break }
    $sleepSeconds = $IntervalMinutes * 60
    $remaining = [int][Math]::Ceiling(($deadline - (Get-Date)).TotalSeconds)
    if ($remaining -lt $sleepSeconds) { $sleepSeconds = $remaining }
    if ($sleepSeconds -gt 0) { Start-Sleep -Seconds $sleepSeconds }
}

exit 0