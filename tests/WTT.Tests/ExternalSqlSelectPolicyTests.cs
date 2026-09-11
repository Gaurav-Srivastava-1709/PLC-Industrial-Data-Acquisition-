using FluentAssertions;
using WTT.Domain.Exceptions;
using WTT.Infrastructure.Services;
using Xunit;

namespace WTT.Tests;

public sealed class ExternalSqlSelectPolicyTests
{
    private readonly ExternalSqlSelectPolicy _policy = new();

    [Theory]
    [InlineData("SELECT TOP (1) Name FROM dbo.Products ORDER BY Id")]
    [InlineData("WITH latest AS (SELECT MAX(Id) AS Id FROM dbo.Products) SELECT Id FROM latest;")]
    [InlineData("/* context */ SELECT 1 AS Value;")]
    public void Accepts_one_read_only_select(string sql) => _policy.Invoking(x => x.DemandSelectOnly(sql)).Should().NotThrow();

    [Theory]
    [InlineData("SELECT 1; DROP TABLE dbo.Products;")]
    [InlineData("SELECT 1; SELECT 2;")]
    [InlineData("SELECT * INTO dbo.Copy FROM dbo.Products;")]
    [InlineData("WITH x AS (SELECT 1 AS Id) UPDATE dbo.Products SET Name='x';")]
    [InlineData("EXEC dbo.DoWork;")]
    [InlineData("DELETE FROM dbo.Products;")]
    [InlineData("not valid sql")]
    public void Rejects_non_read_only_or_multiple_statements(string sql) =>
        _policy.Invoking(x => x.DemandSelectOnly(sql)).Should().Throw<BusinessRuleException>();
}
