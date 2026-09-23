using Microsoft.Maui.Handlers;
using UIKit;

namespace Gnb.Clocking.App;

public static class BadgeEntrySetup
{
    public static void Configure()
    {
        EntryHandler.Mapper.AppendToMapping("GroupNbBadge", (handler, view) =>
        {
            handler.PlatformView.BorderStyle = UITextBorderStyle.None;
            handler.PlatformView.BackgroundColor = UIColor.Clear;
        });
    }
}
