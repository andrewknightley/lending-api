using Microsoft.Data.SqlClient;

namespace Lending.Infrastructure;

public sealed class SqlConnectionFactory
{
    private readonly string connectionString;
    public SqlConnectionFactory(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Configure ConnectionStrings:Lending using user secrets or ConnectionStrings__Lending.");
        _ = new SqlConnectionStringBuilder(connectionString); // Validate syntax without exposing credentials.
        this.connectionString = connectionString;
    }

    public async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connectionString);
        try { await connection.OpenAsync(cancellationToken); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
}
