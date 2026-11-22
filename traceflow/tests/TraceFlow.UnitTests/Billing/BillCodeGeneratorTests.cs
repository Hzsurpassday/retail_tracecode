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
}