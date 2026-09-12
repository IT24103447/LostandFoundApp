using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using WebDriverManager;
using WebDriverManager.DriverConfigs.Impl;
using WebDriverManager.Helpers;
using Xunit;

namespace LoginForm.SeleniumTests;

// One real Chrome browser is shared by the test class.
// Requirements:
//   1. Google Chrome installed
//   2. Frontend running at http://localhost:5173
//   3. Auth/Admin service running at http://localhost:5261
//   4. MySQL + Kafka running if the auth service needs them
public sealed class BrowserFixture : IDisposable
{
    public const string BaseUrl = "http://localhost:5173";
    public const string AuthApiBaseUrl = "http://localhost:5261";

    public IWebDriver Driver { get; }

    public BrowserFixture()
    {
        new DriverManager().SetUpDriver(
            new ChromeConfig(),
            VersionResolveStrategy.MatchingBrowser);

        var options = new ChromeOptions();
        // Uncomment for CI/headless execution:
        // options.AddArgument("--headless=new");
        options.AddArgument("--window-size=1280,900");

        Driver = new ChromeDriver(options);
        Driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromSeconds(2);
        Driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(30);
    }

    public void Dispose()
    {
        try
        {
            Driver.Quit();
        }
        finally
        {
            Driver.Dispose();
        }
    }
}
