using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lakebase;

/// <summary>
/// CLI I/O: progress on stderr when <see cref="JsonMode"/> is set; final report on stdout as JSON.
/// Human mode writes progress on stdout.
/// </summary>
public static class CliLog
{
    public static bool JsonMode { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Progress / step line (prefixed with ==&gt;).</summary>
    public static void Progress(string message)
    {
        var line = message.StartsWith("==>", StringComparison.Ordinal) ? message : $"==> {message}";
        WriteProgressStream(line);
    }

    /// <summary>Unprefixed detail line (still routed like progress).</summary>
    public static void Detail(string message) => WriteProgressStream(message);

    /// <summary>Blank line in human mode; no-op in JSON mode.</summary>
    public static void Blank()
    {
        if (!JsonMode)
            Console.WriteLine();
    }

    /// <summary>Verbose API / tool dumps — stderr in JSON mode so stdout stays clean.</summary>
    public static void Dump(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        WriteProgressStream(text);
    }

    private static void WriteProgressStream(string line)
    {
        if (JsonMode)
            Console.Error.WriteLine(line);
        else
            Console.WriteLine(line);
    }

    public static void WriteReport(object report)
        => Console.Out.WriteLine(JsonSerializer.Serialize(report, JsonOptions));

    public static void WriteErrorReport(string operation, string code, string message, int exitCode = 1)
    {
        WriteReport(new
        {
            ok = false,
            operation,
            error = new { code, message },
        });
        Environment.ExitCode = exitCode;
    }
}

/// <summary>Machine-readable success envelope (lakebase-scm-utils style).</summary>
public static class OperationReport
{
    public static object Ok(string operation, object? result = null, IReadOnlyList<string>? warnings = null)
        => new
        {
            ok = true,
            operation,
            result,
            warnings,
        };
}
