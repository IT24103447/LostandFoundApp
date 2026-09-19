using MatchingService.Configuration;
using Microsoft.Extensions.Options;

namespace MatchingService.Services;

public sealed class BlobUrlValidator
{
    private readonly BlobStorageSettings _settings;

    public BlobUrlValidator(IOptions<BlobStorageSettings> settings)
    {
        _settings = settings.Value;
    }

    public Uri Validate(string blobUrl)
    {
        var allowedHost = _settings.AllowedHost.Trim();

        if (string.IsNullOrWhiteSpace(allowedHost))
        {
            throw new ImageProcessingException(
                "BLOB_HOST_NOT_CONFIGURED",
                retryable: false);
        }

        if (!Uri.TryCreate(blobUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath == "/")
        {
            throw new ImageProcessingException(
                "BLOB_URL_INVALID",
                retryable: false);
        }

        if (!string.Equals(
                uri.IdnHost,
                allowedHost,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ImageProcessingException(
                "BLOB_HOST_NOT_ALLOWED",
                retryable: false);
        }

        return uri;
    }
}