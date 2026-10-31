using SkiaSharp;

internal static partial class Program
{
    static void VerifyEvidenceStorage(Action<bool,string> assert)
    {
        var root=Path.Combine(Path.GetTempPath(),"SekaiStorySkip-evidence-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var png=File.ReadAllBytes(Path.Combine(Root,"tests","fixtures","story-reader-menu-open.png"));
            var legacy=Path.Combine(root,"story-check","old.png");Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);File.WriteAllBytes(legacy,png);
            var oldTime=DateTime.UtcNow.AddDays(-2);File.SetLastWriteTimeUtc(legacy,oldTime);
            var live=Path.Combine(root,"instance-0-current.png");File.WriteAllBytes(live,png);
            var queue=Path.Combine(root,"instance-0-queue.json");File.WriteAllText(queue,"progress");
            var store=new EvidenceStore(root);
            store.Migrate();
            var converted=Path.ChangeExtension(legacy,".webp");
            using (var image=SKBitmap.Decode(converted))
                assert(!File.Exists(legacy) && image is {Width:960,Height:540} && File.GetLastWriteTimeUtc(converted)==oldTime,
                    "existing evidence is converted to valid full-resolution WebP while preserving its age");
            assert(new FileInfo(converted).Length<png.Length && File.ReadAllBytes(live).SequenceEqual(png),
                "evidence compression reduces storage and leaves the live PNG unchanged");
            var size=new FileInfo(converted).Length;
            var limited=new EvidenceStore(root,size*2+10);
            var second=limited.Save(png,Path.Combine(root,"second.png"));
            File.SetLastWriteTimeUtc(second,DateTime.UtcNow.AddDays(-1));
            File.WriteAllText(Path.ChangeExtension(converted,".json"),"capture metadata");
            var newest=limited.Save(png,Path.Combine(root,"newest.png"));
            assert(!File.Exists(converted) && !File.Exists(Path.ChangeExtension(converted,".json")) && File.Exists(second) && File.Exists(newest),
                "the size cap evicts the oldest evidence and its metadata, keeping newer captures");
            assert(Directory.GetFiles(root,"*.webp",SearchOption.AllDirectories).Sum(f=>new FileInfo(f).Length)<=size*2+10 &&
                File.ReadAllText(queue)=="progress" && File.Exists(live),"retention stays within its byte limit without deleting progress or live previews");
            assert(!Directory.GetFiles(root,"*.pending",SearchOption.AllDirectories).Any(),"WebP writes leave no temporary files after completion");
        }
        finally { Directory.Delete(root,true); }
    }
}
