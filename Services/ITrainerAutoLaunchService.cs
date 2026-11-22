using SteamLua.Models;

namespace SteamLua.Services;

public interface ITrainerAutoLaunchService : IDisposable
{
    void Start();
    void Stop();
    void ReloadBindings();
    event Action<string, bool>? StatusChanged;
}
