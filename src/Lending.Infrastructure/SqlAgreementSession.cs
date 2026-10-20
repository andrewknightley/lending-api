using System.Data;
using Lending.Application;
using Lending.Domain;
using Microsoft.Data.SqlClient;

namespace Lending.Infrastructure;

public sealed class SqlAgreementSessionFactory(SqlConnectionFactory connections) : IAgreementSessionFactory
{
    public async Task<IAgreementSession> BeginAsync(CancellationToken cancellationToken)
    {
        var connection = await connections.OpenAsync(cancellationToken);
        try
        {
            var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            return new SqlAgreementSession(connection, transaction);
        }
        catch { await connection.DisposeAsync(); throw; }
    }
}

internal sealed class SqlAgreementSession(SqlConnection connection, SqlTransaction transaction) : IAgreementSession
{
    public async Task<AccountEligibility?> GetAccountEligibilityAsync(Guid accountId, CancellationToken cancellationToken)
    {
        // HOLDLOCK retains shared/key-range locks until commit: eligibility cannot change before the write.
        // This is effective even when READ_COMMITTED_SNAPSHOT is enabled.
        const string sql = """
            SELECT a.AccountId, a.CustomerId, a.Status, c.Status, a.CurrencyCode
            FROM lending.Account AS a WITH (HOLDLOCK)
            INNER JOIN lending.Customer AS c WITH (HOLDLOCK) ON c.CustomerId = a.CustomerId
            WHERE a.AccountId = @id;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add(SqlMapping.GuidParameter("@id", accountId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new AccountEligibility(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)) : null;
    }

    public async Task<ProductEligibility?> GetProductEligibilityAsync(Guid productId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT CreditProductId, ProductType, IsAvailable FROM lending.CreditProduct WITH (HOLDLOCK) WHERE CreditProductId = @id;";
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add(SqlMapping.GuidParameter("@id", productId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ProductEligibility(reader.GetGuid(0), Enum.Parse<AgreementType>(reader.GetString(1)), reader.GetBoolean(2)) : null;
    }

    public async Task<StoredAgreement?> GetAgreementForUpdateAsync(Guid agreementId, CancellationToken cancellationToken)
    {
        // Serialise concurrent activation reads to avoid shared-lock upgrade deadlocks.
        var sql = $"SELECT {SqlMapping.AgreementColumns} FROM lending.CreditAgreement WITH (UPDLOCK, HOLDLOCK) WHERE CreditAgreementId = @id;";
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add(SqlMapping.GuidParameter("@id", agreementId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? SqlMapping.Agreement(reader) : null;
    }

    public async Task<StoredAgreement> InsertAsync(CreditAgreement agreement, CancellationToken cancellationToken)
    {
        var sql = $"""
            INSERT INTO lending.CreditAgreement
                (CreditAgreementId, AgreementNumber, AccountId, CreditProductId, AgreementType,
                 AgreedAmount, AnnualInterestRatePercent, TermMonths, Status, CreatedAtUtc)
            OUTPUT {string.Join(", ", SqlMapping.AgreementColumns.Split(", ").Select(c => "INSERTED." + c))}
            VALUES (@id, @number, @accountId, @productId, @type, @amount, @rate, @term, 'Pending', @created);
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add(SqlMapping.GuidParameter("@id", agreement.CreditAgreementId));
        command.Parameters.Add(new SqlParameter("@number", SqlDbType.VarChar, 30) { Value = agreement.AgreementNumber });
        command.Parameters.Add(SqlMapping.GuidParameter("@accountId", agreement.AccountId));
        command.Parameters.Add(SqlMapping.GuidParameter("@productId", agreement.CreditProductId));
        command.Parameters.Add(new SqlParameter("@type", SqlDbType.VarChar, 20) { Value = agreement.Terms.Type.ToString() });
        command.Parameters.Add(SqlMapping.DecimalParameter("@amount", agreement.Terms.Amount, 19, 4));
        command.Parameters.Add(SqlMapping.DecimalParameter("@rate", agreement.Terms.AnnualInterestRatePercent, 9, 6));
        command.Parameters.Add(new SqlParameter("@term", SqlDbType.Int) { Value = (object?)agreement.Terms.TermMonths ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@created", SqlDbType.DateTime2) { Scale = 7, Value = agreement.CreatedAtUtc.UtcDateTime });
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Agreement insert returned no row.");

            return SqlMapping.Agreement(reader);
        }
        catch (SqlException e) when (e.Number is 2601 or 2627) { throw new AgreementNumberConflictException(); }
    }

    public async Task<StoredAgreement> UpdateActivationAsync(CreditAgreement agreement, byte[] expectedVersion, CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE lending.CreditAgreement SET Status = @status, ActivatedAtUtc = @activated
            OUTPUT {string.Join(", ", SqlMapping.AgreementColumns.Split(", ").Select(c => "INSERTED." + c))}
            WHERE CreditAgreementId = @id AND RowVersion = @version;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add(SqlMapping.GuidParameter("@id", agreement.CreditAgreementId));
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Binary, 8) { Value = expectedVersion });
        command.Parameters.Add(new SqlParameter("@status", SqlDbType.VarChar, 20) { Value = agreement.Status.ToString() });
        command.Parameters.Add(new SqlParameter("@activated", SqlDbType.DateTime2) { Scale = 7, Value = (object?)agreement.ActivatedAtUtc?.UtcDateTime ?? DBNull.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new AgreementConcurrencyException();
        return SqlMapping.Agreement(reader);
    }

    public Task CommitAsync(CancellationToken cancellationToken) => transaction.CommitAsync(cancellationToken);
    public async ValueTask DisposeAsync()
    {
        // Disposing an uncommitted transaction rolls it back, including failures during rule validation.
        try { await transaction.DisposeAsync(); }
        finally { await connection.DisposeAsync(); }
    }
}
