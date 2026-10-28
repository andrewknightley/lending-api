using Lending.Api;
using Lending.Application;
using Lending.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lending.ApiTests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public FakeStore Store { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAgreementSessionFactory>(); services.RemoveAll<ILendingQueries>();
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<IAgreementSessionFactory>(Store); services.AddSingleton<ILendingQueries>(Store);
            services.AddSingleton<TimeProvider>(new FixedClock());
        });
    }
}

internal sealed class FixedClock : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);
}

// Test boundary replacement: HTTP/application integration tests are separate from the real SQL tests.
public sealed class FakeStore : IAgreementSessionFactory, ILendingQueries
{
    public static readonly Guid AccountId = Guid.Parse("20000000-0000-4000-8000-000000000001");
    public static readonly Guid ProductId = Guid.Parse("30000000-0000-4000-8000-000000000001");
    public static readonly Guid CustomerId = Guid.Parse("10000000-0000-4000-8000-000000000001");
    public AccountEligibility Account { get; set; } = new(AccountId, CustomerId, "Open", "Active", "GBP");
    public ProductEligibility Product { get; set; } = new(ProductId, AgreementType.FixedLoan, true);
    private readonly Dictionary<Guid, StoredAgreement> agreements = [];
    private long version;

    public Task<IAgreementSession> BeginAsync(CancellationToken cancellationToken) => Task.FromResult<IAgreementSession>(new Session(this));
    private static StoredAgreement Clone(StoredAgreement stored)
    {
        return new(CreditAgreement.Replay(stored.Aggregate.History), [.. stored.RowVersion]);
    }

    private sealed class Session(FakeStore store) : IAgreementSession
    {
        private StoredAgreement? staged;
        public Task<AccountEligibility?> GetAccountEligibilityAsync(Guid id, CancellationToken cancellationToken)
            => Task.FromResult(id == AccountId ? store.Account : null);
        public Task<ProductEligibility?> GetProductEligibilityAsync(Guid id, CancellationToken cancellationToken)
            => Task.FromResult(id == ProductId ? store.Product : null);
        public Task<StoredAgreement?> GetAgreementForUpdateAsync(Guid id, CancellationToken cancellationToken)
            => Task.FromResult(store.agreements.TryGetValue(id, out var a) ? Clone(a) : null);
        public Task<StoredAgreement> InsertAsync(CreditAgreement agreement, CancellationToken cancellationToken)
        {
            if (store.agreements.Values.Any(a => string.Equals(a.Aggregate.AgreementNumber, agreement.AgreementNumber, StringComparison.OrdinalIgnoreCase)))
                throw new AgreementNumberConflictException();
            staged = new(agreement, BitConverter.GetBytes(++store.version)); return Task.FromResult(Clone(staged));
        }
        public Task<StoredAgreement> UpdateActivationAsync(CreditAgreement agreement, byte[] expectedVersion, CancellationToken cancellationToken)
        {
            if (!store.agreements[agreement.CreditAgreementId].RowVersion.SequenceEqual(expectedVersion)) throw new AgreementConcurrencyException();
            staged = new(agreement, BitConverter.GetBytes(++store.version)); return Task.FromResult(Clone(staged));
        }
        public Task CommitAsync(CancellationToken cancellationToken)
        {
            if (staged is not null) store.agreements[staged.Aggregate.CreditAgreementId] = Clone(staged);
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public Task<AgreementView?> GetAgreementAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult(agreements.TryGetValue(id, out var a) ? a.ToView() : null);
    public Task<AccountView?> GetAccountAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult(id == AccountId ? new AccountView(AccountId, CustomerId, "ACC-TEST", "GBP", Account.AccountStatus,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), null) : null);
    public Task<CustomerView?> GetCustomerAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult(id == CustomerId ? new CustomerView(CustomerId, "CUS-TEST", "Morgan", "Reed", Account.CustomerStatus) : null);
    public async Task<Page<CustomerView>> ListCustomersAsync(int offset, int pageSize, CancellationToken cancellationToken)
        => PageOf(new[] { (await GetCustomerAsync(CustomerId, cancellationToken))! }, offset, pageSize);
    public async Task<Page<AccountView>> ListAccountsAsync(Guid? customerId, int offset, int pageSize, CancellationToken cancellationToken)
        => PageOf(customerId is null || customerId == CustomerId ? new[] { (await GetAccountAsync(AccountId, cancellationToken))! } : [], offset, pageSize);
    public Task<Page<ProductView>> ListProductsAsync(bool? available, int offset, int pageSize, CancellationToken cancellationToken)
        => Task.FromResult(PageOf(available is null || available == Product.IsAvailable
            ? new[] { new ProductView(ProductId, "PL-TEST", "Test fixed loan", Product.ProductType.ToString(), Product.IsAvailable) } : [], offset, pageSize));
    public Task<Page<AgreementView>> ListAgreementsAsync(Guid accountId, int offset, int pageSize, CancellationToken cancellationToken)
        => Task.FromResult(PageOf(agreements.Values.Where(a => a.Aggregate.AccountId == accountId).Select(a => a.ToView()).OrderBy(a => a.AgreementNumber), offset, pageSize));
    private static Page<T> PageOf<T>(IEnumerable<T> rows, int offset, int size)
    {
        var all = rows.Skip(offset).Take(size + 1).ToArray(); return new Page<T>(all.Take(size).ToArray(), offset, size, all.Length > size);
    }
}
