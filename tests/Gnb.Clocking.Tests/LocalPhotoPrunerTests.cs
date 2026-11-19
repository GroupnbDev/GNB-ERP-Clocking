using Gnb.Clocking.Application.Clocking;
using Gnb.Clocking.Infrastructure.Photos;
using Xunit;

namespace Gnb.Clocking.Tests;

public sealed class LocalPhotoPrunerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gnb-prune-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string Photo(int userId, string day, string file = "ClockIN.jpeg")
    {
        var folder = Path.Combine(_root, "Users", userId.ToString(), "Records", day);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, file);
        File.WriteAllBytes(path, new byte[] { 0xFF, 0xD8, 0xFF });
        return path;
    }

    [Fact]
    public void Default_window_is_one_week()
    {
        Assert.Equal(7, LocalPhotoPruner.DefaultKeepDays);
        Assert.Equal(480, ClockPhotoEncode.MaxLongEdge);
        Assert.Equal(0.4f, ClockPhotoEncode.JpegQuality);
    }

    [Fact]
    public void Removes_days_older_than_the_window_and_keeps_recent_ones()
    {
        var old = Photo(1042, "2026-09-01");
        var edge = Photo(1042, "2026-09-08");
        var recent = Photo(1042, "2026-09-22", "ClockOut.jpeg");

        var removed = LocalPhotoPruner.Prune(_root, keepDays: 14, today: new DateTime(2026, 9, 22));

        Assert.Equal(1, removed);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(edge));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void Removes_empty_user_folders()
    {
        Photo(7, "2025-01-01");

        LocalPhotoPruner.Prune(_root, keepDays: 14, today: new DateTime(2026, 9, 22));

        Assert.False(Directory.Exists(Path.Combine(_root, "Users", "7")));
    }

    [Fact]
    public void Does_nothing_without_a_photo_folder_or_with_retention_off()
    {
        Assert.Equal(0, LocalPhotoPruner.Prune(_root, keepDays: 14, today: DateTime.Today));

        var photo = Photo(1, "2020-01-01");
        Assert.Equal(0, LocalPhotoPruner.Prune(_root, keepDays: 0, today: DateTime.Today));
        Assert.True(File.Exists(photo));
    }

    [Fact]
    public void Removes_thumbnails_with_the_day_folder()
    {
        var old = Photo(1042, "2026-09-01");
        var thumb = Path.Combine(Path.GetDirectoryName(old)!, "ClockIN.thumb.jpeg");
        File.WriteAllBytes(thumb, new byte[] { 0xFF, 0xD8, 0xFF });

        LocalPhotoPruner.Prune(_root, keepDays: 14, today: new DateTime(2026, 9, 22));

        Assert.False(File.Exists(old));
        Assert.False(File.Exists(thumb));
    }

    private string ServerPhoto(string fileName, DateTime? lastWrite = null)
    {
        var folder = Path.Combine(_root, "Users", "7", "clock", "users", "6", "7", "2026-09");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);
        File.WriteAllBytes(path, new byte[] { 0xFF, 0xD8, 0xFF });
        if (lastWrite is DateTime when)
            File.SetLastWriteTime(path, when);
        return path;
    }

    [Fact]
    public void Month_folder_photos_are_pruned_by_the_day_in_the_file_name()
    {
        var old = ServerPhoto("2026-09-01-in-0123456789abcdef.jpeg");
        var oldThumb = ServerPhoto("2026-09-01-in-0123456789abcdef.thumb.jpeg");
        var compact = ServerPhoto("20260902_out.jpeg");
        var recent = ServerPhoto("2026-09-21-out-0123456789abcdef.jpeg");

        var removed = LocalPhotoPruner.Prune(_root, keepDays: 14, today: new DateTime(2026, 9, 22));

        Assert.Equal(3, removed);
        Assert.False(File.Exists(old));
        Assert.False(File.Exists(oldThumb));
        Assert.False(File.Exists(compact));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void Files_without_a_date_use_the_last_write_time()
    {
        var old = ServerPhoto("photo-old.jpeg", new DateTime(2026, 8, 1));
        var recent = ServerPhoto("photo-new.jpeg", new DateTime(2026, 9, 21));

        LocalPhotoPruner.Prune(_root, keepDays: 14, today: new DateTime(2026, 9, 22));

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }
}
