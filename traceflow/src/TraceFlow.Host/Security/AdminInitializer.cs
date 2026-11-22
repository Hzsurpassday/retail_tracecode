using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using TraceFlow.Application.Security;

namespace TraceFlow.Host.Security;

public static class AdminInitializer
{
    public static async Task<int> RunAsync(string[] args, Microsoft.Extensions.Configuration.IConfiguration config)
    {
        var i = Array.IndexOf(args, "--init-admin");
        var username = i >= 0 && i + 1 < args.Length ? args[i + 1] : "";
        if (username.Length == 0) { Console.Error.WriteLine("用法: --init-admin <username>"); return 2; }
        Console.Write("密码（≥10位）: "); var p1 = ReadHidden();
        Console.Write("再次输入: "); var p2 = ReadHidden();
        if (p1 != p2 || p1.Length < 10) { Console.Error.WriteLine("两次不一致或长度不足"); return 2; }
        var conn = config.GetConnectionString("TraceFlow")!;
        var hasher = new PasswordHasher<string>();
        await using var c = new SqlConnection(conn);
        await c.OpenAsync();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        var id = await c.ExecuteScalarAsync<long>(
            "INSERT INTO dbo.app_user(username,display_name,password_hash) OUTPUT inserted.id VALUES(@u,@u,@h)",
            new { u = username, h = hasher.HashPassword(username, p1) }, tx);
        foreach (var perm in new[] { PermissionCatalog.SystemManage, PermissionCatalog.TraceRead, PermissionCatalog.AuditRead })
            await c.ExecuteAsync("INSERT INTO dbo.user_permission(user_id,permission,granted_by) VALUES(@id,@p,N'init')", new { id, p = perm }, tx);
        await c.ExecuteAsync(@"INSERT INTO dbo.audit_log(actor,actor_kind,action,object_type,object_id,reason)
                               VALUES(N'init',N'System',N'admin.init',N'app_user',@id,N'初始化管理员')", new { id = id.ToString() }, tx);
        await tx.CommitAsync();
        Console.WriteLine($"管理员 {username} 已创建（仅 system:manage/trace:read/audit:read，其余权限按需分配）");
        return 0;
    }

    private static string ReadHidden()
    {
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; }
            else if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }
}