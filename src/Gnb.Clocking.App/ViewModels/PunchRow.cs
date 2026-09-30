using Gnb.Clocking.Domain.Clocking;
using Gnb.Clocking.App.Theming;
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
    public string? PhotoPath { get; init; }
    public bool HasPhoto { get; init; }
    public bool ShowInitials { get; init; }

    /// <summary>True for a punch that arrived after the first load; the list animates it in once.</summary>
    public bool IsFresh { get; set; }

    /// <summary>Saved on this device but not yet accepted by the server (Warning).</summary>
    public bool ShowPendingNote { get; init; }

    /// <summary>The server refused it on sync (Error). Staff decide what happens to it.</summary>
    public bool ShowAttentionNote { get; init; }

    public static PunchRow From(ClockEvent clockEvent)
    {
        var arrived = clockEvent.Action == ClockAction.In;
        var detail = !arrived && clockEvent.HoursWorked is double hours
            ? $"{clockEvent.Assignment} · {hours:0.##}h"
            : clockEvent.Assignment;

        // Clock in and out are normal events: brand for in, neutral slate for out. Never a status colour.
        var accent = arrived ? StatusColors.Brand : Color.FromArgb("#475569");
        return new PunchRow
        {
            Key = $"{clockEvent.CandidateId}|{clockEvent.Action}|{clockEvent.At.UtcTicks}",
            ShowPendingNote = clockEvent.PendingSync && !clockEvent.NeedsAttention,
            ShowAttentionNote = clockEvent.NeedsAttention,
            Initials = clockEvent.Initials,
            Name = clockEvent.CandidateName,
            Action = clockEvent.IsExtra
                ? arrived ? "Extra in" : "Extra out"
                : arrived ? "Clock in" : "Clock out",
            Time = clockEvent.At.ToLocalTime().ToString("h:mm tt"),
            Detail = detail,
            Accent = accent,
            PhotoPath = clockEvent.PhotoAbsolutePath,
            HasPhoto = !string.IsNullOrWhiteSpace(clockEvent.PhotoAbsolutePath),
            ShowInitials = string.IsNullOrWhiteSpace(clockEvent.PhotoAbsolutePath)
        };
    }
}
