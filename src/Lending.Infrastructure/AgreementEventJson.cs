using System.Text.Json;
using System.Text.Json.Serialization;
using Lending.Domain.Events;

namespace Lending.Infrastructure;

internal static class AgreementEventJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    internal static string TypeName(AgreementEvent value) => value switch
    {
        AgreementCreated => "AgreementCreated",
        AgreementActivated => "AgreementActivated",
        AgreementImported => "AgreementImported",
        _ => throw new InvalidOperationException("Unsupported agreement event type.")
    };

    internal static string Serialize(AgreementEvent value) => JsonSerializer.Serialize(value, value.GetType(), Options);

    internal static AgreementEvent Deserialize(string type, int schemaVersion, string payload)
    {
        if (schemaVersion != 1) throw new InvalidOperationException($"Unsupported agreement event schema {schemaVersion}.");
        Type eventType = type switch
        {
            "AgreementCreated" => typeof(AgreementCreated),
            "AgreementActivated" => typeof(AgreementActivated),
            "AgreementImported" => typeof(AgreementImported),
            _ => throw new InvalidOperationException($"Unsupported agreement event type {type}.")
        };
        return (AgreementEvent)(JsonSerializer.Deserialize(payload, eventType, Options)
            ?? throw new InvalidOperationException("Empty agreement event payload."));
    }
}
