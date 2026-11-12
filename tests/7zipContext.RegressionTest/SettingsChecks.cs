using System.Text;
using ZipContext.App;
using ZipContext.Core;

internal static class SettingsChecks
{
    internal static void Run(Action<string, bool> check, string root)
    {
        Directory.CreateDirectory(root);
        var path=Path.Combine(root,"custom.ini");
        var original="; keep heading\n[PREVIEW]\nRootMax = 7 ; keep inline\nunknown=keep\nrootMax=8 # duplicate\n[other]\nrootMax=99\ntext=自由文字\n[archive]\nsplitFirst=.001;.z01 ; aliases\nsplitLater=\nopenWithSevenZip=YES\n";
        File.WriteAllText(path,original,new UTF8Encoding(false));
        var s=SettingsDocument.Load(path);
        check("settings duplicate last value wins",s.RootMax==8);
        check("settings aliases and semicolon separators read",s.SplitFirstSuffixes==".001;.z01");
        check("settings empty list retained",s.SplitLaterSuffixes=="");
        check("settings boolean aliases read",s.OpenWithSevenZip);
        s.RootMax=12; s.SplitFirstSuffixes=""; s.Save(path);
        var saved=File.ReadAllText(path);
        check("settings comments retained",saved.Contains("; keep heading") && saved.Contains("; keep inline") && saved.Contains("# duplicate") && saved.Contains("; aliases"));
        check("settings unknown keys and sections retained",saved.Contains("unknown=keep") && saved.Contains("[other]\nrootMax=99\ntext=自由文字"));
        check("settings newline and absence of BOM retained",!saved.Contains('\r') && File.ReadAllBytes(path)[0]!=(byte)239);
        check("settings aliases updated without reverting empty",SettingsDocument.Load(path).SplitFirstSuffixes=="" && SettingsDocument.Load(path).RootMax==12);
        check("settings missing known keys supplied",saved.Contains("childrenperfolder=3") && saved.Contains("maxdepth=1"));
        s.Save(path);
        check("settings repeated save stable",File.ReadAllText(path)==saved);
        s=new SettingsDocument(); s.Save(path);
        check("settings explicit reset restores defaults",SettingsDocument.Load(path).SplitFirstSuffixes==ArchiveRules.DefaultSplitFirstSuffixes && File.ReadAllText(path).Contains("unknown=keep"));
        s.SplitFirstSuffixes=".001\n[evil]";
        var before=File.ReadAllBytes(path); var rejected=false;
        try { s.Save(path); } catch (ArgumentException) { rejected=true; }
        check("settings invalid suffix cannot inject configuration",rejected && before.SequenceEqual(File.ReadAllBytes(path)));
        var fresh=Path.Combine(root,"new.ini"); new SettingsDocument().Save(fresh);
        check("settings new file has shared template and BOM",File.ReadAllBytes(fresh).Take(3).SequenceEqual(new byte[]{239,187,191}) && File.ReadAllText(fresh).Contains("[preview]"));
        File.WriteAllText(path,"[preview]\nrootMax=-1\nmaxDepth=9\n[archive]\nsplitFirstSuffixes=\nsplitLaterSuffixes=\n");
        s=SettingsDocument.Load(path);
        check("settings invalid numeric values use defaults",s.RootMax==20 && s.MaxDepth==1);
        check("settings blank differs from missing suffix key",s.SplitFirstSuffixes=="" && SettingsDocument.Load(fresh).SplitFirstSuffixes.Length>0);
        check("settings suffix normalization shared",SettingsDocument.ParseSuffixList("001;.z01,.001").SequenceEqual(new[]{".001",".z01"}));
        var ini=AppSettings.IniPath;
        var backup=File.Exists(ini)?File.ReadAllBytes(ini):null;
        try
        {
            File.WriteAllText(ini,"; hand edited\n[preview]\nrootMax=9\n[archive]\nsplitFirstSuffixes=\nsplitLaterSuffixes=\n[custom]\nx=keep\n");
            File.SetLastWriteTimeUtc(ini,DateTime.UtcNow.AddMinutes(3));
            var app=AppSettings.Load();
            check("app reads empty lists without default fallback",app.SplitFirstSuffixes=="" && app.SplitLaterSuffixes=="");
            check("menu archive rules read empty lists",ArchiveRules.Current.SplitFirstSuffixes.Count==0 && ArchiveRules.Current.SplitLaterSuffixes.Count==0);
            check("disabled numeric first volume not extractable",!ArchiveFormats.IsArchive(Path.Combine(root,"disabled.7z.001")));
            app.RootMax=17; app.Save();
            File.SetLastWriteTimeUtc(ini,DateTime.UtcNow.AddMinutes(4));
            var preview=typeof(ZipContext.Classic.PreviewMenu).Assembly.GetType("ZipContext.Classic.PreviewSettings")!;
            var current=preview.GetProperty("Current")!.GetValue(null)!;
            check("classic preview reads same shared settings",(int)preview.GetProperty("RootMax")!.GetValue(current)! == 17);
            check("app save preserves hand edits",File.ReadAllText(ini).Contains("; hand edited") && File.ReadAllText(ini).Contains("x=keep"));
        }
        finally { if(backup is null) File.Delete(ini); else File.WriteAllBytes(ini,backup); }
    }
}