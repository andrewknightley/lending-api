using Lending.Domain;

namespace Lending.Application;

public sealed class AgreementService(IAgreementSessionFactory sessionFactory, TimeProvider clock)
{
    public async Task<AgreementView> CreateAsync(
        CreateAgreementCommand command,
        CancellationToken cancellationToken = default)
    {
        var number = CreditAgreement.ValidateNumber(command.AgreementNumber);
        var errors = new Dictionary<string, string[]>();

        if (command.AccountId == Guid.Empty)
            errors["accountId"] = ["An account ID is required."];

        if (command.CreditProductId == Guid.Empty)
            errors["creditProductId"] = ["A credit product ID is required."];

        if (errors.Count != 0)
            throw new DomainValidationException(errors);

        var terms = AgreementTerms.Create(
            command.AgreementType,
            command.AgreedAmount,
            command.AnnualInterestRatePercent,
            command.TermMonths);

        await using var session = await sessionFactory.BeginAsync(cancellationToken);

        var account = await session.GetAccountEligibilityAsync(command.AccountId, cancellationToken)
                      ?? throw new EntityNotFoundException("Account", command.AccountId);
        var product = await session.GetProductEligibilityAsync(command.CreditProductId, cancellationToken)
                      ?? throw new EntityNotFoundException("CreditProduct", command.CreditProductId);
        var agreement = CreditAgreement.Create(Guid.NewGuid(), number, account, product, terms, clock.GetUtcNow());
        var stored = await session.InsertAsync(agreement, cancellationToken);

        await session.CommitAsync(cancellationToken);

        return stored.ToView();
    }

    public async Task<AgreementView> ActivateAsync(
        Guid id,
        byte[] expectedVersion,
        CancellationToken cancellationToken = default)
    {
        await using var session = await sessionFactory.BeginAsync(cancellationToken);
        var stored = await session.GetAgreementForUpdateAsync(id, cancellationToken)
                     ?? throw new EntityNotFoundException("CreditAgreement", id);

        if (!stored.RowVersion.AsSpan().SequenceEqual(expectedVersion))
            throw new AgreementConcurrencyException();

        if (stored.Aggregate.Status == AgreementStatus.Active)
        {
            await session.CommitAsync(cancellationToken);
            return stored.ToView(); // Idempotent with the current version; no duplicate update or timestamp change.
        }

        var account = await session.GetAccountEligibilityAsync(stored.Aggregate.AccountId, cancellationToken)
                      ?? throw new EntityNotFoundException("Account", stored.Aggregate.AccountId);
        stored.Aggregate.Activate(account, clock.GetUtcNow());
        var updated = await session.UpdateActivationAsync(stored.Aggregate, expectedVersion, cancellationToken);
        await session.CommitAsync(cancellationToken);
        return updated.ToView();
    }
}