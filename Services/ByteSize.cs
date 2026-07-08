using System.Globalization;

namespace MediaDownloader.Services;

/// <summary>Formatting and parsing helpers for byte counts (binary/IEC units).</summary>
public static class ByteSize
{
    private static readonly string[] Units = { "B", "KiB", "MiB", "GiB", "TiB", "PiB" };

    /// <summary>Formats a byte count as a human-readable binary size, e.g. "1.4 GiB".</summary>
    public static string Format(long bytes, int decimals = 1)
    {
        double size = bytes;
        var i = 0;
        while (size >= 1024 && i < Units.Length - 1) { size /= 1024; i++; }
        return size.ToString("0." + new string('#', decimals), CultureInfo.InvariantCulture) + " " + Units[i];
    }

    /// <summary>Formats a per-second byte rate, e.g. "2.3 MiB/s".</summary>
    public static string FormatRate(long bytesPerSecond) => Format(bytesPerSecond) + "/s";

    /// <summary>Parses a binary size string such as "1.4 GiB" or "550.3 MiB" into bytes (0 if unparseable).</summary>
    public static long Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        // Mirror HTML uses &nbsp; (U+00A0) between the number and unit.
        var parts = text.Replace(' ', ' ').Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            return 0;

        var unit = parts[1].ToUpperInvariant();
        var multiplier = unit switch
        {
            "KIB" or "KB" => 1024d,
            "MIB" or "MB" => 1024d * 1024,
            "GIB" or "GB" => 1024d * 1024 * 1024,
            "TIB" or "TB" => 1024d * 1024 * 1024 * 1024,
            _ => 1d
        };
        return (long)(value * multiplier);
    }
}
