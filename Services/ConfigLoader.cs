using System.Text.Json;
using PiiMasker.Models;

namespace PiiMasker.Services;

public static class ConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static MaskingConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Config file not found: {path}");
        }

        string json = File.ReadAllText(path);

        MaskingConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<MaskingConfig>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to parse config JSON: {ex.Message}", ex);
        }

        if (config is null)
        {
            throw new InvalidOperationException("Config file deserialized to null.");
        }

        Validate(config);
        return config;
    }

    private static void Validate(MaskingConfig config)
    {
        if (config.Tables.Count == 0)
        {
            throw new InvalidOperationException("Config must contain at least one table.");
        }

        for (int t = 0; t < config.Tables.Count; t++)
        {
            var table = config.Tables[t];

            if (string.IsNullOrWhiteSpace(table.Name))
                throw new InvalidOperationException($"Table at index {t} is missing a 'name'.");

            if (table.Columns.Count == 0)
                throw new InvalidOperationException($"Table '{table.Schema}.{table.Name}' has no columns defined.");

            for (int c = 0; c < table.Columns.Count; c++)
            {
                var col = table.Columns[c];

                if (string.IsNullOrWhiteSpace(col.Name))
                    throw new InvalidOperationException(
                        $"Column at index {c} in table '{table.Schema}.{table.Name}' is missing a 'name'.");

                switch (col.Action)
                {
                    case ColumnAction.Replace:
                        if (string.IsNullOrWhiteSpace(col.Value))
                            throw new InvalidOperationException(
                                $"Column '{col.Name}' in table '{table.Schema}.{table.Name}' has action 'replace' but is missing a 'value'.");
                        break;

                    case ColumnAction.Calculate:
                        if (string.IsNullOrWhiteSpace(col.Expression))
                            throw new InvalidOperationException(
                                $"Column '{col.Name}' in table '{table.Schema}.{table.Name}' has action 'calculate' but is missing an 'expression'.");
                        break;

                    case ColumnAction.Shuffle:
                        break;
                }
            }
        }
    }
}
