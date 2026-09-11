using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using WebDriverManager;
using WebDriverManager.DriverConfigs.Impl;
using WebDriverManager.Helpers;
using Xunit;

namespace ReportLostItemForm.SeleniumTests;

public sealed class BrowserFixture : IDisposable
{
    public const string BaseUrl = "http://localhost:5173";
    public IWebDriver Driver { get; }

    public BrowserFixture()
    {
        new DriverManager().SetUpDriver(new ChromeConfig(), VersionResolveStrategy.MatchingBrowser);
        var options = new ChromeOptions();
        options.AddArgument("--window-size=1280,900");
        options.AddArgument("--disable-features=PasswordLeakDetection");
        options.AddUserProfilePreference("credentials_enable_service", false);
        options.AddUserProfilePreference("profile.password_manager_enabled", false);
        options.AddUserProfilePreference("profile.password_manager_leak_detection", false);
        Driver = new ChromeDriver(options);
    }

    public void Dispose()
    {
        Driver.Quit();
        Driver.Dispose();
    }
}

public class ReportLostItemWizardTests : IDisposable
{
    private static string? _authToken;
    private readonly BrowserFixture _fixture;
    private readonly IWebDriver _driver;
    private readonly WebDriverWait _wait;
    private const string BaseUrl = BrowserFixture.BaseUrl;

