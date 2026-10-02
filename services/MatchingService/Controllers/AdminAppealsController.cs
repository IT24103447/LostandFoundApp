using System.Security.Claims;
using MatchingService.Appeals;
using MatchingService.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace MatchingService.Controllers;

[ApiController]
[Route("api/admin/match-appeals")]
[Authorize(Policy = "AdminOnly")]
public sealed class AdminAppealsController : ControllerBase
{
    private readonly AdminAppealService _appeals;
    private readonly ILogger<AdminAppealsController> _logger;

    public AdminAppealsController(
        AdminAppealService appeals,
        ILogger<AdminAppealsController> logger)
    {
        _appeals = appeals;
        _logger = logger;
    }

    [HttpGet]
    public Task<IActionResult> List(
        [FromQuery] string status = AppealStatus.Pending,
        [FromQuery] int page = 1,
        CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(async _ =>
        {
            if (page is < 1 or > 100000)
            {
                throw new ClaimException(
                    StatusCodes.Status400BadRequest,
                    "Invalid page number.");
            }

            return Ok(await _appeals.ListAsync(
                status,
                page,
                cancellationToken));
        });
    }

    [HttpGet("{id:guid}")]
    public Task<IActionResult> Open(
        Guid id,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(async _ =>
            Ok(await _appeals.OpenAsync(id, cancellationToken)));
    }

    [HttpPost("{id:guid}/verify")]
    public Task<IActionResult> Verify(
        Guid id,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(async adminId =>
        {
            var appeal = await _appeals.VerifyAsync(
                id,
                adminId,
                cancellationToken);

            _logger.LogInformation(
                "Admin {AdminId} verified match appeal {AppealId}.",
                adminId, id);

            return Ok(appeal);
        });
    }

    [HttpPost("{id:guid}/reject")]
    public Task<IActionResult> Reject(
        Guid id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] RejectAppealRequest? request,
        CancellationToken cancellationToken)
    {
        return ExecuteAsync(async adminId =>
        {
            var appeal = await _appeals.RejectAsync(
                id,
                adminId,
                request?.Reason,
                cancellationToken);

            _logger.LogInformation(
                "Admin {AdminId} rejected match appeal {AppealId}.",
                adminId, id);

            return Ok(appeal);
        });
    }

    private async Task<IActionResult> ExecuteAsync(
        Func<Guid, Task<IActionResult>> action)
    {
        var subject =
            User.FindFirstValue(ClaimTypes.NameIdentifier) ??
            User.FindFirstValue("sub");

        if (!Guid.TryParse(subject, out var adminId))
        {
            return Unauthorized(new
            {
                error = "Please sign in."
            });
        }

        try
        {
            return await action(adminId);
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
