using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class AutoPropMaterialSetup
{
    private const string PropsRoot = "Assets/Props";

    [MenuItem("Tools/Props/Auto Setup All Prop Materials")]
    public static void AutoSetupAll()
    {
        if (!AssetDatabase.IsValidFolder(PropsRoot))
        {
            EditorUtility.DisplayDialog(
                "Auto Prop Materials",
                $"Could not find {PropsRoot}\n\nCreate that folder or change PropsRoot in the script.",
                "OK");
            return;
        }

        string[] modelGuids = AssetDatabase.FindAssets("t:Model", new[] { PropsRoot });
        int modelCount = 0;
        int materialCount = 0;
        int assignedRendererCount = 0;

        foreach (string guid in modelGuids)
        {
            string modelPath = AssetDatabase.GUIDToAssetPath(guid);

            // Skip generated or package-like content if any appears under Props.
            if (modelPath.IndexOf("/Materials/", StringComparison.OrdinalIgnoreCase) >= 0)
                continue;

            try
            {
                ProcessModel(modelPath, ref materialCount, ref assignedRendererCount);
                modelCount++;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AutoPropMaterials] Failed on {modelPath}\n{ex}");
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        EditorUtility.DisplayDialog(
            "Auto Prop Materials",
            $"Finished.\n\nModels processed: {modelCount}\nMaterials created/reused: {materialCount}\nRenderers updated: {assignedRendererCount}\n\nCheck the Console for warnings about packed MRA/ORM textures or unmatched materials.",
            "OK");
    }

    [MenuItem("Tools/Props/Auto Setup Selected Model")]
    public static void AutoSetupSelected()
    {
        UnityEngine.Object selected = Selection.activeObject;
        if (selected == null)
        {
            EditorUtility.DisplayDialog("Auto Prop Materials", "Select a model asset in the Project window first.", "OK");
            return;
        }

        string path = AssetDatabase.GetAssetPath(selected);
        if (string.IsNullOrEmpty(path))
        {
            EditorUtility.DisplayDialog("Auto Prop Materials", "Could not resolve the selected asset path.", "OK");
            return;
        }

        int materials = 0;
        int renderers = 0;

        ProcessModel(path, ref materials, ref renderers);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        EditorUtility.DisplayDialog(
            "Auto Prop Materials",
            $"Done.\n\nMaterials created/reused: {materials}\nRenderers updated: {renderers}",
            "OK");
    }

    private static void ProcessModel(string modelPath, ref int materialCount, ref int assignedRendererCount)
    {
        GameObject modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
        if (modelAsset == null)
            return;

        string modelDirectory = Normalize(Path.GetDirectoryName(modelPath));
        string propRoot = FindPropRoot(modelDirectory);

        string materialFolder = $"{propRoot}/Materials";
        EnsureFolder(materialFolder);

        List<Texture2D> textures = FindNearbyTextures(propRoot);

        GameObject instance = PrefabUtility.InstantiatePrefab(modelAsset) as GameObject;
        if (instance == null)
            instance = UnityEngine.Object.Instantiate(modelAsset);

        try
        {
            Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(true);

            foreach (Renderer renderer in renderers)
            {
                Material[] existing = renderer.sharedMaterials;
                if (existing == null || existing.Length == 0)
                    continue;

                Material[] replacements = new Material[existing.Length];

                for (int i = 0; i < existing.Length; i++)
                {
                    string sourceMaterialName =
                        existing[i] != null && !string.IsNullOrWhiteSpace(existing[i].name)
                            ? CleanName(existing[i].name)
                            : $"{CleanName(renderer.name)}_{i}";

                    Material mat = GetOrCreateMaterial(
                        materialFolder,
                        sourceMaterialName,
                        textures,
                        ref materialCount);

                    replacements[i] = mat;
                }

                renderer.sharedMaterials = replacements;
                assignedRendererCount++;
            }

            // Apply generated materials back onto model importer through external object remapping where possible.
            ApplyMaterialRemapsToImporter(modelPath, renderers, materialFolder);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(instance);
        }
    }

    private static Material GetOrCreateMaterial(
        string materialFolder,
        string materialName,
        List<Texture2D> allTextures,
        ref int materialCount)
    {
        string safeName = MakeSafeFilename(materialName);
        string materialPath = $"{materialFolder}/{safeName}.mat";

        Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);

        if (material == null)
        {
            Shader shader = FindBestLitShader();

            if (shader == null)
                throw new Exception("Could not find URP/Lit or Standard shader.");

            material = new Material(shader)
            {
                name = safeName
            };

            AssetDatabase.CreateAsset(material, materialPath);
        }

        List<Texture2D> candidates = RankTexturesForMaterial(allTextures, materialName);

        Texture2D baseColor = FindTexture(candidates,
            "basecolor", "base_color", "albedo", "diffuse", "_bc", "color");

        Texture2D normal = FindTexture(candidates,
            "normal", "_n", "norm");

        Texture2D metallic = FindTexture(candidates,
            "metallic", "metalness", "_metal", "_m");

        Texture2D emission = FindTexture(candidates,
            "emissive", "emission", "_e", "emit");

        Texture2D ao = FindTexture(candidates,
            "ambientocclusion", "ambient_occlusion", "occlusion", "_ao", " ao");

        Texture2D roughness = FindTexture(candidates,
            "roughness", "_rough", "rough");

        Texture2D packed = FindTexture(candidates,
            "_mra", "mra", "_orm", "orm", "occlusionroughnessmetallic", "metallicroughness");

        ConfigureMaterial(material, baseColor, normal, metallic, emission, ao);

        if (roughness != null)
        {
            Debug.LogWarning(
                $"[AutoPropMaterials] {material.name}: found roughness map '{roughness.name}'. " +
                "URP uses smoothness, so this script leaves it unassigned rather than applying it backwards.");
        }

        if (packed != null)
        {
            Debug.LogWarning(
                $"[AutoPropMaterials] {material.name}: found packed map '{packed.name}' (MRA/ORM-style). " +
                "It was not auto-assigned because channel packing differs between assets.");
        }

        EditorUtility.SetDirty(material);
        materialCount++;
        return material;
    }

    private static void ConfigureMaterial(
        Material mat,
        Texture2D baseColor,
        Texture2D normal,
        Texture2D metallic,
        Texture2D emission,
        Texture2D ao)
    {
        bool urp = mat.shader != null &&
                   mat.shader.name.IndexOf("Universal Render Pipeline", StringComparison.OrdinalIgnoreCase) >= 0;

        if (baseColor != null)
        {
            if (urp && mat.HasProperty("_BaseMap"))
                mat.SetTexture("_BaseMap", baseColor);
            else if (mat.HasProperty("_MainTex"))
                mat.SetTexture("_MainTex", baseColor);
        }

        if (normal != null)
        {
            MarkAsNormalMap(normal);

            if (mat.HasProperty("_BumpMap"))
            {
                mat.EnableKeyword("_NORMALMAP");
                mat.SetTexture("_BumpMap", normal);
            }
        }

        if (metallic != null && mat.HasProperty("_MetallicGlossMap"))
        {
            mat.EnableKeyword("_METALLICSPECGLOSSMAP");
            mat.SetTexture("_MetallicGlossMap", metallic);

            if (mat.HasProperty("_Metallic"))
                mat.SetFloat("_Metallic", 1f);
        }

        if (ao != null && mat.HasProperty("_OcclusionMap"))
        {
            mat.SetTexture("_OcclusionMap", ao);

            if (mat.HasProperty("_OcclusionStrength"))
                mat.SetFloat("_OcclusionStrength", 1f);
        }

        if (emission != null)
        {
            mat.EnableKeyword("_EMISSION");

            if (mat.HasProperty("_EmissionMap"))
                mat.SetTexture("_EmissionMap", emission);

            if (mat.HasProperty("_EmissionColor"))
                mat.SetColor("_EmissionColor", Color.white);

            mat.globalIlluminationFlags &= ~MaterialGlobalIlluminationFlags.EmissiveIsBlack;
        }
    }

    private static void ApplyMaterialRemapsToImporter(
        string modelPath,
        Renderer[] instanceRenderers,
        string materialFolder)
    {
        ModelImporter importer = AssetImporter.GetAtPath(modelPath) as ModelImporter;
        if (importer == null)
            return;

        var mappings = new Dictionary<AssetImporter.SourceAssetIdentifier, UnityEngine.Object>();

        foreach (Renderer renderer in instanceRenderers)
        {
            foreach (Material sourceMat in renderer.sharedMaterials)
            {
                if (sourceMat == null)
                    continue;

                string materialName = MakeSafeFilename(CleanName(sourceMat.name));
                Material generated = AssetDatabase.LoadAssetAtPath<Material>(
                    $"{materialFolder}/{materialName}.mat");

                if (generated == null)
                    continue;

                var id = new AssetImporter.SourceAssetIdentifier(typeof(Material), sourceMat.name);
                mappings[id] = generated;
            }
        }

        if (mappings.Count == 0)
            return;

        foreach (var kvp in mappings)
            importer.AddRemap(kvp.Key, kvp.Value);

        importer.SaveAndReimport();
    }

    private static List<Texture2D> FindNearbyTextures(string propRoot)
    {
        string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { propRoot });

        return guids
            .Select(AssetDatabase.GUIDToAssetPath)
            .Select(p => AssetDatabase.LoadAssetAtPath<Texture2D>(p))
            .Where(t => t != null)
            .ToList();
    }

    private static List<Texture2D> RankTexturesForMaterial(List<Texture2D> textures, string materialName)
    {
        string cleanMaterial = NormalizeToken(materialName);

        return textures
            .OrderByDescending(t =>
            {
                string token = NormalizeToken(t.name);
                if (string.IsNullOrEmpty(cleanMaterial))
                    return 0;

                if (token.Contains(cleanMaterial))
                    return 100;

                string[] parts = cleanMaterial
                    .Split(new[] { '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries);

                return parts.Count(p => p.Length >= 3 && token.Contains(p)) * 10;
            })
            .ToList();
    }

    private static Texture2D FindTexture(IEnumerable<Texture2D> textures, params string[] tokens)
    {
        foreach (Texture2D tex in textures)
        {
            string raw = tex.name.ToLowerInvariant();
            string normalized = NormalizeToken(tex.name);

            foreach (string token in tokens)
            {
                string t = token.ToLowerInvariant().Trim();

                if (t.StartsWith("_") || t.StartsWith(" "))
                {
                    if (raw.Contains(t))
                        return tex;
                }
                else
                {
                    string nt = NormalizeToken(t);
                    if (normalized.Contains(nt))
                        return tex;
                }
            }
        }

        return null;
    }

    private static void MarkAsNormalMap(Texture2D texture)
    {
        string path = AssetDatabase.GetAssetPath(texture);
        TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;

        if (importer == null || importer.textureType == TextureImporterType.NormalMap)
            return;

        importer.textureType = TextureImporterType.NormalMap;
        importer.SaveAndReimport();
    }

    private static Shader FindBestLitShader()
    {
        Shader urp = Shader.Find("Universal Render Pipeline/Lit");
        if (urp != null)
            return urp;

        return Shader.Find("Standard");
    }

    private static string FindPropRoot(string modelDirectory)
    {
        string normalized = Normalize(modelDirectory);
        string propsPrefix = PropsRoot + "/";

        if (!normalized.StartsWith(propsPrefix, StringComparison.OrdinalIgnoreCase))
            return normalized;

        string remainder = normalized.Substring(propsPrefix.Length);
        string first = remainder.Split('/')[0];

        if (string.IsNullOrWhiteSpace(first))
            return PropsRoot;

        return $"{PropsRoot}/{first}";
    }

    private static void EnsureFolder(string folderPath)
    {
        string normalized = Normalize(folderPath);
        string[] parts = normalized.Split('/');

        string current = parts[0];

        for (int i = 1; i < parts.Length; i++)
        {
            string next = $"{current}/{parts[i]}";

            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(current, parts[i]);

            current = next;
        }
    }

    private static string CleanName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Material";

        string cleaned = name;

        // Common Unity/import suffixes.
        cleaned = cleaned.Replace(" (Instance)", "");
        cleaned = cleaned.Replace(".001", "");
        cleaned = cleaned.Replace(".002", "");

        return cleaned.Trim();
    }

    private static string MakeSafeFilename(string value)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '_');

        return string.IsNullOrWhiteSpace(value) ? "Material" : value.Trim();
    }

    private static string NormalizeToken(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        char[] chars = value
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray();

        return new string(chars);
    }

    private static string Normalize(string path)
    {
        return path?.Replace("\\", "/");
    }
}
