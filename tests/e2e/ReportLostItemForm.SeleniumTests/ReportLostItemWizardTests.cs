using NUnit.Framework;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;

[TestFixture]
public class ReportLostItemWizardTests
{
    private IWebDriver _driver = null!;
    private WebDriverWait _wait = null!;
    private const string BaseUrl = "http://localhost:5173";

    [SetUp]
    public void SetUp()
    {
        _driver = new ChromeDriver();
        _driver.Manage().Window.Maximize();
        _wait = new WebDriverWait(_driver, TimeSpan.FromSeconds(10));
        LoginHelper.LoginAsTestUser(_driver, BaseUrl); // sets auth_token cookie via real login flow
        _driver.Navigate().GoToUrl($"{BaseUrl}/report-lost-item");
    }

    [TearDown]
    public void TearDown() => _driver.Quit();

    // E2E-01
    [Test]
    public void CompletesFullWizard_HappyPath_ReachesSuccessPage()
    {
        _driver.FindElement(By.Id("title")).SendKeys("Black leather wallet");

        _driver.FindElement(By.Id("category")).Click();
        _wait.Until(d => d.FindElement(By.XPath("//button[contains(text(),'Wallets')]"))).Click();

        _driver.FindElement(By.Id("description")).SendKeys("Bifold wallet, slightly worn, lost near the food court.");
        _driver.FindElement(By.XPath("//button[contains(text(),'Continue')]")).Click();

        var dateInput = _wait.Until(d => d.FindElement(By.CssSelector("input[aria-label='Date lost']")));
        ((IJavaScriptExecutor)_driver).ExecuteScript(
            "arguments[0].value = arguments[1]; arguments[0].dispatchEvent(new Event('change'));",
            dateInput, "2026-08-28");

        _driver.FindElement(By.Id("lastKnownLocation")).SendKeys("Colombo City Centre, 2nd floor food court");
        _driver.FindElement(By.XPath("//button[contains(text(),'Continue')]")).Click();

        _wait.Until(d => d.FindElement(By.Id("hiddenInformation")))
             .SendKeys("Torn inner pocket with a faded bus ticket stub inside.");

        _driver.FindElement(By.XPath("//button[contains(text(),'Report Lost Item')]")).Click();

        _wait.Until(d => d.Url.Contains("/report-lost-item/success"));
        Assert.That(_driver.PageSource, Does.Contain("Lost Item Reported Successfully"));
        Assert.That(_driver.PageSource, Does.Contain("ACTIVE"));
    }

    // E2E-02
    [Test]
    public void Step1_ContinueBlocked_WhenTitleEmpty()
    {
        _driver.FindElement(By.Id("description")).SendKeys("Some description");
        _driver.FindElement(By.XPath("//button[contains(text(),'Continue')]")).Click();

        var error = _wait.Until(d => d.FindElement(By.XPath("//label[@for='title']/following-sibling::*//p")));
        Assert.That(_driver.Url, Does.Contain("/report-lost-item"));
        Assert.That(_driver.Url, Does.Not.Contain("success"));
    }

    // E2E-07
    [Test]
    public void DateField_MaxAttribute_IsToday_NoFutureDatesAllowed()
    {
        _driver.FindElement(By.Id("title")).SendKeys("Test item");
        _driver.FindElement(By.XPath("//button[contains(text(),'Continue')]")).Click();

        var dateInput = _wait.Until(d => d.FindElement(By.CssSelector("input[aria-label='Date lost']")));
        var maxAttr = dateInput.GetAttribute("max");
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");

        Assert.That(maxAttr, Is.EqualTo(today));
    }

    // E2E-04 / E2E-05 — dropzone accepts valid image, rejects invalid type
    [Test]
    public void PhotoDropzone_RejectsNonImageFile()
    {
        NavigateToStep3();
        var fileInput = _driver.FindElement(By.CssSelector("input[type=file]"));
        fileInput.SendKeys(TestFiles.PathTo("not_an_image.txt"));

        var thumbnails = _driver.FindElements(By.CssSelector("[class*='aspect-square'] img"));
        Assert.That(thumbnails.Count, Is.EqualTo(0));
    }

    [Test]
    public void PhotoDropzone_AcceptsValidJpeg_ShowsPreview()
    {
        NavigateToStep3();
        var fileInput = _driver.FindElement(By.CssSelector("input[type=file]"));
        fileInput.SendKeys(TestFiles.PathTo("sample.jpg"));

        var thumbnail = _wait.Until(d => d.FindElement(By.CssSelector("[class*='aspect-square'] img")));
        Assert.That(thumbnail.Displayed, Is.True);
    }

    // E2E-10 — hidden info value must never appear in the rendered DOM
    [Test]
    public void SuccessPage_NeverRendersHiddenInformationValueInDom()
    {
        const string secretMarker = "unique-secret-marker-xyz";
        FillFullWizard(hiddenInfo: secretMarker);
        _driver.FindElement(By.XPath("//button[contains(text(),'Report Lost Item')]")).Click();
        _wait.Until(d => d.Url.Contains("success"));

        Assert.That(_driver.PageSource, Does.Not.Contain(secretMarker));
        Assert.That(_driver.PageSource, Does.Contain("Hidden")); // masked label only
    }

    // E2E-11
    [Test]
    public void GoingBackToStep1_PreservesPreviouslyEnteredData()
    {
        _driver.FindElement(By.Id("title")).SendKeys("Preserved title");
        _driver.FindElement(By.XPath("//button[contains(text(),'Continue')]")).Click();
        _wait.Until(d => d.FindElement(By.Id("lastKnownLocation")));

        _driver.FindElement(By.XPath("//button[contains(text(),'Back')]")).Click();

        var titleValue = _wait.Until(d => d.FindElement(By.Id("title"))).GetAttribute("value");
        Assert.That(titleValue, Is.EqualTo("Preserved title"));
    }

    private void NavigateToStep3()
    {
        _driver.FindElement(By.Id("title")).SendKeys("Test item");
        _driver.FindElement(By.XPath("//button[contains(text(),'Continue')]")).Click();
        _wait.Until(d => d.FindElement(By.Id("lastKnownLocation"))).SendKeys("Test location");
        _driver.FindElement(By.XPath("//button[contains(text(),'Continue')]")).Click();
        _wait.Until(d => d.FindElement(By.Id("hiddenInformation")));
    }

    private void FillFullWizard(string hiddenInfo)
    {
        NavigateToStep3();
        _driver.FindElement(By.Id("hiddenInformation")).SendKeys(hiddenInfo);
    }
}
