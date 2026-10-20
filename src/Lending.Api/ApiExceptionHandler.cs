using System.Diagnostics;
using Lending.Application;
using Lending.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace Lending.Api;

public sealed class ApiExceptionHandler(IProblemDetailsService problems, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested) return false;
        var (status, code, title, detail) = exception switch
        {
            DomainValidationException => (400, "validation_failed", "Invalid agreement", exception.Message),
            EntityNotFoundException => (404, "not_found", "Resource not found", exception.Message),
            DomainConflictException e => (409, e.Code, "Business rule conflict", e.Message),
            AgreementNumberConflictException => (409, "agreement_number_exists", "Agreement number already exists", exception.Message),
            AgreementConcurrencyException => (412, "agreement_version_changed", "Precondition failed", exception.Message),
            SqlException e when e.Number is -2 or 0 or 20 or 53 or 64 or 233 or 1205 or 4060 or 18456 or 10060 or 10061 or 11001
                => (503, "database_unavailable", "Database unavailable", "The database is unavailable or busy. Retry later."),
            _ => (500, "unexpected_error", "Unexpected error", "An unexpected error occurred.")
        };
        if (status >= 500) logger.LogError(exception, "Request failed with error code {ErrorCode}", code);
        ProblemDetails problem = exception is DomainValidationException validation
            ? new ValidationProblemDetails(validation.Errors) : new ProblemDetails();
        problem.Status = status; problem.Title = title; problem.Detail = detail;
        problem.Type = $"urn:ak-lending:problem:{code}"; problem.Instance = context.Request.Path;
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier;
        context.Response.StatusCode = status;
        await problems.WriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem });
        return true;
    }
}
