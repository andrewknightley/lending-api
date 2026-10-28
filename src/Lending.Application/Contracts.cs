using Lending.Domain;

namespace Lending.Application;

public sealed record AgreementView(Guid CreditAgreementId, string AgreementNumber, Guid AccountId,
    Guid CreditProductId, string AgreementType, decimal AgreedAmount, decimal AnnualInterestRatePercent,
    int? TermMonths, string Status, DateTimeOffset CreatedAtUtc, DateTimeOffset? ActivatedAtUtc, string RowVersion);

public sealed record CustomerView(Guid CustomerId, string CustomerNumber, string FirstName, string LastName, string Status);
public sealed record AccountView(Guid AccountId, Guid CustomerId, string AccountNumber, string CurrencyCode,
    string Status, DateTimeOffset OpenedAtUtc, DateTimeOffset? ClosedAtUtc);
public sealed record ProductView(Guid CreditProductId, string ProductCode, string ProductName, string ProductType, bool IsAvailable);
public sealed record Page<T>(IReadOnlyList<T> Items, int Offset, int PageSize, bool HasMore);

public sealed record StoredAgreement(CreditAgreement Aggregate, byte[] RowVersion)
{
    public AgreementView ToView() => new(Aggregate.CreditAgreementId, Aggregate.AgreementNumber, Aggregate.AccountId,
        Aggregate.CreditProductId, Aggregate.Terms.Type.ToString(), Aggregate.Terms.Amount,
        Aggregate.Terms.AnnualInterestRatePercent, Aggregate.Terms.TermMonths, Aggregate.Status.ToString(),
        Aggregate.CreatedAtUtc, Aggregate.ActivatedAtUtc, Convert.ToBase64String(RowVersion));
}
