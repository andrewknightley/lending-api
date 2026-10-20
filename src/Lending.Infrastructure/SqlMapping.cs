using System.Data;
using Lending.Application;
using Lending.Domain;
using Microsoft.Data.SqlClient;

namespace Lending.Infrastructure;

internal static class SqlMapping
{
    internal const string AgreementColumns = "CreditAgreementId, AgreementNumber, AccountId, CreditProductId, AgreementType, AgreedAmount, AnnualInterestRatePercent, TermMonths, Status, CreatedAtUtc, ActivatedAtUtc, RowVersion";
    internal static DateTimeOffset Utc(SqlDataReader reader, int ordinal)
        => new(DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc));

    internal static StoredAgreement Agreement(SqlDataReader reader)
    {
        var aggregate = CreditAgreement.Restore(reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.GetGuid(3),
            AgreementTerms.Create(Enum.Parse<AgreementType>(reader.GetString(4)), reader.GetDecimal(5), reader.GetDecimal(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7)), Enum.Parse<AgreementStatus>(reader.GetString(8)),
            Utc(reader, 9), reader.IsDBNull(10) ? null : Utc(reader, 10));
        return new StoredAgreement(aggregate, (byte[])reader.GetValue(11));
    }

    internal static SqlParameter GuidParameter(string name, Guid value) => new(name, SqlDbType.UniqueIdentifier) { Value = value };
    internal static SqlParameter DecimalParameter(string name, decimal value, byte precision, byte scale)
        => new(name, SqlDbType.Decimal) { Value = value, Precision = precision, Scale = scale };
}
