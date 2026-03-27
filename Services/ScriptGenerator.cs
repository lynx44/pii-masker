using System.Text;
using PiiMasker.Models;

namespace PiiMasker.Services;

public static class ScriptGenerator
{
    public static (string sql, int tableCount, int columnCount) Generate(MaskingConfig config, string connectionString)
    {
        var sb = new StringBuilder();
        int totalColumns = 0;

        sb.AppendLine("/*");
        sb.AppendLine($"  PII Masking Script");
        sb.AppendLine($"  Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"  Tables: {config.Tables.Count}");
        sb.AppendLine("*/");
        sb.AppendLine();
        sb.AppendLine("SET NOCOUNT ON;");
        sb.AppendLine("SET XACT_ABORT ON;");
        sb.AppendLine();
        sb.AppendLine("BEGIN TRANSACTION;");
        sb.AppendLine();

        foreach (var table in config.Tables)
        {
            string fullName = $"[{table.Schema}].[{table.Name}]";

            sb.AppendLine("-- " + new string('=', 70));
            sb.AppendLine($"-- Table: {fullName}");
            sb.AppendLine("-- " + new string('=', 70));
            sb.AppendLine();
            sb.AppendLine($"PRINT 'Processing {fullName}...';");
            sb.AppendLine($"PRINT 'Row count before: ' + CAST((SELECT COUNT(*) FROM {fullName}) AS VARCHAR);");
            sb.AppendLine();

            // Group columns by action type for efficient generation
            var shuffleColumns = table.Columns.Where(c => c.Action == ColumnAction.Shuffle).ToList();
            var replaceColumns = table.Columns.Where(c => c.Action == ColumnAction.Replace).ToList();
            var calculateColumns = table.Columns.Where(c => c.Action == ColumnAction.Calculate).ToList();

            // Generate shuffle operations
            foreach (var col in shuffleColumns)
            {
                GenerateShuffle(sb, table, col, connectionString);
                totalColumns++;
            }

            // Batch replace columns into a single UPDATE if possible
            if (replaceColumns.Count > 0)
            {
                GenerateReplace(sb, table, replaceColumns);
                totalColumns += replaceColumns.Count;
            }

            // Batch calculate columns into a single UPDATE if possible
            if (calculateColumns.Count > 0)
            {
                GenerateCalculate(sb, table, calculateColumns);
                totalColumns += calculateColumns.Count;
            }

            sb.AppendLine($"PRINT 'Row count after: ' + CAST((SELECT COUNT(*) FROM {fullName}) AS VARCHAR);");
            sb.AppendLine($"PRINT '{fullName} complete.';");
            sb.AppendLine($"PRINT '';");
            sb.AppendLine();
        }

        sb.AppendLine("-- Uncomment ROLLBACK and comment COMMIT to test without persisting changes");
        sb.AppendLine("-- ROLLBACK TRANSACTION;");
        sb.AppendLine("COMMIT TRANSACTION;");
        sb.AppendLine();
        sb.AppendLine($"PRINT 'Masking complete. {config.Tables.Count} table(s), {totalColumns} column(s) processed.';");

        return (sb.ToString(), config.Tables.Count, totalColumns);
    }

    private static void GenerateShuffle(StringBuilder sb, TableConfig table, ColumnConfig col, string connectionString)
    {
        string fullName = $"[{table.Schema}].[{table.Name}]";
        string pkColumn = GetPrimaryKeyColumnName(table, connectionString);

        sb.AppendLine($"-- Shuffle: {col.Name}");
        sb.AppendLine($"WITH Shuffled AS (");
        sb.AppendLine($"  SELECT");
        sb.AppendLine($"    ROW_NUMBER() OVER (ORDER BY NEWID()) AS rn,");
        sb.AppendLine($"    [{col.Name}]");
        sb.AppendLine($"  FROM {fullName}");
        sb.AppendLine($"),");
        sb.AppendLine($"Original AS (");
        sb.AppendLine($"  SELECT");
        sb.AppendLine($"    ROW_NUMBER() OVER (ORDER BY NEWID()) AS rn,");
        sb.AppendLine($"    [{pkColumn}]");
        sb.AppendLine($"  FROM {fullName}");
        sb.AppendLine($")");
        sb.AppendLine($"UPDATE a");
        sb.AppendLine($"SET a.[{col.Name}] = s.[{col.Name}]");
        sb.AppendLine($"FROM {fullName} a");
        sb.AppendLine($"JOIN Original o ON a.[{pkColumn}] = o.[{pkColumn}]");
        sb.AppendLine($"JOIN Shuffled s ON o.rn = s.rn;");
        sb.AppendLine();
    }

    private static void GenerateReplace(StringBuilder sb, TableConfig table, List<ColumnConfig> columns)
    {
        string fullName = $"[{table.Schema}].[{table.Name}]";

        sb.AppendLine($"-- Replace columns");
        sb.AppendLine($"UPDATE {fullName}");
        sb.AppendLine("SET");

        for (int i = 0; i < columns.Count; i++)
        {
            string comma = i < columns.Count - 1 ? "," : "";
            sb.AppendLine($"  [{columns[i].Name}] = {columns[i].Value}{comma}");
        }

        sb.AppendLine(";");
        sb.AppendLine();
    }

    private static void GenerateCalculate(StringBuilder sb, TableConfig table, List<ColumnConfig> columns)
    {
        string fullName = $"[{table.Schema}].[{table.Name}]";

        sb.AppendLine($"-- Calculate columns");
        sb.AppendLine($"UPDATE {fullName}");
        sb.AppendLine("SET");

        for (int i = 0; i < columns.Count; i++)
        {
            string comma = i < columns.Count - 1 ? "," : "";
            sb.AppendLine($"  [{columns[i].Name}] = {columns[i].Expression}{comma}");
        }

        sb.AppendLine(";");
        sb.AppendLine();
    }

    private static string GetPrimaryKeyColumnName(TableConfig table, string connectionString)
    {
        // If we have a connection string, query for the actual PK
        if (!string.IsNullOrEmpty(connectionString))
        {
            try
            {
                using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
                connection.Open();

                using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT c.COLUMN_NAME
                    FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                    JOIN INFORMATION_SCHEMA.CONSTRAINT_COLUMN_USAGE c
                        ON tc.CONSTRAINT_NAME = c.CONSTRAINT_NAME
                        AND tc.TABLE_SCHEMA = c.TABLE_SCHEMA
                    WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
                        AND tc.TABLE_SCHEMA = @Schema
                        AND tc.TABLE_NAME = @Table";
                cmd.Parameters.AddWithValue("@Schema", table.Schema);
                cmd.Parameters.AddWithValue("@Table", table.Name);

                var result = cmd.ExecuteScalar();
                if (result is string pk)
                    return pk;
            }
            catch
            {
                // Fall through to convention-based naming
            }
        }

        // Convention: try TableNameId, then Id
        return $"{table.Name}Id";
    }
}
