using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using PiiMasker.Models;

namespace PiiMasker.Services;

public static class Scanner
{
    private static readonly Dictionary<string, (ColumnAction action, string? value, string? expression, string category)> ExactMatches = new(StringComparer.OrdinalIgnoreCase)
    {
        // Shuffle candidates
        ["firstname"]     = (ColumnAction.Shuffle, null, null, "name"),
        ["first_name"]    = (ColumnAction.Shuffle, null, null, "name"),
        ["lastname"]      = (ColumnAction.Shuffle, null, null, "name"),
        ["last_name"]     = (ColumnAction.Shuffle, null, null, "name"),
        ["fullname"]      = (ColumnAction.Shuffle, null, null, "name"),
        ["full_name"]     = (ColumnAction.Shuffle, null, null, "name"),
        ["displayname"]   = (ColumnAction.Shuffle, null, null, "name"),
        ["display_name"]  = (ColumnAction.Shuffle, null, null, "name"),
        ["middlename"]    = (ColumnAction.Shuffle, null, null, "name"),
        ["middle_name"]   = (ColumnAction.Shuffle, null, null, "name"),

        // Email
        ["email"]          = (ColumnAction.Replace, null, null, "email"),
        ["emailaddress"]   = (ColumnAction.Replace, null, null, "email"),
        ["email_address"]  = (ColumnAction.Replace, null, null, "email"),

        // Phone
        ["phone"]          = (ColumnAction.Replace, "'555-000-0000'", null, "phone"),
        ["phonenumber"]    = (ColumnAction.Replace, "'555-000-0000'", null, "phone"),
        ["phone_number"]   = (ColumnAction.Replace, "'555-000-0000'", null, "phone"),
        ["mobile"]         = (ColumnAction.Replace, "'555-000-0000'", null, "phone"),
        ["cell"]           = (ColumnAction.Replace, "'555-000-0000'", null, "phone"),
        ["cellphone"]      = (ColumnAction.Replace, "'555-000-0000'", null, "phone"),
        ["fax"]            = (ColumnAction.Replace, "'555-000-0000'", null, "phone"),

        // Null-out sensitive fields
        ["address"]         = (ColumnAction.Replace, "NULL", null, "address"),
        ["streetaddress"]   = (ColumnAction.Replace, "NULL", null, "address"),
        ["street_address"]  = (ColumnAction.Replace, "NULL", null, "address"),
        ["address1"]        = (ColumnAction.Replace, "NULL", null, "address"),
        ["address2"]        = (ColumnAction.Replace, "NULL", null, "address"),
        ["addressline1"]    = (ColumnAction.Replace, "NULL", null, "address"),
        ["addressline2"]    = (ColumnAction.Replace, "NULL", null, "address"),
        ["ssn"]             = (ColumnAction.Replace, "NULL", null, "government_id"),
        ["socialsecurity"]  = (ColumnAction.Replace, "NULL", null, "government_id"),
        ["social_security"] = (ColumnAction.Replace, "NULL", null, "government_id"),
        ["taxid"]           = (ColumnAction.Replace, "NULL", null, "government_id"),
        ["tax_id"]          = (ColumnAction.Replace, "NULL", null, "government_id"),

        // Date of birth
        ["dob"]           = (ColumnAction.Calculate, null, null, "dob"),
        ["dateofbirth"]   = (ColumnAction.Calculate, null, null, "dob"),
        ["date_of_birth"] = (ColumnAction.Calculate, null, null, "dob"),
        ["birthdate"]     = (ColumnAction.Calculate, null, null, "dob"),
        ["birth_date"]    = (ColumnAction.Calculate, null, null, "dob"),
    };

    // Internal unified fuzzy pattern record
    private readonly record struct FuzzyMatch(string Pattern, ColumnAction Action, string? Value, string? Expression);

    // Fuzzy patterns: if a column name contains any of these substrings, flag for review
    private static readonly FuzzyMatch[] BuiltInFuzzyPatterns =
    {
        new("name",    ColumnAction.Shuffle,  null,            null),
        new("email",   ColumnAction.Replace,  null,            null),   // value resolved at runtime using PK
        new("phone",   ColumnAction.Replace,  "'555-000-0000'", null),
        new("mobile",  ColumnAction.Replace,  "'555-000-0000'", null),
        new("addr",    ColumnAction.Replace,  "NULL",          null),
        new("street",  ColumnAction.Replace,  "NULL",          null),
        new("ssn",     ColumnAction.Replace,  "NULL",          null),
        new("birth",   ColumnAction.Calculate, null,           null),   // expression resolved at runtime
        new("salary",  ColumnAction.Replace,  "NULL",          null),
        new("wage",    ColumnAction.Replace,  "NULL",          null),
        new("bank",    ColumnAction.Replace,  "NULL",          null),
        new("account", ColumnAction.Replace,  "NULL",          null),
    };

