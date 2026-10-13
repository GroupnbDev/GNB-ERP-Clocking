using Gnb.Clocking.Application.Clocking;
using Xunit;

namespace Gnb.Clocking.Tests;

public sealed class CameraFormatPolicyTests
{
    private static CameraFormatOption F(uint width, uint height, double fps, string subtype = "NV12") =>
        new(width, height, fps, subtype);

    [Fact]
    public void Prefers_480p_at_15_fps_over_the_1080p_default()
    {
        var choice = CameraFormatPolicy.Choose(new[]
        {
            F(1920, 1080, 30),
            F(1280, 720, 30),
            F(640, 480, 30),
            F(640, 480, 15),
            F(320, 240, 15),
        });

        Assert.Equal(F(640, 480, 15), choice);
    }

    [Fact]
    public void Prefers_uncompressed_over_mjpg_at_the_same_size_and_rate()
    {
        var choice = CameraFormatPolicy.Choose(new[]
        {
            F(640, 480, 15, "MJPG"),
            F(640, 480, 15, "YUY2"),
        });

        Assert.Equal("YUY2", choice!.Subtype);
    }

    [Fact]
    public void Takes_30_fps_when_15_is_not_offered()
    {
        var choice = CameraFormatPolicy.Choose(new[] { F(640, 480, 30), F(1280, 720, 15) });

        Assert.Equal(F(640, 480, 30), choice);
    }

    [Fact]
    public void Falls_back_to_the_smallest_format_when_nothing_is_480p_or_less()
    {
        var choice = CameraFormatPolicy.Choose(new[] { F(1920, 1080, 30), F(1280, 720, 30) });

        Assert.Equal(F(1280, 720, 30), choice);
    }

    [Fact]
    public void Ignores_rates_outside_10_to_30_fps()
    {
        var choice = CameraFormatPolicy.Choose(new[] { F(640, 480, 60), F(640, 480, 5), F(320, 240, 30) });

        Assert.Equal(F(320, 240, 30), choice);
    }

    [Fact]
    public void Prefers_mjpg_over_h264_and_hevc_at_the_same_size()
    {
        var choice = CameraFormatPolicy.Choose(new[]
        {
            F(640, 480, 15, "H264"),
            F(640, 480, 15, "HEVC"),
            F(640, 480, 15, "H265"),
            F(640, 480, 15, "MJPG"),
        });

        Assert.Equal("MJPG", choice!.Subtype);
    }

    [Fact]
    public void Prefers_an_ordinary_subtype_over_mjpg_and_h264()
    {
        var choice = CameraFormatPolicy.Choose(new[]
        {
            F(640, 480, 15, "H264"),
            F(640, 480, 15, "MJPG"),
            F(640, 480, 15, "RGB24"),
        });

        Assert.Equal("RGB24", choice!.Subtype);
    }

    [Fact]
    public void Returns_null_when_nothing_is_usable()
    {
        Assert.Null(CameraFormatPolicy.Choose(Array.Empty<CameraFormatOption>()));
        Assert.Null(CameraFormatPolicy.Choose(new[] { F(640, 480, 60) }));
    }
}
