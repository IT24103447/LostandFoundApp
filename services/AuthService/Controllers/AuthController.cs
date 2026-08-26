using AuthService.Configuration;
using AuthService.Models;
using AuthService.Models.Dtos;
using AuthService.Repositories;
using AuthService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using MySqlConnector;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace AuthService.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly IUsersRepository _users;
    private readonly IEmailVerificationTokensRepository _tokens;
    private readonly IPasswordResetTokensRepository _resetTokens;
    private readonly IPasswordHasher _passwordHasher;
    private readonly PasswordValidator _passwordValidator;
    private readonly ITokenGenerator _tokenGenerator;
    private readonly IEmailService _email;
    private readonly IVerificationSessionService _sessionService;
    private readonly IJwtTokenService _jwtTokenService;
    private readonly AuthSettings _auth;
    private readonly JwtSettings _jwt;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        IUsersRepository users,
        IEmailVerificationTokensRepository tokens,
        IPasswordResetTokensRepository resetTokens,
        IPasswordHasher passwordHasher,
        PasswordValidator passwordValidator,
        ITokenGenerator tokenGenerator,
        IEmailService email,
        IVerificationSessionService sessionService,
        IJwtTokenService jwtTokenService,
        IOptions<AuthSettings> auth,
        IOptions<JwtSettings> jwt,
        ILogger<AuthController> logger)
    {
        _users = users;
        _tokens = tokens;
        _resetTokens = resetTokens;
        _passwordHasher = passwordHasher;
        _passwordValidator = passwordValidator;
        _tokenGenerator = tokenGenerator;
        _email = email;
        _sessionService = sessionService;
        _jwtTokenService = jwtTokenService;
        _auth = auth.Value;
        _jwt = jwt.Value;
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
                new Dictionary<string, string[]> { ["Password"] = pwErrors.ToArray() }));
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

        var currentUserId = GetCurrentUserIdFromCookie();
        if (currentUserId.HasValue)
        {
            return BadRequest(new { error = "Already authenticated. Please sign out first." });
        }

        var user = await _users.GetByIdAsync(userId.Value, ct)
            ?? throw new InvalidOperationException("Session references a non-existent user.");

        if (user.IsEmailVerified)
        {
            return BadRequest(new { error = "Email is already verified." });
        }

        var codeHash = _tokenGenerator.Hash(req.Code);
        var token = await _tokens.GetActiveByUserAsync(userId.Value, ct);
        if (token is null)
        {
            return BadRequest(new { error = "Invalid or expired verification code." });
        }

        if (token.CodeHash != codeHash)
        {
            await _tokens.IncrementAttemptsAsync(token.Id, ct);
            if (token.Attempts + 1 >= _auth.MaxOtpAttempts)
            {
                await _tokens.MarkUsedAsync(token.Id, ct);
                return BadRequest(new { error = "Too many failed attempts. Please request a new code." });
            }
            return BadRequest(new { error = "Invalid or expired verification code." });
        }

        if (!string.IsNullOrEmpty(token.PendingEmail) &&
            !string.Equals(token.PendingEmail, user.Email, StringComparison.OrdinalIgnoreCase))
        {
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

        var jwtToken = _jwtTokenService.IssueLoginToken(user);
        var isProduction = HttpContext.RequestServices.GetService<IHostEnvironment>()?.IsProduction() ?? false;
        var cookieOptions = new CookieOptions
        {
            HttpOnly = true,
            Secure = isProduction,
            SameSite = isProduction ? SameSiteMode.None : SameSiteMode.Lax,
            Path = "/",
            Expires = DateTime.UtcNow.AddMinutes(_jwt.ExpiryMinutes)
        };
        Response.Cookies.Append("auth_token", jwtToken, cookieOptions);

        _logger.LogInformation("User {UserId} email verified and logged in.", userId.Value);

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

    /// <summary>
    /// Authenticate a user by email + password. On success, issues a JWT as an httpOnly cookie
    /// and returns the user's profile (no token in the response body).
    ///</summary>
    [HttpPost("login")]
    [EnableRateLimiting("login")]
    public async Task<ActionResult<LoginResponse>> Login(
        [FromBody] LoginRequest req,
        CancellationToken ct)
    {
        var user = await _users.GetByEmailAsync(req.Email, ct);
        if (user is null || !_passwordHasher.Verify(req.Password, user.PasswordHash))
        {
            return Unauthorized(new { error = "Invalid email or password." });
        }

        if (!user.IsEmailVerified)
        {
            var sessionToken = _sessionService.Issue(user.Id);
            return StatusCode(403, new
            {
                error = "Email not verified. Please verify your email before signing in.",
                email = user.Email,
                verificationSessionToken = sessionToken
            });
        }

        var token = _jwtTokenService.IssueLoginToken(user);
        var isProduction = HttpContext.RequestServices.GetService<IHostEnvironment>()?.IsProduction() ?? false;
        var cookieOptions = new CookieOptions
        {
            HttpOnly = true,
            Secure = isProduction,
            SameSite = isProduction ? SameSiteMode.None : SameSiteMode.Lax,
            Path = "/",
            Expires = DateTime.UtcNow.AddMinutes(_jwt.ExpiryMinutes)
        };
        Response.Cookies.Append("auth_token", token, cookieOptions);

        _logger.LogInformation("User {UserId} logged in successfully.", user.Id);

        return Ok(new LoginResponse
        {
            Id = user.Id,
            Email = user.Email,
            Name = user.Name,
            PhoneNo = user.PhoneNo,
            IsAdmin = user.IsAdmin,
            IsEmailVerified = user.IsEmailVerified,
            CreatedAt = user.CreatedAt
        });
    }

    /// <summary>
    /// Clears the auth cookie, effectively logging the user out.
    ///</summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        var isProduction = HttpContext.RequestServices.GetService<IHostEnvironment>()?.IsProduction() ?? false;
        Response.Cookies.Delete("auth_token", new CookieOptions
        {
            HttpOnly = true,
            Secure = isProduction,
            SameSite = isProduction ? SameSiteMode.None : SameSiteMode.Lax,
            Path = "/"
        });
        await Task.CompletedTask;
        return Ok(new { success = true });
    }

    /// <summary>
    /// Returns the current user's profile based on the auth cookie.
    /// Returns 401 if no valid session cookie is present.
    ///</summary>
    [HttpGet("me")]
    public async Task<ActionResult<UserProfileDto>> GetMe(CancellationToken ct)
    {
        return await GetUserProfileFromToken(ct);
    }

    /// <summary>
    /// Updates the current user's name and phone number.
    /// Returns the updated profile. Phone uniqueness is enforced.
    ///</summary>
    [HttpPut("me")]
    [Authorize]
    public async Task<ActionResult<UserProfileDto>> UpdateMe(
        [FromBody] UpdateProfileRequest req,
        CancellationToken ct)
    {
        var userIdClaim = User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdClaim is null || !Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized(new { error = "Invalid session." });
        }

        if (await _users.PhoneExistsForOtherUserAsync(userId, req.PhoneNo, ct))
        {
            return Conflict(new { error = "This phone number is already in use by another account." });
        }

        await _users.UpdateProfileAsync(userId, req.Name, req.PhoneNo, ct);

        var user = await _users.GetByIdAsync(userId, ct);
        if (user is null)
        {
            return Unauthorized(new { error = "User not found." });
        }

        return Ok(new UserProfileDto
        {
            Id = user.Id,
            Email = user.Email,
            Name = user.Name,
            PhoneNo = user.PhoneNo,
            IsAdmin = user.IsAdmin,
            IsEmailVerified = user.IsEmailVerified,
            CreatedAt = user.CreatedAt
        });
    }

    /// <summary>
    /// Permanently deletes the current user's account and all associated data.
    /// Admin accounts cannot be deleted through this endpoint.
    ///</summary>
    [HttpDelete("me")]
    [Authorize]
    public async Task<IActionResult> DeleteMe(
        [FromBody] DeleteAccountRequest req,
        CancellationToken ct)
    {
        var userIdClaim = User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdClaim is null || !Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized(new { error = "Invalid session." });
        }

        var user = await _users.GetByIdAsync(userId, ct);
        if (user is null)
        {
            return Unauthorized(new { error = "User not found." });
        }

        if (user.IsAdmin)
        {
            return StatusCode(403, new { error = "Admin accounts cannot be deleted through this interface." });
        }

        if (!_passwordHasher.Verify(req.Password, user.PasswordHash))
        {
            return BadRequest(new { error = "Incorrect password." });
        }

        await _resetTokens.DeleteForUserAsync(userId, ct);
        await _tokens.DeleteForUserAsync(userId, ct);
        await _users.DeleteAsync(userId, ct);

        Response.Cookies.Delete("auth_token", new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
        });

        _logger.LogInformation("User {UserId} deleted their account.", userId);

        return Ok(new { success = true });
    }

    /// <summary>
    /// Initiates a password reset by sending a 6-digit OTP to the user's email.
    /// Always returns the same response regardless of whether the email exists (prevents enumeration).
    ///</summary>
    [HttpPost("forgot-password")]
    [EnableRateLimiting("forgot-password")]
    public async Task<IActionResult> ForgotPassword(
        [FromBody] ForgotPasswordRequest req,
        CancellationToken ct)
    {
        var user = await _users.GetByEmailAsync(req.Email, ct);
        if (user is null)
        {
            return BadRequest(new { error = "there is no account associated with this email." });
        }

        await _resetTokens.InvalidateAllForUserAsync(user.Id, ct);

        var code = _tokenGenerator.GenerateCode();
        var codeHash = _tokenGenerator.Hash(code);
        var expiresAt = DateTime.UtcNow.AddMinutes(5);
        await _resetTokens.CreateAsync(new PasswordResetToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            CodeHash = codeHash,
            ExpiresAt = expiresAt,
            Attempts = 0,
            UsedAt = null,
            CreatedAt = DateTime.UtcNow,
        }, ct);

        var htmlBody = $"""
            <p>Your Lost & Found password reset code</p>
            <p style="font-size:32px;font-weight:bold;letter-spacing:6px">{code}</p>
            <p>This code expires in 5 minutes.</p>
            <p>If you didn't request this, you can safely ignore this email.</p>
            """;
        var plainBody =
            $"Your password reset code is: {code}\n\n" +
            $"This code expires in 5 minutes.";

        await _email.SendAsync(
            user.Email,
            "Your Lost & Found password reset code",
            htmlBody,
            plainBody,
            ct);

        var sessionToken = _sessionService.Issue(user.Id);

        return Ok(new { sessionToken });
    }

    /// <summary>
    /// Resets the user's password using a 6-digit OTP and a new password.
    /// Validates the OTP, enforces complexity rules, and hashes the new password.
    ///</summary>
    [HttpPost("reset-password")]
    [EnableRateLimiting("reset-password")]
    public async Task<IActionResult> ResetPassword(
        [FromBody] ResetPasswordRequest req,
        CancellationToken ct)
    {
        var userId = _sessionService.Validate(req.SessionToken);
        if (userId is null)
        {
            return BadRequest(new { error = "Invalid or expired reset session." });
        }

        var codeHash = _tokenGenerator.Hash(req.Code);
        var token = await _resetTokens.GetActiveByUserIdAsync(userId.Value, ct);

        if (token is null)
        {
            return BadRequest(new { error = "Invalid or expired reset code." });
        }

        if (token.CodeHash != codeHash)
        {
            await _resetTokens.IncrementAttemptsAsync(token.Id, ct);
            if (token.Attempts + 1 >= _auth.MaxOtpAttempts)
            {
                await _resetTokens.MarkUsedAsync(token.Id, ct);
                return BadRequest(new { error = "Too many failed attempts. Please request a new code." });
            }
            return BadRequest(new { error = "Invalid or expired reset code." });
        }

        var (pwOk, pwErrors) = _passwordValidator.Validate(req.NewPassword);
        if (!pwOk)
        {
            return ValidationProblem(new ValidationProblemDetails(
                new Dictionary<string, string[]> { ["Password"] = pwErrors.ToArray() }));
        }

        var newHash = _passwordHasher.Hash(req.NewPassword);
        await _users.UpdatePasswordHashAsync(userId.Value, newHash, ct);
        await _resetTokens.MarkUsedAsync(token.Id, ct);

        var user = await _users.GetByIdAsync(userId.Value, ct)
            ?? throw new InvalidOperationException("Session references a non-existent user.");

        var jwtToken = _jwtTokenService.IssueLoginToken(user);
        var isProduction = HttpContext.RequestServices.GetService<IHostEnvironment>()?.IsProduction() ?? false;
        var cookieOptions = new CookieOptions
        {
            HttpOnly = true,
            Secure = isProduction,
            SameSite = isProduction ? SameSiteMode.None : SameSiteMode.Lax,
            Path = "/",
            Expires = DateTime.UtcNow.AddMinutes(_jwt.ExpiryMinutes)
        };
        Response.Cookies.Append("auth_token", jwtToken, cookieOptions);

        _logger.LogInformation("User {UserId} reset their password and logged in.", userId.Value);

        return Ok(new { success = true });
    }

    private async Task<ActionResult<UserProfileDto>> GetUserProfileFromToken(CancellationToken ct)
    {
        var userIdClaim = User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdClaim is null || !Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized(new { error = "Invalid session." });
        }

        var user = await _users.GetByIdAsync(userId, ct);
        if (user is null)
        {
            return Unauthorized(new { error = "User not found." });
        }

        return Ok(new UserProfileDto
        {
            Id = user.Id,
            Email = user.Email,
            Name = user.Name,
            PhoneNo = user.PhoneNo,
            IsAdmin = user.IsAdmin,
            IsEmailVerified = user.IsEmailVerified,
            CreatedAt = user.CreatedAt
        });
    }

    private Guid? GetCurrentUserIdFromCookie()
    {
        if (!HttpContext.Request.Cookies.TryGetValue("auth_token", out var token) || string.IsNullOrEmpty(token))
            return null;

        try
        {
            var handler = new JwtSecurityTokenHandler();
            var jwt = handler.ReadJwtToken(token);
            if (jwt.ValidTo < DateTime.UtcNow) return null;
            var sub = jwt.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Sub)?.Value;
            return Guid.TryParse(sub, out var userId) ? userId : null;
        }
        catch
        {
            return null;
        }
    }
}
