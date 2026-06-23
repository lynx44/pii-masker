# pii-masker

A CLI tool that generates T-SQL masking scripts from a JSON config file. Designed for obfuscating PII in non-production SQL Server databases.

## Usage

### Generate a masking script

```bash
pii-masker --config masking-config.json --output mask.sql
```

If you provide a `--connection` string, the tool will query the database for primary key information to generate more accurate shuffle operations. It also enables the large-table optimizations described below (PK-range chunking and optional index rebuilding):

```bash
pii-masker --config masking-config.json --connection "Server=.;Database=MyDb;Trusted_Connection=True;TrustServerCertificate=True" --output mask.sql
```

### Large databases / performance

The generator is tuned for masking tables with millions of rows:

- **One sort per table for shuffles.** All shuffled columns are captured in a single `ORDER BY NEWID()` pass into a temp table, rather than sorting the whole table twice *per column*. This is usually the single biggest speedup.
- **No wrapping transaction.** Each statement autocommits so the transaction log can be truncated continuously instead of growing to hold every change at once. This is safe because masking is meant to run against backups / non-production copies — if a run fails partway, re-run it.
- **PK-range chunking.** Large UPDATEs are split into primary-key ranges so locks and log usage stay bounded, with a `CHECKPOINT` after each batch.

| Option | Default | Description |
|--------|---------|-------------|
| `--batch-size <n>` | `50000` | Rows per chunk. Chunking is applied only to tables with a **single integer primary key** (detected via `--connection`). Set to `0` to disable chunking. |
| `--rebuild-indexes` | off | Before masking a large table, disable its **nonclustered** indexes and rebuild them afterwards. The clustered index, primary key, and unique constraints are always left in place. |

