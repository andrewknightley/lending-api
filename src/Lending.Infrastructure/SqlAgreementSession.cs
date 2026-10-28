using System.Data;
using Lending.Application;
using Lending.Domain;
using Lending.Domain.Events;
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
        var aggregate = await LoadStreamAsync(agreementId, cancellationToken);
        await using var command = new SqlCommand("SELECT RowVersion FROM lending.CreditAgreement WITH (UPDLOCK, HOLDLOCK) WHERE CreditAgreementId = @id;", connection, transaction);
        command.Parameters.Add(SqlMapping.GuidParameter("@id", agreementId));
        var version = await command.ExecuteScalarAsync(cancellationToken) as byte[];
        if (aggregate is null)
        {
            if (version is not null) throw new InvalidOperationException("Agreement has no event stream. Run the explicit legacy import before switching API versions.");
            return null;
        }
        if (version is null) throw new InvalidOperationException("Agreement projection is missing. Rebuild it from the event stream.");
        return new StoredAgreement(aggregate, version);
    }

    internal async Task<CreditAgreement?> LoadStreamAsync(Guid id, CancellationToken cancellationToken)
    {
        long head;
        string number;
        await using (var command = new SqlCommand("SELECT Version, AgreementNumber FROM lending.AgreementStream WITH (UPDLOCK, HOLDLOCK) WHERE CreditAgreementId = @id;", connection, transaction))
        {
            command.Parameters.Add(SqlMapping.GuidParameter("@id", id));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            head = reader.GetInt64(0); number = reader.GetString(1);
        }
        var events = new List<AgreementEvent>();
        await using (var command = new SqlCommand("SELECT StreamVersion, EventType, SchemaVersion, Payload FROM lending.AgreementEvent WHERE CreditAgreementId = @id ORDER BY StreamVersion;", connection, transaction))
        {
            command.Parameters.Add(SqlMapping.GuidParameter("@id", id));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetInt64(0) != events.Count + 1L) throw new InvalidOperationException("Agreement event stream contains a version gap.");
                var @event = AgreementEventJson.Deserialize(reader.GetString(1), reader.GetInt32(2), reader.GetString(3));
                if (@event.CreditAgreementId != id) throw new InvalidOperationException("Agreement event payload belongs to a different stream.");
                events.Add(@event);
            }
        }
        if (events.Count != head || head == 0) throw new InvalidOperationException("Agreement stream head does not match its history.");
        var agreement = CreditAgreement.Replay(events);
        if (agreement.AgreementNumber != number) throw new InvalidOperationException("Agreement number reservation does not match history.");
        return agreement;
    }

    public async Task<StoredAgreement> InsertAsync(CreditAgreement agreement, CancellationToken cancellationToken)
    {
        await AppendAsync(agreement, 0, cancellationToken);
        return await WriteProjectionAsync(agreement, true, null, cancellationToken);
    }

    public async Task<StoredAgreement> UpdateActivationAsync(CreditAgreement agreement, byte[] expectedVersion, CancellationToken cancellationToken)
    {
        await AppendAsync(agreement, agreement.Version - agreement.PendingEvents.Count, cancellationToken);
        return await WriteProjectionAsync(agreement, false, expectedVersion, cancellationToken);
    }

    private async Task AppendAsync(CreditAgreement agreement, long expectedVersion, CancellationToken cancellationToken)
    {
        if (agreement.PendingEvents.Count == 0 || agreement.Version != expectedVersion + agreement.PendingEvents.Count)
            throw new InvalidOperationException("An event append requires pending events and the correct expected stream version.");
        try
        {
            var sql = expectedVersion == 0
                ? "INSERT INTO lending.AgreementStream (CreditAgreementId, AgreementNumber, Version) VALUES (@id, @number, @next);"
                : "UPDATE lending.AgreementStream SET Version = @next WHERE CreditAgreementId = @id AND Version = @expected;";
            await using var head = new SqlCommand(sql, connection, transaction);
            head.Parameters.Add(SqlMapping.GuidParameter("@id", agreement.CreditAgreementId));
            head.Parameters.Add(new SqlParameter("@number", SqlDbType.VarChar, 30) { Value = agreement.AgreementNumber });
            head.Parameters.Add(new SqlParameter("@next", SqlDbType.BigInt) { Value = agreement.Version });
            head.Parameters.Add(new SqlParameter("@expected", SqlDbType.BigInt) { Value = expectedVersion });
            if (await head.ExecuteNonQueryAsync(cancellationToken) != 1) throw new AgreementConcurrencyException();
        }
        catch (SqlException e) when (e.Number is 2601 or 2627)
        {
            if (e.Message.Contains("PK_AgreementStream", StringComparison.Ordinal)) throw new AgreementConcurrencyException();
            throw new AgreementNumberConflictException();
        }
        var version = expectedVersion;
        foreach (var @event in agreement.PendingEvents)
        {
            await using var command = new SqlCommand("""
                INSERT INTO lending.AgreementEvent (EventId, CreditAgreementId, StreamVersion, EventType, SchemaVersion, OccurredAtUtc, Payload)
                VALUES (@eventId, @id, @version, @type, 1, @occurred, @payload);
                """, connection, transaction);
            command.Parameters.Add(SqlMapping.GuidParameter("@eventId", Guid.NewGuid()));
            command.Parameters.Add(SqlMapping.GuidParameter("@id", agreement.CreditAgreementId));
            command.Parameters.Add(new SqlParameter("@version", SqlDbType.BigInt) { Value = ++version });
            command.Parameters.Add(new SqlParameter("@type", SqlDbType.VarChar, 80) { Value = AgreementEventJson.TypeName(@event) });
            command.Parameters.Add(new SqlParameter("@occurred", SqlDbType.DateTime2) { Scale = 7, Value = @event.OccurredAtUtc.UtcDateTime });
            command.Parameters.Add(new SqlParameter("@payload", SqlDbType.NVarChar, -1) { Value = AgreementEventJson.Serialize(@event) });
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    internal async Task<StoredAgreement> WriteProjectionAsync(CreditAgreement agreement, bool insert, byte[]? expectedVersion, CancellationToken cancellationToken)
    {
        var sql = insert ? """
            INSERT INTO lending.CreditAgreement (CreditAgreementId, AgreementNumber, AccountId, CreditProductId,
                AgreementType, AgreedAmount, AnnualInterestRatePercent, TermMonths, Status, CreatedAtUtc, ActivatedAtUtc)
            OUTPUT INSERTED.RowVersion
            VALUES (@id, @number, @accountId, @productId, @type, @amount, @rate, @term, @status, @created, @activated);
            """ : """
            UPDATE lending.CreditAgreement SET AgreementNumber = @number, AccountId = @accountId, CreditProductId = @productId,
                AgreementType = @type, AgreedAmount = @amount, AnnualInterestRatePercent = @rate, TermMonths = @term,
                Status = @status, CreatedAtUtc = @created, ActivatedAtUtc = @activated
            OUTPUT INSERTED.RowVersion WHERE CreditAgreementId = @id AND RowVersion = @version;
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
        command.Parameters.Add(new SqlParameter("@status", SqlDbType.VarChar, 20) { Value = agreement.Status.ToString() });
        command.Parameters.Add(new SqlParameter("@created", SqlDbType.DateTime2) { Scale = 7, Value = agreement.CreatedAtUtc.UtcDateTime });
        command.Parameters.Add(new SqlParameter("@activated", SqlDbType.DateTime2) { Scale = 7, Value = (object?)agreement.ActivatedAtUtc?.UtcDateTime ?? DBNull.Value });
        command.Parameters.Add(new SqlParameter("@version", SqlDbType.Binary, 8) { Value = (object?)expectedVersion ?? DBNull.Value });
        try
        {
            var version = await command.ExecuteScalarAsync(cancellationToken) as byte[] ?? throw new AgreementConcurrencyException();
            return new StoredAgreement(agreement, version);
        }
        catch (SqlException e) when (e.Number is 2601 or 2627) { throw new AgreementNumberConflictException(); }
    }

    public Task CommitAsync(CancellationToken cancellationToken) => transaction.CommitAsync(cancellationToken);
    public async ValueTask DisposeAsync()
    {
        try { await transaction.DisposeAsync(); }
        finally { await connection.DisposeAsync(); }
    }
}
