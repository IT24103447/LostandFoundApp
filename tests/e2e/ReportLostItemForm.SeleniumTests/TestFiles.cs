/// <summary>
/// Resolves absolute paths to test asset files stored in the TestAssets folder.
/// The folder is copied to the build output directory by the .csproj, so paths
/// work whether tests run from within an IDE or via "dotnet test".
/// </summary>
public static class TestFiles
{
    // BaseDirectory is the bin/Debug/net8.0/ folder at runtime.
    private static readonly string AssetsDir =
        Path.Combine(AppContext.BaseDirectory, "TestAssets");

    /// <summary>
    /// Returns the full absolute path to a file inside TestAssets/.
    /// Throws if the file does not exist so that test failures are obvious.
    /// </summary>
    public static string PathTo(string fileName)
    {
        var path = Path.Combine(AssetsDir, fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Test asset '{fileName}' not found. Expected it at: {path}", path);
        return path;
    }
}
