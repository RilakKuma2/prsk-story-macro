using SkiaSharp;
using System.Text.RegularExpressions;

sealed class EvidenceStore(string root,long limit=100_000_000,Action<string>? report=null)
{
    internal const int Quality = 80;
    readonly object gate = new();
    bool migrating;
    static bool IsEvidence(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".webp" &&
        !Regex.IsMatch(Path.GetFileName(path),@"^instance-\d+-current\.png$",RegexOptions.IgnoreCase) &&
        Path.GetFileName(path) != "percent-ocr.png";
    IEnumerable<FileInfo> Files() => Directory.Exists(root)
        ? Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).Where(IsEvidence).Select(p=>new FileInfo(p)) : [];
    internal string Save(byte[] bytes,string requested,DateTime? originalTime=null)
    {
        lock (gate)
        {
            var target=Path.ChangeExtension(requested,".webp");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var pending=target+".pending";
            try
            {
                using var image=SKImage.FromEncodedData(bytes) ?? throw new InvalidDataException("증거 캡처를 읽지 못했습니다.");
                using var encoded=image.Encode(SKEncodedImageFormat.Webp,Quality) ?? throw new InvalidDataException("WebP 압축에 실패했습니다.");
                using (var output=File.Create(pending)) encoded.SaveTo(output);
                File.Move(pending,target,true);
                if (originalTime is { } time) File.SetLastWriteTimeUtc(target,time);
                if (requested!=target && File.Exists(requested) && IsEvidence(requested)) File.Delete(requested);
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
            if (!migrating) Prune();
            return target;
        }
    }
    internal void Migrate()
    {
        lock (gate) { if (migrating) return;migrating=true; }
        try
        {
            foreach (var file in Files().Where(f=>f.Extension.Equals(".png",StringComparison.OrdinalIgnoreCase)).OrderByDescending(f=>f.LastWriteTimeUtc).ToArray())
            {
                try
                {
                    lock (gate)
                    {
                        file.Refresh();
                        if (!file.Exists) continue;
                        var target=Path.ChangeExtension(file.FullName,".webp");
                        if (File.Exists(target) && File.GetLastWriteTimeUtc(target)>=file.LastWriteTimeUtc) File.Delete(file.FullName);
                        else Save(File.ReadAllBytes(file.FullName),file.FullName,file.LastWriteTimeUtc);
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { report?.Invoke("증거 캡처 변환 지연 · "+file.Name+" · "+ex.Message); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { report?.Invoke("증거 캡처 정리 지연 · "+ex.Message); }
        finally { lock (gate) { migrating=false;Prune(); } }
    }
    internal void Prune()
    {
        lock (gate)
        {
            var files=Files().OrderBy(f=>f.LastWriteTimeUtc).ThenBy(f=>f.FullName,StringComparer.Ordinal).ToArray();
            var bytes=files.Sum(f=>f.Length);
            foreach (var file in files)
            {
                if (bytes<=limit) break;
                try
                {
                    var length=file.Length;
                    file.Delete();bytes-=length;
                    var metadata=Path.ChangeExtension(file.FullName,".json");
                    if (File.Exists(metadata)) File.Delete(metadata);
                    var folder=file.Directory!;
                    if (folder.FullName!=Path.GetFullPath(root) && !folder.EnumerateFileSystemInfos().Any()) folder.Delete();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { report?.Invoke("오래된 증거 캡처 삭제 지연 · "+file.Name+" · "+ex.Message); }
            }
        }
    }
}

internal static partial class Program
{
    static readonly Lazy<EvidenceStore> EvidenceArchive = new(()=>new(Path.Combine(DataRoot,"artifacts"),report:Log));
    static void SaveEvidenceImage(byte[] bytes,string path) => SaveEvidence(path,()=>EvidenceArchive.Value.Save(bytes,path));
}
