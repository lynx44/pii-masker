using System.Text.Json.Serialization;

namespace PiiMasker.Models;

public class PatternsFile
{
    [JsonPropertyName("exact")]
    public List<ExactPattern> Exact { get; set; } = new();

    [JsonPropertyName("fuzzy")]
    public List<FuzzyPattern> Fuzzy { get; set; } = new();
}

/// <summary>
/// Matches a column by exact name (case-insensitive).
/// </summary>
public class ExactPattern
{
    [JsonPropertyName("column")]
    public string Column { get; set; } = string.Empty;

    [JsonPropertyName("action")]
    public ColumnAction Action { get; set; }

    /// <summary>Required when action is "replace".</summary>
    [JsonPropertyName("value")]
    public string? Value { get; set; }

    /// <summary>Required when action is "calculate".</summary>
    [JsonPropertyName("expression")]
    public string? Expression { get; set; }
}

/// <summary>
/// Matches any column whose name contains the pattern substring (case-insensitive).
/// Matched columns are flagged with "review": true in the output.
/// </summary>
public class FuzzyPattern
{
    [JsonPropertyName("pattern")]
    public string Pattern { get; set; } = string.Empty;

    [JsonPropertyName("action")]
    public ColumnAction Action { get; set; }

    /// <summary>Required when action is "replace".</summary>
    [JsonPropertyName("value")]
    public string? Value { get; set; }

    /// <summary>Required when action is "calculate".</summary>
    [JsonPropertyName("expression")]
    public string? Expression { get; set; }
}
