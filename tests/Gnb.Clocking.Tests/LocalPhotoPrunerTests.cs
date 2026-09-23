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

    private string Photo(int candidateId, string day, string file = "ClockIN.jpeg")
    {
        var folder = Path.Combine(_root, "Candidates", candidateId.ToString(), "Records", day);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, file);
        File.WriteAllBytes(path, new byte[] { 0xFF, 0xD8, 0xFF });
        return path;
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
    public void Removes_empty_candidate_folders_and_ignores_unexpected_names()
    {
        Photo(7, "2025-01-01");
        var stray = Photo(8, "not-a-date");

        LocalPhotoPruner.Prune(_root, keepDays: 14, today: new DateTime(2026, 9, 22));

        Assert.False(Directory.Exists(Path.Combine(_root, "Candidates", "7")));
        Assert.True(File.Exists(stray));
    }

    [Fact]
    public void Does_nothing_without_a_photo_folder_or_with_retention_off()
    {
        Assert.Equal(0, LocalPhotoPruner.Prune(_root, keepDays: 14, today: DateTime.Today));

        var photo = Photo(1, "2020-01-01");
        Assert.Equal(0, LocalPhotoPruner.Prune(_root, keepDays: 0, today: DateTime.Today));
        Assert.True(File.Exists(photo));
    }
}
