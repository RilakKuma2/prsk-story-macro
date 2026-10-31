using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal static partial class Program
{
    static Form? DownloadWindow;

    // Gets the game XAPK in a built-in browser window: apkpure serves its download link to a normal browser.
    // Nothing is faked or bypassed; a check page apkpure shows stays in the window for the user.
    static bool OpenGameDownload(Form owner,CoreWebView2Environment environment,string key)
    {
        owner.BeginInvoke(new Action(async()=>
        {
            if(DownloadWindow is { IsDisposed: false }) { DownloadWindow.Activate(); return; }
            var title=key=="qooapp"?"QooApp":T("게임 받기 · apkpure","ゲームを取得 · apkpure","Get the game · apkpure");
            var form=new Form { Text=title,Width=1000,Height=720,StartPosition=FormStartPosition.CenterParent,Icon=owner.Icon };
            DownloadWindow=form;
            var view=new WebView2 { Dock=DockStyle.Fill };
            form.Controls.Add(view);
            form.Show(owner);
            try
            {
                await view.EnsureCoreWebView2Async(environment);
                view.CoreWebView2.DownloadStarting+=(_,e)=>
                {
                    var name=Path.GetFileName(e.ResultFilePath);
                    var version=System.Text.RegularExpressions.Regex.Match(name,@"([0-9]+(?:\.[0-9]+)+)").Groups[1].Value;
                    Directory.CreateDirectory(ApkCache);
                    var target=KeptPath(key,version,name.EndsWith(".apk",StringComparison.OrdinalIgnoreCase));
                    File.WriteAllText(LatestFile(key),version);
                    // The newest version is already kept: nothing to download.
                    if(File.Exists(target)) { e.Cancel=true; form.BeginInvoke(form.Close); return; }
                    e.ResultFilePath=target+".part"; e.Handled=true;
                    var download=e.DownloadOperation;
                    download.BytesReceivedChanged+=(_,_)=>form.Text=$"{title} · {download.BytesReceived>>20}/{(long)(download.TotalBytesToReceive??0)>>20}MB";
                    download.StateChanged+=(_,_)=>
                    {
                        if(download.State==CoreWebView2DownloadState.Completed) { File.Move(target+".part",target,true); form.Close(); }
                        else if(download.State==CoreWebView2DownloadState.Interrupted) form.Text=$"{title} · {T("실패","失敗","Failed")}: {download.InterruptReason}";
                    };
                };
                view.CoreWebView2.Navigate(ApkUrl(key));
            }
            catch(Exception ex) { form.Text=$"{title} · {ex.Message}"; }
        }));
        return true;
    }

}
