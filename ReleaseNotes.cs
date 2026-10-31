using System.Text.RegularExpressions;
using System.Text.Json;

internal static partial class Program
{
    static string ChangesFromReleaseNotes(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        var lines = body.Replace("\r\n","\n").Split('\n');
        var changes = new List<string>();
        var section = 0;
        var fenced = false;
        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("```")) { fenced = !fenced; continue; }
            var heading = fenced ? Match.Empty : Regex.Match(line,@"^\s{0,3}(#{1,6})[ \t]*(.*?)[ \t]*#*[ \t]*$");
            if (heading.Success)
            {
                if (section == 0)
                {
                    if (Regex.Replace(heading.Groups[2].Value,@"\s+","") is "수정사항" or "변경사항")
                        section = heading.Groups[1].Length;
                    continue;
                }
                if (heading.Groups[1].Length <= section) break;
                continue;
            }
            if (section != 0) changes.Add(line);
        }
        var text = string.Join('\n',changes).Trim();
        text = Regex.Replace(text,@"\[([^\]]+)\]\([^\)]+\)","$1");
        text = text.Replace("**","").Replace("__","").Replace("`","");
        return Regex.Replace(text,@"(?m)^\s*[-*]\s+","• ").Trim();
    }
    static string LegacyChangesFromReleaseNotes(string? body)
    {
        var changes=ChangesFromReleaseNotes(body);
        if (changes.Length>0 || string.IsNullOrWhiteSpace(body) || Regex.IsMatch(body,@"(?m)^\s{0,3}#{1,6}")) return changes;
        var bullets=new List<string>();
        foreach(var line in body.Replace("\r\n","\n").Split('\n'))
        {
            if (Regex.IsMatch(line,@"^\s*[-*]\s+")) bullets.Add(line);
            else if (bullets.Count>0) break;
        }
        return Regex.Replace(string.Join('\n',bullets).Replace("**","").Replace("`",""),@"(?m)^\s*[-*]\s+","• ").Trim();
    }
    static string PendingReleaseNotes(IEnumerable<JsonElement> releases,Version current,Version latest)
    {
        var versions=new Dictionary<Version,string>();
        foreach(var release in releases)
        {
            if (release.TryGetProperty("draft",out var draft) && draft.GetBoolean() ||
                release.TryGetProperty("prerelease",out var preview) && preview.GetBoolean()) continue;
            if (!release.TryGetProperty("tag_name",out var tag) || !Version.TryParse(tag.GetString()?.TrimStart('v','V'),out var version)) continue;
            version=Normalize(version);
            if (version<=Normalize(current) || version>Normalize(latest) || versions.ContainsKey(version)) continue;
            var notes=LegacyChangesFromReleaseNotes(release.TryGetProperty("body",out var body)?body.GetString():null);
            versions[version]=notes.Length>0?notes:T("변경사항 설명이 없습니다.","変更内容の説明がありません。","No change description is available.");
        }
        return string.Join("\n\n",versions.OrderByDescending(p=>p.Key).Select(p=>$"v{p.Key} {T("변경사항","変更内容","Changes")}\n{p.Value}"));
    }
}
