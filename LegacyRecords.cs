using System.Text.Json;
internal static partial class Program
{
    static async Task MigrateMuMuRecords()
    {
        if (Selected is not { Kind: "mumu" } vm) return;
        var oldPath=Path.Combine(Artifacts,$"instance-{vm.LocalIndex}-queue.json");
        var old=LoadRecord<QueueProgress>(oldPath);
        if(old==null || old.Instance!=vm.LocalIndex || old.Name!=vm.Name || old.Port!=vm.Port || old.Created>=10_000_000_000_000_000L) return;
        var destination=Path.Combine(Artifacts,$"instance-{vm.Index}-queue.json");
        if(!File.Exists(destination)) await SaveRecord(destination,JsonSerializer.Serialize(old with { Instance=vm.Index, Created=vm.Created }));
        var oldProgress=Path.Combine(Artifacts,$"instance-{vm.LocalIndex}-story-progress.json");
        var progress=LoadRecord<StoryProgress>(oldProgress);
        if(progress!=null && !File.Exists(Path.Combine(Artifacts,$"instance-{vm.Index}-story-progress.json")))
            await SaveRecord(Path.Combine(Artifacts,$"instance-{vm.Index}-story-progress.json"),JsonSerializer.Serialize(progress with { Instance=vm.Index }));
        var archive=Path.Combine(Artifacts,"legacy-mumu"); Directory.CreateDirectory(archive);
        foreach(var file in Directory.GetFiles(Artifacts,$"instance-{vm.LocalIndex}-*"))
            try { File.Move(file,Path.Combine(archive,Path.GetFileName(file)),true); } catch(IOException) { }
        Log("기존 뮤뮤 진행 기록을 새 인스턴스 번호로 이전했습니다.");
    }
}
