using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Chromium;
using WebDriverManager;
using WebDriverManager.DriverConfigs.Impl;
using WebDriverManager.Helpers;
using Xunit;

namespace ReportLostItemForm.SeleniumTests;

public class ReportLostItemFixture : IDisposable
{
    public const string BaseUrl     = "http://localhost:5173";
    private const string ItemApiUrl = "http://localhost:5001";

    public IWebDriver Driver { get; }

    public ReportLostItemFixture()
    {
        new DriverManager().SetUpDriver(new ChromeConfig(), VersionResolveStrategy.MatchingBrowser);

        var options = new ChromeOptions();
        options.AddArgument("--window-size=1280,900");

        Driver = new ChromeDriver(options);
        Driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromSeconds(5);

        // Login ONCE for the entire test suite via the real UI.
        LoginHelper.LoginAsTestUser(Driver, BaseUrl);

        // The auth_token cookie is set on localhost:5173 with SameSite=Lax.
        // Modern browsers do not reliably send it cross-port to localhost:5001.
        // Fix: plant the same cookie value on localhost:5001 via CDP so that
        // ItemService's OnMessageReceived handler can read it from the request.
        SyncAuthCookieToItemService();
    }

    private void SyncAuthCookieToItemService()
    {
        if (string.IsNullOrEmpty(LoginHelper.AuthToken))
            return;

        var chrome = Driver as ChromiumDriver;
        if (chrome == null) return;

        // Enable the Network domain so CDP cookie commands work.
        chrome.ExecuteCdpCommand("Network.enable", new Dictionary<string, object>());

        // Plant the auth_token cookie on the ItemService origin.
        // secure=false because local ItemService runs over plain HTTP.
        chrome.ExecuteCdpCommand("Network.setCookie", new Dictionary<string, object>
        {
            { "name",     "auth_token"          },
            { "value",    LoginHelper.AuthToken  },
            { "domain",   "localhost"            },
            { "path",     "/"                   },
            { "httpOnly", true                   },
            { "secure",   false                  },
            { "sameSite", "Lax"                  },
            { "url",      ItemApiUrl             }   // anchors the cookie to port 5001
        });
    }

    public void Dispose()
    {
        Driver.Quit();
        Driver.Dispose();
    }
}
