using Lixin.Core.Election;

namespace Lixin.Tests;

/// <summary>能力档案持久化测试。全部使用临时目录，不碰真实的 %LOCALAPPDATA%。</summary>
public class CapabilityProfileStoreTests : IDisposable
{
    private readonly string _tempDir;

    public CapabilityProfileStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lixin-tests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private CapabilityProfileStore CreateStore() => new(_tempDir);

    // ---------- 身份生成 ----------

    [Fact]
    public void LoadOrCreate_NoFile_GeneratesPersisted16ByteIdentity()
    {
        var store = CreateStore();
        var profile = store.LoadOrCreate();

        Assert.Equal(CampaignKey.Size, profile.DeviceId.Length);
        Assert.False(profile.PeripheralUnsupported);
        Assert.True(File.Exists(store.FilePath), "创建身份后必须立即落盘，否则每次启动都换身份");
    }

    [Fact]
    public void Identity_IsStableAcrossReloads()
    {
        var store = CreateStore();
        var first = store.LoadOrCreate().DeviceId;

        // 模拟进程重启：重新构造 store 再读
        var second = CreateStore().LoadOrCreate().DeviceId;

        Assert.Equal(first, second);
    }

    [Fact]
    public void Identity_IsStableAcrossStoreInstances_InSameRun()
    {
        var id = CreateStore().LoadOrCreate().DeviceId;
        Assert.Equal(id, CreateStore().LoadOrCreate().DeviceId);
    }

    // ---------- 失败学习（PRD 规则 3） ----------

    [Fact]
    public void MarkPeripheralUnsupported_PersistsAcrossReload()
    {
        var store = CreateStore();
        var profile = store.LoadOrCreate();
        profile.MarkPeripheralUnsupported("GattServiceProvider: 0x80070005");

        store.Save(profile);

        var reloaded = CreateStore().LoadOrCreate();
        Assert.True(reloaded.PeripheralUnsupported);
        Assert.Equal("GattServiceProvider: 0x80070005", reloaded.PeripheralFailureReason);
        Assert.NotNull(reloaded.PeripheralUnsupportedAt);
    }

    [Fact]
    public void EffectiveCapabilities_FailureTurnsOffAdvertiseBit()
    {
        var profile = CreateStore().LoadOrCreate();

        var healthy = profile.EffectiveCapabilities(adapterSupportsPeripheral: true, adapterSupportsCentral: true);
        Assert.True(healthy.CanAdvertise);
        Assert.True(healthy.CanConnect);

        profile.MarkPeripheralUnsupported("广播启动失败");
        var degraded = profile.EffectiveCapabilities(adapterSupportsPeripheral: true, adapterSupportsCentral: true);
        Assert.False(degraded.CanAdvertise);   // 选举输入随之翻转，本机此后只当中心
        Assert.True(degraded.CanConnect);
    }

    [Fact]
    public void ClearPeripheralUnsupported_RestoresAdvertiseBit()
    {
        var profile = CreateStore().LoadOrCreate();
        profile.MarkPeripheralUnsupported("x");
        profile.ClearPeripheralUnsupported();

        var caps = profile.EffectiveCapabilities(true, true);
        Assert.True(caps.CanAdvertise);
        Assert.Null(profile.PeripheralFailureReason);
        Assert.Null(profile.PeripheralUnsupportedAt);
    }

    // ---------- 损坏防御 ----------

    [Fact]
    public void CorruptJson_ReturnsFreshProfile_WithoutInheritingFailure()
    {
        var store = CreateStore();

        // 先造一份「带失败记录」的正常档案
        var profile = store.LoadOrCreate();
        profile.MarkPeripheralUnsupported("旧的失败");
        store.Save(profile);

        // 然后把文件写坏
        File.WriteAllText(store.FilePath, "{ 这不是 JSON ]");

        var recovered = store.LoadOrCreate();
        Assert.False(recovered.PeripheralUnsupported, "档案损坏不能继承旧的失败记录，更不能凭空制造失败");
        Assert.Equal(CampaignKey.Size, recovered.DeviceId.Length);
    }

    [Fact]
    public void WrongVersion_ReturnsFreshProfile()
    {
        var store = CreateStore();
        var profile = store.LoadOrCreate();
        profile.Version = 99;    // 模拟未来版本写出的档案
        store.Save(profile);

        var recovered = store.LoadOrCreate();
        Assert.Equal(CapabilityProfile.CurrentVersion, recovered.Version);
    }

    [Fact]
    public void WrongDeviceIdLength_ReturnsFreshProfile()
    {
        var store = CreateStore();
        var profile = store.LoadOrCreate();
        profile.DeviceId = new byte[8];   // 被截断/篡改过的身份不可用
        store.Save(profile);

        var recovered = store.LoadOrCreate();
        Assert.Equal(CampaignKey.Size, recovered.DeviceId.Length);
    }

    [Fact]
    public void Save_IsAtomic_NoTempFileLeftBehind()
    {
        var store = CreateStore();
        store.Save(store.LoadOrCreate());
        store.Save(store.LoadOrCreate());   // 连续两次覆盖

        Assert.False(File.Exists(store.FilePath + ".tmp"), "临时文件必须被替换掉，不能残留");
        Assert.True(File.Exists(store.FilePath));
    }

    [Fact]
    public void Save_CreatesMissingDirectory()
    {
        var deepDir = Path.Combine(_tempDir, "a", "b", "c");
        var store = new CapabilityProfileStore(deepDir);
        store.Save(store.LoadOrCreate());
        Assert.True(File.Exists(store.FilePath));
    }
}
