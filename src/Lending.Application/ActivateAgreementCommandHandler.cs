using Lending.Domain;

namespace Lending.Application;

public sealed class ActivateAgreementCommandHandler(IAgreementSessionFactory sessionFactory, TimeProvider clock)
{
    public async Task<AgreementView> HandleAsync(
        ActivateAgreementCommand command,
        CancellationToken cancellationToken = default)
    {
        await using var session = await sessionFactory.BeginAsync(cancellationToken);
        var stored = await session.GetAgreementForUpdateAsync(command.CreditAgreementId, cancellationToken)
                     ?? throw new EntityNotFoundException("CreditAgreement", command.CreditAgreementId);

        if (!stored.RowVersion.AsSpan().SequenceEqual(command.ExpectedVersion))
            throw new AgreementConcurrencyException();

        if (stored.Aggregate.Status == AgreementStatus.Active)
        {
            await session.CommitAsync(cancellationToken);
            return stored.ToView(); // Idempotent with the current version; no duplicate update or timestamp change.
        }

        var account = await session.GetAccountEligibilityAsync(stored.Aggregate.AccountId, cancellationToken)
                      ?? throw new EntityNotFoundException("Account", stored.Aggregate.AccountId);
        stored.Aggregate.Activate(account, clock.GetUtcNow());
        var updated = await session.UpdateActivationAsync(stored.Aggregate, command.ExpectedVersion, cancellationToken);
        await session.CommitAsync(cancellationToken);
        return updated.ToView();
    }
}
