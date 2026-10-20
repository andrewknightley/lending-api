namespace Lending.Domain;

public sealed class CreditAgreement
{
    public Guid CreditAgreementId { get; }
    public string AgreementNumber { get; }
    public Guid AccountId { get; }
    public Guid CreditProductId { get; }
    public AgreementTerms Terms { get; }
    public AgreementStatus Status { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset? ActivatedAtUtc { get; private set; }

    private CreditAgreement(Guid id, string number, Guid accountId, Guid productId, AgreementTerms terms,
        AgreementStatus status, DateTimeOffset createdAtUtc, DateTimeOffset? activatedAtUtc)
    {
        CreditAgreementId = id; AgreementNumber = number; AccountId = accountId;
        CreditProductId = productId; Terms = terms; Status = status;
        CreatedAtUtc = createdAtUtc.ToUniversalTime(); ActivatedAtUtc = activatedAtUtc?.ToUniversalTime();
    }

    public static string ValidateNumber(string? number)
    {
        var normalized = number?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 30 || normalized.Any(c => c < 32 || c > 126))
            throw new DomainValidationException(new() { ["agreementNumber"] = ["Use 1–30 printable ASCII characters."] });
        return normalized;
    }

    public static CreditAgreement Create(Guid id, string number, AccountEligibility account,
        ProductEligibility product, AgreementTerms terms, DateTimeOffset now)
    {
        number = ValidateNumber(number);
        if (id == Guid.Empty)
            throw new ArgumentException("An agreement identity is required.", nameof(id));

        account.EnsureEligible();
        product.EnsureEligible(terms.Type);

        return new CreditAgreement(id, number, account.AccountId, product.CreditProductId, terms,
            AgreementStatus.Pending, now, null);
    }

    // Hydration is separate from creation: changed parent/catalogue state must not erase accepted terms.
    public static CreditAgreement Restore(Guid id, string number, Guid accountId, Guid productId,
        AgreementTerms terms, AgreementStatus status, DateTimeOffset createdAtUtc, DateTimeOffset? activatedAtUtc)
        => new(id, number, accountId, productId, terms, status, createdAtUtc, activatedAtUtc);

    public bool Activate(AccountEligibility account, DateTimeOffset now)
    {
        if (account.AccountId != AccountId) throw new ArgumentException("Eligibility must belong to this account.", nameof(account));
        if (Status == AgreementStatus.Active) return false;
        if (Status != AgreementStatus.Pending)
            throw new DomainConflictException("invalid_agreement_transition", "Only a Pending agreement can be activated.");
        account.EnsureEligible();
        if (now < CreatedAtUtc)
            throw new DomainConflictException("invalid_activation_time", "Activation cannot precede agreement creation.");

        Status = AgreementStatus.Active;
        ActivatedAtUtc = now.ToUniversalTime();

        return true;
    }
}
