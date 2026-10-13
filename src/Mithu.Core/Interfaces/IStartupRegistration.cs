namespace Mithu.Core.Interfaces;

/// <summary>Controls whether Mithu starts when the user signs in to Windows.</summary>
public interface IStartupRegistration
{
    bool IsEnabled { get; }

    void SetEnabled(bool enabled);
}
