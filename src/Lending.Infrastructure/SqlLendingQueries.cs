using System.Data;
using Lending.Application;
using Microsoft.Data.SqlClient;

namespace Lending.Infrastructure;

public sealed class SqlLendingQueries(SqlConnectionFactory connections) : ILendingQueries
{
    public Task<AgreementView?> GetAgreementAsync(Guid id, CancellationToken cancellationToken)
        => SingleAsync($"SELECT {SqlMapping.AgreementColumns} FROM lending.CreditAgreement WHERE CreditAgreementId = @id;", id,
            r => SqlMapping.Agreement(r).ToView(), cancellationToken);

    public Task<AccountView?> GetAccountAsync(Guid id, CancellationToken cancellationToken)
        => SingleAsync("SELECT AccountId, CustomerId, AccountNumber, CurrencyCode, Status, OpenedAtUtc, ClosedAtUtc FROM lending.Account WHERE AccountId = @id;",
            id, Account, cancellationToken);

    public Task<CustomerView?> GetCustomerAsync(Guid id, CancellationToken cancellationToken)
        => SingleAsync("SELECT CustomerId, CustomerNumber, FirstName, LastName, Status FROM lending.Customer WHERE CustomerId = @id;",
            id, Customer, cancellationToken);

    public Task<Page<CustomerView>> ListCustomersAsync(int offset, int pageSize, CancellationToken cancellationToken)
        => ListAsync("SELECT CustomerId, CustomerNumber, FirstName, LastName, Status FROM lending.Customer ORDER BY CustomerNumber OFFSET @offset ROWS FETCH NEXT @take ROWS ONLY;",
            [], offset, pageSize, Customer, cancellationToken);

    public Task<Page<AccountView>> ListAccountsAsync(Guid? customerId, int offset, int pageSize, CancellationToken cancellationToken)
        => ListAsync("SELECT AccountId, CustomerId, AccountNumber, CurrencyCode, Status, OpenedAtUtc, ClosedAtUtc FROM lending.Account WHERE (@customerId IS NULL OR CustomerId = @customerId) ORDER BY AccountNumber OFFSET @offset ROWS FETCH NEXT @take ROWS ONLY;",
            [new SqlParameter("@customerId", SqlDbType.UniqueIdentifier) { Value = (object?)customerId ?? DBNull.Value }], offset, pageSize, Account, cancellationToken);

    public Task<Page<ProductView>> ListProductsAsync(bool? available, int offset, int pageSize, CancellationToken cancellationToken)
        => ListAsync("SELECT CreditProductId, ProductCode, ProductName, ProductType, IsAvailable FROM lending.CreditProduct WHERE (@available IS NULL OR IsAvailable = @available) ORDER BY ProductCode OFFSET @offset ROWS FETCH NEXT @take ROWS ONLY;",
            [new SqlParameter("@available", SqlDbType.Bit) { Value = (object?)available ?? DBNull.Value }], offset, pageSize,
            r => new ProductView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4)), cancellationToken);

    public Task<Page<AgreementView>> ListAgreementsAsync(Guid accountId, int offset, int pageSize, CancellationToken cancellationToken)
        => ListAsync($"SELECT {SqlMapping.AgreementColumns} FROM lending.CreditAgreement WHERE AccountId = @accountId ORDER BY AgreementNumber OFFSET @offset ROWS FETCH NEXT @take ROWS ONLY;",
            [SqlMapping.GuidParameter("@accountId", accountId)], offset, pageSize, r => SqlMapping.Agreement(r).ToView(), cancellationToken);

    private async Task<T?> SingleAsync<T>(string sql, Guid id, Func<SqlDataReader, T> map, CancellationToken cancellationToken) where T : class
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add(SqlMapping.GuidParameter("@id", id));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? map(reader) : null;
    }

    private async Task<Page<T>> ListAsync<T>(string sql, SqlParameter[] parameters, int offset, int pageSize,
        Func<SqlDataReader, T> map, CancellationToken cancellationToken)
    {
        if (offset < 0 || pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(pageSize));
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        command.Parameters.Add(new SqlParameter("@offset", SqlDbType.Int) { Value = offset });
        command.Parameters.Add(new SqlParameter("@take", SqlDbType.Int) { Value = pageSize + 1 });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<T>();
        while (await reader.ReadAsync(cancellationToken)) rows.Add(map(reader));
        var hasMore = rows.Count > pageSize;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        return new Page<T>(rows, offset, pageSize, hasMore);
    }

    private static CustomerView Customer(SqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4));
    private static AccountView Account(SqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4),
        SqlMapping.Utc(r, 5), r.IsDBNull(6) ? null : SqlMapping.Utc(r, 6));
}
