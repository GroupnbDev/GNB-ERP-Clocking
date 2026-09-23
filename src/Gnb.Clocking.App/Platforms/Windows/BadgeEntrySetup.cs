using Microsoft.Maui.Handlers;

namespace Gnb.Clocking.App;

public static class BadgeEntrySetup
{
    public static void Configure()
    {
        EntryHandler.Mapper.AppendToMapping("GroupNbBadge", (handler, view) =>
        {
            handler.PlatformView.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
            handler.PlatformView.Background = null;
        });
    }
}
