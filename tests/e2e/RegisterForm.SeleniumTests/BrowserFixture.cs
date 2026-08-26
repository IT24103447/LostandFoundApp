using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using WebDriverManager;
using WebDriverManager.DriverConfigs.Impl;
using WebDriverManager.Helpers;
using Xunit;

namespace RegisterForm.SeleniumTests;

// Shared across all tests in a class via IClassFixture<BrowserFixture> — starts Chrome
// once, reuses it for every test, then quits it when the class is done. Requires:
//   1. Google Chrome installed on this PC (any recent version)
//   2. The frontend running at http://localhost:5173 (npm run dev)
//   3. The backend running at http://localhost:5261 (dotnet run), since the form calls it
public class BrowserFixture : IDisposable
{
    public const string BaseUrl = "http://localhost:5173";

    public IWebDriver Driver { get; }

    public BrowserFixture()
    {
        // MatchingBrowser (not the default Latest) ensures the downloaded ChromeDriver
        // version matches whatever Chrome is actually installed on this PC, avoiding
        // "This version of ChromeDriver only supports Chrome version X" errors.
        new DriverManager().SetUpDriver(new ChromeConfig(), VersionResolveStrategy.MatchingBrowser);

        var options = new ChromeOptions();
        // Comment out the next line if you want to watch the browser drive itself.
        // options.AddArgument("--headless=new");
        options.AddArgument("--window-size=1280,900");

        Driver = new ChromeDriver(options);
        Driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromSeconds(5);
    }

    public void Dispose()
    {
        Driver.Quit();
        Driver.Dispose();
    }
}
