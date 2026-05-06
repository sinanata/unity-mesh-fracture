using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace MeshFractureDemo.EditorTools
{
    /// <summary>
    /// Bootstraps a URP <see cref="UniversalRenderPipelineAsset"/> + renderer
    /// pair on first project open if one isn't already wired in. Skips on
    /// subsequent loads.
    ///
    /// Why this is editor-only auto-create rather than committed assets: the
    /// URP RPAsset's serialization carries a non-trivial number of internal
    /// fields URP populates from <c>OnEnable</c>; hand-authored YAML would
    /// drift on every URP package upgrade. Letting Unity instantiate the
    /// ScriptableObject and then injecting the renderer reference via
    /// SerializedObject keeps the bootstrap aligned with whichever URP the
    /// project happens to be on (we ship 17.3.0 today, but a contributor
    /// upgrading the package shouldn't have to re-author YAML).
    ///
    /// Result: <c>git clone &amp;&amp; open in Unity</c> produces a working URP
    /// project on the first launch, no manual menu commands required.
    /// </summary>
    public static class EnsureUrpAssets
    {
        const string SETTINGS_DIR  = "Assets/Settings";
        const string URP_PATH      = "Assets/Settings/URPRenderPipelineAsset.asset";
        const string RENDERER_PATH = "Assets/Settings/URPRenderPipelineAsset_Renderer.asset";

        [InitializeOnLoadMethod]
        static void Init()
        {
            // Defer: at static-ctor time the AssetDatabase isn't fully alive
            // and modifying GraphicsSettings can deadlock on first import.
            EditorApplication.delayCall += Run;
        }

        static void Run()
        {
            if (GraphicsSettings.defaultRenderPipeline is UniversalRenderPipelineAsset) return;

            if (!Directory.Exists(SETTINGS_DIR))
                Directory.CreateDirectory(SETTINGS_DIR);

            var rendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RENDERER_PATH);
            if (rendererData == null)
            {
                rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
                AssetDatabase.CreateAsset(rendererData, RENDERER_PATH);
            }

            var urpAsset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(URP_PATH);
            if (urpAsset == null)
            {
                urpAsset = ScriptableObject.CreateInstance<UniversalRenderPipelineAsset>();
                AttachRenderer(urpAsset, rendererData);
                AssetDatabase.CreateAsset(urpAsset, URP_PATH);
            }
            else
            {
                AttachRenderer(urpAsset, rendererData);
                EditorUtility.SetDirty(urpAsset);
            }

            AssetDatabase.SaveAssets();

            // Assign across all quality levels so WebGL (Mobile-default) and
            // Standalone (PC) both pick up the same pipeline. Iterating
            // QualitySettings is the only public API that lets you write to
            // a level other than the current one.
            int currentLevel = QualitySettings.GetQualityLevel();
            int levelCount   = QualitySettings.names.Length;
            for (int i = 0; i < levelCount; i++)
            {
                QualitySettings.SetQualityLevel(i, applyExpensiveChanges: false);
                QualitySettings.renderPipeline = urpAsset;
            }
            QualitySettings.SetQualityLevel(currentLevel, applyExpensiveChanges: false);

            GraphicsSettings.defaultRenderPipeline = urpAsset;

            Debug.Log($"[MeshFractureDemo] Bootstrapped URP RenderPipelineAsset at {URP_PATH} — pipeline is now active.");
        }

        // SerializedObject lets us write the renderer reference into the URP
        // asset's internal m_RendererDataList without depending on URP's
        // internal API surface (which has changed shape between 14.x and
        // 17.x). The list is the canonical source URP queries to resolve
        // GetDefaultRenderer().
        static void AttachRenderer(UniversalRenderPipelineAsset urpAsset, UniversalRendererData rendererData)
        {
            var so = new SerializedObject(urpAsset);
            var listProp = so.FindProperty("m_RendererDataList");
            if (listProp == null) return;
            if (listProp.arraySize < 1) listProp.arraySize = 1;
            var elem = listProp.GetArrayElementAtIndex(0);
            if (elem.objectReferenceValue != rendererData)
                elem.objectReferenceValue = rendererData;
            var idxProp = so.FindProperty("m_DefaultRendererIndex");
            if (idxProp != null) idxProp.intValue = 0;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
