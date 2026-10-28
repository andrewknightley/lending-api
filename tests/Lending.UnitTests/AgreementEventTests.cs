using Lending.Domain;
using Lending.Domain.Events;

namespace Lending.UnitTests;

public sealed class AgreementEventTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 10, 0, 0, TimeSpan.Zero);
    private static readonly AccountEligibility Account = new(Guid.NewGuid(), Guid.NewGuid(), "Open", "Active", "GBP");
    private static readonly ProductEligibility Product = new(Guid.NewGuid(), AgreementType.FixedLoan, true);
    private static CreditAgreement Create() => CreditAgreement.Create(Guid.NewGuid(), "AGR-EVENT", Account, Product,
        AgreementTerms.Create(AgreementType.FixedLoan, 10000.1234m, 7.512345m, 36), Now);

    [Fact]
    public void Creation_and_activation_produce_facts_with_accepted_terms()
    {
        var agreement = Create();
        var created = Assert.IsType<AgreementCreated>(Assert.Single(agreement.PendingEvents));
        Assert.Equal(10000.1234m, created.AgreedAmount);
        Assert.Equal(7.512345m, created.AnnualInterestRatePercent);
        Assert.Equal(1, agreement.Version);
        agreement.Activate(Account, Now.AddMinutes(1));
        Assert.IsType<AgreementActivated>(agreement.PendingEvents[1]);
        Assert.Equal(2, agreement.Version);
        Assert.False(agreement.Activate(Account, Now.AddMinutes(2)));
        Assert.Equal(2, agreement.PendingEvents.Count);
    }

    [Fact]
    public void Replay_recovers_state_without_emitting_events_or_rechecking_current_eligibility()
    {
        var original = Create(); original.Activate(Account, Now.AddMinutes(1));
        // Replay has no account/product lookup and needs no clock.
        var replayed = CreditAgreement.Replay(original.History);
        Assert.Equal(original.CreditAgreementId, replayed.CreditAgreementId);
        Assert.Equal(original.Terms, replayed.Terms);
        Assert.Equal(original.Status, replayed.Status);
        Assert.Equal(original.ActivatedAtUtc, replayed.ActivatedAtUtc);
        Assert.Equal(2, replayed.Version); Assert.Empty(replayed.PendingEvents);
        Assert.False(replayed.Activate(Account with { CustomerStatus = "Inactive" }, Now.AddHours(1)));
        Assert.Empty(replayed.PendingEvents);
    }

    [Fact]
    public void Replayed_pending_agreement_still_checks_eligibility_for_a_new_command()
    {
        var agreement = CreditAgreement.Replay(Create().History);
        Assert.Throws<DomainConflictException>(() => agreement.Activate(Account with { CustomerStatus = "Inactive" }, Now));
        Assert.Empty(agreement.PendingEvents); Assert.Equal(1, agreement.Version);
        Assert.True(agreement.Activate(Account, Now));
        Assert.Single(agreement.PendingEvents); Assert.Equal(2, agreement.Version);
    }

    [Theory]
    [InlineData(AgreementStatus.Pending)]
    [InlineData(AgreementStatus.Active)]
    [InlineData(AgreementStatus.Settled)]
    [InlineData(AgreementStatus.Cancelled)]
    public void Import_replays_known_state_without_inventing_creation_history(AgreementStatus status)
    {
        var id = Guid.NewGuid();
        DateTimeOffset? activated = status is AgreementStatus.Active or AgreementStatus.Settled ? Now.AddDays(-1) : null;
        var baseline = new AgreementImported(id, Now, "LEGACY", Account.AccountId, Product.CreditProductId,
            AgreementType.FixedLoan, 100m, 0m, 12, status, Now.AddDays(-2), activated);
        var agreement = CreditAgreement.Replay([baseline]);
        Assert.Equal(status, agreement.Status); Assert.Equal(activated, agreement.ActivatedAtUtc);
        Assert.Equal(Now.AddDays(-2), agreement.CreatedAtUtc); Assert.Equal(1, agreement.Version);
        Assert.Empty(agreement.PendingEvents); Assert.IsType<AgreementImported>(Assert.Single(agreement.History));
    }

    [Fact]
    public void Invalid_order_mixed_streams_and_duplicate_activation_fail_closed()
    {
        var original = Create(); var created = original.History[0];
        var activated = new AgreementActivated(original.CreditAgreementId, Now);
        Assert.Throws<InvalidOperationException>(() => CreditAgreement.Replay([]));
        Assert.Throws<InvalidOperationException>(() => CreditAgreement.Replay([activated]));
        Assert.Throws<InvalidOperationException>(() => CreditAgreement.Replay([created, created]));
        Assert.Throws<InvalidOperationException>(() => CreditAgreement.Replay([created, new AgreementActivated(Guid.NewGuid(), Now)]));
        Assert.Throws<InvalidOperationException>(() => CreditAgreement.Replay([created, activated, activated]));
    }
}
