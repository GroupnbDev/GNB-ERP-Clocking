namespace Gnb.Clocking.Application.Clocking;

/// <summary>
/// The JPEG saved on the kiosk and uploaded with a punch. A face only needs to be recognizable,
/// so the file stays small enough to send on a slow link and to keep for a week.
/// </summary>
public static class ClockPhotoEncode
{
    public const int MaxLongEdge = 480;
    public const float JpegQuality = 0.4f;
}
