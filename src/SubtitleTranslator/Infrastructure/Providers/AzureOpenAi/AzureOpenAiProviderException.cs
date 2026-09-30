namespace SubtitleTranslator.Infrastructure.Providers.AzureOpenAi;

public sealed class AzureOpenAiProviderException : Exception
{
    public AzureOpenAiProviderException(
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
