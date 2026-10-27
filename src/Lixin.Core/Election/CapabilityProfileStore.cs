using System.IO;
using System.Text.Json;

namespace Lixin.Core.Election;

/// <summary>
/// 能力档案的 JSON 持久化。
///
/// 设计取舍（都有明确理由）：
/// - **原子写入**：先写 `.tmp` 再替换。写到一半崩溃不会留下半截 JSON。
/// - **损坏即重建**：JSON 解析失败或版本不符时返回全新档案。
///   代价是本机身份（竞选键）会换——可接受，因为它只是个随机数，不承载任何用户数据；
///   换掉它最坏结果是「角色分配方向翻转一次」。
/// - **路径注入**：目录由调用方给定，测试用临时目录，产品用 `%LOCALAPPDATA%\Lixin\`。
/// </summary>
public sealed class CapabilityProfileStore
{
    private static readonly string TempSuffix = ".tmp";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _directory;
    private readonly string _filePath;

    /// <summary>构造一个存储实例。目录不必预先存在，<see cref="Save"/> 会创建。</summary>
    public CapabilityProfileStore(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("目录不能为空。", nameof(directory));
        }

        _directory = directory;
        _filePath = Path.Combine(directory, "capability-profile.json");
    }

    /// <summary>
    /// 默认位置：`%LOCALAPPDATA%\Lixin\`（PRD §7.4.1 规则 3）。
    /// </summary>
    public static CapabilityProfileStore CreateDefault()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new CapabilityProfileStore(Path.Combine(root, "Lixin"));
    }

    /// <summary>档案文件完整路径，诊断用。</summary>
    public string FilePath => _filePath;

    /// <summary>
    /// 读取档案；不存在则创建并**立即持久化**——竞选键必须跨运行稳定，
    /// 若只生成不落盘，每次启动都会换身份，角色分配就会漂移。
    /// </summary>
    public CapabilityProfile LoadOrCreate()
    {
        if (!File.Exists(_filePath))
        {
            var fresh = CreateFresh();
            Save(fresh);
            return fresh;
        }

        try
        {
            CapabilityProfile? profile;

            // 必须先读完并**关闭读句柄**，之后才可能替换这个文件。
            // 注意 C# 的坑：`using` 块里的 `return Recreate(...)` 会先执行表达式、后执行 Dispose，
            // 原样写在 using 里会在文件还被本进程占用时就去替换，Windows 直接拒绝（Access Denied）。
            using (var stream = File.OpenRead(_filePath))
            {
                profile = JsonSerializer.Deserialize<CapabilityProfile>(stream);
            }

            if (profile is null || profile.Version != CapabilityProfile.CurrentVersion
                || profile.DeviceId.Length != CampaignKey.Size)
            {
                return Recreate("档案缺失字段或版本不符，已重建");
            }

            return profile;
        }
        catch (JsonException)
        {
            return Recreate("档案文件损坏（JSON 解析失败），已重建");
        }
        catch (IOException)
        {
            // 读取到一半失败（磁盘/占用问题）与损坏同等对待，避免启动被档案卡死
            return Recreate("档案文件读取失败，已重建");
        }
    }

    /// <summary>保存档案（原子写入：先写临时文件再替换）。</summary>
    public void Save(CapabilityProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        Directory.CreateDirectory(_directory);

        var tempPath = _filePath + TempSuffix;
        using (var stream = File.Create(tempPath))
        {
            JsonSerializer.Serialize(stream, profile, JsonOptions);
        }

        File.Move(tempPath, _filePath, overwrite: true);
    }

    private CapabilityProfile Recreate(string reason)
    {
        // 注意：档案损坏不等于外设角色失败，不能写进 PeripheralUnsupported* 字段，
        // 否则一次文件损坏就会让本机永久失去外设资格。丢弃重建即可。
        _ = reason;
        var fresh = CreateFresh();
        Save(fresh);
        return fresh;
    }

    private static CapabilityProfile CreateFresh()
    {
        var profile = new CapabilityProfile();
        CampaignKey.NewRandom().Value.Span.CopyTo(profile.DeviceId);
        return profile;
    }
}
