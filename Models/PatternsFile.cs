using System.Text.Json.Serialization;

namespace PiiMasker.Models;

public class PatternsFile
{
    [JsonPropertyName("exact")]
    public List<ExactPattern> Exact { get; set; } = new();

    [JsonPropertyName("fuzzy")]
    public List<FuzzyPattern> Fuzzy { get; set; } = new();

    [JsonPropertyName("ignore")]
    public List<IgnorePattern> Ignore { get; set; } = new();
}

/// <summary>
/// Excludes a column from scanning entirely — it will not be matched by any
/// built-in or user-defined exact/fuzzy pattern, so it never appears in the output.
/// </summary>
public class IgnorePattern
{
    [JsonPropertyName("column")]
    public string Column { get; set; } = string.Empty;

    /// <summary>
    /// Optional table name. When set, the column is ignored only in that table.
    /// When omitted, the column is ignored in every table.
    /// </summary>
    [JsonPropertyName("table")]
    public string? Table { get; set; }
}

/// <summary>
/// Matches a column by exact name (case-insensitive).
/// </summary>
public class ExactPattern
{
    [JsonPropertyName("column")]
    public string Column { get; set; } = string.Empty;

    /// <summary>
    /// Optional table name. When set, the pattern applies only to that table.
    /// When omitted, it applies to any column with this name. A table-scoped
    /// entry takes precedence over a global one for the same column.
    /// </summary>
    [JsonPropertyName("table")]
    public string? Table { get; set; }

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

    /// <summary>
    /// Optional table name. When set, the pattern applies only to that table.
    /// When omitted, it applies to every table.
    /// </summary>
    [JsonPropertyName("table")]
    public string? Table { get; set; }

    [JsonPropertyName("action")]
    public ColumnAction Action { get; set; }

    /// <summary>Required when action is "replace".</summary>
    [JsonPropertyName("value")]
    public string? Value { get; set; }

    /// <summary>Required when action is "calculate".</summary>
    [JsonPropertyName("expression")]
    public string? Expression { get; set; }
}
