// UI language (ko/ja/en) for server messages and control logs; saved in local/language.txt and shared by every client.
// The web UI keeps its own strings and reads the language from /api/meta.
internal static partial class Program
{
    static readonly string[] Languages=["ko","ja","en"];
    static string LanguageFile=>Path.Combine(DataRoot,"local","language.txt");
    static string? language;
    static string Language
    {
        get
        {
            if(language!=null) return language;
            try { var saved=File.Exists(LanguageFile)?File.ReadAllText(LanguageFile).Trim():null; language=Languages.Contains(saved)?saved:"ko"; }
            catch { language="ko"; }
            return language!;
        }
    }
    static void SetLanguage(string? value)
    {
        if(!Languages.Contains(value)) throw new ArgumentException("Unknown language: "+value);
        Directory.CreateDirectory(Path.GetDirectoryName(LanguageFile)!);
        File.WriteAllText(LanguageFile,value);
        language=value;
    }

    static string AppTitle=>T("셐스토리스킵","セカイストーリースキップ","Sekai Story Skip");
    internal static string PickLanguage(string ko,string ja,string en)=>T(ko,ja,en);
    static string T(string ko,string ja,string en)=>Language switch { "ja"=>ja,"en"=>en,_=>ko };

}
