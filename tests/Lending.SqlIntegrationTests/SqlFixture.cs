using System.Text.RegularExpressions;
using Lending.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Lending.SqlIntegrationTests;

public sealed class SqlFactAttribute : FactAttribute
{
    public SqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LENDING_TEST_SQL_CONNECTION_STRING"))
            && Environment.GetEnvironmentVariable("LENDING_REQUIRE_SQL_TESTS") != "1")
            Skip = "Set LENDING_TEST_SQL_CONNECTION_STRING to an isolated SQL Server's master database to run real SQL tests.";
    }
}

public sealed class SqlFixture : IAsyncLifetime
{
    public static readonly Guid CustomerId = Guid.Parse("10000000-0000-4000-8000-000000000001");
    public static readonly Guid AccountId = Guid.Parse("20000000-0000-4000-8000-000000000001");
    public static readonly Guid ProductId = Guid.Parse("30000000-0000-4000-8000-000000000001");
    private readonly string databaseName = "LendingApiTests_" + Guid.NewGuid().ToString("N");
    private string? masterConnectionString;
    private bool databaseCreated;
    public string ConnectionString { get; private set; } = "";
    public SqlApiFactory Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable("LENDING_TEST_SQL_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(configured))
        {
            if (Environment.GetEnvironmentVariable("LENDING_REQUIRE_SQL_TESTS") == "1")
                throw new InvalidOperationException("SQL tests are required, but LENDING_TEST_SQL_CONNECTION_STRING is missing.");
            return;
        }
        var builder = new SqlConnectionStringBuilder(configured);
        if (!string.Equals(builder.InitialCatalog, "master", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SQL integration tests require Database=master. They create and drop their own uniquely named database, never your Lending database.");
        masterConnectionString = builder.ConnectionString;
        try
        {
            await using var master = new SqlConnection(masterConnectionString); await master.OpenAsync();
            await using var create = new SqlCommand($"CREATE DATABASE [{databaseName}];", master); await create.ExecuteNonQueryAsync(); databaseCreated = true;
            builder.InitialCatalog = databaseName; ConnectionString = builder.ConnectionString;
            var schema = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "schema.sql"));
            foreach (var batch in Regex.Split(schema, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase).Where(s => !string.IsNullOrWhiteSpace(s)))
                await ExecuteAsync(batch);
            await ExecuteAsync("""
                INSERT INTO lending.Customer (CustomerId, CustomerNumber, FirstName, LastName, DateOfBirth)
                VALUES ('10000000-0000-4000-8000-000000000001', 'CUS-TEST', 'Morgan', 'Reed', '1986-05-14');
                INSERT INTO lending.Account (AccountId, CustomerId, AccountNumber)
                VALUES ('20000000-0000-4000-8000-000000000001', '10000000-0000-4000-8000-000000000001', 'ACC-TEST');
                INSERT INTO lending.CreditProduct (CreditProductId, ProductCode, ProductName, ProductType)
                VALUES ('30000000-0000-4000-8000-000000000001', 'PL-TEST', 'Test fixed loan', 'FixedLoan');
                """);
            Factory = new SqlApiFactory(ConnectionString);
        }
        catch { await DisposeAsync(); throw; }
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString); await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection); await command.ExecuteNonQueryAsync();
    }

    public async Task<int> CountAsync(string agreementNumber)
    {
        await using var connection = new SqlConnection(ConnectionString); await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT COUNT(*) FROM lending.CreditAgreement WHERE AgreementNumber = @number;", connection);
        command.Parameters.Add(new SqlParameter("@number", System.Data.SqlDbType.VarChar, 30) { Value = agreementNumber });
        return (int)(await command.ExecuteScalarAsync())!;
    }

    public async Task DisposeAsync()
    {
        if (Factory is not null) await Factory.DisposeAsync();
        if (!databaseCreated || masterConnectionString is null) return;
        SqlConnection.ClearAllPools();
        await using var master = new SqlConnection(masterConnectionString); await master.OpenAsync();
        await using var drop = new SqlCommand($"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}];", master);
        await drop.ExecuteNonQueryAsync(); databaseCreated = false;
    }
}

public sealed class SqlApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Lending"] = connectionString }));
    }
}
