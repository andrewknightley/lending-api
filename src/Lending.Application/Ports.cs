using Lending.Domain;

namespace Lending.Application;

// One short transaction covers reading eligibility, enforcing rules and persisting the outcome.
public interface IAgreementSessionFactory
{
    Task<IAgreementSession> BeginAsync(CancellationToken cancellationToken);
}

public interface IAgreementSession : IAsyncDisposable
{
    Task<AccountEligibility?> GetAccountEligibilityAsync(Guid accountId, CancellationToken cancellationToken);
    Task<ProductEligibility?> GetProductEligibilityAsync(Guid productId, CancellationToken cancellationToken);
    Task<StoredAgreement?> GetAgreementForUpdateAsync(Guid agreementId, CancellationToken cancellationToken);
    Task<StoredAgreement> InsertAsync(CreditAgreement agreement, CancellationToken cancellationToken);
    Task<StoredAgreement> UpdateActivationAsync(CreditAgreement agreement, byte[] expectedVersion, CancellationToken cancellationToken);
    Task CommitAsync(CancellationToken cancellationToken);
}

// Read queries have independent DTOs and SQL; this phase still shares the same database.
public interface ILendingQueries
{
    Task<AgreementView?> GetAgreementAsync(Guid id, CancellationToken cancellationToken);
    Task<AccountView?> GetAccountAsync(Guid id, CancellationToken cancellationToken);
    Task<CustomerView?> GetCustomerAsync(Guid id, CancellationToken cancellationToken);
    Task<Page<CustomerView>> ListCustomersAsync(int offset, int pageSize, CancellationToken cancellationToken);
    Task<Page<AccountView>> ListAccountsAsync(Guid? customerId, int offset, int pageSize, CancellationToken cancellationToken);
    Task<Page<ProductView>> ListProductsAsync(bool? available, int offset, int pageSize, CancellationToken cancellationToken);
    Task<Page<AgreementView>> ListAgreementsAsync(Guid accountId, int offset, int pageSize, CancellationToken cancellationToken);
}
