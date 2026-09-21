using MatchingService.Services;

namespace MatchingService.Tests.Services;

/// <summary>
/// Not itself an acceptance-criteria scenario, but the mechanism Scenario 8's idempotency depends on:
/// the same logical photo must always normalize to the same photo_key so the database's unique
/// constraint can actually catch a redelivered event.
/// </summary>
public sealed class BlobUrlPhotoKeyGeneratorTests
{
    private readonly BlobUrlPhotoKeyGenerator _generator = new();

    // The key is a SHA-256 hex digest: fixed 64 lowercase hex characters, matching the CHAR(64) photo_key column. 
    [Fact]
    public void Create_ValidUrl_ReturnsSixtyFourCharacterLowercaseHexKey()
    {
        var (_, photoKey) = _generator.Create("https://blob.example.com/container/photo.jpg");

        Assert.Equal(64, photoKey.Length);
        Assert.Matches("^[0-9a-f]{64}$", photoKey);
    }

    /* Determinism: the same URL, given twice, must always produce the same key. This is exactly what
       the database's unique-constraint idempotency check for Scenario 8 relies on. */
    [Fact]
    public void Create_SameUrlTwice_ProducesTheSameKey()
    {
        var (_, first) = _generator.Create("https://blob.example.com/container/photo.jpg");
        var (_, second) = _generator.Create("https://blob.example.com/container/photo.jpg");

        Assert.Equal(first, second);
    }

    // Two genuinely different photos must not collide.
    [Fact]
    public void Create_DifferentUrls_ProduceDifferentKeys()
    {
        var (_, first) = _generator.Create("https://blob.example.com/container/a.jpg");
        var (_, second) = _generator.Create("https://blob.example.com/container/b.jpg");

        Assert.NotEqual(first, second);
    }

    /* Scheme and host casing must not affect the key, since they're not meaningfully different URLs.
       This avoids a redelivered event with different casing slipping past the unique constraint. */
    [Fact]
    public void Create_SameUrlDifferentSchemeAndHostCasing_ProducesTheSameKey()
    {
        var (_, first) = _generator.Create("https://blob.example.com/container/photo.jpg");
        var (_, second) = _generator.Create("HTTPS://BLOB.EXAMPLE.COM/container/photo.jpg");

        Assert.Equal(first, second);
    }

    // A trailing fragment is stripped before hashing, so it doesn't change identity. Azure Blob URLs  never use fragments meaningfully. 
    [Fact]
    public void Create_UrlWithFragment_NormalizesToSameKeyAsWithoutFragment()
    {
        var (_, withFragment) = _generator.Create("https://blob.example.com/container/photo.jpg#ignored");
        var (_, withoutFragment) = _generator.Create("https://blob.example.com/container/photo.jpg");

        Assert.Equal(withoutFragment, withFragment);
    }

    // Surrounding whitespace (e.g. from an upstream copy/paste) is trimmed before parsing.
    [Fact]
    public void Create_UrlWithSurroundingWhitespace_TrimsBeforeParsing()
    {
        var (_, trimmed) = _generator.Create("  https://blob.example.com/container/photo.jpg  ");
        var (_, clean) = _generator.Create("https://blob.example.com/container/photo.jpg");

        Assert.Equal(clean, trimmed);
    }

    /* A relative or otherwise unparseable URL is rejected. 
       This is the exact exception ImageDescriptionEventHandler catches and treats as "skip this one photo, keep going". */
    [Fact]
    public void Create_NotAnAbsoluteUrl_ThrowsInvalidDataException()
    {
        Assert.Throws<InvalidDataException>(() => _generator.Create("not-a-url"));
    }

    // A non-HTTP(S) scheme (e.g. a blob:// or file:// URI) is rejected the same way.
    [Fact]
    public void Create_NonHttpScheme_ThrowsInvalidDataException()
    {
        Assert.Throws<InvalidDataException>(() => _generator.Create("ftp://blob.example.com/photo.jpg"));
    }
}
