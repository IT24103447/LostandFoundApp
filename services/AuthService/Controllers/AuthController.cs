using System.Net;
using AuthService.Models;
using AuthService.Models.Dtos;
using AuthService.Repositories;
using AuthService.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MySqlConnector;

namespace AuthService.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly IUsersRepository _users;
    private readonly IPasswordHasher _passwordHasher;
    private readonly PasswordValidator _passwordValidator;

    public AuthController(
        IUsersRepository users,
        IPasswordHasher passwordHasher,
        PasswordValidator passwordValidator)
    {
        _users = users;
        _passwordHasher = passwordHasher;
        _passwordValidator = passwordValidator;
    }

    /// <summary>
    /// Self-register a new (non-admin) user account.
    /// </summary>
    [HttpPost("register")]
    [EnableRateLimiting("register")]
    public async Task<ActionResult<UserProfileDto>> Register(
        [FromBody] RegisterRequest req,
        CancellationToken ct)
    {
        // DTO-level validation (email format, phone regex, length bounds) is handled
        // automatically by [ApiController]. Any ModelState errors return 400 before reaching here.

        // Password complexity (upper/lower/digit) — manual since data annotations can't express it.
        var (pwOk, pwErrors) = _passwordValidator.Validate(req.Password);
        if (!pwOk)
        {
            return ValidationProblem(new ValidationProblemDetails(
                pwErrors.ToDictionary(e => "Password", e => new[] { e })));
        }

        // Duplicate email check (clean 409 instead of a raw SQL exception).
        if (await _users.EmailExistsAsync(req.Email, ct))
        {
            return Conflict(new { error = "An account with this email already exists." });
        }

        // Duplicate phone check.
        if (await _users.PhoneExistsAsync(req.PhoneNo, ct))
        {
            return Conflict(new { error = "An account with this phone number already exists." });
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = req.Email,
            PasswordHash = _passwordHasher.Hash(req.Password),
            Name = req.Name,
            PhoneNo = req.PhoneNo,
            IsAdmin = false,           // self-registration never creates an admin
            IsEmailVerified = false,   // email verification is deferred (IEmailService not yet built)
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        try
        {
            await _users.CreateAsync(user, ct);
        }
        catch (MySqlException ex) when (ex.Number == 1062)
        {
            // Backstop against the race window between check and insert: the UNIQUE
            // constraint on email or phone_no rejects the parallel insert.
            return Conflict(new { error = "An account with this email or phone number already exists." });
        }

        var dto = new UserProfileDto
        {
            Id = user.Id,
            Email = user.Email,
            Name = user.Name,
            PhoneNo = user.PhoneNo,
            IsAdmin = user.IsAdmin,
            IsEmailVerified = user.IsEmailVerified,
            CreatedAt = user.CreatedAt
        };

        return Ok(dto);
    }
}
