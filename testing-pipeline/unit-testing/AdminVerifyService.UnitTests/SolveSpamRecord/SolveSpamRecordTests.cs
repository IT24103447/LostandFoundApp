using System.Diagnostics;
using AdminVerifyService.Spam;
using AdminVerifyService.UnitTests.Support;

namespace AdminVerifyService.UnitTests.SolveSpamRecord;

public sealed class SolveSpamRecordTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("five-attempt-limit")]
    [InlineData("retry-succeeds")]
    [InlineData("skip-404")]
    [InlineData("skip-409")]
    [InlineData("resume")]
    [InlineData("kick-checked")]
    [InlineData("kick-unchecked")]
    [InlineData("kick-refused")]
    [InlineData("already-kicked")]
    [InlineData("kick-not-repeated")]
    [InlineData("save-result-failure")]
    [InlineData("start-forbidden")]
    [InlineData("finish-failure")]
    [InlineData("failed-delete-still-kicks")]
    public async Task ProductionSolveRunnerHonorsApiResultsAndRetryPolicy(string scenario)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "frontend/src/features/admin/solve/runSolve.ts")))
            root = root.Parent;
        Assert.NotNull(root);
        var start = new ProcessStartInfo("node")
        {
            WorkingDirectory = root.FullName, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        start.ArgumentList.Add("--experimental-vm-modules");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "SolveSpamRecord/solve-unit-cases.mjs"));
        start.ArgumentList.Add(scenario);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"Solve unit case {scenario} exceeded 15 seconds.");
        }
        Assert.True(process.ExitCode == 0, $"{scenario}: {await stdout}\n{await stderr}");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("UNKNOWN")]
    public async Task InvalidListingResultIsRejectedBeforeDatabaseAccess(string? result)
    {
        var error = await Assert.ThrowsAsync<SpamReviewException>(() => new SpamSolveRepository(null!, TimeProvider.System)
            .SaveListingResultAsync(Guid.NewGuid(), Guid.NewGuid(), result, CancellationToken.None));
        Assert.Equal(400, error.StatusCode);
    }

    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, "0", false)]
    [InlineData(true, "1", true)]
    public Task SolveRequiresAdmin(bool authenticated, string? claim, bool expected) =>
        AdminPolicy.AssertAccessAsync("Solve", authenticated, claim, expected);
}
