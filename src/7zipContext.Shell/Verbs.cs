using System.Runtime.InteropServices;

namespace ZipContext.Shell;

// ---------- 8 个右键动词（CLSID 必须与 AppxManifest.xml 完全一致） ----------

[ComVisible(true)]
[Guid(VerbClsids.SmartExtract)]
public sealed class SmartExtractCommand : ExplorerCommandBase
{
    protected override string Title => "智能解压到此处(S)";
    protected override string Verb => "--smart-extract";
    protected override Guid CanonicalGuid => new(VerbClsids.SmartExtract);
    protected override bool IsVisible(IReadOnlyList<string> paths) => ArchiveFormatsCompat.CanExtract(paths);
}

[ComVisible(true)]
[Guid(VerbClsids.SmartExtractDelete)]
public sealed class SmartExtractDeleteCommand : ExplorerCommandBase
{
    protected override string Title => "智能解压到此处并删除";
    protected override string Verb => "--smart-extract-delete";
    protected override Guid CanonicalGuid => new(VerbClsids.SmartExtractDelete);
    protected override bool IsVisible(IReadOnlyList<string> paths) => ArchiveFormatsCompat.CanExtract(paths);
}

[ComVisible(true)]
[Guid(VerbClsids.ExtractToName)]
public sealed class ExtractToNameCommand : ExplorerCommandBase
{
    // {name} 会在 GetTitle 时替换为去扩展名的压缩包名（显示为「解压到 xxx\」）
    protected override string Title => "解压到 {name}\\(T)";
    protected override string Verb => "--extract-to-name";
    protected override Guid CanonicalGuid => new(VerbClsids.ExtractToName);
    protected override bool IsVisible(IReadOnlyList<string> paths) => ArchiveFormatsCompat.CanExtract(paths);
}

[ComVisible(true)]
[Guid(VerbClsids.ExtractToFolder)]
public sealed class ExtractToFolderCommand : ExplorerCommandBase
{
    protected override string Title => "解压到其他文件夹(B)...";
    protected override string Verb => "--extract-to";
    protected override Guid CanonicalGuid => new(VerbClsids.ExtractToFolder);
    protected override bool IsVisible(IReadOnlyList<string> paths) => ArchiveFormatsCompat.CanExtract(paths);
}

[ComVisible(true)]
[Guid(VerbClsids.AddZip)]
public sealed class AddZipCommand : ExplorerCommandBase
{
    protected override string Title => "添加到 {name}.zip";
    protected override string Verb => "--add-zip";
    protected override Guid CanonicalGuid => new(VerbClsids.AddZip);
    protected override bool IsVisible(IReadOnlyList<string> paths) => paths.Count > 0;
}

[ComVisible(true)]
[Guid(VerbClsids.Add7z)]
public sealed class Add7zCommand : ExplorerCommandBase
{
    protected override string Title => "添加到 {name}.7z";
    protected override string Verb => "--add-7z";
    protected override Guid CanonicalGuid => new(VerbClsids.Add7z);
    protected override bool IsVisible(IReadOnlyList<string> paths) => paths.Count > 0;
}

[ComVisible(true)]
[Guid(VerbClsids.QuickZip)]
public sealed class QuickZipCommand : ExplorerCommandBase
{
    protected override string Title => "快速压缩到 {name}.zip(打开所在文件夹)";
    protected override string Verb => "--quick-zip";
    protected override Guid CanonicalGuid => new(VerbClsids.QuickZip);
    protected override bool IsVisible(IReadOnlyList<string> paths) => paths.Count > 0;
}

/// <summary>动词可见性辅助。分卷规则统一由 Core 提供。</summary>
internal static class ArchiveFormatsCompat
{
    public static bool IsArchive(string path) => ZipContext.Core.ArchiveFormats.IsArchive(path);

    public static bool AllArchives(IReadOnlyList<string> paths) =>
        paths.Count > 0 && paths.All(IsArchive);

    /// <summary>解压动词可见性：含 .001 首卷即显示；否则保持“全是压缩包才显示”。</summary>
    public static bool CanExtract(IReadOnlyList<string> paths) =>
        ZipContext.Core.ArchiveSelection.CanExtract(paths);

    public static IReadOnlyList<string> TargetsForExtraction(IReadOnlyList<string> paths) =>
        ZipContext.Core.ArchiveSelection.TargetsForExtraction(paths);
}

/// <summary>CLSID 常量（与 package/AppxManifest.xml 一一对应）。</summary>
public static class VerbClsids
{
    public const string SmartExtract = "B9A10001-8C3F-4A2E-9E6D-5F1A2B3C4D01";
    public const string SmartExtractDelete = "B9A10002-8C3F-4A2E-9E6D-5F1A2B3C4D02";
    public const string ExtractToName = "B9A10003-8C3F-4A2E-9E6D-5F1A2B3C4D03";
    public const string ExtractToFolder = "B9A10004-8C3F-4A2E-9E6D-5F1A2B3C4D04";
    // B9A10005 已退役：压缩文件预览改由 7zipContext.Classic 在经典菜单内联渲染
    public const string AddZip = "B9A10006-8C3F-4A2E-9E6D-5F1A2B3C4D06";
    public const string Add7z = "B9A10007-8C3F-4A2E-9E6D-5F1A2B3C4D07";
    public const string QuickZip = "B9A10008-8C3F-4A2E-9E6D-5F1A2B3C4D08";

    public static readonly IReadOnlyDictionary<string, Type> Map = new Dictionary<string, Type>
    {
        [SmartExtract] = typeof(SmartExtractCommand),
        [SmartExtractDelete] = typeof(SmartExtractDeleteCommand),
        [ExtractToName] = typeof(ExtractToNameCommand),
        [ExtractToFolder] = typeof(ExtractToFolderCommand),
        [AddZip] = typeof(AddZipCommand),
        [Add7z] = typeof(Add7zCommand),
        [QuickZip] = typeof(QuickZipCommand),
    };
}
