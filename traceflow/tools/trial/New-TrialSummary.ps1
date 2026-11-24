#Requires -Version 5.1
<#
.SYNOPSIS
    聚合试运行证据（JSONL）生成一页摘要。

.DESCRIPTION
    读取 traceflow/tools/trial/.runtime/evidence/*.jsonl，统计：
    覆盖日期数、样本数、失败样本数与失败原因 Top、相邻样本间隔超过 2 倍周期的缺口数、
    backlog 最大/中位、alertsCount 最大、出现过的 alert code 集合、对账未完成（RECON INCOMPLETE）是否出现。
    覆盖不足 7 天时，摘要首行明确"未满 7 天，不构成试运行验收"。

.PARAMETER EvidenceDir
    证据目录，默认 traceflow/tools/trial/.runtime/evidence。

.PARAMETER Out
    输出路径，默认 traceflow/tools/trial/.runtime/summary.md。

.PARAMETER IntervalMinutes
    预期采样周期（分钟），默认 60；用于缺口判定（> 2 倍周期）。

.PARAMETER ExpectedDays
    验收所需覆盖天数，默认 7。
#>
[CmdletBinding()]
param(
    [string]$EvidenceDir,
    [string]$Out,
    [int]$IntervalMinutes = 60,
    [int]$ExpectedDays = 7
)

$ErrorActionPreference = 'Stop'
# 控制台输出统一 UTF-8（脚本文件本身为 UTF-8 BOM），避免重定向到日志/管道时中文乱码。
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

$TrialRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$RuntimeDir = Join-Path $TrialRoot '.runtime'
if ([string]::IsNullOrWhiteSpace($EvidenceDir)) { $EvidenceDir = Join-Path $RuntimeDir 'evidence' }
if ([string]::IsNullOrWhiteSpace($Out)) { $Out = Join-Path $RuntimeDir 'summary.md' }

if (-not (Test-Path -LiteralPath $EvidenceDir)) {
    Write-Host ("[失败] 证据目录不存在：" + $EvidenceDir) -ForegroundColor Red
    exit 2
}
$outDir = Split-Path -Parent $Out
if ($outDir -and -not (Test-Path -LiteralPath $outDir)) {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}

function ConvertTo-UtcDate($Value) {
    if ($null -eq $Value) { return $null }
    if ($Value -is [datetime]) { return $Value.ToUniversalTime() }
    try { return [datetime]::Parse([string]$Value, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime() } catch { return $null }
}

function Get-Median([double[]]$Numbers) {
    if ($null -eq $Numbers -or $Numbers.Length -eq 0) { return $null }
    $sorted = $Numbers | Sort-Object
    $n = $sorted.Length
    if ($n % 2 -eq 1) { return [double]$sorted[[int](($n - 1) / 2)] }
    return ([double]$sorted[[int](($n / 2) - 1)] + [double]$sorted[[int]($n / 2)]) / 2.0
}

$files = @(Get-ChildItem -LiteralPath $EvidenceDir -Filter '*.jsonl' -File | Sort-Object Name)
$samples = New-Object System.Collections.Generic.List[object]
$invalidLines = 0

foreach ($file in $files) {
    foreach ($line in (Get-Content -LiteralPath $file.FullName -Encoding UTF8)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try {
            $obj = $line | ConvertFrom-Json
            if ($obj) { $samples.Add($obj) } else { $invalidLines++ }
        } catch {
            $invalidLines++
        }
    }
}

$total = $samples.Count
$failed = @($samples | Where-Object { -not $_.ok })
$okCount = $total - $failed.Count

# 覆盖日期（按样本 tsUtc 的本地日期）
$dates = @()
foreach ($s in $samples) {
    $utc = ConvertTo-UtcDate $s.tsUtc
    if ($utc) { $dates += $utc.ToLocalTime().ToString('yyyy-MM-dd') }
}
$coveredDates = @($dates | Sort-Object -Unique)
$coveredDays = $coveredDates.Count

# 缺口：相邻样本间隔 > 2 倍周期
$timestamps = @()
foreach ($s in $samples) {
    $utc = ConvertTo-UtcDate $s.tsUtc
    if ($utc) { $timestamps += $utc }
}
$timestamps = @($timestamps | Sort-Object)
$thresholdMinutes = 2 * $IntervalMinutes
$gapCount = 0
$gapDetails = New-Object System.Collections.Generic.List[string]
for ($i = 1; $i -lt $timestamps.Count; $i++) {
    $delta = ($timestamps[$i] - $timestamps[$i - 1]).TotalMinutes
    if ($delta -gt $thresholdMinutes) {
        $gapCount++
        if ($gapDetails.Count -lt 10) {
            $gapDetails.Add($timestamps[$i - 1].ToLocalTime().ToString('yyyy-MM-dd HH:mm') + ' -> ' + $timestamps[$i].ToLocalTime().ToString('yyyy-MM-dd HH:mm') + '（' + [int][Math]::Round($delta) + ' 分钟）')
        }
    }
}

# backlog / alertsCount
$backlogs = @()
$alertsCounts = @()
foreach ($s in $samples) {
    if ($null -ne $s.backlog) { $backlogs += [double]$s.backlog }
    if ($null -ne $s.alertsCount) { $alertsCounts += [double]$s.alertsCount }
    elseif ($s.dashboard -and $null -ne $s.dashboard.alertsCount) { $alertsCounts += [double]$s.dashboard.alertsCount }
}
$backlogMax = $null
$backlogMedian = $null
if ($backlogs.Count -gt 0) {
    $backlogMax = ($backlogs | Measure-Object -Maximum).Maximum
    $backlogMedian = Get-Median ([double[]]$backlogs)
}
$alertsMax = $null
if ($alertsCounts.Count -gt 0) { $alertsMax = ($alertsCounts | Measure-Object -Maximum).Maximum }

# alert code 集合 + 对账未完成
$codes = New-Object System.Collections.Generic.SortedSet[string]
$reconIncompleteSeen = $false
foreach ($s in $samples) {
    if ($s.alerts) {
        foreach ($a in @($s.alerts)) {
            if ($a -and $a.code) { [void]$codes.Add([string]$a.code) }
        }
    }
    if ($s.alerts) {
        foreach ($a in @($s.alerts)) {
            if ($a -and ([string]$a.code) -eq 'RECON_INCOMPLETE_24H') { $reconIncompleteSeen = $true }
        }
    }
    if ($s.metrics -and $null -ne $s.metrics.reconIncomplete24h -and [int]$s.metrics.reconIncomplete24h -gt 0) {
        $reconIncompleteSeen = $true
    }
}

# 失败原因 Top
$reasonCounts = @{}
foreach ($f in $failed) {
    $reason = '（无错误文本）'
    if ($f.error) { $reason = [string]$f.error }
    if ($reason.Length -gt 120) { $reason = $reason.Substring(0, 120) }
    if ($reasonCounts.ContainsKey($reason)) { $reasonCounts[$reason]++ } else { $reasonCounts[$reason] = 1 }
}
$reasonTop = @($reasonCounts.GetEnumerator() | Sort-Object -Property Value -Descending | Select-Object -First 5)

$breach = $coveredDays -lt $ExpectedDays

$sb = New-Object System.Text.StringBuilder
if ($breach) {
    [void]$sb.AppendLine('未满 7 天，不构成试运行验收')
    [void]$sb.AppendLine('')
}
[void]$sb.AppendLine('# 试运行证据摘要')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('- 生成时间（本地）：' + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))
[void]$sb.AppendLine('- 证据目录：' + $EvidenceDir)
[void]$sb.AppendLine('- 预期周期：' + $IntervalMinutes + ' 分钟；验收所需覆盖：' + $ExpectedDays + ' 天')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('## 覆盖与样本')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('- 覆盖日期数：' + $coveredDays + '（' + ($coveredDates -join '、') + '）')
[void]$sb.AppendLine('- 样本数：' + $total + '（成功 ' + $okCount + '，失败 ' + $failed.Count + '）')
[void]$sb.AppendLine('- 无法解析的行：' + $invalidLines)
[void]$sb.AppendLine('- 相邻样本间隔 > ' + $thresholdMinutes + ' 分钟的缺口数：' + $gapCount)
foreach ($g in $gapDetails) { [void]$sb.AppendLine('  - ' + $g) }
[void]$sb.AppendLine('')
[void]$sb.AppendLine('## 队列与告警')
[void]$sb.AppendLine('')
if ($null -ne $backlogMax) {
    [void]$sb.AppendLine('- backlog 最大：' + [int]$backlogMax + '；中位：' + [double]$backlogMedian)
} else {
    [void]$sb.AppendLine('- backlog：无数据')
}
if ($null -ne $alertsMax) {
    [void]$sb.AppendLine('- alertsCount 最大：' + [int]$alertsMax)
} else {
    [void]$sb.AppendLine('- alertsCount：无数据')
}
if ($codes.Count -gt 0) {
    [void]$sb.AppendLine('- 出现过的 alert code：' + (@($codes) -join '、'))
} else {
    [void]$sb.AppendLine('- 出现过的 alert code：（无）')
}
[void]$sb.AppendLine('- 对账未完成（RECON INCOMPLETE）是否出现：' + $(if ($reconIncompleteSeen) { '是' } else { '否' }))
[void]$sb.AppendLine('')
[void]$sb.AppendLine('## 失败原因 Top')
[void]$sb.AppendLine('')
if ($reasonTop.Count -eq 0) {
    [void]$sb.AppendLine('- （无失败样本）')
} else {
    foreach ($r in $reasonTop) {
        [void]$sb.AppendLine('- ' + $r.Value + ' 次：' + $r.Key)
    }
}
[void]$sb.AppendLine('')
if ($breach) {
    [void]$sb.AppendLine('> 结论：覆盖 ' + $coveredDays + '/' + $ExpectedDays + ' 天，未满 ' + $ExpectedDays + ' 天，不构成试运行验收。')
}

$encoding = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($Out, $sb.ToString(), $encoding)

Write-Host ("摘要已写入：" + (Resolve-Path $Out).Path)
if ($breach) { Write-Host '未满 7 天，不构成试运行验收' -ForegroundColor Yellow }
Write-Host ("覆盖 " + $coveredDays + " 天 / 样本 " + $total + "（失败 " + $failed.Count + "）/ 缺口 " + $gapCount)
exit 0