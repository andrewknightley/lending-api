namespace Lending.Domain.Events;

public abstract record AgreementEvent(Guid CreditAgreementId, DateTimeOffset OccurredAtUtc);

public sealed record AgreementCreated(Guid CreditAgreementId, DateTimeOffset OccurredAtUtc,
    string AgreementNumber, Guid AccountId, Guid CreditProductId, AgreementType AgreementType,
    decimal AgreedAmount, decimal AnnualInterestRatePercent, int? TermMonths)
    : AgreementEvent(CreditAgreementId, OccurredAtUtc);

public sealed record AgreementActivated(Guid CreditAgreementId, DateTimeOffset OccurredAtUtc)
    : AgreementEvent(CreditAgreementId, OccurredAtUtc);

// A migration baseline records known state; it does not invent a missing creation/activation history.
public sealed record AgreementImported(Guid CreditAgreementId, DateTimeOffset OccurredAtUtc,
    string AgreementNumber, Guid AccountId, Guid CreditProductId, AgreementType AgreementType,
    decimal AgreedAmount, decimal AnnualInterestRatePercent, int? TermMonths,
    AgreementStatus Status, DateTimeOffset CreatedAtUtc, DateTimeOffset? ActivatedAtUtc)
    : AgreementEvent(CreditAgreementId, OccurredAtUtc);
