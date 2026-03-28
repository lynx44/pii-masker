using System.Text.Json;
using PiiMasker.Models;

namespace PiiMasker.Services;

public static class PatternsLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static PatternsFile Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Patterns file not found: {path}");

        string json = File.ReadAllText(path);

        PatternsFile? file;
        try
        {
            file = JsonSerializer.Deserialize<PatternsFile>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to parse patterns JSON: {ex.Message}", ex);
        }

        if (file is null)
            throw new InvalidOperationException("Patterns file deserialized to null.");

        Validate(file);
        return file;
    }

    private static void Validate(PatternsFile file)
    {
        for (int i = 0; i < file.Exact.Count; i++)
        {
            var p = file.Exact[i];

            if (string.IsNullOrWhiteSpace(p.Column))
                throw new InvalidOperationException($"Exact pattern at index {i} is missing a 'column' name.");

            ValidateActionFields(p.Action, p.Value, p.Expression, $"exact pattern '{p.Column}'");
        }

        for (int i = 0; i < file.Fuzzy.Count; i++)
        {
            var p = file.Fuzzy[i];

            if (string.IsNullOrWhiteSpace(p.Pattern))
                throw new InvalidOperationException($"Fuzzy pattern at index {i} is missing a 'pattern' value.");

            ValidateActionFields(p.Action, p.Value, p.Expression, $"fuzzy pattern '{p.Pattern}'");
        }
    }

    private static void ValidateActionFields(ColumnAction action, string? value, string? expression, string context)
    {
        if (action == ColumnAction.Replace && string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(
                $"The {context} has action 'replace' but is missing a 'value'.");

        if (action == ColumnAction.Calculate && string.IsNullOrWhiteSpace(expression))
            throw new InvalidOperationException(
                $"The {context} has action 'calculate' but is missing an 'expression'.");
    }
}
