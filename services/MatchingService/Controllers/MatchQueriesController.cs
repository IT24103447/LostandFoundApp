using System.Security.Claims;
using MatchingService.Claims;
using MatchingService.Matches;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MatchingService.Controllers;

[ApiController]
[Route("api/matches")]
[Authorize(Policy = "VerifiedClaimUser")]
public sealed class MatchQueriesController : ControllerBase
{
    private readonly MatchReadService _matches;

    public MatchQueriesController(MatchReadService matches)
    {
        _matches = matches;
    }

    [HttpGet]
    public Task<IActionResult> List(
        [FromQuery] string section = "active",
        [FromQuery] int page = 1,
        [FromQuery] int size = 20,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(async userId =>
            Ok(await _matches.GetPageAsync(
                userId,
                section,
                page,
                size,
                cancellationToken)));
    }

    [HttpGet("{matchId:guid}")]
    public Task<IActionResult> Details(
        Guid matchId,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(async userId =>
            Ok(await _matches.GetByIdAsync(
                matchId,
                userId,
                cancellationToken)));
    }

    private async Task<IActionResult> ExecuteAsync(
        Func<Guid, Task<IActionResult>> action)
    {
        var subject =
            User.FindFirstValue(ClaimTypes.NameIdentifier) ??
            User.FindFirstValue("sub");

        if (!Guid.TryParse(subject, out var userId))
        {
            return Unauthorized(new
            {
                error = "Please sign in."
            });
        }

        try
        {
            return await action(userId);
        }
        catch (ClaimException exception)
        {
            return StatusCode(
                exception.StatusCode,
                new
                {
                    error = exception.Message
                });
        }
    }
}