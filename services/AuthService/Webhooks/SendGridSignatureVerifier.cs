using System.Security.Cryptography;
using System.Text;

namespace AuthService.Webhooks;

/// <summary>
/// Verifies SendGrid's ECDSA-signed Event Webhook requests using the configured public key.
/// Reference: https://docs.sendgrid.com/for-developers/tracking-events/getting-started-event-webhook-security
///</summary>
public static class SendGridSignatureVerifier
{
    public static bool Verify(
        string publicKeyPem,
        string timestampHeader,
        string signatureHeader,
        string rawBody)
    {
        if (string.IsNullOrWhiteSpace(publicKeyPem)) return false;
        if (string.IsNullOrWhiteSpace(timestampHeader)) return false;
        if (string.IsNullOrWhiteSpace(signatureHeader)) return false;

        try
        {
            using var ecdsa = LoadPublicKey(publicKeyPem);
            var data = Encoding.UTF8.GetBytes(timestampHeader + rawBody);
            var signature = Convert.FromBase64String(signatureHeader);
            return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
        catch
        {
            return false;
        }
    }

    private static ECDsa LoadPublicKey(string pem)
    {
        var ecdsa = ECDsa.Create();
        // Accept both "-----BEGIN PUBLIC KEY-----" (X.509 SubjectPublicKeyInfo) and
        // "-----BEGIN EC PUBLIC KEY-----" (SEC1) formats SendGrid may serve.
        pem = pem.Trim();
        if (pem.Contains("BEGIN EC PUBLIC KEY"))
        {
            ecdsa.ImportFromPem(pem);
        }
        else
        {
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(StripPemHeaders(pem)), out _);
        }
        return ecdsa;
    }

    private static string StripPemHeaders(string pem)
    {
        var sb = new StringBuilder();
        foreach (var line in pem.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("-----")) continue;
            if (t.Length == 0) continue;
            sb.Append(t);
        }
        return sb.ToString();
    }
}
