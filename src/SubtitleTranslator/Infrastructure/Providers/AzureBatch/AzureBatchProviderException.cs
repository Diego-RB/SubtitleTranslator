namespace SubtitleTranslator.Infrastructure.Providers.AzureBatch;

public sealed class AzureBatchProviderException(
    string message,
    int? statusCode = null,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    public int? StatusCode { get; } = statusCode;
}
