using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using Xunit;

namespace ReportLostItemForm.SeleniumTests;

/// <summary>Story 4 browser checks for the authenticated Find an Item page.</summary>
public class SearchAndFilterItemsSeleniumTests : IClassFixture<ReportLostItemFixture>
{
    private readonly IWebDriver _driver;
    private readonly WebDriverWait _wait;

    public SearchAndFilterItemsSeleniumTests(ReportLostItemFixture fixture)
    {
        _driver = fixture.Driver;
        _wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void BrowsePage_RendersSearchFiltersAndActiveOnlyMessage()
    {
        OpenBrowsePage();

        Assert.NotEmpty(_driver.FindElements(By.CssSelector("input[placeholder*='Search by item name']")));
        Assert.NotEmpty(_driver.FindElements(By.Id("category-filter")));
        Assert.Contains("Only active lost and found reports are shown", _driver.PageSource, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BrowsePage_SearchForUniqueTermShowsSuccessfulEmptyState()
    {
        OpenBrowsePage();
        var search = _driver.FindElement(By.CssSelector("input[placeholder*='Search by item name']"));
        search.SendKeys("qa-no-match-9cbb2e8e");
        _driver.FindElement(By.XPath("//button[normalize-space()='Search']")).Click();

        _wait.Until(d => d.PageSource.Contains("No matching items", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("No matching items", _driver.PageSource, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("We couldn't load items", _driver.PageSource, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BrowsePage_EnterKeyRunsKeywordSearch()
    {
        OpenBrowsePage();
        var search = _driver.FindElement(By.CssSelector("input[placeholder*='Search by item name']"));
        search.SendKeys("qa-enter-no-match-285b");
        search.SendKeys(Keys.Enter);

        _wait.Until(d => d.PageSource.Contains("No matching items", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("No matching items", _driver.PageSource, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BrowsePage_SelectingFoundTypeDisplaysFoundFilterChip()
    {
        OpenBrowsePage();
        _driver.FindElement(By.XPath("//button[normalize-space()='Found']")).Click();

        _wait.Until(d => d.FindElements(By.CssSelector("button[aria-label='Remove Found filter']")).Count == 1);
        Assert.NotEmpty(_driver.FindElements(By.CssSelector("button[aria-label='Remove Found filter']")));
    }

    [Fact]
    public void BrowsePage_DateToHasSelectedDateFromAsMinimum()
    {
        OpenBrowsePage();
        var dateInputs = _driver.FindElements(By.CssSelector("input[type='date']"));
        var dateFrom = dateInputs[0];
        var dateTo = dateInputs[1];
        ((IJavaScriptExecutor)_driver).ExecuteScript(
            "const setValue = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;" +
            "setValue.call(arguments[0], arguments[1]);" +
            "arguments[0].dispatchEvent(new Event('input', { bubbles: true }));" +
            "arguments[0].dispatchEvent(new Event('change', { bubbles: true }));",
            dateFrom, "2026-09-01");

        _wait.Until(_ => dateTo.GetAttribute("min") == "2026-09-01");
        Assert.Equal("2026-09-01", dateTo.GetAttribute("min"));
    }

    [Fact]
    public void BrowsePage_CategoryFilterCanBeAppliedAndCleared()
    {
        OpenBrowsePage();
        var category = _driver.FindElement(By.Id("category-filter"));
        ((IJavaScriptExecutor)_driver).ExecuteScript(
            "arguments[0].value = arguments[1]; arguments[0].dispatchEvent(new Event('change', { bubbles: true }));",
            category, "Accessories");
        _driver.FindElement(By.XPath("//button[normalize-space()='Apply Filters']")).Click();

        _wait.Until(d => d.FindElements(By.CssSelector("button[aria-label*='Remove']")).Count > 0);
        _driver.FindElement(By.XPath("//button[normalize-space()='Clear all']")).Click();
        _wait.Until(d => d.FindElements(By.CssSelector("button[aria-label*='Remove']")).Count == 0);

        Assert.Empty(_driver.FindElements(By.CssSelector("button[aria-label*='Remove']")));
    }

    [Fact]
    public void BrowsePage_ClearFiltersButtonIsAvailableForEmptyResults()
    {
        OpenBrowsePage();
        var search = _driver.FindElement(By.CssSelector("input[placeholder*='Search by item name']"));
        search.SendKeys("qa-clear-results-139d");
        _driver.FindElement(By.XPath("//button[normalize-space()='Search']")).Click();
        _wait.Until(d => d.PageSource.Contains("No matching items", StringComparison.OrdinalIgnoreCase));

        _driver.FindElement(By.XPath("//button[normalize-space()='Clear Filters']")).Click();
        _wait.Until(d => !d.PageSource.Contains("No matching items", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BrowsePage_ResultPresentationDoesNotContainPrivateFieldLabel()
    {
        OpenBrowsePage();
        _wait.Until(d => !d.PageSource.Contains("Loading…", StringComparison.Ordinal));

        Assert.DoesNotContain("HiddenInformation", _driver.PageSource, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BrowsePage_SearchWalletShowsKnownFoundWalletReport()
    {
        OpenBrowsePage();
        Search("wallet");

        _wait.Until(d => d.PageSource.Contains("Found wallet", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Found wallet", _driver.PageSource, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BrowsePage_SearchLibraryMatchesKnownDescriptionOrLocation()
    {
        OpenBrowsePage();
        Search("library");

        _wait.Until(d => d.PageSource.Contains("Found wallet", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Found wallet", _driver.PageSource, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BrowsePage_AccessoriesFilterShowsKnownWalletReport()
    {
        OpenBrowsePage();
        SetCategory("Accessories");
        _driver.FindElement(By.XPath("//button[normalize-space()='Apply Filters']")).Click();

        _wait.Until(d => d.PageSource.Contains("Found wallet", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Accessories", _driver.PageSource, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BrowsePage_SameDayDateRangeShowsSeptemberTwelfthWalletReport()
    {
        OpenBrowsePage();
        SetDate(0, "2026-09-12");
        SetDate(1, "2026-09-12");
        _driver.FindElement(By.XPath("//button[normalize-space()='Apply Filters']")).Click();

        _wait.Until(d => d.PageSource.Contains("Found wallet", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Sep 12, 2026", _driver.PageSource, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BrowsePage_AccessoriesAndSameDayFiltersShowOnlyTheirIntersection()
    {
        OpenBrowsePage();
        SetCategory("Accessories");
        SetDate(0, "2026-09-12");
        SetDate(1, "2026-09-12");
        _driver.FindElement(By.XPath("//button[normalize-space()='Apply Filters']")).Click();

        _wait.Until(d => d.PageSource.Contains("Found wallet", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Found wallet", _driver.PageSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Accessories", _driver.PageSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sep 12, 2026", _driver.PageSource, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BrowsePage_ApiResponseDoesNotContainPrivateInformationField()
    {
        OpenBrowsePage();
        var response = (string)((IJavaScriptExecutor)_driver).ExecuteAsyncScript("""
            const done = arguments[arguments.length - 1];
            fetch('http://localhost:5001/api/items?page=1&pageSize=12', { credentials: 'include' })
              .then(r => r.text()).then(done).catch(e => done(String(e)));
            """)!;

        Assert.DoesNotContain("hiddenInformation", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hidden_information", response, StringComparison.OrdinalIgnoreCase);
    }

    private void OpenBrowsePage()
    {
        _driver.Navigate().GoToUrl(ReportLostItemFixture.BaseUrl);
        _wait.Until(d => d.FindElements(By.CssSelector("input[placeholder*='Search by item name']")).Count == 1);
    }

    private void Search(string value)
    {
        var input = _driver.FindElement(By.CssSelector("input[placeholder*='Search by item name']"));
        input.Clear();
        input.SendKeys(value);
        _driver.FindElement(By.XPath("//button[normalize-space()='Search']")).Click();
    }

    private void SetDate(int index, string value)
    {
        var input = _driver.FindElements(By.CssSelector("input[type='date']"))[index];
        ((IJavaScriptExecutor)_driver).ExecuteScript(
            "const setValue = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;" +
            "setValue.call(arguments[0], arguments[1]);" +
            "arguments[0].dispatchEvent(new Event('input', { bubbles: true }));" +
            "arguments[0].dispatchEvent(new Event('change', { bubbles: true }));", input, value);
    }

    private void SetCategory(string value)
    {
        var select = _driver.FindElement(By.Id("category-filter"));
        ((IJavaScriptExecutor)_driver).ExecuteScript(
            "const setValue = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set;" +
            "setValue.call(arguments[0], arguments[1]);" +
            "arguments[0].dispatchEvent(new Event('change', { bubbles: true }));", select, value);
    }
}
