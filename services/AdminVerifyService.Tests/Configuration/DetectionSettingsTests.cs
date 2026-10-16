using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using AdminVerifyService.Configuration;

namespace AdminVerifyService.Tests.Configuration;

/// <summary>
/// Story LF-82 no-network coverage of DetectionSettings: the shipped defaults, binding from the
/// "Detection" configuration section (the exact path Program.cs wires), the boundary of the
/// startup validation rule (SpamThreshold &gt;= 2, SpamWindowMinutes &gt;= 1, SpamRecordCap &gt;= 0), and the
/// IOptionsMonitor reload behaviour that lets a config change reach the next SpamRule application.
/// No database, no Kafka and no Docker, so this class always runs in CI.
///
/// Why the predicate is duplicated here: the production registration is a lambda inside
/// Program.cs (AddOptions&lt;DetectionSettings&gt;().Validate(...)), which cannot be referenced from a
/// test project. This class re-declares it as a mirror so the DI *behaviour* (invalid config
/// failing resolution, valid config resolving) is pinned end-to-end. Known-gaps records the
/// lockstep requirement and the stronger alternative (extract an IsValid method); if the rule in
/// Program.cs ever changes, SatisfiesValidationRule below must change at the same time.
/// </summary>
public sealed class DetectionSettingsTests
{
    private const string FailureMessage = "Detection settings are invalid.";

    // MUST stay in lockstep with Program.cs AddOptions<DetectionSettings>().Validate(...).
    private static bool SatisfiesValidationRule(DetectionSettings settings) =>
        settings.SpamThreshold >= 2 &&
        settings.SpamWindowMinutes >= 1 &&
        settings.SpamRecordCap >= 0;

    // DetectionSettings uses init-only setters, so a future .Configure(o => o.X = ...) call would
    // not compile. The production path (and these tests) set values through configuration binding.
    private static IConfiguration Section(string threshold, string window, string cap) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Detection:SpamThreshold"] = threshold,
                ["Detection:SpamWindowMinutes"] = window,
                ["Detection:SpamRecordCap"] = cap
            })
            .Build();

    private static IConfiguration Empty() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

    private static IOptions<DetectionSettings> BuildDetectionOptions(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddOptions<DetectionSettings>()
            .Bind(configuration.GetSection("Detection"))
            .Validate(SatisfiesValidationRule, FailureMessage);

        return services.BuildServiceProvider()
            .GetRequiredService<IOptions<DetectionSettings>>();
    }

    // ---- shipped defaults ----

    [Fact] // LF-82 AC: a deployment that ships no Detection section still runs with a sanctioned rule.
    public void Defaults_AreSanctionedByTheValidationRule()
    {
        var settings = new DetectionSettings();

        Assert.Equal(3, settings.SpamThreshold);
        Assert.Equal(60, settings.SpamWindowMinutes);
        Assert.Equal(0, settings.SpamRecordCap); // 0 = no cap
        Assert.True(SatisfiesValidationRule(settings));
    }

    [Fact]
    public void BindFromEmptyConfiguration_FallsBackToTheShippedDefaults()
    {
        var options = BuildDetectionOptions(Empty());

        Assert.Equal(3, options.Value.SpamThreshold);
        Assert.Equal(60, options.Value.SpamWindowMinutes);
        Assert.Equal(0, options.Value.SpamRecordCap);
    }

    // ---- accepted boundary ----

    [Theory]
    [InlineData("2", "1", "0")]   // every minimum at once
    [InlineData("2", "1", "100")] // cap > 0 allowed
    [InlineData("10", "1440", "0")] // a day-long window with no cap
    [InlineData("3", "60", "0")]  // the shipped appsettings values
    public void ValidSettings_AreAccepted(string threshold, string window, string cap)
    {
        var options = BuildDetectionOptions(Section(threshold, window, cap));

        Assert.Equal(int.Parse(threshold), options.Value.SpamThreshold);
        Assert.Equal(int.Parse(window), options.Value.SpamWindowMinutes);
        Assert.Equal(int.Parse(cap), options.Value.SpamRecordCap);
    }

    // ---- rejected boundary ----

    [Theory]
    [InlineData("1", "1", "0")]    // threshold below its minimum of 2
    [InlineData("2", "0", "0")]    // window below its minimum of 1
    [InlineData("2", "1", "-1")]   // cap below its minimum of 0
    [InlineData("0", "0", "0")]    // every minimum missed at once
    public void InvalidSettings_AreRejectedAtResolution(string threshold, string window, string cap)
    {
        var options = BuildDetectionOptions(Section(threshold, window, cap));

        var exception = Assert.Throws<OptionsValidationException>(() => _ = options.Value);
        Assert.Contains(FailureMessage, exception.Failures);
    }

    // ---- section binding ----

    [Fact] // Only the "Detection" section feeds these settings; a stray section must not leak in.
    public void BindFromConfiguration_ReadsOnlyTheDetectionSection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // A matching section under a different root must be ignored.
                ["Other:SpamRecordCap"] = "7",
                ["Detection:SpamThreshold"] = "4",
                ["Detection:SpamWindowMinutes"] = "45",
                ["Detection:SpamRecordCap"] = "2"
            })
            .Build();

        var options = BuildDetectionOptions(configuration);

        Assert.Equal(4, options.Value.SpamThreshold);
        Assert.Equal(45, options.Value.SpamWindowMinutes);
        Assert.Equal(2, options.Value.SpamRecordCap);
    }

    // ---- IOptionsMonitor hot-swap (what SpamRule reads per application) ----

    [Fact] // LF-82 AC: a change to the Detection section reaches the NEXT SpamRule application.
    public void MonitorReload_PicksUpChangedDetectionValuesWithoutRestart()
    {
        // A JSON file on disk is the realistic reload path (appsettings.json + AddJsonFile).
        // An in-memory provider does not re-read a mutated dictionary on Reload(), so this test
        // reloads a real file synchronously - no file-watcher timing, deterministic on all hosts.
        var path = Path.Combine(Path.GetTempPath(), $"detection-settings-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(
                path,
                """
                {
                  "Detection": {
                    "SpamThreshold": 2,
                    "SpamWindowMinutes": 30,
                    "SpamRecordCap": 1
                  }
                }
                """);

            var configuration = new ConfigurationBuilder()
                .AddJsonFile(path, optional: false, reloadOnChange: false)
                .Build();

            var services = new ServiceCollection();
            services.AddOptions<DetectionSettings>()
                .Bind(configuration.GetSection("Detection"))
                .Validate(SatisfiesValidationRule, FailureMessage);

            var provider = services.BuildServiceProvider();
            var monitor = provider.GetRequiredService<IOptionsMonitor<DetectionSettings>>();

            Assert.Equal(2, monitor.CurrentValue.SpamThreshold);

            File.WriteAllText(
                path,
                """
                {
                  "Detection": {
                    "SpamThreshold": 5,
                    "SpamWindowMinutes": 30,
                    "SpamRecordCap": 1
                  }
                }
                """);

            configuration.Reload();

            Assert.Equal(5, monitor.CurrentValue.SpamThreshold);
            Assert.Equal(30, monitor.CurrentValue.SpamWindowMinutes);
            Assert.Equal(1, monitor.CurrentValue.SpamRecordCap);
        }
        finally
        {
            File.Delete(path);
        }
    }
}