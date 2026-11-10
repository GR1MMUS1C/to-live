using Unity.Entities;
using Unity.NetCode;

namespace ToLive.Net
{
    /// <summary>
    /// Custom Netcode bootstrap. Unity calls this once at startup (think of it like a custom
    /// UGameInstance::Init in Unreal). Instead of creating client/server worlds straight away we
    /// only create a plain "local" world, so the game boots into the main menu. The menu then
    /// creates the networked worlds on demand (see <see cref="NetSession"/>).
    /// </summary>
    [UnityEngine.Scripting.Preserve]
    public class ToLiveBootstrap : ClientServerBootstrap
    {
        public override bool Initialize(string defaultWorldName)
        {
            AutoConnectPort = 0; // never auto-connect; Host/Join buttons decide.
            CreateLocalWorld(defaultWorldName);
            return true;
        }
    }
}
