using Gnb.Clocking.Domain.Clocking;
using Microsoft.Maui.Graphics;

namespace Gnb.Clocking.App.ViewModels;

public sealed class PunchRow
{
    public required string Key { get; init; }
    public required string Initials { get; init; }
    public required string Name { get; init; }
    public required string Action { get; init; }
    public required string Time { get; init; }
    public required string Detail { get; init; }
    public required Color Accent { get; init; }
    public required Color Tint { get; init; }
    public string? PhotoPath { get; init; }
    public bool HasPhoto { get; init; }
    public bool ShowInitials { get; init; }

    /// <summary>True for a punch that arrived after the first load; the list animates it in once.</summary>
    public bool IsFresh { get; set; }

    /// <summary>Saved on this device but not yet accepted by the server.</summary>
    public bool ShowSyncNote { get; init; }
    public string SyncNote { get; init; } = string.Empty;
    public Color SyncNoteColor { get; init; } = Colors.Transparent;

    public static PunchRow From(ClockEvent clockEvent)
    {
        var arrived = clockEvent.Action == ClockAction.In;
        var detail = !arrived && clockEvent.HoursWorked is double hours
            ? $"{clockEvent.Assignment} · {hours:0.##}h"
            : clockEvent.Assignment;

        var accent = Color.FromArgb(arrived ? "#C70000" : "#718096");
        var pending = clockEvent.PendingSync || clockEvent.NeedsAttention;
        return new PunchRow
        {
            Key = $"{clockEvent.CandidateId}|{clockEvent.Action}|{clockEvent.At.UtcTicks}",
            ShowSyncNote = pending,
            SyncNote = clockEvent.NeedsAttention ? "Needs attention" : "Saved here, not synced",
            SyncNoteColor = Color.FromArgb(clockEvent.NeedsAttention ? "#C70000" : "#F59E0B"),
            Initials = clockEvent.Initials,
            Name = clockEvent.CandidateName,
            Action = clockEvent.IsExtra
                ? arrived ? "Extra in" : "Extra out"
                : arrived ? "Clock in" : "Clock out",
            Time = clockEvent.At.ToLocalTime().ToString("h:mm tt"),
            Detail = detail,
            Accent = accent,
            Tint = accent.WithAlpha(0.14f),
            PhotoPath = clockEvent.PhotoAbsolutePath,
            HasPhoto = !string.IsNullOrWhiteSpace(clockEvent.PhotoAbsolutePath),
            ShowInitials = string.IsNullOrWhiteSpace(clockEvent.PhotoAbsolutePath)
        };
    }
}
