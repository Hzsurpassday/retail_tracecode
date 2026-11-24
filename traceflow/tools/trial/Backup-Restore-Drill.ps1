#Requires -Version 5.1
<#
.SYNOPSIS
    备份/恢复真实演练（NFR-07 / NFR-08 / AT-072）：建演练库 -> 迁移 -> 种子标记 -> 备份 ->
    变更数据 -> 恢复 -> 断言 -> 写实测 JSON -> 清理。

.DESCRIPTION
    只操作 TraceFlow_Trial_Drill_<Stamp>（白名单 ^TraceFlow_(Trial|Perf)_[A-Za-z0-9_]{1,60}$ +
    前缀自检 + 保护库/测试库/系统库硬拒绝）；任何其它库名以退出码 2 拒绝。
    连接一律走 sqlcmd -E（集成认证）/ OpsTool 环境变量，脚本不回显任何连接串或密码。
    建库前先用 DB_ID 预检：库已存在（-Stamp 重复）时直接 exit 2，绝不复用或删除既有库
    （OpsTool create-db 是幂等的，不预检就会静默复用旧库）。
    finally 块保证：库先回到 MULTI_USER 再删除（除非 -Keep；-Keep 时也确保库回到 MULTI_USER
    以可正常使用），未由本次运行创建的库永不删除，并清除本脚本设置的连接环境变量。
    .bak 默认写到 traceflow/tools/trial/.runtime/（git-ignored）；该目录不可写时回退到实例默认备份目录。

    事实边界：本脚本验证的是数据库层备份/恢复，不涉及平台（G-TOP）与 LIVE 对账流程；
    SQL Server 2008 R2 未在本仓库验证（AT-070）。

.PARAMETER ServerInstance
    SQL Server 实例名，默认 localhost。

.PARAMETER Base
    基础连接串（不含 Initial Catalog），缺省取环境变量 TRACEFLOW_TRIAL_CONN。

.PARAMETER Stamp
    演练标识，默认 UTC yyyyMMddHHmmss；仅允许字母/数字/下划线。

.PARAMETER Keep
    保留演练库（默认结束后删除；仅删除本次创建且匹配演练前缀的库）。

.PARAMETER KeepBackup
    保留 .bak 文件（默认删除）。

.PARAMETER Out
    结果 JSON 路径（默认仓库根 docs/delivery/backup-restore-drill.json，解析为绝对路径并打印）。

.EXAMPLE
    $env:TRACEFLOW_TRIAL_CONN = 'Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true'
    .\Backup-Restore-Drill.ps1
