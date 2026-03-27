using System.CommandLine;
using PiiMasker.Services;

var configOption = new Option<FileInfo?>(
    name: "--config",
    description: "Path to the JSON config file");

var connectionOption = new Option<string?>(
    name: "--connection",
    description: "SQL Server connection string");

var outputOption = new Option<FileInfo?>(
    name: "--output",
    description: "Output path for the generated .sql file");

var scanOption = new Option<bool>(
    name: "--scan",
    description: "Scan the database to suggest a PII masking config");

var rootCommand = new RootCommand("pii-masker — Generate T-SQL masking scripts from a config file")
{
    configOption,
    connectionOption,
    outputOption,
    scanOption
};

rootCommand.SetHandler(async (FileInfo? config, string? connection, FileInfo? output, bool scan) =>
{
    try
    {
        if (scan)
        {
            await HandleScan(connection, output);
        }
        else
        {
            HandleGenerate(config, connection, output);
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        Environment.ExitCode = 1;
    }
}, configOption, connectionOption, outputOption, scanOption);

return await rootCommand.InvokeAsync(args);

// ──────────────────────────────────────────────────────────────────────
// Handlers
// ──────────────────────────────────────────────────────────────────────

static void HandleGenerate(FileInfo? configFile, string? connection, FileInfo? outputFile)
{
    if (configFile is null)
    {
        Console.Error.WriteLine("Error: --config is required when not using --scan.");
        Environment.ExitCode = 1;
        return;
    }

    if (outputFile is null)
    {
        Console.Error.WriteLine("Error: --output is required when not using --scan.");
        Environment.ExitCode = 1;
        return;
    }

    Console.WriteLine($"Loading config from: {configFile.FullName}");
    var maskingConfig = ConfigLoader.Load(configFile.FullName);

    Console.WriteLine($"Generating masking script for {maskingConfig.Tables.Count} table(s)...");
    var (sql, tableCount, columnCount) = ScriptGenerator.Generate(maskingConfig, connection ?? string.Empty);

    var outputDir = Path.GetDirectoryName(outputFile.FullName);
    if (!string.IsNullOrEmpty(outputDir))
        Directory.CreateDirectory(outputDir);

    File.WriteAllText(outputFile.FullName, sql);

    Console.WriteLine();
    Console.WriteLine("Summary");
    Console.WriteLine(new string('\u2500', 31));
    Console.WriteLine($"  Tables processed:  {tableCount}");
    Console.WriteLine($"  Columns masked:    {columnCount}");
    Console.WriteLine($"  Output written to: {outputFile.FullName}");
}

static async Task HandleScan(string? connection, FileInfo? outputFile)
{
    if (string.IsNullOrEmpty(connection))
    {
        Console.Error.WriteLine("Error: --connection is required for --scan mode.");
        Environment.ExitCode = 1;
        return;
    }

    Console.WriteLine("Connecting to database and scanning for PII columns...");
    var config = await Scanner.ScanAsync(connection);

    string json = Scanner.SerializeConfig(config);

    int reviewCount = config.Tables
        .SelectMany(t => t.Columns)
        .Count(c => c.Review == true);

    if (outputFile is not null)
    {
        var outputDir = Path.GetDirectoryName(outputFile.FullName);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        File.WriteAllText(outputFile.FullName, json);
        Console.WriteLine($"Suggested config written to: {outputFile.FullName}");
    }
    else
    {
        Console.WriteLine(json);
    }

    Console.WriteLine();
    Console.WriteLine("Scan Summary");
    Console.WriteLine(new string('\u2500', 31));
    Console.WriteLine($"  Tables with PII:     {config.Tables.Count}");
    Console.WriteLine($"  Columns identified:  {config.Tables.Sum(t => t.Columns.Count)}");
    Console.WriteLine($"  Flagged for review:  {reviewCount}");

    if (reviewCount > 0)
    {
        Console.WriteLine();
        Console.WriteLine("Review the generated config — columns marked with \"review\": true need manual verification.");
    }
}
