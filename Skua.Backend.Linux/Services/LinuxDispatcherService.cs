using Skua.Core.Interfaces;

namespace Skua.Backend.Linux.Services;

public sealed class LinuxDispatcherService : IDispatcherService
{
    public void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }
}
