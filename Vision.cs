using System.Drawing;

internal static partial class Program
{
    record Step(string Name, string Image, int X, int Y, int Left, int Top, int Width, int Height);
    static readonly Step[] HomeSteps =
    [
        new("title-start", "title-logo-inner.png", 480, 461, 838, 21, 16, 16),
        new("login-bonus", "login-bonus.png", 850, 450, 389, 113, 443, 53),
        new("campaign-bonus", "campaign-bonus.png", 850, 450, 399, 306, 154, 18),
        new("beginner-bonus", "beginner-bonus.png", 850, 450, 154, 214, 615, 40)
    ];
    static bool Matches(Bitmap screen, Step step)
    {
        using var expected = new Bitmap(Path.Combine(Root, "templates", step.Image));
        var original = DifferenceAt(screen, expected, step.Left, step.Top, step.Width, step.Height);
        if (original < 12 || step.Width <= 20) return original < 12;
        // Original Program.Difference local alignment fallback.
        foreach (var dx in new[] { -2, 0, 2 }) foreach (var dy in new[] { -2, 0, 2 })
            if (DifferenceAt(screen, expected, step.Left + dx, step.Top + dy, step.Width, step.Height) < 12) return true;
        return false;
    }
    // Retained from Program.cs in the source project.
    static double DifferenceAt(Bitmap screen, Bitmap expected, int left, int top, int width, int height)
    {
        if (left < 0 || top < 0 || left + width > screen.Width || top + height > screen.Height || width > expected.Width || height > expected.Height) return 255;
        long error = 0; int samples = 0;
        for (int y = 0; y < height; y += 2) for (int x = 0; x < width; x += 2)
        {
            var a = screen.GetPixel(left + x, top + y); var b = expected.GetPixel(x, y);
            error += Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B); samples += 3;
        }
        return (double)error / samples;
    }
    // Same sampled error as DifferenceAt, with an early exit once passing is impossible.
    // Adding a second layout position must not make the reader's 1-second lease checks slow.
    static bool DifferenceBelow(Bitmap screen, Bitmap expected, int left, int top, int width, int height, double limit)
    {
        long error = 0;
        var budget = ((width + 1) / 2) * ((height + 1) / 2) * 3 * limit;
        for (var y = 0; y < height; y += 2) for (var x = 0; x < width; x += 2)
        {
            var a = screen.GetPixel(left + x, top + y); var b = expected.GetPixel(x, y);
            error += Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
            if (error >= budget) return false;
        }
        return true;
    }
    // Retained from Resume.cs: area globe plus the cyan live button.
    static bool IsAreaMain(Bitmap screen)
    {
        if (screen.Width != 960 || screen.Height != 540) return false;
        if (!Matches(screen, new("home-globe", "area-open.png", 0, 0, 18, 18, 27, 27))) return false;
        var cyan = 0;
        for (var y = 477; y < 501; y++) for (var x = 883; x < 929; x++)
        { var p = screen.GetPixel(x, y); if (p.G > 170 && p.B > 145 && p.R < 145 && p.G - p.R > 45) cyan++; }
        return cyan >= 60;
    }

    record HomeAction(string Name, int X, int Y);
    static HomeAction? FindHomeAction(Bitmap screen)
    {
        if (screen.Width != 960 || screen.Height != 540) return null;
        if (IsNewDataNotice(screen)) return null;
        if (IsStoryDownload(screen)) return new("startup-data-download", 571, 405);
        if (FindNoticeAction(screen) is { } notice) return notice;
        foreach (var step in HomeSteps)
        {
            if (!Matches(screen, step)) continue;
            // Original title anchor is static. TAP TO START has animated artwork behind it.
            return new(step.Name, step.X, step.Y);
        }
        return null;
    }

    static bool IsDialogGray(Color p) => p.R is >= 210 and <= 248 && Math.Abs(p.R - p.G) <= 7 && p.B - p.R is >= 3 and <= 18;
    static bool IsButtonCyan(Color p) => p.G > 175 && p.B > 150 && p.R < 170 && p.G - p.R > 50 && p.B - p.R > 35;
    static bool IsXInk(Color p) => p.R < 145 && p.G < 145 && p.B < 185 && Math.Abs(p.R - p.G) < 40;

    // Content-independent detection requested by the user: connected gray panel + cyan button + an X glyph.
    // Only the X is returned. Text and button labels do not participate in the trigger.
    static Point? FindColorDialogClose(Bitmap screen)
    {
        if (screen.Width != 960 || screen.Height != 540) return null;
        const int width = 480, height = 270;
        var gray = new bool[width * height];
        for (var y = 25; y < 255; y++) for (var x = 20; x < 460; x++) gray[y * width + x] = IsDialogGray(screen.GetPixel(x * 2, y * 2));
        var queue = new int[gray.Length];
        var panels = new List<Rectangle>();
        for (var seed = 0; seed < gray.Length; seed++)
        {
            if (!gray[seed]) continue;
            int head = 0, tail = 0, left = width, top = height, right = 0, bottom = 0;
            queue[tail++] = seed; gray[seed] = false;
            while (head < tail)
            {
                var pos = queue[head++]; var x = pos % width; var y = pos / width;
                left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                void Add(int n) { if (gray[n]) { gray[n] = false; queue[tail++] = n; } }
                if (x > 0) Add(pos - 1); if (x + 1 < width) Add(pos + 1);
                if (y > 0) Add(pos - width); if (y + 1 < height) Add(pos + width);
            }
            var panel = Rectangle.FromLTRB(left * 2, top * 2, right * 2 + 2, bottom * 2 + 2);
            if (tail >= 5000 && panel.Width >= 280 && panel.Height >= 100 && panel.Width <= 820 &&
                panel.Height <= 460 && tail * 4 > panel.Width * panel.Height * .55 && panel.Contains(480, panel.Top + panel.Height / 2)) panels.Add(panel);
        }
        using var cross = new Bitmap(Path.Combine(Root, "templates", "dialog-close-x.png"));
        var closes = new List<Point>();
        foreach (var panel in panels)
        {
            var cyan = 0; var cyanLeft = 960; var cyanRight = 0;
            for (var y = panel.Top + panel.Height / 2; y < panel.Bottom - 8; y += 2)
                for (var x = panel.Left + 12; x < panel.Right - 12; x += 2)
                    if (IsButtonCyan(screen.GetPixel(x, y))) { cyan++; cyanLeft = Math.Min(cyanLeft, x); cyanRight = Math.Max(cyanRight, x); }
            if (cyan < 300 || cyanRight - cyanLeft < 75) continue;
            var matches = new List<(Point Point, int Bad)>();
            for (var y = panel.Top + 2; y <= Math.Min(panel.Top + 40, panel.Bottom - 18); y++)
                for (var x = Math.Max(panel.Left + panel.Width / 2, panel.Right - 75); x <= panel.Right - 18; x++)
                {
                    int bad = 0, ink = 0;
                    for (var dy = 0; dy < 18 && bad <= 10; dy++) for (var dx = 0; dx < 18; dx++)
                    {
                        var observed = IsXInk(screen.GetPixel(x + dx, y + dy));
                        if (observed) ink++;
                        if (observed != IsXInk(cross.GetPixel(dx, dy))) bad++;
                    }
                    if (bad <= 10 && ink >= 30) matches.Add((new(x + 9, y + 9), bad));
                }
            if (matches.Count == 0) continue;
            var best = matches.OrderBy(m => m.Bad).First().Point;
            if (matches.Any(m => Math.Abs(m.Point.X - best.X) > 4 || Math.Abs(m.Point.Y - best.Y) > 4)) return null;
            closes.Add(best);
        }
        return closes.Count == 1 ? closes[0] : null;
    }
}
