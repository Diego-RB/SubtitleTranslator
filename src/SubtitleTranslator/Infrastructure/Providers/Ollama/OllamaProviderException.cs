namespace SubtitleTranslator.Infrastructure.Providers.Ollama;

public sealed class OllamaProviderException : Exception
{
    public OllamaProviderException(string message, int? statusCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    public int? StatusCode { get; }
}
