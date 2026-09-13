using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using Xunit;

namespace ReportLostItemForm.SeleniumTests;

/// <summary>
/// Story 8 browser coverage. Configure one known lost and one known found report
/// owned by SELENIUM_TEST_EMAIL. No frontend test IDs are required.
/// </summary>
[Trait("Story", "8")]
[Trait("Requires", "MyReportsTestData")]
public sealed class MyReportsSeleniumTests : IClassFixture<ReportLostItemFixture>
{
    // Story 8 browser flow: owner history shows both report types, current state, and resolved filtering.
    private readonly IWebDriver _driver;
    private readonly WebDriverWait _wait;

    public MyReportsSeleniumTests(ReportLostItemFixture fixture)
    {
        _driver = fixture.Driver;
        _wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(15));
    }

    // Verifies visible lost/found cards and their configured current statuses.
    [Fact]
    public void MyReports_OwnerSeesOwnLostAndFoundReportsWithCurrentStatuses()
    {
        var lostTitle = Required("SELENIUM_MY_REPORTS_LOST_TITLE");
        var lostStatus = Required("SELENIUM_MY_REPORTS_LOST_STATUS");
        var foundTitle = Required("SELENIUM_MY_REPORTS_FOUND_TITLE");
        var foundStatus = Required("SELENIUM_MY_REPORTS_FOUND_STATUS");

        OpenMyReports();

        FindReportAcrossPages(lostTitle, lostStatus, "LOST");
        FindReportAcrossPages(foundTitle, foundStatus, "FOUND");
    }

    // Verifies the resolved filter retains the configured resolved report.
    [Fact]
    public void MyReports_ResolvedFilterShowsTheConfiguredResolvedOwnerReport()
    {
        var resolvedTitle = Required("SELENIUM_MY_REPORTS_RESOLVED_TITLE");
        OpenMyReports();

        _driver.FindElement(By.XPath("//button[normalize-space()='[ Resolved ]']")).Click();
        FindReportAcrossPages(resolvedTitle, "RESOLVED", null);
    }

    private void OpenMyReports()
    {
        _driver.Navigate().GoToUrl($"{ReportLostItemFixture.BaseUrl}/my-reports");
        _wait.Until(d => d.FindElement(By.XPath("//h1[normalize-space()='My Reports']")));
        WaitForReportsToFinishLoading();
    }

    private void FindReportAcrossPages(string title, string status, string? kind)
    {
        for (var page = 1; page <= 20; page++)
        {
            var titleElement = _driver.FindElements(By.XPath($"//p[normalize-space()={XPathLiteral(title)}]"))
                .FirstOrDefault();

            if (titleElement is not null)
            {
                var card = titleElement.FindElement(By.XPath(
                    "./ancestor::div[contains(concat(' ', normalize-space(@class), ' '), ' flex-col ')][1]"));

                Assert.Contains(status, card.Text, StringComparison.OrdinalIgnoreCase);
                if (kind is not null)
                    Assert.Contains(kind, card.Text, StringComparison.Ordinal);
                return;
            }

            var next = _driver.FindElement(By.CssSelector("button[aria-label='Next page']"));
            if (!next.Enabled)
                break;

            var previousPageText = _driver.PageSource;
            next.Click();
            _wait.Until(d => !string.Equals(d.PageSource, previousPageText, StringComparison.Ordinal));
        }

        throw new Xunit.Sdk.XunitException(
            $"Could not find report '{title}' in the configured My Reports pages. " +
            "Confirm that it belongs to SELENIUM_TEST_EMAIL and that its configured status is current.");
    }

    private void WaitForReportsToFinishLoading()
    {
        _wait.Until(d => !d.PageSource.Contains("Loading your reports", StringComparison.Ordinal));

        if (_driver.PageSource.Contains("We couldn't load your reports", StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException(
                "My Reports displayed its load error. Confirm the Item Service is running with GET /api/items/lost/mine and GET /api/items/found/mine available.");
    }

    private static string XPathLiteral(string value)
    {
        if (!value.Contains('\'')) return $"'{value}'";
        if (!value.Contains('"')) return $"\"{value}\"";

        return "concat('" + value.Replace("'", "', \"'\", '") + "')";
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name)?.Trim()
        ?? throw new InvalidOperationException($"Set {name} before running Story 8 Selenium tests.");
}
