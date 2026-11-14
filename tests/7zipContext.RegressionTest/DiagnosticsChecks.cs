using System.Diagnostics;
using System.Runtime.InteropServices;
using ZipContext.Core;

internal static class DiagnosticsChecks
{
    internal static void Run(Action<string,bool> check,string root)
    {
        Directory.CreateDirectory(root);
        ArchiveEntry Entry(string name,long size=1,bool directory=false) => new(name,directory,size,0,null,false);
        var tree=new TreeNode{Name="root",IsFolder=true};
        var watch=Stopwatch.StartNew();
        for(int i=0;i<10000;i++) tree.Insert(Entry("folder/item"+i));
        watch.Stop();
        check("indexed tree keeps all wide-directory entries",tree.Children.Single().Children.Count==10000);
        Console.WriteLine($"INFO wide tree construction: {watch.ElapsedMilliseconds}ms / 10000 entries");
        tree.Insert(Entry("folder/item100",42));
        check("tree duplicate updates metadata",tree.Children[0].Children.Single(n=>n.Name=="item100").Size==42);
        tree.Insert(Entry("same",directory:true)); tree.Insert(Entry("same"));
        check("tree keeps same-name file and directory distinct",tree.Children.Count(n=>n.Name=="same")==2);
        var promoted=new TreeNode{Name="p",IsFolder=true};
        promoted.Insert(Entry("node"));
        promoted.Insert(Entry("node/child.txt"));
        check("tree promotes same-name file node to folder",promoted.Children.Count(n=>n.Name=="node")==1 && promoted.Children[0].IsFolder &&
            promoted.Children[0].Children.Count==1);
        tree.Insert(Entry("Case")); tree.Insert(Entry("case"));
        check("tree preserves case-sensitive names",tree.Children.Count(n=>n.Name is "Case" or "case")==2);
        var list=tree.Children;
        list.Clear(); tree.Insert(Entry("after-clear"));
        check("tree supports clearing retained public list",list.Count==1 && list[0].Name=="after-clear");
        var replacement=new TreeNode{Name="replacement",IsFolder=false}; list[0]=replacement;
        tree.Insert(Entry("replacement",7));
        check("tree supports same-count external replacement",list.Count==1 && ReferenceEquals(list[0],replacement) && replacement.Size==7);
        tree.Insert(Entry("z")); tree.Insert(Entry("a")); tree.SortRecursive(); tree.Insert(Entry("a",9));
        check("tree sorting preserves lookup semantics",list.Count==3 && list.Single(n=>n.Name=="a").Size==9 && list[0].Name=="a");
        var path=Path.Combine(root,"bounded.log");
        File.WriteAllText(path,new string('A',BoundedLog.MaximumBytes-4));
        BoundedLog.TryAppend(path,"rotation\n");
        check("log rotates before crossing size limit",File.Exists(path+".1") && File.ReadAllText(path)=="rotation\n");
        BoundedLog.TryAppend(path,new string('中',100000));
        check("log truncates oversized entries",new FileInfo(path).Length<20000 && File.ReadAllText(path).Contains("[truncated]"));
        var before=File.ReadAllBytes(path);
        using(var gate=new FileStream(path+".lock",FileMode.Open,FileAccess.Write,FileShare.None))
            BoundedLog.TryAppend(path,"busy\n");
        check("busy log lock drops entry without failing",before.SequenceEqual(File.ReadAllBytes(path)));
        File.WriteAllText(path,new string('B',BoundedLog.MaximumBytes+1));
        File.WriteAllText(path+".1",new string('C',BoundedLog.MaximumBytes+1));
        BoundedLog.TryAppend(path,"recovered\n");
        check("legacy oversized logs are bounded on next write",new FileInfo(path).Length<=BoundedLog.MaximumBytes && (!File.Exists(path+".1") || new FileInfo(path+".1").Length<=BoundedLog.MaximumBytes));
        Parallel.For(0,40,i=>BoundedLog.TryAppend(path,"parallel "+i+"\n"));
        check("concurrent log writes stay bounded",new FileInfo(path).Length<=BoundedLog.MaximumBytes);
        BoundedLog.TryAppend(Path.Combine(root,"missing","no.log"),"ignored");
        check("unavailable log directory does not throw",!Directory.Exists(Path.Combine(root,"missing")));
        var order=new List<int>();
        var resources=new NativeResources();
        resources.OnDispose(()=>order.Add(1));
        resources.OnDispose(()=>throw new IOException("fixture release failure"));
        resources.OnDispose(()=>order.Add(2));
        resources.Dispose(); resources.Dispose();
        check("resource scope releases in reverse order despite failure",order.SequenceEqual(new[]{2,1}));
        using(var scope=new NativeResources())
        {
            var memory=scope.Allocate(16); Marshal.WriteInt32(memory,123);
            check("owned native buffer is usable",Marshal.ReadInt32(memory)==123);
            scope.FreeHGlobal(memory); scope.FreeHGlobal(memory);
        }
        // 受限宿主（沙箱/低权限策略）可能不允许打开自身进程句柄查询 GDI/USER 计数。
        // 此时只跳过句柄计数那一项，不把「没测到」记成通过，也不影响后续检查。
        IntPtr? processHandle = null;
        try { processHandle = Process.GetCurrentProcess().Handle; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            Console.WriteLine($"SKIP GDI/USER handle accounting: host denied process handle ({ex.Message})");
        }
        if (processHandle is { } handle)
        {
            var count=GetGuiResources(handle,1);
            var created=0;
            for(int i=0;i<100;i++) { using var scope=new NativeResources(); if(scope.Menu(CreatePopupMenu())!=IntPtr.Zero) created++; }
            check("owned undisplayed menus do not accumulate handles",created==100 && GetGuiResources(handle,1)<=count+2);
        }
        using var started=new ManualResetEventSlim();
        var finished=0;
        var timer=new DrainingTimer(_=>{started.Set(); Thread.Sleep(10); Interlocked.Increment(ref finished);},null,0,Timeout.Infinite);
        var callbackStarted=started.Wait(5000);
        timer.Dispose();
        check("diagnostic timer disposes after callback completion",callbackStarted && finished==1);
    }
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr process,uint flags);
}
