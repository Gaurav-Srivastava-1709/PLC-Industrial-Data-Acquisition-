using Microsoft.SqlServer.TransactSql.ScriptDom;
using WTT.Application.Interfaces;
using WTT.Domain.Exceptions;

namespace WTT.Infrastructure.Services;

public sealed class ExternalSqlSelectPolicy : IExternalSqlPolicy
{
    public void DemandSelectOnly(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) throw new BusinessRuleException("Query is required.");

        var parser = new TSql180Parser(initialQuotedIdentifiers: true);
        var fragment = parser.Parse(new StringReader(sql), out var errors);
        if (errors.Count > 0)
            throw new BusinessRuleException($"Invalid SQL near line {errors[0].Line}: {errors[0].Message}");
        if (fragment is not TSqlScript script || script.Batches.Count != 1 ||
            script.Batches[0].Statements.Count != 1 || script.Batches[0].Statements[0] is not SelectStatement statement)
            throw new BusinessRuleException("Exactly one read-only SELECT statement is allowed.");

        if (statement.Into is not null)
            throw new BusinessRuleException("SELECT INTO is not allowed.");
    }
}
