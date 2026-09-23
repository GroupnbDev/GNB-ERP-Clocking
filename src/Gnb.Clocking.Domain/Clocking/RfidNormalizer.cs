namespace Gnb.Clocking.Domain.Clocking;

/// <summary>
/// USB RFID readers usually type the tag id and press Enter, sometimes wrapped
/// in sentinel characters such as <c>;04A1C8E291?</c>.
/// </summary>
public static class RfidNormalizer
{
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        Span<char> buffer = stackalloc char[raw.Length];
        var count = 0;
        foreach (var character in raw)
        {
            if (char.IsLetterOrDigit(character))
                buffer[count++] = char.ToUpperInvariant(character);
        }

        return new string(buffer[..count]);
    }
}
