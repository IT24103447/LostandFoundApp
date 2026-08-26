using AuthService.Configuration;
using AuthService.Models.Dtos;
using AuthService.Models.Events;
using AuthService.Repositories;
using AuthService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AuthService.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize(Policy = "ActiveUser")]
public class AdminController : ControllerBase
{
    private readonly IUsersRepository _users;
    private readonly IEventPublisher _publisher;
    private readonly KafkaSettings _kafka;
    private readonly ILogger<AdminController> _logger;

    public AdminController(
        IUsersRepository users,
        IEventPublisher publisher,
        IOptions<KafkaSettings> kafka,
        ILogger<AdminController> logger)
    {
        _users = users;
        _publisher = publisher;
        _kafka = kafka.Value;
        _logger = logger;
    }

    /// <summary>
    /// List users with optional search and filters. Paginated.
    /// </summary>
    [HttpGet("users")]
    public async Task<IActionResult> GetUsers(
        [FromQuery] string? search,
        [FromQuery] bool? isKicked,
        [FromQuery] bool? isVerified,
        [FromQuery] bool? isDeleted,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var offset = (page - 1) * pageSize;

        var users = await _users.SearchUsersAsync(search, isKicked, isVerified, isDeleted, pageSize, offset, ct);
        var total = await _users.CountUsersAsync(search, isKicked, isVerified, isDeleted, ct);

        var result = users.Select(u => new AdminUserDto
        {
            Id = u.Id,
            Email = u.Email,
            Name = u.Name,
            PhoneNo = u.PhoneNo,
            IsAdmin = u.IsAdmin,
            IsEmailVerified = u.IsEmailVerified,
            IsKicked = u.IsKicked,
            CreatedAt = u.CreatedAt,
            DeletedAt = u.DeletedAt
        });

        return Ok(new
        {
            users = result,
            total,
            page,
            pageSize,
            totalPages = (int)Math.Ceiling((double)total / pageSize)
        });
    }

    /// <summary>
    /// Kick (soft-ban) a user. Requires explicit confirmation.
    /// Admins cannot kick other admins or themselves.
    /// </summary>
    [HttpPost("users/{id:guid}/kick")]
    public async Task<IActionResult> KickUser(
        Guid id,
        [FromBody] ConfirmRequest req,
        CancellationToken ct)
    {
        if (!req.Confirm)
        {
            return BadRequest(new { error = "Confirmation is required. Set confirm to true." });
        }

        var currentUserId = GetCurrentUserId();
        if (currentUserId == id)
        {
            return BadRequest(new { error = "You cannot kick yourself." });
        }

        var user = await _users.GetByIdAsync(id, ct);
        if (user is null)
        {
            return NotFound(new { error = "User not found." });
        }

        if (user.IsAdmin)
        {
            return BadRequest(new { error = "Cannot kick an admin account." });
        }

        if (user.IsKicked)
        {
            return BadRequest(new { error = "User is already kicked." });
        }

        await _users.SetKickedAsync(id, true, ct);

        await _publisher.PublishAsync($"{_kafka.TopicPrefix}.user.kicked", new UserKickedEvent
        {
            UserId = id,
            KickedBy = currentUserId,
            Email = user.Email,
            Name = user.Name,
            Phone = user.PhoneNo
        }, ct);

        _logger.LogInformation("Admin {AdminId} kicked user {UserId}.", currentUserId, id);

        return Ok(new { success = true, message = $"User {user.Email} has been kicked." });
    }

    /// <summary>
    /// Unkick (restore) a user. Requires explicit confirmation.
    /// </summary>
    [HttpPost("users/{id:guid}/unkick")]
    public async Task<IActionResult> UnkickUser(
        Guid id,
        [FromBody] ConfirmRequest req,
        CancellationToken ct)
    {
        if (!req.Confirm)
        {
            return BadRequest(new { error = "Confirmation is required. Set confirm to true." });
        }

        var user = await _users.GetByIdAsync(id, ct);
        if (user is null)
        {
            return NotFound(new { error = "User not found." });
        }

        if (!user.IsKicked)
        {
            return BadRequest(new { error = "User is not kicked." });
        }

        await _users.SetKickedAsync(id, false, ct);

        await _publisher.PublishAsync($"{_kafka.TopicPrefix}.user.unkicked", new UserUnkickedEvent
        {
            UserId = id,
            UnkickedBy = GetCurrentUserId(),
            Email = user.Email,
            Name = user.Name,
            Phone = user.PhoneNo
        }, ct);

        _logger.LogInformation("Admin {AdminId} un-kicked user {UserId}.", GetCurrentUserId(), id);

        return Ok(new { success = true, message = $"User {user.Email} has been restored." });
    }

    private Guid GetCurrentUserId()
    {
        var claim = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
        return Guid.TryParse(claim, out var userId) ? userId : Guid.Empty;
    }
}
