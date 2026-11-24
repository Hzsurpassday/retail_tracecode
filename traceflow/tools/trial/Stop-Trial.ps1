#Requires -Version 5.1
<#
.SYNOPSIS
    停止试运行宿主；可选删除 TraceFlow_Trial_* / TraceFlow_Perf_* 库。

.DESCRIPTION
    读取 traceflow/tools/trial/.runtime/host.pid（Start-Trial.ps1 写入），等待进程退出（≤15s）。
    只处理 PID 文件记录的进程及其子进程；进程名不是 dotnet/TraceFlow.Host 时拒绝强杀并报错。
    -DropDb 时调用 OpsTool `drop-db <DbName> --yes`，脚本内再次拒绝保护库/测试库/系统库。
    连接串只从 -BaseConn 或 $env:TRACEFLOW_TRIAL_CONN 读取，不内置凭据、不回显。

.PARAMETER DropDb
    停止后删除试运行库。

.PARAMETER DbName
    要删除的库名；缺省读取 .runtime/host.json 中记录的库名。

.PARAMETER BaseConn
    基础连接串（不含 Initial Catalog）；缺省读环境变量 TRACEFLOW_TRIAL_CONN（仅 -DropDb 需要）。
#>
[CmdletBinding()]
param(
    [switch]$DropDb,
    [string]$DbName,
    [string]$BaseConn = $env:TRACEFLOW_TRIAL_CONN
)

$ErrorActionPreference = 'Stop'
# 控制台输出统一 UTF-8（脚本文件本身为 UTF-8 BOM），避免重定向到日志/管道时中文乱码。
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

$TrialRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = (Resolve-Path (Join-Path $TrialRoot '..\..\..')).Path
$RuntimeDir = Join-Path $TrialRoot '.runtime'
$PidFile = Join-Path $RuntimeDir 'host.pid'
$MetaFile = Join-Path $RuntimeDir 'host.json'
$OpsToolProject = Join-Path $RepoRoot 'traceflow\tools\TraceFlow.OpsTool'

$NamePattern = '^TraceFlow_(Trial|Perf)_[A-Za-z0-9_]{1,60}$'
$ProtectedNames = @('TraceFlow_Dev', 'TraceFlow_M2Manual', 'TraceFlow_M2Gate', 'TraceFlow_M3Ui')
$SystemNames = @('master', 'model', 'msdb', 'tempdb')

function Fail([string]$Message, [int]$Code = 1) {
    Write-Host ("[失败] " + $Message) -ForegroundColor Red
    exit $Code
}

function Get-DescendantIds([int]$ParentId) {
    $ids = New-Object System.Collections.Generic.List[int]
    try {
        $children = @(Get-CimInstance Win32_Process -Filter ("ParentProcessId=" + $ParentId) -ErrorAction SilentlyContinue)
    } catch {
        $children = @()
    }
    foreach ($child in $children) {
        $ids.Add([int]$child.ProcessId)
        foreach ($grand in (Get-DescendantIds ([int]$child.ProcessId))) { $ids.Add($grand) }
    }
    return $ids
}

# ---------------------------------------------------------------- 1. 解析目标
if (-not (Test-Path -LiteralPath $RuntimeDir)) {
    Write-Host '无 .runtime 目录：视为未运行。'
}
if (-not $DbName -and (Test-Path -LiteralPath $MetaFile)) {
    try {
        $meta = Get-Content -LiteralPath $MetaFile -Raw | ConvertFrom-Json
        if ($meta -and $meta.dbName) { $DbName = [string]$meta.dbName }
    } catch {
        Write-Host '[警告] 无法解析 host.json，忽略其中的库名。' -ForegroundColor Yellow
    }
}

