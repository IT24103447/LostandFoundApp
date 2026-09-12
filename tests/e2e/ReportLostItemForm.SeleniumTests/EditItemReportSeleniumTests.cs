using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using Xunit;

namespace ReportLostItemForm.SeleniumTests;

/// <summary>
/// Story 3 browser checks. These require the configured Selenium user to own an
/// least one report; the tests deliberately use that owner's My Reports page
/// instead of relying on another user's private data.
/// </summary>
public class EditItemReportSeleniumTests : IClassFixture<ReportLostItemFixture>
{
    private readonly IWebDriver _driver;
    private readonly WebDriverWait _wait;

    public EditItemReportSeleniumTests(ReportLostItemFixture fixture)
    {
        _driver = fixture.Driver;
        _wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void EditReport_OwnerCanOpenEditPageFromMyReports()
    {
        OpenFirstOwnedReport();

        Assert.Contains("/edit-", _driver.Url);
        Assert.NotEmpty(_driver.FindElement(By.Id("title")).GetAttribute("value"));
    }

    [Fact]
    public void EditReport_HiddenInformationIsNeverPrePopulated()
    {
        OpenFirstOwnedReport();

        Assert.Equal(string.Empty, _driver.FindElement(By.Id("hiddenInformation")).GetAttribute("value"));
        Assert.Contains("isn't pre-filled", _driver.PageSource, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EditReport_BlankHiddenInformationShowsClientValidationAndDoesNotSave()
    {
        OpenFirstOwnedReport();
        var privateField = _driver.FindElement(By.Id("hiddenInformation"));
        privateField.Clear();

        Save();

        var error = _wait.Until(d => d.FindElement(By.Id("hiddenInformation-error")));
        Assert.Contains("required", error.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Changes saved.", _driver.PageSource, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EditLostOrFoundReport_DateFieldDisallowsFutureDates()
    {
        OpenFirstOwnedReport();
        var dateField = _driver.FindElements(By.Id("dateLost")).FirstOrDefault()
            ?? _driver.FindElement(By.Id("dateFound"));

        Assert.Equal(DateTime.UtcNow.ToString("yyyy-MM-dd"), dateField.GetAttribute("max"));
    }

    [Fact]
    public void EditReport_DescriptionCounterUsesTwoThousandCharacterLimit()
    {
        OpenFirstOwnedReport();

        Assert.Contains("/ 2000", _driver.PageSource, StringComparison.Ordinal);
    }

    [Fact]
    public void EditReport_PhotoControlsAreAvailableToTheOwner()
    {
        OpenFirstOwnedReport();

        Assert.Contains("One photo per report", _driver.PageSource, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(_driver.FindElements(By.CssSelector("input[type='file']")));
    }

    [Fact]
    public void EditReport_BackReturnsToMyReportsWithoutSubmitting()
    {
        OpenFirstOwnedReport();

        _driver.FindElement(By.XPath("//button[normalize-space()='Back']")).Click();
        _wait.Until(d => d.Url.EndsWith("/my-reports", StringComparison.OrdinalIgnoreCase));

        Assert.EndsWith("/my-reports", _driver.Url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EditReport_PrivateFieldCarriesPrivateLabel()
    {
        OpenFirstOwnedReport();

        Assert.Contains("PRIVATE", _driver.PageSource, StringComparison.Ordinal);
        Assert.Contains("Used only for secure matching", _driver.PageSource, StringComparison.OrdinalIgnoreCase);
    }

    private void OpenFirstOwnedReport()
    {
        _driver.Navigate().GoToUrl($"{ReportLostItemFixture.BaseUrl}/my-reports");
        var edit = _wait.Until(d => d.FindElements(By.XPath("//button[normalize-space()='Edit']")).FirstOrDefault()
            ?? throw new InvalidOperationException($"Seed an active report for {LoginHelper.TestEmail} before running Story 3 Selenium tests."));
        edit.Click();
        _wait.Until(d => d.FindElement(By.Id("title")));
    }

    private void Save() => _driver.FindElement(By.XPath("//button[normalize-space()='Save Changes']")).Click();
}
