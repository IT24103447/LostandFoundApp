using AdminVerifyService.Configuration;
using AdminVerifyService.Spam;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AdminVerifyService.Controllers;

[ApiController]
[Route("api/admin/spam-records")]
[Authorize(Policy = SecurityRegistration.AdminOnlyPolicy)]
public sealed class AdminSpamRecordsController(SpamReviewRepository records) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? tab,
        [FromQuery] string? sort,
        [FromQuery] string? from,
        [FromQuery] string? to,
        [FromQuery] string? userIds,
        CancellationToken cancellationToken,
        [FromQuery] int page = 1)
    {
        try
        {
            var query = SpamReviewQueryParser.Parse(tab, sort, from, to, userIds, page);

            return Ok(await records.ListAsync(query, cancellationToken));
        }
        catch (SpamReviewException exception)
        {
            return StatusCode(exception.StatusCode, new { error = exception.Message });
        }
    }
}
