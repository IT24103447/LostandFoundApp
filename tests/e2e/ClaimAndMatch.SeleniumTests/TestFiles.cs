namespace ClaimAndMatch.SeleniumTests;

/// <summary>
/// Resolves absolute paths to test asset files stored in the TestAssets folder.
/// The folder is copied to the build output directory by the .csproj, so paths
/// work whether tests run from within an IDE or via "dotnet test".
/// </summary>
public static class TestFiles
{
    private static readonly string AssetsDir =
        Path.Combine(AppContext.BaseDirectory, "TestAssets");

    public static string PathTo(string fileName)
    {
        var path = Path.Combine(AssetsDir, fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Test asset '{fileName}' not found. Expected it at: {path}", path);
        return path;
    }
}
