using LostFound.E2E.Tests.Support;
using OpenQA.Selenium;

namespace LostFound.E2E.Tests.Pages;

/// <summary>Page object for /edit-lost-item/:id (EditLostItemPage.tsx).</summary>
public sealed class EditLostItemPage
{
    private static readonly By TitleInput = By.Id("title");

    private readonly IWebDriver _driver;

    public EditLostItemPage(IWebDriver driver) => _driver = driver;

    /// <summary>Waits for the form to appear with the report's current title already loaded.</summary>
    public EditLostItemPage WaitUntilLoaded(string currentTitle)
    {
        _driver.WaitVisible(By.XPath("//h1[normalize-space()='Edit Lost Item Report']"));
        _driver.Wait().Until(d => d.FindElement(TitleInput).GetAttribute("value") == currentTitle);
        return this;
    }

    /// <summary>Selects the whole title and replaces it. An empty string clears the field.</summary>
    public EditLostItemPage ReplaceTitle(string text)
    {
        var input = _driver.WaitVisible(TitleInput);
        input.SendKeys(Keys.Control + "a");
        input.SendKeys(text.Length == 0 ? Keys.Delete : text);
        return this;
    }

    /// <summary>
    /// The private hidden information is never returned by the API, so the edit form starts with this
    /// field empty and refuses to save until the user types it again.
    /// </summary>
    public EditLostItemPage ReEnterHiddenInformation(string text)
    {
        var field = _driver.WaitVisible(By.Id("hiddenInformation"));
        field.Clear();
        field.SendKeys(text);
        return this;
    }

    public EditLostItemPage Save()
    {
        _driver.WaitVisible(By.XPath("//button[normalize-space()='Save Changes']")).Click();
        return this;
    }

    public void WaitForSavedMessage() =>
        _driver.WaitVisible(By.XPath("//*[normalize-space()='Changes saved.']"));

    /// <summary>Waits for an error message shown after saving (for example the 403 permission message).</summary>
    public void WaitForSubmitError(string message) =>
        _driver.WaitVisible(By.XPath($"//*[normalize-space()=\"{message}\"]"));

    public void WaitForValidationMessage(string message) =>
        _driver.WaitVisible(By.XPath($"//p[contains(@class,'text-red-500') and normalize-space()='{message}']"));
}
