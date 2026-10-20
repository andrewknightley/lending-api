using Lending.Application;
using Microsoft.AspNetCore.Mvc;

namespace Lending.Api;

[ApiController]
[Route("api")]
public sealed class ReferenceDataController(ILendingQueries queries) : ControllerBase
{
    [HttpGet("customers")]
    public async Task<ActionResult<Page<CustomerView>>> Customers([FromQuery] int offset = 0, [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        if (BadPage(offset, pageSize)) return InvalidPage();
        return Ok(await queries.ListCustomersAsync(offset, pageSize, cancellationToken));
    }

    [HttpGet("customers/{id:guid}")]
    public async Task<ActionResult<CustomerView>> Customer(Guid id, CancellationToken cancellationToken)
        => Ok(await queries.GetCustomerAsync(id, cancellationToken) ?? throw new EntityNotFoundException("Customer", id));

    [HttpGet("accounts")]
    public async Task<ActionResult<Page<AccountView>>> Accounts([FromQuery] Guid? customerId = null, [FromQuery] int offset = 0,
        [FromQuery] int pageSize = 50, CancellationToken cancellationToken = default)
    {
        if (BadPage(offset, pageSize)) return InvalidPage();
        return Ok(await queries.ListAccountsAsync(customerId, offset, pageSize, cancellationToken));
    }

    [HttpGet("accounts/{id:guid}")]
    public async Task<ActionResult<AccountView>> Account(Guid id, CancellationToken cancellationToken)
        => Ok(await queries.GetAccountAsync(id, cancellationToken) ?? throw new EntityNotFoundException("Account", id));

    [HttpGet("credit-products")]
    public async Task<ActionResult<Page<ProductView>>> Products([FromQuery] bool? available = null, [FromQuery] int offset = 0,
        [FromQuery] int pageSize = 50, CancellationToken cancellationToken = default)
    {
        if (BadPage(offset, pageSize)) return InvalidPage();
        return Ok(await queries.ListProductsAsync(available, offset, pageSize, cancellationToken));
    }

    private static bool BadPage(int offset, int size) => offset < 0 || size is < 1 or > 100;
    private ObjectResult InvalidPage() => Problem(statusCode: 400, title: "Invalid pagination", detail: "Offset must be non-negative and pageSize must be between 1 and 100.");
}
