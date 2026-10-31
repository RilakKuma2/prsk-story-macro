using System.Drawing;
internal static partial class Program
{
    static string? PendingProtectionReason;
    static DateTime lastProtectionEvidence;
    static void PreserveProtectionFrame(byte[] bytes,int instance,string reason)
    {
        if(DateTime.UtcNow-lastProtectionEvidence<TimeSpan.FromSeconds(5))return;
        lastProtectionEvidence=DateTime.UtcNow;
        var path=Path.Combine(Artifacts,"protection",$"instance-{instance}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.webp");
        SaveEvidenceImage(bytes,path);
        Log(reason+" · 감지 당시 캡처: "+Path.GetFileName(path));
    }
    static void CheckPendingProtection()
    {
        if(!WorkflowActive)return;
        if(Interlocked.Exchange(ref PendingProtectionReason,null) is { } reason)
            throw new RecoveryNeeded(RecoveryCause.AccountSafety,reason+" · 터치 없이 게임 재실행");
    }
    static bool IsAccountDeleteConfirm(Bitmap screen)
    {
        if(screen.Width!=960 || screen.Height!=540)return false;
        static bool Neutral(Color p)=>p.R>=190 && p.G>=190 && p.B>=190 && Math.Max(p.R,Math.Max(p.G,p.B))-Math.Min(p.R,Math.Min(p.G,p.B))<35;
        static int Bright(Color p)=>(p.R+p.G+p.B)/3;
        // A confirmation is an inset neutral panel over a dimmed background.
        // Two isolated gray pixels also occur in ordinary rooms behind characters.
        var panelRows=0;
        for(var y=90;y<=440;y+=25)
        {
            var left=screen.GetPixel(200,y);var right=screen.GetPixel(750,y);
            if(Neutral(left) && Neutral(right) && Bright(left)-Bright(screen.GetPixel(40,y))>=25 &&
                Bright(right)-Bright(screen.GetPixel(920,y))>=25)panelRows++;
        }
        if(panelRows<9)return false;
        // The deletion warning has three separated lines of red text. It is independent of player ID,
        // checkbox state and whether the final button is disabled. Normal home artwork is not a template.
        var lines=0;var run=0;
        for(var y=294;y<355;y++)
        {
            var red=0;for(var x=292;x<668;x+=2){var p=screen.GetPixel(x,y);if(p.R>230 && p.G<150 && p.B<180 && p.R-p.G>95 && p.R-p.B>45)red++;}
            if(red>=5)run++;
            else if(run>0){if(run>=3)lines++;run=0;}
        }
        if(run>=3)lines++;
        return lines>=3;
    }
    static bool IsGachaProtected(Bitmap screen)=>screen.Width==960 && screen.Height==540 &&
        Matches(screen,new("gacha-crystal","gacha-protected-crystal.png",0,0,754,23,17,20)) &&
        LightGlyph(screen,"gacha-protected-paid.png",766,8,220);
    static bool IsCardProtected(Bitmap screen)=>screen.Width==960 && screen.Height==540 &&
        LightGlyph(screen,"card-protected-performance.png",552,109,110,darkInk:true) &&
        LightGlyph(screen,"card-protected-master.png",552,405,110,darkInk:true);
    static bool IsTitleMenuProtected(Bitmap screen)=>screen.Width==960 && screen.Height==540 &&
        LightGlyph(screen,"title-menu-transfer.png",320,202,130,darkInk:true) &&
        LightGlyph(screen,"title-menu-delete.png",584,282,130,darkInk:true);
    static string? ProtectedScreenReason(Bitmap screen)=>IsAccountDeleteConfirm(screen)?"계정 삭제 확인창 감지":
        IsGachaProtected(screen)?"가챠 화면 감지":IsCardProtected(screen)?"카드 화면 감지":IsTitleMenuProtected(screen)?"타이틀 메뉴 감지":null;
}
