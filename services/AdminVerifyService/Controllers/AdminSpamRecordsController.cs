using AdminVerifyService.Configuration;
using AdminVerifyService.Spam;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdminVerifyService.Controllers;

[ApiController]
[Route("api/admin/spam-records")]
[Authorize(Policy = SecurityRegistration.AdminOnlyPolicy)]
public sealed class AdminSpamRecordsController(
    SpamReviewRepository records,
    SpamCaseRepository cases,
    SpamSolveRepository solves) : ControllerBase
{
    [HttpGet]
    public Task<IActionResult> List(
        [FromQuery] string? tab,
        [FromQuery] string? sort,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string? userIds,
        CancellationToken cancellationToken,
        [FromQuery] int page = 1) =>
        ExecuteAsync(async () =>
        {
            var query = SpamReviewQueryParser.Parse(tab, sort, from, to, userIds, page);

            return Ok(await records.ListAsync(query, cancellationToken));
        });

    [HttpGet("{id:guid}")]
    public Task<IActionResult> Detail(Guid id, CancellationToken cancellationToken) =>
        ExecuteAsync(async () => Ok(await cases.GetDetailAsync(id, cancellationToken)));

    [HttpPost("{id:guid}/open")]
    public Task<IActionResult> Open(Guid id, CancellationToken cancellationToken) =>
        ExecuteAsync(async () => Ok(await cases.OpenAsync(id, cancellationToken)));

    [HttpPost("{id:guid}/dismiss")]
    public Task<IActionResult> Dismiss(Guid id, CancellationToken cancellationToken) =>
        ExecuteAsync(async () => Ok(await cases.DismissAsync(id, cancellationToken)));

    [HttpPost("{id:guid}/solve")]
    public Task<IActionResult> Solve(Guid id, [FromBody] SolveRequest request, CancellationToken cancellationToken) =>
        ExecuteAsync(async () => Ok(await solves.StartAsync(id, request, cancellationToken)));

    [HttpPut("{id:guid}/listings/{listingId:guid}/result")]
    public Task<IActionResult> ListingResult(
        Guid id,
        Guid listingId,
        [FromBody] ResultRequest request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            await solves.SaveListingResultAsync(id, listingId, request.Result, cancellationToken);
            return NoContent();
        });

    [HttpPut("{id:guid}/kick-result")]
    public Task<IActionResult> KickResult(Guid id, [FromBody] ResultRequest request, CancellationToken cancellationToken) =>
        ExecuteAsync(async () => Ok(await solves.SaveKickResultAsync(id, request.Result, cancellationToken)));

    [HttpPost("{id:guid}/finish")]
    public Task<IActionResult> Finish(Guid id, CancellationToken cancellationToken) =>
        ExecuteAsync(async () => Ok(await solves.FinishAsync(id, cancellationToken)));

    private async Task<IActionResult> ExecuteAsync(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (SpamReviewException exception)
        {
            return StatusCode(exception.StatusCode, new { error = exception.Message });
        }
    }
}
