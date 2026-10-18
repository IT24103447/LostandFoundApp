using LostFound.E2E.Tests.Pages;
using LostFound.E2E.Tests.Support;
using OpenQA.Selenium;
using Xunit;

namespace LostFound.E2E.Tests.Tests;

/// <summary>
/// Authentication and route-guard journeys (test plan section 6.1).
/// Uses the seeded accounts from the pipeline's users.csv / admin.csv.
/// </summary>
public class AuthTests : E2ETestBase, IClassFixture<BrowserFixture>
{
    public AuthTests(BrowserFixture fixture) : base(fixture) { }

    [Fact]
    [Trait("Category", "Smoke")]
    [Trait("Category", "Demo")]
    [Trait("Description", "A seeded verified user can sign in and lands on the home page.")]
    public void AUTH_01_ValidUserLogin_LandsOnHomePage() => Run(nameof(AUTH_01_ValidUserLogin_LandsOnHomePage), () =>
    {
        new LoginPage(Driver).LoginAs(TestConfig.User1);

        new HomePage(Driver).WaitUntilLoaded();
    });

    [Fact]
    [Trait("Category", "Smoke")]
    [Trait("Description", "An admin signs in and is taken to the admin area (User Management), not the user home page.")]
    public void AUTH_02_AdminLogin_LandsInAdminArea() => Run(nameof(AUTH_02_AdminLogin_LandsInAdminArea), () =>
    {
        new LoginPage(Driver).LoginAs(TestConfig.Admin);

        new AdminHomePage(Driver).WaitUntilLoaded();
    });

    [Fact]
    [Trait("Category", "Smoke")]
    [Trait("Category", "Demo")]
    [Trait("Description", "A wrong password and an unknown email give the same generic error, so the form does not reveal which accounts exist.")]
    public void AUTH_03_InvalidCredentials_ShowSameGenericError() => Run(nameof(AUTH_03_InvalidCredentials_ShowSameGenericError), () =>
    {
        var login = new LoginPage(Driver);

        login.LoginAs(TestConfig.User1.Email, "Wrong-Password-1!");
        var wrongPasswordMessage = login.WaitForErrorMessage();

        login.LoginAs($"nobody-{Guid.NewGuid():N}@example.com", "Wrong-Password-1!");
        var unknownEmailMessage = login.WaitForErrorMessage();

        Assert.False(string.IsNullOrWhiteSpace(wrongPasswordMessage));
        Assert.Equal(wrongPasswordMessage, unknownEmailMessage);
        Driver.WaitForPath("/login");
    });

    [Fact]
    [Trait("Category", "Full")]
    [Trait("Category", "Demo")]
    [Trait("Description", "Submitting an empty login form shows inline required-field errors and stays on the login page.")]
    public void AUTH_04_EmptyForm_ShowsInlineValidationErrors() => Run(nameof(AUTH_04_EmptyForm_ShowsInlineValidationErrors), () =>
    {
        var login = new LoginPage(Driver);

        login.SubmitEmpty();

        Assert.Equal("Email is required.", login.WaitForEmailError());
        Assert.Equal("Password is required.", login.WaitForPasswordError());
        Driver.WaitForPath("/login");
    });

    [Fact]
    [Trait("Category", "Smoke")]
    [Trait("Description", "A visitor who is not signed in is redirected to /login when opening a protected page directly.")]
    public void AUTH_05_ProtectedPage_RedirectsAnonymousVisitorToLogin() => Run(nameof(AUTH_05_ProtectedPage_RedirectsAnonymousVisitorToLogin), () =>
    {
        Open("/my-reports");

        Driver.WaitForPath("/login");
    });

    [Fact]
    [Trait("Category", "Smoke")]
    [Trait("Category", "Demo")]
    [Trait("Description", "A regular user who types an admin URL is bounced back to the home page; the admin UI never renders.")]
    public void AUTH_06_RegularUser_CannotOpenAdminPages() => Run(nameof(AUTH_06_RegularUser_CannotOpenAdminPages), () =>
    {
        new LoginPage(Driver).LoginAs(TestConfig.User1);
        new HomePage(Driver).WaitUntilLoaded();

        Open("/admin/users");

        new HomePage(Driver).WaitUntilLoaded();
    });

    [Fact]
    [Trait("Category", "Full")]
    [Trait("Description", "An admin who opens a user-only page is redirected to the admin area.")]
    public void AUTH_07_Admin_CannotUseUserOnlyPages() => Run(nameof(AUTH_07_Admin_CannotUseUserOnlyPages), () =>
    {
        new LoginPage(Driver).LoginAs(TestConfig.Admin);
        new AdminHomePage(Driver).WaitUntilLoaded();

        Open("/report-lost-item");

        new AdminHomePage(Driver).WaitUntilLoaded();
    });

    [Fact]
    [Trait("Category", "Full")]
    [Trait("Category", "Demo")]
    [Trait("Description", "After signing out, pressing Back never shows the protected page (the app replaces history entries, so Back leaves the app); Forward returns to the login page.")]
    public void AUTH_08_SignOut_ThenBrowserBack_DoesNotShowProtectedPage() => Run(nameof(AUTH_08_SignOut_ThenBrowserBack_DoesNotShowProtectedPage), () =>
    {
        new LoginPage(Driver).LoginAs(TestConfig.User1);
        new HomePage(Driver).WaitUntilLoaded();

        new AccountMenuPage(Driver).SignOut();
        Driver.Navigate().Back();

        // The app replaces history entries on login and sign-out, so Back leaves the app
        // instead of revealing the protected page.
        Assert.Empty(Driver.FindElements(By.XPath("//h1[normalize-space()='Find an Item']")));

        Driver.Navigate().Forward();
        Driver.WaitForPath("/login");
        Assert.Empty(Driver.FindElements(By.XPath("//h1[normalize-space()='Find an Item']")));
    });

    [Fact]
    [Trait("Category", "Full")]
    [Trait("Description", "The register form rejects a malformed e-mail and a weak password with inline errors, shows a Weak strength rating, and does not submit.")]
    public void AUTH_09_Register_WeakPasswordAndBadEmail_ShowInlineErrors() => Run(nameof(AUTH_09_Register_WeakPasswordAndBadEmail_ShowInlineErrors), () =>
    {
        var register = new RegisterPage(Driver).Open().Fill("not-an-email", "abc");

        register.WaitForStrengthLabel("Weak");
        register.SubmitForm();

        Assert.Equal("Enter a valid email address.", register.WaitForEmailError());
        Assert.Equal("Password must be at least 8 characters.", register.WaitForPasswordError());
        Assert.Equal("/register", new Uri(Driver.Url).AbsolutePath);
    });
}
