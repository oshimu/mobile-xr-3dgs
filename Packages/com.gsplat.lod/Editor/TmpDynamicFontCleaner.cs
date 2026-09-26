// Dynamic TMP font assets accumulate glyphs/atlas data at runtime as new characters
// are requested. That data regenerates from the source ttf, so it never needs saving,
// and leaving it in the asset produces a git diff every time the editor plays.

using TMPro;
using UnityEditor;

namespace GsplatLod.Editor
{
    public sealed class TmpDynamicFontCleaner : AssetModificationProcessor
    {
        static string[] OnWillSaveAssets(string[] paths)
        {
            foreach (var path in paths)
            {
                if (!path.EndsWith(".asset"))
                    continue;
                var fontAsset = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);
                if (fontAsset != null && fontAsset.atlasPopulationMode == AtlasPopulationMode.Dynamic)
                {
                    fontAsset.ClearFontAssetData(true);
                }
            }

            return paths;
        }
    }
}
