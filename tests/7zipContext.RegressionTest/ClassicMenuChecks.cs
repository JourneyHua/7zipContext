using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using ZipContext.Classic;

// Only creates/destroys undisplayed HMENU objects. Never tracks a popup or invokes a valid action.
internal static class ClassicMenuChecks
{
    private const uint Separator = 0x800;
    private const uint ByPosition = 0x400;
    private const uint Grayed = 1;
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    internal static void Run(Action<string, bool> check, string work)
    {
        Directory.CreateDirectory(work);
        var text = Path.Combine(work, "plain.txt");
        File.WriteAllText(text, "menu fixture");
        var archive = Path.Combine(work, "preview.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create)) zip.CreateEntry("inside.txt");

        foreach (var flag in new[] { 1u, 0x101u, 0x800u })
        {
            var calls = 0;
            var handler = Seed(text, insert: (h, p, f, id, label) => { calls++; return NativeMethods.InsertMenuW(h, p, f, id, label); });
            WithMenu(h =>
            {
                var result = handler.QueryContextMenu(h, 0, 100, 200, flag);
                check($"menu flag 0x{flag:X} adds nothing", result == 0 && calls == 0 && Rows(h).Count == 0 && Commands(handler).Count == 0);
            });
        }

        foreach (var (first, last, expected) in new[]
                 { (100, 100, 1), (100, 101, 2), (100, 102, 3), (100, 99, 0),
                   (0, 1, 2), (int.MaxValue - 1, int.MaxValue, 2), (-2, -1, 2) })
        {
            var handler = Seed(text);
            WithMenu(h =>
            {
                var result = handler.QueryContextMenu(h, 0, first, last, 0);
                var rows = Rows(h).Where(r => (r.Type & Separator) == 0).ToList();
                check($"menu ID range {unchecked((uint)first)}..{unchecked((uint)last)} stays inclusive",
                    result == expected && Commands(handler).Count == expected && rows.Count == expected &&
                    rows.Select(r => (ulong)r.Id).SequenceEqual(Enumerable.Range(0, expected).Select(i => (ulong)unchecked((uint)first) + (uint)i)));
            });
        }

        var normal = Seed(text);
        WithMenu(h =>
        {
            NativeMethods.InsertMenuW(h, 0, ByPosition, (UIntPtr)10, "existing first");
            NativeMethods.InsertMenuW(h, 1, ByPosition, (UIntPtr)11, "existing last");
            var result = normal.QueryContextMenu(h, 1, 100, 200, 8); // NOVERBS is ignored by shortcut handlers.
            var rows = Rows(h);
            check("menu insertion preserves host items and insertion position", result == 3 &&
                rows[0].Text == "existing first" && rows[^1].Text == "existing last" && rows[1].Type == Separator);
            check("menu offsets preserve action order", Commands(normal).SequenceEqual(new[] { "--add-zip", "--add-7z", "--open-with-7zip" }));
        });

        var preview = Seed(archive, archive);
        WithMenu(h =>
        {
            var result = preview.QueryContextMenu(h, 0, 100, 200, 0);
            var rows = Rows(h).Where(r => (r.Type & Separator) == 0).ToList();
            check("preview rows use unique reserved IDs and count toward HRESULT", result == rows.Count &&
                result == Commands(preview).Count && rows.Count >= 9 && rows.Select(r => r.Id)
                    .SequenceEqual(Enumerable.Range(100, result).Select(i => (uint)i)));
            var title = rows.FindIndex(r => r.Text == "压缩文件预览");
            check("preview title is disabled and has no executable verb", title >= 0 &&
                (rows[title].State & Grayed) != 0 && Commands(preview)[title] is null);
            check("preview text never invokes an archive action", rows.Where((_, i) => Commands(preview)[i] is null)
                .All(r => (r.State & Grayed) != 0) && title >= 0 && Invoke(preview, (IntPtr)title) < 0);
        });
        WithMenu(h =>
        {
            var result = preview.QueryContextMenu(h, 0, 100, 107, 0);
            check("insufficient preview IDs keep actions without an orphan title", result == 7 &&
                Commands(preview).All(v => v is not null) && Rows(h).All(r => r.Text != "压缩文件预览"));
        });
        WithMenu(h =>
        {
            var result = preview.QueryContextMenu(h, 0, 100, 108, 0);
            check("preview stops at final supplied ID", result == 9 && Rows(h).Where(r => (r.Type & Separator) == 0)
                .All(r => r.Id is >= 100 and <= 108));
        });

        var attempts = 0;
        var partial = Seed(text, insert: (h, p, f, id, label) =>
            (f & Separator) == 0 && ++attempts == 2 ? false : NativeMethods.InsertMenuW(h, p, f, id, label));
        WithMenu(h =>
        {
            var result = partial.QueryContextMenu(h, 0, 100, 200, 0);
            check("failed insertion does not consume ID or verb mapping", result == 1 &&
                Commands(partial).SequenceEqual(new[] { "--add-zip" }) && Rows(h).Count(r => (r.Type & Separator) == 0) == 1);
            check("failed command offset cannot be invoked", Invoke(partial, (IntPtr)1) < 0);
        });

        attempts = 0;
        var throwing = Seed(text, insert: (h, p, f, id, label) =>
        {
            if ((f & Separator) == 0 && ++attempts == 2) throw new InvalidOperationException("simulated insert fault");
            return NativeMethods.InsertMenuW(h, p, f, id, label);
        });
        WithMenu(h => check("exception preserves count of IDs already inserted", throwing.QueryContextMenu(h, 0, 100, 200, 0) == 1 &&
            Commands(throwing).Count == 1 && Rows(h).Count(r => (r.Type & Separator) == 0) == 1));

        var never = Seed(text, insert: (_, _, _, _, _) => false);
        WithMenu(h => check("first insertion failure leaves no commands", never.QueryContextMenu(h, 0, 100, 200, 0) == 0 &&
            Commands(never).Count == 0 && Rows(h).Count == 0));
        WithMenu(h => check("invalid menu handle leaves no commands", normal.QueryContextMenu(IntPtr.Zero, 0, 100, 200, 0) == 0 && Commands(normal).Count == 0));

        var noTitle = Seed(archive, archive, (h, p, f, id, label) =>
            label == "压缩文件预览" ? false : NativeMethods.InsertMenuW(h, p, f, id, label));
        WithMenu(h => check("failed preview title preserves only action IDs", noTitle.QueryContextMenu(h, 0, 100, 200, 0) == 7 &&
            Commands(noTitle).Count == 7 && Rows(h).Count(r => (r.Type & Separator) == 0) == 7));

        WithMenu(h => normal.QueryContextMenu(h, 0, 100, 200, 0));
        WithMenu(h => check("default-only requery clears stale offsets", normal.QueryContextMenu(h, 0, 100, 200, 1) == 0 &&
            Commands(normal).Count == 0 && Invoke(normal, IntPtr.Zero) < 0 && Rows(h).Count == 0));
        WithMenu(h => normal.QueryContextMenu(h, 0, 100, 200, 0));
        Paths(normal).Clear();
        WithMenu(h => check("empty selection requery clears stale offsets", normal.QueryContextMenu(h, 0, 100, 200, 0) == 0 &&
            Commands(normal).Count == 0 && Invoke(normal, IntPtr.Zero) < 0));

        var valid = Seed(text);
        WithMenu(h => valid.QueryContextMenu(h, 0, 100, 200, 0));
        check("out of range integer verb reports failure", Invoke(valid, (IntPtr)Commands(valid).Count) < 0);
        check("high bits are never interpreted as integer offset zero", Invoke(valid, new IntPtr(1L << 32)) < 0);
        var name = Marshal.StringToHGlobalAnsi("unrecognized-verb");
        try { check("unrecognized string verb reports failure", Invoke(valid, name) < 0); }
        finally { Marshal.FreeHGlobal(name); }
        var invalid = new CMINVOKECOMMANDINFO { cbSize = 0, lpVerb = IntPtr.Zero };
        check("invalid invoke structure rejected", valid.InvokeCommand(ref invalid) < 0);
    }

    private static List<string> Paths(PreviewMenu handler) => (List<string>)typeof(PreviewMenu).GetField("_paths", PrivateInstance)!.GetValue(handler)!;
    private static List<string?> Commands(PreviewMenu handler) => (List<string?>)typeof(PreviewMenu).GetField("_commands", PrivateInstance)!.GetValue(handler)!;
    private static PreviewMenu Seed(string path, string? archive = null,
        Func<IntPtr, uint, uint, UIntPtr, string?, bool>? insert = null)
    {
        var handler = insert is null ? new PreviewMenu() : (PreviewMenu)typeof(PreviewMenu)
            .GetConstructors(PrivateInstance).Single().Invoke(new object[] { insert });
        Paths(handler).Add(path);
        typeof(PreviewMenu).GetField("_archive", PrivateInstance)!.SetValue(handler, archive);
        if (archive is not null)
            ((List<string>)typeof(PreviewMenu).GetField("_extractTargets", PrivateInstance)!.GetValue(handler)!).Add(archive);
        return handler;
    }
    private static int Invoke(PreviewMenu handler, IntPtr verb)
    {
        var info = new CMINVOKECOMMANDINFO { cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFO>(), lpVerb = verb };
        return handler.InvokeCommand(ref info);
    }
    private static void WithMenu(Action<IntPtr> action)
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) throw new InvalidOperationException("Cannot create undisplayed menu");
        try { action(menu); }
        finally { DestroyMenu(menu); }
    }
    private sealed record Row(uint Id, uint Type, uint State, string Text);
    private static List<Row> Rows(IntPtr menu)
    {
        var rows = new List<Row>();
        for (uint i = 0; i < GetMenuItemCount(menu); i++)
        {
            var info = new MENUITEMINFO { cbSize = (uint)Marshal.SizeOf<MENUITEMINFO>(), fMask = 0x103 };
            if (!GetMenuItemInfoW(menu, i, true, ref info)) throw new InvalidOperationException("Cannot inspect undisplayed menu");
            var text = new StringBuilder(512);
            GetMenuStringW(menu, i, text, text.Capacity, ByPosition);
            rows.Add(new Row(info.wID, info.fType, info.fState, text.ToString()));
        }
        return rows;
    }
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMenuStringW(IntPtr menu, uint item, StringBuilder text, int max, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMenuItemInfoW(IntPtr menu, uint item, [MarshalAs(UnmanagedType.Bool)] bool byPosition, ref MENUITEMINFO info);
}
