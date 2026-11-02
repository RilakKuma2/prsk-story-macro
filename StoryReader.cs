using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;

internal static partial class Program
{
    static bool IsStoryReader(Bitmap screen)
    {
        if (screen.Width != 960 || screen.Height != 540) return false;
        using var expected = new Bitmap(Path.Combine(Root, "templates", "story-reading.png"));
        var low = 255; var high = 0;
        for (var y = 0; y < 12; y++) for (var x = 0; x < 20; x++)
        { var p = screen.GetPixel(918 + x, 26 + y); var v = (p.R + p.G + p.B) / 3; low = Math.Min(low, v); high = Math.Max(high, v); }
        if (high - low < 20 || high < 85) return false;
        var bad = 0; var ink = 0;
        for (var y = 0; y < 12; y++) for (var x = 0; x < 20; x++)
        {
            var p = screen.GetPixel(918 + x, 26 + y); var e = expected.GetPixel(x, y);
            var observed = (p.R + p.G + p.B) / 3 < (low + high) / 2;
            var glyph = e.R < 160 && e.G < 160 && e.B < 180;
            if (glyph) ink++; if (observed != glyph) bad++;
        }
        return ink > 10 && bad < 20;
    }
    static bool IsDarkStoryTransition(Bitmap screen)
    {
        var dark = 0; var total = 0;
        for (var y = 60; y < 480; y += 20) for (var x = 40; x < 900; x += 20)
        { var p = screen.GetPixel(x, y); total++; if (p.R < 25 && p.G < 25 && p.B < 25) dark++; }
        return dark > total * .85;
    }
    static bool IsStoryVoice(Bitmap screen) =>
        Matches(screen, new("voice-heading", "story-voice-heading.png", 0, 0, 256, 127, 447, 19)) &&
        Matches(screen, new("no-voice", "story-no-voice.png", 0, 0, 438, 395, 84, 20));
    static bool IsStoryMvPause(Bitmap screen) =>
        Matches(screen, new("mv-pause-text", "story-mv-pause-text.png", 0, 0, 384, 234, 182, 36)) &&
        Matches(screen, new("mv-pause-skip", "story-mv-pause-skip.png", 0, 0, 359, 310, 64, 16));
    static bool ReadingMainStory;
    static bool HasMainStoryBadgeOffset(Bitmap screen)
    {
        if (!IsEpisodeList(screen)) return false;
        using var unread = new Bitmap(Path.Combine(Root, "templates", "story-unread-badge.png"));
        for (var y = 75; y < 490 - unread.Height; y++)
            for (var x = 874; x <= 877; x++)
                if (DifferenceAt(screen, unread, x, y, unread.Width, unread.Height) < 9) return true;
        using var skip = new Bitmap(Path.Combine(Root, "templates", "story-skip-badge.png"));
        var hasMainSkip = false;
        for (var y = 75; y < 490 - skip.Height; y++)
            for (var x = 834; x <= 846; x++)
            {
                var diff = DifferenceAt(screen, skip, x, y, skip.Width, skip.Height);
                if (diff < 9) return false;
                if (diff < 16) hasMainSkip = true;
            }
        return hasMainSkip;
    }
    static bool IsMainStoryEpisodeList(Bitmap screen) =>
        IsEpisodeList(screen) &&
        (HasMainStoryBadgeOffset(screen) ||
         Matches(screen, new("unit-main-banner", "unit-main-story-banner.png", 0, 0, 88, 493, 114, 18)));
    static bool StoryContinuousChecked(Bitmap screen)
    {
        var pink = 0;
        for (var y = 346; y < 366; y++) for (var x = 361; x < 383; x++)
        { var p = screen.GetPixel(x, y); if (p.R > 210 && p.G < 155 && p.B > 110 && p.R - p.G > 70) pink++; }
        return pink >= 25;
    }
    static bool StoryContinuousOptionVisible(Bitmap screen)
    {
        if(StoryContinuousChecked(screen))return true;
        var white=0;
        for(var y=346;y<366;y++)for(var x=361;x<383;x++)
        {var p=screen.GetPixel(x,y);if(p.R>245 && p.G>245 && p.B>245)white++;}
        return white>=200;
    }
    static bool IsEpisodeList(Bitmap screen)
    {
        if (screen.Width != 960 || screen.Height != 540 || IsStoryVoice(screen) || IsStoryMvPause(screen) || IsStoryRewardsBanner(screen) ||
            IsStoryReceipt(screen) || IsStoryReward(screen) || IsReceivedItems(screen) || FindNoticeAction(screen) != null) return false;
        return HasEpisodeListAnchor(screen);
    }
    static bool HasEpisodeListAnchor(Bitmap screen)
    {
        if (screen.Width != 960 || screen.Height != 540) return false;
        using var expected = new Bitmap(Path.Combine(Root, "templates", "event-stories-complete.png"));
        // World Link's tabbed layout shifts the same information icon two pixels left.
        // Reuse the original template tolerance; episode numbers and headings are irrelevant.
        for (var y = 85; y < 465; y++) foreach (var x in new[] { 905, 903, 907 })
            if (DifferenceBelow(screen, expected, x, y, 18, 22, 10)) return true;
        return false;
    }
    static bool IsStoryRewardsBanner(Bitmap screen)
    {
        if (screen.Width != 960 || screen.Height != 540) return false;
        // The item artwork is opaque. Never require the translucent banner text or page behind it.
        // Crystal recognition includes x50; tickets move horizontally as the number of items changes.
        return HasStoryRewardItem(screen, "story-reward-crystal-50.png", new(450, 221, 69, 75), new(23, 51, 37, 16)) ||
            HasStoryRewardItem(screen, "story-reward-banner-ticket.png", new(40, 228, 880, 50));
    }
    static bool HasStoryRewardItem(Bitmap screen, string image, Rectangle area, Rectangle? quantity = null)
    {
        using var expected = new Bitmap(Path.Combine(Root, "templates", image));
        using var count = quantity is { } region ? expected.Clone(region, expected.PixelFormat) : null;
        var anchors = new List<(int X, int Y, Color Color)>();
        foreach (var y in new[] { expected.Height / 4, expected.Height / 2, expected.Height * 3 / 4 })
            foreach (var x in new[] { expected.Width / 4, expected.Width / 2, expected.Width * 3 / 4 })
                anchors.Add((x, y, expected.GetPixel(x, y)));
        for (var top = area.Top; top + expected.Height <= area.Bottom; top++)
            for (var left = area.Left; left + expected.Width <= area.Right; left++)
            {
                var candidate = true;
                foreach (var anchor in anchors)
                {
                    var p = screen.GetPixel(left + anchor.X, top + anchor.Y);
                    if (Math.Abs(p.R - anchor.Color.R) + Math.Abs(p.G - anchor.Color.G) + Math.Abs(p.B - anchor.Color.B) <= 80) continue;
                    candidate = false; break;
                }
                if (!candidate || !DifferenceBelow(screen, expected, left, top, expected.Width, expected.Height, 8)) continue;
                // The icon occupies most of the crop: also compare x50 tightly so x25/missing digits cannot pass.
                if (quantity is { } q && !DifferenceBelow(screen, count!, left + q.X, top + q.Y, q.Width, q.Height, 5)) continue;
                return true;
            }
        return false;
    }
    static bool HasRewardsObtainedText(Bitmap screen)
    {
        if (screen.Width != 960 || screen.Height != 540) return false;
        // This completion banner has no OK button. Its translucent backdrop and reward
        // quantities vary, so compare only the white "rewards obtained" lettering mask.
        using var expected = new Bitmap(Path.Combine(Root, "templates", "story-rewards-banner-text.png"));
        static bool White(Color color) => color.R >= 210 && color.G >= 210 && color.B >= 210;
        var ink = 0; var missing = 0; var extra = 0;
        for (var y = 0; y < expected.Height; y++) for (var x = 0; x < expected.Width; x++)
        {
            var glyph = White(expected.GetPixel(x, y));
            var observed = White(screen.GetPixel(399 + x, 307 + y));
            if (glyph) { ink++; if (!observed) missing++; }
            else if (observed) extra++;
        }
        return ink >= 100 && missing <= ink * .15 && extra <= ink * .2;
    }
    static List<int> EpisodeLockedRows(Bitmap screen)
    {
        var rows = new List<int>();
        if (!IsEpisodeList(screen)) return rows;
        using var glyph = new Bitmap(Path.Combine(Root, "templates", "episode-lock-glyph.png"));
        static bool White(Color p) => p.R > 235 && p.G > 235 && p.B > 235;
        var samples = new List<(int X, int Y, bool Ink)>();
        for (var y = 0; y < glyph.Height; y += 2) for (var x = 0; x < glyph.Width; x += 2)
            samples.Add((x, y, White(glyph.GetPixel(x, y))));
        var ink = samples.First(p => p.Ink);
        for (var top = 75; top + glyph.Height < 490; top++)
        {
            foreach (var left in new[] { 672, 674, 676, 678, 680, 682, 684 })
            {
                if (!White(screen.GetPixel(left + ink.X, top + ink.Y))) continue;
                var bad = 0;
                foreach (var p in samples)
                    if (White(screen.GetPixel(left + p.X, top + p.Y)) != p.Ink && ++bad > samples.Count * .10) break;
                if (bad > samples.Count * .10) continue;
                rows.Add(top + glyph.Height / 2);
                top += glyph.Height / 2;
                break;
            }
        }
        return rows;
    }
    static List<int> EpisodeBadgeRows(Bitmap screen, bool skip, IReadOnlyList<int>? lockedRows = null)
    {
        var rows = new List<int>();
        if (!IsEpisodeList(screen)) return rows;
        using var expected = new Bitmap(Path.Combine(Root, "templates", skip ? "story-skip-badge.png" : "story-unread-badge.png"));
        var limit = skip ? 16 : 9;
        for (var y = 75; y < 490 - expected.Height; y++)
            for (var x = skip ? 834 : 874; x <= (skip ? 846 : 884); x++)
                if (DifferenceAt(screen, expected, x, y, expected.Width, expected.Height) < limit)
                { if (rows.Count == 0 || y - rows[^1] > 12) rows.Add(y); break; }
        if (rows.Count == 0) return rows;
        lockedRows ??= EpisodeLockedRows(screen);
        return rows.Where(y => !lockedRows.Any(locked => Math.Abs(locked - (y + 32)) < 38)).ToList();
    }
    static readonly Step StoryCrystal = new("crystal", "story-reward-crystal.png", 0, 0, 414, 228, 42, 44);
    static readonly Step StoryTicket = new("ticket", "story-reward-ticket.png", 0, 0, 495, 239, 52, 31);
    static bool IsStoryReward(Bitmap screen) => Matches(screen, StoryCrystal) && Matches(screen, StoryTicket);
    static bool IsStoryReceipt(Bitmap screen) => Matches(screen, StoryCrystal with { Left = 236, Top = 91 }) &&
        Matches(screen, StoryTicket with { Left = 229, Top = 190 }) && Matches(screen, new("receipt", "story-receipt-ok.png", 0, 0, 463, 481, 34, 20));
    static bool IsReceivedItems(Bitmap screen) => Matches(screen, new("received", "items-received-title.png", 0, 0, 340, 42, 280, 26)) &&
        Matches(screen, new("received-ok", "items-received-ok.png", 0, 0, 420, 478, 120, 28));
    static HomeAction? FindStoryRewardAction(Bitmap screen)
    {
        if (IsStoryRewardsBanner(screen)) return new("story-rewards-banner-close", 480, 315);
        if (IsStoryReceipt(screen)) return new("story-receipt-ok", 480, 492);
        if (IsStoryReward(screen)) return new("story-reward-ok", 480, 420);
        if (IsReceivedItems(screen)) return new("story-received-ok", 480, 491);
        return null;
    }
    static HomeAction? FindReaderAction(Bitmap screen)
    {
        if (ReadingSideStory && FindSideResultAction(screen) is { } result) return result;
        if (FindStoryRewardAction(screen) is { } reward) return reward;
        if (ReadingSideStory && IsSideVoice(screen)) return new("side-no-voice", 494, 491);
        if (IsStoryVoice(screen)) return StoryContinuousOptionVisible(screen) && !StoryContinuousChecked(screen)
            ? new("story-continuous-on", 372, 355) : new("story-no-voice", 480, 406);
        if (ReadingMainStory && IsStoryMvPause(screen)) return new("story-mv-skip", 390, 318);
        return null;
    }

