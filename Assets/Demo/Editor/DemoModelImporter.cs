using UnityEditor;
using UnityEngine;

namespace MeshFractureDemo.EditorTools
{
    /// <summary>
    /// AssetPostprocessor that fixes import settings on every FBX dropped into
    /// <c>Assets/Demo/Resources/Models</c>. The fracturer needs read/write on
    /// the mesh data (otherwise <c>mesh.vertices</c> throws), and the demo
    /// pivots stay clean if we strip the auto-generated colliders.
    ///
    /// Without this, the kenney FBXes import with Read/Write off — every
    /// fracture call would error at <c>MeshFragmenter.Fragment</c> trying to
    /// read <c>source.vertices</c>. Surface-level "obvious" but easy to miss
    /// when the runtime works in editor but breaks in builds.
    /// </summary>
    public class DemoModelImporter : AssetPostprocessor
    {
        const string SCOPED_PATH = "/Demo/Resources/Models/";

        void OnPreprocessModel()
        {
            if (!assetPath.Replace('\\', '/').Contains(SCOPED_PATH)) return;
            var importer = (ModelImporter)assetImporter;

            // Read/Write required for runtime mesh.vertices access. Costs
            // memory (mesh stays in CPU RAM after upload) but the demo only
            // ever loads ~7 small kenney models so the budget is fine.
            importer.isReadable = true;

            // Skip the box-collider-per-FBX that Unity generates by default —
            // DemoBootstrap attaches one BoxCollider sized to the visual
            // bounds for raycast click detection.
            importer.addCollider = false;

            // Materials embedded in the .fbx are ignored at runtime — we apply
            // KenneyColormapMaterial programmatically. Setting the import to
            // "no materials" stops Unity creating placeholder .mat assets in
            // the same folder (they'd just sit there orphaned).
            importer.materialImportMode = ModelImporterMaterialImportMode.None;

            // characterMedium.fbx is rigged; preserve the skinning hierarchy
            // and bind pose so SkinnedMeshRenderer.BakeMesh produces a usable
            // pose snapshot. Generic rig is fine — we don't use the kenney
            // animations, just the skinned mesh.
            importer.animationType = ModelImporterAnimationType.Generic;
        }

        void OnPreprocessTexture()
        {
            if (!assetPath.Replace('\\', '/').Contains(SCOPED_PATH)) return;
            var importer = (TextureImporter)assetImporter;

            // colormap.png is a 256x256 palette atlas — no mipmaps means crisp
            // texel boundaries between palette cells. Bilinear filter on a
            // small atlas reads exactly the same as point at the demo's
            // viewing distance and avoids "8-bit" jaggies.
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.maxTextureSize = 512;
        }
    }
}
