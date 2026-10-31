using System.Text.RegularExpressions;

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
                    if (Regex.Replace(heading.Groups[2].Value,@"\s+","") == "수정사항")
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
}
