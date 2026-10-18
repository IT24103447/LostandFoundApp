using LostFound.E2E.Tests.Support;
using OpenQA.Selenium;

namespace LostFound.E2E.Tests.Pages;

/// <summary>Admin sections other than User Management: Match Appeals and Spam Review.</summary>
public sealed class AdminSectionPage
{
    private readonly IWebDriver _driver;

    public AdminSectionPage(IWebDriver driver) => _driver = driver;

    public AdminSectionPage OpenMatchAppeals() => Open("/admin/match-appeals", "Match Appeals");
    public AdminSectionPage OpenSpamReview() => Open("/admin/spam-review", "Spam Review");

    private AdminSectionPage Open(string path, string heading)
    {
        _driver.Navigate().GoToUrl(TestConfig.BaseUrl + path);
        _driver.WaitForPath(path);
        _driver.WaitVisible(By.XPath($"//h1[normalize-space()='{heading}']"));
        return this;
    }

    /// <summary>True when the page is showing an error banner instead of a list or an empty state.</summary>
    public bool HasErrorBanner() =>
        _driver.FindElements(By.XPath("//div[contains(@class,'bg-red-50') and contains(@class,'text-red-700')]")).Count > 0;
}
