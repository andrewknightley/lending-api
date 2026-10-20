using Lending.Application;
using Microsoft.AspNetCore.Mvc;

namespace Lending.Api;

[ApiController]
[Route("api")]
public sealed class CreditAgreementsController(AgreementService service, ILendingQueries queries) : ControllerBase
{
    [HttpPost("accounts/{accountId:guid}/credit-agreements")]
    [ProducesResponseType<AgreementView>(StatusCodes.Status201Created)]
    public async Task<ActionResult<AgreementView>> Create(Guid accountId, CreateAgreementRequest request, CancellationToken cancellationToken)
    {
        var result = await service.CreateAsync(request.ToCommand(accountId), cancellationToken);
        Response.Headers.ETag = AgreementEtags.FromVersion(result.RowVersion);
        return CreatedAtAction(nameof(GetAgreement), new { id = result.CreditAgreementId }, result);
    }

    [HttpGet("credit-agreements/{id:guid}")]
    [ProducesResponseType<AgreementView>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AgreementView>> GetAgreement(Guid id, CancellationToken cancellationToken)
    {
        var result = await queries.GetAgreementAsync(id, cancellationToken) ?? throw new EntityNotFoundException("CreditAgreement", id);
        Response.Headers.ETag = AgreementEtags.FromVersion(result.RowVersion);
        return Ok(result);
    }

    [HttpPost("credit-agreements/{id:guid}/activate")]
    [ProducesResponseType<AgreementView>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AgreementView>> Activate(Guid id, [FromHeader(Name = "If-Match")] string? ifMatch, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ifMatch))
            return Problem(statusCode: 428, title: "Precondition required", detail: "Supply the agreement's current ETag in the If-Match header.");
        if (!AgreementEtags.TryParse(ifMatch, out var version))
            return Problem(statusCode: 400, title: "Invalid If-Match header", detail: "Supply one strong, quoted ETag returned by this API. Wildcards and weak ETags are not supported.");
        var result = await service.ActivateAsync(id, version, cancellationToken);
        Response.Headers.ETag = AgreementEtags.FromVersion(result.RowVersion);
        return Ok(result);
    }

    [HttpGet("accounts/{accountId:guid}/credit-agreements")]
    public async Task<ActionResult<Page<AgreementView>>> List(Guid accountId, [FromQuery] int offset = 0,
        [FromQuery] int pageSize = 50, CancellationToken cancellationToken = default)
    {
        if (offset < 0 || pageSize is < 1 or > 100)
            return Problem(statusCode: 400, title: "Invalid pagination", detail: "Offset must be non-negative and pageSize must be between 1 and 100.");
        _ = await queries.GetAccountAsync(accountId, cancellationToken) ?? throw new EntityNotFoundException("Account", accountId);
        return Ok(await queries.ListAgreementsAsync(accountId, offset, pageSize, cancellationToken));
    }
}
