using Dapper;
using Microsoft.Data.SqlClient;

namespace TraceFlow.IntegrationTests.Sources;

/// <summary>幂等共享种子：O1 组织 + KSOA 来源系统（每类独立测试库内 IF NOT EXISTS）。</summary>
public static class TestSeed
{
    public static async Task<(long OrgId, long SysId)> EnsureAsync(SqlConnection c)
    {
        var orgId = await c.ExecuteScalarAsync<long>(@"
IF NOT EXISTS (SELECT 1 FROM dbo.organization WHERE org_code = N'O1')
    INSERT INTO dbo.organization(org_code,name) VALUES(N'O1',N'店');
SELECT id FROM dbo.organization WHERE org_code = N'O1'");
        var sysId = await c.ExecuteScalarAsync<long>(@"
IF NOT EXISTS (SELECT 1 FROM dbo.source_system WHERE system_code = N'KSOA')
    INSERT INTO dbo.source_system(system_code,name,adapter_contract_version,config_json)
    VALUES(N'KSOA',N'KSOA ERP/POS',N'ksoa-fixture-1',N'{""mode"":""fixture""}');
SELECT id FROM dbo.source_system WHERE system_code = N'KSOA'");
        return (orgId, sysId);
    }
}
