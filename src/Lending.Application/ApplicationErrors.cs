namespace Lending.Application;

public sealed class EntityNotFoundException(string entity, Guid id) : Exception($"{entity} '{id}' was not found.");
public sealed class AgreementNumberConflictException() : Exception("An agreement with this agreement number already exists.");
public sealed class AgreementConcurrencyException() : Exception("The agreement changed. Fetch it again and retry with its current ETag.");
