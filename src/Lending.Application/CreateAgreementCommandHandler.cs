using Lending.Domain;

namespace Lending.Application;

public sealed class CreateAgreementCommandHandler(IAgreementSessionFactory sessionFactory, TimeProvider clock)
{
    public async Task<AgreementView> HandleAsync(
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
}
