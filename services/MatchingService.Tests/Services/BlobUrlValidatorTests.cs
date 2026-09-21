using MatchingService.Configuration;
using MatchingService.Services;
using Microsoft.Extensions.Options;

namespace MatchingService.Tests.Services;

/// <summary>
/// Not itself an acceptance-criteria scenario, but real security-relevant logic behind Scenario 4/7:
/// this is the SSRF-prevention gate on every blob URL before it's ever handed to Gemini, so it needs
/// its own direct coverage rather than only being exercised incidentally through other tests.
/// </summary>
public sealed class BlobUrlValidatorTests
{
    private const string AllowedHost = "lostfoundblob.blob.core.windows.net";

    private static BlobUrlValidator BuildValidator(string? allowedHost = AllowedHost) =>
        new(Options.Create(new BlobStorageSettings { AllowedHost = allowedHost ?? string.Empty }));

    // The happy path: HTTPS, default port, matching host, a real blob path, nothing extra.
    [Fact]
    public void Validate_WellFormedUrlOnAllowedHost_ReturnsUri()
    {
        var uri = BuildValidator().Validate($"https://{AllowedHost}/container/photo.jpg");

        Assert.Equal(AllowedHost, uri.IdnHost);
    }

    // Host matching is case-insensitive, since DNS hostnames are.
    [Fact]
    public void Validate_HostDiffersOnlyByCase_ReturnsUri()
    {
        var uri = BuildValidator().Validate($"https://{AllowedHost.ToUpperInvariant()}/container/photo.jpg");

        Assert.NotNull(uri);
    }

    // If AllowedHost isn't configured at all, nothing can ever validate. It fails closed, not open.
    [Fact]
    public void Validate_AllowedHostNotConfigured_ThrowsHostNotConfigured()
    {
        var exception = Assert.Throws<ImageProcessingException>(
            () => BuildValidator(allowedHost: "").Validate($"https://{AllowedHost}/container/photo.jpg"));

        Assert.Equal("BLOB_HOST_NOT_CONFIGURED", exception.Code);
        Assert.False(exception.Retryable);
    }

    // Plain HTTP is rejected. Only HTTPS blob URLs are ever accepted.
    [Fact]
    public void Validate_HttpScheme_ThrowsUrlInvalid()
    {
        var exception = Assert.Throws<ImageProcessingException>(
            () => BuildValidator().Validate($"http://{AllowedHost}/container/photo.jpg"));

        Assert.Equal("BLOB_URL_INVALID", exception.Code);
    }

    // A non-default port is rejected, narrowing the accepted shape to exactly what Azure Blob's public HTTPS endpoint looks like. 
    [Fact]
    public void Validate_NonDefaultPort_ThrowsUrlInvalid()
    {
        Assert.Throws<ImageProcessingException>(
            () => BuildValidator().Validate($"https://{AllowedHost}:8443/container/photo.jpg"));
    }

    // Embedded credentials in the URL itself are rejected outright.
    [Fact]
    public void Validate_UrlWithUserInfo_ThrowsUrlInvalid()
    {
        Assert.Throws<ImageProcessingException>(
            () => BuildValidator().Validate($"https://user:pass@{AllowedHost}/container/photo.jpg"));
    }

    /* Critical case: a SAS-token URL (query string) is rejected. 
       Item Service's blob container is anonymously readable at the blob level specifically so plain URLs like this work. A signed URL
       must never be expected to pass this validator. */
    [Fact]
    public void Validate_UrlWithQueryString_ThrowsUrlInvalid()
    {
        Assert.Throws<ImageProcessingException>(
            () => BuildValidator().Validate($"https://{AllowedHost}/container/photo.jpg?sig=abc123"));
    }

    [Fact]
    public void Validate_UrlWithFragment_ThrowsUrlInvalid()
    {
        Assert.Throws<ImageProcessingException>(
            () => BuildValidator().Validate($"https://{AllowedHost}/container/photo.jpg#section"));
    }

    // A bare root URL with no blob path at all isn't a usable photo URL.
    [Fact]
    public void Validate_RootPathOnly_ThrowsUrlInvalid()
    {
        Assert.Throws<ImageProcessingException>(() => BuildValidator().Validate($"https://{AllowedHost}/"));
    }

    // A relative or otherwise malformed string can't be parsed as an absolute URL at all.
    [Fact]
    public void Validate_NotAnAbsoluteUrl_ThrowsUrlInvalid()
    {
        Assert.Throws<ImageProcessingException>(() => BuildValidator().Validate("not-a-url"));
    }

    /* The SSRF-prevention core: a well-formed HTTPS URL on a *different* host than configured is
       rejected even though it's otherwise perfectly valid. */
    [Fact]
    public void Validate_HostNotAllowed_ThrowsHostNotAllowed()
    {
        var exception = Assert.Throws<ImageProcessingException>(
            () => BuildValidator().Validate("https://attacker.example.com/container/photo.jpg"));

        Assert.Equal("BLOB_HOST_NOT_ALLOWED", exception.Code);
    }
}
