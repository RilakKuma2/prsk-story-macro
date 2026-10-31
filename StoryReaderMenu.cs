using System.Drawing;

internal static partial class Program
{
    static readonly TimeSpan StoryMenuCloseDelay = TimeSpan.FromSeconds(20);
    static HomeAction? FindStoryMenuClose(Bitmap screen)
    {
        if (screen.Width != 960 || screen.Height != 540) return null;
        // These two opaque labels identify the menu; the X has a variable translucent backdrop.
        return Matches(screen,new("story-menu-auto","story-menu-auto.png",0,0,811,110,110,25)) &&
            Matches(screen,new("story-menu-skip","virtual-intro-menu-skip.png",0,0,812,212,109,25))
            ? new("story-menu-close",940,20) : null;
    }
    static HomeAction? DelayedStoryMenuClose(Bitmap screen, DateTime now, ref DateTime? openedAt)
    {
        var close = FindStoryMenuClose(screen);
        if (close == null) { openedAt = null; return null; }
        openedAt ??= now;
        return now - openedAt.Value >= StoryMenuCloseDelay ? close : null;
    }
}
