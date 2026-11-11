using Unity.Entities;
using UnityEngine;

namespace ToLive.Player
{
    /// <summary>Lives in the gameplay subscene. Spawn points are this object's children.</summary>
    [DisallowMultipleComponent]
    public class PlayerSpawnerAuthoring : MonoBehaviour
    {
        public GameObject PlayerPrefab;

        [Header("Training dummy")]
        public GameObject DummyPrefab;
        [Tooltip("Where the server spawns (and resets) the training dummy.")]
        public Transform DummyHome;

        class Baker : Baker<PlayerSpawnerAuthoring>
        {
            public override void Bake(PlayerSpawnerAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);
                var spawner = new PlayerSpawner { PlayerPrefab = GetEntity(authoring.PlayerPrefab, TransformUsageFlags.Dynamic) };
                if (authoring.DummyPrefab != null) spawner.DummyPrefab = GetEntity(authoring.DummyPrefab, TransformUsageFlags.Dynamic);
                if (authoring.DummyHome != null)
                {
                    DependsOn(authoring.DummyHome);
                    spawner.DummyHome = authoring.DummyHome.position;
                    spawner.DummyHomeYaw = authoring.DummyHome.eulerAngles.y;
                }
                AddComponent(entity, spawner);
                var points = AddBuffer<PlayerSpawnPoint>(entity);
                foreach (Transform child in authoring.transform)
                {
                    if (child == authoring.DummyHome) continue;
                    DependsOn(child);
                    points.Add(new PlayerSpawnPoint { Position = child.position });
                }
            }
        }
    }
}
