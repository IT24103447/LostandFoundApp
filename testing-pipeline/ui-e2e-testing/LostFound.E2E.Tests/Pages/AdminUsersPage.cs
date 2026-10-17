using LostFound.E2E.Tests.Support;
using OpenQA.Selenium;

namespace LostFound.E2E.Tests.Pages;

/// <summary>Page object for /admin/users (UserManagementSection.tsx): search, kick and unkick.</summary>
public sealed class AdminUsersPage
{
    private static readonly By SearchBox = By.XPath("//input[starts-with(@placeholder,'Search by name or email')]");

    private readonly IWebDriver _driver;

    public AdminUsersPage(IWebDriver driver) => _driver = driver;

    private static By Row(string email) => By.XPath($"//tr[td[normalize-space()='{email}']]");
    private static By RowButton(string email, string label) =>
        By.XPath($"//tr[td[normalize-space()='{email}']]//button[normalize-space()='{label}']");
    private static By RowStatus(string email, string status) =>
        By.XPath($"//tr[td[normalize-space()='{email}']]//span[normalize-space()='{status}']");

    /// <summary>Filters the list down to one account by typing its e-mail in the search box.</summary>
    public AdminUsersPage Find(string email)
    {
        var box = _driver.WaitVisible(SearchBox);
        box.Clear();
        box.SendKeys(email);
        _driver.WaitVisible(Row(email));
        return this;
    }

    public AdminUsersPage Kick(string email) => Confirm(email, "Kick");
    public AdminUsersPage Unkick(string email) => Confirm(email, "Unkick");

    public void WaitForStatus(string email, string status) => _driver.WaitVisible(RowStatus(email, status));

    // Clicks the row button, then the confirm button in the dialog. With the list filtered to one
    // user, the dialog's button is the last one on the page with that label.
    private AdminUsersPage Confirm(string email, string label)
    {
        _driver.WaitVisible(RowButton(email, label)).Click();
        _driver.WaitVisible(By.XPath($"(//button[normalize-space()='{label}'])[last()]")).Click();
        return this;
    }
}
