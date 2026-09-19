using System.Security.Cryptography;
using System.Text;

namespace MatchingService.Services;

public sealed class BlobUrlPhotoKeyGenerator
{
    public (string NormalizedUrl, string PhotoKey) Create(string blobUrl)
    {
        if (!Uri.TryCreate(blobUrl.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidDataException("The photo URL must be an absolute HTTP or HTTPS URL.");
        }

        var normalized = new UriBuilder(uri)
        {
            Scheme = uri.Scheme.ToLowerInvariant(),
            Host = uri.IdnHost.ToLowerInvariant(),
            Fragment = string.Empty
        }.Uri.AbsoluteUri;

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return (normalized, Convert.ToHexString(digest).ToLowerInvariant());
    }
}
