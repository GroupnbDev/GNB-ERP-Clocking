using Gnb.Clocking.Application.Clocking;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Graphics.Platform;

namespace Gnb.Clocking.App.Photos;

public static class MauiPhotoThumbnail
{
    public const int Edge = 96;

    public static void Write(string absoluteJpegPath)
    {
        var destination = ClockPhotoThumbnail.PathFor(absoluteJpegPath);
        using var input = File.OpenRead(absoluteJpegPath);
        var image = PlatformImage.FromStream(input);
        if (image == null)
            return;

        using var small = image.Downsize(Edge, disposeOriginal: true);
        var folder = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);

        using var output = File.Create(destination);
        small.Save(output, ImageFormat.Jpeg, 0.85f);
    }
}