    public static async Task<MaskingConfig> ScanAsync(string connectionString, PatternsFile? extraPatterns = null)
    {
        // Merge extra exact patterns — user-supplied entries override built-ins on name collision
        var effectiveExact = new Dictionary<string, (ColumnAction action, string? value, string? expression, string category)>(ExactMatches, StringComparer.OrdinalIgnoreCase);
        if (extraPatterns?.Exact.Count > 0)
        {
            foreach (var p in extraPatterns.Exact)
                effectiveExact[p.Column] = (p.Action, p.Value, p.Expression, "custom");
        }

        // Extra fuzzy patterns are checked before built-ins so they can take precedence
        FuzzyMatch[] effectiveFuzzy = extraPatterns?.Fuzzy.Count > 0
            ? extraPatterns.Fuzzy
                .Select(p => new FuzzyMatch(p.Pattern, p.Action, p.Value, p.Expression))
                .Concat(BuiltInFuzzyPatterns)
                .ToArray()
            : BuiltInFuzzyPatterns;

        var config = new MaskingConfig();

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        // Get all user tables
        await using var tableCmd = connection.CreateCommand();
        tableCmd.CommandText = @"
            SELECT TABLE_SCHEMA, TABLE_NAME
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_TYPE = 'BASE TABLE'
            ORDER BY TABLE_SCHEMA, TABLE_NAME";

        var tables = new List<(string schema, string name)>();
        await using (var reader = await tableCmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                tables.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        foreach (var (schema, tableName) in tables)
        {
            // Get PK column name for email replacement pattern
            string pkColumn = await GetPrimaryKeyAsync(connection, schema, tableName) ?? $"{tableName}Id";

            await using var colCmd = connection.CreateCommand();
            colCmd.CommandText = @"
                SELECT COLUMN_NAME, DATA_TYPE
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = @Schema AND TABLE_NAME = @Table
                ORDER BY ORDINAL_POSITION";
            colCmd.Parameters.AddWithValue("@Schema", schema);
            colCmd.Parameters.AddWithValue("@Table", tableName);

            var tableConfig = new TableConfig { Name = tableName, Schema = schema };

            await using var colReader = await colCmd.ExecuteReaderAsync();
            while (await colReader.ReadAsync())
            {
                string colName = colReader.GetString(0);
                string dataType = colReader.GetString(1);

                var colConfig = ClassifyColumn(colName, dataType, pkColumn, effectiveExact, effectiveFuzzy);
                if (colConfig != null)
                {
                    tableConfig.Columns.Add(colConfig);
                }
            }

            if (tableConfig.Columns.Count > 0)
            {
                config.Tables.Add(tableConfig);
            }
        }

        return config;
    }

    private static ColumnConfig? ClassifyColumn(
        string colName,
        string dataType,
        string pkColumn,
        Dictionary<string, (ColumnAction action, string? value, string? expression, string category)> exactMatches,
        FuzzyMatch[] fuzzyPatterns)
    {
        // Check exact matches first
        if (exactMatches.TryGetValue(colName, out var match))
        {
            var col = new ColumnConfig
            {
                Name = colName,
                Action = match.action
            };

            // Built-in special categories resolve value/expression dynamically using the PK column
            switch (match.category)
            {
                case "email":
                    col.Value = $"CONCAT('user_', CAST([{pkColumn}] AS VARCHAR), '@dev.invalid')";
                    break;
                case "dob":
                    col.Expression = $"DATEADD(day, (ABS(CHECKSUM(NEWID())) % 365) - 182, [{colName}])";
                    break;
                default:
                    col.Value = match.value;
                    col.Expression = match.expression;
                    break;
            }

            return col;
        }

        // Check fuzzy patterns — flag for review
        string normalizedName = colName.ToLowerInvariant();
        foreach (var fuzzy in fuzzyPatterns)
        {
            if (!normalizedName.Contains(fuzzy.Pattern, StringComparison.OrdinalIgnoreCase))
                continue;

            // Skip likely foreign key / ID columns
            if (normalizedName.EndsWith("id") || normalizedName.EndsWith("_id"))
                return null;

            var col = new ColumnConfig
            {
                Name = colName,
                Action = fuzzy.Action,
                Review = true,
                Reason = $"Column name contains '{fuzzy.Pattern}' — may contain PII"
            };

            // If fuzzy.Value/Expression are null this is a built-in pattern; resolve dynamically
            if (fuzzy.Value is null && fuzzy.Expression is null)
            {
                switch (fuzzy.Pattern)
                {
                    case "email":
                        col.Value = $"CONCAT('user_', CAST([{pkColumn}] AS VARCHAR), '@dev.invalid')";
                        break;
                    case "birth":
                        col.Expression = $"DATEADD(day, (ABS(CHECKSUM(NEWID())) % 365) - 182, [{colName}])";
                        break;
                    default:
                        // Shuffle needs no value/expression
                        break;
                }
            }
            else
            {
                col.Value = fuzzy.Value;
                col.Expression = fuzzy.Expression;
            }

            return col;
        }

        return null;
    }

    private static async Task<string?> GetPrimaryKeyAsync(SqlConnection connection, string schema, string tableName)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT c.COLUMN_NAME
            FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
            JOIN INFORMATION_SCHEMA.CONSTRAINT_COLUMN_USAGE c
                ON tc.CONSTRAINT_NAME = c.CONSTRAINT_NAME
                AND tc.TABLE_SCHEMA = c.TABLE_SCHEMA
            WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
                AND tc.TABLE_SCHEMA = @Schema
                AND tc.TABLE_NAME = @Table";
        cmd.Parameters.AddWithValue("@Schema", schema);
        cmd.Parameters.AddWithValue("@Table", tableName);

        var result = await cmd.ExecuteScalarAsync();
        return result as string;
    }

    public static string SerializeConfig(MaskingConfig config)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        return JsonSerializer.Serialize(config, options);
    }
}
