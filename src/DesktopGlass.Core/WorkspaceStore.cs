using System.Text.Json;

namespace DesktopGlass.Core;

/// <summary>原子保存工作区，保留有效备份，拒绝用默认配置掩盖数据损坏。</summary>
public sealed class WorkspaceStore(string directory)
{
    private readonly string path = Path.Combine(directory, "workspace.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    /// <summary>最近一次读取是否使用备份；为 true 时原损坏文件仍保留。</summary>
    public bool RecoveredFromBackup { get; private set; }
    private byte[]? migrationSource;

    /// <summary>读取配置，不存在时返回空工作区；主文件及备份均无效时抛出 InvalidDataException。</summary>
    /// <returns>已校验工作区。</returns>
    public Workspace Load()
    {
        RecoveredFromBackup = false; migrationSource = null;
        if (!File.Exists(path) && !File.Exists(path + ".bak")) return new Workspace();
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                if (new FileInfo(candidate).Length > 8 * 1024 * 1024) throw new InvalidDataException("配置过大。");
                var original = File.ReadAllBytes(candidate);
                ReadOnlyMemory<byte> json = original;
                if (original.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) json = json[3..];
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("Version", out var version) || version.ValueKind != JsonValueKind.Number ||
                    !version.TryGetInt32(out var number)) throw new InvalidDataException("配置版本字段损坏。");
                if (number is not (1 or 2))
                    throw new NotSupportedException("此配置来自更新的软件版本，已保留原文件；请使用匹配版本打开。");
                var value = JsonSerializer.Deserialize<Workspace>(json.Span, Options) ?? throw new InvalidDataException("空配置。");
                value.Validate(); RecoveredFromBackup = candidate != path;
                if (value.Version == 1)
                {
                    migrationSource = original;
                    value.Version = 2;
                    foreach (var group in value.Groups) group.DesktopVisible = false;
                }
                return value;
            }
            catch (Exception e) when (e is JsonException or InvalidDataException) { /* 仅尝试上一份有效备份，不覆盖损坏数据。 */ }
        }
        throw new InvalidDataException("配置与备份均无法读取。原文件已保留，请备份后检查。");
    }

    /// <summary>校验并原子替换配置；使用备份恢复后首次保存保留原损坏副本。</summary>
    /// <param name="workspace">完整工作区。</param><exception cref="InvalidDataException">数据不符合范围。</exception>
    public void Save(Workspace workspace)
    {
        workspace.Validate();
        if (workspace.Version != 2) throw new InvalidDataException("旧版配置必须先读取迁移，禁止直接覆盖保存。");
        Directory.CreateDirectory(directory);
        if (migrationSource is not null)
        {
            var backup = Path.Combine(directory, "workspace.v1.backup.json");
            if (!File.Exists(backup))
            {
                using var stream = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(migrationSource); stream.Flush(true);
            }
        }
        var temporary = path + ".new";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, workspace, Options); stream.Flush(true); }
        if (File.Exists(path))
        {
            if (RecoveredFromBackup) File.Copy(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"), false);
            File.Replace(temporary, path, RecoveredFromBackup ? null : path + ".bak");
        }
        else File.Move(temporary, path);
        RecoveredFromBackup = false; migrationSource = null;
    }
}
