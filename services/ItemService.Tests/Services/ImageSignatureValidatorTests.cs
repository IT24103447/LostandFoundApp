using ItemService.Services;
using Microsoft.AspNetCore.Http;

public class ImageSignatureValidatorTests
{
    [Theory]
    [InlineData("image/jpeg", "image.jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 })]
    [InlineData("image/png", "image.png", new byte[] { 0x89, 0x50, 0x4E, 0x47 })]
    [InlineData("image/webp", "image.webp", new byte[] { 0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50 })]
    public async Task MatchesDeclaredTypeAsync_ValidSignature_ReturnsTrue(string contentType, string fileName, byte[] bytes)
    {
        var file = CreateFile(contentType, fileName, bytes);

        var result = await ImageSignatureValidator.MatchesDeclaredTypeAsync(file);

        Assert.True(result);
    }

    [Theory]
    [InlineData("image/jpeg", "forged.jpg", new byte[] { 0x00, 0x01, 0x02, 0x03 })]
    [InlineData("image/png", "truncated.png", new byte[] { 0x89, 0x50, 0x4E })]
    [InlineData("image/webp", "truncated.webp", new byte[] { 0x52, 0x49, 0x46, 0x46 })]
    [InlineData("application/pdf", "document.pdf", new byte[] { 0x25, 0x50, 0x44, 0x46 })]
    public async Task MatchesDeclaredTypeAsync_ForgedOrUnsupportedFile_ReturnsFalse(string contentType, string fileName, byte[] bytes)
    {
        var file = CreateFile(contentType, fileName, bytes);

        var result = await ImageSignatureValidator.MatchesDeclaredTypeAsync(file);

        Assert.False(result);
    }

    private static IFormFile CreateFile(string contentType, string fileName, byte[] bytes)
    {
        var stream = new MemoryStream(bytes);
        var file = new FormFile(stream, 0, bytes.Length, "Photos", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType
        };
        return file;
    }
}
