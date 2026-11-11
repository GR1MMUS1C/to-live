using ToLive.Combat;
using Unity.NetCode;

namespace ToLive.UI
{
    /// <summary>Sends debug commands (reset / destroy building) to the server as an RPC.</summary>
    public static class DebugCommands
    {
        public static bool Send(byte command) => Send(command, 0);

        public static bool Send(byte command, byte arg)
        {
            var client = ClientServerBootstrap.ClientWorld;
            if (client == null || !client.IsCreated) return false;
            var em = client.EntityManager;
            var e = em.CreateEntity(typeof(DebugCommandRequest), typeof(SendRpcCommandRequest));
            em.SetComponentData(e, new DebugCommandRequest { Command = command, Arg = arg });
            return true;
        }
    }
}
