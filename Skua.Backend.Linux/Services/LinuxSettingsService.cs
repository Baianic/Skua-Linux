using Skua.Core.Interfaces;
using Skua.Core.Models;
using Skua.Core.Services;

namespace Skua.Backend.Linux.Services;

public sealed class LinuxSettingsService : ISettingsService
{
    private readonly UnifiedSettingsService _unifiedService;

    public LinuxSettingsService()
    {
        _unifiedService = new UnifiedSettingsService();
        _unifiedService.Initialize(AppRole.Client);
    }

    public T? Get<T>(string key)
    {
        return _unifiedService.Get<T>(key);
    }

    public T Get<T>(string key, T defaultValue)
    {
        return _unifiedService.Get(key, defaultValue);
    }

    public void Set<T>(string key, T value)
    {
        _unifiedService.Set(key, value);
    }

    public void Initialize(AppRole role)
    {
        _unifiedService.Initialize(role);
    }

    public SharedSettings GetShared()
    {
        return _unifiedService.GetShared();
    }

    public ClientSettings GetClient()
    {
        return _unifiedService.GetClient();
    }

    public ManagerSettings GetManager()
    {
        return _unifiedService.GetManager();
    }

    public void SetApplicationVersion()
    {
        _unifiedService.SetApplicationVersion();
    }
}
