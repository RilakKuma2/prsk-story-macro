internal static partial class Program
{
    const string MuMuDownloadUrl="https://www.mumuplayer.com/kr/download/";
    const string AirDownloadUrl="https://www.bluestacks.com/mac";
    static bool IsEmulatorDownload(string url) => url is MuMuDownloadUrl or AirDownloadUrl;
}
