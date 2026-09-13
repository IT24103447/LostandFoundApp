using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using Xunit;

namespace ReportLostItemForm.SeleniumTests;

/// <summary>
/// Story 7 browser coverage. Set the named report ID and title variables before
/// running. The confirmed-match browser check remains deferred because a
/// confirmed match cannot yet be created through the application.
/// </summary>
[Trait("Story", "7")]
[Trait("Requires", "DeleteReportTestData")]
public sealed class DeleteItemReportSeleniumTests : IClassFixture<ReportLostItemFixture>
{
    // Story 7 browser flow: cancel safely, then confirm deletion for disposable lost and found reports.
    private readonly IWebDriver _driver;
    private readonly WebDriverWait _wait;

    public DeleteItemReportSeleniumTests(ReportLostItemFixture fixture)
    {
        _driver = fixture.Driver;
        _wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(15));
    }

    // Verifies cancelling leaves the active report visible after refresh.
    [Fact]
    public void Delete_CancelClosesConfirmationAndLeavesActiveReportVisibleAfterRefresh()
    {
        var report = RequiredReport("SELENIUM_DELETE_CANCEL_REPORT");
        OpenMyReports();

        OpenDeleteModal(FindReportAcrossPages(report));
        Assert.Equal("Delete this report?", _driver.FindElement(By.Id("delete-modal-title")).Text);
        Assert.Contains(report.Title, _driver.PageSource, StringComparison.Ordinal);

        _driver.FindElement(By.XPath("//button[normalize-space()='Cancel']")).Click();
        _wait.Until(d => d.FindElements(By.Id("delete-modal-title")).Count == 0);

        _driver.Navigate().Refresh();
        WaitForReportsToFinishLoading();
        Assert.NotNull(FindReportAcrossPages(report));
    }

    // Verifies confirmed owner deletion removes the report from history and public details.
    [Theory]
    [InlineData("SELENIUM_DELETE_LOST_REPORT", "lost")]
    [InlineData("SELENIUM_DELETE_FOUND_REPORT", "found")]
    public void Delete_OwnerConfirms_RemovesReportFromHistoryAndDetailsPage(string environmentPrefix, string expectedKind)
    {
        var report = RequiredReport(environmentPrefix);
        OpenMyReports();

        var card = FindReportAcrossPages(report);
        Assert.Contains(expectedKind.ToUpperInvariant(), card.Text, StringComparison.Ordinal);

        OpenDeleteModal(card);
        _driver.FindElement(By.XPath("//button[normalize-space()='Yes, Delete']")).Click();
        _wait.Until(d => d.FindElements(By.Id("delete-modal-title")).Count == 0);

        _driver.Navigate().Refresh();
        WaitForReportsToFinishLoading();
        Assert.False(IsReportVisibleOnAnyPage(report));

        _driver.Navigate().GoToUrl($"{ReportLostItemFixture.BaseUrl}/items/{report.Id}");
        _wait.Until(d => d.PageSource.Contains("This item couldn't be found", StringComparison.Ordinal));
    }

    private void OpenMyReports()
    {
        _driver.Navigate().GoToUrl($"{ReportLostItemFixture.BaseUrl}/my-reports");
        _wait.Until(d => d.FindElement(By.XPath("//h1[normalize-space()='My Reports']")));
        WaitForReportsToFinishLoading();
    }

    private void OpenDeleteModal(IWebElement card)
    {
        card.FindElement(By.CssSelector("button[aria-label='Delete report']")).Click();
        _wait.Until(d => d.FindElement(By.Id("delete-modal-title")));
    }

    private IWebElement FindReportAcrossPages(ReportIdentity report)
    {
        for (var page = 1; page <= 20; page++)
        {
            var card = FindOnCurrentPage(report);
            if (card is not null) return card;
            if (!GoToNextPage()) break;
        }

        throw new Xunit.Sdk.XunitException(
            $"Could not find '{report.Title}' in My Reports. Confirm it belongs to SELENIUM_TEST_EMAIL and is ACTIVE.");
    }

    private bool IsReportVisibleOnAnyPage(ReportIdentity report)
    {
        for (var page = 1; page <= 20; page++)
        {
            if (FindOnCurrentPage(report) is not null) return true;
            if (!GoToNextPage()) return false;
        }

        return false;
    }

    private IWebElement? FindOnCurrentPage(ReportIdentity report)
    {
        var title = _driver.FindElements(By.XPath($"//p[normalize-space()={XPathLiteral(report.Title)}]")).FirstOrDefault();
        return title?.FindElement(By.XPath(
            "./ancestor::div[contains(concat(' ', normalize-space(@class), ' '), ' flex-col ')][1]"));
    }

    private bool GoToNextPage()
    {
        var next = _driver.FindElements(By.CssSelector("button[aria-label='Next page']")).FirstOrDefault();
        if (next is null || !next.Enabled) return false;

        var pageSource = _driver.PageSource;
        next.Click();
        _wait.Until(d => !string.Equals(d.PageSource, pageSource, StringComparison.Ordinal));
        return true;
    }

    private void WaitForReportsToFinishLoading()
    {
        _wait.Until(d => !d.PageSource.Contains("Loading your reports", StringComparison.Ordinal));

        if (_driver.PageSource.Contains("We couldn't load your reports", StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException("My Reports could not load. Confirm the Item Service is running.");
    }

    private static ReportIdentity RequiredReport(string prefix) =>
        new(
            Guid.Parse(Required($"{prefix}_ID")),
            Required($"{prefix}_TITLE"));

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name)?.Trim()
        ?? throw new InvalidOperationException($"Set {name} before running Story 7 Selenium tests.");

    private static string XPathLiteral(string value)
    {
        if (!value.Contains('\'')) return $"'{value}'";
        if (!value.Contains('"')) return $"\"{value}\"";
        return "concat('" + value.Replace("'", "', \"'\", '") + "')";
    }

    private sealed record ReportIdentity(Guid Id, string Title);
}
