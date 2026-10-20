namespace Lending.Domain;

public sealed record AccountEligibility(Guid AccountId, Guid CustomerId, string AccountStatus,
    string CustomerStatus, string CurrencyCode)
{
    public void EnsureEligible()
    {
        if (CustomerStatus != "Active")
            throw new DomainConflictException("customer_ineligible", "Only an Active customer may create or activate an agreement.");
        if (AccountStatus != "Open")
            throw new DomainConflictException("account_ineligible", "Only an Open account may create or activate an agreement.");
        if (CurrencyCode != "GBP")
            throw new DomainConflictException("currency_unsupported", "Only GBP accounts are supported.");
    }
}

public sealed record ProductEligibility(Guid CreditProductId, AgreementType ProductType, bool IsAvailable)
{
    public void EnsureEligible(AgreementType agreementType)
    {
        if (!IsAvailable)
            throw new DomainConflictException("product_unavailable", "The selected credit product is not available.");
        if (ProductType != agreementType)
            throw new DomainConflictException("product_type_mismatch", "The agreement type must match the credit product type.");
    }
}
