using System.Text;

namespace CafePos.Core.Common;

/// <summary>Semicolon separated CSV reader/writer (Excel friendly in ru-RU locale).</summary>
public static class Csv
{
    public const char Delimiter = ';';

    public static string Escape(string? value)
    {
        var text = value ?? string.Empty;
        return text.Contains(Delimiter) || text.Contains('"') || text.Contains('\n') || text.Contains('\r')
            ? $"\"{text.Replace("\"", "\"\"")}\""
            : text;
    }

    public static string Join(params string?[] fields) => string.Join(Delimiter, fields.Select(Escape));

    /// <summary>Splits one CSV line honouring quoted fields.</summary>
    public static string[] ParseLine(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (inQuotes && index + 1 < line.Length && line[index + 1] == '"')
                {
                    current.Append('"');
                    index++;
                    continue;
                }

                inQuotes = !inQuotes;
                continue;
            }

            if (character == Delimiter && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(character);
        }

        result.Add(current.ToString());
        return [.. result];
    }

    /// <summary>Splits a whole document into rows, ignoring blank lines.</summary>
    public static List<string[]> Parse(string content) => content
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => ParseLine(line.TrimEnd('\r')))
        .ToList();
}
