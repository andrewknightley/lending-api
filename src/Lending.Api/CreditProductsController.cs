using Lending.Application;
using Microsoft.AspNetCore.Mvc;

namespace Lending.Api;

[ApiController]
[Route("api/credit-products")]
public sealed class CreditProductsController(ILendingQueries queries) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<Page<ProductView>>> Products([FromQuery] bool? available = null, [FromQuery] int offset = 0,
        [FromQuery] int pageSize = 50, CancellationToken cancellationToken = default)
    {
        if (offset < 0 || pageSize is < 1 or > 100)
            return Problem(statusCode: 400, title: "Invalid pagination", detail: "Offset must be non-negative and pageSize must be between 1 and 100.");
        return Ok(await queries.ListProductsAsync(available, offset, pageSize, cancellationToken));
    }
}
