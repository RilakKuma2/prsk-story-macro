using System.Text.Json;

internal static partial class Program
{
    static void VerifyInstallAndReleaseChanges(Action<bool,string> assert)
    {
        var notes = new string('x',700)+"\n윈도우: [zip](https://example.com/game.zip)\n맥: dmg\n\n### 수정 사항\n\n- **미읽음** 처리\n- `X` 닫기\n\n### 확인한 동작\n검사 결과";
        assert(ChangesFromReleaseNotes(notes)=="• 미읽음 처리\n• X 닫기","update notes contain only the changes section, without download instructions, headings or Markdown markup");
        assert(ChangesFromReleaseNotes("##수정사항\n수정 본문\n## 기타\n다른 본문")=="수정 본문","compact change headings work and the following section is excluded");
        assert(ChangesFromReleaseNotes("다운로드 안내만 있습니다.")=="" && ChangesFromReleaseNotes(null)=="","missing changes never fall back to showing download instructions");
        using (var release=JsonDocument.Parse(JsonSerializer.Serialize(new {
            tag_name="v99.0.0",body=notes,assets=new[]{new{name="SekaiStorySkip-mac-arm64.dmg",browser_download_url=$"https://github.com/{ReleaseRepo}/releases/download/v99.0.0/SekaiStorySkip-mac-arm64.dmg"}}})))
            assert(ParseUpdateInfo(release.RootElement,true).Notes=="• 미읽음 처리\n• X 닫기","the web/Mac update dialog receives the same filtered notes as Windows");
        using var manifest=JsonDocument.Parse("""
            {"split_apks":[{"file":"base.apk","id":"base"},{"file":"arm64.apk","id":"config.arm64_v8a"},{"file":"x64.apk","id":"config.x86_64"},{"file":"arm.apk","id":"config.armeabi_v7a"},{"file":"ja.apk","id":"config.ja"}]}
            """);
        assert(ChooseGameSplits(manifest.RootElement,"arm64-v8a,armeabi-v7a").Select(s=>s.File).SequenceEqual(new[]{"base.apk","arm64.apk","ja.apk"}),"BlueStacks Air installs the base, preferred ARM64 split and shared resources");
        assert(ChooseGameSplits(manifest.RootElement,"x86_64,x86").Select(s=>s.File).SequenceEqual(new[]{"base.apk","x64.apk","ja.apk"}),"Windows emulators install their matching ABI without incompatible splits");
        try { ChooseGameSplits(manifest.RootElement,"mips");assert(false,"an incompatible package must not be installed"); }
        catch (InvalidDataException) { assert(true,"missing compatible ABI fails before installation"); }
        assert(IsGameDownloadSite(GamePageUrl) && IsGameDownloadSite(QooAppApkUrl) && !IsGameDownloadSite("file:///tmp/game.apk"),"download links use the invitation macro's package sources");
    }
}
