using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

// Mixamo FBX materials import without their textures, so rebuild them as URP materials from
// the extracted maps: <Folder>/<Part>_Diffuse/_Normal/_Opacity (Remy style) or
// ChNN_1001_* for the body and ChNN_1002_* for hair (most other Mixamo characters).
public static class CharacterMaterials
{
    const string OutDir = "Assets/Generated/Characters";
    static readonly Dictionary<string, Material> cache = new Dictionary<string, Material>();

    public static void Apply(GameObject instance, string fbxPath)
    {
        Directory.CreateDirectory(OutDir);
        string texDir = "Assets/Characters/Textures/" + Path.GetFileNameWithoutExtension(fbxPath);
        if (!Directory.Exists(texDir)) return;
        var files = Directory.GetFiles(texDir, "*.png").Select(p => p.Replace('\\', '/')).ToArray();

        foreach (var r in instance.GetComponentsInChildren<Renderer>())
        {
            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
                if (mats[i]) mats[i] = Build(texDir, files, mats[i].name);
            r.sharedMaterials = mats;
        }
    }

    static Material Build(string texDir, string[] files, string matName)
    {
        string key = texDir + "|" + matName;
        if (cache.TryGetValue(key, out var existing) && existing) return existing;

        string part = matName.ToLower().Replace("mat", "");
        if (part.Contains("_")) part = part.Substring(part.LastIndexOf('_') + 1);   // ch21_body -> body
        bool hairLike = part.Contains("hair") || part.Contains("eyelash") || part.Contains("lash");

        string diffuse = Find(files, "_" + part + "_", "diffuse")
            ?? (hairLike ? Find(files, "_hair_", "diffuse") ?? Find(files, "_1002_", "diffuse") : Find(files, "_1001_", "diffuse"))
            ?? Find(files, "", "diffuse");
        string normal = diffuse == null ? null : files.FirstOrDefault(f => f == diffuse.Replace("_Diffuse", "_Normal"));
        string opacity = diffuse == null ? null : files.FirstOrDefault(f => f == diffuse.Replace("_Diffuse", "_Opacity"));

        var mat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = matName };
        mat.SetFloat("_Smoothness", hairLike ? 0.35f : 0.25f);
        if (diffuse != null)
        {
            var baseMap = opacity != null && hairLike ? MergeAlpha(diffuse, opacity) : AssetDatabase.LoadAssetAtPath<Texture2D>(diffuse);
            mat.SetTexture("_BaseMap", baseMap);
            bool hasAlpha = opacity != null || ((TextureImporter)AssetImporter.GetAtPath(diffuse)).DoesSourceTextureHaveAlpha();
            if (hairLike && hasAlpha)
            {
                mat.SetFloat("_AlphaClip", 1f);
                mat.SetFloat("_Cutoff", 0.4f);
                mat.EnableKeyword("_ALPHATEST_ON");
                mat.SetFloat("_Cull", 0f);   // hair cards are single-sided
            }
        }
        if (normal != null)
        {
            var ti = (TextureImporter)AssetImporter.GetAtPath(normal);
            if (ti.textureType != TextureImporterType.NormalMap)
            {
                ti.textureType = TextureImporterType.NormalMap;
                ti.SaveAndReimport();
            }
            mat.SetTexture("_BumpMap", AssetDatabase.LoadAssetAtPath<Texture2D>(normal));
            mat.EnableKeyword("_NORMALMAP");
        }

        string path = $"{OutDir}/{Path.GetFileName(texDir)}_{matName}.mat";
        AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(mat, path);
        cache[key] = mat;
        return mat;
    }

    static string Find(string[] files, string part, string kind) =>
        files.FirstOrDefault(f => Path.GetFileName(f).ToLower().Contains(part) && Path.GetFileName(f).ToLower().Contains(kind));

    // Combines a colour map with a separate opacity map into one RGBA texture for alpha clipping.
    static Texture2D MergeAlpha(string diffusePath, string opacityPath)
    {
        string outPath = $"{OutDir}/{Path.GetFileNameWithoutExtension(diffusePath)}_RGBA.png";
        if (!File.Exists(outPath))
        {
            var c = new Texture2D(2, 2); c.LoadImage(File.ReadAllBytes(diffusePath));
            var a = new Texture2D(2, 2); a.LoadImage(File.ReadAllBytes(opacityPath));
            var px = c.GetPixels32();
            var ax = a.width == c.width && a.height == c.height ? a.GetPixels32() : null;
            for (int i = 0; i < px.Length; i++) px[i].a = ax != null ? ax[i].r : (byte)255;
            var outTex = new Texture2D(c.width, c.height, TextureFormat.RGBA32, false);
            outTex.SetPixels32(px);
            File.WriteAllBytes(outPath, outTex.EncodeToPNG());
            AssetDatabase.ImportAsset(outPath);
            var ti = (TextureImporter)AssetImporter.GetAtPath(outPath);
            ti.alphaIsTransparency = true;
            ti.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(outPath);
    }
}
