using Lending.Domain;

namespace Lending.Application;

public sealed record CreateAgreementCommand(Guid AccountId, string AgreementNumber, Guid CreditProductId,
    AgreementType AgreementType, decimal AgreedAmount, decimal AnnualInterestRatePercent, int? TermMonths);
