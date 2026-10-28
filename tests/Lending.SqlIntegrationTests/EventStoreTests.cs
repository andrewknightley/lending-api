using System.Net;
using System.Net.Http.Json;
using Lending.Application;
using Lending.Domain;
using Lending.Infrastructure;
using Microsoft.Data.SqlClient;

namespace Lending.SqlIntegrationTests;

public sealed class EventStoreTests(SqlFixture fixture) : IClassFixture<SqlFixture>
{
    private static string Number() => "E-" + Guid.NewGuid().ToString("N")[..24];
    private static object Request(string number) => new { agreementNumber = number, creditProductId = SqlFixture.ProductId,
        agreementType = "FixedLoan", agreedAmount = 10000.1234m, annualInterestRatePercent = 7.512345m, termMonths = 36 };
    private async Task<long> Scalar(string sql)
    {
        await using var connection = new SqlConnection(fixture.ConnectionString); await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
    private async Task<AgreementView> Create(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync($"/api/accounts/{SqlFixture.AccountId}/credit-agreements", Request(Number()));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AgreementView>())!;
    }
    private static async Task<HttpResponseMessage> Activate(HttpClient client, AgreementView agreement)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/credit-agreements/{agreement.CreditAgreementId}/activate");
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{agreement.RowVersion}\"");
        return await client.SendAsync(request);
    }

    [SqlFact]
    public async Task Commands_append_ordered_events_and_repeat_activation_adds_nothing()
    {
        using var client = fixture.Factory.CreateClient(); var pending = await Create(client);
        var id = pending.CreditAgreementId;
        Assert.Equal(1, await Scalar($"SELECT Version FROM lending.AgreementStream WHERE CreditAgreementId = '{id}'"));
        using var activated = await Activate(client, pending); Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
        var active = (await activated.Content.ReadFromJsonAsync<AgreementView>())!;
        using var repeat = await Activate(client, active); Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.Equal(2, await Scalar($"SELECT COUNT(*) FROM lending.AgreementEvent WHERE CreditAgreementId = '{id}'"));
        Assert.Equal(2, await Scalar($"SELECT Version FROM lending.AgreementStream WHERE CreditAgreementId = '{id}'"));
        Assert.Equal(1, await Scalar($"SELECT COUNT(*) FROM lending.AgreementEvent WHERE CreditAgreementId = '{id}' AND StreamVersion = 2 AND EventType = 'AgreementActivated'"));
    }

    [SqlFact]
    public async Task Replay_uses_event_terms_and_projection_can_be_rebuilt_without_changing_events()
    {
        using var client = fixture.Factory.CreateClient(); var pending = await Create(client); var id = pending.CreditAgreementId;
        await fixture.ExecuteAsync($"UPDATE lending.CreditAgreement SET AgreedAmount = 1, TermMonths = 1 WHERE CreditAgreementId = '{id}';");
        var sessions = new SqlAgreementSessionFactory(new SqlConnectionFactory(fixture.ConnectionString));
        await using (var session = await sessions.BeginAsync(default))
        {
            var loaded = (await session.GetAgreementForUpdateAsync(id, default))!;
            Assert.Equal(pending.AgreedAmount, loaded.Aggregate.Terms.Amount);
            Assert.Equal(36, loaded.Aggregate.Terms.TermMonths); Assert.Equal(1, loaded.Aggregate.Version);
        }
        await fixture.ExecuteAsync($"DELETE FROM lending.CreditAgreement WHERE CreditAgreementId = '{id}';");
        // The stream reserves the number even while its projection is absent.
        using var duplicate = await client.PostAsJsonAsync($"/api/accounts/{SqlFixture.AccountId}/credit-agreements", Request(pending.AgreementNumber));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        await new SqlAgreementProjectionRebuilder(new SqlConnectionFactory(fixture.ConnectionString)).RebuildAsync(id);
        var rebuilt = (await client.GetFromJsonAsync<AgreementView>($"/api/credit-agreements/{id}"))!;
        Assert.Equal(pending.AgreedAmount, rebuilt.AgreedAmount); Assert.Equal(pending.TermMonths, rebuilt.TermMonths);
        Assert.NotEqual(pending.RowVersion, rebuilt.RowVersion);
        Assert.Equal(1, await Scalar($"SELECT COUNT(*) FROM lending.AgreementEvent WHERE CreditAgreementId = '{id}'"));
        using var stale = await Activate(client, pending); Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        using var active = await Activate(client, rebuilt); Assert.Equal(HttpStatusCode.OK, active.StatusCode);
    }

    [SqlFact]
    public async Task Failed_projection_update_rolls_back_event_append_and_stream_head()
    {
        using var client = fixture.Factory.CreateClient(); var pending = await Create(client); var id = pending.CreditAgreementId;
        var sessions = new SqlAgreementSessionFactory(new SqlConnectionFactory(fixture.ConnectionString));
        await using (var session = await sessions.BeginAsync(default))
        {
            var stored = (await session.GetAgreementForUpdateAsync(id, default))!;
            var eligibility = (await session.GetAccountEligibilityAsync(SqlFixture.AccountId, default))!;
            stored.Aggregate.Activate(eligibility, DateTimeOffset.UtcNow);
            await Assert.ThrowsAsync<AgreementConcurrencyException>(() => session.UpdateActivationAsync(stored.Aggregate, new byte[8], default));
            // Disposing rolls back the stream CAS and appended event as well as the projection.
        }
        Assert.Equal(1, await Scalar($"SELECT Version FROM lending.AgreementStream WHERE CreditAgreementId = '{id}'"));
        Assert.Equal(1, await Scalar($"SELECT COUNT(*) FROM lending.AgreementEvent WHERE CreditAgreementId = '{id}'"));
        var persisted = (await client.GetFromJsonAsync<AgreementView>($"/api/credit-agreements/{id}"))!;
        Assert.Equal(pending, persisted);
    }

    [SqlFact]
    public async Task Abandoned_creation_leaves_no_stream_or_events()
    {
        var id = Guid.NewGuid(); var sessions = new SqlAgreementSessionFactory(new SqlConnectionFactory(fixture.ConnectionString));
        await using (var session = await sessions.BeginAsync(default))
        {
            var account = (await session.GetAccountEligibilityAsync(SqlFixture.AccountId, default))!;
            var product = (await session.GetProductEligibilityAsync(SqlFixture.ProductId, default))!;
            var aggregate = CreditAgreement.Create(id, Number(), account, product,
                AgreementTerms.Create(AgreementType.FixedLoan, 100m, 0m, 12), DateTimeOffset.UtcNow);
            await session.InsertAsync(aggregate, default);
        }
        Assert.Equal(0, await Scalar($"SELECT COUNT(*) FROM lending.AgreementStream WHERE CreditAgreementId = '{id}'"));
        Assert.Equal(0, await Scalar($"SELECT COUNT(*) FROM lending.AgreementEvent WHERE CreditAgreementId = '{id}'"));
    }

    [SqlFact]
    public async Task Historical_events_cannot_be_updated_or_deleted()
    {
        using var client = fixture.Factory.CreateClient(); var pending = await Create(client); var id = pending.CreditAgreementId;
        var update = await Assert.ThrowsAsync<SqlException>(() => fixture.ExecuteAsync($"UPDATE lending.AgreementEvent SET Payload = Payload WHERE CreditAgreementId = '{id}';"));
        Assert.Equal(51000, update.Number);
        var delete = await Assert.ThrowsAsync<SqlException>(() => fixture.ExecuteAsync($"DELETE FROM lending.AgreementEvent WHERE CreditAgreementId = '{id}';"));
        Assert.Equal(51000, delete.Number);
        Assert.Equal(1, await Scalar($"SELECT COUNT(*) FROM lending.AgreementEvent WHERE CreditAgreementId = '{id}'"));
    }

    [SqlFact]
    public async Task Existing_stream_identity_cannot_be_recreated_or_append_another_event()
    {
        using var client = fixture.Factory.CreateClient(); var pending = await Create(client); var id = pending.CreditAgreementId;
        var sessions = new SqlAgreementSessionFactory(new SqlConnectionFactory(fixture.ConnectionString));
        await using (var session = await sessions.BeginAsync(default))
        {
            var stored = (await session.GetAgreementForUpdateAsync(id, default))!;
            var account = (await session.GetAccountEligibilityAsync(SqlFixture.AccountId, default))!;
            // An aggregate with two uncommitted events expects an empty stream, not this persisted stream.
            var wrong = CreditAgreement.Create(id, pending.AgreementNumber, account,
                new ProductEligibility(SqlFixture.ProductId, AgreementType.FixedLoan, true),
                stored.Aggregate.Terms, stored.Aggregate.CreatedAtUtc);
            wrong.Activate(account, DateTimeOffset.UtcNow);
            await Assert.ThrowsAsync<AgreementConcurrencyException>(() => session.UpdateActivationAsync(wrong, stored.RowVersion, default));
        }
        Assert.Equal(1, await Scalar($"SELECT COUNT(*) FROM lending.AgreementEvent WHERE CreditAgreementId = '{id}'"));
    }

    [SqlFact]
    public async Task Stale_stream_version_rejects_append_even_outside_HTTP()
    {
        using var client = fixture.Factory.CreateClient(); var pending = await Create(client);
        var sessions = new SqlAgreementSessionFactory(new SqlConnectionFactory(fixture.ConnectionString));
        StoredAgreement stale;
        await using (var session = await sessions.BeginAsync(default))
            stale = (await session.GetAgreementForUpdateAsync(pending.CreditAgreementId, default))!;
        using var activated = await Activate(client, pending); Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
        stale.Aggregate.Activate(new AccountEligibility(SqlFixture.AccountId, SqlFixture.CustomerId, "Open", "Active", "GBP"), DateTimeOffset.UtcNow);
        await using (var session = await sessions.BeginAsync(default))
            await Assert.ThrowsAsync<AgreementConcurrencyException>(() => session.UpdateActivationAsync(stale.Aggregate, stale.RowVersion, default));
        Assert.Equal(2, await Scalar($"SELECT Version FROM lending.AgreementStream WHERE CreditAgreementId = '{pending.CreditAgreementId}'"));
        Assert.Equal(2, await Scalar($"SELECT COUNT(*) FROM lending.AgreementEvent WHERE CreditAgreementId = '{pending.CreditAgreementId}'"));
    }

    [SqlFact]
    public async Task Legacy_import_is_explicit_repeatable_and_preserves_HTTP_versions_and_known_state()
    {
        using var client = fixture.Factory.CreateClient(); var id = Guid.NewGuid(); var number = Number();
        await fixture.ExecuteAsync($"""
            INSERT INTO lending.CreditAgreement (CreditAgreementId, AgreementNumber, AccountId, CreditProductId,
                AgreementType, AgreedAmount, AnnualInterestRatePercent, TermMonths, Status, CreatedAtUtc, ActivatedAtUtc)
            VALUES ('{id}', '{number}', '{SqlFixture.AccountId}', '{SqlFixture.ProductId}',
                'FixedLoan', 500.1234, 1.123456, 12, 'Active', '2026-01-01', '2026-01-02');
            """);
        var legacy = (await client.GetFromJsonAsync<AgreementView>($"/api/credit-agreements/{id}"))!;
        using var before = await Activate(client, legacy); Assert.Equal(HttpStatusCode.InternalServerError, before.StatusCode);
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "import-legacy-agreements.sql"));
        await fixture.ExecuteAsync(sql); await fixture.ExecuteAsync(sql);
        var after = (await client.GetFromJsonAsync<AgreementView>($"/api/credit-agreements/{id}"))!;
        Assert.Equal(legacy, after);
        Assert.Equal(1, await Scalar($"SELECT COUNT(*) FROM lending.AgreementEvent WHERE CreditAgreementId = '{id}' AND EventType = 'AgreementImported'"));
        using var repeat = await Activate(client, after); Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.Equal(after.RowVersion, (await repeat.Content.ReadFromJsonAsync<AgreementView>())!.RowVersion);
        await new SqlAgreementProjectionRebuilder(new SqlConnectionFactory(fixture.ConnectionString)).RebuildAsync(id);
        var rebuilt = (await client.GetFromJsonAsync<AgreementView>($"/api/credit-agreements/{id}"))!;
        Assert.Equal(legacy with { RowVersion = rebuilt.RowVersion }, rebuilt);
    }
}
