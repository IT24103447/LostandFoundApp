using System.Security.Claims;
using MatchingService.Appeals;
using MatchingService.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MatchingService.Controllers;

[ApiController]
[Route("api/matches/appeals")]
[Authorize]
public sealed class AppealsController : ControllerBase
{
    private readonly AppealService _appeals;

    public AppealsController(AppealService appeals)
    {
        _appeals = appeals;
    }

    [HttpPost]
    [Authorize(Policy = "VerifiedClaimUser")]
    public Task<IActionResult> Send(
        [FromBody] SendAppealRequest request,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(async userId =>
        {
            var email =
                User.FindFirstValue(ClaimTypes.Email) ??
                User.FindFirstValue("email");
            var phone = User.FindFirstValue("phone_no");

            var appeal = await _appeals.SendAsync(
                request,
                userId,
                email,
                phone,
                cancellationToken);

            return StatusCode(
                StatusCodes.Status201Created,
                appeal);
        });
    }

    [HttpGet("mine")]
    public Task<IActionResult> Mine(
        [FromQuery] int page = 1,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(async userId =>
        {
            if (page is < 1 or > 100000)
            {
                throw new ClaimException(
                    StatusCodes.Status400BadRequest,
                    "Invalid page number.");
            }

            var appeals = await _appeals.GetMineAsync(
                userId,
                page,
                cancellationToken);

            return Ok(appeals);
        });
    }

    [HttpGet("pair-status")]
    public Task<IActionResult> PairStatus(
        [FromQuery] Guid lostItemId,
        [FromQuery] Guid foundItemId,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(async userId =>
        {
            var appealed = await _appeals.PairHasAppealAsync(
                lostItemId,
                foundItemId,
                userId,
                cancellationToken);

            return Ok(new { appealed });
        });
    }

    [HttpGet("edit-warning")]
    public Task<IActionResult> EditWarning(
        [FromQuery] string type,
        [FromQuery] Guid id,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(async userId =>
        {
            var warn = await _appeals.HasEditWarningAsync(
                type ?? string.Empty,
                id,
                userId,
                cancellationToken);

            return Ok(new { warn });
        });
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
