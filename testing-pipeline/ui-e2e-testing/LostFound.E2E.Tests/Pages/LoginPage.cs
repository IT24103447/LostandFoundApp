using LostFound.E2E.Tests.Support;
using OpenQA.Selenium;

namespace LostFound.E2E.Tests.Pages;

/// <summary>Page object for /login. Selectors come from LoginForm.tsx.</summary>
public sealed class LoginPage
{
    private static readonly By Email = By.Id("email");
    private static readonly By Password = By.Id("password");
    private static readonly By Submit = By.CssSelector("button[type='submit']");
    private static readonly By ErrorAlert = By.CssSelector("[role='alert']");
    private static readonly By EmailError = By.Id("email-error");
    private static readonly By PasswordError = By.Id("password-error");

    private readonly IWebDriver _driver;

    public LoginPage(IWebDriver driver) => _driver = driver;

    public LoginPage Open()
    {
        _driver.Navigate().GoToUrl(TestConfig.BaseUrl + "/login");
        _driver.WaitVisible(Email);
        return this;
    }

    /// <summary>Opens the login page, fills the form and submits it.</summary>
    public void LoginAs(TestUser user) => LoginAs(user.Email, user.Password);

    public void LoginAs(string email, string password)
    {
        Open();
        _driver.FindElement(Email).SendKeys(email);
        _driver.FindElement(Password).SendKeys(password);
        _driver.FindElement(Submit).Click();
    }

    public void SubmitEmpty()
    {
        Open();
        _driver.FindElement(Submit).Click();
    }

    public string WaitForErrorMessage() => _driver.WaitVisible(ErrorAlert).Text.Trim();
    public string WaitForEmailError() => _driver.WaitVisible(EmailError).Text.Trim();
    public string WaitForPasswordError() => _driver.WaitVisible(PasswordError).Text.Trim();
}
