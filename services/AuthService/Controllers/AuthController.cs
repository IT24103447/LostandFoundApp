using AuthService.Configuration;
using AuthService.Models;
using AuthService.Models.Dtos;
using AuthService.Repositories;
using AuthService.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace AuthService.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly IUsersRepository _users;
    private readonly IEmailVerificationTokensRepository _tokens;
    private readonly IPasswordHasher _passwordHasher;
    private readonly PasswordValidator _passwordValidator;
    private readonly ITokenGenerator _tokenGenerator;
    private readonly IEmailService _email;
    private readonly IVerificationSessionService _sessionService;
    private readonly AuthSettings _auth;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        IUsersRepository users,
        IEmailVerificationTokensRepository tokens,
        IPasswordHasher passwordHasher,
        PasswordValidator passwordValidator,
        ITokenGenerator tokenGenerator,
        IEmailService email,
        IVerificationSessionService sessionService,
        IOptions<AuthSettings> auth,
        ILogger<AuthController> logger)
    {
        _users = users;
        _tokens = tokens;
        _passwordHasher = passwordHasher;
        _passwordValidator = passwordValidator;
        _tokenGenerator = tokenGenerator;
        _email = email;
        _sessionService = sessionService;
        _auth = auth.Value;
        _logger = logger;
    }

    /// <summary>
    /// Self-register a new (non-admin) user account.
    /// Sends a 6-digit OTP to the registered email and returns a verification session token
    /// that the client uses for subsequent /verify-email and /resend-verification calls.
    ///</summary>
    [HttpPost("register")]
    [EnableRateLimiting("register")]
    public async Task<ActionResult<RegisterResponse>> Register(
        [FromBody] RegisterRequest req,
        CancellationToken ct)
    {
        var (pwOk, pwErrors) = _passwordValidator.Validate(req.Password);
        if (!pwOk)
        {
            return ValidationProblem(new ValidationProblemDetails(
                pwErrors.ToDictionary(e => "Password", e => new[] { e })));
        }

        if (await _users.EmailExistsAsync(req.Email, ct))
        {
            return Conflict(new { error = "An account with this email already exists." });
        }

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
            IsAdmin = false,
            IsEmailVerified = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        try
        {
            await _users.CreateAsync(user, ct);
        }
        catch (MySqlException ex) when (ex.Number == 1062)
        {
            return Conflict(new { error = "An account with this email or phone number already exists." });
        }

        // Issue verification code + send email (fire-and-log).
        var code = _tokenGenerator.GenerateCode();
        var codeHash = _tokenGenerator.Hash(code);
        var expiresAt = DateTime.UtcNow.AddMinutes(_auth.OtpExpiryMinutes);
        await _tokens.CreateAsync(user.Id, codeHash, pendingEmail: null, expiresAt, ct);

        var htmlBody = $"""
            <p>Welcome to Lost & Found</p>
            <p style="font-size:32px;font-weight:bold;letter-spacing:6px">{code}</p>
            <p>Enter this code on the verification page. It expires in {_auth.OtpExpiryMinutes} minutes</p>
            <p>If you didn't request this, you can safely ignore this email</p>
            """;
        var plainBody =
            $"Your Lost & Found verification code is: {code}\n\n" +
            $"Enter it on the verification page. It expires in {_auth.OtpExpiryMinutes} minutes.";

        await _email.SendAsync(
            user.Email,
            "Your Lost & Found verification code",
            htmlBody,
            plainBody,
            ct);

        var sessionToken = _sessionService.Issue(user.Id);

        return Ok(new RegisterResponse
        {
            Id = user.Id,
            Email = user.Email,
            Name = user.Name,
            PhoneNo = user.PhoneNo,
            IsAdmin = user.IsAdmin,
            IsEmailVerified = user.IsEmailVerified,
            CreatedAt = user.CreatedAt.ToString("o"),
            VerificationSessionToken = sessionToken,
        });
    }

    /// <summary>
    /// Verify a 6-digit OTP. If the user edited their email on the verify page,
    /// the stored email is updated on successful verification (pending_email pattern).
    ///</summary>
    [HttpPost("verify-email")]
    [EnableRateLimiting("verify-email")]
    public async Task<IActionResult> VerifyEmail(
        [FromBody] VerifyEmailRequest req,
        CancellationToken ct)
    {
        var userId = _sessionService.Validate(req.SessionToken);
        if (userId is null)
        {
            return BadRequest(new { error = "Invalid or expired verification session." });
        }

        var codeHash = _tokenGenerator.Hash(req.Code);
        var token = await _tokens.GetActiveByHashAsync(codeHash, ct);
        if (token is null || token.UserId != userId.Value)
        {
            // Token not found / expired / used / locked. Don't reveal which.
            return BadRequest(new { error = "Invalid or expired verification code." });
        }

        await _tokens.IncrementAttemptsAsync(token.Id, ct);
        if (token.Attempts + 1 >= _auth.MaxOtpAttempts)
        {
            await _tokens.MarkUsedAsync(token.Id, ct);
            return BadRequest(new { error = "Invalid or expired verification code." });
        }

        // Code matches. Apply pending email change (if any) before marking verified.
        var user = await _users.GetByIdAsync(userId.Value, ct)
            ?? throw new InvalidOperationException("Session references a non-existent user.");

        if (!string.IsNullOrEmpty(token.PendingEmail) &&
            !string.Equals(token.PendingEmail, user.Email, StringComparison.OrdinalIgnoreCase))
        {
            // Race re-check: ensure the pending email is still free.
            if (await _users.IsEmailRegisteredAsync(token.PendingEmail, ct))
            {
                await _tokens.MarkUsedAsync(token.Id, ct);
                return BadRequest(new
                {
                    error = "This verification code can no longer be used because the email " +
                            "address has been claimed by another account since the code was issued."
                });
            }
            await _users.UpdateEmailAsync(userId.Value, token.PendingEmail, ct);
            user.Email = token.PendingEmail;
        }

        await _users.MarkEmailVerifiedAsync(userId.Value, ct);
        await _tokens.MarkUsedAsync(token.Id, ct);

        return Ok(new { verified = true, email = user.Email });
    }

    /// <summary>
    /// Lightweight polling endpoint for the verify-email page. Returns the user's current
    /// verification status.
    ///</summary>
    [HttpGet("verification-status")]
    public async Task<IActionResult> VerificationStatus(
        [FromQuery] string sessionToken,
        CancellationToken ct)
    {
        var userId = _sessionService.Validate(sessionToken);
        if (userId is null)
        {
            return BadRequest(new { error = "Invalid or expired verification session." });
        }
        var status = await _users.GetVerificationStatusAsync(userId.Value, ct);
        return Ok(new
        {
            isEmailVerified = status.IsEmailVerified
        });
    }

    /// <summary>
    /// Resend the OTP to an (optionally edited) email. Enforces cooldown + email-change rules.
    ///</summary>
    [HttpPost("resend-verification")]
    [EnableRateLimiting("resend-verification")]
    public async Task<IActionResult> ResendVerification(
        [FromBody] ResendVerificationRequest req,
        CancellationToken ct)
    {
        var userId = _sessionService.Validate(req.SessionToken);
        if (userId is null)
        {
            return BadRequest(new { error = "Invalid or expired verification session." });
        }

        var user = await _users.GetByIdAsync(userId.Value, ct);
        if (user is null)
        {
            return BadRequest(new { error = "Invalid or expired verification session." });
        }

        // Cooldown check: skip on first resend (last_resent_at IS NULL after register).
        var lastSent = await _users.GetLastResentAtAsync(userId.Value, ct);
        if (lastSent.HasValue)
        {
            var elapsed = DateTime.UtcNow - lastSent.Value;
            var cooldown = TimeSpan.FromSeconds(_auth.ResendCooldownSeconds);
            if (elapsed < cooldown)
            {
                var remaining = cooldown - elapsed;
                var seconds = (int)Math.Ceiling(remaining.TotalSeconds);
                Response.Headers["Retry-After"] = seconds.ToString();
                return StatusCode(429, new
                {
                    error = $"Please wait {seconds} seconds before requesting another code."
                });
            }
        }

        // Email-change validation: if the user typed a different email, ensure it's free.
        var emailChanged = !string.Equals(req.Email, user.Email, StringComparison.OrdinalIgnoreCase);
        if (emailChanged)
        {
            if (await _users.IsEmailRegisteredAsync(req.Email, ct))
            {
                return BadRequest(new
                {
                    error = "This email is already registered by another account. " +
                            "Please use a different email or sign in to the existing account."
                });
            }
        }

        // Invalidate previous codes + generate new one.
        await _tokens.InvalidateAllForUserAsync(userId.Value, ct);
        var code = _tokenGenerator.GenerateCode();
        var codeHash = _tokenGenerator.Hash(code);
        var expiresAt = DateTime.UtcNow.AddMinutes(_auth.OtpExpiryMinutes);
        await _tokens.CreateAsync(
            userId.Value,
            codeHash,
            pendingEmail: emailChanged ? req.Email : null,
            expiresAt,
            ct);

        var htmlBody = $"""
            <p>Your Lost & Found verification code</p>
            <p style="font-size:32px;font-weight:bold;letter-spacing:6px">{code}</p>
            <p>Enter this code on the verification page. It expires in {_auth.OtpExpiryMinutes} minutes</p>
            """;
        var plainBody =
            $"Your Lost & Found verification code is: {code}\n\n" +
            $"Enter it on the verification page. It expires in {_auth.OtpExpiryMinutes} minutes.";

        await _email.SendAsync(req.Email, "Your Lost & Found verification code", htmlBody, plainBody, ct);
        await _users.SetLastResentAtAsync(userId.Value, DateTime.UtcNow, ct);

        return Ok(new { sent = true });
    }
}


