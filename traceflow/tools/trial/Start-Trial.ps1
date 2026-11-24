#Requires -Version 5.1
<#
.SYNOPSIS
    启动 TraceFlow 试运行实例（独立 TraceFlow_Trial_* 库、MOCK 通道、仅本机回环端口）。

.DESCRIPTION
    前置条件：
      - 本机 SQL Server 可用（Windows 集成认证或由调用方通过环境变量提供连接串）；
      - 已 `dotnet build traceflow/TraceFlow.sln -c Release`（首跑会自行还原/构建）；
      - 连接串只从 -BaseConn 或 $env:TRACEFLOW_TRIAL_CONN 读取，脚本不内置任何凭据，也不回显连接串。

    步骤：库名自检 -> OpsTool create-db -> DbMigrator 迁移 -> （可选）OpsTool init-admin
          -> Start-Process 启动 Host（--web-only）-> 轮询 /api/v1/health（≤60s）-> 打印"就绪"。

.PARAMETER DbName
    试运行库名，默认 TraceFlow_Trial_<yyyyMMdd>；必须匹配 ^TraceFlow_(Trial|Perf)_[A-Za-z0-9_]{1,60}$。

.PARAMETER BaseConn
    不含 Initial Catalog 的基础连接串；缺省读环境变量 TRACEFLOW_TRIAL_CONN。

.PARAMETER Port
    宿主监听端口（仅绑定 127.0.0.1），默认 5080。

.PARAMETER SeedAdminUser
    可选：创建管理员用户名。需要环境变量 TRACEFLOW_TRIAL_ADMIN_PASSWORD（≥10 位，不打印）。

.EXAMPLE
    $env:TRACEFLOW_TRIAL_CONN = 'Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true'
    .\Start-Trial.ps1 -SeedAdminUser trialadmin
#>
[CmdletBinding()]
param(
    [string]$DbName = ('TraceFlow_Trial_' + (Get-Date).ToString('yyyyMMdd')),
    [string]$BaseConn = $env:TRACEFLOW_TRIAL_CONN,
    [int]$Port = 5080,
    [string]$SeedAdminUser
)

$ErrorActionPreference = 'Stop'
# 控制台输出统一 UTF-8（脚本文件本身为 UTF-8 BOM），避免重定向到日志/管道时中文乱码。
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

$TrialRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = (Resolve-Path (Join-Path $TrialRoot '..\..\..')).Path
$RuntimeDir = Join-Path $TrialRoot '.runtime'
$PidFile = Join-Path $RuntimeDir 'host.pid'
$MetaFile = Join-Path $RuntimeDir 'host.json'
$EvidenceDir = Join-Path $RuntimeDir 'evidence'
$OpsToolProject = Join-Path $RepoRoot 'traceflow\tools\TraceFlow.OpsTool'
$MigratorProject = Join-Path $RepoRoot 'traceflow\db\tools\TraceFlow.DbMigrator'
$HostProject = Join-Path $RepoRoot 'traceflow\src\TraceFlow.Host'
$MigrationsDir = Join-Path $RepoRoot 'traceflow\db\migrations'

# 与 TraceFlow.Application/Ops/DatabaseNamePolicy 同规则（脚本侧先自检，纵深防御）。
$NamePattern = '^TraceFlow_(Trial|Perf)_[A-Za-z0-9_]{1,60}$'
$ProtectedNames = @('TraceFlow_Dev', 'TraceFlow_M2Manual', 'TraceFlow_M2Gate', 'TraceFlow_M3Ui')

function Fail([string]$Message, [int]$Code = 1) {
    Write-Host ("[失败] " + $Message) -ForegroundColor Red
    exit $Code
}

function Hide-SecretText([string]$Text) {
    if ([string]::IsNullOrEmpty($Text)) { return $Text }
    $t = $Text
    if ($script:BaseConn) { $t = $t.Replace($script:BaseConn, '<conn>') }
    if ($script:FullConn) { $t = $t.Replace($script:FullConn, '<conn>') }
    $t = [regex]::Replace($t, '(?i)(password|pwd)\s*=\s*[^;]*', '$1=***')
    return $t
}