#>
[CmdletBinding()]
param(
    [string]$ServerInstance = 'localhost',
    [string]$Base = $env:TRACEFLOW_TRIAL_CONN,
    [string]$Stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss'),
    [switch]$Keep,
    [switch]$KeepBackup,
    [string]$Out
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 控制台输出统一 UTF-8（脚本文件为 UTF-8 BOM），避免重定向日志时中文乱码。
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = (Resolve-Path (Join-Path $ScriptDir '..\..\..')).Path
$RuntimeDir = Join-Path $ScriptDir '.runtime'
$OpsToolProject = Join-Path $RepoRoot 'traceflow\tools\TraceFlow.OpsTool'
$MigratorProject = Join-Path $RepoRoot 'traceflow\db\tools\TraceFlow.DbMigrator'
$MigrationsDir = Join-Path $RepoRoot 'traceflow\db\migrations'
if ([string]::IsNullOrWhiteSpace($Out)) {
    $Out = Join-Path (Join-Path $RepoRoot 'docs\delivery') 'backup-restore-drill.json'
}
$Out = [System.IO.Path]::GetFullPath($Out)

# ---------------------------------------------------------------- 守卫（与 OpsTool 同规则）
$NamePattern = '^TraceFlow_(Trial|Perf)_[A-Za-z0-9_]{1,60}$'
$DrillPrefix = 'TraceFlow_Trial_Drill_'
$ProtectedNames = @('TraceFlow_Dev', 'TraceFlow_M2Manual', 'TraceFlow_M2Gate', 'TraceFlow_M3Ui')
$SystemNames = @('master', 'model', 'msdb', 'tempdb')

$DbName = $DrillPrefix + $Stamp

function Reject([string]$Message) {
    Write-Host ("[拒绝] " + $Message) -ForegroundColor Red
    exit 2
}

function Hide-SecretText([string]$Text) {
    if ([string]::IsNullOrEmpty($Text)) { return $Text }
    $t = $Text
    if ($Base) { $t = $t.Replace($Base, '<conn>') }
    $t = [regex]::Replace($t, '(?i)(password|pwd)\s*=\s*[^;]*', '$1=***')
    return $t
}

if ([string]::IsNullOrWhiteSpace($Base)) {
    Reject '缺少基础连接串：请传 -Base 或设置环境变量 TRACEFLOW_TRIAL_CONN（不含 Initial Catalog）。'
}
if ([string]::IsNullOrWhiteSpace($Stamp) -or $Stamp -cnotmatch '^[A-Za-z0-9_]{1,40}$') {
    Reject ("Stamp 只允许字母/数字/下划线（1-40 位）。实际：" + $Stamp)
}
if ($DbName -cnotmatch $NamePattern) {
    Reject ("库名不合法（必须匹配 " + $NamePattern + "）：" + $DbName)
}
if (-not $DbName.StartsWith($DrillPrefix, [System.StringComparison]::Ordinal)) {
    Reject ("演练库名必须以 " + $DrillPrefix + " 开头：" + $DbName)
}
foreach ($p in $ProtectedNames) {
    if ([string]::Equals($DbName, $p, [System.StringComparison]::OrdinalIgnoreCase)) {
        Reject ("拒绝操作保护库：" + $DbName)
    }
}
foreach ($s in $SystemNames) {
    if ([string]::Equals($DbName, $s, [System.StringComparison]::OrdinalIgnoreCase)) {
        Reject ("拒绝操作系统库：" + $DbName)
    }
}
if ($DbName.StartsWith('TraceFlow_Test_', [System.StringComparison]::OrdinalIgnoreCase)) {
    Reject ("拒绝操作集成测试库：" + $DbName)
}
if (-not (Get-Command sqlcmd -ErrorAction SilentlyContinue)) {
    Reject '找不到 sqlcmd（需在 PATH 中，或使用 SQL Server 客户端工具目录）。'
}

# ---------------------------------------------------------------- 辅助
function Invoke-SqlBatch {
    param([string]$Database, [string]$Query, [string]$File)
    # -I 开启 QUOTED_IDENTIFIER（筛选索引/索引视图要求的会话选项，sqlcmd 默认 OFF）。
    $cmdArgs = @('-S', $ServerInstance, '-E', '-b', '-I', '-h', '-1', '-W', '-d', $Database)
    if ($File) { $cmdArgs += @('-i', $File, '-f', '65001') } else { $cmdArgs += @('-Q', $Query) }
    $raw = & sqlcmd @cmdArgs 2>&1
    $code = $LASTEXITCODE
    $text = (@($raw) | ForEach-Object { [string]$_ }) -join "`n"
    return [pscustomobject]@{ ExitCode = $code; Text = $text.Trim() }
}

function Assert-SqlOk {
    param([pscustomobject]$Result, [string]$What)
    if ($Result.ExitCode -ne 0) {
        throw ($What + " 失败（退出码 " + $Result.ExitCode + "）：" + (Hide-SecretText $Result.Text))
    }
}

function Get-Scalar {
    param([string]$Database, [string]$Query, [string]$What)
    $r = Invoke-SqlBatch -Database $Database -Query $Query
    Assert-SqlOk $r $What
    $lines = @($r.Text -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 })
    if ($lines.Count -lt 1) { throw ($What + ' 无返回值（期望标量）。') }
    return $lines[$lines.Count - 1].Trim()
}

function Write-SqlFile {
    param([string]$Path, [string]$Content)
    [System.IO.File]::WriteAllText($Path, $Content, (New-Object System.Text.UTF8Encoding($false)))
}

function New-SqlPath([string]$Name) {
    return (Join-Path $RuntimeDir ($Name + '-' + $Stamp + '.sql'))
}

