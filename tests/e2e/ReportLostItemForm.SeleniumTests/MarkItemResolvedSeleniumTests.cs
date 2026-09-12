using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using Xunit;

namespace ReportLostItemForm.SeleniumTests;

/// <summary>
/// Story 5 UI tests. They require disposable ACTIVE reports owned by SELENIUM_TEST_EMAIL:
/// SELENIUM_RESOLVE_LOST_REPORT_ID (resolved by the success test) and
/// SELENIUM_RESOLVE_CANCEL_REPORT_ID (left ACTIVE by the cancel test).
/// </summary>
[Trait("Story", "5")]
[Trait("Requires", "ResolveTestData")]
public sealed class MarkItemResolvedSeleniumTests : IClassFixture<ReportLostItemFixture>
{
    private readonly IWebDriver _driver;
    private readonly WebDriverWait _wait;

    public MarkItemResolvedSeleniumTests(ReportLostItemFixture fixture)
    {
        _driver = fixture.Driver;
        _wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Resolve_CancelClosesConfirmationWithoutChangingAnActiveReport()
    {
        var card = OpenMyReport(Required("SELENIUM_RESOLVE_CANCEL_REPORT_ID"), "lost");
        card.FindElement(By.CssSelector("[data-testid='resolve-report']")).Click();
        _wait.Until(d => d.FindElement(By.Id("resolve-modal-title")));
        Assert.Contains("no longer appear", _driver.PageSource, StringComparison.OrdinalIgnoreCase);
        _driver.FindElement(By.XPath("//button[normalize-space()='Cancel']")).Click();
        _wait.Until(d => !d.FindElements(By.Id("resolve-modal-title")).Any());
        Assert.Contains("ACTIVE", card.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Single(card.FindElements(By.CssSelector("[data-testid='resolve-report']")));
    }

    [Fact]
    public void Resolve_OwnerConfirms_StatusBecomesResolvedAndReportRemainsInHistory()
    {
        var card = OpenMyReport(Required("SELENIUM_RESOLVE_LOST_REPORT_ID"), "lost");
        card.FindElement(By.CssSelector("[data-testid='resolve-report']")).Click();
        _wait.Until(d => d.FindElement(By.Id("resolve-modal-title")));
        _driver.FindElement(By.XPath("//button[normalize-space()='Yes, Resolve']")).Click();
        _wait.Until(d => d.FindElement(By.CssSelector($"[data-report-id='{Required("SELENIUM_RESOLVE_LOST_REPORT_ID")}']")).Text.Contains("RESOLVED", StringComparison.OrdinalIgnoreCase));
        var resolvedCard = _driver.FindElement(By.CssSelector($"[data-report-id='{Required("SELENIUM_RESOLVE_LOST_REPORT_ID")}']"));
        Assert.Contains("RESOLVED", resolvedCard.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(resolvedCard.FindElements(By.CssSelector("[data-testid='resolve-report']")));
    }

    [Fact]
    public void Resolve_NonOwnerDirectRequestReturns403AndDoesNotShowSuccess()
    {
        LoginHelper.LoginAs(_driver, ReportLostItemFixture.BaseUrl, Required("SELENIUM_NONOWNER_EMAIL"), Required("SELENIUM_NONOWNER_PASSWORD"));
        ReportLostItemFixture.SyncAuthCookieToItemService(_driver, LoginHelper.AuthToken);
        var id = Required("SELENIUM_RESOLVE_NONOWNER_REPORT_ID");
        var status = (long)((IJavaScriptExecutor)_driver).ExecuteAsyncScript(@"
            const done = arguments[arguments.length - 1];
            fetch('http://localhost:5001/api/items/lost/' + arguments[0] + '/resolve', { method: 'POST', credentials: 'include', headers: { 'Content-Type': 'application/json' }, body: '{}' })
              .then(r => done(r.status)).catch(e => done(-1));", id)!;
        Assert.Equal(403L, status);
        Assert.DoesNotContain("resolved successfully", _driver.PageSource, StringComparison.OrdinalIgnoreCase);
    }

    private IWebElement OpenMyReport(string id, string kind)
    {
        _driver.Navigate().GoToUrl($"{ReportLostItemFixture.BaseUrl}/my-reports");
        var selector = $"[data-report-id='{id}'][data-report-kind='{kind}']";
        return _wait.Until(d => d.FindElement(By.CssSelector(selector)));
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name)?.Trim()
        ?? throw new InvalidOperationException($"Set {name} before running Story 5 Selenium tests.");
}
