using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;

namespace ReportLostItemForm.SeleniumTests;

/// <summary>
/// Drives the real login UI at /login so that tests run as an authenticated user.
/// Requires:
///   1. A test account to exist in the database (email + password below).
///   2. The frontend running at the given baseUrl (npm run dev).
///   3. The AuthService (local/deployed) to be reachable from the frontend.
/// </summary>
public static class LoginHelper
{
    private const string TestEmail    = "selenium.test@example.com";
    private const string TestPassword = "Str0ngPass1";

    public static string AuthToken { get; set; } = string.Empty;

    public static void LoginAsTestUser(IWebDriver driver, string baseUrl)
    {
        driver.Navigate().GoToUrl($"{baseUrl}/login");

        var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(10));

        wait.Until(d => d.FindElement(By.Id("email")));

        driver.FindElement(By.Id("email")).Clear();
        driver.FindElement(By.Id("email")).SendKeys(TestEmail);

        driver.FindElement(By.Id("password")).Clear();
        driver.FindElement(By.Id("password")).SendKeys(TestPassword);

        driver.FindElement(By.CssSelector("button[type='submit']")).Click();

        wait.Until(d => !d.Url.Contains("/login"));

        // Capture the auth_token cookie set on frontend origin (5173)
        var authCookie = driver.Manage().Cookies.GetCookieNamed("auth_token");
        if (authCookie != null)
        {
            AuthToken = authCookie.Value;
        }
    }
}

