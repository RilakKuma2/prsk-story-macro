using System.Drawing;

internal static partial class Program
{
    static void VerifyStoryMenuClose(Action<bool,string> assert)
    {
        using var menu = new Bitmap(Path.Combine(Root,"tests","fixtures","story-reader-menu-open.png"));
        using var reader = new Bitmap(Path.Combine(Root,"tests","fixtures","story-reader-fade.png"));
        var close = new HomeAction("story-menu-close",940,20);
        assert(FindStoryMenuClose(menu)==close && ReaderBlockReason(menu)=="story-menu-open" && !CanTapStory(menu),
            "the actual expanded menu is recognized by auto and skip, and dialogue tapping is paused");
        foreach (var (x,y,width,height) in new[] { (790,100,145,45), (790,202,145,45) })
        {
            using var missing = new Bitmap(menu);
            using (var graphics = Graphics.FromImage(missing)) graphics.FillRectangle(Brushes.Black,x,y,width,height);
            assert(FindStoryMenuClose(missing)==null,"both auto and skip buttons are required before the X can be tapped");
        }
        foreach (var color in new[] {Color.Black,Color.White,Color.FromArgb(240,70,150)})
        {
            using var changed = new Bitmap(960,540);
            using (var graphics = Graphics.FromImage(changed))
            {
                graphics.Clear(color);
                using var auto = menu.Clone(new Rectangle(795,102,134,40),menu.PixelFormat);
                using var skip = menu.Clone(new Rectangle(795,206,134,40),menu.PixelFormat);
                graphics.DrawImage(auto,new Rectangle(795,102,134,40),new Rectangle(0,0,134,40),GraphicsUnit.Pixel);
                graphics.DrawImage(skip,new Rectangle(795,206,134,40),new Rectangle(0,0,134,40),GraphicsUnit.Pixel);
            }
            assert(FindStoryMenuClose(changed)==close,"menu recognition ignores the scene and translucent X background");
        }
        foreach (var name in new[] {"story-reader-fade","top-story-episodes","story-voice-dialog","title-menu-protected","virtual-live-dark"})
        {
            using var other = new Bitmap(Path.Combine(Root,"tests","fixtures",name+".png"));
            assert(FindStoryMenuClose(other)==null,"unrelated screen cannot trigger story menu close: "+name);
        }
        var saved = AutoResumeEnabled;
        try
        {
            foreach (var autoResume in new[] {false,true})
            {
                AutoResumeEnabled = autoResume;
                DateTime? opened = null;
                var start = DateTime.UtcNow;
                assert(DelayedStoryMenuClose(menu,start,ref opened)==null &&
                    DelayedStoryMenuClose(menu,start.AddMilliseconds(19999),ref opened)==null &&
                    DelayedStoryMenuClose(menu,start.AddSeconds(20),ref opened)==close,
                    $"menu closes only after 20 seconds, regardless of auto resume={autoResume}");
                assert(DelayedStoryMenuClose(reader,start.AddSeconds(21),ref opened)==null && opened==null,
                    "a menu that disappears clears its pending close");
                assert(DelayedStoryMenuClose(menu,start.AddSeconds(22),ref opened)==null &&
                    DelayedStoryMenuClose(menu,start.AddSeconds(41),ref opened)==null &&
                    DelayedStoryMenuClose(menu,start.AddSeconds(42),ref opened)==close,
                    "a reopened menu starts a fresh 20-second wait");
            }
        }
        finally { AutoResumeEnabled = saved; }
    }
}
