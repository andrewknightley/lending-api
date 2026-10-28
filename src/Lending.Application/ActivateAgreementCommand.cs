namespace Lending.Application;

public sealed record ActivateAgreementCommand(Guid CreditAgreementId, byte[] ExpectedVersion);
