using System.Text;
using Microsoft.Data.SqlClient;
using PiiMasker.Models;

namespace PiiMasker.Services;

public static class ScriptGenerator
{
    /// <summary>
    /// Generates a T-SQL masking script.
    /// </summary>
    /// <param name="batchSize">
    /// When &gt; 0 and a table has a single integer primary key, large UPDATEs are
    /// chunked into primary-key ranges of this many rows to keep the transaction
    /// log and lock footprint small. Set to 0 to disable chunking.
    /// </param>
    /// <param name="rebuildIndexes">
    /// When true, nonclustered indexes on large tables are disabled before the
    /// updates and rebuilt afterwards. The clustered index, primary key, and
    /// unique constraints are always left in place.
    /// </param>
    public static (string sql, int tableCount, int columnCount) Generate(
        MaskingConfig config,
        string connectionString,
        int batchSize = 50_000,
        bool rebuildIndexes = false)
    {
        using var connection = TryOpenConnection(connectionString);

        var sb = new StringBuilder();
        int totalColumns = 0;
        int tableCount = 0;

        sb.AppendLine("/*");
        sb.AppendLine("  PII Masking Script");
        sb.AppendLine($"  Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"  Tables: {config.Tables.Count}");
        sb.AppendLine($"  Chunking: {(batchSize > 0 ? $"{batchSize:N0} rows/batch (tables with a single integer PK)" : "disabled")}");
        sb.AppendLine($"  Index rebuild: {(rebuildIndexes ? "enabled (large tables)" : "disabled")}");
        if (connection is null)
        {
            sb.AppendLine("  NOTE: no connection supplied — primary keys are guessed by convention");
            sb.AppendLine("        and chunking/index handling are skipped. Pass --connection to enable them.");
        }
        sb.AppendLine("*/");
        sb.AppendLine();
        sb.AppendLine("SET NOCOUNT ON;");
        // No wrapping transaction: each statement autocommits so the log can be
        // truncated continuously instead of growing to hold every table's changes
        // at once. Safe here because masking always runs against backups/dev copies.
        sb.AppendLine();
        sb.AppendLine("DECLARE @rc INT;");
        sb.AppendLine("DECLARE @lo BIGINT, @hi BIGINT;");
        sb.AppendLine($"DECLARE @bs INT = {Math.Max(batchSize, 0)};");
        sb.AppendLine("DECLARE @disable NVARCHAR(MAX);");
        sb.AppendLine();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in config.Tables)
        {
            string fullName = $"[{table.Schema}].[{table.Name}]";

            if (!seen.Add(fullName))
            {
                sb.AppendLine($"-- Skipped duplicate table {fullName}");
                sb.AppendLine();
                continue;
            }

            var meta = GetTableMeta(connection, table);
            totalColumns += GenerateTable(sb, table, meta, batchSize, rebuildIndexes, tableCount);
            tableCount++;
        }

        sb.AppendLine($"PRINT 'Masking complete. {tableCount} table(s), {totalColumns} column(s) processed.';");

        return (sb.ToString(), tableCount, totalColumns);
    }

