namespace Lending.Domain;

public sealed class DomainValidationException(Dictionary<string, string[]> errors)
    : Exception("One or more agreement fields are invalid.")
{
    public Dictionary<string, string[]> Errors { get; } = errors;
}

public sealed class DomainConflictException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
