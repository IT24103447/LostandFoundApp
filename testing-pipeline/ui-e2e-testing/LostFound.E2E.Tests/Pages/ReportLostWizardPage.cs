using LostFound.E2E.Tests.Support;
using OpenQA.Selenium;

namespace LostFound.E2E.Tests.Pages;

/// <summary>
/// Page object for the 3-step "Report a Lost Item" wizard (/report-lost-item) and its success page.
/// Step 1: title, category, description. Step 2: date lost, last known location.
/// Step 3: hidden information and optional photo.
/// </summary>
public sealed class ReportLostWizardPage
{
    private readonly IWebDriver _driver;

    public ReportLostWizardPage(IWebDriver driver) => _driver = driver;

    private static By Step(int n) => By.XPath($"//span[normalize-space()='Step {n} of 3']");

    public ReportLostWizardPage Open()
    {
        _driver.Navigate().GoToUrl(TestConfig.BaseUrl + "/report-lost-item");
        _driver.WaitVisible(By.XPath("//h1[normalize-space()='Report a Lost Item']"));
        _driver.WaitVisible(Step(1));
        return this;
    }

    // ---- step 1 ----
    public ReportLostWizardPage EnterTitle(string title) => Type("title", title);
    public ReportLostWizardPage EnterDescription(string text) => Type("description", text);

    public ReportLostWizardPage ChooseCategory(string label)
    {
        _driver.WaitVisible(By.Id("category")).Click();                       // opens the custom dropdown
        _driver.WaitVisible(By.XPath($"//button[@type='button' and normalize-space()='{label}']")).Click();
        return this;
    }

    // ---- step 2 ----
    public ReportLostWizardPage EnterDateLost(DateOnly date)
    {
        var input = _driver.WaitPresent(By.Id("dateLost")); // native input is invisible (opacity 0)
        // Typing into <input type=date> depends on the browser locale, so set it natively instead.
        ((IJavaScriptExecutor)_driver).ExecuteScript(
            "const el = arguments[0];" +
            "Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(el, arguments[1]);" +
            "el.dispatchEvent(new Event('input', { bubbles: true }));" +
            "el.dispatchEvent(new Event('change', { bubbles: true }));",
            input, date.ToString("yyyy-MM-dd"));
        return this;
    }

    public ReportLostWizardPage EnterLocation(string text) => Type("lastKnownLocation", text);

    // ---- step 3 ----
    public ReportLostWizardPage EnterHiddenInformation(string text) => Type("hiddenInformation", text);

    // ---- navigation ----
    public ReportLostWizardPage Continue(int expectedNextStep)
    {
        _driver.WaitVisible(By.XPath("//button[normalize-space()='Continue']")).Click();
        _driver.WaitVisible(Step(expectedNextStep));
        return this;
    }

    /// <summary>Clicks Continue when the step is incomplete; the wizard should stay where it is.</summary>
    public ReportLostWizardPage ContinueExpectingToStay() 
    {
        _driver.WaitVisible(By.XPath("//button[normalize-space()='Continue']")).Click();
        return this;
    }

    public ReportLostWizardPage Back(int expectedPreviousStep)
    {
        _driver.WaitVisible(By.XPath("//button[normalize-space()='Back']")).Click();
        _driver.WaitVisible(Step(expectedPreviousStep));
        return this;
    }

    public void Submit() =>
        _driver.WaitVisible(By.XPath("//button[@type='submit' and normalize-space()='Report Lost Item']")).Click();

    // ---- assertions helpers ----
    public void WaitForValidationMessage(string message) =>
        _driver.WaitVisible(By.XPath($"//p[contains(@class,'text-red-500') and normalize-space()='{message}']"));

    public void WaitForStep(int n) => _driver.WaitVisible(Step(n));

    public string TitleValue() => _driver.FindElement(By.Id("title")).GetAttribute("value") ?? "";

    public void WaitForSuccessPage()
    {
        _driver.WaitForPath("/report-lost-item/success");
        _driver.WaitVisible(By.XPath("//h1[normalize-space()='Lost Item Reported Successfully']"));
    }

    // ---- date field ----
    /// <summary>The value of the date field's max attribute (the latest date the browser allows).</summary>
    public string DateMaxAttribute() =>
        _driver.WaitPresent(By.Id("dateLost")).GetAttribute("max") ?? "";

    // ---- photo (step 3) ----
    private static readonly By FileInput = By.CssSelector("input[type='file']");

    /// <summary>
    /// Chooses a file in the photo dropzone. The real input is display:none, so it is made
    /// reachable first; the app's own change handler then runs exactly as for a user's choice.
    /// </summary>
    public ReportLostWizardPage UploadPhoto(string path)
    {
        var input = _driver.WaitPresent(FileInput);
        ((IJavaScriptExecutor)_driver).ExecuteScript("arguments[0].style.display='block';", input);
        input.SendKeys(path);
        return this;
    }

    public void WaitForPhotoPreview(string fileName) =>
        _driver.WaitVisible(By.XPath($"//img[@alt='{fileName}']"));

    public int PhotoPreviewCount() => _driver.FindElements(By.XPath("//button[starts-with(@aria-label,'Remove ')]")).Count;

    public bool HasRemoveButtonFor(string fileName) =>
        _driver.FindElements(By.XPath($"//button[@aria-label='Remove {fileName}']")).Count > 0;

    /// <summary>The dropzone (and its file input) disappears once the one-photo limit is reached.</summary>
    public bool CanAddAnotherPhoto() => _driver.FindElements(FileInput).Count > 0;

    /// <summary>Fills steps 1 and 2 with valid data and stops on step 3.</summary>
    public ReportLostWizardPage GoToStepThree(string title)
    {
        EnterTitle(title).ChooseCategory("Accessories").EnterDescription($"E2E description for {title}").Continue(2);
        EnterDateLost(DateOnly.FromDateTime(DateTime.Today.AddDays(-1))).EnterLocation("Main campus library").Continue(3);
        return this;
    }

    public string PageSource => _driver.PageSource;

    private ReportLostWizardPage Type(string id, string text)
    {
        var element = _driver.WaitVisible(By.Id(id));
        element.Clear();
        element.SendKeys(text);
        return this;
    }

    /// <summary>Fills all three steps with valid data and submits. Returns the title and hidden text used.</summary>
    public (string Title, string Hidden) CompleteWithValidData(string title)
    {
        var hidden = $"hidden-{Guid.NewGuid():N}";
        EnterTitle(title).ChooseCategory("Accessories").EnterDescription($"E2E description for {title}")
            .Continue(2);
        EnterDateLost(DateOnly.FromDateTime(DateTime.Today.AddDays(-1))).EnterLocation("Main campus library")
            .Continue(3);
        EnterHiddenInformation(hidden).Submit();
        return (title, hidden);
    }
}
