namespace MatchingService.Services;

public sealed class ImageProcessingException : Exception
{
    public string Code { get; }

    public bool Retryable { get; }

    public ImageProcessingException(
        string code,
        bool retryable)
        : base(code)
    {
        Code = code;
        Retryable = retryable;
    }
}