    // Reused from the invitation macro: kernel event down/up packets, MuMu's rotated touch range and ~8 center taps/sec.
    // A separate screen watcher renews a one-second device-side lease; losing the host or reader expires input automatically.
    record StoryTouch(string Device, int MaxX, int MaxY, bool Pressure, bool Files, bool Tracking = true, bool Wide = true);
    static StoryTouch? ReaderTouch;
    static bool ReaderTouchSide;
    static string? AuthorizedAuxiliary;
    static string ReaderRemote(string name) => $"/data/local/tmp/sekai-storyskip-{Selected!.Index}-{name}";
    static byte[] EventData(bool wide, params (short type, short code, int value)[] events)
    {
        using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory);
        foreach (var e in events)
        {
            if (wide) { writer.Write(0L); writer.Write(0L); } else { writer.Write(0); writer.Write(0); }
            writer.Write(e.type); writer.Write(e.code); writer.Write(e.value);
        }
        return memory.ToArray();
    }
    static async Task<byte[]> Auxiliary(string[] args, bool cleanup = false)
    {
        var allowed = args.Length == 5 && args[0] == "-s" && args[1] == Serial && args[2] == "push" &&
            new[] { "down.bin", "up.bin", "taps.sh" }.Any(name => args[4] == ReaderRemote(name) &&
                args[3] == Path.Combine(Artifacts, name == "taps.sh" ? "story-taps.sh" : "story-touch-" + name));
        allowed |= args.Length == 4 && args[0] == "-s" && args[1] == Serial && args[2] == "shell" &&
            (args[3] == "sh " + ReaderRemote("taps.sh") || args[3] == ReaderLeaseCommand(true) || args[3] == ReaderLeaseCommand(false));
        if (!allowed || cleanup && args[3] != ReaderLeaseCommand(false)) throw new InvalidOperationException("허용하지 않은 스토리 입력 보조 명령입니다.");
        AuthorizedAuxiliary = string.Join('\0', args);
        try { return await RunBytes(Adb, args, cancellation: cleanup ? CancellationToken.None : null); } finally { AuthorizedAuxiliary = null; }
    }
    static string ReaderLeaseCommand(bool allow) => allow
        ? $"expr $(date +%s%3N) + 1000 > {ReaderRemote("lease-next")}; mv {ReaderRemote("lease-next")} {ReaderRemote("lease")}" : $"printf '0\\n' > {ReaderRemote("lease-next")}; mv {ReaderRemote("lease-next")} {ReaderRemote("lease")}";
    static Task<byte[]> SetReaderLease(bool allow) => Auxiliary(["-s", Serial, "shell", ReaderLeaseCommand(allow)], cleanup: !allow);
    static string? ReaderBlockReason(Bitmap screen)
    {
        if (IsNewDataNotice(screen)) return "new-data-notice";
        if (FindStoryMenuClose(screen) != null) return "story-menu-open";
        if (IsStoryMvPause(screen)) return "mv-pause-dialog";
        if (ReadingSideStory && (IsSideStoryList(screen) || IsSidePerformanceResult(screen) || IsSideRewardResult(screen) || IsSideVoice(screen) || IsSideUnlockConfirm(screen))) return "side-list-or-dialog";
        if (FindStoryRewardAction(screen) != null) return "story-reward";
        if (!IsStoryReader(screen)) return "ellipsis-not-recognized";
        if (FindNoticeAction(screen) != null) return "notice-open";
        if (IsStoryVoice(screen)) return "voice-dialog";
        if (IsEpisodeList(screen)) return "episode-list";
        if (FindColorDialogClose(screen) != null) return "gray-cyan-popup";
        return null;
    }
    static bool CanTapStory(Bitmap screen) => ReaderBlockReason(screen) == null;
    static bool CanProbeStoryMv(Bitmap screen) =>
        screen.Width == 960 && screen.Height == 540 && !IsStoryLoading(screen) && !IsStoryReader(screen) &&
        FindStoryMenuClose(screen) == null && FindNoticeAction(screen) == null && FindReaderAction(screen) == null &&
        !IsEpisodeList(screen) && ProtectedScreenReason(screen) == null;
    static bool ShouldProbeStoryMv(Bitmap screen, DateTime transitionSince, DateTime now, int probeCount) =>
        ReadingMainStory && probeCount < 2 && now - transitionSince >= TimeSpan.FromSeconds((probeCount + 1) * 10) && CanProbeStoryMv(screen);
    static async Task<bool> ProbeStoryMvPause()
    {
        if (!ReadingMainStory) return false;
        await Guard();
        if (!(await Foreground()).Contains(Package + "/")) return false;
        using var verify = await Capture();
        if (!CanProbeStoryMv(verify)) return false;
        Log("메인스토리 … 10초 미감지 · MV 확인을 위해 화면 중앙 2회 터치");
        for (var tap = 0; tap < 2; tap++)
        {
            Stop.Token.ThrowIfCancellationRequested();
            AuthorizedInput = "input tap 480 270";
            try { await Shell(AuthorizedInput); }
            finally { AuthorizedInput = null; }
            if (tap == 0) await Task.Delay(250, Stop.Token);
        }
        await Task.Delay(450, Stop.Token);
        return true;
    }
    static async Task PrepareReaderTouch()
    {
        await Guard();
        var abi = (await Shell("getprop ro.product.cpu.abi")).Trim();
        ReaderTouch = SelectStoryTouch(Selected!.Kind, OperatingSystem.IsMacOS(), abi, await Shell("getevent -lp"));
        var touch = ReaderTouch;
        var side = ReaderTouchSide = ReadingSideStory;
        var (down, up) = StoryTouchPackets(touch, side);
        foreach (var (name, data) in new[] { ("down.bin", down), ("up.bin", up) })
        {
            if (!touch.Files) continue;
            var local = Path.Combine(Artifacts, "story-touch-" + name);
            await File.WriteAllBytesAsync(local, data);
            await Auxiliary(["-s", Serial, "push", local, ReaderRemote(name)]);
        }
        var script = $"exec 3>{touch.Device} || exit 1\ntrap 'cat {ReaderRemote("up.bin")} >&3' EXIT INT TERM\ni=0\nwhile [ $i -lt 240 ]; do\nread -r deadline < {ReaderRemote("lease")} || exit 2\ncase \\\"$deadline\\\" in ''|*[!0-9]*) exit 3;; esac\nnow=$(date +%s%3N)\ntoybox test \\\"$now\\\" -lt \\\"$deadline\\\" || break\ncat {ReaderRemote("down.bin")} >&3\nsleep 0.025\ncat {ReaderRemote("up.bin")} >&3\nsleep 0.055\ni=$((i+1))\ndone\nprintf 'taps=%s\\n' \\\"$i\\\"\n".Replace("\\\"", "\"");
        // LD's shell test overflows epoch milliseconds, and its toybox has no test
        // applet. Subtraction yields a small remaining interval on 32/64-bit shells.
        script = script.Replace("toybox test \"$now\" -lt \"$deadline\" || break",
            "[ \"$deadline\" != 0 ] || break\nremaining=$((deadline-now))\n[ \"$remaining\" -gt 0 ] && [ \"$remaining\" -le 1000 ] || break");
        // The one-second device lease bounds every tap; a fixed 240-tap batch only caused periodic pauses.
        script = script.Replace("while [ $i -lt 240 ]; do", "while :; do");
        // Original LDPlayer printf / other VM cat split and measured resting intervals.
        script = script.Replace("sleep 0.055", touch.Files ? "sleep 0.075" : "sleep 0.100");
        // Side stories: a rotated (portrait-axis) device has no verified landscape mapping for an off-center
        // point, so it taps the safe gap between card columns through Android's own coordinate system.
        if (side && touch.MaxX < touch.MaxY)
            script = script.Replace($"cat {ReaderRemote("down.bin")} >&3\nsleep 0.025\n", $"input tap {SideTapX} {SideTapY}\n");
        if (!touch.Files)
        {
            static string Print(byte[] data) => "printf '" + string.Concat(data.Select(b => "\\" + Convert.ToString(b, 8).PadLeft(3, '0'))) + "'";
            script = script.Replace("cat " + ReaderRemote("down.bin"), Print(down)).Replace("cat " + ReaderRemote("up.bin"), Print(up));
            // The EXIT trap needs its own quoted body when printf contains single quotes.
            var trapEnd = script.IndexOf('\n', script.IndexOf("trap ", StringComparison.Ordinal));
            var trapStart = script.IndexOf("trap ", StringComparison.Ordinal);
            script = script[..trapStart] + "trap \"" + Print(up) + " >&3\" EXIT INT TERM" + script[trapEnd..];
        }
        var scriptPath = Path.Combine(Artifacts, "story-taps.sh");
        await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(false));
        await Auxiliary(["-s", Serial, "push", scriptPath, ReaderRemote("taps.sh")]);
    }
    // Original Program.cs StoryTouchDevice: LD event2, Air virtual touch, other VM axis discovery.
    static StoryTouch SelectStoryTouch(string kind, bool mac, string abi, string info)
    {
        if (kind == "ldplayer" && abi == "x86_64")
        {
            var ld = info.Split("add device").FirstOrDefault(device => device.Contains("/dev/input/event2"));
            if (ld != null && ld.Contains("max 959") && ld.Contains("max 539") && ld.Contains("ABS_MT_TRACKING_ID"))
                return new("/dev/input/event2", 959, 539, true, false);
        }
        foreach (var device in info.Split("add device").Skip(1))
        {
            var path = Regex.Match(device, @"^\s*\d+:\s*(/dev/input/event\d+)").Groups[1].Value;
            int Max(string axis) => Regex.Match(device, axis + @"\s*: value -?\d+, min 0, max (\d+)") is { Success: true } m ? int.Parse(m.Groups[1].Value) : 0;
            var x = Max("ABS_MT_POSITION_X"); var y = Max("ABS_MT_POSITION_Y");
            if (mac)
            {
                if (path.Length > 0 && Regex.IsMatch(device, "name:\\s*\"BlueStacks Virtual Touch\"") && x == 32767 && y == 32767)
                    return new(path, x, y, false, true);
                continue;
            }
            if (path.Length == 0 || x < 100 || y < 100 || Regex.IsMatch(device, @"name:\s*""[^""]*[Mm]ouse")) continue;
            return new(path, x, y, Max("ABS_MT_PRESSURE") > 0, true, device.Contains("ABS_MT_TRACKING_ID"), abi.Contains("64"));
        }
        throw new InvalidOperationException("원본 방식으로 터치 장치를 찾지 못해 연타를 시작하지 않았습니다.");
    }
    static (byte[] Down, byte[] Up) StoryTouchPackets(StoryTouch touch, bool side = false)
    {
        // Side story lists put buttons under the screen center; taps go to the gap between the two card columns.
        var x = side && touch.MaxX >= touch.MaxY ? (touch.MaxX + 1) * SideTapX / 960 : (touch.MaxX + 1) / 2;
        var y = side && touch.MaxX >= touch.MaxY ? (touch.MaxY + 1) * SideTapY / 540 : (touch.MaxY + 1) / 2;
        List<(short, short, int)> down, up;
        if (touch.Tracking)
        {
            down = [(3,47,0),(3,57,1),(3,53,x),(3,54,y)];
            up = [(3,47,0),(3,57,-1)];
            if (touch.Pressure) { down.Add((3,58,1)); up.Add((3,58,0)); }
        }
        else
        {
            down = [(3,53,x),(3,54,y)];
            if (touch.Pressure) down.Add((3,58,1));
            down.Add((0,2,0)); up = [(0,2,0)];
        }
        down.AddRange([(1,330,1),(0,0,0)]); up.AddRange([(1,330,0),(0,0,0)]);
        return (EventData(touch.Wide, [..down]), EventData(touch.Wide, [..up]));
    }
    static async Task ContinuousStoryTaps()
    {
        await Guard();
        await EnsureGameForeground();
        using var verify = await Capture();
        if (!CanTapStory(verify)) return;
        if (ReaderTouch == null || ReaderTouchSide != ReadingSideStory) await PrepareReaderTouch();
        using (var fresh = await Capture()) if (!CanTapStory(fresh)) return;
        await SetReaderLease(true);
        var tapping = Auxiliary(["-s", Serial, "shell", "sh " + ReaderRemote("taps.sh")]);
        try
        {
            while (!tapping.IsCompleted)
            {
                await Task.Delay(250, Stop.Token);
                using var current = await Capture();
                if (ReaderBlockReason(current) != null) break;
                if (!(await Foreground()).Contains(Package + "/")) break;
                await SetReaderLease(true);
            }
        }
        finally
        {
            // Even cancellation revokes the remote lease. If ADB/host is gone, its deadline still expires on Android.
            try { await SetReaderLease(false); } catch { }
            await tapping;
        }
    }
    static async Task ReadStory()
    {
        Log("기존 매크로 방식으로 연속 읽기 시작 · 스킵/빨리감기 메뉴를 사용하지 않습니다.");
        var lastKnown = DateTime.UtcNow; var lastAction = DateTime.MinValue;
        var listFrames = 0;
        var listReturn = new StoryListReturnGate();
        DateTime? transitionSince = null;
        var mvProbeCount = 0;
        DateTime? menuOpenedAt = null;
        while (true)
        {
            await Guard();
            await EnsureGameForeground();
            using var screen = await Capture();
            var listReady = listReturn.Observe(IsEpisodeList(screen), DateTime.UtcNow);
            if (await DismissNotice(screen)) { lastKnown = DateTime.UtcNow; listFrames = 0; transitionSince = null; mvProbeCount = 0; menuOpenedAt = null; continue; }
            var menuWasOpen = menuOpenedAt != null;
            var menuClose = DelayedStoryMenuClose(screen,DateTime.UtcNow,ref menuOpenedAt);
            if (menuOpenedAt != null)
            {
                lastKnown = DateTime.UtcNow; listFrames = 0; transitionSince = null; mvProbeCount = 0;
                if (!menuWasOpen) Log("오토·스킵 버튼으로 스토리 메뉴 확인 · 20초 후 우상단 X 닫기");
                if (menuClose != null && DateTime.UtcNow - lastAction > TimeSpan.FromSeconds(2) &&
                    await GuardedTap(menuClose,FindStoryMenuClose))
                {
                    lastAction = DateTime.UtcNow;
                    Log("스토리 메뉴 20초 대기 완료 · 우상단 X 닫고 이어 읽기");
                }
                await Task.Delay(350,Stop.Token);
                continue;
            }
            var action = FindReaderAction(screen);
            if (action != null)
            {
                lastKnown = DateTime.UtcNow; listFrames = 0; transitionSince = null; mvProbeCount = 0;
                if (DateTime.UtcNow - lastAction > TimeSpan.FromSeconds(2) && await GuardedTap(action, FindReaderAction)) lastAction = DateTime.UtcNow;
            }
            else if (CanTapStory(screen)) { lastKnown = DateTime.UtcNow; listFrames = 0; transitionSince = null; mvProbeCount = 0; await ContinuousStoryTaps(); }
            else if (ReadingSideStory && IsSideStoryList(screen))
            {
                transitionSince = null; mvProbeCount = 0;
                if (++listFrames >= 3) { Log("사이드스토리 목록 복귀 · 남은 SKIP/미읽음 검사"); return; }
            }
            else if (IsEpisodeList(screen))
            {
                transitionSince = null; mvProbeCount = 0;
                if (listReady) { Log("에피소드 목록 안정 확인 · 후속 보상 없음 · 현재 위치부터 아래로 새 스토리/SKIP 검사"); return; }
            }
            else
            {
                listFrames = 0;
                var now = DateTime.UtcNow;
                if (transitionSince == null) { transitionSince = now; mvProbeCount = 0; Log("스토리 … 사라짐 · 연타 해제, 빠른 화면 재확인"); }
                else if (ShouldProbeStoryMv(screen, transitionSince.Value, now, mvProbeCount))
                {
                    mvProbeCount++;
                    if (await ProbeStoryMvPause()) continue;
                }
                if (now - transitionSince > TimeSpan.FromSeconds(30))
                {
                    CopyEvidence(ShotPath, Path.Combine(Artifacts, $"instance-{Selected!.Index}-reader-timeout.png"));
                    throw new RecoveryNeeded(RecoveryCause.UnknownState, "스토리 … / 보상 / 에피소드 목록이 30초 동안 확인되지 않았습니다.");
                }
            }
            var interval = transitionSince == null ? 350 : DateTime.UtcNow - transitionSince < TimeSpan.FromSeconds(5) ? 500 : 1000;
            await Task.Delay(interval, Stop.Token);
        }
    }
}
