using TraceFlow.Domain.Billing;
using Xunit;

namespace TraceFlow.UnitTests.Billing;

public class BillCodeGeneratorTests
{
    private static readonly BillCodeContract Contract = new("TF", 32);

    [Fact]
    public void Deterministic_for_same_task_and_part()
    {
        Assert.Equal(BillCodeGenerator.Generate(Contract, 12345, 0),
                     BillCodeGenerator.Generate(Contract, 12345, 0));
    }

    [Fact]
    public void Different_tasks_different_codes()
    {
        Assert.NotEqual(BillCodeGenerator.Generate(Contract, 1, 0),
                        BillCodeGenerator.Generate(Contract, 2, 0));
    }

    [Fact]
    public void Parts_have_stable_suffix_and_no_suffix_for_whole_bill()
    {
        var whole = BillCodeGenerator.Generate(Contract, 12345, 0);
        var p1 = BillCodeGenerator.Generate(Contract, 12345, 1);
        var p2 = BillCodeGenerator.Generate(Contract, 12345, 2);
        Assert.DoesNotContain("P", whole);
        Assert.Equal(whole + "P1", p1);
        Assert.Equal(whole + "P2", p2);
    }

    [Fact]
    public void Alphanumeric_only()
    {
        var code = BillCodeGenerator.Generate(Contract, long.MaxValue, 3);
        Assert.Matches("^[0-9A-Z]+$", code);
    }

    [Fact]
    public void Over_length_throws_not_truncates()
    {
        var tight = new BillCodeContract("TF", 5);
        Assert.Throws<InvalidOperationException>(() => BillCodeGenerator.Generate(tight, long.MaxValue, 0));
    }

    [Fact]
    public void Invalid_contract_rejected()
    {
        Assert.Throws<ArgumentException>(() => BillCodeGenerator.Generate(new BillCodeContract("tf-", 32), 1, 0));
        Assert.Throws<ArgumentException>(() => BillCodeGenerator.Generate(new BillCodeContract("TF", 0), 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => BillCodeGenerator.Generate(Contract, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => BillCodeGenerator.Generate(Contract, 1, -1));
    }

    // I-1 回归：旧 Base36 字母表含分隔符 'P'，Base36(2197)="1P1" 与 taskId=1/part=1 的 "1"+"P1" 碰撞。
    // Base35（剔除 P）下 2197 = 1·35² + 27·35 + 27 → 数字 1,S,S → "1SS"，碰撞消除。
    [Fact]
    public void Separator_collision_regression_base35()
    {
        Assert.Equal("TF1SS", BillCodeGenerator.Generate(Contract, 2197, 0)); // 1·1225 + 27·35 + 27 = 2197
        Assert.Equal("TF1P1", BillCodeGenerator.Generate(Contract, 1, 1));
        Assert.NotEqual(BillCodeGenerator.Generate(Contract, 2197, 0),
                        BillCodeGenerator.Generate(Contract, 1, 1));
        // 12345 = 10·35² + 2·35 + 25 → A(10),2,Q(25)（P 剔除后索引 25 = 'Q'）
        Assert.Equal("TFA2Q", BillCodeGenerator.Generate(Contract, 12345, 0));
    }

    [Fact]
    public void Body_never_contains_separator_P()
    {
        for (long taskId = 1; taskId <= 1000; taskId++)
        {
            var code = BillCodeGenerator.Generate(Contract, taskId, 0);
            var body = code["TF".Length..];
            Assert.DoesNotContain('P', body);
        }
    }

    [Fact]
    public void Uniqueness_sweep_100k_tasks_4_parts()
    {
        var codes = new HashSet<string>(400_000);
        for (long taskId = 1; taskId <= 100_000; taskId++)
            for (var part = 0; part <= 3; part++)
                codes.Add(BillCodeGenerator.Generate(Contract, taskId, part));
        Assert.Equal(400_000, codes.Count);
    }
}
