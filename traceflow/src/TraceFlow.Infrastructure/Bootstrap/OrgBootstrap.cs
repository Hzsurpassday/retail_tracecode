using Dapper;
using Microsoft.Data.SqlClient;

namespace TraceFlow.Infrastructure.Bootstrap;

/// <summary>
/// 单门店引导（幂等）：仅当不存在 active 组织时插入 DEFAULT 组织（UX_organization_single_active 保证单活）；
/// KSOA 来源系统 IF NOT EXISTS。已有数据一律不动（不覆盖用户配置）。
/// 属基础设施种子而非业务变更，不写审计；web/worker 两种模式启动都会经过。
/// </summary>
public static class OrgBootstrap
{
    public static async Task EnsureAsync(string connectionString, CancellationToken ct = default)
    {
        await using var c = new SqlConnection(connectionString);
        await c.OpenAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(@"
IF NOT EXISTS (SELECT 1 FROM dbo.organization WHERE is_active = 1)
    INSERT INTO dbo.organization(org_code, name) VALUES (N'DEFAULT', N'默认门店');
IF NOT EXISTS (SELECT 1 FROM dbo.source_system WHERE system_code = N'KSOA')
    INSERT INTO dbo.source_system(system_code, name, adapter_contract_version, config_json)
    VALUES (N'KSOA', N'KSOA ERP/POS', N'ksoa-fixture-1', N'{""mode"":""fixture"",""fixtureDir"":""fixtures/ksoa""}');",
            cancellationToken: ct));
    }
}