Both options require `--connection` (they need the database's primary-key types and row-count estimates). Without a connection, the tool still applies the consolidated-shuffle and no-wrapping-transaction optimizations, but skips chunking and index handling.

```bash
pii-masker --config masking-config.json \
  --connection "Server=.;Database=MyDb;Trusted_Connection=True;TrustServerCertificate=True" \
  --batch-size 100000 --rebuild-indexes --output mask.sql
```

### Scan a database for PII columns

```bash
pii-masker --scan --connection "Server=.;Database=MyDb;Trusted_Connection=True;TrustServerCertificate=True" --output suggested-config.json
```

This connects to the database, inspects `INFORMATION_SCHEMA.COLUMNS`, and produces a suggested config. Columns it's uncertain about are marked with `"review": true` and a `"reason"` explaining the match.

### Scan with extra patterns

Use `--patterns` to supply additional column name patterns alongside the built-in ones. Useful when you know a specific database has non-standard column names that contain PII:

```bash
pii-masker --scan --connection "..." --patterns my-patterns.json --output suggested-config.json
```

#### Patterns file format

```json
{
  "exact": [
    {
      "column": "NationalInsuranceNumber",
      "action": "replace",
      "value": "NULL"
    },
    {
      "column": "PreferredName",
      "action": "shuffle"
    },
    {
      "column": "AnnualSalary",
      "action": "calculate",
      "expression": "ROUND([AnnualSalary] / 5000.0, 0) * 5000"
    },
    {
      "column": "Username",
      "table": "ApiCredentials",
      "action": "replace",
      "value": "''"
    }
  ],
  "fuzzy": [
    {
      "pattern": "passport",
      "action": "replace",
      "value": "NULL"
    },
    {
      "pattern": "nin",
      "action": "replace",
      "value": "NULL"
    }
  ],
  "ignore": [
    {
      "column": "Name"
    },
    {
      "column": "AccountManager",
      "table": "Vendors"
    },
    {
      "table": "AuditLog"
    }
  ]
}
```

**`exact`** — matches a column by its full name (case-insensitive). If the same name exists in the built-in list, your entry takes precedence.

**`fuzzy`** — matches any column whose name *contains* the pattern as a substring (case-insensitive). User-defined fuzzy patterns are checked before the built-in ones, so they can take precedence. Matched columns are always flagged with `"review": true` in the output.

**`ignore`** — excludes a column (or whole table) from scanning entirely. Ignored columns are matched by no pattern — built-in or user-defined — so they never appear in the output. The ignore list is evaluated first, so it overrides everything else. Each entry must specify at least one of `column` or `table`:

- `column` only — ignored in every table.
- `table` only — every column of that table is ignored (the table is skipped entirely; useful for system/junction tables that hold no personal data).
- both — only that column in that table is ignored.

Use it to suppress false positives and keep scan output consistent across databases with similar schemas.

#### Optional `table` scoping

`exact`, `fuzzy`, and `ignore` entries all accept an optional `table` field:

- **omitted (or empty)** — the entry applies to any column with that name, in every table.
- **set** — the entry applies only to the named table (case-insensitive).

For `exact` and `fuzzy`, a table-scoped entry takes precedence over a table-less one for the same column, which lets you set a different action/value for one specific table — e.g. a generic rule for `Username` everywhere, plus a `table`-scoped override for `ApiCredentials.Username`.

For `ignore`, the `column` field is also optional — an entry with only `table` set ignores every column in that table.

The `exact` and `fuzzy` entry types follow the same `action` / `value` / `expression` rules as the masking config. `ignore` entries take no action.

## Config file format

```json
{
  "tables": [
    {
      "name": "Applicants",
      "schema": "dbo",
      "columns": [
        {
          "name": "FirstName",
          "action": "shuffle"
        },
        {
          "name": "LastName",
          "action": "shuffle"
        },
        {
          "name": "Email",
          "action": "replace",
          "value": "CONCAT('user_', CAST(ApplicantId AS VARCHAR(50)), '@dev.invalid')"
        },
        {
          "name": "Phone",
          "action": "replace",
          "value": "'555-000-0000'"
        },
        {
          "name": "StreetAddress",
          "action": "replace",
          "value": "NULL"
        },
        {
          "name": "DateOfBirth",
          "action": "calculate",
          "expression": "DATEADD(day, (ABS(CHECKSUM(NEWID())) % 365) - 182, DateOfBirth)"
        }
      ]
    }
  ]
}
```

### Column actions

| Action      | Required field | Description |
|-------------|---------------|-------------|
| `shuffle`   | —             | Randomly redistributes existing values across rows (set-based, one sort per table regardless of how many columns are shuffled) |
| `replace`   | `value`       | Sets the column to a T-SQL expression (e.g. `NULL`, a string literal, or `CONCAT(...)`) |
| `calculate` | `expression`  | Sets the column to a computed T-SQL expression referencing the current row |

## Sample generated output

Generated with `--connection`, `--batch-size 25000`, and `--rebuild-indexes` against a large table with an integer primary key:

```sql
/*
  PII Masking Script
  Generated: 2026-06-23 00:41:40 UTC
  Tables: 1
  Chunking: 25,000 rows/batch (tables with a single integer PK)
  Index rebuild: enabled (large tables)
*/

SET NOCOUNT ON;

DECLARE @rc INT;
DECLARE @lo BIGINT, @hi BIGINT;
DECLARE @bs INT = 25000;
DECLARE @disable NVARCHAR(MAX);

-- ======================================================================
-- Table: [dbo].[Applicants]  (~120,000 rows)
-- ======================================================================

PRINT 'Processing [dbo].[Applicants]...';
SET @rc = (SELECT COUNT(*) FROM [dbo].[Applicants]);
PRINT 'Row count before: ' + CAST(@rc AS VARCHAR);

PRINT 'Disabling nonclustered indexes on [dbo].[Applicants]...';
SET @disable = N'';
SELECT @disable += N'ALTER INDEX ' + QUOTENAME(i.name) + N' ON [dbo].[Applicants] DISABLE;' + CHAR(13)
FROM sys.indexes i
WHERE i.object_id = OBJECT_ID('[dbo].[Applicants]')
  AND i.type_desc = 'NONCLUSTERED'
  AND i.is_primary_key = 0
  AND i.is_unique_constraint = 0
  AND i.name IS NOT NULL;
IF @disable <> N'' EXEC sp_executesql @disable;

-- Capture shuffled values + PK map (one pass for all shuffled columns)
DROP TABLE IF EXISTS #vals, #orig;
SELECT
  [FirstName],
  [LastName],
  ROW_NUMBER() OVER (ORDER BY NEWID()) AS rn
INTO #vals FROM [dbo].[Applicants];

SELECT
  [ApplicantId],
  ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS rn
INTO #orig FROM [dbo].[Applicants];

CREATE UNIQUE CLUSTERED INDEX IX_orig_pk ON #orig([ApplicantId]);
CREATE UNIQUE CLUSTERED INDEX IX_vals_rn ON #vals(rn);

SELECT @lo = MIN([ApplicantId]), @hi = MAX([ApplicantId]) FROM [dbo].[Applicants];
WHILE @lo IS NOT NULL AND @lo <= @hi
BEGIN
  -- Apply shuffled values
  UPDATE a
  SET
    a.[FirstName] = v.[FirstName],
    a.[LastName] = v.[LastName]
  FROM [dbo].[Applicants] a
  JOIN #orig o ON a.[ApplicantId] = o.[ApplicantId]
  JOIN #vals v ON o.rn = v.rn
  WHERE a.[ApplicantId] >= @lo AND a.[ApplicantId] < @lo + @bs;

  -- Replace / calculate columns
  UPDATE [dbo].[Applicants]
  SET
    [Email] = CONCAT('user_', CAST(ApplicantId AS VARCHAR), '@dev.invalid'),
    [Phone] = '555-000-0000',
    [StreetAddress] = NULL,
    [DateOfBirth] = DATEADD(day, (ABS(CHECKSUM(NEWID())) % 365) - 182, DateOfBirth)
  WHERE [ApplicantId] >= @lo AND [ApplicantId] < @lo + @bs;

  SET @lo = @lo + @bs;
  CHECKPOINT;
END

DROP TABLE IF EXISTS #vals, #orig;

PRINT 'Rebuilding indexes on [dbo].[Applicants]...';
ALTER INDEX ALL ON [dbo].[Applicants] REBUILD;

SET @rc = (SELECT COUNT(*) FROM [dbo].[Applicants]);
PRINT 'Row count after: ' + CAST(@rc AS VARCHAR);
PRINT '[dbo].[Applicants] complete.';
PRINT '';

PRINT 'Masking complete. 1 table(s), 6 column(s) processed.';
```

Without a connection (or for a table without a single integer PK), the same masking is emitted without the `WHILE` chunk loop and index handling — the shuffle still uses the single-sort temp-table approach.

## Building

```bash
dotnet build
```

### Publish as a single self-contained executable

```bash
dotnet publish -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## Project structure

```
pii-masker/
  pii-masker.csproj
  Program.cs               # CLI entry point, argument wiring
  Models/
    Config.cs              # Strongly-typed config models
    ColumnAction.cs        # Enum: Shuffle, Replace, Calculate
    PatternsFile.cs        # Extra patterns file model
  Services/
    ConfigLoader.cs        # JSON deserialization + validation
    PatternsLoader.cs      # Extra patterns deserialization + validation
    Scanner.cs             # DB scanning + PII heuristics
    ScriptGenerator.cs     # T-SQL generation logic
  sample-config.json       # Example masking config
  sample-patterns.json     # Example extra patterns file for --scan
  README.md
```
