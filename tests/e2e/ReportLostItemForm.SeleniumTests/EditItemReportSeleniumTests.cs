using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using Xunit;

namespace ReportLostItemForm.SeleniumTests;

/// <summary>
/// Story 3 browser checks. Configure SELENIUM_EDIT_REPORT_ID and optionally
/// SELENIUM_EDIT_REPORT_TYPE (lost or found) for an active report owned by the
/// configured Selenium user. The suite opens the report's edit URL directly,
/// so it does not depend on the My Reports listing API.
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
    public void EditReport_OwnerCanOpenEditPageByReportId()
    {
        OpenConfiguredReport();

        Assert.Equal(EditReportUrl, _driver.Url);
        Assert.NotEmpty(_driver.FindElement(By.Id("title")).GetAttribute("value"));
    }

    [Fact]
    public void EditReport_HiddenInformationIsNeverPrePopulated()
    {
        OpenConfiguredReport();

        Assert.Equal(string.Empty, _driver.FindElement(By.Id("hiddenInformation")).GetAttribute("value"));
        Assert.Contains("isn't pre-filled", _driver.PageSource, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EditReport_BlankHiddenInformationShowsClientValidationAndDoesNotSave()
    {
        OpenConfiguredReport();
        var privateField = _driver.FindElement(By.Id("hiddenInformation"));
        privateField.Clear();

        Save();

        var error = ValidationErrorFor("hiddenInformation");
        Assert.Contains("required", error.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Changes saved.", _driver.PageSource, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EditReport_BlankTitleShowsClientValidationAndDoesNotSave()
    {
        OpenConfiguredReport();
        _driver.FindElement(By.Id("title")).Clear();

        Save();

        Assert.Contains("required", ValidationErrorFor("title").Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Changes saved.", _driver.PageSource, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EditReport_DescriptionOverTwoThousandCharactersShowsClientValidation()
    {
        OpenConfiguredReport();
        SetText("description", new string('d', 2001));

        Save();

        Assert.Contains("2000", ValidationErrorFor("description").Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Changes saved.", _driver.PageSource, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EditReport_HiddenInformationOverFiveHundredCharactersShowsClientValidation()
    {
        OpenConfiguredReport();
        SetText("hiddenInformation", new string('h', 501));

        Save();

        Assert.Contains("500", ValidationErrorFor("hiddenInformation").Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Changes saved.", _driver.PageSource, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EditReport_OwnerCanSaveAndRefreshAPublicChange_WithoutPrivateDataBeingPrePopulated()
    {
        var hiddenInformation = GetRequiredSetting("SELENIUM_EDIT_HIDDEN_INFORMATION");
        OpenConfiguredReport();
        var originalTitle = _driver.FindElement(By.Id("title")).GetAttribute("value");
        var changedTitle = $"Selenium edit {Guid.NewGuid():N}";

        try
        {
            SetText("title", changedTitle);
            SetText("hiddenInformation", hiddenInformation);
            Save();
            WaitForSaveConfirmation();
            Assert.Equal(changedTitle, _driver.FindElement(By.Id("title")).GetAttribute("value"));

            OpenConfiguredReport();
            Assert.Equal(changedTitle, _driver.FindElement(By.Id("title")).GetAttribute("value"));
            Assert.Equal(string.Empty, _driver.FindElement(By.Id("hiddenInformation")).GetAttribute("value"));
        }
        finally
        {
            // Restore the designated test report, including its required private value.
            OpenConfiguredReport();
            SetText("title", originalTitle);
            SetText("hiddenInformation", hiddenInformation);
            Save();
            WaitForSaveConfirmation();
        }
    }

    [Fact]
    public void EditLostOrFoundReport_DateFieldDisallowsFutureDates()
    {
        OpenConfiguredReport();
        var dateField = _driver.FindElements(By.Id("dateLost")).FirstOrDefault()
            ?? _driver.FindElement(By.Id("dateFound"));

        Assert.Equal(DateTime.UtcNow.ToString("yyyy-MM-dd"), dateField.GetAttribute("max"));
    }

    [Fact]
    public void EditReport_DescriptionCounterUsesTwoThousandCharacterLimit()
    {
        OpenConfiguredReport();

        Assert.Contains("/ 2000", _driver.PageSource, StringComparison.Ordinal);
    }

    [Fact]
    public void EditReport_PhotoControlsAreAvailableToTheOwner()
    {
        OpenConfiguredReport();

        Assert.Contains("One photo per report", _driver.PageSource, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(_driver.FindElements(By.CssSelector("input[type='file']")));
    }

    [Fact]
    public void EditReport_BackReturnsToMyReportsWithoutSubmitting()
    {
        OpenConfiguredReport();

        _driver.FindElement(By.XPath("//button[normalize-space()='Back']")).Click();
        _wait.Until(d => d.Url.EndsWith("/my-reports", StringComparison.OrdinalIgnoreCase));

        Assert.EndsWith("/my-reports", _driver.Url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EditReport_PrivateFieldCarriesPrivateLabel()
    {
        OpenConfiguredReport();

        Assert.Contains("PRIVATE", _driver.PageSource, StringComparison.Ordinal);
        Assert.Contains("Used only for secure matching", _driver.PageSource, StringComparison.OrdinalIgnoreCase);
    }

    private static string EditReportType =>
        (Environment.GetEnvironmentVariable("SELENIUM_EDIT_REPORT_TYPE") ?? "lost")
            .Trim()
            .ToLowerInvariant();

    private static string EditReportId =>
        Environment.GetEnvironmentVariable("SELENIUM_EDIT_REPORT_ID")?.Trim()
        ?? throw new InvalidOperationException(
            "Set SELENIUM_EDIT_REPORT_ID to an active report owned by SELENIUM_TEST_EMAIL before running Story 3 Selenium tests.");

    private static string EditReportUrl
    {
        get
        {
            if (EditReportType is not ("lost" or "found"))
            {
                throw new InvalidOperationException(
                    "SELENIUM_EDIT_REPORT_TYPE must be either 'lost' or 'found'.");
            }

            return $"{ReportLostItemFixture.BaseUrl}/edit-{EditReportType}-item/{EditReportId}";
        }
    }

    private void OpenConfiguredReport()
    {
        _driver.Navigate().GoToUrl(EditReportUrl);
        _wait.Until(d => d.FindElement(By.Id("title")));
    }

    private void Save() => _driver.FindElement(By.XPath("//button[normalize-space()='Save Changes']")).Click();

    private IWebElement ValidationErrorFor(string fieldId) =>
        _wait.Until(d => d.FindElement(By.Id($"{fieldId}-error")));

    private void WaitForSaveConfirmation() =>
        _wait.Until(d => d.FindElement(By.XPath("//div[contains(normalize-space(), 'Changes saved.')]")).Displayed);

    private void SetText(string fieldId, string value)
    {
        var field = _driver.FindElement(By.Id(fieldId));
        field.Clear();
        field.SendKeys(value);
    }

    private static string GetRequiredSetting(string name) =>
        Environment.GetEnvironmentVariable(name)?.Trim()
        ?? throw new InvalidOperationException($"Set the {name} environment variable before running this Selenium test.");
}
