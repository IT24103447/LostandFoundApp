using LostFound.E2E.Tests.Support;
using OpenQA.Selenium;

namespace LostFound.E2E.Tests.Pages;

/// <summary>Page object for /register (RegisterForm.tsx). Only client-side behaviour is tested (no OTP e-mail).</summary>
public sealed class RegisterPage
{
    private static readonly By Email = By.Id("email");
    private static readonly By Password = By.Id("password");
    private static readonly By Submit = By.CssSelector("button[type='submit']");
    private static readonly By EmailError = By.Id("email-error");
    private static readonly By PasswordError = By.Id("password-error");

    private readonly IWebDriver _driver;

    public RegisterPage(IWebDriver driver) => _driver = driver;

    public RegisterPage Open()
    {
        _driver.Navigate().GoToUrl(TestConfig.BaseUrl + "/register");
        _driver.WaitVisible(Email);
        return this;
    }

    public RegisterPage Fill(string email, string password)
    {
        _driver.FindElement(Email).SendKeys(email);
        var field = _driver.FindElement(Password);
        field.SendKeys(password);
        field.SendKeys(Keys.Tab);   // blur: the strength meter only re-renders after the form state changes
        return this;
    }

    /// <summary>Fills every field of the form with the given values (confirm-password repeats the password).</summary>
    public RegisterPage FillAll(string name, string email, string phone, string password)
    {
        _driver.FindElement(By.Id("name")).SendKeys(name);
        _driver.FindElement(Email).SendKeys(email);
        _driver.FindElement(By.Id("phoneNo")).SendKeys(phone);
        _driver.FindElement(Password).SendKeys(password);
        var confirm = _driver.FindElement(By.Id("confirmPassword"));
        confirm.SendKeys(password);
        confirm.SendKeys(Keys.Tab);
        return this;
    }

    public RegisterPage SubmitForm()
    {
        _driver.FindElement(Submit).Click();
        return this;
    }

    public string WaitForEmailError() => _driver.WaitVisible(EmailError).Text.Trim();
    public string WaitForPasswordError() => _driver.WaitVisible(PasswordError).Text.Trim();

    /// <summary>The password-strength meter prints a word (Weak, Fair, Good, Strong) under the field.</summary>
    public void WaitForStrengthLabel(string label) =>
        _driver.WaitVisible(By.XPath($"//*[normalize-space()='{label}']"));
}