function Add-Check {
    param([string]$Name, [string]$Expected, [string]$Actual, [string]$Method, [bool]$Pass)
    [void]$script:Checks.Add([ordered]@{
        name     = $Name
        expected = $Expected
        actual   = $Actual
        method   = $Method
        pass     = $Pass
    })
    $color = 'Green'
    if (-not $Pass) { $color = 'Red' }
    $label = 'PASS'
    if (-not $Pass) { $label = 'FAIL' }
    Write-Host ("  [" + $label + "] " + $Name + "：expected=" + $Expected + " actual=" + $Actual) -ForegroundColor $color
}

function Invoke-Dotnet([string]$Project, [string[]]$Arguments) {
    $all = @('run', '--project', $Project, '-c', 'Release', '--') + $Arguments
    $out = & dotnet @all 2>&1
    $code = $LASTEXITCODE
    $text = (@($out) | ForEach-Object { [string]$_ }) -join "`n"
    return [pscustomobject]@{ ExitCode = $code; Text = $text.Trim() }
}

# ---------------------------------------------------------------- 状态
$script:DbCreated = $false
$script:BackupPath = $null
$script:Checks = New-Object System.Collections.ArrayList
$exitCode = 1
$startedAt = Get-Date

Write-Host ("[drill] 演练库 " + $DbName + "（实例 " + $ServerInstance + "）")
Write-Host ("[drill] 结果 JSON（绝对路径）：" + $Out)

try {
    # ------------------------------------------------------------ 0. 环境准备
    if (-not (Test-Path -LiteralPath $RuntimeDir)) {
        New-Item -ItemType Directory -Path $RuntimeDir -Force | Out-Null
    }
    if (-not (Test-Path -LiteralPath (Split-Path -Parent $Out))) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $Out) -Force | Out-Null
    }

    # ------------------------------------------------------------ 1. 引擎版本（真实读取）
    Write-Host '[1/8] 读取 SQL Server 版本 ...'
    $productVersion = Get-Scalar -Database 'master' -Query "SET NOCOUNT ON; SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64))" -What '读取 ProductVersion'
    $versionLine = Get-Scalar -Database 'master' -Query "SET NOCOUNT ON; SELECT LEFT(@@VERSION, CHARINDEX(CHAR(10), @@VERSION) - 1)" -What '读取 @@VERSION'
    $serverVersion = ($versionLine + ' (ProductVersion ' + $productVersion + ')')
    $engineIs2008R2 = $productVersion.StartsWith('10.50.', [System.StringComparison]::Ordinal)
    Write-Host ("  " + $serverVersion)
    if (-not $engineIs2008R2) {
        Write-Host '  [说明] 当前引擎不是 SQL Server 2008 R2；本次结果只代表当前引擎（2008 R2 未验证，AT-070）。' -ForegroundColor Yellow
    }

    # ------------------------------------------------------------ 2. 建库 + 迁移
    Write-Host '[2/8] 预检演练库是否已存在（DB_ID），再创建并执行迁移 0001-0009 ...'
    # create-db 是幂等的（库已存在时退出码 0），若 -Stamp 重复会静默复用旧库：
    # 那既让本次断言毫无意义，也让 finally 的"只删本次创建"判断失真——故先显式预检并拒绝。
    $dbIdState = Get-Scalar -Database 'master' -Query ("SET NOCOUNT ON; SELECT CASE WHEN DB_ID(N'" + $DbName + "') IS NULL THEN N'ABSENT' ELSE N'PRESENT' END") -What '预检演练库是否已存在（DB_ID）'
    if ($dbIdState -ne 'ABSENT') {
        Write-Host ('[拒绝] 演练库已存在（-Stamp 重复）；拒绝复用/删除，请换 -Stamp 或先人工确认：' + $DbName) -ForegroundColor Red
        exit 2
    }
    Write-Host '  预检通过：演练库不存在，将由本次运行创建。'

    $env:TRACEFLOW_TRIAL_CONN = $Base
    $create = Invoke-Dotnet $OpsToolProject @('create-db', $DbName)
    if ($create.ExitCode -ne 0) {
        throw ('OpsTool create-db 失败（退出码 ' + $create.ExitCode + '）：' + (Hide-SecretText $create.Text))
    }
    Write-Host ('  ' + (Hide-SecretText $create.Text))
    # 仅"预检证明不存在 + create-db 退出 0"后才登记"本次创建"；finally 只删这里登记过的库。
    $script:DbCreated = $true

    $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder($Base)
    $builder['Initial Catalog'] = $DbName
    $env:TRACEFLOW_MIGRATE_CONN = $builder.ConnectionString
    $migrate = Invoke-Dotnet $MigratorProject @('--dir', $MigrationsDir)
    if ($migrate.ExitCode -ne 0) {
        throw ('迁移失败（退出码 ' + $migrate.ExitCode + '）：' + (Hide-SecretText $migrate.Text))
    }
    $applied = Get-Scalar -Database $DbName -Query "SET NOCOUNT ON; SELECT CAST(COUNT(*) AS nvarchar(10)) FROM dbo.schema_version WHERE success = 1" -What '统计已应用迁移'
    Write-Host ('  迁移成功行数：' + $applied)

    # ------------------------------------------------------------ 3. 种子标记（备份前）
    Write-Host '[3/8] 写入备份前标记数据 ...'
    $preHash = '0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF'
    $postHash = 'FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210'
    $seedSql = @'
