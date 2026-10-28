using System.Data;
using Lending.Application;
using Microsoft.Data.SqlClient;

namespace Lending.Infrastructure;

// An explicit maintenance operation, not a public HTTP endpoint. Run with agreement writes paused.
public sealed class SqlAgreementProjectionRebuilder(SqlConnectionFactory connections)
{
    public async Task RebuildAsync(Guid agreementId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await using var session = new SqlAgreementSession(connection, transaction);
        var agreement = await session.LoadStreamAsync(agreementId, cancellationToken)
            ?? throw new EntityNotFoundException("CreditAgreement", agreementId);
        await using (var delete = new SqlCommand("DELETE FROM lending.CreditAgreement WHERE CreditAgreementId = @id;", connection, transaction))
        {
            delete.Parameters.Add(SqlMapping.GuidParameter("@id", agreementId));
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        await session.WriteProjectionAsync(agreement, true, null, cancellationToken);
        await session.CommitAsync(cancellationToken);
    }
}
