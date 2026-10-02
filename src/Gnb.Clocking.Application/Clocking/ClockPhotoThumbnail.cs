namespace Gnb.Clocking.Application.Clocking;

/// <summary>
/// 96 px copy written beside a clock photo. The app assigns <see cref="Write"/>; the store calls
/// <see cref="TryWrite"/> and never fails a punch if the thumbnail cannot be made.
/// </summary>
public static class ClockPhotoThumbnail
{
    public static Action<string>? Write { get; set; }

    public static string PathFor(string absolutePhotoPath)
    {
        var folder = Path.GetDirectoryName(absolutePhotoPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(absolutePhotoPath) + ".thumb.jpeg";
        return Path.Combine(folder, name);
    }

    public static void TryWrite(string absoluteJpegPath)
    {
        if (Write == null || string.IsNullOrWhiteSpace(absoluteJpegPath))
            return;

        try
        {
            Write(absoluteJpegPath);
        }
        catch (Exception)
        {
            // The full photo is what gets uploaded. A missing thumbnail only changes the activity list.
        }
    }
}
