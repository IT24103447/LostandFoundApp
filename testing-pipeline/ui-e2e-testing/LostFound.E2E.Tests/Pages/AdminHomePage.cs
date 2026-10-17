using LostFound.E2E.Tests.Support;
using OpenQA.Selenium;

namespace LostFound.E2E.Tests.Pages;

/// <summary>
/// The admin landing screen. The app routes /admin/dashboard straight on to /admin/users
/// (AdminDashboardPage.tsx is only a redirect), so User Management is where an admin ends up.
/// The "Admin Dashboard" heading comes from AdminLayout and is shown on every admin page.
/// </summary>
public sealed class AdminHomePage
{
    private static readonly By LayoutHeading = By.XPath("//h2[normalize-space()='Admin Dashboard']");

    private readonly IWebDriver _driver;

    public AdminHomePage(IWebDriver driver) => _driver = driver;

    public AdminHomePage WaitUntilLoaded()
    {
        _driver.WaitForPath("/admin/users");
        _driver.WaitVisible(LayoutHeading);
        return this;
    }
}
