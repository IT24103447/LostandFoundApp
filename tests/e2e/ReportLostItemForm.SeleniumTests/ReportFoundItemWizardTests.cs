using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using WebDriverManager;
using WebDriverManager.DriverConfigs.Impl;
using WebDriverManager.Helpers;
using Xunit;

namespace ReportLostItemForm.SeleniumTests;

public sealed class ReportFoundItemWizardTests : IDisposable
{
    private static string? _authToken;
    private readonly IWebDriver _driver;
    private readonly WebDriverWait _wait;
    private const string BaseUrl = BrowserFixture.BaseUrl;

    public ReportFoundItemWizardTests()
    {
        new DriverManager().SetUpDriver(new ChromeConfig(), VersionResolveStrategy.MatchingBrowser);
        var options = new ChromeOptions();
        options.AddArgument("--window-size=1280,900");
        options.AddArgument("--disable-features=PasswordLeakDetection");
        options.AddUserProfilePreference("credentials_enable_service", false);
        options.AddUserProfilePreference("profile.password_manager_enabled", false);
        options.AddUserProfilePreference("profile.password_manager_leak_detection", false);
        _driver = new ChromeDriver(options);
        _wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(15));
    }

    public void Dispose()
    {
        _driver.Quit();
        _driver.Dispose();
    }

    [Fact]
    public void CompletesFoundWizard_HappyPath_ReachesSuccessPage()
    {
        SignInAndOpenFoundWizard();
        FillStep1();
        ClickContinue();
        FillStep2();
        ClickContinue();
        _wait.Until(d => d.FindElement(By.Id("hiddenInformation"))).SendKeys("Scratch beside the clasp");

        _driver.FindElement(By.XPath("//button[contains(text(),'Report Found Item')]")).Click();
        _wait.Until(d => d.Url.Contains("/report-found-item/success", StringComparison.Ordinal));

        Assert.Contains("Found Item Reported Successfully", _driver.PageSource);
        Assert.Contains("ACTIVE", _driver.PageSource);
    }

    [Fact]
    public void FoundSuccessPage_NeverRendersHiddenInformation()
    {
        SignInAndOpenFoundWizard();
        FillStep1();
        ClickContinue();
        FillStep2();
        ClickContinue();
        const string secret = "found-secret-not-public";
        _wait.Until(d => d.FindElement(By.Id("hiddenInformation"))).SendKeys(secret);

        _driver.FindElement(By.XPath("//button[contains(text(),'Report Found Item')]")).Click();
        _wait.Until(d => d.Url.Contains("/report-found-item/success", StringComparison.Ordinal));

        Assert.DoesNotContain(secret, _driver.PageSource, StringComparison.Ordinal);
        Assert.Contains("Hidden", _driver.PageSource, StringComparison.Ordinal);
    }

    [Fact]
    public void FoundWizard_BlocksContinue_WhenDescriptionIsEmpty()
    {
        SignInAndOpenFoundWizard();
        _driver.FindElement(By.Id("title")).SendKeys("Found item");
        SelectCategory("Accessories");
        ClickContinue();

        var error = _wait.Until(d => d.FindElement(
            By.XPath("//label[@for='description']/ancestor::div[1]/following-sibling::p")));
        Assert.Equal("Description is required.", error.Text);
    }

    [Fact]
    public void FoundWizard_BlocksContinue_WhenTitleIsEmpty()
    {
        SignInAndOpenFoundWizard();
        SelectCategory("Accessories");
        _driver.FindElement(By.Id("description")).SendKeys("A valid description.");
        ClickContinue();

        AssertFieldError("title", "Item title is required.");
    }

    [Fact]
    public void FoundWizard_BlocksContinue_WhenCategoryIsEmpty()
    {
        SignInAndOpenFoundWizard();
        _driver.FindElement(By.Id("title")).SendKeys("Found item");
        _driver.FindElement(By.Id("description")).SendKeys("A valid description.");
        ClickContinue();

        _wait.Until(d => d.PageSource.Contains("Please select a category.", StringComparison.Ordinal));
        Assert.Contains("report-found-item", _driver.Url, StringComparison.Ordinal);
    }

    [Fact]
    public void FoundWizard_BlocksContinue_WhenLocationFoundIsEmpty()
    {
        SignInAndOpenFoundWizard();
        FillStep1();
        ClickContinue();
        SetDate(DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd"));
        ClickContinue();

        AssertFieldError("locationFound", "Location found is required.");
    }

    [Fact]
    public void FoundWizard_BlocksSubmission_WhenHiddenInformationIsEmpty()
    {
        SignInAndOpenFoundWizard();
        GoToVerificationStep();

        SubmitFoundItem();

        AssertFieldError("hiddenInformation", "Hidden information is required.");
        Assert.DoesNotContain("/success", _driver.Url, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("title", "   ", "Item title is required.")]
    [InlineData("description", "   ", "Description is required.")]
    public void FoundWizard_BlocksWhitespaceOnlyStepOneFields(string fieldId, string value, string expectedError)
    {
        SignInAndOpenFoundWizard();
        if (fieldId != "title") _driver.FindElement(By.Id("title")).SendKeys("Found item");
        SelectCategory("Accessories");
        if (fieldId != "description") _driver.FindElement(By.Id("description")).SendKeys("Valid description");
        _driver.FindElement(By.Id(fieldId)).SendKeys(value);
        ClickContinue();

        AssertFieldError(fieldId, expectedError);
    }

    [Theory]
    [InlineData("locationFound", "Location found is required.")]
    [InlineData("hiddenInformation", "Hidden information is required.")]
    public void FoundWizard_BlocksWhitespaceOnlyLaterFields(string fieldId, string expectedError)
    {
        SignInAndOpenFoundWizard();
        GoToVerificationStep();

        if (fieldId == "locationFound")
        {
            // Return to step 2, replace the value with whitespace, and validate that step.
            _driver.FindElement(By.XPath("//button[contains(text(),'Back')]")).Click();
            var location = _wait.Until(d => d.FindElement(By.Id("locationFound")));
            location.Clear();
            location.SendKeys("   ");
            ClickContinue();
        }
        else
        {
            _driver.FindElement(By.Id("hiddenInformation")).SendKeys("   ");
            SubmitFoundItem();
        }

        AssertFieldError(fieldId, expectedError);
    }

    [Theory]
    [InlineData("title", 150, "Title must be at most 150 characters.")]
    [InlineData("description", 2000, "Description must be at most 2000 characters.")]
    public void FoundWizard_EnforcesStepOneMaximumLengths(string fieldId, int maximum, string expectedError)
    {
        SignInAndOpenFoundWizard();
        _driver.FindElement(By.Id("title")).SendKeys(fieldId == "title" ? new string('a', maximum) : "Found item");
        SelectCategory("Accessories");
        _driver.FindElement(By.Id("description")).SendKeys(fieldId == "description" ? new string('a', maximum) : "Valid description");
        ClickContinue();
        _wait.Until(d => d.FindElement(By.Id("dateFound"))); // Exact maximum is accepted.

        _driver.FindElement(By.XPath("//button[contains(text(),'Back')]")).Click();
        _wait.Until(d => d.FindElement(By.Id(fieldId))).SendKeys("x");
        ClickContinue();
        AssertFieldError(fieldId, expectedError);
    }

    [Theory]
    [InlineData("locationFound", 255, "Location found must be at most 255 characters.")]
    [InlineData("hiddenInformation", 500, "Hidden information must be at most 500 characters.")]
    public void FoundWizard_EnforcesLaterMaximumLengths(string fieldId, int maximum, string expectedError)
    {
        SignInAndOpenFoundWizard();
        FillStep1();
        ClickContinue();
        SetDate(DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd"));
        _driver.FindElement(By.Id("locationFound")).SendKeys(fieldId == "locationFound" ? new string('a', maximum) : "Library");
        ClickContinue();

        if (fieldId == "hiddenInformation")
        {
            _driver.FindElement(By.Id("hiddenInformation")).SendKeys(new string('a', maximum));
            SubmitFoundItem();
            _wait.Until(d => d.Url.Contains("/success", StringComparison.Ordinal)); // Exact maximum is accepted.
            return;
        }

        _driver.FindElement(By.XPath("//button[contains(text(),'Back')]")).Click();
        _wait.Until(d => d.FindElement(By.Id("locationFound"))).SendKeys("x");
        ClickContinue();
        AssertFieldError("locationFound", expectedError);
    }

    [Fact]
    public void FoundWizard_RejectsHiddenInformationOverMaximumLength()
    {
        SignInAndOpenFoundWizard();
        GoToVerificationStep();
        _driver.FindElement(By.Id("hiddenInformation")).SendKeys(new string('a', 501));

        SubmitFoundItem();

        AssertFieldError("hiddenInformation", "Hidden information must be at most 500 characters.");
        Assert.DoesNotContain("/success", _driver.Url, StringComparison.Ordinal);
    }

    [Fact]
    public void FoundWizard_CapsAtOnePhoto()
    {
        SignInAndOpenFoundWizard();
        GoToVerificationStep();
        var files = Enumerable.Range(1, 2)
            .Select(i => CreateTempFile($"found-{i}.jpg", "jpeg placeholder"));

        _driver.FindElement(By.CssSelector("input[type=file]")).SendKeys(string.Join("\n", files));

        _wait.Until(d => d.FindElements(By.CssSelector(".overflow-hidden img")).Count == 1);
        Assert.Contains("1 added", _driver.PageSource, StringComparison.Ordinal);
        Assert.Empty(_driver.FindElements(By.CssSelector("input[type=file]")));
    }

    [Fact]
    public void FoundWizard_RejectsNonImageFileWithoutAddingPreview()
    {
        SignInAndOpenFoundWizard();
        GoToVerificationStep();

        _driver.FindElement(By.CssSelector("input[type=file]"))
            .SendKeys(CreateTempFile("not-an-image.pdf", "not a PDF image"));

        Assert.Empty(_driver.FindElements(By.CssSelector(".overflow-hidden img")));
        Assert.Contains("0 added", _driver.PageSource, StringComparison.Ordinal);
    }

    [Fact]
    public void FoundWizard_RejectsEmptyImageAtServer()
    {
        SignInAndOpenFoundWizard();
        GoToVerificationStep();
        _driver.FindElement(By.Id("hiddenInformation")).SendKeys("Private verification detail");
        _driver.FindElement(By.CssSelector("input[type=file]"))
            .SendKeys(CreateTempFile("empty.jpg", string.Empty));

        SubmitFoundItem();

        _wait.Until(d => d.PageSource.Contains("The photo file is empty.", StringComparison.Ordinal));
        Assert.DoesNotContain("/success", _driver.Url, StringComparison.Ordinal);
    }

    [Fact]
    public void FoundWizard_RejectsPhotoOverFiveMegabytes()
    {
        SignInAndOpenFoundWizard();
        GoToVerificationStep();
        _driver.FindElement(By.Id("hiddenInformation")).SendKeys("Private verification detail");
        _driver.FindElement(By.CssSelector("input[type=file]"))
            .SendKeys(CreateTempBinaryFile("oversized.jpg", 5 * 1024 * 1024 + 1));

        SubmitFoundItem();

        _wait.Until(d => d.PageSource.Contains("Each photo must be at most 5 MB.", StringComparison.Ordinal));
        Assert.DoesNotContain("/success", _driver.Url, StringComparison.Ordinal);
    }

    [Fact]
    public void FoundWizard_MapsServerInvalidImageErrorWithoutShowingSuccess()
    {
        SignInAndOpenFoundWizard();
        GoToVerificationStep();
        _driver.FindElement(By.Id("hiddenInformation")).SendKeys("Private verification detail");
        _driver.FindElement(By.CssSelector("input[type=file]"))
            .SendKeys(CreateTempFile("forged.jpg", "these bytes are not a JPEG"));

        SubmitFoundItem();

        _wait.Until(d => d.PageSource.Contains(
            "The photo file does not match a supported image format.", StringComparison.Ordinal));
        Assert.Contains("report-found-item", _driver.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("/success", _driver.Url, StringComparison.Ordinal);
    }

    [Fact]
    public void FoundWizard_DateFieldUsesTodayAsMaximum()
    {
        SignInAndOpenFoundWizard();
        FillStep1();
        ClickContinue();

        var dateInput = _wait.Until(d => d.FindElement(By.CssSelector("input[aria-label='Date found']")));
        Assert.Equal(DateTime.UtcNow.ToString("yyyy-MM-dd"), dateInput.GetAttribute("max"));
    }

    [Fact]
    public void FoundWizard_AcceptsOnePhotoAndShowsPreview()
    {
        SignInAndOpenFoundWizard();
        FillStep1();
        ClickContinue();
        FillStep2();
        ClickContinue();

        var path = CreateTempFile("found.jpg", "jpeg placeholder");
        _driver.FindElement(By.CssSelector("input[type=file]")).SendKeys(path);

        var image = _wait.Until(d => d.FindElement(By.CssSelector(".overflow-hidden img")));
        Assert.True(image.Displayed);
        Assert.Contains("1 added", _driver.PageSource, StringComparison.Ordinal);
    }

    private void SignInAndOpenFoundWizard()
    {
        var email = Environment.GetEnvironmentVariable("SELENIUM_TEST_EMAIL")
            ?? throw new InvalidOperationException("Set SELENIUM_TEST_EMAIL to a verified test account.");
        var password = Environment.GetEnvironmentVariable("SELENIUM_TEST_PASSWORD")
            ?? throw new InvalidOperationException("Set SELENIUM_TEST_PASSWORD for the Selenium test account.");

        _driver.Navigate().GoToUrl(BaseUrl);
        _driver.Manage().Cookies.DeleteAllCookies();
        if (_authToken is null)
        {
            _driver.Navigate().GoToUrl($"{BaseUrl}/login");
            _wait.Until(d => d.FindElement(By.Id("email"))).SendKeys(email);
            _driver.FindElement(By.Id("password")).SendKeys(password);
            _driver.FindElement(By.CssSelector("button[type='submit']")).Click();
            _wait.Until(d => d.Url == BaseUrl + "/" || d.Url == BaseUrl);
            _authToken = _driver.Manage().Cookies.GetCookieNamed("auth_token")?.Value
                ?? throw new InvalidOperationException("Login succeeded but no auth_token cookie was found.");
        }
        else
        {
            _driver.Manage().Cookies.AddCookie(new Cookie(
                "auth_token", _authToken, "localhost", "/", DateTime.UtcNow.AddHours(1)));
        }

        _driver.Navigate().GoToUrl($"{BaseUrl}/report-found-item");
        _wait.Until(d => d.FindElement(By.Id("title")));
    }

    private void FillStep1()
    {
        _driver.FindElement(By.Id("title")).SendKeys("Found wallet");
        SelectCategory("Accessories");
        _driver.FindElement(By.Id("description")).SendKeys("Black wallet found near the library.");
    }

    private void FillStep2()
    {
        SetDate(DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd"));
        _driver.FindElement(By.Id("locationFound")).SendKeys("Main library entrance");
    }

    private void GoToVerificationStep()
    {
        FillStep1();
        ClickContinue();
        FillStep2();
        ClickContinue();
        _wait.Until(d => d.FindElement(By.Id("hiddenInformation")));
    }

    private void SelectCategory(string category)
    {
        _driver.FindElement(By.Id("category")).Click();
        _wait.Until(d => d.FindElement(By.XPath($"//button[normalize-space()='{category}']"))).Click();
        _wait.Until(d => d.FindElement(By.Id("category")).Text.Contains(category, StringComparison.Ordinal));
    }

    private void ClickContinue()
    {
        _wait.Until(d =>
        {
            try
            {
                var button = d.FindElement(By.XPath("//button[contains(text(),'Continue')]"));
                if (!button.Displayed || !button.Enabled)
                    return false;

                button.Click();
                return true;
            }
            catch (StaleElementReferenceException)
            {
                // React can replace the button after a field-validation rerender.
                // Re-find it on the next wait attempt rather than failing the test.
                return false;
            }
        });
    }

    private void SubmitFoundItem() =>
        _driver.FindElement(By.XPath("//button[contains(text(),'Report Found Item')]")).Click();

    private void AssertFieldError(string fieldId, string expectedError)
    {
        var error = _wait.Until(d => d.FindElement(
            By.XPath($"//label[@for='{fieldId}']/ancestor::div[1]/following-sibling::p")));
        Assert.Equal(expectedError, error.Text);
    }

    private void SetDate(string date)
    {
        var dateInput = _wait.Until(d => d.FindElement(By.CssSelector("input[aria-label='Date found']")));
        ((IJavaScriptExecutor)_driver).ExecuteScript(
            "const input = arguments[0];" +
            "const valueSetter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;" +
            "valueSetter.call(input, arguments[1]);" +
            "input.dispatchEvent(new Event('input', { bubbles: true }));" +
            "input.dispatchEvent(new Event('change', { bubbles: true }));",
            dateInput,
            date);
        _wait.Until(d => d.FindElement(By.CssSelector("input[aria-label='Date found']"))
            .GetAttribute("value") == date);
    }

    private static string CreateTempFile(string name, string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-{name}");
        File.WriteAllText(path, contents);
        return path;
    }

    private static string CreateTempBinaryFile(string name, int byteCount)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-{name}");
        File.WriteAllBytes(path, new byte[byteCount]);
        return path;
    }
}
