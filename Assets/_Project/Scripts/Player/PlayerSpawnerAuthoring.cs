using Unity.Entities;
using UnityEngine;

namespace ToLive.Player
{
    /// <summary>Lives in the gameplay subscene. Spawn points are this object's children.</summary>
    [DisallowMultipleComponent]
    public class PlayerSpawnerAuthoring : MonoBehaviour
    {
        public GameObject PlayerPrefab;

        class Baker : Baker<PlayerSpawnerAuthoring>
        {
            public override void Bake(PlayerSpawnerAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);
                AddComponent(entity, new PlayerSpawner { PlayerPrefab = GetEntity(authoring.PlayerPrefab, TransformUsageFlags.Dynamic) });
                var points = AddBuffer<PlayerSpawnPoint>(entity);
                foreach (Transform child in authoring.transform)
                {
                    DependsOn(child);
                    points.Add(new PlayerSpawnPoint { Position = child.position });
                }
            }
        }
    }
}
