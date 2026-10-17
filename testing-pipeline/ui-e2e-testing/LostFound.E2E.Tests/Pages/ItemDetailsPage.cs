using LostFound.E2E.Tests.Support;
using OpenQA.Selenium;

namespace LostFound.E2E.Tests.Pages;

/// <summary>Page object for /items/:id (ItemDetailsPage.tsx).</summary>
public sealed class ItemDetailsPage
{
    private readonly IWebDriver _driver;

    public ItemDetailsPage(IWebDriver driver) => _driver = driver;

    private static By TitleHeading(string title) => By.XPath($"//h1[normalize-space()='{title}']");

    /// <summary>Waits until the browser is on an /items/ page that shows the given title.</summary>
    public ItemDetailsPage WaitUntilLoaded(string title)
    {
        _driver.Wait().Until(d => new Uri(d.Url).AbsolutePath.StartsWith("/items/", StringComparison.Ordinal));
        _driver.WaitVisible(TitleHeading(title));
        return this;
    }

    public void WaitForDescription(string text) =>
        _driver.WaitVisible(By.XPath($"//p[normalize-space()='{text}']"));

    public void WaitForLocation(string location) =>
        _driver.WaitVisible(By.XPath($"//span[contains(.,'Location:') and contains(.,'{location}')]"));

    /// <summary>The full DOM, used to prove that private text is never rendered.</summary>
    public string PageSource => _driver.PageSource;
}
