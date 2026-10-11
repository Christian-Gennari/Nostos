using System.Globalization;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services;

namespace Nostos.Product.Alignment;

/// <summary>
/// Reads the audiobook's stored global duration without opening media.
/// Malformed or incomplete duration metadata is treated as unknown.
/// </summary>
public static class AlignmentAudioDuration
{
    public static double? GetSeconds(AudioBookModel book)
    {
        if (!string.IsNullOrWhiteSpace(book.FileDetails.TracksJson))
        {
            var tracks = BookTrackList.Parse(
                book.FileDetails.TracksJson);

            if (BookTrackList.Validate(tracks) is not null)
                return null;

            try
            {
                var totalMs = BookTrackList.TotalDurationMs(tracks);
                return totalMs > 0 ? totalMs / 1000d : null;
            }
            catch (OverflowException)
            {
                return null;
            }
        }

        return ParseSeconds(book.Duration);
    }

    public static double? ParseSeconds(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return null;

        var parts = stored.Trim().Split(':');

        if (parts.Length is < 2 or > 3)
            return null;

        var values = new double[parts.Length];

        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(
                    parts[i],
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out values[i]) ||
                !double.IsFinite(values[i]) ||
                values[i] < 0)
                return null;
        }

        if (values[^1] >= 60)
            return null;

        if (parts.Length == 3 && values[1] >= 60)
            return null;

        var seconds = parts.Length == 3
            ? values[0] * 3600 + values[1] * 60 + values[2]
            : values[0] * 60 + values[1];

        return double.IsFinite(seconds) && seconds > 0
            ? seconds
            : null;
    }
}
