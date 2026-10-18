using LostFound.E2E.Tests.Support;
using OpenQA.Selenium;

namespace LostFound.E2E.Tests.Pages;

/// <summary>Page object for /admin/users (UserManagementSection.tsx): list, search, kick and unkick.</summary>
public sealed class AdminUsersPage
{
    private static readonly By SearchBox = By.XPath("//input[starts-with(@placeholder,'Search by name or email')]");
    private static readonly By DataRows = By.XPath("//tbody/tr[count(td)>1]");
    private static readonly By NoUsersFound = By.XPath("//td[normalize-space()='No users found.']");
    private static readonly By DialogCancel = By.XPath("//button[normalize-space()='Cancel']");

    private readonly IWebDriver _driver;

    public AdminUsersPage(IWebDriver driver) => _driver = driver;

    private static By Row(string email) => By.XPath($"//tr[td[normalize-space()='{email}']]");
    private static By RowButton(string email, string label) =>
        By.XPath($"//tr[td[normalize-space()='{email}']]//button[normalize-space()='{label}']");
    private static By RowStatus(string email, string status) =>
        By.XPath($"//tr[td[normalize-space()='{email}']]//span[normalize-space()='{status}']");

    // ---- list and search ----

    /// <summary>Waits until at least one account row is shown (the table is no longer loading or empty).</summary>
    public int WaitForRows()
    {
        _driver.Wait().Until(d => d.FindElements(DataRows).Count > 0);
        return _driver.FindElements(DataRows).Count;
    }

    /// <summary>Types in the search box without waiting for a particular result.</summary>
    public AdminUsersPage Search(string text)
    {
        var box = _driver.WaitVisible(SearchBox);
        box.Clear();
        box.SendKeys(text);
        return this;
    }

    /// <summary>Filters the list down to one account by typing its e-mail in the search box.</summary>
    public AdminUsersPage Find(string email)
    {
        Search(email);
        _driver.WaitVisible(Row(email));
        return this;
    }

    /// <summary>Waits until the list has narrowed to exactly one row: the given account.</summary>
    public void WaitForOnlyRow(string email) =>
        _driver.Wait().Until(d => d.FindElements(DataRows).Count == 1 && d.FindElements(Row(email)).Count == 1);

    public void WaitForNoUsers() => _driver.WaitVisible(NoUsersFound);

    /// <summary>The display name shown in the first column of an account's row.</summary>
    public string ReadName(string email) =>
        _driver.WaitVisible(By.XPath($"//tr[td[normalize-space()='{email}']]/td[1]")).Text.Trim();

    // ---- kick / unkick ----

    public AdminUsersPage Kick(string email) => Confirm(email, "Kick");
    public AdminUsersPage Unkick(string email) => Confirm(email, "Unkick");

    /// <summary>Opens the kick confirmation dialog without confirming it.</summary>
    public AdminUsersPage OpenKickDialog(string email)
    {
        _driver.WaitVisible(RowButton(email, "Kick")).Click();
        _driver.WaitVisible(By.XPath("//h3[normalize-space()='Kick User']"));
        return this;
    }

    public string ReadDialogMessage() =>
        _driver.WaitVisible(By.XPath("//h3[normalize-space()='Kick User']/following-sibling::p[1]")).Text.Trim();

    public void CancelDialog()
    {
        _driver.WaitVisible(DialogCancel).Click();
        _driver.Wait().Until(d => d.FindElements(By.XPath("//h3[normalize-space()='Kick User']")).Count == 0);
    }

    public void WaitForStatus(string email, string status) => _driver.WaitVisible(RowStatus(email, status));

    public bool IsKicked(string email) => _driver.FindElements(RowStatus(email, "Kicked")).Count > 0;

    // Clicks the row button, then the confirm button in the dialog. With the list filtered to one
    // user, the dialog's button is the last one on the page with that label.
    private AdminUsersPage Confirm(string email, string label)
    {
        _driver.WaitVisible(RowButton(email, label)).Click();
        _driver.WaitVisible(By.XPath($"(//button[normalize-space()='{label}'])[last()]")).Click();
        return this;
    }
}
