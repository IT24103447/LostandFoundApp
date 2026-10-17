using LostFound.E2E.Tests.Support;
using OpenQA.Selenium;

namespace LostFound.E2E.Tests.Pages;

/// <summary>Page object for "/" (the browse / search page, HomePage.tsx).</summary>
public sealed class HomePage
{
    private static readonly By Heading = By.XPath("//h1[normalize-space()='Find an Item']");
    private static readonly By SearchBox = By.CssSelector("input[placeholder^='Search by item name']");
    private static readonly By CategoryFilter = By.Id("category-filter");
    private static readonly By ApplyFilters = By.XPath("//button[normalize-space()='Apply Filters']");
    private static readonly By NoResults = By.XPath("//h3[normalize-space()='No matching items']");
    private static readonly By FilterChipRemove = By.XPath("//button[starts-with(@aria-label,'Remove')]");

    private readonly IWebDriver _driver;

    public HomePage(IWebDriver driver) => _driver = driver;

    private static By ResultCard(string title) =>
        By.XPath($"//a[starts-with(@href,'/items/')]//h3[normalize-space()='{title}']");

    public HomePage WaitUntilLoaded()
    {
        _driver.WaitForPath("/");
        _driver.WaitVisible(Heading);
        return this;
    }

    public HomePage Search(string term)
    {
        var box = _driver.WaitVisible(SearchBox);
        box.Clear();
        box.SendKeys(term);
        box.SendKeys(Keys.Enter);
        return this;
    }

    /// <summary>Clicks the Lost / Found / All toggle (applies immediately).</summary>
    public HomePage ChooseType(string label)
    {
        _driver.WaitVisible(By.XPath($"//button[normalize-space()='{label}']")).Click();
        return this;
    }

    /// <summary>Picks a category in the sidebar and presses Apply Filters.</summary>
    public HomePage ApplyCategory(string categoryLabel)
    {
        var select = _driver.WaitVisible(CategoryFilter);
        // React controls this <select>, so set it through the native setter and fire a change event.
        ((IJavaScriptExecutor)_driver).ExecuteScript(
            "const el = arguments[0];" +
            "Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set.call(el, arguments[1]);" +
            "el.dispatchEvent(new Event('change', { bubbles: true }));",
            select, categoryLabel);
        _driver.WaitVisible(ApplyFilters).Click();
        return this;
    }

    public HomePage RemoveFirstFilterChip()
    {
        _driver.WaitVisible(FilterChipRemove).Click();
        return this;
    }

    public void WaitForResult(string title) => _driver.WaitVisible(ResultCard(title));

    public void WaitForNoResults() => _driver.WaitVisible(NoResults);

    public void OpenResult(string title) => _driver.WaitVisible(ResultCard(title)).Click();
}
