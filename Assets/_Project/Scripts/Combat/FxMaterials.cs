using UnityEngine;

namespace ToLive.Combat
{
    /// <summary>
    /// Materials for runtime-created effects. In a player build, shaders that no asset references
    /// are stripped, so <c>Shader.Find</c> can return null there. The setup tool creates material
    /// assets and the "FX Materials" scene object (<see cref="FxMaterialSource"/>) references
    /// them, which keeps their shaders in the build.
    /// </summary>
    public static class FxMaterials
    {
        public static Material Unlit() => Make(FxMaterialSource.Instance != null ? FxMaterialSource.Instance.Unlit : null, "Universal Render Pipeline/Unlit");
        public static Material Lit() => Make(FxMaterialSource.Instance != null ? FxMaterialSource.Instance.Lit : null, "Universal Render Pipeline/Lit");
        public static Material Particles() => Make(FxMaterialSource.Instance != null ? FxMaterialSource.Instance.Particles : null, "Universal Render Pipeline/Particles/Unlit");

        static Material Make(Material template, string fallbackShader)
        {
            if (template != null) return new Material(template);
            var shader = Shader.Find(fallbackShader);
            return shader != null ? new Material(shader) : new Material(Shader.Find("Hidden/InternalErrorShader"));
        }
    }
}
