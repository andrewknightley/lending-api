namespace Lending.Domain;

public enum AgreementType { FixedLoan, RevolvingCredit }
public enum AgreementStatus { Pending, Active, Settled, Cancelled }

// Accepted terms belong to the agreement; later catalogue edits cannot replace them.
public sealed record AgreementTerms
{
    public AgreementType Type { get; }
    public decimal Amount { get; }
    public decimal AnnualInterestRatePercent { get; }
    public int? TermMonths { get; }

    private AgreementTerms(AgreementType type, decimal amount, decimal rate, int? termMonths)
        => (Type, Amount, AnnualInterestRatePercent, TermMonths) = (type, amount, rate, termMonths);

    public static AgreementTerms Create(AgreementType type, decimal amount, decimal rate, int? termMonths)
    {
        var errors = new Dictionary<string, string[]>();
        if (!Enum.IsDefined(type)) errors["agreementType"] = ["Use FixedLoan or RevolvingCredit."];
        if (amount <= 0 || amount > 999999999999999.9999m || decimal.Round(amount, 4) != amount)
            errors["agreedAmount"] = ["Amount must be positive and fit decimal(19,4), with at most four decimal places."];
        if (rate < 0 || rate > 999.999999m || decimal.Round(rate, 6) != rate)
            errors["annualInterestRatePercent"] = ["Rate must be non-negative and fit decimal(9,6), with at most six decimal places."];
        if (type == AgreementType.FixedLoan && (termMonths is null || termMonths <= 0))
            errors["termMonths"] = ["A fixed loan requires a positive term in months."];
        if (type == AgreementType.RevolvingCredit && termMonths is not null)
            errors["termMonths"] = ["Revolving credit must have a null term."];
        if (errors.Count != 0) throw new DomainValidationException(errors);
        return new AgreementTerms(type, amount, rate, termMonths);
    }
}
