using System.Security.Claims;
using MatchingService.Claims;
using MatchingService.Matches;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MatchingService.Controllers;

[ApiController]
[Route("api/matches/{matchId:guid}/finder")]
[Authorize(Policy = "VerifiedClaimUser")]
public sealed class FinderDecisionsController : ControllerBase
{
    private readonly FinderDecisionRepository _decisions;

    public FinderDecisionsController(
        FinderDecisionRepository decisions)
    {
        _decisions = decisions;
    }

    [HttpPost("confirm")]
    public Task<IActionResult> Confirm(
        Guid matchId,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(async userId =>
        {
            var email =
                User.FindFirstValue(ClaimTypes.Email) ??
                User.FindFirstValue("email");

            var phone = User.FindFirstValue("phone_no");

            return Ok(await _decisions.DecideAsync(
                matchId,
                userId,
                confirm: true,
                email,
                phone,
                cancellationToken));
        });
    }

    [HttpPost("reject")]
    public Task<IActionResult> Reject(
        Guid matchId,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(async userId =>
            Ok(await _decisions.DecideAsync(
                matchId,
                userId,
                confirm: false,
                finderEmail: null,
                finderPhone: null,
                cancellationToken)));
    }

    [HttpGet("return-contact")]
    [ResponseCache(
        NoStore = true,
        Location = ResponseCacheLocation.None)]
    public Task<IActionResult> ReturnContact(
        Guid matchId,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(async userId =>
            Ok(await _decisions.GetLostReporterContactAsync(
                matchId,
                userId,
                cancellationToken)));
    }

    private async Task<IActionResult> ExecuteAsync(
        Func<Guid, Task<IActionResult>> action)
    {
        Response.Headers.CacheControl = "no-store";

        var subject =
            User.FindFirstValue(ClaimTypes.NameIdentifier) ??
            User.FindFirstValue("sub");

        if (!Guid.TryParse(subject, out var userId))
        {
            return Unauthorized(new { error = "Please sign in." });
        }

        try
        {
            return await action(userId);
        }
        catch (ClaimException exception)
        {
            return StatusCode(
                exception.StatusCode,
                new { error = exception.Message });
        }
    }
}