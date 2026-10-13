using Mithu.Core.Layout;

namespace Mithu.Core.Interfaces;

/// <summary>Enumerates connected displays.</summary>
public interface IMonitorProvider
{
    IReadOnlyList<MonitorInfo> GetMonitors();
}
