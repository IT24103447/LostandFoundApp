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
        SetDate(DateTime.UtcNow.ToString("yyyy-MM-dd"));
        _driver.FindElement(By.Id("locationFound")).SendKeys("Main library entrance");
    }

    private void SelectCategory(string category)
    {
        _driver.FindElement(By.Id("category")).Click();
        _wait.Until(d => d.FindElement(By.XPath($"//button[normalize-space()='{category}']"))).Click();
        _wait.Until(d => d.FindElement(By.Id("category")).Text.Contains(category, StringComparison.Ordinal));
    }

    private void ClickContinue() =>
        _driver.FindElement(By.XPath("//button[contains(text(),'Continue')]")).Click();

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
}
