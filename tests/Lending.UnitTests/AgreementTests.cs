using Lending.Domain;
using Lending.Domain.Events;

namespace Lending.UnitTests;

public sealed class AgreementTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 14, 0, 0, TimeSpan.Zero);
    private static readonly AccountEligibility Account = new(Guid.NewGuid(), Guid.NewGuid(), "Open", "Active", "GBP");
    private static readonly ProductEligibility Product = new(Guid.NewGuid(), AgreementType.FixedLoan, true);
    private static AgreementTerms Terms() => AgreementTerms.Create(AgreementType.FixedLoan, 10000m, 7.5m, 36);
    private static CreditAgreement NewAgreement() => CreditAgreement.Create(Guid.NewGuid(), "AGR-1001", Account, Product, Terms(), Now);

    [Fact]
    public void Creation_snapshots_terms_and_starts_pending()
    {
        var a = NewAgreement();
        Assert.Equal(AgreementStatus.Pending, a.Status); Assert.Null(a.ActivatedAtUtc);
        Assert.Equal(10000m, a.Terms.Amount); Assert.Equal(7.5m, a.Terms.AnnualInterestRatePercent);
        Assert.Equal(36, a.Terms.TermMonths); Assert.Equal(Now, a.CreatedAtUtc);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("0.00001")]
    [InlineData("1000000000000000")]
    public void Amount_must_fit_schema_without_rounding(string amount)
        => Assert.Contains("agreedAmount", Assert.Throws<DomainValidationException>(() =>
            AgreementTerms.Create(AgreementType.FixedLoan, decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), 7.5m, 36)).Errors.Keys);

    [Theory]
    [InlineData("-0.01")]
    [InlineData("0.0000001")]
    [InlineData("1000")]
    public void Rate_must_fit_schema_without_rounding(string rate)
        => Assert.Contains("annualInterestRatePercent", Assert.Throws<DomainValidationException>(() =>
            AgreementTerms.Create(AgreementType.FixedLoan, 100m, decimal.Parse(rate, System.Globalization.CultureInfo.InvariantCulture), 12)).Errors.Keys);

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void Fixed_loan_requires_positive_term(int? term)
        => Assert.Throws<DomainValidationException>(() => AgreementTerms.Create(AgreementType.FixedLoan, 100m, 0m, term));

    [Fact]
    public void Revolving_credit_requires_null_term()
    {
        Assert.Throws<DomainValidationException>(() => AgreementTerms.Create(AgreementType.RevolvingCredit, 100m, 0m, 12));
        Assert.Null(AgreementTerms.Create(AgreementType.RevolvingCredit, 100m, 0m, null).TermMonths);
    }

    [Theory]
    [InlineData("Inactive", "Open", "GBP", "customer_ineligible")]
    [InlineData("Deceased", "Open", "GBP", "customer_ineligible")]
    [InlineData("Active", "Suspended", "GBP", "account_ineligible")]
    [InlineData("Active", "Closed", "GBP", "account_ineligible")]
    [InlineData("Active", "Open", "EUR", "currency_unsupported")]
    public void Parent_eligibility_is_enforced(string customer, string account, string currency, string code)
    {
        var eligibility = Account with { CustomerStatus = customer, AccountStatus = account, CurrencyCode = currency };
        var error = Assert.Throws<DomainConflictException>(() => CreditAgreement.Create(Guid.NewGuid(), "AGR-1", eligibility, Product, Terms(), Now));
        Assert.Equal(code, error.Code);
    }

    [Fact]
    public void Unavailable_or_mismatched_product_is_rejected()
    {
        Assert.Equal("product_unavailable", Assert.Throws<DomainConflictException>(() =>
            CreditAgreement.Create(Guid.NewGuid(), "AGR-1", Account, Product with { IsAvailable = false }, Terms(), Now)).Code);
        Assert.Equal("product_type_mismatch", Assert.Throws<DomainConflictException>(() =>
            CreditAgreement.Create(Guid.NewGuid(), "AGR-1", Account, Product with { ProductType = AgreementType.RevolvingCredit }, Terms(), Now)).Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("1234567890123456789012345678901")]
    [InlineData("AGR-£")]
    [InlineData("AGR-\n1")]
    public void Business_number_is_safe_for_existing_varchar_column(string number)
        => Assert.Throws<DomainValidationException>(() => CreditAgreement.ValidateNumber(number));

    [Fact]
    public void Activation_preserves_terms_and_repeating_it_does_not_reset_time()
    {
        var a = NewAgreement(); var terms = a.Terms;
        Assert.True(a.Activate(Account, Now.AddMinutes(1)));
        Assert.Equal(AgreementStatus.Active, a.Status); Assert.Equal(Now.AddMinutes(1), a.ActivatedAtUtc);
        Assert.Same(terms, a.Terms);
        Assert.False(a.Activate(Account, Now.AddMinutes(2)));
        Assert.Equal(Now.AddMinutes(1), a.ActivatedAtUtc);
    }

    [Theory]
    [InlineData(AgreementStatus.Cancelled)]
    [InlineData(AgreementStatus.Settled)]
    public void Other_terminal_states_cannot_be_activated(AgreementStatus status)
    {
        var a = CreditAgreement.Replay([new AgreementImported(Guid.NewGuid(), Now, "AGR-1", Account.AccountId,
            Product.CreditProductId, AgreementType.FixedLoan, 10000m, 7.5m, 36, status,
            Now, status == AgreementStatus.Settled ? Now : null)]);
        Assert.Equal("invalid_agreement_transition", Assert.Throws<DomainConflictException>(() => a.Activate(Account, Now)).Code);
    }

    [Fact]
    public void Activation_rechecks_parent_eligibility_and_time()
    {
        var a = NewAgreement();
        Assert.Throws<DomainConflictException>(() => a.Activate(Account with { AccountStatus = "Suspended" }, Now));
        Assert.Throws<DomainConflictException>(() => a.Activate(Account, Now.AddSeconds(-1)));
        Assert.Equal(AgreementStatus.Pending, a.Status); Assert.Null(a.ActivatedAtUtc);
    }

    [Fact]
    public void Maximum_SQL_decimal_values_and_zero_rate_are_accepted()
    {
        var terms = AgreementTerms.Create(AgreementType.FixedLoan, 999999999999999.9999m, 999.999999m, 1);
        Assert.Equal(999999999999999.9999m, terms.Amount);
        Assert.Equal(0m, AgreementTerms.Create(AgreementType.FixedLoan, 0.0001m, 0m, 1).AnnualInterestRatePercent);
    }
}
