# pii-masker

A CLI tool that generates T-SQL masking scripts from a JSON config file. Designed for obfuscating PII in non-production SQL Server databases.

## Usage

### Generate a masking script

```bash
pii-masker --config masking-config.json --output mask.sql
```

If you provide a `--connection` string, the tool will query the database for primary key information to generate more accurate shuffle operations:

```bash
pii-masker --config masking-config.json --connection "Server=.;Database=MyDb;Trusted_Connection=True;TrustServerCertificate=True" --output mask.sql
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
  ]
}
```

**`exact`** — matches a column by its full name (case-insensitive). If the same name exists in the built-in list, your entry takes precedence.

**`fuzzy`** — matches any column whose name *contains* the pattern as a substring (case-insensitive). User-defined fuzzy patterns are checked before the built-in ones, so they can take precedence. Matched columns are always flagged with `"review": true` in the output.

Both entry types follow the same `action` / `value` / `expression` rules as the masking config.

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
          "value": "CONCAT('user_', CAST(ApplicantId AS VARCHAR), '@dev.invalid')"
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
| `shuffle`   | —             | Randomly redistributes existing values across rows using a set-based CTE approach |
| `replace`   | `value`       | Sets the column to a T-SQL expression (e.g. `NULL`, a string literal, or `CONCAT(...)`) |
| `calculate` | `expression`  | Sets the column to a computed T-SQL expression referencing the current row |

## Sample generated output

```sql
/*
  PII Masking Script
  Generated: 2026-03-27 05:52:02 UTC
  Tables: 1
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION;

-- ======================================================================
-- Table: [dbo].[Applicants]
-- ======================================================================

PRINT 'Processing [dbo].[Applicants]...';
PRINT 'Row count before: ' + CAST((SELECT COUNT(*) FROM [dbo].[Applicants]) AS VARCHAR);

-- Shuffle: FirstName
WITH Shuffled AS (
  SELECT
    ROW_NUMBER() OVER (ORDER BY NEWID()) AS rn,
    [FirstName]
  FROM [dbo].[Applicants]
),
Original AS (
  SELECT
    ROW_NUMBER() OVER (ORDER BY NEWID()) AS rn,
    [ApplicantId]
  FROM [dbo].[Applicants]
)
UPDATE a
SET a.[FirstName] = s.[FirstName]
FROM [dbo].[Applicants] a
JOIN Original o ON a.[ApplicantId] = o.[ApplicantId]
JOIN Shuffled s ON o.rn = s.rn;

-- Replace columns
UPDATE [dbo].[Applicants]
SET
  [Email] = CONCAT('user_', CAST(ApplicantId AS VARCHAR), '@dev.invalid'),
  [Phone] = '555-000-0000',
  [StreetAddress] = NULL
;

-- Calculate columns
UPDATE [dbo].[Applicants]
SET
  [DateOfBirth] = DATEADD(day, (ABS(CHECKSUM(NEWID())) % 365) - 182, DateOfBirth)
;

PRINT 'Row count after: ' + CAST((SELECT COUNT(*) FROM [dbo].[Applicants]) AS VARCHAR);
PRINT '[dbo].[Applicants] complete.';

-- Uncomment ROLLBACK and comment COMMIT to test without persisting changes
-- ROLLBACK TRANSACTION;
COMMIT TRANSACTION;

PRINT 'Masking complete. 1 table(s), 6 column(s) processed.';
```

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
