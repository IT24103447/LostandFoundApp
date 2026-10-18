using LostFound.E2E.Tests.Pages;
using LostFound.E2E.Tests.Support;
using OpenQA.Selenium;
using Xunit;
using Xunit.Abstractions;

namespace LostFound.E2E.Tests.Tests;

/// <summary>Robustness: things a user can do that are not the happy path (test plan section 6.8).</summary>
public class NegativeTests : E2ETestBase, IClassFixture<BrowserFixture>
{
    private readonly ITestOutputHelper _output;

    public NegativeTests(BrowserFixture fixture, ITestOutputHelper output) : base(fixture) => _output = output;

    private void SignInAsUser1()
    {
        new LoginPage(Driver).LoginAs(TestConfig.User1);
        new HomePage(Driver).WaitUntilLoaded();
    }

    [Fact]
    [Trait("Category", "Full")]
    [Trait("Description", "Opening an item id that does not exist shows a friendly 'could not be found' message with a way back, not a crash.")]
    public void NEG_01_UnknownItemId_ShowsFriendlyNotFoundState() => Run(nameof(NEG_01_UnknownItemId_ShowsFriendlyNotFoundState), () =>
    {
        SignInAsUser1();

        Open("/items/00000000-0000-0000-0000-000000000000");

        Driver.WaitVisible(By.XPath("//p[contains(normalize-space(),'be found')]"));
        Driver.WaitVisible(By.XPath("//button[normalize-space()='Back to Results']"));
    });

    [Fact]
    [Trait("Category", "Full")]
    [Trait("Description", "If the sign-in cookie disappears while the user is on a protected page, the next navigation sends them to the login page.")]
    public void NEG_02_AuthCookieRemoved_RedirectsToLogin() => Run(nameof(NEG_02_AuthCookieRemoved_RedirectsToLogin), () =>
    {
        SignInAsUser1();
        new MyReportsPage(Driver).Open();

        Driver.Manage().Cookies.DeleteAllCookies();
        Open("/my-reports");

        Driver.WaitForPath("/login");
    });

    [Fact]
    [Trait("Category", "Full")]
    [Trait("Description", "An unknown route does not crash the app (no error overlay); the test records what the user actually sees, because the app has no catch-all route.")]
    public void NEG_03_UnknownRoute_DoesNotCrash_AndActualBehaviourIsRecorded() => Run(nameof(NEG_03_UnknownRoute_DoesNotCrash_AndActualBehaviourIsRecorded), () =>
    {
        SignInAsUser1();

        Open("/does-not-exist");
        Driver.Wait().Until(d => (string)((IJavaScriptExecutor)d).ExecuteScript("return document.readyState") == "complete");

        Assert.Empty(Driver.FindElements(By.TagName("vite-error-overlay")));

        var visibleText = (string)((IJavaScriptExecutor)Driver).ExecuteScript(
            "return (document.getElementById('root')?.innerText ?? '').trim();");
        _output.WriteLine(visibleText.Length == 0
            ? $"Observed: '{new Uri(Driver.Url).AbsolutePath}' renders a BLANK page (no catch-all route in App.tsx). Logged as a defect."
            : $"Observed: '{new Uri(Driver.Url).AbsolutePath}' shows: {visibleText[..Math.Min(80, visibleText.Length)]}");
    });
}