    public ReportLostItemWizardTests()
    {
        _fixture = new BrowserFixture();
        _driver = _fixture.Driver;
        _wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(15));
    }

    public void Dispose() => _fixture.Dispose();

    private void SignInAndOpenWizard()
    {
        var email = Environment.GetEnvironmentVariable("SELENIUM_TEST_EMAIL")
            ?? throw new InvalidOperationException("Set SELENIUM_TEST_EMAIL to a verified test account.");
        var password = Environment.GetEnvironmentVariable("SELENIUM_TEST_PASSWORD")
            ?? throw new InvalidOperationException("Set SELENIUM_TEST_PASSWORD for the Selenium test account.");

        _driver.Manage().Cookies.DeleteAllCookies();

        if (_authToken is null)
        {
            _driver.Navigate().GoToUrl($"{BaseUrl}/login");
            _wait.Until(d => d.FindElement(By.Id("email"))).SendKeys(email);
            _driver.FindElement(By.Id("password")).SendKeys(password);
            _driver.FindElement(By.CssSelector("button[type='submit']")).Click();
            _wait.Until(d => d.Url == $"{BaseUrl}/" || d.Url == $"{BaseUrl}/");

            _authToken = _driver.Manage().Cookies.GetCookieNamed("auth_token")?.Value
                ?? throw new InvalidOperationException("Login succeeded but no auth_token cookie was found.");
        }
        else
        {
            _driver.Navigate().GoToUrl(BaseUrl);
            _driver.Manage().Cookies.AddCookie(new Cookie(
                "auth_token",
                _authToken,
                "localhost",
                "/",
                DateTime.UtcNow.AddHours(1)));
        }

        _driver.Navigate().GoToUrl($"{BaseUrl}/report-lost-item");
        _wait.Until(d => d.FindElement(By.Id("title")));
    }

    // E2E-01
    [Fact]
    public void CompletesFullWizard_HappyPath_ReachesSuccessPage()
    {
        SignInAndOpenWizard();
        _driver.FindElement(By.Id("title")).SendKeys("Black leather wallet");

        SelectCategory("Accessories");

        _driver.FindElement(By.Id("description")).SendKeys("Bifold wallet, slightly worn, lost near the food court.");
        _driver.FindElement(By.XPath("//button[contains(text(),'Continue')]")).Click();

        SetDate("2026-08-28");

        _driver.FindElement(By.Id("lastKnownLocation")).SendKeys("Colombo City Centre, 2nd floor food court");
        _driver.FindElement(By.XPath("//button[contains(text(),'Continue')]")).Click();

        _wait.Until(d => d.FindElement(By.Id("hiddenInformation")))
             .SendKeys("Torn inner pocket with a faded bus ticket stub inside.");

        _driver.FindElement(By.XPath("//button[contains(text(),'Report Lost Item')]")).Click();

        _wait.Until(d => d.Url.Contains("/report-lost-item/success"));
        Assert.Contains("Lost Item Reported Successfully", _driver.PageSource);
        Assert.Contains("ACTIVE", _driver.PageSource);
    }

    // E2E-02
    [Fact]
    public void Step1_ContinueBlocked_WhenTitleEmpty()
    {
        SignInAndOpenWizard();
        SelectCategory("Accessories");
        _driver.FindElement(By.Id("description")).SendKeys("Some description");
        _driver.FindElement(By.XPath("//button[contains(text(),'Continue')]")).Click();

        var error = _wait.Until(d => d.FindElement(By.XPath("//label[@for='title']/ancestor::div[1]/following-sibling::p")));
        Assert.Equal("Item title is required.", error.Text);
        Assert.Contains("/report-lost-item", _driver.Url);
        Assert.DoesNotContain("success", _driver.Url);
    }

    [Fact]
    public void Step1_ContinueBlocked_WhenDescriptionEmpty()
    {
        SignInAndOpenWizard();
        _driver.FindElement(By.Id("title")).SendKeys("Test item");
        SelectCategory("Accessories");
        _driver.FindElement(By.XPath("//button[contains(text(),'Continue')]")).Click();

        var error = _wait.Until(d => d.FindElement(By.XPath("//label[@for='description']/ancestor::div[1]/following-sibling::p")));
        Assert.Equal("Description is required.", error.Text);
        Assert.Contains("/report-lost-item", _driver.Url);
        Assert.DoesNotContain("success", _driver.Url);
    }

    // E2E-03
    [Fact]
    public void CategorySelection_UpdatesValue_AndClosesWhenClickedOutside()
    {
        SignInAndOpenWizard();
        SelectCategory("Accessories");

        Assert.Contains("Accessories", _driver.FindElement(By.Id("category")).Text);
        _driver.FindElement(By.TagName("h1")).Click();
        Assert.Empty(_driver.FindElements(By.XPath("//button[normalize-space()='Accessories' and not(@id='category')]")));
    }

    // E2E-07
    [Fact]
    public void DateField_MaxAttribute_IsToday_NoFutureDatesAllowed()
    {
        SignInAndOpenWizard();
        _driver.FindElement(By.Id("title")).SendKeys("Test item");
        SelectCategory("Accessories");
        _driver.FindElement(By.Id("description")).SendKeys("Test item description");
        _driver.FindElement(By.XPath("//button[contains(text(),'Continue')]")).Click();

        var dateInput = _wait.Until(d => d.FindElement(By.CssSelector("input[aria-label='Date lost']")));
        var maxAttr = dateInput.GetAttribute("max");
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");

        Assert.Equal(today, maxAttr);
    }

    // E2E-04 / E2E-05 — dropzone accepts valid image, rejects invalid type
    [Fact]
    public void PhotoDropzone_RejectsNonImageFile()
    {
        SignInAndOpenWizard();
        NavigateToStep3();
        var fileInput = _driver.FindElement(By.CssSelector("input[type=file]"));
        fileInput.SendKeys(CreateTempFile("not-an-image.txt", "not an image"));

        var thumbnails = _driver.FindElements(By.CssSelector("[class*='aspect-square'] img"));
        Assert.Empty(thumbnails);
    }

    [Fact]
    public void PhotoDropzone_AcceptsValidJpeg_ShowsPreview()
    {
        SignInAndOpenWizard();
        NavigateToStep3();
        var fileInput = _driver.FindElement(By.CssSelector("input[type=file]"));
        fileInput.SendKeys(CreateTempFile("sample.jpg", "not a real jpeg"));

        var thumbnail = _wait.Until(d => d.FindElement(By.CssSelector("[class*='aspect-square'] img")));
        Assert.True(thumbnail.Displayed);
    }

    // E2E-06
    [Fact]
    public void PhotoDropzone_CapsAtOnePhoto()
    {
        SignInAndOpenWizard();
        NavigateToStep3();
        var paths = Enumerable.Range(1, 2)
            .Select(i => CreateTempFile($"photo-{i}.jpg", "jpeg placeholder"))
            .ToArray();
        _driver.FindElement(By.CssSelector("input[type=file]")).SendKeys(string.Join(Environment.NewLine, paths));

        _wait.Until(d => d.FindElements(By.CssSelector("[class*='aspect-square'] img")).Count == 1);
        Assert.Single(_driver.FindElements(By.CssSelector("[class*='aspect-square'] img")));
        Assert.Empty(_driver.FindElements(By.XPath("//button[contains(.,'Add another photo')]")));
    }

    // E2E-12
    [Fact]
    public void DescriptionCounter_UpdatesAsTextIsEntered()
    {
        SignInAndOpenWizard();
        var description = _driver.FindElement(By.Id("description"));
        description.SendKeys("A detailed description");

        var counter = _wait.Until(d =>
        {
            var element = d.FindElement(By.XPath("//label[@for='description']/parent::div/span"));
            return element.Text == "22 / 2000" ? element : null;
        });
        Assert.NotNull(counter);
        Assert.Equal("22 / 2000", counter!.Text);
    }

    // E2E-10 — hidden info value must never appear in the rendered DOM
    [Fact]
    public void SuccessPage_NeverRendersHiddenInformationValueInDom()
    {
        SignInAndOpenWizard();
        const string secretMarker = "unique-secret-marker-xyz";
        FillFullWizard(hiddenInfo: secretMarker);
        _driver.FindElement(By.XPath("//button[contains(text(),'Report Lost Item')]")).Click();
        _wait.Until(d => d.Url.Contains("success"));

        Assert.DoesNotContain(secretMarker, _driver.PageSource);
        Assert.Contains("Hidden", _driver.PageSource);
    }

    // E2E-11
    [Fact]
    public void GoingBackToStep1_PreservesPreviouslyEnteredData()
    {
        SignInAndOpenWizard();
        _driver.FindElement(By.Id("title")).SendKeys("Preserved title");
        SelectCategory("Accessories");
        _driver.FindElement(By.Id("description")).SendKeys("Description to preserve");
        _driver.FindElement(By.XPath("//button[contains(text(),'Continue')]")).Click();
        SetSafePastDate();
        _wait.Until(d => d.FindElement(By.Id("lastKnownLocation")));

        _driver.FindElement(By.XPath("//button[contains(text(),'Back')]")).Click();

        var titleValue = _wait.Until(d => d.FindElement(By.Id("title"))).GetAttribute("value");
        Assert.Equal("Preserved title", titleValue);
    }

    private void NavigateToStep3()
    {
        _driver.FindElement(By.Id("title")).SendKeys("Test item");
        SelectCategory("Accessories");
        _driver.FindElement(By.Id("description")).SendKeys("Test item description");
        _driver.FindElement(By.XPath("//button[contains(text(),'Continue')]")).Click();
        SetSafePastDate();
        _wait.Until(d => d.FindElement(By.Id("lastKnownLocation"))).SendKeys("Test location");
        _driver.FindElement(By.XPath("//button[contains(text(),'Continue')]")).Click();
        _wait.Until(d => d.FindElement(By.Id("hiddenInformation")));
    }

    private void SetSafePastDate()
    {
        SetDate(DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd"));
    }

    private void SetDate(string date)
    {
        var dateInput = _wait.Until(d => d.FindElement(By.CssSelector("input[aria-label='Date lost']")));
        ((IJavaScriptExecutor)_driver).ExecuteScript(
            "const input = arguments[0];" +
            "const valueSetter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;" +
            "valueSetter.call(input, arguments[1]);" +
            "input.dispatchEvent(new Event('input', { bubbles: true }));" +
            "input.dispatchEvent(new Event('change', { bubbles: true }));",
            dateInput,
            date);
        _wait.Until(d => d.FindElement(By.CssSelector("input[aria-label='Date lost']"))
            .GetAttribute("value") == date);
    }

    private void SelectCategory(string category)
    {
        _driver.FindElement(By.Id("category")).Click();
        _wait.Until(d => d.FindElement(
            By.XPath($"//button[normalize-space()='{category}']"))).Click();
        _wait.Until(d => d.FindElement(By.Id("category")).Text.Contains(category, StringComparison.Ordinal));
    }

    private void FillFullWizard(string hiddenInfo)
    {
        NavigateToStep3();
        _driver.FindElement(By.Id("hiddenInformation")).SendKeys(hiddenInfo);
    }

    private static string CreateTempFile(string name, string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-{name}");
        File.WriteAllText(path, contents);
        return path;
    }
}
