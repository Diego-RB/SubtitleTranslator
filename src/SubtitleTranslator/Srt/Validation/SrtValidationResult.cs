namespace SubtitleTranslator.Srt.Validation;

public sealed class SrtValidationResult
{
    private SrtValidationResult(bool isValid, IReadOnlyList<string> errors)
    {
        IsValid = isValid;
        Errors = errors;
    }

    public bool IsValid { get; }
    public IReadOnlyList<string> Errors { get; }

    public static SrtValidationResult Valid() => new(true, []);

    public static SrtValidationResult Invalid(IEnumerable<string> errors) =>
        new(false, errors.Where(error => !string.IsNullOrWhiteSpace(error)).ToArray());
}
