using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal static partial class Program
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);
    // Original invitation macro host and window geometry; independent server, profile and mutex.
    static void ShowGui()
    {
        using var single = new Mutex(true, @"Local\SekaiStorySkipGui", out var first);
        using var raise = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SekaiStorySkipGuiRaise");
        if (!first) { AllowSetForegroundWindow(-1); raise.Set(); return; }
        ApplicationConfiguration.Initialize();
        using var panel = new StoryPanel();
        var server = panel.BuildServer();
        server.StartAsync().GetAwaiter().GetResult();
        panel.RestoreListen().GetAwaiter().GetResult();
        var url = server.Urls.Single() + "/";
        panel.StartMonitor();
        File.WriteAllText(Path.Combine(Artifacts, "panel-info.json"), System.Text.Json.JsonSerializer.Serialize(new { Url = url, Token = panel.Token }));
        var dark = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0;
        using var window = new Form
        {
            Text = AppTitle, Width = 1320, Height = 900, MinimumSize = new Size(420, 560),
            StartPosition = FormStartPosition.CenterScreen,
            BackColor = dark ? Color.FromArgb(17, 17, 19) : Color.FromArgb(242, 244, 246),
            Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!)
        };
        window.HandleCreated += (_, _) => { var on = dark ? 1 : 0; DwmSetWindowAttribute(window.Handle, 20, ref on, sizeof(int)); };
        var registered = ThreadPool.RegisterWaitForSingleObject(raise, (_, _) =>
        {
            if (!window.IsDisposed && window.IsHandleCreated) window.BeginInvoke(() =>
            {
                window.Show();
                if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal;
                window.Activate();
            });
        }, null, -1, false);
        using var view = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = window.BackColor };
        window.Controls.Add(view);
        BindUpdateLifetime(window);
        window.Shown += async (_, _) =>
        {
            try
            {
                var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Root, "local", "webview2"));
                await view.EnsureCoreWebView2Async(environment);
                GameDownloadWindow=key=>OpenGameDownload(window,environment,key);
                view.CoreWebView2.Settings.AreDevToolsEnabled = false;
                view.CoreWebView2.Settings.IsStatusBarEnabled = false;
                void OpenDownload(string target)
                {
                    if (IsEmulatorDownload(target) || IsGameDownloadSite(target)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
                }
                view.CoreWebView2.NewWindowRequested += (_, e) => { e.Handled = true; OpenDownload(e.Uri); };
                view.CoreWebView2.NavigationStarting += (_, e) =>
                {
                    if (!e.Uri.StartsWith(url, StringComparison.Ordinal)) { e.Cancel = true; OpenDownload(e.Uri); }
                };
                view.CoreWebView2.WebMessageReceived += async (_, e) =>
                {
                    if(e.Source.StartsWith(url,StringComparison.Ordinal) && e.TryGetWebMessageAsString()=="check-update")
                    { await CheckForUpdate(window,panel,true);return; }
                    if(e.Source.StartsWith(url,StringComparison.Ordinal) && e.TryGetWebMessageAsString().StartsWith("title:",StringComparison.Ordinal))
                    { window.Text=e.TryGetWebMessageAsString()[6..];return; }
                    if (e.Source.StartsWith(url, StringComparison.Ordinal) && e.TryGetWebMessageAsString() == "preview-ready")
                    {
                        try
                        {
                            using var output = new MemoryStream();
                            await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
                            SaveEvidenceImage(output.ToArray(),Path.Combine(Artifacts,"gui-preview.webp"));
                        }
                        catch (Exception ex) { Log("GUI 미리보기 저장: " + ex.Message); }
                    }
                };
                view.CoreWebView2.Navigate(url);
            }
            catch (Exception ex) { MessageBox.Show(window, ex.Message, "스토리 제어판 시작 실패"); }
            _ = Task.Run(()=>CheckForUpdate(window,panel));
        };
        window.FormClosing += (_, _) => { panel.RequestStop();DownloadWindow?.Close(); };
        Application.Run(window);
        registered.Unregister(null);
        panel.Shutdown().GetAwaiter().GetResult();
        server.StopAsync().GetAwaiter().GetResult();
        server.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
