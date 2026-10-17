using LostFound.E2E.Tests.Support;
using OpenQA.Selenium;

namespace LostFound.E2E.Tests.Pages;

/// <summary>Page object for /my-reports (MyReportsPage.tsx): the report cards and the delete dialog.</summary>
public sealed class MyReportsPage
{
    private static readonly By DeleteDialog = By.XPath("//div[@role='dialog']");
    private static readonly By DialogCancel = By.XPath("//div[@role='dialog']//button[normalize-space()='Cancel']");
    private static readonly By DialogConfirm = By.XPath("//div[@role='dialog']//button[normalize-space()='Yes, Delete']");

    private readonly IWebDriver _driver;

    public MyReportsPage(IWebDriver driver) => _driver = driver;

    // A report card is the rounded box that holds the title and the Edit button.
    private static string Card(string title) =>
        $"//div[contains(@class,'rounded-2xl') and .//p[normalize-space()='{title}'] and .//button[normalize-space()='Edit']]";

    private static By Title(string title) => By.XPath($"//p[normalize-space()='{title}']");

    public MyReportsPage Open()
    {
        _driver.Navigate().GoToUrl(TestConfig.BaseUrl + "/my-reports");
        _driver.WaitVisible(By.XPath("//h1[normalize-space()='My Reports']"), 60);
        return this;
    }

    private static readonly By NextPage = By.CssSelector("button[aria-label='Next page']");

    /// <summary>
    /// Waits until the report is visible, moving to the next page when needed. My Reports shows six
    /// reports per page and all new reports share a date, so a new report is not always on page 1.
    /// </summary>
    public void WaitForReport(string title) =>
        _driver.Wait(45).Until(d =>
        {
            var found = d.FindElements(Title(title));
            if (found.Count > 0 && found[0].Displayed) return true;

            var next = d.FindElements(NextPage);
            if (next.Count > 0 && next[0].Enabled) next[0].Click();
            return false;
        });

    /// <summary>Waits until no card with this title is on the page.</summary>
    public void WaitForReportGone(string title) =>
        _driver.Wait().Until(d => d.FindElements(Title(title)).Count == 0);

    public void WaitForTypeLabel(string title, string label) =>
        _driver.WaitVisible(By.XPath($"{Card(title)}//span[normalize-space()='{label}']"));

    public void ClickEdit(string title)
    {
        WaitForReport(title);
        _driver.WaitVisible(By.XPath($"{Card(title)}//button[normalize-space()='Edit']")).Click();
    }

    public MyReportsPage ClickDelete(string title)
    {
        WaitForReport(title);
        _driver.WaitVisible(By.XPath($"{Card(title)}//button[@aria-label='Delete report']")).Click();
        _driver.WaitVisible(DeleteDialog);
        return this;
    }

    public void CancelDelete()
    {
        _driver.WaitVisible(DialogCancel).Click();
        _driver.Wait().Until(d => d.FindElements(DeleteDialog).Count == 0);
    }

    public void ConfirmDelete() => _driver.WaitVisible(DialogConfirm).Click();
}
