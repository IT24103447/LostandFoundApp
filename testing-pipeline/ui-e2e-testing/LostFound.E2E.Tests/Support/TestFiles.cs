namespace LostFound.E2E.Tests.Support;

/// <summary>
/// Creates the small files the upload tests need at run time in a temp folder, so no binary
/// fixtures have to be committed to the repository.
/// </summary>
public static class TestFiles
{
    // A valid 1x1 PNG.
    private const string TinyPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private static string Folder
    {
        get
        {
            var path = Path.Combine(Path.GetTempPath(), "lostfound-e2e-files");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    /// <summary>A real, tiny PNG (an allowed photo type).</summary>
    public static string ValidImage(string name = "e2e-photo.png")
    {
        var path = Path.Combine(Folder, name);
        File.WriteAllBytes(path, Convert.FromBase64String(TinyPngBase64));
        return path;
    }

    /// <summary>A plain text file: not an allowed photo type.</summary>
    public static string TextFile(string name = "e2e-notes.txt")
    {
        var path = Path.Combine(Folder, name);
        File.WriteAllText(path, "this is not an image");
        return path;
    }

    /// <summary>A file one byte over the 5 MB photo limit, named as a PNG so the type check passes.</summary>
    public static string OversizedImage(string name = "e2e-too-big.png")
    {
        var path = Path.Combine(Folder, name);
        using var stream = File.Create(path);
        stream.SetLength(5L * 1024 * 1024 + 1);
        return path;
    }
}
