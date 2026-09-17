using Microsoft.AspNetCore.Mvc;

namespace Akasha.Api.Controllers;

[ApiController]
[Route("api/v1/system")]
public sealed class SystemController(ILogger<SystemController> logger) : ControllerBase
{
    [HttpGet("ping")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Ping()
    {
        logger.LogInformation("Ping received");
        return Ok(new { status = "ok" });
    }

    [HttpGet("fail")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public IActionResult Fail() => throw new InvalidOperationException("Intentional failure to verify central error handling.");
}
