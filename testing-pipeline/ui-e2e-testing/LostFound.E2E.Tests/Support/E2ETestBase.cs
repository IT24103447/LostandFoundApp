using OpenQA.Selenium;

namespace LostFound.E2E.Tests.Support;

/// <summary>
/// Base class for every E2E test class. xUnit creates a new instance per test, so the constructor
/// gives each test a logged-out browser even though Chrome itself is shared by the class.
/// </summary>
public abstract class E2ETestBase
{
    protected BrowserFixture Fixture { get; }
    protected IWebDriver Driver => Fixture.Driver;

    protected E2ETestBase(BrowserFixture fixture)
    {
        Fixture = fixture;
        ResetSession();
    }

    private void ResetSession()
    {
        // A hung or crashed browser would make every remaining test wait out its own timeout.
        // Replace it (up to twice) and retry; if it still fails, surface the real error.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                ClearBrowserState();
                return;
            }
            catch (WebDriverException) when (attempt < 3)
            {
                Fixture.Restart();
            }
        }
    }

    /// <summary>Signs everyone out (cookies and storage). Used between users inside one test.</summary>
    protected void ClearBrowserState()
    {
        // Cookies can only be removed while on the app's own domain.
        Driver.Navigate().GoToUrl(TestConfig.BaseUrl + "/login");
        Driver.Manage().Cookies.DeleteAllCookies();
        ((IJavaScriptExecutor)Driver).ExecuteScript("window.localStorage.clear(); window.sessionStorage.clear();");
    }

    protected void Open(string path) => Driver.Navigate().GoToUrl(TestConfig.BaseUrl + path);

    /// <summary>Runs a test body and saves a screenshot if it fails, then rethrows.</summary>
    protected void Run(string testName, Action body)
    {
        try
        {
            body();
        }
        catch
        {
            Fixture.Capture(testName);
            throw;
        }
    }
}
