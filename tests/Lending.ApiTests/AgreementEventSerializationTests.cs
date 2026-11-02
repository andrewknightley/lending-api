using Lending.Domain;
using Lending.Domain.Events;
using Lending.Infrastructure;

namespace Lending.ApiTests;

public sealed class AgreementEventSerializationTests
{
    [Fact]
    public void Schema_one_round_trip_preserves_precision_UTC_and_stable_type_names()
    {
        var created = new AgreementCreated(Guid.NewGuid(), DateTimeOffset.Parse("2026-10-28T12:34:56.1234567Z"),
            "AGR-JSON", Guid.NewGuid(), Guid.NewGuid(), AgreementType.FixedLoan, 10000.1234m, 7.512345m, 36);
        Assert.Equal("AgreementCreated", AgreementEventJson.TypeName(created));
        var json = AgreementEventJson.Serialize(created);
        Assert.Contains("\"agreementType\":\"FixedLoan\"", json);
        Assert.Equal(created, AgreementEventJson.Deserialize("AgreementCreated", 1, json));
        var activated = new AgreementActivated(created.CreditAgreementId, created.OccurredAtUtc.AddDays(1));
        Assert.Equal(activated, AgreementEventJson.Deserialize("AgreementActivated", 1, AgreementEventJson.Serialize(activated)));
    }

    [Fact]
    public void Unknown_contracts_fail_instead_of_silently_skipping_events()
    {
        Assert.Throws<InvalidOperationException>(() => AgreementEventJson.Deserialize("AgreementCreated", 2, "{}"));
        Assert.Throws<InvalidOperationException>(() => AgreementEventJson.Deserialize("FutureEvent", 1, "{}"));
    }
}
