using System.Net.Http.Json;
using AuthService.Models.Dtos;
using AuthService.Tests.Integration.Fakes;

namespace AuthService.Tests.Integration;

public record TestUser(string Email, string Password, string Name, string PhoneNo);

/// <summary>
/// Registers and fully verifies a brand-new user against the real API + real DB.
/// Every integration test class needing "an existing verified user" starts here,
/// so each test gets its own isolated user (unique email/phone) even though they
/// share one MySQL container.
/// </summary>
public static class TestUserFactory
{
    private static long _counter;

    public static async Task<TestUser> RegisterAndVerifyAsync(HttpClient client, FakeEmailService fakeEmail)
    {
        var unique = Guid.NewGuid().ToString("N")[..10];
        // Phone must be E.164: '+' then digits only, so build it from digits, not the hex guid.
        var digits = Interlocked.Increment(ref _counter).ToString().PadLeft(9, '0');
        var user = new TestUser(
            Email: $"itest_{unique}@example.com",
            Password: "Str0ng!Passw0rd",
            Name: "Integration Test User",
            PhoneNo: $"+947{digits[^8..]}"); // e.g. +94712345678

        var registerResponse = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            Email = user.Email,
            Password = user.Password,
            Name = user.Name,
            PhoneNo = user.PhoneNo
        });
        registerResponse.EnsureSuccessStatusCode();
        var registerBody = await registerResponse.Content.ReadFromJsonAsync<RegisterResponse>()
            ?? throw new InvalidOperationException("Register response body was empty.");

        var code = fakeEmail.GetLastOtpCodeFor(user.Email);

        var verifyResponse = await client.PostAsJsonAsync("/api/auth/verify-email", new VerifyEmailRequest
        {
            SessionToken = registerBody.VerificationSessionToken,
            Code = code
        });
        verifyResponse.EnsureSuccessStatusCode();

        return user;
    }
}
