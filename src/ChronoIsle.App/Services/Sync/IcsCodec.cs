using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ChronoIsle.App.Services.Domain;

namespace ChronoIsle.App.Services.Sync;

internal sealed record IcsProperty(string Name, IReadOnlyDictionary<string, string> Parameters, string Value);
internal sealed record IcsComponent(string Name, IReadOnlyList<IcsProperty> Properties, string Raw);

internal static class IcsCodec
{
    public static string Escape(string value) => value.Replace("\\", "\\\\")
        .Replace("\r\n", "\\n").Replace("\n", "\\n").Replace(";", "\\;").Replace(",", "\\,");

    public static string Unescape(string value) => value.Replace("\\n", "\n", StringComparison.OrdinalIgnoreCase)
        .Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");

    public static string FormatTime(string property, TemporalValue value)
    {
        if (value.Semantics == TimeSemantics.AbsoluteInstant)
            return $"{property}:{value.UtcInstant!.Value.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}";
        var local = value.LocalDateTime!.Value;
        return value.Semantics == TimeSemantics.ZonedWallClock
            ? $"{property};TZID={value.IanaTimeZoneId}:{local:yyyyMMdd'T'HHmmss}"
            : $"{property}:{local:yyyyMMdd'T'HHmmss}";
    }

    public static IEnumerable<string> Fold(string line)
    {
        const int limit = 75;
        var current = new StringBuilder();
        var bytes = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            var text = rune.ToString();
            var size = Encoding.UTF8.GetByteCount(text);
            if (bytes > 0 && bytes + size > limit)
            {
                yield return current.ToString();
                current.Clear(); current.Append(' '); bytes = 1;
            }
            current.Append(text); bytes += size;
        }
        yield return current.ToString();
    }

    public static IReadOnlyList<IcsComponent> Parse(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var unfolded = new List<string>();
        foreach (var line in normalized.Split('\n'))
        {
            if ((line.StartsWith(' ') || line.StartsWith('\t')) && unfolded.Count > 0)
                unfolded[^1] += line[1..];
            else if (line.Length > 0) unfolded.Add(line);
        }

        var result = new List<IcsComponent>();
        for (var i = 0; i < unfolded.Count; i++)
        {
            if (unfolded[i] is not ("BEGIN:VEVENT" or "BEGIN:VTODO")) continue;
            var name = unfolded[i][6..];
            var start = i;
            var properties = new List<IcsProperty>();
            while (++i < unfolded.Count && !unfolded[i].Equals("END:" + name, StringComparison.OrdinalIgnoreCase))
            {
                var colon = unfolded[i].IndexOf(':');
                if (colon < 1) continue;
                var header = unfolded[i][..colon].Split(';');
                var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var part in header.Skip(1))
                {
                    var equals = part.IndexOf('=');
                    if (equals > 0) parameters[part[..equals]] = part[(equals + 1)..].Trim('"');
                }
                properties.Add(new(header[0].ToUpperInvariant(), parameters, unfolded[i][(colon + 1)..]));
            }
            result.Add(new(name, properties, string.Join("\r\n", unfolded.Skip(start).Take(i - start + 1))));
        }
        return result;
    }

    public static TemporalValue ParseTime(IcsProperty property, IOccurrenceTimeResolver resolver)
    {
        if (property.Value.EndsWith('Z') && DateTimeOffset.TryParseExact(property.Value, "yyyyMMdd'T'HHmmss'Z'",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant))
            return TemporalValue.Absolute(instant);
        if (!DateTime.TryParseExact(property.Value, new[] { "yyyyMMdd'T'HHmmss", "yyyyMMdd" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            throw new FormatException($"Invalid ICS time '{property.Value}'.");
        var local = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        if (property.Parameters.TryGetValue("TZID", out var iana))
        {
            var temporal = new TemporalValue(local, null, iana, null, TimeSemantics.ZonedWallClock);
            return temporal with { UtcInstant = resolver.ResolveSingleLocalTime(temporal) };
        }
        var floating = new TemporalValue(local, null, null, null, TimeSemantics.DeviceLocalFloatingWallClock);
        return floating with { UtcInstant = resolver.ResolveSingleLocalTime(floating) };
    }

    public static string StableId(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24].ToLowerInvariant();
}
