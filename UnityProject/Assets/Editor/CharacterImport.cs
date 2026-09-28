using System.IO;
using UnityEditor;

// Rigged characters dropped into Assets/Characters (e.g. Mixamo FBX) import as humanoids,
// so one walk clip retargets onto every character; clips loop and root motion is baked in place.
public class CharacterImport : AssetPostprocessor
{
    const string Folder = "Assets/Characters/";

    void OnPreprocessModel()
    {
        if (!assetPath.StartsWith(Folder)) return;
        var importer = (ModelImporter)assetImporter;
        importer.animationType = ModelImporterAnimationType.Human;
        importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
    }

    void OnPreprocessAnimation()
    {
        if (!assetPath.StartsWith(Folder)) return;
        var importer = (ModelImporter)assetImporter;
        var clips = importer.defaultClipAnimations;
        foreach (var clip in clips)
        {
            clip.loopTime = true;
            clip.lockRootRotation = true;
            clip.lockRootHeightY = true;
            clip.lockRootPositionXZ = true;
            clip.keepOriginalOrientation = true;
            clip.keepOriginalPositionY = true;
            clip.keepOriginalPositionXZ = true;
        }
        importer.clipAnimations = clips;
    }

    // Mixamo embeds skin and clothing textures in the FBX; pull them out once so the
    // materials pick them up (otherwise the characters render plain white).
    public static void ExtractEmbeddedTextures()
    {
        if (!Directory.Exists(Folder)) return;
        foreach (var file in Directory.GetFiles(Folder, "*.fbx"))
        {
            string path = file.Replace('\\', '/');
            string texDir = Folder + "Textures/" + Path.GetFileNameWithoutExtension(path);
            if (Directory.Exists(texDir)) continue;
            Directory.CreateDirectory(texDir);
            var importer = (ModelImporter)AssetImporter.GetAtPath(path);
            importer.ExtractTextures(texDir);
            AssetDatabase.Refresh();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        }
    }
}
