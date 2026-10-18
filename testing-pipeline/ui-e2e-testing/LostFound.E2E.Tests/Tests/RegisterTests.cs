using LostFound.E2E.Tests.Pages;
using LostFound.E2E.Tests.Support;
using Xunit;

namespace LostFound.E2E.Tests.Tests;

/// <summary>
/// Registration paths that do not need an e-mailed code (test plan section 4, "OTP constraint"):
/// client-side validation lives in AuthTests (AUTH_09); this class covers the server's duplicate-email
/// refusal. Only ONE request is sent to /register per run because registration is rate-limited
/// (5 per minute, held in memory).
/// </summary>
public class RegisterTests : E2ETestBase, IClassFixture<BrowserFixture>
{
    public RegisterTests(BrowserFixture fixture) : base(fixture) { }

    /// <summary>A valid E.164 number (the form requires a leading +) that no seeded account can already own.</summary>
    private static string UniquePhone() => $"+94{Random.Shared.Next(100000000, 999999999)}";

    [Fact]
    [Trait("Category", "Full")]
    [Trait("Description", "Registering with the e-mail address of an existing account is refused: an 'already' error appears on the e-mail field and the user stays on /register, so no account and no verification e-mail is created.")]
    public void AUTH_10_Register_DuplicateEmail_ShowsInlineError() => Run(nameof(AUTH_10_Register_DuplicateEmail_ShowsInlineError), () =>
    {
        // Every other field is valid and unique, so the e-mail is the only thing the server can object to.
        var register = new RegisterPage(Driver).Open()
            .FillAll("E2E Duplicate", TestConfig.User1.Email, UniquePhone(), "Str0ngPass1");

        register.SubmitForm();

        Assert.Contains("already", register.WaitForEmailError(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("/register", new Uri(Driver.Url).AbsolutePath);
    });
}
