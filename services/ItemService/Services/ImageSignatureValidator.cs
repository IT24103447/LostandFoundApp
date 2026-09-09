namespace ItemService.Services;

public static class ImageSignatureValidator
{
    // Validates that a file's actual bytes match its declared Content-Type,
    // so a spoofed header (e.g. a .exe renamed with Content-Type: image/jpeg)
    // gets rejected instead of trusted blindly.
    public static async Task<bool> MatchesDeclaredTypeAsync(IFormFile file, CancellationToken ct = default)
    {
        var header = new byte[12];
        await using var stream = file.OpenReadStream();
        var read = await stream.ReadAsync(header.AsMemory(0, header.Length), ct);

        return file.ContentType.ToLowerInvariant() switch
        {
            "image/jpeg" => read >= 3
                && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF,
            "image/png" => read >= 4
                && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47,
            "image/webp" => read >= 12
                && header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F'
                && header[8] == (byte)'W' && header[9] == (byte)'E' && header[10] == (byte)'B' && header[11] == (byte)'P',
            _ => false
        };
    }
}