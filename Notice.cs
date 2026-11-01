using System.Drawing;

internal static partial class Program
{
    // Same headings, buttons and coordinates as the original Resume.DismissNotice.
    static HomeAction? FindNoticeAction(Bitmap screen)
    {
        if (screen.Width != 960 || screen.Height != 540 || IsNewDataNotice(screen) || IsStoryVoice(screen) || (ReadingMainStory && IsStoryMvPause(screen))) return null;
        // Side story voice/unlock dialogs have their own explicit buttons; never close them through the generic X.
        if (ReadingSideStory && (IsSideVoice(screen) || IsSideUnlockConfirm(screen) || IsSidePerformanceResult(screen) || IsSideRewardResult(screen))) return null;
        if (Matches(screen, new("feedback", "feedback-heading.png", 0, 0, 425, 153, 112, 18)) &&
            Matches(screen, new("decline", "feedback-do-not-send.png", 0, 0, 362, 397, 56, 18)))
            return new("feedback-do-not-send", 390, 406);
        if (Matches(screen, new("challenge", "challenge-unlock-heading.png", 0, 0, 334, 225, 292, 18)) &&
            Matches(screen, new("challenge-close", "challenge-unlock-close.png", 0, 0, 365, 309, 50, 18)))
            return new("challenge-notice-close", 390, 318);
        if (Matches(screen, new("notice", "notice-header.png", 0, 0, 110, 22, 86, 20)) &&
            Matches(screen, new("close", "notice-close.png", 0, 0, 852, 24, 18, 18))) return new("notice-x", 860, 33);
        if ((Matches(screen, new("rating", "rating-heading.png", 0, 0, 425, 105, 112, 16)) ||
             Matches(screen, new("rating-prefix", "rating-heading-prefix.png", 0, 0, 425, 105, 64, 16))) &&
             Matches(screen, new("rating-x", "rating-close.png", 0, 0, 745, 22, 18, 18))) return new("rating-x", 753, 30);
        if (!IsStoryDownload(screen) && FindColorDialogClose(screen) is { } close) return new("color-dialog-x", close.X, close.Y);
        return null;
    }
    static async Task<bool> DismissNotice(Bitmap screen)
    {
        var action = FindNoticeAction(screen);
        if (action == null || !await GuardedTap(action, FindNoticeAction)) return false;
        // Original TapUntilClosed: do not tap through the notice into the underlying scene.
        for (var check = 0; check < 6; check++)
        {
            await Task.Delay(500, Stop.Token);
            using var current = await Capture();
            if (FindNoticeAction(current) != action) break;
        }
        return true;
    }
}
