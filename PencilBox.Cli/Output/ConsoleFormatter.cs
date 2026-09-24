using System.Text.Json;

namespace PencilBox.Cli.Output;

/// <summary>
/// Console output formatter: table / json / quiet modes
/// </summary>
public enum OutputFormat
{
    Table,
    Json,
    Quiet
}

public static class ConsoleFormatter
{
    public static OutputFormat Format { get; set; } = OutputFormat.Table;

    /// <summary>
    /// Print a table from a list of objects
    /// </summary>
    public static void PrintTable<T>(IEnumerable<T> items, params (string Header, Func<T, string> Selector)[] columns)
    {
        var list = items.ToList();
        if (list.Count == 0)
        {
            if (Format == OutputFormat.Table)
                Console.WriteLine("(empty)");
            else if (Format == OutputFormat.Json)
                Console.WriteLine("[]");
            return;
        }

        switch (Format)
        {
            case OutputFormat.Json:
                var options = new JsonSerializerOptions { WriteIndented = true };
                Console.WriteLine(JsonSerializer.Serialize(list, options));
                break;

            case OutputFormat.Quiet:
                // Only print the first column value per row
                foreach (var item in list)
                    Console.WriteLine(columns[0].Selector(item));
                break;

            default:
                PrintTableFormat(list, columns);
                break;
        }
    }

    /// <summary>
    /// Print a single object as key-value pairs or JSON
    /// </summary>
    public static void PrintObject(object? obj)
    {
        if (obj == null)
        {
            Console.WriteLine("(null)");
            return;
        }

        switch (Format)
        {
            case OutputFormat.Json:
                var options = new JsonSerializerOptions { WriteIndented = true };
                Console.WriteLine(JsonSerializer.Serialize(obj, obj.GetType(), options));
                break;

            case OutputFormat.Quiet:
                // Print nothing in quiet mode for single objects
                break;

            default:
                var props = obj.GetType().GetProperties();
                foreach (var prop in props)
                {
                    var value = prop.GetValue(obj);
                    Console.WriteLine($"  {prop.Name,-25} = {FormatValue(value)}");
                }
                break;
        }
    }

    /// <summary>
    /// Print an info line (suppressed in quiet mode)
    /// </summary>
    public static void Info(string message)
    {
        if (Format != OutputFormat.Quiet)
            Console.WriteLine(message);
    }

    /// <summary>
    /// Print an error (always shown)
    /// </summary>
    public static void Error(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[ERROR] {message}");
        Console.ResetColor();
    }

    /// <summary>
    /// Print a warning: 自动恢复类提示（如 OTA 重查/重发）——不是失败，勿用 [ERROR] 误导判读
    /// </summary>
    public static void Warn(string message)
    {
        if (Format != OutputFormat.Quiet)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[WARN] {message}");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// Print a success line (suppressed in quiet mode)
    /// </summary>
    public static void Success(string message)
    {
        if (Format != OutputFormat.Quiet)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[OK] {message}");
            Console.ResetColor();
        }
    }

    private static void PrintTableFormat<T>(List<T> list, (string Header, Func<T, string> Selector)[] columns)
    {
        // Calculate column widths
        var widths = new int[columns.Length];
        for (int c = 0; c < columns.Length; c++)
        {
            widths[c] = columns[c].Header.Length;
            foreach (var item in list)
            {
                var len = columns[c].Selector(item).Length;
                if (len > widths[c]) widths[c] = len;
            }
        }

        // Header
        for (int c = 0; c < columns.Length; c++)
        {
            Console.Write(columns[c].Header.PadRight(widths[c] + 2));
        }
        Console.WriteLine();

        // Separator
        for (int c = 0; c < columns.Length; c++)
        {
            Console.Write(new string('-', widths[c]) + "  ");
        }
        Console.WriteLine();

        // Rows
        foreach (var item in list)
        {
            for (int c = 0; c < columns.Length; c++)
            {
                Console.Write(columns[c].Selector(item).PadRight(widths[c] + 2));
            }
            Console.WriteLine();
        }
    }

    private static string FormatValue(object? value)
    {
        return value switch
        {
            null => "null",
            byte b => $"0x{b:X2}",
            ushort u => $"0x{u:X4}",
            bool b => b ? "true" : "false",
            _ => value.ToString() ?? "null"
        };
    }
}
