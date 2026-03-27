using System.Text.Json.Serialization;

namespace PiiMasker.Models;

public class MaskingConfig
{
    [JsonPropertyName("tables")]
    public List<TableConfig> Tables { get; set; } = new();
}

public class TableConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("schema")]
    public string Schema { get; set; } = "dbo";

    [JsonPropertyName("columns")]
    public List<ColumnConfig> Columns { get; set; } = new();
}

public class ColumnConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("action")]
    public ColumnAction Action { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }

    [JsonPropertyName("expression")]
    public string? Expression { get; set; }

    [JsonPropertyName("review")]
    public bool? Review { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}
