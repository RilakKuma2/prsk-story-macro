internal static partial class Program
{
    static async Task VerifyCaptureContention(Action<bool, string> assert)
    {
        var path = Path.Combine(Artifacts, "capture-lock-test-" + Guid.NewGuid().ToString("N") + ".png");
        var bytes = await File.ReadAllBytesAsync(Path.Combine(Root, "tests", "fixtures", "main.png"));
        var original = await File.ReadAllBytesAsync(Path.Combine(Root, "tests", "fixtures", "title-animated.png"));
        try
        {
            await File.WriteAllBytesAsync(path, original);
            var captures = 0;
            Task<byte[]> retry;
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                retry = Snapshot(path, () => { captures++; return Task.FromResult(bytes); });
                await Task.Delay(250);
                assert(!retry.IsCompleted && captures == 1, "transient file lock waits before recapturing");
            }
            var frame = await retry;
            assert(captures == 2 && frame.SequenceEqual(bytes), "fresh capture succeeds after the file lock clears");
            assert((await File.ReadAllBytesAsync(path)).SequenceEqual(bytes), "retried screenshot replaces the previous frame");

            await File.WriteAllBytesAsync(path, original);
            captures = 0;
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Exception? failure = null;
                try { await Snapshot(path, () => { captures++; return Task.FromResult(bytes); }); }
                catch (IOException ex) { failure = ex; }
                assert(captures == 3 && failure != null, "persistent file lock stops after exactly three captures");
                assert(failure!.Message.Contains(path) && failure.InnerException != null, "capture failure retains path and underlying exception");
            }
            assert((await File.ReadAllBytesAsync(path)).SequenceEqual(original), "failed capture does not present the old file as a fresh result");
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".pending");
        }
    }
}
