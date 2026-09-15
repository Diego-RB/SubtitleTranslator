using SubtitleTranslator.Domain.ValueObjects;

namespace SubtitleTranslator.Srt.Validation;

public sealed class SrtValidator
{
    public SrtValidationResult Validate(
        IReadOnlyList<Subtitle> source,
        IReadOnlyList<Subtitle> translated)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(translated);

        var errors = new List<string>();
        if (source.Count != translated.Count)
        {
            errors.Add($"Subtitle count differs: source={source.Count}, translated={translated.Count}.");
        }

        var count = Math.Min(source.Count, translated.Count);
        for (var i = 0; i < count; i++)
        {
            var expected = source[i];
            var actual = translated[i];
            if (actual.Index != expected.Index)
            {
                errors.Add($"Subtitle position {i + 1} has index {actual.Index}; expected {expected.Index}.");
            }

            if (actual.Start != expected.Start || actual.End != expected.End)
            {
                errors.Add($"Subtitle {expected.Index} timestamps were changed.");
            }

            if (string.IsNullOrWhiteSpace(actual.Text))
            {
                errors.Add($"Subtitle {expected.Index} has empty text.");
            }
        }

        var duplicateIndexes = translated.GroupBy(subtitle => subtitle.Index)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);
        foreach (var index in duplicateIndexes)
        {
            errors.Add($"Subtitle index {index} is duplicated.");
        }

        if (translated.Select(subtitle => subtitle.Index).Distinct().Count() != translated.Count)
        {
            errors.Add("Translated subtitles contain duplicate indexes.");
        }

        for (var i = 1; i < translated.Count; i++)
        {
            if (translated[i - 1].Index >= translated[i].Index)
            {
                errors.Add("Translated subtitle indexes are not in ascending order.");
                break;
            }
        }

        return errors.Count == 0 ? SrtValidationResult.Valid() : SrtValidationResult.Invalid(errors);
    }
}
