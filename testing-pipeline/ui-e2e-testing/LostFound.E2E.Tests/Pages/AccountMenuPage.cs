using LostFound.E2E.Tests.Support;
using OpenQA.Selenium;

namespace LostFound.E2E.Tests.Pages;

/// <summary>The account dropdown in the app header (AccountMenu.tsx): avatar button, then "Sign out".</summary>
public sealed class AccountMenuPage
{
    // The toggle button holds the round initials badge (bg-indigo-100); it has no id or label.
    private static readonly By Toggle = By.XPath("//button[.//span[contains(@class,'bg-indigo-100')]]");
    private static readonly By SignOutButton = By.XPath("//button[normalize-space()='Sign out']");

    private readonly IWebDriver _driver;

    public AccountMenuPage(IWebDriver driver) => _driver = driver;

    /// <summary>Opens the menu, clicks Sign out and waits until the app is back on /login.</summary>
    public void SignOut()
    {
        _driver.WaitVisible(Toggle).Click();
        _driver.WaitVisible(SignOutButton).Click();
        _driver.WaitForPath("/login");
    }
}
