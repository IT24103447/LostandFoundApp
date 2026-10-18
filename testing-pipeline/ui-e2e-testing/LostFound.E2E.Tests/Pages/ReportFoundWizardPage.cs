using LostFound.E2E.Tests.Support;
using OpenQA.Selenium;

namespace LostFound.E2E.Tests.Pages;

/// <summary>
/// Page object for the 3-step "Report a Found Item" wizard (/report-found-item) and its success page.
/// Step 1: title, category, description. Step 2: date found, location found.
/// Step 3: hidden (verification) information and an optional photo.
/// </summary>
public sealed class ReportFoundWizardPage
{
    private readonly IWebDriver _driver;

    public ReportFoundWizardPage(IWebDriver driver) => _driver = driver;

    private static By Step(int n) => By.XPath($"//span[normalize-space()='Step {n} of 3']");

    public ReportFoundWizardPage Open()
    {
        _driver.Navigate().GoToUrl(TestConfig.BaseUrl + "/report-found-item");
        _driver.WaitVisible(By.XPath("//h1[normalize-space()='Report a Found Item']"));
        _driver.WaitVisible(Step(1));
        return this;
    }

    // ---- step 1 ----
    public ReportFoundWizardPage EnterTitle(string title) => Type("title", title);
    public ReportFoundWizardPage EnterDescription(string text) => Type("description", text);

    public ReportFoundWizardPage ChooseCategory(string label)
    {
        _driver.WaitVisible(By.Id("category")).Click();
        _driver.WaitVisible(By.XPath($"//button[@type='button' and normalize-space()='{label}']")).Click();
        return this;
    }

    // ---- step 2 ----
    public ReportFoundWizardPage EnterDateFound(DateOnly date)
    {
        var input = _driver.WaitPresent(By.Id("dateFound"));   // native input is invisible (opacity 0)
        ((IJavaScriptExecutor)_driver).ExecuteScript(
            "const el = arguments[0];" +
            "Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(el, arguments[1]);" +
            "el.dispatchEvent(new Event('input', { bubbles: true }));" +
            "el.dispatchEvent(new Event('change', { bubbles: true }));",
            input, date.ToString("yyyy-MM-dd"));
        return this;
    }

    public ReportFoundWizardPage EnterLocationFound(string text) => Type("locationFound", text);

    // ---- step 3 ----
    public ReportFoundWizardPage EnterHiddenInformation(string text) => Type("hiddenInformation", text);

    // ---- navigation ----
    public ReportFoundWizardPage Continue(int expectedNextStep)
    {
        _driver.WaitVisible(By.XPath("//button[normalize-space()='Continue']")).Click();
        _driver.WaitVisible(Step(expectedNextStep));
        return this;
    }

    /// <summary>Clicks Continue on an incomplete step; the wizard should stay where it is.</summary>
    public ReportFoundWizardPage ContinueExpectingToStay()
    {
        _driver.WaitVisible(By.XPath("//button[normalize-space()='Continue']")).Click();
        return this;
    }

    public ReportFoundWizardPage Back(int expectedPreviousStep)
    {
        _driver.WaitVisible(By.XPath("//button[normalize-space()='Back']")).Click();
        _driver.WaitVisible(Step(expectedPreviousStep));
        return this;
    }

    public void Submit() =>
        _driver.WaitVisible(By.XPath("//button[@type='submit' and normalize-space()='Report Found Item']")).Click();

    // ---- assertion helpers ----
    public void WaitForValidationMessage(string message) =>
        _driver.WaitVisible(By.XPath($"//p[contains(@class,'text-red-500') and normalize-space()='{message}']"));

    /// <summary>
    /// Waits for a required-field message. It is normally raised when Continue is clicked, but on some
    /// fields this form only raises it once the field has been visited, so if the message is not there
    /// quickly the field is focused and left (blur), which is what a user tabbing through does.
    /// Returns true if the message appeared without having to visit the field.
    /// </summary>
    public bool WaitForRequiredMessage(string fieldId, string message)
    {
        var by = By.XPath($"//p[contains(@class,'text-red-500') and normalize-space()='{message}']");
        try
        {
            _driver.WaitVisible(by, 3);
            return true;
        }
        catch (WebDriverTimeoutException)
        {
            var field = _driver.WaitPresent(By.Id(fieldId));
            ((IJavaScriptExecutor)_driver).ExecuteScript("arguments[0].focus(); arguments[0].blur();", field);
            _driver.WaitVisible(by);
            return false;
        }
    }

    public void WaitForStep(int n) => _driver.WaitVisible(Step(n));

    public string TitleValue() => _driver.FindElement(By.Id("title")).GetAttribute("value") ?? "";
    public string LocationValue() => _driver.WaitVisible(By.Id("locationFound")).GetAttribute("value") ?? "";

    public void WaitForSuccessPage()
    {
        _driver.WaitForPath("/report-found-item/success");
        _driver.WaitVisible(By.XPath("//h1[normalize-space()='Found Item Reported Successfully']"));
    }

    /// <summary>
    /// Sets a text field in one step the way React expects it (native value setter, input and change
    /// events, then focus and blur so the field counts as visited). Used for very long and
    /// whitespace-only values, where typing character by character would be slow.
    /// </summary>
    public ReportFoundWizardPage SetField(string id, string text)
    {
        var element = _driver.WaitVisible(By.Id(id));
        ((IJavaScriptExecutor)_driver).ExecuteScript(
            "const el = arguments[0];" +
            "const proto = el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;" +
            "Object.getOwnPropertyDescriptor(proto, 'value').set.call(el, arguments[1]);" +
            "el.dispatchEvent(new Event('input', { bubbles: true }));" +
            "el.dispatchEvent(new Event('change', { bubbles: true }));" +
            "el.focus(); el.blur();",
            element, text);
        return this;
    }

    private ReportFoundWizardPage Type(string id, string text)
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
        EnterTitle(title).ChooseCategory("Accessories").EnterDescription($"E2E found description for {title}")
            .Continue(2);
        EnterDateFound(DateOnly.FromDateTime(DateTime.Today.AddDays(-1))).EnterLocationFound("Main campus library")
            .Continue(3);
        EnterHiddenInformation(hidden).Submit();
        return (title, hidden);
    }
}
