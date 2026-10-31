using System.Drawing;
#if WINDOWS
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
#endif
using System.Text.Json;
using System.Text.RegularExpressions;

internal static partial class Program
{
    sealed record StoryProgress(int Instance, decimal? Percent, DateTimeOffset? LastSeen, bool Visible, string Status);
    static readonly System.Collections.Concurrent.ConcurrentDictionary<int, StoryProgress> Progress = new();
    static DateTime LastProgressRead = DateTime.MinValue;
    static int LastProgressInstance = -1;

    // Reuses the original Windows OCR pipeline, restricted to the completion percentage at the top of the story list.
    static async Task<decimal?> ReadStoryPercent(Bitmap screen)
    {
        if (!IsMainEventList(screen)) return null;
        var text = await OcrRegionText(screen, new Rectangle(407, 8, 97, 51));
        var candidates = text.Split('\n').Select(ParsePercent).Where(value => value != null).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }
#if WINDOWS
    static async Task<string> OcrRegionText(Bitmap screen, Rectangle region)
    {
        using var crop = screen.Clone(region, PixelFormat.Format24bppRgb);
        using var enlarged = new Bitmap(crop.Width * 3, crop.Height * 3);
        using (var graphics = Graphics.FromImage(enlarged))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(crop, 0, 0, enlarged.Width, enlarged.Height);
        }
        // Keep the original Windows OCR pipeline in memory so reporting needs no temporary files.
        using var encoded = new MemoryStream();
        enlarged.Save(encoded, ImageFormat.Png);
        var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US")) ??
            Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("ja")) ??
            Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages() ?? throw new InvalidOperationException("Windows OCR 언어를 사용할 수 없습니다.");
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        using (var writer = new Windows.Storage.Streams.DataWriter(stream))
        {
            writer.WriteBytes(encoded.ToArray());
            await writer.StoreAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap);
        return string.Join('\n', result.Lines.Select(line => line.Text));
    }
#endif
    static decimal? ParsePercent(string text)
    {
        var normalized = Regex.Replace(text.Normalize(System.Text.NormalizationForm.FormKC), @"\s+", "");
        var match = Regex.Match(normalized, @"^(100(?:\.0{1,2})?|[1-9]?[0-9](?:\.[0-9]{1,2})?)%$");
        return match.Success ? decimal.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }
    static async Task ObserveStoryProgress(Bitmap screen)
    {
        var index = Selected?.Index ?? -1;
        if (index < 0 || (index == LastProgressInstance && DateTime.UtcNow - LastProgressRead < TimeSpan.FromSeconds(2))) return;
        LastProgressRead = DateTime.UtcNow; LastProgressInstance = index;
        Progress.TryGetValue(index, out var previous);
        previous ??= LoadRecord<StoryProgress>(Path.Combine(Artifacts, $"instance-{index}-story-progress.json"));
        StoryProgress progress;
        try
        {
            var percent = await ReadStoryPercent(screen);
            progress = percent != null
                ? new(index, percent, DateTimeOffset.Now, true, "실시간 인식 중")
                : new(index, previous?.Percent, previous?.LastSeen, false, "완독률 표시 화면 대기");
        }
        catch (Exception ex) { progress = new(index, previous?.Percent, previous?.LastSeen, false, "인식 대기: " + ex.Message); }
        Progress[index] = progress;
        if (progress.Percent != null && (progress.Percent != previous?.Percent || progress.Visible != previous?.Visible))
            await SaveRecord(Path.Combine(Artifacts, $"instance-{index}-story-progress.json"), JsonSerializer.Serialize(progress, new JsonSerializerOptions { WriteIndented = true }));
    }
}