    private static int GenerateTable(
        StringBuilder sb, TableConfig table, TableMeta meta, int batchSize, bool rebuildIndexes, int tableIndex)
    {
        string fullName = $"[{table.Schema}].[{table.Name}]";
        string pk = meta.PkColumn;

        // Each table gets its own temp-table names. The whole script is a single
        // batch (no GO), and SQL Server fails at compile time if the same temp
        // table is created via SELECT...INTO more than once in one batch — even
        // with a DROP TABLE IF EXISTS between the two creations.
        string valsTbl = $"#vals{tableIndex}";
        string origTbl = $"#orig{tableIndex}";

        var shuffleColumns = table.Columns.Where(c => c.Action == ColumnAction.Shuffle).ToList();

        // Replace and Calculate are both just "column = <scalar expression>", so they
        // can be applied in a single UPDATE pass instead of two.
        var directColumns = table.Columns
            .Where(c => c.Action is ColumnAction.Replace or ColumnAction.Calculate)
            .Select(c => (c.Name, Expr: c.Action == ColumnAction.Replace ? c.Value! : c.Expression!))
            .ToList();

        bool hasShuffle = shuffleColumns.Count > 0;
        bool hasDirect = directColumns.Count > 0;

        // Chunk only when we can prove the table has a single integer PK (range
        // arithmetic on @lo/@hi requires it) and it is big enough to be worth it.
        bool large = meta.EstimatedRows < 0 || meta.EstimatedRows > batchSize;
        bool useLoop = batchSize > 0 && meta.SingleIntegerPk && large;
        bool doIndexes = rebuildIndexes && meta.HasConnection && large;

        sb.AppendLine("-- " + new string('=', 70));
        sb.AppendLine($"-- Table: {fullName}"
            + (meta.EstimatedRows >= 0 ? $"  (~{meta.EstimatedRows:N0} rows)" : ""));
        sb.AppendLine("-- " + new string('=', 70));
        sb.AppendLine();
        sb.AppendLine($"PRINT 'Processing {fullName}...';");
        sb.AppendLine($"SET @rc = (SELECT COUNT(*) FROM {fullName});");
        sb.AppendLine("PRINT 'Row count before: ' + CAST(@rc AS VARCHAR);");
        sb.AppendLine();

        if (doIndexes)
            GenerateDisableIndexes(sb, fullName);

        if (hasShuffle)
            GenerateShuffleTempTables(sb, fullName, pk, shuffleColumns, valsTbl, origTbl);

        if (useLoop)
        {
            sb.AppendLine($"SELECT @lo = MIN([{pk}]), @hi = MAX([{pk}]) FROM {fullName};");
            sb.AppendLine("WHILE @lo IS NOT NULL AND @lo <= @hi");
            sb.AppendLine("BEGIN");
            if (hasShuffle)
                AppendShuffleUpdate(sb, fullName, pk, shuffleColumns, valsTbl, origTbl, batched: true, indent: "  ");
            if (hasDirect)
                AppendDirectUpdate(sb, fullName, pk, directColumns, batched: true, indent: "  ");
            sb.AppendLine("  SET @lo = @lo + @bs;");
            sb.AppendLine("  CHECKPOINT;");
            sb.AppendLine("END");
            sb.AppendLine();
        }
        else
        {
            if (hasShuffle)
                AppendShuffleUpdate(sb, fullName, pk, shuffleColumns, valsTbl, origTbl, batched: false, indent: "");
            if (hasDirect)
                AppendDirectUpdate(sb, fullName, pk, directColumns, batched: false, indent: "");
        }

        if (hasShuffle)
        {
            sb.AppendLine($"DROP TABLE IF EXISTS {valsTbl}, {origTbl};");
            sb.AppendLine();
        }

        if (doIndexes)
        {
            sb.AppendLine($"PRINT 'Rebuilding indexes on {fullName}...';");
            sb.AppendLine($"ALTER INDEX ALL ON {fullName} REBUILD;");
            sb.AppendLine();
        }

        sb.AppendLine($"SET @rc = (SELECT COUNT(*) FROM {fullName});");
        sb.AppendLine("PRINT 'Row count after: ' + CAST(@rc AS VARCHAR);");
        sb.AppendLine($"PRINT '{fullName} complete.';");
        sb.AppendLine("PRINT '';");
        sb.AppendLine();

        return shuffleColumns.Count + directColumns.Count;
    }

    private static void GenerateShuffleTempTables(
        StringBuilder sb, string fullName, string pk, List<ColumnConfig> shuffleColumns,
        string valsTbl, string origTbl)
    {
        // A single pass captures a randomly-ordered copy of every shuffled column
        // (one ORDER BY NEWID() sort, regardless of how many columns are shuffled),
        // plus a row-number map for the primary keys. Joining the two by row number
        // redistributes the values. The previous approach sorted the whole table
        // twice per column; this sorts it once per table.
        sb.AppendLine("-- Capture shuffled values + PK map (one pass for all shuffled columns)");
        sb.AppendLine($"DROP TABLE IF EXISTS {valsTbl}, {origTbl};");
        sb.AppendLine("SELECT");
        foreach (var col in shuffleColumns)
            sb.AppendLine($"  [{col.Name}],");
        sb.AppendLine("  ROW_NUMBER() OVER (ORDER BY NEWID()) AS rn");
        sb.AppendLine($"INTO {valsTbl} FROM {fullName};");
        sb.AppendLine();
        sb.AppendLine("SELECT");
        sb.AppendLine($"  [{pk}],");
        sb.AppendLine("  ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS rn");
        sb.AppendLine($"INTO {origTbl} FROM {fullName};");
        sb.AppendLine();
        sb.AppendLine($"CREATE UNIQUE CLUSTERED INDEX IX_orig_pk ON {origTbl}([{pk}]);");
        sb.AppendLine($"CREATE UNIQUE CLUSTERED INDEX IX_vals_rn ON {valsTbl}(rn);");
        sb.AppendLine();
    }

    private static void AppendShuffleUpdate(
        StringBuilder sb, string fullName, string pk, List<ColumnConfig> shuffleColumns,
        string valsTbl, string origTbl, bool batched, string indent)
    {
        sb.AppendLine($"{indent}-- Apply shuffled values");
        sb.AppendLine($"{indent}UPDATE a");
        sb.AppendLine($"{indent}SET");
        for (int i = 0; i < shuffleColumns.Count; i++)
        {
            string comma = i < shuffleColumns.Count - 1 ? "," : "";
            sb.AppendLine($"{indent}  a.[{shuffleColumns[i].Name}] = v.[{shuffleColumns[i].Name}]{comma}");
        }
        sb.AppendLine($"{indent}FROM {fullName} a");
        sb.AppendLine($"{indent}JOIN {origTbl} o ON a.[{pk}] = o.[{pk}]");
        sb.Append($"{indent}JOIN {valsTbl} v ON o.rn = v.rn");
        if (batched)
        {
            sb.AppendLine();
            sb.AppendLine($"{indent}WHERE a.[{pk}] >= @lo AND a.[{pk}] < @lo + @bs;");
        }
        else
        {
            sb.AppendLine(";");
        }
        sb.AppendLine();
    }

