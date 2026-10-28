namespace Lending.Application.Commands;

public sealed record ActivateAgreementCommand(Guid CreditAgreementId, byte[] ExpectedVersion);