SET NOCOUNT ON;
DECLARE @org bigint, @task bigint;
INSERT INTO dbo.organization(org_code, name) VALUES (N'DRILL-{{STAMP}}', N'演练门店');
SET @org = SCOPE_IDENTITY();
INSERT INTO dbo.audit_log(actor, actor_kind, action, object_type, object_id, reason, env)
VALUES (N'drill-script', N'SYSTEM', N'drill.marker', N'drill', N'pre-backup', N'备份/恢复演练：备份前标记', N'LIVE');
INSERT INTO dbo.upload_task(org_id, env, event_key, task_type, source_hash, payload_hash, state)
VALUES (@org, N'MOCK', N'DRILL-{{STAMP}}', N'RETAIL_321', '{{PREHASH}}', '{{POSTHASH}}', N'Succeeded');
SET @task = SCOPE_IDENTITY();
SELECT CAST(@org AS nvarchar(20)) + N'|' + CAST(@task AS nvarchar(20));
'@
    $seedSql = $seedSql.Replace('{{STAMP}}', $Stamp).Replace('{{PREHASH}}', $preHash).Replace('{{POSTHASH}}', $postHash)
    $seedFile = New-SqlPath 'drill-seed'
    Write-SqlFile $seedFile $seedSql
    $seed = Invoke-SqlBatch -Database $DbName -File $seedFile
    Assert-SqlOk $seed '写入备份前标记'
    $seedIds = @($seed.Text -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 })
    if ($seedIds.Count -lt 1) { throw '种子标记未返回 org_id|task_id。' }
    $idParts = $seedIds[$seedIds.Count - 1].Trim().Split('|')
    if ($idParts.Count -ne 2) { throw ('种子标记返回格式异常：' + $seed.Text) }
    $preOrgId = $idParts[0]
    $preTaskId = $idParts[1]
    Write-Host ('  种子完成：org_id=' + $preOrgId + ' preTaskId=' + $preTaskId)

    # ------------------------------------------------------------ 4. 备份 + VERIFYONLY
    Write-Host '[4/8] 备份（COPY_ONLY + CHECKSUM）并 VERIFYONLY ...'
    $defaultBackupDir = Get-Scalar -Database 'master' -Query "SET NOCOUNT ON; SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(400))" -What '读取默认备份目录'
    $backupDirs = @($RuntimeDir)
    if ($defaultBackupDir -and $defaultBackupDir -ne $RuntimeDir) { $backupDirs += $defaultBackupDir }

    $backupResult = $null
    $backupFile = Join-Path $RuntimeDir ($DbName + '.bak')
    foreach ($dir in $backupDirs) {
        $candidate = Join-Path $dir ($DbName + '.bak')
        $backupSql = "BACKUP DATABASE [$DbName] TO DISK = N'$candidate' WITH INIT, COPY_ONLY, CHECKSUM, NAME = N'TraceFlow drill $Stamp', DESCRIPTION = N'backup/restore drill (copy-only, checksum)';"
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $r = Invoke-SqlBatch -Database 'master' -Query $backupSql
        $sw.Stop()
        if ($r.ExitCode -eq 0) {
            $backupResult = $r
            $backupFile = $candidate
            $backupMs = [math]::Round($sw.Elapsed.TotalMilliseconds, 1)
            break
        } else {
            Write-Host ('  备份到 ' + $dir + ' 失败（退出码 ' + $r.ExitCode + '）：' + (Hide-SecretText $r.Text)) -ForegroundColor Yellow
        }
    }
    if ($null -eq $backupResult) {
        throw ('BACKUP DATABASE 在所有候选目录均失败（服务账号可能无写权限）。请检查 SQL Server 服务账号对 ' + ($backupDirs -join ' / ') + ' 的写权限。')
    }
    $script:BackupPath = $backupFile
    if (-not (Test-Path -LiteralPath $backupFile)) { throw ('备份命令返回成功但找不到备份文件：' + $backupFile) }
    $backupBytes = (Get-Item -LiteralPath $backupFile).Length
    Write-Host ('  备份文件：' + $backupFile + '（' + $backupBytes + ' 字节，' + $backupMs + ' ms）')

    $verifySql = "RESTORE VERIFYONLY FROM DISK = N'$backupFile' WITH CHECKSUM;"
    $verify = Invoke-SqlBatch -Database 'master' -Query $verifySql
    Assert-SqlOk $verify 'RESTORE VERIFYONLY'
    Write-Host ('  VERIFYONLY 通过：' + (Hide-SecretText $verify.Text))

    # ------------------------------------------------------------ 5. 备份后变更（应被恢复丢弃）
    Write-Host '[5/8] 备份后变更数据（post 标记 / 删除 pre 标记 / 改 organization 名称）...'
    $mutateSql = @'
