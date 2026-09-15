namespace SubtitleTranslator.Infrastructure.Providers.OpenRouter;

public sealed class OpenRouterProviderException : Exception
{
    public OpenRouterProviderException(
        string message,
        int? statusCode = null,
        bool isTransient = false,
        Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        IsTransient = isTransient;
    }

    public int? StatusCode { get; }
    public bool IsTransient { get; }
}
