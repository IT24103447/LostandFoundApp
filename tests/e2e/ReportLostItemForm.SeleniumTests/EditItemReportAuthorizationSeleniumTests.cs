using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using Xunit;

namespace ReportLostItemForm.SeleniumTests;

/// <summary>
/// Browser-level ownership check for Story 3. Run this trait only with a
/// second account that does not own SELENIUM_EDIT_REPORT_ID.
/// </summary>
[Trait("Requires", "NonOwnerCredentials")]
public sealed class EditItemReportAuthorizationSeleniumTests : IDisposable
{
    private readonly BrowserFixture _fixture = new();
    private readonly IWebDriver _driver;
    private readonly WebDriverWait _wait;

    public EditItemReportAuthorizationSeleniumTests()
    {
        _driver = _fixture.Driver;
        _wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(10));

        LoginHelper.LoginAs(
            _driver,
            BrowserFixture.BaseUrl,
            GetRequiredSetting("SELENIUM_NONOWNER_EMAIL"),
            GetRequiredSetting("SELENIUM_NONOWNER_PASSWORD"));
        ReportLostItemFixture.SyncAuthCookieToItemService(_driver, LoginHelper.AuthToken);
    }

    [Fact]
    public void EditReport_NonOwnerSave_ShowsPermissionMessageAndDoesNotShowSuccess()
    {
        _driver.Navigate().GoToUrl(EditReportUrl);
        _wait.Until(d => d.FindElement(By.Id("title")));

        var hiddenInformation = _driver.FindElement(By.Id("hiddenInformation"));
        hiddenInformation.SendKeys(GetRequiredSetting("SELENIUM_EDIT_HIDDEN_INFORMATION"));
        _driver.FindElement(By.XPath("//button[normalize-space()='Save Changes']")).Click();

        var error = _wait.Until(d => d.FindElement(By.XPath(
            "//div[contains(normalize-space(), \"don't have permission\")]")));
        Assert.Contains("permission", error.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Changes saved.", _driver.PageSource, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _fixture.Dispose();

    private static string EditReportUrl
    {
        get
        {
            var type = (Environment.GetEnvironmentVariable("SELENIUM_EDIT_REPORT_TYPE") ?? "lost")
                .Trim()
                .ToLowerInvariant();
            if (type is not ("lost" or "found"))
                throw new InvalidOperationException("SELENIUM_EDIT_REPORT_TYPE must be either 'lost' or 'found'.");

            return $"{BrowserFixture.BaseUrl}/edit-{type}-item/{GetRequiredSetting("SELENIUM_EDIT_REPORT_ID")}";
        }
    }

    private static string GetRequiredSetting(string name) =>
        Environment.GetEnvironmentVariable(name)?.Trim()
        ?? throw new InvalidOperationException($"Set the {name} environment variable before running this Selenium test.");
}
