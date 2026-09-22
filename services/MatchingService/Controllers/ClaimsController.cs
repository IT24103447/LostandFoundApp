using System.Security.Claims;
using MatchingService.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MatchingService.Controllers;

[ApiController]
[Route("api/matches")]
[Authorize(Policy = "VerifiedClaimUser")]
public sealed class ClaimsController : ControllerBase
{
    private readonly ClaimService _claims;
    private readonly ClaimItemClient _items;
    private readonly ClaimRepository _repository;

    public ClaimsController(
        ClaimService claims,
        ClaimItemClient items,
        ClaimRepository repository)
    {
        _claims = claims;
        _items = items;
        _repository = repository;
    }

    [HttpGet("candidates")]
    public Task<IActionResult> Candidates(
        [FromQuery] string type,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(async userId =>
        {
            var reports = await _items.GetMineAsync(
                type,
                userId,
                cancellationToken);

            return Ok(reports);
        });
    }

    [HttpPost("preview")]
    public Task<IActionResult> Preview(
        [FromBody] PairRequest request,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(async userId =>
        {
            var preview = await _claims.PreviewAsync(
                request,
                userId,
                cancellationToken);

            return Ok(preview);
        });
    }

    [HttpPost("claim")]
    public Task<IActionResult> Claim(
        [FromBody] SubmitClaimRequest request,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(async userId =>
        {
            var match = await _claims.SubmitAsync(
                request,
                userId,
                cancellationToken);

            return StatusCode(
                StatusCodes.Status201Created,
                match);
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

            var matches = await _repository.GetMineAsync(
                userId,
                page,
                cancellationToken);

            return Ok(matches);
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