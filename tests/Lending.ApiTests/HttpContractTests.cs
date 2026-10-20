using System.Net;
using System.Net.Http.Json;
using Lending.Application;
using Lending.Domain;

namespace Lending.ApiTests;

public sealed class HttpContractTests
{
    private static object Request(string number = "AGR-HTTP", string type = "FixedLoan", decimal amount = 10000m,
        decimal rate = 7.5m, int? term = 36, Guid? productId = null)
        => new { agreementNumber = number, creditProductId = productId ?? FakeStore.ProductId, agreementType = type,
            agreedAmount = amount, annualInterestRatePercent = rate, termMonths = term };
    private static async Task<HttpResponseMessage> Activate(HttpClient client, Guid id, string? etag)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/credit-agreements/{id}/activate");
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Create_get_activate_and_repeat_preserve_terms_and_return_ETags()
    {
        await using var factory = new ApiFactory(); using var client = factory.CreateClient();
        using var created = await client.PostAsJsonAsync($"/api/accounts/{FakeStore.AccountId}/credit-agreements", Request());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); Assert.NotNull(created.Headers.Location); Assert.NotNull(created.Headers.ETag);
        var pending = (await created.Content.ReadFromJsonAsync<AgreementView>())!;
        Assert.Equal("Pending", pending.Status); Assert.Null(pending.ActivatedAtUtc);
        using var get = await client.GetAsync(created.Headers.Location); Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(created.Headers.ETag, get.Headers.ETag);
        using var activated = await Activate(client, pending.CreditAgreementId, created.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.OK, activated.StatusCode); Assert.NotEqual(created.Headers.ETag, activated.Headers.ETag);
        var active = (await activated.Content.ReadFromJsonAsync<AgreementView>())!;
        Assert.Equal("Active", active.Status); Assert.NotNull(active.ActivatedAtUtc);
        Assert.Equal(pending.AgreedAmount, active.AgreedAmount); Assert.Equal(pending.AnnualInterestRatePercent, active.AnnualInterestRatePercent);
        using var repeated = await Activate(client, pending.CreditAgreementId, activated.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode); Assert.Equal(activated.Headers.ETag, repeated.Headers.ETag);
        Assert.Equal(active.ActivatedAtUtc, (await repeated.Content.ReadFromJsonAsync<AgreementView>())!.ActivatedAtUtc);
        using var stale = await Activate(client, pending.CreditAgreementId, created.Headers.ETag.ToString());
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
    }

    [Theory]
    [InlineData("Unknown", 100, 7.5, 12)]
    [InlineData("0", 100, 7.5, 12)]
    [InlineData("FixedLoan", 0, 7.5, 12)]
    [InlineData("FixedLoan", 100, -1, 12)]
    [InlineData("FixedLoan", 100, 7.5, 0)]
    [InlineData("RevolvingCredit", 100, 7.5, 12)]
    public async Task Invalid_terms_return_validation_problem(string type, double amount, double rate, int term)
    {
        await using var factory = new ApiFactory(); using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync($"/api/accounts/{FakeStore.AccountId}/credit-agreements", Request(type: type, amount: (decimal)amount, rate: (decimal)rate, term: term));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("errors", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Missing_rate_is_rejected_instead_of_assumed_zero()
    {
        await using var factory = new ApiFactory(); using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync($"/api/accounts/{FakeStore.AccountId}/credit-agreements",
            new { agreementNumber = "AGR-MISSING", creditProductId = FakeStore.ProductId, agreementType = "FixedLoan", agreedAmount = 100, termMonths = 12 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("Inactive", "Open", true, "customer_ineligible")]
    [InlineData("Active", "Suspended", true, "account_ineligible")]
    [InlineData("Active", "Open", false, "product_unavailable")]
    public async Task Eligibility_failures_are_conflicts(string customer, string account, bool available, string code)
    {
        await using var factory = new ApiFactory(); factory.Store.Account = factory.Store.Account with { CustomerStatus = customer, AccountStatus = account };
        factory.Store.Product = factory.Store.Product with { IsAvailable = available }; using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync($"/api/accounts/{FakeStore.AccountId}/credit-agreements", Request());
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); Assert.Contains(code, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Mismatched_product_and_duplicate_business_number_return_conflict()
    {
        await using var factory = new ApiFactory(); using var client = factory.CreateClient();
        factory.Store.Product = factory.Store.Product with { ProductType = AgreementType.RevolvingCredit };
        using var mismatch = await client.PostAsJsonAsync($"/api/accounts/{FakeStore.AccountId}/credit-agreements", Request());
        Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
        factory.Store.Product = factory.Store.Product with { ProductType = AgreementType.FixedLoan };
        using var first = await client.PostAsJsonAsync($"/api/accounts/{FakeStore.AccountId}/credit-agreements", Request()); Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var duplicate = await client.PostAsJsonAsync($"/api/accounts/{FakeStore.AccountId}/credit-agreements", Request()); Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task Missing_references_and_unknown_agreement_return_not_found()
    {
        await using var factory = new ApiFactory(); using var client = factory.CreateClient();
        using var account = await client.PostAsJsonAsync($"/api/accounts/{Guid.NewGuid()}/credit-agreements", Request()); Assert.Equal(HttpStatusCode.NotFound, account.StatusCode);
        using var product = await client.PostAsJsonAsync($"/api/accounts/{FakeStore.AccountId}/credit-agreements", Request(productId: Guid.NewGuid())); Assert.Equal(HttpStatusCode.NotFound, product.StatusCode);
        using var agreement = await client.GetAsync($"/api/credit-agreements/{Guid.NewGuid()}"); Assert.Equal(HttpStatusCode.NotFound, agreement.StatusCode);
    }

    [Theory]
    [InlineData(null, 428)]
    [InlineData("*", 400)]
    [InlineData("W/\"AAAAAAAAAAA=\"", 400)]
    [InlineData("\"bad\"", 400)]
    public async Task Activation_requires_one_strong_rowversion_ETag(string? etag, int status)
    {
        await using var factory = new ApiFactory(); using var client = factory.CreateClient();
        using var response = await Activate(client, Guid.NewGuid(), etag); Assert.Equal(status, (int)response.StatusCode);
    }

    [Fact]
    public async Task Activation_rechecks_customer_and_account_but_not_catalogue_availability()
    {
        await using var factory = new ApiFactory(); using var client = factory.CreateClient();
        using var created = await client.PostAsJsonAsync($"/api/accounts/{FakeStore.AccountId}/credit-agreements", Request());
        var agreement = (await created.Content.ReadFromJsonAsync<AgreementView>())!;
        factory.Store.Account = factory.Store.Account with { AccountStatus = "Suspended" };
        using var refused = await Activate(client, agreement.CreditAgreementId, created.Headers.ETag!.ToString()); Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        factory.Store.Account = factory.Store.Account with { AccountStatus = "Open" }; factory.Store.Product = factory.Store.Product with { IsAvailable = false };
        using var accepted = await Activate(client, agreement.CreditAgreementId, created.Headers.ETag.ToString()); Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task Lists_pagination_and_development_OpenAPI_are_available()
    {
        await using var factory = new ApiFactory(); using var client = factory.CreateClient();
        foreach (var route in new[] { "/api/customers", "/api/accounts", "/api/credit-products?available=true", $"/api/accounts/{FakeStore.AccountId}/credit-agreements", "/openapi/v1.json", "/health/live" })
        { using var response = await client.GetAsync(route); Assert.Equal(HttpStatusCode.OK, response.StatusCode); }
        using var badPage = await client.GetAsync("/api/accounts?pageSize=101"); Assert.Equal(HttpStatusCode.BadRequest, badPage.StatusCode);
        using var badOffset = await client.GetAsync("/api/customers?offset=-1"); Assert.Equal(HttpStatusCode.BadRequest, badOffset.StatusCode);
    }
}