if ($DropDb) {
    if ([string]::IsNullOrWhiteSpace($DbName)) {
        Fail '使用了 -DropDb，但无法确定库名：请传 -DbName 或保留 .runtime/host.json。' 2
    }
    if ($DbName -cnotmatch $NamePattern) {
        Fail ("库名不合法：只允许 TraceFlow_Trial_* / TraceFlow_Perf_*。实际：" + $DbName) 2
    }
    foreach ($p in $ProtectedNames) {
        if ([string]::Equals($DbName, $p, [System.StringComparison]::OrdinalIgnoreCase)) {
            Fail ("拒绝删除保护库：" + $DbName) 2
        }
    }
    foreach ($s in $SystemNames) {
        if ([string]::Equals($DbName, $s, [System.StringComparison]::OrdinalIgnoreCase)) {
            Fail ("拒绝删除系统库：" + $DbName) 2
        }
    }
    if ($DbName -like 'TraceFlow_Test_*') { Fail ("拒绝删除集成测试库：" + $DbName) 2 }
    if ([string]::IsNullOrWhiteSpace($BaseConn)) {
        Fail '缺少连接串：-DropDb 需要 -BaseConn 或环境变量 TRACEFLOW_TRIAL_CONN（不含 Initial Catalog）。' 2
    }
}

# ---------------------------------------------------------------- 2. 停止进程
$stopped = $false
if (Test-Path -LiteralPath $PidFile) {
    $raw = (Get-Content -LiteralPath $PidFile -ErrorAction SilentlyContinue | Select-Object -First 1)
    $hostPid = 0
    if ($raw -and [int]::TryParse([string]$raw, [ref]$hostPid)) {
        $proc = Get-Process -Id $hostPid -ErrorAction SilentlyContinue
        if ($proc) {
            if ($proc.ProcessName -ne 'dotnet' -and $proc.ProcessName -ne 'TraceFlow.Host') {
                Fail ("PID " + $hostPid + " 的进程名是 '" + $proc.ProcessName + "'，不是本试运行宿主；拒绝强杀，请人工确认。") 1
            }
            Write-Host ("停止宿主 PID " + $hostPid + "（" + $proc.ProcessName + "）...")
            # dotnet run 会派生真正的应用进程：先停子进程，再停父进程。
            $descendants = @(Get-DescendantIds $hostPid)
            foreach ($childId in $descendants) {
                Stop-Process -Id $childId -Force -ErrorAction SilentlyContinue
            }
            Stop-Process -Id $hostPid -ErrorAction SilentlyContinue
            $deadline = (Get-Date).AddSeconds(15)
            while ((Get-Date) -lt $deadline) {
                $alive = Get-Process -Id $hostPid -ErrorAction SilentlyContinue
                if (-not $alive) { break }
                Start-Sleep -Milliseconds 500
            }
            if (Get-Process -Id $hostPid -ErrorAction SilentlyContinue) {
                Stop-Process -Id $hostPid -Force -ErrorAction SilentlyContinue
                Start-Sleep -Seconds 2
            }
            if (Get-Process -Id $hostPid -ErrorAction SilentlyContinue) {
                Fail ("宿主 PID " + $hostPid + " 未能在 15s 内退出，请人工检查。") 1
            }
            $stopped = $true
            Write-Host ("宿主 PID " + $hostPid + " 已退出（子进程 " + $descendants.Count + " 个）。")
        } else {
            Write-Host ("PID 文件记录的进程 " + $hostPid + " 已不存在。")
        }
    } else {
        Write-Host '[警告] PID 文件内容不是整数，视为无运行中宿主。' -ForegroundColor Yellow
    }
} else {
    Write-Host '未找到 PID 文件：视为无运行中宿主。'
}
if (-not $stopped) { Write-Host '宿主进程已不在运行。' }

# ---------------------------------------------------------------- 3. 清理 PID 文件
Remove-Item -LiteralPath $PidFile -Force -ErrorAction SilentlyContinue

# ---------------------------------------------------------------- 4. 可选删库
if ($DropDb) {
    Write-Host ("删除库 " + $DbName + " ...")
    $env:TRACEFLOW_TRIAL_CONN = $BaseConn
    & dotnet run --project $OpsToolProject -c Release -- drop-db $DbName --yes
    if ($LASTEXITCODE -ne 0) { Fail ("drop-db 失败（退出码 " + $LASTEXITCODE + "）。host.json 已保留，可修正后重试。") $LASTEXITCODE }
    Write-Host ("库 " + $DbName + " 处理完成。")
}

# ---------------------------------------------------------------- 5. 清理元数据（放在删库之后）
# host.json 记录库名：删库失败时保留它，重跑 -DropDb 才能自动定位目标库。
Remove-Item -LiteralPath $MetaFile -Force -ErrorAction SilentlyContinue

exit 0