function Show-LogTail([string]$Path, [int]$Lines = 40) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    Write-Host ("---- " + [System.IO.Path]::GetFileName($Path) + "（尾部 " + $Lines + " 行）----") -ForegroundColor Yellow
    Get-Content -LiteralPath $Path -Tail $Lines -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host (Hide-SecretText $_)
    }
}

# ---------------------------------------------------------------- 1. 参数与库名自检
if ([string]::IsNullOrWhiteSpace($DbName)) { Fail '缺少库名。' 2 }
if ($DbName -cnotmatch $NamePattern) {
    Fail ("库名不合法：必须以 TraceFlow_Trial_ 或 TraceFlow_Perf_ 开头，仅含字母/数字/下划线（1-60 位）。实际：" + $DbName) 2
}
foreach ($p in $ProtectedNames) {
    if ([string]::Equals($DbName, $p, [System.StringComparison]::OrdinalIgnoreCase)) {
        Fail ("拒绝操作保护库：" + $DbName) 2
    }
}
if ($DbName -like 'TraceFlow_Test_*') { Fail ("拒绝操作集成测试库：" + $DbName) 2 }
if ([string]::IsNullOrWhiteSpace($BaseConn)) {
    Fail '缺少基础连接串：请传 -BaseConn 或设置环境变量 TRACEFLOW_TRIAL_CONN（不含 Initial Catalog）。' 2
}
if ($SeedAdminUser) {
    $adminPwd = $env:TRACEFLOW_TRIAL_ADMIN_PASSWORD
    if ([string]::IsNullOrEmpty($adminPwd) -or $adminPwd.Length -lt 10) {
        Fail '使用了 -SeedAdminUser，但环境变量 TRACEFLOW_TRIAL_ADMIN_PASSWORD 缺失或不足 10 位。' 2
    }
}

# ---------------------------------------------------------------- 2. 防重复启动
if (-not (Test-Path -LiteralPath $RuntimeDir)) {
    New-Item -ItemType Directory -Path $RuntimeDir -Force | Out-Null
}
if (Test-Path -LiteralPath $PidFile) {
    $oldPid = (Get-Content -LiteralPath $PidFile -ErrorAction SilentlyContinue | Select-Object -First 1)
    if ($oldPid -and ($oldPid -as [int])) {
        $oldProc = Get-Process -Id ([int]$oldPid) -ErrorAction SilentlyContinue
        if ($oldProc) {
            Fail ("检测到仍在运行的宿主（PID " + $oldPid + "）。请先执行 .\Stop-Trial.ps1。") 1
        }
    }
    Remove-Item -LiteralPath $PidFile -Force -ErrorAction SilentlyContinue
}

Add-Type -AssemblyName System.Data
$builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder($BaseConn)
$builder['Initial Catalog'] = $DbName
$script:FullConn = $builder.ConnectionString

# 后续 OpsTool 子命令从环境变量取连接串（避免出现在命令行上）。
$env:TRACEFLOW_TRIAL_CONN = $BaseConn

# ---------------------------------------------------------------- 3. 建库
Write-Host ("[1/4] 创建/确认库 " + $DbName + " ...")
& dotnet run --project $OpsToolProject -c Release -- create-db $DbName
if ($LASTEXITCODE -ne 0) { Fail ("create-db 失败（退出码 " + $LASTEXITCODE + "）。") $LASTEXITCODE }

# ---------------------------------------------------------------- 4. 迁移（DbMigrator 输出目录不含脚本，显式传脚本目录）
Write-Host "[2/4] 执行迁移（db/migrations 0001-0009）..."
& dotnet run --project $MigratorProject -c Release -- $script:FullConn $MigrationsDir
if ($LASTEXITCODE -ne 0) { Fail ("迁移失败（退出码 " + $LASTEXITCODE + "）。") $LASTEXITCODE }

# ---------------------------------------------------------------- 5. 管理员（可选）
if ($SeedAdminUser) {
    Write-Host ("[3/4] 初始化管理员 " + $SeedAdminUser + " ...")
    & dotnet run --project $OpsToolProject -c Release -- init-admin $SeedAdminUser --db $DbName
    if ($LASTEXITCODE -ne 0) { Fail ("init-admin 失败（退出码 " + $LASTEXITCODE + "）。") $LASTEXITCODE }
} else {
    Write-Host "[3/4] 跳过管理员初始化（库中尚无管理员时，稍后执行：dotnet run --project traceflow/src/TraceFlow.Host -c Release -- --init-admin）"
}

