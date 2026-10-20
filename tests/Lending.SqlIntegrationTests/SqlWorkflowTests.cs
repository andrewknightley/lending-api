using System.Net;
using System.Net.Http.Json;
using Lending.Application;
using Lending.Domain;
using Lending.Infrastructure;
using Microsoft.Data.SqlClient;

namespace Lending.SqlIntegrationTests;

// Tests in this class share one disposable database and run serially; mutations are restored in finally blocks.
public sealed class SqlWorkflowTests(SqlFixture fixture) : IClassFixture<SqlFixture>
{
    private static object Request(string number, string type = "FixedLoan", decimal amount = 10000.1234m,
        decimal rate = 7.512345m, int? term = 36)
        => new { agreementNumber = number, creditProductId = SqlFixture.ProductId, agreementType = type,
            agreedAmount = amount, annualInterestRatePercent = rate, termMonths = term };
    private static string Number() => "T-" + Guid.NewGuid().ToString("N")[..24];
    private static async Task<HttpResponseMessage> Activate(HttpClient client, Guid id, string etag)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"/api/credit-agreements/{id}/activate");
        message.Headers.TryAddWithoutValidation("If-Match", etag); return await client.SendAsync(message);
    }

    [SqlFact]
    public async Task SQL_HTTP_workflow_preserves_decimal_precision_UTC_and_rowversion()
    {
        using var client = fixture.Factory.CreateClient(); var number = Number();
        using var created = await client.PostAsJsonAsync($"/api/accounts/{SqlFixture.AccountId}/credit-agreements", Request(number));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var pending = (await created.Content.ReadFromJsonAsync<AgreementView>())!;
        Assert.Equal(10000.1234m, pending.AgreedAmount); Assert.Equal(7.512345m, pending.AnnualInterestRatePercent);
        Assert.Equal(TimeSpan.Zero, pending.CreatedAtUtc.Offset); Assert.Equal(8, Convert.FromBase64String(pending.RowVersion).Length);
        using var activated = await Activate(client, pending.CreditAgreementId, created.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.OK, activated.StatusCode); Assert.NotEqual(created.Headers.ETag, activated.Headers.ETag);
        var active = (await activated.Content.ReadFromJsonAsync<AgreementView>())!;
        Assert.Equal("Active", active.Status); Assert.True(active.ActivatedAtUtc >= active.CreatedAtUtc);
        var queries = new SqlLendingQueries(new SqlConnectionFactory(fixture.ConnectionString));
        var persisted = await queries.GetAgreementAsync(pending.CreditAgreementId, default);
        Assert.Equal(active, persisted); Assert.Equal(1, await fixture.CountAsync(number));
    }

    [SqlFact]
    public async Task Duplicate_number_hits_SQL_unique_constraint_and_maps_to_409()
    {
        using var client = fixture.Factory.CreateClient(); var number = Number();
        using var first = await client.PostAsJsonAsync($"/api/accounts/{SqlFixture.AccountId}/credit-agreements", Request(number));
        using var duplicate = await client.PostAsJsonAsync($"/api/accounts/{SqlFixture.AccountId}/credit-agreements", Request(number));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode); Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(1, await fixture.CountAsync(number));
    }

    [SqlFact]
    public async Task Concurrent_activation_has_one_success_and_one_stale_precondition()
    {
        using var client = fixture.Factory.CreateClient();
        using var created = await client.PostAsJsonAsync($"/api/accounts/{SqlFixture.AccountId}/credit-agreements", Request(Number()));
        var agreement = (await created.Content.ReadFromJsonAsync<AgreementView>())!;
        var responses = await Task.WhenAll(Activate(client, agreement.CreditAgreementId, created.Headers.ETag!.ToString()),
            Activate(client, agreement.CreditAgreementId, created.Headers.ETag.ToString()));
        try
        {
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.PreconditionFailed);
            var successful = responses.Single(r => r.StatusCode == HttpStatusCode.OK);
            using var repeat = await Activate(client, agreement.CreditAgreementId, successful.Headers.ETag!.ToString());
            Assert.Equal(HttpStatusCode.OK, repeat.StatusCode); Assert.Equal(successful.Headers.ETag, repeat.Headers.ETag);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [SqlFact]
    public async Task Eligibility_failure_does_not_insert_and_uncommitted_insert_rolls_back()
    {
        using var client = fixture.Factory.CreateClient(); var number = Number();
        await fixture.ExecuteAsync("UPDATE lending.Customer SET Status = 'Inactive';");
        try
        {
            using var response = await client.PostAsJsonAsync($"/api/accounts/{SqlFixture.AccountId}/credit-agreements", Request(number));
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); Assert.Equal(0, await fixture.CountAsync(number));
        }
        finally { await fixture.ExecuteAsync("UPDATE lending.Customer SET Status = 'Active';"); }
        var abandoned = Number();
        var factory = new SqlAgreementSessionFactory(new SqlConnectionFactory(fixture.ConnectionString));
        await using (var session = await factory.BeginAsync(default))
        {
            var account = (await session.GetAccountEligibilityAsync(SqlFixture.AccountId, default))!;
            var product = (await session.GetProductEligibilityAsync(SqlFixture.ProductId, default))!;
            var a = CreditAgreement.Create(Guid.NewGuid(), abandoned, account, product,
                AgreementTerms.Create(AgreementType.FixedLoan, 100m, 0m, 1), DateTimeOffset.UtcNow);
            await session.InsertAsync(a, default); // Intentionally do not commit.
        }
        Assert.Equal(0, await fixture.CountAsync(abandoned));
    }

    [SqlFact]
    public async Task Product_catalogue_change_does_not_replace_accepted_terms()
    {
        using var client = fixture.Factory.CreateClient();
        using var created = await client.PostAsJsonAsync($"/api/accounts/{SqlFixture.AccountId}/credit-agreements", Request(Number()));
        var a = (await created.Content.ReadFromJsonAsync<AgreementView>())!;
        await fixture.ExecuteAsync("UPDATE lending.CreditProduct SET IsAvailable = 0, ProductType = 'RevolvingCredit';");
        try
        {
            using var activated = await Activate(client, a.CreditAgreementId, created.Headers.ETag!.ToString());
            Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
            var active = (await activated.Content.ReadFromJsonAsync<AgreementView>())!;
            Assert.Equal("FixedLoan", active.AgreementType); Assert.Equal(a.AgreedAmount, active.AgreedAmount); Assert.Equal(a.TermMonths, active.TermMonths);
        }
        finally { await fixture.ExecuteAsync("UPDATE lending.CreditProduct SET IsAvailable = 1, ProductType = 'FixedLoan';"); }
    }

    [SqlFact]
    public async Task Read_queries_filter_and_paginate_the_real_SQL_data()
    {
        using var client = fixture.Factory.CreateClient();
        var first = (await client.GetFromJsonAsync<Page<AccountView>>($"/api/accounts?customerId={SqlFixture.CustomerId}&pageSize=1"))!;
        Assert.Single(first.Items); Assert.Equal(SqlFixture.AccountId, first.Items[0].AccountId);
        var noAccounts = (await client.GetFromJsonAsync<Page<AccountView>>($"/api/accounts?customerId={Guid.NewGuid()}"))!; Assert.Empty(noAccounts.Items);
        var available = (await client.GetFromJsonAsync<Page<ProductView>>("/api/credit-products?available=true"))!; Assert.Single(available.Items);
        using var missing = await client.GetAsync($"/api/accounts/{Guid.NewGuid()}/credit-agreements"); Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var ready = await client.GetAsync("/health/ready"); Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [SqlFact]
    public async Task Database_rejects_invalid_fixed_loan_terms_even_for_direct_SQL_writes()
    {
        var number = Number();
        await Assert.ThrowsAsync<SqlException>(() => fixture.ExecuteAsync($"""
            INSERT INTO lending.CreditAgreement (AgreementNumber, AccountId, CreditProductId, AgreementType, AgreedAmount, AnnualInterestRatePercent, TermMonths)
            VALUES ('{number}', '{SqlFixture.AccountId}', '{SqlFixture.ProductId}', 'FixedLoan', 100, 0, NULL);
            """));
        Assert.Equal(0, await fixture.CountAsync(number));
    }

    [SqlFact]
    public async Task Eligibility_locks_prevent_parent_change_until_agreement_transaction_completes()
    {
        var sessions = new SqlAgreementSessionFactory(new SqlConnectionFactory(fixture.ConnectionString));
        await using var session = await sessions.BeginAsync(default);
        _ = await session.GetAccountEligibilityAsync(SqlFixture.AccountId, default);
        await using var connection = new SqlConnection(fixture.ConnectionString); await connection.OpenAsync();
        await using var update = new SqlCommand("SET LOCK_TIMEOUT 300; UPDATE lending.Account SET Status = 'Suspended';", connection);
        var error = await Assert.ThrowsAsync<SqlException>(() => update.ExecuteNonQueryAsync()); Assert.Equal(1222, error.Number);
        await session.CommitAsync(default);
        await fixture.ExecuteAsync("UPDATE lending.Account SET Status = 'Suspended';");
        await fixture.ExecuteAsync("UPDATE lending.Account SET Status = 'Open';");
    }
}
