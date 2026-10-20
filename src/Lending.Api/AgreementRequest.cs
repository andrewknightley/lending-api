using System.ComponentModel.DataAnnotations;
using Lending.Application;
using Lending.Domain;

namespace Lending.Api;

public sealed record CreateAgreementRequest
{
    [Required, StringLength(30)] public string? AgreementNumber { get; init; }
    [Required] public Guid? CreditProductId { get; init; }
    [Required] public string? AgreementType { get; init; }
    [Required] public decimal? AgreedAmount { get; init; }
    [Required] public decimal? AnnualInterestRatePercent { get; init; }
    public int? TermMonths { get; init; }

    public CreateAgreementCommand ToCommand(Guid accountId)
    {
        var errors = new Dictionary<string, string[]>();
        if (AgreementNumber is null) errors["agreementNumber"] = ["Agreement number is required."];
        if (CreditProductId is null) errors["creditProductId"] = ["Product ID is required."];
        if (AgreedAmount is null) errors["agreedAmount"] = ["Amount is required."];
        if (AnnualInterestRatePercent is null) errors["annualInterestRatePercent"] = ["Rate is required."];
        if (!Enum.TryParse<AgreementType>(AgreementType, false, out var type) || !Enum.IsDefined(type)
                                                                              || AgreementType != type.ToString()) errors["agreementType"] = ["Use FixedLoan or RevolvingCredit."];
        if (errors.Count != 0) throw new DomainValidationException(errors);
        return new CreateAgreementCommand(accountId, AgreementNumber!, CreditProductId!.Value, type,
            AgreedAmount!.Value, AnnualInterestRatePercent!.Value, TermMonths);
    }
}

public static class AgreementEtags
{
    public static string FromVersion(string base64Version) => $"\"{base64Version}\"";
    public static bool TryParse(string value, out byte[] version)
    {
        version = [];
        if (value.Length < 3 || value[0] != '"' || value[^1] != '"') return false;
        try { version = Convert.FromBase64String(value[1..^1]); return version.Length == 8; }
        catch (FormatException) { return false; }
    }
}