    private static void AppendDirectUpdate(
        StringBuilder sb, string fullName, string pk, List<(string Name, string Expr)> columns,
        bool batched, string indent)
    {
        sb.AppendLine($"{indent}-- Replace / calculate columns");
        sb.AppendLine($"{indent}UPDATE {fullName}");
        sb.AppendLine($"{indent}SET");
        for (int i = 0; i < columns.Count; i++)
        {
            string comma = i < columns.Count - 1 ? "," : "";
            sb.AppendLine($"{indent}  [{columns[i].Name}] = {columns[i].Expr}{comma}");
        }
        if (batched)
            sb.AppendLine($"{indent}WHERE [{pk}] >= @lo AND [{pk}] < @lo + @bs;");
        else
            sb.AppendLine($"{indent};");
        sb.AppendLine();
    }

    private static void GenerateDisableIndexes(StringBuilder sb, string fullName)
    {
        // Only nonclustered, non-PK, non-unique indexes are disabled. Disabling the
        // clustered index would make the table unreadable, and disabling a unique
        // index risks a rebuild failure if a masked value collides.
        sb.AppendLine($"PRINT 'Disabling nonclustered indexes on {fullName}...';");
        sb.AppendLine("SET @disable = N'';");
        sb.AppendLine($"SELECT @disable += N'ALTER INDEX ' + QUOTENAME(i.name) + N' ON {fullName} DISABLE;' + CHAR(13)");
        sb.AppendLine("FROM sys.indexes i");
        sb.AppendLine($"WHERE i.object_id = OBJECT_ID('{fullName}')");
        sb.AppendLine("  AND i.type_desc = 'NONCLUSTERED'");
        sb.AppendLine("  AND i.is_primary_key = 0");
        sb.AppendLine("  AND i.is_unique_constraint = 0");
        sb.AppendLine("  AND i.name IS NOT NULL;");
        sb.AppendLine("IF @disable <> N'' EXEC sp_executesql @disable;");
        sb.AppendLine();
    }

    private static SqlConnection? TryOpenConnection(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return null;

        try
        {
            var connection = new SqlConnection(connectionString);
            connection.Open();
            return connection;
        }
        catch
        {
            return null;
        }
    }

    private static readonly HashSet<string> IntegerTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "int", "bigint", "smallint", "tinyint"
    };

    private static TableMeta GetTableMeta(SqlConnection? connection, TableConfig table)
    {
        // Offline fallback: guess the PK by convention, no chunking/index handling.
        if (connection is null)
            return new TableMeta($"{table.Name}Id", SingleIntegerPk: false, EstimatedRows: -1, HasConnection: false);

        string pkColumn = $"{table.Name}Id";
        bool singleIntegerPk = false;
        long estimatedRows = -1;

        try
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT c.COLUMN_NAME, col.DATA_TYPE
                    FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                    JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE c
                        ON tc.CONSTRAINT_NAME = c.CONSTRAINT_NAME
                        AND tc.TABLE_SCHEMA = c.TABLE_SCHEMA
                        AND tc.TABLE_NAME = c.TABLE_NAME
                    JOIN INFORMATION_SCHEMA.COLUMNS col
                        ON col.TABLE_SCHEMA = c.TABLE_SCHEMA
                        AND col.TABLE_NAME = c.TABLE_NAME
                        AND col.COLUMN_NAME = c.COLUMN_NAME
                    WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
                        AND tc.TABLE_SCHEMA = @Schema
                        AND tc.TABLE_NAME = @Table
                    ORDER BY c.ORDINAL_POSITION";
                cmd.Parameters.AddWithValue("@Schema", table.Schema);
                cmd.Parameters.AddWithValue("@Table", table.Name);

                var pkColumns = new List<(string Name, string Type)>();
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    pkColumns.Add((reader.GetString(0), reader.GetString(1)));

                if (pkColumns.Count >= 1)
                    pkColumn = pkColumns[0].Name;
                if (pkColumns.Count == 1 && IntegerTypes.Contains(pkColumns[0].Type))
                    singleIntegerPk = true;
            }

            using (var cmd = connection.CreateCommand())
            {
                // Instant metadata-based estimate — avoids a COUNT(*) scan of a huge table.
                cmd.CommandText = @"
                    SELECT SUM(p.rows)
                    FROM sys.partitions p
                    WHERE p.object_id = OBJECT_ID(@Full)
                        AND p.index_id IN (0, 1)";
                cmd.Parameters.AddWithValue("@Full", $"[{table.Schema}].[{table.Name}]");

                var result = cmd.ExecuteScalar();
                if (result is not null && result != DBNull.Value)
                    estimatedRows = Convert.ToInt64(result);
            }
        }
        catch
        {
            // Fall through with whatever we managed to determine.
        }

        return new TableMeta(pkColumn, singleIntegerPk, estimatedRows, HasConnection: true);
    }

    private sealed record TableMeta(string PkColumn, bool SingleIntegerPk, long EstimatedRows, bool HasConnection);
}