# ---------------------------------------------------------------- 6. 启动宿主
Write-Host ("[4/4] 启动宿主 127.0.0.1:" + $Port + "（env=MOCK）...")
$env:ConnectionStrings__TraceFlow = $script:FullConn
$env:TraceFlow__Env = 'MOCK'
$env:ASPNETCORE_URLS = ('http://127.0.0.1:' + $Port)
$env:ASPNETCORE_ENVIRONMENT = 'Production'
# 宿主 appsettings 的 AllowedHosts 只有 localhost，回环地址 127.0.0.1 会被 HostFiltering 以 400 拒绝。
# 试运行实例只绑 127.0.0.1，故显式放行回环主机名（仅本进程/子进程环境变量，不改仓库配置）。
$env:AllowedHosts = 'localhost;127.0.0.1'

$stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
$stdoutLog = Join-Path $RuntimeDir ('host-' + $stamp + '.log')
$stderrLog = Join-Path $RuntimeDir ('host-' + $stamp + '.err.log')

$hostArgs = 'run --project "' + $HostProject + '" -c Release --no-launch-profile -- --web-only'
$proc = Start-Process -FilePath 'dotnet' -ArgumentList $hostArgs -WorkingDirectory $RepoRoot `
    -RedirectStandardOutput $stdoutLog -RedirectStandardError $stderrLog -PassThru -WindowStyle Hidden

Set-Content -LiteralPath $PidFile -Value ([string]$proc.Id) -Encoding ASCII
$meta = [ordered]@{
    pid        = $proc.Id
    dbName     = $DbName
    port       = $Port
    startedUtc = (Get-Date).ToUniversalTime().ToString('o')
    stdoutLog  = $stdoutLog
    stderrLog  = $stderrLog
}
($meta | ConvertTo-Json) | Set-Content -LiteralPath $MetaFile -Encoding UTF8

# 健康轮询：≤60s
$healthUrl = ('http://127.0.0.1:' + $Port + '/api/v1/health')
$deadline = (Get-Date).AddSeconds(60)
$ready = $false
while ((Get-Date) -lt $deadline) {
    if ($proc.HasExited) { break }
    try {
        $resp = Invoke-WebRequest -Uri $healthUrl -UseBasicParsing -TimeoutSec 5
        if ($resp.StatusCode -eq 200) {
            $body = $resp.Content | ConvertFrom-Json
            if ($body.data -and $body.data.db -eq 'OK') { $ready = $true; break }
        }
    } catch {
        # 宿主尚未监听：继续轮询
    }
    Start-Sleep -Seconds 2
}

if (-not $ready) {
    Write-Host "[失败] 宿主未在 60s 内就绪（PID " + $proc.Id + "）。" -ForegroundColor Red
    Show-LogTail $stdoutLog
    Show-LogTail $stderrLog
    exit 1
}

# ---------------------------------------------------------------- 7. 就绪输出
Write-Host ""
Write-Host ("就绪：http://127.0.0.1:" + $Port + "（库 " + $DbName + "，env MOCK，PID " + $proc.Id + "）") -ForegroundColor Green
Write-Host ("日志：" + $stdoutLog)
Write-Host ("证据目录：" + $EvidenceDir)
Write-Host ""
Write-Host "后续命令："
Write-Host "  .\Collect-Evidence.ps1 -Once                     # 立即采样一次并写入 JSONL"
Write-Host "  .\Collect-Evidence.ps1 -IntervalMinutes 60 -Days 7   # 按 60 分钟周期采集 7 天（Ctrl-C 结束）"
Write-Host "  .\New-TrialSummary.ps1                           # 聚合 .runtime/evidence 生成 summary.md"
Write-Host "  .\Stop-Trial.ps1                                 # 停止宿主（保留库）"
Write-Host "  .\Stop-Trial.ps1 -DropDb                         # 停止宿主并删除 TraceFlow_Trial_* 库"
if (-not $SeedAdminUser) {
    Write-Host ""
    Write-Host "提示：若库中尚无管理员，请手动初始化（密码只从环境变量读取）：" -ForegroundColor Yellow
    Write-Host "  dotnet run --project traceflow/src/TraceFlow.Host -c Release -- --init-admin"
}
exit 0