SET NOCOUNT ON;
INSERT INTO dbo.audit_log(actor, actor_kind, action, object_type, object_id, reason, env)
VALUES (N'drill-script', N'SYSTEM', N'drill.marker', N'drill', N'post-backup', N'备份/恢复演练：备份后标记（恢复后应消失）', N'LIVE');
DELETE FROM dbo.audit_log WHERE action = N'drill.marker' AND object_type = N'drill' AND object_id = N'pre-backup';
UPDATE dbo.organization SET name = N'演练门店-已变更' WHERE org_code = N'DRILL-{{STAMP}}';
'@
    $mutateSql = $mutateSql.Replace('{{STAMP}}', $Stamp)
    $mutateFile = New-SqlPath 'drill-mutate'
    Write-SqlFile $mutateFile $mutateSql
    $mutate = Invoke-SqlBatch -Database $DbName -File $mutateFile
    Assert-SqlOk $mutate '写入备份后变更'
    $midPre = Get-Scalar -Database $DbName -Query "SET NOCOUNT ON; SELECT CAST(COUNT(*) AS nvarchar(10)) FROM dbo.audit_log WHERE action = N'drill.marker' AND object_id = N'pre-backup'" -What '校验变更已生效'
    $midPost = Get-Scalar -Database $DbName -Query "SET NOCOUNT ON; SELECT CAST(COUNT(*) AS nvarchar(10)) FROM dbo.audit_log WHERE action = N'drill.marker' AND object_id = N'post-backup'" -What '校验变更已生效'
    Write-Host ('  变更后：pre=' + $midPre + ' post=' + $midPost + '（恢复前）')
    if ($midPre -ne '0' -or $midPost -ne '1') {
        throw ('备份后变更未按预期生效（pre=' + $midPre + ', post=' + $midPost + '），恢复断言将不成立。')
    }

    # ------------------------------------------------------------ 6. 恢复
    Write-Host '[6/8] 单用户模式恢复（REPLACE + RECOVERY），再回多用户 ...'
    $single = Invoke-SqlBatch -Database 'master' -Query "ALTER DATABASE [$DbName] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;"
    Assert-SqlOk $single 'SET SINGLE_USER'
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $restore = Invoke-SqlBatch -Database 'master' -Query "RESTORE DATABASE [$DbName] FROM DISK = N'$backupFile' WITH REPLACE, RECOVERY, CHECKSUM;"
    $sw.Stop()
    $restoreMs = [math]::Round($sw.Elapsed.TotalMilliseconds, 1)
    # 无论恢复成功与否，先回到多用户（finally 还会再兜一次）。
    $multi = Invoke-SqlBatch -Database 'master' -Query "ALTER DATABASE [$DbName] SET MULTI_USER;"
    Assert-SqlOk $restore 'RESTORE DATABASE'
    Assert-SqlOk $multi 'SET MULTI_USER'
    Write-Host ('  恢复完成（' + $restoreMs + ' ms）：' + (Hide-SecretText $restore.Text))

    $dbcc = Invoke-SqlBatch -Database 'master' -Query "DBCC CHECKDB(N'$DbName') WITH NO_INFOMSGS;"
    Assert-SqlOk $dbcc 'DBCC CHECKDB'
    Write-Host '  DBCC CHECKDB 无错误。'

    # ------------------------------------------------------------ 7. 断言（标量逐项比较）
    Write-Host '[7/8] 恢复后断言 ...'
    $preMarkers = Get-Scalar -Database $DbName -Query "SET NOCOUNT ON; SELECT CAST(COUNT(*) AS nvarchar(10)) FROM dbo.audit_log WHERE action = N'drill.marker' AND object_type = N'drill' AND object_id = N'pre-backup' AND env = 'LIVE'" -What '断言 pre 标记'
    Add-Check 'pre_backup_marker_present' '1' $preMarkers 'sqlcmd -h -1 -W COUNT(*) audit_log pre-backup' ($preMarkers -eq '1')

    $postMarkers = Get-Scalar -Database $DbName -Query "SET NOCOUNT ON; SELECT CAST(COUNT(*) AS nvarchar(10)) FROM dbo.audit_log WHERE action = N'drill.marker' AND object_type = N'drill' AND object_id = N'post-backup'" -What '断言 post 标记'
    Add-Check 'post_backup_marker_absent' '0' $postMarkers 'sqlcmd -h -1 -W COUNT(*) audit_log post-backup' ($postMarkers -eq '0')

    $orgCase = "SET NOCOUNT ON; SELECT CASE WHEN name = N'演练门店' THEN 'MATCH' ELSE 'MISMATCH' END FROM dbo.organization WHERE org_code = N'DRILL-" + $Stamp + "'"
    $orgCheck = Get-Scalar -Database $DbName -Query $orgCase -What '断言 organization 名称'
    Add-Check 'organization_name_restored' 'MATCH' $orgCheck 'sqlcmd -h -1 -W organization.name = 演练门店' ($orgCheck -eq 'MATCH')

    $taskCheck = Get-Scalar -Database $DbName -Query "SET NOCOUNT ON; SELECT CAST(COUNT(*) AS nvarchar(10)) FROM dbo.upload_task WHERE id = $preTaskId AND env = N'MOCK' AND task_type = N'RETAIL_321' AND state = N'Succeeded' AND event_key = N'DRILL-$Stamp'" -What '断言 upload_task'
    Add-Check 'upload_task_same_pre_id_present' '1' $taskCheck 'sqlcmd -h -1 -W COUNT(*) upload_task id=preTaskId' ($taskCheck -eq '1')

    $sv = Get-Scalar -Database $DbName -Query "SET NOCOUNT ON; SELECT CAST(COUNT(*) AS nvarchar(10)) + N'|' + CAST(SUM(CASE WHEN success = 1 THEN 1 ELSE 0 END) AS nvarchar(10)) FROM dbo.schema_version" -What '断言 schema_version'
    $svParts = $sv.Split('|')
    Add-Check 'schema_version_rows_and_success' '9|9' $sv 'sqlcmd -h -1 -W COUNT(*)|SUM(success=1) schema_version' ($sv -eq '9|9')
    $schemaRows = $svParts[0]
    $schemaSuccess = $svParts[1]

    Add-Check 'restore_verifyonly_checksum' 'exit 0' ('exit ' + $verify.ExitCode) 'sqlcmd -b RESTORE VERIFYONLY WITH CHECKSUM' ($verify.ExitCode -eq 0)
    Add-Check 'dbcc_checkdb_clean' 'exit 0' ('exit ' + $dbcc.ExitCode) 'sqlcmd -b DBCC CHECKDB WITH NO_INFOMSGS' ($dbcc.ExitCode -eq 0)
    Add-Check 'backup_file_nonempty' '>0' ([string]$backupBytes) 'Get-Item Length' ($backupBytes -gt 0)

    $failed = @($script:Checks | Where-Object { -not $_.pass })
    if ($failed.Count -gt 0) {
        throw ('恢复后断言未全部通过（失败 ' + $failed.Count + ' 项）。')
    }

    # ------------------------------------------------------------ 8. 写 JSON（实测值）
    Write-Host '[8/8] 写入实测 JSON ...'
    $finishedAt = Get-Date
    $result = [ordered]@{
        generatedAtUtc        = $finishedAt.ToUniversalTime().ToString('o')
        serverVersion         = $serverVersion
        productVersion        = $productVersion
        engineVerified2008R2  = [bool]$engineIs2008R2
        dbName                = $DbName
        databaseCreatedByRun  = $script:DbCreated
        backupPath            = $backupFile
        backupFileBytes       = [long]$backupBytes
        backupDurationMs      = [double]$backupMs
        restoreDurationMs     = [double]$restoreMs
        restoreVerified       = $true
        backupVerifyOnlyOk    = ($verify.ExitCode -eq 0)
        dbccCheckDbClean      = ($dbcc.ExitCode -eq 0)
        preMarkerRestored     = ($preMarkers -eq '1')
        postMarkerAbsent      = ($postMarkers -eq '0')
        organizationRestored  = ($orgCheck -eq 'MATCH')
        preTaskId             = [long]$preTaskId
        preOrgId              = [long]$preOrgId
        preTaskRestored       = ($taskCheck -eq '1')
        schemaVersions        = [int]$schemaRows
        schemaVersionSuccess  = [int]$schemaSuccess
        appliedMigrations     = [int]$applied
        elapsedSeconds        = [math]::Round(($finishedAt - $startedAt).TotalSeconds, 1)
        checks                = $script:Checks
        backupOutput          = (Hide-SecretText $backupResult.Text)
        verifyOutput          = (Hide-SecretText $verify.Text)
        restoreOutput         = (Hide-SecretText $restore.Text)
        notes                 = @(
            '本次演练在真实本机实例执行；若 productVersion 不是 10.50.x，则 SQL Server 2008 R2 未验证（AT-070 未完成），不构成 2008 R2 证据。',
            '演练只覆盖数据库层备份/恢复（BACKUP COPY_ONLY + CHECKSUM / RESTORE VERIFYONLY / RESTORE WITH REPLACE, RECOVERY / DBCC CHECKDB）；未执行任何平台（G-TOP）调用。',
            'NFR-08 恢复后顺序（保持发送停止 -> 先对账 -> 修复差异/补录证据 -> 再开放发送）需在目标环境按 docs/delivery/backup-restore.md 执行，脚本不覆盖该流程。',
            'LIVE 平台对账与真实事务未执行；G-TOP/G-ERP 未取证。'
        )
    }
    $json = ($result | ConvertTo-Json -Depth 6)
    [System.IO.File]::WriteAllText($Out, $json + "`n", (New-Object System.Text.UTF8Encoding($false)))
    Write-Host ('  已写入：' + $Out) -ForegroundColor Green

    $exitCode = 0
}
catch {
    Write-Host ('[失败] ' + (Hide-SecretText $_.Exception.Message)) -ForegroundColor Red
    $exitCode = 1
}
finally {
    if ($script:DbCreated) {
        if ($Keep) {
            # -Keep：库要留下来给人用，必须保证不是 SINGLE_USER 残留（第 6 步恢复时曾置单用户，
            # 中途失败会停在单用户）。先确认库确实存在，再回多用户。
            try {
                $exists = Get-Scalar -Database 'master' -Query ("SET NOCOUNT ON; SELECT CASE WHEN DB_ID(N'" + $DbName + "') IS NULL THEN N'ABSENT' ELSE N'PRESENT' END") -What '清理时确认演练库存在'
                if ($exists -eq 'PRESENT') {
                    $r = Invoke-SqlBatch -Database 'master' -Query "ALTER DATABASE [$DbName] SET MULTI_USER;"
                    if ($r.ExitCode -ne 0) { Write-Host ('[清理] -Keep 下 MULTI_USER 未成功：' + (Hide-SecretText $r.Text)) -ForegroundColor Yellow }
                } else {
                    Write-Host '[清理] 演练库已不存在（无需恢复多用户）。' -ForegroundColor Yellow
                }
            } catch {
                Write-Host ('[清理] -Keep 下 MULTI_USER 异常：' + (Hide-SecretText $_.Exception.Message)) -ForegroundColor Yellow
            }
            Write-Host ('[清理] -Keep 指定：保留演练库 ' + $DbName)
        } else {
            # 先确保多用户，再删除（删除只针对本次创建且匹配演练前缀的库）。
            if ($DbName.StartsWith($DrillPrefix, [System.StringComparison]::Ordinal) -and $DbName -cmatch $NamePattern) {
                try {
                    $r = Invoke-SqlBatch -Database 'master' -Query "ALTER DATABASE [$DbName] SET MULTI_USER WITH ROLLBACK IMMEDIATE;"
                    if ($r.ExitCode -ne 0) { Write-Host ('[清理] MULTI_USER 未成功（可能库已离线）：' + (Hide-SecretText $r.Text)) -ForegroundColor Yellow }
                } catch {
                    Write-Host ('[清理] MULTI_USER 异常：' + (Hide-SecretText $_.Exception.Message)) -ForegroundColor Yellow
                }
                try {
                    $drop = Invoke-Dotnet $OpsToolProject @('drop-db', $DbName, '--yes')
                    if ($drop.ExitCode -eq 0) {
                        Write-Host ('[清理] 已删除演练库 ' + $DbName)
                    } else {
                        Write-Host ('[清理] drop-db 失败（退出码 ' + $drop.ExitCode + '）：' + (Hide-SecretText $drop.Text) + '。请手动核对后删除。') -ForegroundColor Yellow
                    }
                } catch {
                    Write-Host ('[清理] drop-db 异常：' + (Hide-SecretText $_.Exception.Message)) -ForegroundColor Yellow
                }
            } else {
                Write-Host '[清理] 库名不再匹配演练前缀/白名单，拒绝删除。' -ForegroundColor Yellow
            }
        }
    } else {
        Write-Host '[清理] 演练库未由本次运行创建，跳过删除。'
    }

    if ($script:BackupPath -and (Test-Path -LiteralPath $script:BackupPath)) {
        if ($KeepBackup) {
            Write-Host ('[清理] -KeepBackup 指定：保留备份文件 ' + $script:BackupPath)
        } else {
            Remove-Item -LiteralPath $script:BackupPath -Force -ErrorAction SilentlyContinue
            Write-Host ('[清理] 已删除备份文件 ' + $script:BackupPath)
        }
    }

    foreach ($f in @((New-SqlPath 'drill-seed'), (New-SqlPath 'drill-mutate'))) {
        if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue }
    }

    # 释放连接：清除本脚本设置的两个环境变量（避免污染父进程以外的后续调用）。
    Remove-Item Env:\TRACEFLOW_MIGRATE_CONN -ErrorAction SilentlyContinue
    Remove-Item Env:\TRACEFLOW_TRIAL_CONN -ErrorAction SilentlyContinue
}

if ($exitCode -eq 0) {
    Write-Host ''
    Write-Host ('[drill] 结论 PASS：备份/恢复断言全部通过（引擎 ' + $productVersion + '）。') -ForegroundColor Green
    Write-Host ('[drill] 实测 JSON：' + $Out)
} else {
    Write-Host ''
    Write-Host '[drill] 结论 FAIL：详见上方失败信息（JSON 未写出，避免留下失败伪装为成功的证据）。' -ForegroundColor Red
}
exit $exitCode