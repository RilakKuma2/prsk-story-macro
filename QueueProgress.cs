using System.Drawing;
using System.Text.Json;

internal static partial class Program
{
    sealed record QueueProgress(int Instance, long Created, int Port, string Name,
        List<byte[]> Completed, byte[]? Current, string Stage, DateTimeOffset Updated);
    static string QueueProgressPath => Path.Combine(Artifacts, $"instance-{Selected!.Index}-queue.json");
    static QueueProgress NewQueueProgress(Vm vm) => new(vm.Index, vm.Created, vm.Port, vm.Name, [], null, "catalogue", DateTimeOffset.Now);
    static bool QueueBelongsTo(QueueProgress progress, Vm vm) => progress.Instance == vm.Index && progress.Created == vm.Created && progress.Port == vm.Port && progress.Name == vm.Name;
    static QueueProgress LoadQueueProgress()
    {
        var value = LoadRecord<QueueProgress>(QueueProgressPath);
        if (value == null || value.Stage == "complete" || !QueueBelongsTo(value, Selected!) || value.Completed == null) return NewQueueProgress(Selected!);
        // A damaged record is ignored; it is never a requirement for progressing through the game.
        value = value with { Completed = value.Completed.Where(key => key?.Length == 12870).ToList(), Current = value.Current?.Length == 12870 ? value.Current : null };
        if (value.Completed.Count > 0 || value.Current != null) Log($"진행 기록 복원 · 확인한 스토리 {value.Completed.Count}개, 단계 {value.Stage}");
        return value;
    }
    static Task SaveQueueProgress(QueueProgress value) => SaveRecord(QueueProgressPath,
        JsonSerializer.Serialize(value with { Updated = DateTimeOffset.Now }));
    // The same pixels and tolerance as SameCatalogueCard, stored without a temporary bitmap file.
    static byte[] CatalogueSignature(Bitmap screen)
    {
        var key = new byte[12870]; var offset = 0;
        for (var y = 230; y < 308; y += 2) for (var x = 110; x < 330; x += 2)
        {
            var p = screen.GetPixel(x, y); key[offset++] = p.R; key[offset++] = p.G; key[offset++] = p.B;
        }
        return key;
    }
    static bool SameCatalogueSignature(byte[] left, byte[] right)
    {
        if (left.Length != 12870 || right.Length != left.Length) return false;
        long error = 0;
        for (var i = 0; i < left.Length; i++) error += Math.Abs(left[i] - right[i]);
        return (double)error / left.Length < 4;
    }
    sealed class CatalogueReadPass
    {
        readonly List<byte[]> checkedCards = [];
        public bool AlreadyChecked(byte[] card) => checkedCards.Any(done => SameCatalogueSignature(done,card));
        public void Confirm(byte[] card, EpisodeVerification verification)
        {
            if (!verification.BottomReached || verification.HasSkip || verification.HasUnread)
                throw new InvalidOperationException("미읽음 검증이 끝나지 않은 이벤트는 확인 완료로 처리할 수 없습니다.");
            checkedCards.Add(card);
        }
    }
    static QueueProgress RecordVerifiedStory(QueueProgress progress, EpisodeVerification verification)
    {
        if (!verification.BottomReached || verification.HasSkip || verification.HasUnread)
            throw new InvalidOperationException("미읽음 검증이 끝나지 않아 다음 이벤트로 넘어가지 않습니다.");
        if (!verification.HasLocked) return MarkCurrentStoryComplete(progress);
        if (progress.Current is { } key) progress.Completed.RemoveAll(done => SameCatalogueSignature(done,key));
        return progress with {Current=null,Stage="locked"};
    }
    static QueueProgress MarkCurrentStoryComplete(QueueProgress progress)
    {
        if (progress.Current is { } key && !progress.Completed.Any(done => SameCatalogueSignature(done, key))) progress.Completed.Add(key);
        return progress with { Current = null, Stage = "verified" };
    }
}
