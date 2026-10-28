using Lending.Application;
using Microsoft.AspNetCore.Mvc;

namespace Lending.Api.Controllers;

[ApiController]
[Route("api/accounts")]
public sealed class AccountsController(ILendingQueries queries) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<Page<AccountView>>> Accounts([FromQuery] Guid? customerId = null, [FromQuery] int offset = 0,
        [FromQuery] int pageSize = 50, CancellationToken cancellationToken = default)
    {
        if (offset < 0 || pageSize is < 1 or > 100)
            return Problem(statusCode: 400, title: "Invalid pagination", detail: "Offset must be non-negative and pageSize must be between 1 and 100.");
        return Ok(await queries.ListAccountsAsync(customerId, offset, pageSize, cancellationToken));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AccountView>> Account(Guid id, CancellationToken cancellationToken)
        => Ok(await queries.GetAccountAsync(id, cancellationToken) ?? throw new EntityNotFoundException("Account", id));
}
