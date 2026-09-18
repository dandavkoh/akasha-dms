using Akasha.Application.Tenancy;
using Akasha.Application.Tenants;
using Microsoft.AspNetCore.Mvc;

namespace Akasha.Api.Controllers;

[ApiController]
[Route("api/v1/tenants")]
public sealed class TenantsController(ITenantContext tenantContext, ITenantQueries tenantQueries) : ControllerBase
{
    [HttpGet("current")]
    [ProducesResponseType(typeof(TenantInfo), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCurrent(CancellationToken cancellationToken)
    {
        if (!tenantContext.HasTenant)
        {
            return Unauthorized();
        }

        var tenant = await tenantQueries.FindAsync(tenantContext.TenantId, cancellationToken);
        return tenant is null ? NotFound() : Ok(tenant);
    }
}
