using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace MeshFractureDemo.EditorTools
{
    /// <summary>
    /// Ensures the URP shaders that <see cref="MeshFractureDemo.MaterialFactory"/>
    /// resolves at runtime via <c>Shader.Find</c> are present in
    /// GraphicsSettings.AlwaysIncludedShaders, so a player build (especially
    /// WebGL with aggressive shader stripping) keeps them.
    ///
    /// The demo deliberately ships zero <c>.mat</c> assets — every material
    /// is constructed in C# at runtime. Without anything in the build's
    /// scenes/assets pulling URP/Lit in, Unity strips the shader, and the
    /// runtime <c>Shader.Find("Universal Render Pipeline/Lit")</c> returns
    /// null → fallback to <c>Shader.Find("Standard")</c> (also stripped) →
    /// <c>new Material(null)</c> → every primitive renders magenta. Adding
    /// URP/Lit / URP/Simple Lit / URP/Unlit to AlwaysIncludedShaders is the
    /// canonical fix for "Shader.Find returns null in build" — it adds the
    /// shaders to the build's shader registry without forcing the demo to
    /// commit .mat assets it doesn't otherwise need.
    ///
    /// Runs at editor load (<c>InitializeOnLoadMethod</c>) AND immediately
    /// before every build (<c>IPreprocessBuildWithReport</c>). The latter
    /// is belt-and-suspenders for batchmode: <c>delayCall</c> can race the
    /// build trigger when Unity is invoked with <c>-executeMethod</c>, so
    /// the preprocessor guarantees the shaders are present before any
    /// shader-stripping pass runs. Idempotent: subsequent passes detect the
    /// shaders are already present and no-op.
    /// </summary>
    public class EnsureBuildShaders : IPreprocessBuildWithReport
    {
        const string GRAPHICS_SETTINGS_PATH = "ProjectSettings/GraphicsSettings.asset";

        // Shader names matched against Shader.Find. The strings are the
        // shader's declared name (the `Shader "..." { ... }` header), not
        // the asset path.
        static readonly string[] RequiredShaderNames =
        {
            "Universal Render Pipeline/Lit",
            "Universal Render Pipeline/Simple Lit",
            "Universal Render Pipeline/Unlit",
        };

        // Run before any other preprocessor — shader stripping happens
        // inside Unity's build pipeline AFTER preprocessors, so callbackOrder
        // 0 is fine, but staying low ensures we don't sit behind any user
        // hook that might inspect the shader list.
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report) => Run();

        [InitializeOnLoadMethod]
        static void Init()
        {
            // Defer past the static-ctor stage. AssetDatabase isn't fully
            // alive at that point, and modifying GraphicsSettings can
            // deadlock during first import — same constraint as
            // EnsureUrpAssets.cs.
            EditorApplication.delayCall += Run;
        }

        static void Run()
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath(GRAPHICS_SETTINGS_PATH);
            if (assets == null || assets.Length == 0)
            {
                Debug.LogWarning($"[MeshFractureDemo] Could not load {GRAPHICS_SETTINGS_PATH} — skipping AlwaysIncludedShaders patch.");
                return;
            }

            var so = new SerializedObject(assets[0]);
            var arr = so.FindProperty("m_AlwaysIncludedShaders");
            if (arr == null || !arr.isArray)
            {
                Debug.LogWarning("[MeshFractureDemo] m_AlwaysIncludedShaders property not found on GraphicsSettings — Unity layout may have changed.");
                return;
            }

            // Build a set of currently-tracked shader references so we can
            // dedup. SerializedProperty equality on objectReferenceValue is
            // by-instance, which is what we want.
            var present = new HashSet<Object>();
            for (int i = 0; i < arr.arraySize; i++)
            {
                var elem = arr.GetArrayElementAtIndex(i);
                if (elem.objectReferenceValue != null)
                    present.Add(elem.objectReferenceValue);
            }

            var added = new List<string>();
            foreach (var name in RequiredShaderNames)
            {
                var shader = Shader.Find(name);
                if (shader == null)
                {
                    // URP not installed, or our custom shader hasn't compiled
                    // yet on a fresh import. Don't fail — just skip.
                    continue;
                }
                if (present.Contains(shader)) continue;

                int newIdx = arr.arraySize;
                arr.arraySize = newIdx + 1;
                arr.GetArrayElementAtIndex(newIdx).objectReferenceValue = shader;
                added.Add(name);
            }

            if (added.Count > 0)
            {
                so.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.SaveAssets();
                Debug.Log($"[MeshFractureDemo] Added {added.Count} shader(s) to GraphicsSettings.AlwaysIncludedShaders so player builds keep them: {string.Join(", ", added)}");
            }
        }
    }
}
