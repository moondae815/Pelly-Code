using System.Text;

namespace PellyCoder.Core.Sql;

/// <summary>A one- or two-part object name (<c>X</c> or <c>schema.X</c>) without brackets.</summary>
public sealed record SqlObjectName(string? Schema, string Name)
{
    public string Display => Schema is null ? Name : $"{Schema}.{Name}";

    /// <summary>Parses <c>X</c>, <c>dbo.X</c> or <c>[dbo].[X]</c>. Three or more parts throw <see cref="FormatException"/>.</summary>
    public static SqlObjectName Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parts = new List<string>();
        var current = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '[')
            {
                i++;
                var closed = false;
                while (i < text.Length)
                {
                    if (text[i] == ']')
                    {
                        if (i + 1 < text.Length && text[i + 1] == ']') { current.Append(']'); i += 2; continue; }
                        closed = true;
                        i++;
                        break;
                    }
                    current.Append(text[i++]);
                }
                if (!closed)
                    throw new FormatException($"Unclosed '[' in object name: {text}");
            }
            else if (c == '.')
            {
                parts.Add(current.ToString());
                current.Clear();
                i++;
            }
            else
            {
                current.Append(c);
                i++;
            }
        }
        parts.Add(current.ToString());

        if (parts.Count > 2)
            throw new FormatException($"Object name has more than two parts: {text}");
        if (parts.Any(p => p.Trim().Length == 0))
            throw new FormatException($"Empty part in object name: {text}");

        return parts.Count == 1
            ? new SqlObjectName(null, parts[0].Trim())
            : new SqlObjectName(parts[0].Trim(), parts[1].Trim());
    }

    /// <summary>Case-insensitive comparison where a missing schema means <c>dbo</c>.</summary>
    public bool SameAs(SqlObjectName other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return string.Equals(Schema ?? "dbo", other.Schema ?? "dbo", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);
    }
}
