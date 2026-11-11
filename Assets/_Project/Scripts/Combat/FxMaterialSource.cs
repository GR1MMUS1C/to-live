using UnityEngine;

namespace ToLive.Combat
{
    /// <summary>Scene object holding material assets for runtime effects (see <see cref="FxMaterials"/>).</summary>
    public class FxMaterialSource : MonoBehaviour
    {
        public Material Unlit;
        public Material Lit;
        [Tooltip("Additive particle material (glass blowout).")] public Material Particles;

        public static FxMaterialSource Instance { get; private set; }
        void OnEnable() => Instance = this;
    }
}
