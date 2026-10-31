using System.Text.Json;

internal static partial class Program
{
    static readonly SemaphoreSlim RecordGate = new(1, 1);
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> LatestRecords = new();
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> RecordWarnings = new();

    // Original Resume.WriteCheckpoint: retain a pending record if Windows denies replacement.
    // Reporting and checkpoint I/O must never end a running story.
    static async Task<bool> SaveRecord(string path, string value)
    {
        LatestRecords[path] = value;
        await RecordGate.WaitAsync();
        try
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllTextAsync(path + ".pending", value);
                    File.Move(path + ".pending", path, true);
                    RecordWarnings.TryRemove(path, out _);
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt == 19)
                    {
                        if (RecordWarnings.TryAdd(path, 0)) Log($"기록 저장 지연 · 진행은 계속합니다: {Path.GetFileName(path)} · {ex.Message}");
                        return false;
                    }
                    await Task.Delay(100);
                }
            }
            return false;
        }
        finally { RecordGate.Release(); }
    }
    static T? LoadRecord<T>(string path)
    {
        if (LatestRecords.TryGetValue(path, out var memory))
        {
            try { return JsonSerializer.Deserialize<T>(memory); } catch (JsonException) { }
        }
        static DateTime Modified(string file) { try { return File.GetLastWriteTimeUtc(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.MinValue; } }
        foreach (var candidate in new[] { path, path + ".pending" }.OrderByDescending(Modified))
        {
            try
            {
                if (File.Exists(candidate) && JsonSerializer.Deserialize<T>(File.ReadAllText(candidate)) is { } value) return value;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return default;
    }
    static void SaveEvidence(string path, Action write)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            write();
            RecordWarnings.TryRemove(path, out _);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        {
            if (RecordWarnings.TryAdd(path, 0)) Log($"증거 이미지 저장 실패 · 진행은 계속합니다: {Path.GetFileName(path)} · {ex.Message}");
        }
    }
    static void CopyEvidence(string source, string target) => SaveEvidence(target, () => File.Copy(source, target, true));
}
