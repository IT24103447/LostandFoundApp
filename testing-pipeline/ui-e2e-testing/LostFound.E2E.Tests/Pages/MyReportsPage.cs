using LostFound.E2E.Tests.Support;
using OpenQA.Selenium;

namespace LostFound.E2E.Tests.Pages;

/// <summary>Page object for /my-reports (MyReportsPage.tsx).</summary>
public sealed class MyReportsPage
{
    private readonly IWebDriver _driver;

    public MyReportsPage(IWebDriver driver) => _driver = driver;

    public MyReportsPage Open()
    {
        _driver.Navigate().GoToUrl(TestConfig.BaseUrl + "/my-reports");
        _driver.WaitVisible(By.XPath("//h1[normalize-space()='My Reports']"));
        return this;
    }

    public void WaitForReport(string title) =>
        _driver.WaitVisible(By.XPath($"//p[normalize-space()='{title}']"));
}
