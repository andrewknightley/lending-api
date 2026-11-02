using Lending.Domain.Events;

namespace Lending.Domain;

public sealed class CreditAgreement
{
    private readonly List<AgreementEvent> pendingEvents = [];
    private readonly List<AgreementEvent> history = [];
    public IReadOnlyList<AgreementEvent> History => history.AsReadOnly();
    public long Version { get; private set; }
    public IReadOnlyList<AgreementEvent> PendingEvents => pendingEvents.AsReadOnly();
    private CreditAgreement() { AgreementNumber = ""; Terms = null!; }

    public Guid CreditAgreementId { get; private set; }
    public string AgreementNumber { get; private set; }
    public Guid AccountId { get; private set; }
    public Guid CreditProductId { get; private set; }
    public AgreementTerms Terms { get; private set; }
    public AgreementStatus Status { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? ActivatedAtUtc { get; private set; }

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

        var agreement = new CreditAgreement();
        agreement.Record(new AgreementCreated(id, now.ToUniversalTime(), number, account.AccountId,
            product.CreditProductId, terms.Type, terms.Amount, terms.AnnualInterestRatePercent, terms.TermMonths));
        return agreement;
    }

    public bool Activate(AccountEligibility account, DateTimeOffset now)
    {
        if (account.AccountId != AccountId) throw new ArgumentException("Eligibility must belong to this account.", nameof(account));
        if (Status == AgreementStatus.Active) return false;
        if (Status != AgreementStatus.Pending)
            throw new DomainConflictException("invalid_agreement_transition", "Only a Pending agreement can be activated.");
        account.EnsureEligible();
        if (now < CreatedAtUtc)
            throw new DomainConflictException("invalid_activation_time", "Activation cannot precede agreement creation.");

        Record(new AgreementActivated(CreditAgreementId, now.ToUniversalTime()));

        return true;
    }
    public static CreditAgreement Replay(IEnumerable<AgreementEvent> history)
    {
        var agreement = new CreditAgreement();
        foreach (var @event in history) agreement.Apply(@event);
        if (agreement.Version == 0) throw new InvalidOperationException("An agreement stream cannot be empty.");
        return agreement;
    }

    private void Record(AgreementEvent @event)
    {
        Apply(@event);
        pendingEvents.Add(@event);
    }

    // Replay applies historical facts. It must not consult current eligibility, a clock or emit events.
    private void Apply(AgreementEvent @event)
    {
        if (Version > 0 && @event.CreditAgreementId != CreditAgreementId)
            throw new InvalidOperationException("Event belongs to a different agreement.");
        switch (@event)
        {
            case AgreementCreated e when Version == 0:
                SetInitial(e.CreditAgreementId, e.AgreementNumber, e.AccountId, e.CreditProductId,
                    e.AgreementType, e.AgreedAmount, e.AnnualInterestRatePercent, e.TermMonths,
                    AgreementStatus.Pending, e.OccurredAtUtc, null);
                break;
            case AgreementImported e when Version == 0:
                SetInitial(e.CreditAgreementId, e.AgreementNumber, e.AccountId, e.CreditProductId,
                    e.AgreementType, e.AgreedAmount, e.AnnualInterestRatePercent, e.TermMonths,
                    e.Status, e.CreatedAtUtc, e.ActivatedAtUtc);
                break;
            case AgreementActivated e when Version > 0 && Status == AgreementStatus.Pending:
                Status = AgreementStatus.Active;
                ActivatedAtUtc = e.OccurredAtUtc.ToUniversalTime();
                break;
            default:
                throw new InvalidOperationException("Unsupported event or invalid agreement event order.");
        }
        history.Add(@event);
        Version++;
    }

    private void SetInitial(Guid id, string number, Guid accountId, Guid productId, AgreementType type,
        decimal amount, decimal rate, int? term, AgreementStatus status, DateTimeOffset created, DateTimeOffset? activated)
    {
        CreditAgreementId = id; AgreementNumber = number; AccountId = accountId; CreditProductId = productId;
        Terms = AgreementTerms.FromHistory(type, amount, rate, term);
        Status = status; CreatedAtUtc = created.ToUniversalTime(); ActivatedAtUtc = activated?.ToUniversalTime();
    }
}
