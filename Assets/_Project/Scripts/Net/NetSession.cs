using System.Collections.Generic;
using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;
using Unity.Scenes;
using UnityEngine;

namespace ToLive.Net
{
    /// <summary>
    /// Creates and tears down the client/server ECS worlds. In Unreal terms: "Host" is a listen
    /// server (server + local client in one process), "Join" is a client connecting by IP.
    /// Unlike Unreal, Netcode for Entities runs the server and the client in two separate ECS
    /// Worlds inside the same process, each with its own copy of the game state.
    /// </summary>
    public static class NetSession
    {
        public const ushort DefaultPort = 7979;

        public static bool IsActive => ClientServerBootstrap.ClientWorld != null || ClientServerBootstrap.ServerWorld != null;

        /// <summary>Server + client in this process. Others can join on <paramref name="port"/>.</summary>
        public static void Host(ushort port, SubScene gameplayScene)
        {
            Leave();
            var server = ClientServerBootstrap.CreateServerWorld("ServerWorld");
            var client = ClientServerBootstrap.CreateClientWorld("ClientWorld");
            LoadGameplay(server, gameplayScene);
            LoadGameplay(client, gameplayScene);

            // Listen on every network interface so LAN players can join.
            var listen = server.EntityManager.CreateEntity(typeof(NetworkStreamRequestListen));
            server.EntityManager.SetComponentData(listen, new NetworkStreamRequestListen { Endpoint = NetworkEndpoint.AnyIpv4.WithPort(port) });

            var connect = client.EntityManager.CreateEntity(typeof(NetworkStreamRequestConnect));
            client.EntityManager.SetComponentData(connect, new NetworkStreamRequestConnect { Endpoint = NetworkEndpoint.LoopbackIpv4.WithPort(port) });
            Debug.Log($"[ToLive] Hosting on port {port}");
        }

        /// <summary>Client only, connecting to a host by IP address.</summary>
        public static bool Join(string address, ushort port, SubScene gameplayScene, out string error)
        {
            error = null;
            if (!NetworkEndpoint.TryParse(address.Trim(), port, out var endpoint))
            {
                error = $"'{address}' is not a valid IP address";
                return false;
            }

            Leave();
            var client = ClientServerBootstrap.CreateClientWorld("ClientWorld");
            LoadGameplay(client, gameplayScene);
            var connect = client.EntityManager.CreateEntity(typeof(NetworkStreamRequestConnect));
            client.EntityManager.SetComponentData(connect, new NetworkStreamRequestConnect { Endpoint = endpoint });
            Debug.Log($"[ToLive] Joining {endpoint}");
            return true;
        }

        /// <summary>Disconnects and destroys all networked worlds (back to the menu).</summary>
        public static void Leave()
        {
            var worlds = new List<World>();
            worlds.AddRange(ClientServerBootstrap.ClientWorlds);
            worlds.AddRange(ClientServerBootstrap.ServerWorlds);
            foreach (var w in worlds)
                if (w.IsCreated)
                    w.Dispose();
        }

        // A SubScene is like a level streamed in as baked ECS data. It must be loaded into every
        // world that needs it (server for simulation + collisions, client for rendering + prediction).
        static void LoadGameplay(World world, SubScene scene)
        {
            if (scene == null)
            {
                Debug.LogError("[ToLive] No gameplay SubScene assigned. Run Tools > To Live > Setup Project.");
                return;
            }
            SceneSystem.LoadSceneAsync(world.Unmanaged, scene.SceneGUID);
        }
    }
}
