using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZipContext.Core;

namespace ZipContext.App;

/// <summary>
/// engine.path 是唯一提交点。目标元数据先按目录写入记录，再原子替换指针。
/// engine.json 仅为旧脚本保留的兼容快照；读取时绝不将其他目录的记录配给当前引擎。
/// 调用者须持有 EngineOperationLock；只读调用无需锁。
/// </summary>
internal sealed class EngineStateStore
{
    private readonly string _root;
    private readonly Action<string, string> _write;
    public EngineStateStore(string root, Action<string, string>? write = null)
        => (_root, _write) = (Path.GetFullPath(root), write ?? AtomicTextFile.Write);
    internal string PointerPath => Path.Combine(_root, "engine.path");
    internal string MetadataPath => Path.Combine(_root, "engine.json");

    internal string? ReadPointer()
    {
        try { return AtomicTextFile.Read(PointerPath).Trim() is { Length: > 0 } p ? p : null; }
        catch { return null; }
    }

    internal EngineProvisioner.EngineInfo? ReadMetadata(string? directory = null)
    {
        directory ??= ReadPointer();
        if (string.IsNullOrWhiteSpace(directory)) return null;
        return ReadMatching(RecordPath(directory), directory) ?? ReadMatching(MetadataPath, directory);
    }

    internal string? Commit(EngineProvisioner.EngineInfo info)
    {
        var directory = Path.GetFullPath(info.Path);
        if (!EngineLocator.IsUsable(directory)) throw new IOException("目标引擎四件套不完整，未切换指针。");
        info = info with { Path = directory };
        // 为旧安装迁移当前记录，后续切换及手动回滚仍可读到原来源和校验信息。
        var before = ReadPointer();
        var previous = ReadMetadata(before);
        if (previous is not null && before is not null && !File.Exists(RecordPath(before)))
            _write(RecordPath(before), JsonSerializer.Serialize(previous));
        var record = RecordPath(directory);
        var sameDirectory = before is not null && string.Equals(Path.GetFullPath(before).TrimEnd('\\', '/'),
            directory.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        var oldRecord = sameDirectory && File.Exists(record) ? AtomicTextFile.Read(record) : null;
        _write(record, JsonSerializer.Serialize(info));
        try { _write(PointerPath, directory); } // 提交前失败，旧指针继续指向已有记录。
        catch (Exception commitError)
        {
            if (oldRecord is not null)
            {
                try { _write(record, oldRecord); }
                catch (Exception rollbackError)
                { throw new IOException("指针写入及同目录元数据恢复失败。", new AggregateException(commitError, rollbackError)); }
            }
            throw;
        }
        try { _write(MetadataPath, JsonSerializer.Serialize(info)); }
        catch (Exception ex)
        { return "引擎已切换，但兼容元数据快照更新失败：" + ex.Message; }
        return null;
    }

    private string RecordPath(string directory)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)).ToUpperInvariant();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return Path.Combine(_root, "engine-records", key + ".json");
    }

    private static EngineProvisioner.EngineInfo? ReadMatching(string path, string directory)
    {
        try
        {
            var info = JsonSerializer.Deserialize<EngineProvisioner.EngineInfo>(AtomicTextFile.Read(path));
            return info is not null && !string.IsNullOrWhiteSpace(info.Version) && string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(info.Path)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), StringComparison.OrdinalIgnoreCase)
                ? info : null;
        }
        catch { return null; }
    }
}
