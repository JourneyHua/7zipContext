using System.Runtime.InteropServices;

// Diagnostic-only ownership scope; every acquisition is registered immediately.
internal sealed class NativeResources : IDisposable
{
    private readonly List<(string Kind, object Id, Action Free)> _owned = new();
    private void Add(string kind, object id, Action free) => _owned.Add((kind,id,free));
    private void Free(string kind, object id)
    {
        var index=_owned.FindLastIndex(item => item.Kind==kind && (id is IntPtr ? item.Id.Equals(id) : ReferenceEquals(item.Id,id)));
        if(index<0) return;
        var item=_owned[index]; _owned.RemoveAt(index); item.Free();
    }
    public void Pidl(IntPtr pointer) { if(pointer!=IntPtr.Zero) Add("pidl",pointer,()=>Marshal.FreeCoTaskMem(pointer)); }
    public void ComPointer(IntPtr pointer) { if(pointer!=IntPtr.Zero) Add("com",pointer,()=>Marshal.Release(pointer)); }
    public T Rcw<T>(IntPtr pointer)
    {
        var value=Marshal.GetObjectForIUnknown(pointer);
        Add("rcw",value,()=>Marshal.ReleaseComObject(value));
        return (T)value;
    }
    public IntPtr Allocate(int size) { var p=Marshal.AllocHGlobal(size); Add("memory",p,()=>Marshal.FreeHGlobal(p)); return p; }
    public IntPtr Menu(IntPtr menu) { if(menu!=IntPtr.Zero) Add("menu",menu,()=>DestroyMenu(menu)); return menu; }
    public void FreeHGlobal(IntPtr p) => Free("memory",p);
    public void Release(IntPtr p) => Free("com",p);
    public void ReleaseComObject(object value) => Free("rcw",value);
    public void Destroy(IntPtr menu) => Free("menu",menu);
    internal void OnDispose(Action action) => Add("callback",action,action);
    public void Dispose()
    {
        for(int i=_owned.Count-1;i>=0;i--) { try { _owned[i].Free(); } catch { } }
        _owned.Clear();
    }
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
}
// UI diagnostic callbacks must never synchronously Invoke their owner thread.
internal sealed class DrainingTimer : IDisposable
{
    private readonly System.Threading.Timer _timer;
    public DrainingTimer(TimerCallback callback, object? state, int dueTime, int period)
        => _timer = new System.Threading.Timer(callback,state,dueTime,period);
    public void Dispose() => _timer.DisposeAsync().AsTask().GetAwaiter().GetResult();
}