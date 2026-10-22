using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDK3.Dynamics.PhysBone.Components;
using System.Text.RegularExpressions;
using Array = System.Array;
using System.IO;

#if UNITY_EDITOR
using System.Threading.Tasks;
using System.Reflection;
using UnityEngine.Rendering;
using UnityEditor;
using UnityEditor.Animations;
using d4rkpl4y3r.AvatarOptimizer;
using d4rkpl4y3r.AvatarOptimizer.Util;
using d4rkpl4y3r.AvatarOptimizer.Extensions;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;

using Math = System.Math;
using Type = System.Type;
using Path = System.IO.Path;
using Object = UnityEngine.Object;
using AnimationPath = System.ValueTuple<string, string, System.Type>;
using BlendableLayer = VRC.SDKBase.VRC_AnimatorLayerControl.BlendableLayer;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using CompilationPipeline = UnityEditor.Compilation.CompilationPipeline;
#endif

[HelpURL("https://github.com/GRIMREAPER35487/Synthos-FX-Optimizer/blob/main/README.md")]
[AddComponentMenu("Synthos FX Optimizer")]
public class SynthosFxOptimizer : MonoBehaviour, VRC.SDKBase.IEditorOnly
{
    #region Settings
    [System.Serializable]
    public class Settings
    {
        public bool ApplyOnUpload = true;
        public bool OptimizeFXLayer = true;
        public bool DeleteUnusedAnimatorParameters = true;
        public bool DisablePhysBonesWhenUnused = true;
        public bool MergeSameRatioBlendShapes = true;
        public bool MMDCompatibility = true;
        public bool DeleteUnusedComponents = true;
    }

    public Settings settings = new Settings();
    public bool DoAutoSettings = true;
    public bool ShowExcludedTransforms = false;
    public List<Transform> ExcludeTransforms = new();
    public List<bool> ExcludeTransformChildren = new();
    public bool ShowFXLayerMergeResults = true;
    private bool _ShowFXLayerMergeErrors = false;
    public bool ShowFXLayerMergeErrors { get { return _ShowFXLayerMergeErrors; } set { _ShowFXLayerMergeErrors = value; } }
    public bool ShowDebugInfo = false;
    public bool DebugShowMergeableBlendShapes = true;
    public bool DebugShowFaceTracking = true;
    public bool DebugShowPhysBoneDependencies = true;
    public bool DebugShowUnusedComponents = true;
    public bool DebugShowAlwaysDisabledGameObjects = true;
    public bool DebugShowGameObjectsWithToggle = true;
    #endregion

    public struct MaterialSlot
    {
        public Renderer renderer;
        public int index;
        public readonly Material material
        {
            get
            {
                if (renderer == null || index < 0)
                    return null;
                var materials = renderer.sharedMaterials;
                if (index >= materials.Length)
                    return null;
                return materials[index];
            }
        }
        public MaterialSlot(Renderer renderer, int index)
        {
            this.renderer = renderer;
            this.index = index;
        }
        public static MaterialSlot[] GetAllSlotsFrom(Renderer renderer)
        {
            var result = new MaterialSlot[renderer.sharedMaterials.Length];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = new MaterialSlot(renderer, i);
            }
            return result;
        }
#if UNITY_EDITOR
        public MeshTopology GetTopology()
        {
            var mesh = renderer.GetSharedMesh();
            if (mesh == null || mesh.subMeshCount == 0)
                return MeshTopology.Triangles;
            return mesh.GetTopology(Math.Min(index, mesh.subMeshCount - 1));
        }
#endif
        public override bool Equals(object obj)
        {
            if (obj is MaterialSlot other)
            {
                return renderer == other.renderer && index == other.index;
            }
            return false;
        }
        public override int GetHashCode()
        {
            return (renderer == null ? 0 : renderer.GetHashCode()) ^ index.GetHashCode();
        }
        public static bool operator ==(MaterialSlot a, MaterialSlot b) => a.Equals(b);
        public static bool operator !=(MaterialSlot a, MaterialSlot b) => !a.Equals(b);
    }

#if UNITY_EDITOR
    private bool isOptimizing = false;

    // playModeAvatarIndex > 0 means this is the n-th avatar optimized in the current play session.
    // Each of those gets its own TrashBin subfolder so later avatars don't delete the assets of earlier ones.
    public void Optimize(int playModeAvatarIndex = 0)
    {
        using var _ = new InvariantCultureScope();
        try
        {
            isOptimizing = true;
            Profiler.enabled = true;
            Profiler.Reset();
            DisplayProgressBar("Clear TrashBin Folder", 0.01f);
            ClearTrashBin(playModeAvatarIndex);
            Profiler.StartSection("ClearCaches()");
            optimizedSlotSwapMaterials.Clear();
            newAnimationPaths.Clear();
            keepTransforms.Clear();
            constantAnimatedValuesToAdd.Clear();
            ClearCaches();
            componentsDeletedCount = 0;
            var statsBefore = CountBuildSummaryStats();
            LogAvatarStats($"Avatar stats for '{GetAvatarDescriptor().name}' before optimization:");
            DisplayProgressBar("Destroying unused components", 0.2f);
            Profiler.StartNextSection("DestroyEditorOnlyGameObjects()");
            DestroyEditorOnlyGameObjects();
            Profiler.StartNextSection("DestroyUnusedComponents()");
            DestroyUnusedComponents();
            DisplayProgressBar("Removing duplicate materials", 0.05f);
            Profiler.StartNextSection("DeduplicateMaterials()");
            DeduplicateMaterials();
            physBonesToDisable = FindAllPhysBonesToDisable();
            Profiler.StartNextSection("CalculateUsedBlendShapePaths()");
            CalculateUsedBlendShapePaths();
            // Meshes are never merged; this only removes unused blend shapes, bakes constant ones
            // and merges same ratio blend shapes on each skinned mesh.
            Profiler.StartNextSection("CombineSkinnedMeshes()");
            DisplayProgressBar("Optimizing blend shapes", 0.2f);
            CombineSkinnedMeshes();
            Profiler.StartNextSection("MergeIdenticalMaterialSlots()");
            DisplayProgressBar("Merging identical material slots", 0.4f);
            MergeIdenticalMaterialSlots();
            Profiler.StartNextSection("FixAllAnimationPaths()");
            DisplayProgressBar("Fixing animation paths", 0.6f);
            FixAllAnimationPaths();
            DisplayProgressBar("Done", 1.0f);
            LogAvatarStats("Avatar stats after optimization:");
            SaveBuildSummary(statsBefore, CountBuildSummaryStats());
            Profiler.StartNextSection("DestroyImmediate(this)");
            var t = transform;
            DestroyImmediate(this);
            if (t.childCount == 0 && t.GetComponents<Component>().Length == 1)
                DestroyImmediate(t.gameObject);
            Profiler.EndSection();
            LogToFile(string.Join("\n  - ", Profiler.FormatTimeUsed()));
        }
        catch (System.Exception e)
        {
            LogToFile("An error occurred during optimization:\n" + e.ToString());
            throw;
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            ImportLogFile();
        }
    }

    public static bool HasCustomShaderSupport { get => EditorUserBuildSettings.activeBuildTarget == BuildTarget.StandaloneWindows64; }
    public bool ApplyOnUpload { get { return settings.ApplyOnUpload; } set { settings.ApplyOnUpload = value; } }

    public bool MMDCompatibility { get { return settings.MMDCompatibility; } set { settings.MMDCompatibility = value; } }
    public bool DeleteUnusedComponents { get { return settings.DeleteUnusedComponents; } set { settings.DeleteUnusedComponents = value; } }
    public bool OptimizeFXLayer { get { return settings.OptimizeFXLayer; } set { settings.OptimizeFXLayer = value; } }
    public bool DeleteUnusedAnimatorParameters { get { return settings.DeleteUnusedAnimatorParameters; } set { settings.DeleteUnusedAnimatorParameters = value; } }
    public bool DisablePhysBonesWhenUnused { get { return settings.DisablePhysBonesWhenUnused; } set { settings.DisablePhysBonesWhenUnused = value; } }
    public bool MergeSameRatioBlendShapes { get { return settings.MergeSameRatioBlendShapes; } set { settings.MergeSameRatioBlendShapes = value; } }

    public bool CanChangeSetting(string fieldName) => true;

    private static Dictionary<string, string> FieldDisplayName = new Dictionary<string, string>() {
        {nameof(ApplyOnUpload), "Apply on Upload"},
        {nameof(MMDCompatibility), "MMD Compatibility"},
        {nameof(DeleteUnusedComponents), "Delete Unused Components"},
        {nameof(OptimizeFXLayer), "Optimize FX Layer"},
        {nameof(DeleteUnusedAnimatorParameters), "Delete Unused Animator Parameters"},
        {nameof(DisablePhysBonesWhenUnused), "Disable Phys Bones When Unused"},
        {nameof(MergeSameRatioBlendShapes), "Merge Same Ratio Blend Shapes"},
        {nameof(ShowFXLayerMergeErrors), "Show FX Layer Merge Errors"},
    };

    public static string GetDisplayName(string fieldName)
    {
        if (FieldDisplayName.ContainsKey(fieldName))
        {
            return FieldDisplayName[fieldName];
        }
        return fieldName;
    }

    private static List<(string name, Dictionary<string, object>)> SettingsPresets = new List<(string name, Dictionary<string, object>)>()
    {
        ("Default", new Dictionary<string, object>() {
            {nameof(Settings.ApplyOnUpload), true},
            {nameof(Settings.OptimizeFXLayer), true},
            {nameof(Settings.DeleteUnusedAnimatorParameters), true},
            {nameof(Settings.DisablePhysBonesWhenUnused), true},
            {nameof(Settings.MergeSameRatioBlendShapes), true},
            {nameof(Settings.MMDCompatibility), true},
            {nameof(Settings.DeleteUnusedComponents), true},
        }),
    };

    public List<string> GetPresetNames()
    {
        return SettingsPresets.Select(x => x.name).ToList();
    }

    public bool IsPresetActive(string presetName)
    {
        var preset = SettingsPresets.Find(x => x.name == presetName).Item2;
        foreach (var entry in preset)
        {
            if (!typeof(Settings).GetField(entry.Key).GetValue(settings).Equals(entry.Value))
                return false;
        }
        return true;
    }

    public void SetPreset(string presetName)
    {
        var preset = SettingsPresets.Find(x => x.name == presetName).Item2;
        foreach (var field in preset)
        {
            typeof(Settings).GetField(field.Key).SetValue(settings, field.Value);
        }
        ApplyAutoSettings();
    }

    public void ApplyAutoSettings()
    {
        DoAutoSettings = false;
    }
    private static readonly FieldInfo PhysBoneColliderGlobalCollisionFlagsField = typeof(VRCPhysBoneColliderBase).GetField("globalCollisionFlags");

    private string trashBinPath = "Assets/SynthosFxOptimizer/TrashBin/";
    private HashSet<string> usedBlendShapes = new HashSet<string>();
    private Dictionary<SkinnedMeshRenderer, List<int>> blendShapesToBake = new Dictionary<SkinnedMeshRenderer, List<int>>();
    private Dictionary<AnimationPath, AnimationPath> newAnimationPaths = new Dictionary<AnimationPath, AnimationPath>();
    private Dictionary<string, List<List<string>>> oldPathToMergedPaths = new Dictionary<string, List<List<string>>>();
    private Dictionary<string, string> oldPathToMergedPath = new Dictionary<string, string>();
    private Dictionary<string, List<string>> physBonesToDisable = new Dictionary<string, List<string>>();
    private Dictionary<(string path, int slot), HashSet<Material>> slotSwapMaterials = new Dictionary<(string, int), HashSet<Material>>();
    private Dictionary<(string path, int slot), Dictionary<Material, Material>> optimizedSlotSwapMaterials = new Dictionary<(string, int), Dictionary<Material, Material>>();
    private Dictionary<(string path, int index), (string path, int index)> materialSlotRemap = new Dictionary<(string, int), (string, int)>();
    private HashSet<Transform> keepTransforms = new HashSet<Transform>();
    private Dictionary<string, Transform> transformFromOldPath = new Dictionary<string, Transform>();
    private Dictionary<EditorCurveBinding, float> constantAnimatedValuesToAdd = new Dictionary<EditorCurveBinding, float>();
    // blendshape names come from https://www.deviantart.com/xoriu/art/MMD-Facial-Expressions-Chart-341504917
    private static HashSet<string> MMDBlendShapes = new HashSet<string>()
    {
        "まばたき", "Blink",
        "笑い", "Smile",
        "ウィンク", "Wink",
        "ウィンク右", "Wink-a",
        "ウィンク２", "Wink-b",
        "ｳｨﾝｸ２右", "Wink-c",
        "なごみ", "Howawa",
        "はぅ", "> <",
        "びっくり", "Ha!!!",
        "じと目", "Jito-eye",
        "ｷﾘｯ", "Kiri-eye",
        "はちゅ目", "O O",
        "星目", "EyeStar",
        "はぁと", "EyeHeart",
        "瞳小", "EyeSmall",
        "瞳縦潰れ", "EyeSmall-v",
        "光下", "EyeUnderli",
        "恐ろしい子！", "EyeFunky",
        "ハイライト消", "EyeHi-off",
        "映り込み消", "EyeRef-off",
        "喜び", "Joy",
        "わぉ?!", "Wao?!",
        "なごみω", "Howawa ω",
        "悲しむ", "Wail",
        "敵意", "Hostility",
        "あ", "a",
        "い", "i",
        "う", "u",
        "え", "e",
        "お", "o",
        "あ２", "a 2",
        "ん", "n",
        "▲", "Mouse_1",
        "∧", "Mouse_2",
        "□", "□",
        "ワ", "Wa",
        "ω", "Omega",
        "ω□", "ω□",
        "にやり", "Niyari",
        "にやり２", "Niyari2",
        "にっこり", "Smile",
        "ぺろっ", "Pero",
        "てへぺろ", "Bero-tehe",
        "てへぺろ２", "Bero-tehe2",
        "口角上げ", "MouseUP",
        "口角下げ", "MouseDW",
        "口横広げ", "MouseWD",
        "歯無し上", "ToothAnon",
        "歯無し下", "ToothBnon",
        "真面目", "Serious",
        "困る", "Trouble",
        "にこり", "Smily",
        "怒り", "Get angry",
        "上", "UP",
        "下", "Down",
        "Grin",
        "Blink",
        "Blink Happy",
        "Pupil",
        "Wink",
        "Wink Right",
        "Wink 2",
        "Wink 2 Right",
        "Calm",
        "Stare",
        "Cheerful",
        "Sadness",
        "Anger",
        "Upper",
        "Lower"
    };

    private static float progressBar = 0;

    private void DisplayProgressBar(string text)
    {
        var name = GetRootTransform().name;
        var titleName = name.EndsWith("(BrokenCopy)") ? name.Substring(0, name.Length - "(BrokenCopy)".Length) : name;
        EditorUtility.DisplayProgressBar("Optimizing " + titleName, text, progressBar);
    }

    private void DisplayProgressBar(string text, float progress)
    {
        progressBar = progress;
        DisplayProgressBar(text);
    }

    public static (string root, string name, string path) GetTrashBinLocation()
    {
        var assembly = typeof(SynthosFxOptimizer).Assembly;
        var asmdefPath = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(assembly.GetName().FullName);
        var inPackageAsmdefPath = "/Editor/Synthos.FxOptimizer.Editor.asmdef";
        var packageInfo = PackageInfo.FindForAssembly(assembly);
        string trashBinRoot;
        if (packageInfo?.source == UnityEditor.PackageManager.PackageSource.Embedded
            && !string.IsNullOrEmpty(packageInfo.assetPath))
        {
            trashBinRoot = packageInfo.assetPath;
        }
        else if (asmdefPath.StartsWithSimple("Assets") && asmdefPath.EndsWith(inPackageAsmdefPath))
        {
            trashBinRoot = asmdefPath[..^inPackageAsmdefPath.Length];
        }
        else
        {
            trashBinRoot = "Assets/SynthosFxOptimizer";
            if (!AssetDatabase.IsValidFolder(trashBinRoot))
            {
                AssetDatabase.CreateFolder("Assets", "SynthosFxOptimizer");
            }
        }
        return (trashBinRoot, "TrashBin", $"{trashBinRoot}/TrashBin/");
    }

    public static d4rkpl4y3r.AvatarOptimizer.Util.Logger log = null;

    private void ClearTrashBin(int playModeAvatarIndex)
    {
        Profiler.StartSection("ClearTrashBin()");
        var packageInfo = PackageInfo.FindForAssembly(typeof(SynthosFxOptimizer).Assembly);
        var (trashBinRoot, trashBinName, path) = GetTrashBinLocation();
        trashBinPath = path;
        if (playModeAvatarIndex <= 1 || !AssetDatabase.IsValidFolder($"{trashBinRoot}/{trashBinName}"))
        {
            AssetDatabase.DeleteAsset($"{trashBinRoot}/{trashBinName}");
            AssetDatabase.CreateFolder(trashBinRoot, trashBinName);
        }
        if (playModeAvatarIndex > 0)
        {
            var subFolder = SanitizeName($"{playModeAvatarIndex}_{GetRootTransform().name.Replace("(Clone)", "").Trim()}");
            AssetDatabase.CreateFolder($"{trashBinRoot}/{trashBinName}", subFolder);
            trashBinPath = $"{trashBinRoot}/{trashBinName}/{subFolder}/";
        }
        binaryAssetBundlePath = null;
        materialAssetBundlePath = null;
        log = new (Path.Combine(trashBinPath, "_SynthosFxOptimizer.log"));
        LogToFile($"Synthos FX Optimizer v{packageInfo?.version ?? "unknown"} (based on d4rkAvatarOptimizer by d4rkpl4y3r)");
        LogToFile($"Unity Version: {Application.unityVersion}");
        var vrcPackageInfo = PackageInfo.FindForAssembly(typeof(VRCAvatarDescriptor).Assembly);
        if (vrcPackageInfo != null)
        {
            LogToFile($"VRChat Avatar SDK: {vrcPackageInfo.version}");
        }
        LogToFile($"Application.isPlaying: {Application.isPlaying}");
        LogToFile($"Build Target: {EditorUserBuildSettings.activeBuildTarget}");
        LogToFile($"SystemInfo.graphicsDeviceType: {SystemInfo.graphicsDeviceType}");
        LogToFile("Global Settings:");
        using (log.IndentScope())
        {
            LogToFile($"- Always Optimize on Upload: {AvatarOptimizerSettings.DoOptimizeWithDefaultSettingsWhenNoComponent}");
            LogToFile($"- Optimize in Play Mode: {AvatarOptimizerSettings.DoOptimizeInPlayMode}");
            LogToFile($"- Auto Refresh Preview Timeout: {AvatarOptimizerSettings.AutoRefreshPreviewTimeout} ms");
            LogToFile($"- Shared Mesh Vertex Count Exclusion Threshold: {AvatarOptimizerSettings.SharedMeshVertexCountExclusionThreshold}");
        }
        LogToFile("Settings:");
        using (log.IndentScope())
        {
            foreach (var field in typeof(Settings).GetFields())
            {
                LogToFile($"- {GetDisplayName(field.Name)}: {field.GetValue(settings)}");
            }
        }
        Profiler.EndSection();
    }

    private static string SanitizeName(string name)
    {
        var invalids = Path.GetInvalidFileNameChars();
        return string.Join("_", name.Split(invalids, System.StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.');
    }

    private string binaryAssetBundlePath = null;
    private string materialAssetBundlePath = null;
    private void CreateUniqueAsset(Object asset, string name)
    {
        Profiler.StartSection($"AssetDatabase.CreateAsset({asset.GetType().Name})");
        var sanitizedName = SanitizeName(name);
        if (asset is Material)
        {
            if (materialAssetBundlePath == null)
            {
                materialAssetBundlePath = AssetDatabase.GenerateUniqueAssetPath(trashBinPath + sanitizedName);
                AssetDatabase.CreateAsset(asset, materialAssetBundlePath);
            }
            else
            {
                AssetDatabase.AddObjectToAsset(asset, materialAssetBundlePath);
            }
        }
        else
        {
            if (binaryAssetBundlePath == null)
            {
                binaryAssetBundlePath = AssetDatabase.GenerateUniqueAssetPath(trashBinPath + "BinaryAssetBundle.asset");
                AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<BinarySerializationSO>(), binaryAssetBundlePath);
            }
            AssetDatabase.AddObjectToAsset(asset, binaryAssetBundlePath);
        }
        Profiler.EndSection();
    }

    private void LogToFile(string message, int extraIndent = 0)
    {
        if (!isOptimizing || log == null)
            return;
        using var _ = new Profiler.Section("LogToFile()");
        log.indentLevel += extraIndent;
        log.Append(message);
        log.indentLevel -= extraIndent;
    }

    private void ImportLogFile()
    {
        if (log == null)
            return;
        log.Flush();
        AssetDatabase.ImportAsset(log.filePath);
    }

    [System.Serializable]
    public class BuildSummary
    {
        public string time;
        public int fxLayersBefore, fxLayersAfter;
        public int fxParametersBefore, fxParametersAfter;
        public int blendShapesBefore, blendShapesAfter;
        public int physBonesDisabled;
        public int componentsDeleted;
    }

    private const string BuildSummaryPrefsPrefix = "Synthos_FxOptimizer_LastBuild_";
    private int componentsDeletedCount = 0;

    // The optimizer runs on the upload clone, so the summary is stored by blueprint id (or avatar name)
    // where the component on the scene avatar can find it again.
    public string GetBuildSummaryKey()
    {
        var av = GetAvatarDescriptor();
        if (av == null)
            return null;
        if (av.TryGetComponent<VRC.Core.PipelineManager>(out var pm) && !string.IsNullOrEmpty(pm.blueprintId))
            return pm.blueprintId;
        var name = av.name;
        return name.EndsWith("(Clone)") ? name.Substring(0, name.Length - "(Clone)".Length).TrimEnd() : name;
    }

    public BuildSummary GetLastBuildSummary()
    {
        var key = GetBuildSummaryKey();
        if (key == null)
            return null;
        var json = EditorPrefs.GetString(BuildSummaryPrefsPrefix + key, "");
        if (string.IsNullOrEmpty(json))
            return null;
        try
        {
            return JsonUtility.FromJson<BuildSummary>(json);
        }
        catch (System.ArgumentException)
        {
            return null;
        }
    }

    private (int fxLayers, int fxParameters, int blendShapes) CountBuildSummaryStats()
    {
        var fx = GetFXLayer();
        var blendShapes = GetRootTransform().GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .Sum(r => r.sharedMesh == null ? 0 : r.sharedMesh.blendShapeCount);
        return (fx == null ? 0 : fx.layers.Length, fx == null ? 0 : fx.parameters.Length, blendShapes);
    }

    private void SaveBuildSummary((int fxLayers, int fxParameters, int blendShapes) before, (int fxLayers, int fxParameters, int blendShapes) after)
    {
        var summary = new BuildSummary
        {
            time = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            fxLayersBefore = before.fxLayers,
            fxLayersAfter = after.fxLayers,
            fxParametersBefore = before.fxParameters,
            fxParametersAfter = after.fxParameters,
            blendShapesBefore = before.blendShapes,
            blendShapesAfter = after.blendShapes,
            physBonesDisabled = physBonesToDisable.Values.SelectMany(paths => paths).Distinct().Count(),
            componentsDeleted = componentsDeletedCount,
        };
        LogToFile($"Summary: FX layers {summary.fxLayersBefore} -> {summary.fxLayersAfter}, FX parameters {summary.fxParametersBefore} -> {summary.fxParametersAfter}, "
            + $"blend shapes {summary.blendShapesBefore} -> {summary.blendShapesAfter}, phys bones auto disabled {summary.physBonesDisabled}, components deleted {summary.componentsDeleted}");
        var key = GetBuildSummaryKey();
        if (key != null)
            EditorPrefs.SetString(BuildSummaryPrefsPrefix + key, JsonUtility.ToJson(summary));
    }

    private AnimatorController[] GetAvDescriptorControllers()
    {
        var av = GetAvatarDescriptor();
        return av.baseAnimationLayers.Concat(av.specialAnimationLayers)
            .Select(l => l.animatorController as AnimatorController)
            .Where(c => c != null).Distinct().ToArray();
    }

    private void LogAvatarStats(string header)
    {
        using var _ = new Profiler.Section("LogAvatarStats()");
        LogToFile(header);
        using var __ = log.IndentScope();
        var av = GetAvatarDescriptor();
        var components = av.GetComponentsInChildren<Component>(true).Where(c => c != null).GroupBy(c => c.GetType()).OrderByDescending(g => g.Count()).ThenBy(g => g.Key.FullName).ToArray();
        LogToFile($"- Component Types: {components.Length}");
        foreach (var group in components)
        {
            LogToFile($"- {group.Key}: {group.Count()}", 1);
        }
        var renderers = components.Where(g => g.Key == typeof(MeshRenderer) || g.Key == typeof(SkinnedMeshRenderer)).SelectMany(g => g).Cast<Renderer>().ToArray();
        var skinnedMeshRenderers = components.Where(g => g.Key == typeof(SkinnedMeshRenderer)).SelectMany(g => g).Cast<SkinnedMeshRenderer>().ToArray();
        LogToFile($"- Poly Count: {renderers.Sum(r => GetRendererPolyCount(r))}");
        LogToFile($"- BlendShapes: {skinnedMeshRenderers.Sum(r => r.sharedMesh == null ? 0 : r.sharedMesh.blendShapeCount)}");
        LogToFile($"- Unique Bones: {skinnedMeshRenderers.SelectMany(r => r.bones).Where(b => b != null).Distinct().Count()}");
        LogToFile($"- Renderer Material Slots: {renderers.Sum(r => r.sharedMaterials.Length)}");

        var sharedSkinnedMeshes = skinnedMeshRenderers
            .Where(r => r.sharedMesh != null)
            .GroupBy(r => r.sharedMesh)
            .Where(g => g.Count() > 1)
            .Select(g => (
                mesh: g.Key,
                paths: g.Select(GetPathToRoot).OrderBy(path => path).ToArray()))
            .OrderBy(g => g.mesh.name)
            .ThenBy(g => g.paths[0])
            .ToList();
        if (sharedSkinnedMeshes.Count > 0)
        {
            LogToFile($"- Meshes shared by multiple Skinned Mesh Renderers: {sharedSkinnedMeshes.Count}");
            foreach (var group in sharedSkinnedMeshes)
            {
                LogToFile($"- '{group.mesh.name}' ({group.mesh.vertexCount} vertices):", 1);
                foreach (var path in group.paths)
                {
                    LogToFile($"- {path}", 2);
                }
            }
        }

        var extraMaterialSlotRenderers = renderers.Select(r => (renderer: r, mesh: r.GetSharedMesh()))
            .Where(x => x.mesh != null && x.renderer.sharedMaterials.Length > x.mesh.subMeshCount)
            .Select(x => (
                path: GetPathToRoot(x.renderer),
                extraMaterialSlots: x.renderer.sharedMaterials.Length - x.mesh.subMeshCount,
                extraPolygons: GetRendererExtraMaterialSlotPolyCount(x.renderer)))
            .OrderBy(x => x.path)
            .ToList();
        if (extraMaterialSlotRenderers.Count > 0)
        {
            LogToFile($"- Renderers with extra material slots: {extraMaterialSlotRenderers.Count}");
            foreach (var entry in extraMaterialSlotRenderers)
            {
                LogToFile($"- '{entry.path}': +{entry.extraMaterialSlots} material slots, +{entry.extraPolygons} polygons", 1);
            }
        }

        using (new Profiler.Section("LogAvatarStats() - Animator Analysis"))
        {
            var animatorControllers = GetAvDescriptorControllers();
            var animatorLayers = animatorControllers.SelectMany(c => c.layers).ToArray();
            LogToFile($"- Animator Layers: {animatorLayers.Length}");
            var animatorParameters = animatorControllers.SelectMany(c => c.parameters)
                .Select(p => p.name).Distinct().ToArray();
            LogToFile($"- Unique Animator Parameters: {animatorParameters.Length}");
            static int CalculateBlendTreePerfRank(BlendTree tree)
            {
                if (tree == null)
                    return 0;
                if (tree.blendType == BlendTreeType.Direct)
                {
                    return tree.children.Sum(c =>
                    {
                        if (c.motion is AnimationClip)
                            return 1;
                        if (c.motion is BlendTree subTree)
                            return CalculateBlendTreePerfRank(subTree);
                        return 0;
                    });
                }
                else
                {
                    int worst = 0;
                    foreach (var c in tree.children)
                    {
                        int rank = 0;
                        if (c.motion is AnimationClip)
                            rank = 1;
                        else if (c.motion is BlendTree subTree)
                            rank = CalculateBlendTreePerfRank(subTree);
                        if (rank > worst)
                            worst = rank;
                    }
                    return tree.blendType == BlendTreeType.Simple1D ? worst * 2 : worst * 3;
                }
            }
            int blendTreeRank = animatorLayers.Sum(l => l == null || l.stateMachine == null ? 0
                : l.stateMachine.EnumerateAllStates().Select(s => CalculateBlendTreePerfRank(s.motion as BlendTree)).Aggregate(0, (a, b) => a > b ? a : b));
            LogToFile($"- Blend Tree Perf Rank: {blendTreeRank}");
            // a single layer with a simple toggle is approximately 4 times as expensive a simple 1d blend tree inside a direct blend tree
            // from: https://vrc.school/docs/Other/Benchmarks/#direct-blend-trees
            // 1d inside direct is 2 score so the layer is 8 score
            LogToFile($"- Animator Complexity Score: {animatorLayers.Length * 8 + blendTreeRank}");
            var uniqueClips = animatorControllers.SelectMany(c => c.animationClips).Where(c => c != null).Distinct().ToArray();
            LogToFile($"- Animation Clips: {uniqueClips.Length}");
            var uniqueBindings = uniqueClips.SelectMany(c => AnimationUtility.GetCurveBindings(c))
                .Concat(uniqueClips.SelectMany(c => AnimationUtility.GetObjectReferenceCurveBindings(c)))
                .Distinct().ToArray();
            LogToFile($"- Unique Curve Bindings: {uniqueBindings.Length}");
        }

        var stack = new Stack<Transform>();
        stack.Push(GetRootTransform());
        var missingComponentPaths = new List<string>();
        var gameObjectsWithSlashesInName = new List<string>();
        var ambiguousPaths = new List<string>();
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            var currentPath = GetPathToRoot(current);
            if (current.GetComponents<Component>().Any(c => c == null))
            {
                missingComponentPaths.Add($"- '{currentPath}'");
            }
            if (current != GetTransformFromPath(currentPath))
            {
                ambiguousPaths.Add($"- '{currentPath}'");
                continue;
            }
            foreach (Transform child in current)
            {
                if (child.name.Contains('/'))
                {
                    gameObjectsWithSlashesInName.Add($"- '{currentPath}' => '{child.name}'");
                }
                stack.Push(child);
            }
        }
        void LogPathList(string header, List<string> paths)
        {
            if (paths.Count == 0)
                return;
            paths = paths.Distinct().ToList();
            LogToFile($"{header}: {paths.Count}");
            foreach (var path in paths)
            {
                LogToFile(path, 1);
            }
        }
        LogPathList("- GameObjects with missing components", missingComponentPaths);
        LogPathList("- GameObjects with '/' in name", gameObjectsWithSlashesInName);
        LogPathList("- Ambiguous Paths", ambiguousPaths);
    }

    private VRCAvatarDescriptor cache_avatarDescriptor = null;
    public VRCAvatarDescriptor GetAvatarDescriptor()
    {
        if (cache_avatarDescriptor != null)
            return cache_avatarDescriptor;
        cache_avatarDescriptor = null;
        var current = transform;
        while (current != null)
        {
            if (current.TryGetComponent<VRCAvatarDescriptor>(out var descriptor))
            {
                cache_avatarDescriptor = descriptor;
                break;
            }
            current = current.parent;
        }
        return cache_avatarDescriptor;
    }

    // Use reflection so the optimizer also works with SDK versions without native face tracking.
    private static object GetFaceTrackingField(object instance, string name)
    {
        return instance?.GetType().GetField(name)?.GetValue(instance);
    }

    private bool HasFaceTrackingEnabled()
    {
        var descriptor = GetAvatarDescriptor();
        if (descriptor == null)
            return false;
        return GetFaceTrackingField(descriptor, "enableFaceTracking") is true;
    }

    private (HashSet<SkinnedMeshRenderer> meshes, HashSet<string> names)? cache_FaceTrackingSetup;
    public (HashSet<SkinnedMeshRenderer> meshes, HashSet<string> names) GetFaceTrackingSetup()
    {
        if (cache_FaceTrackingSetup.HasValue)
            return cache_FaceTrackingSetup.Value;
        if (!HasFaceTrackingEnabled())
        {
            cache_FaceTrackingSetup = (new HashSet<SkinnedMeshRenderer>(), new HashSet<string>());
            return cache_FaceTrackingSetup.Value;
        }
        var meshes = new HashSet<SkinnedMeshRenderer>();
        var names = new HashSet<string>(System.StringComparer.Ordinal);
        var descriptor = GetAvatarDescriptor();
        var root = GetRootTransform();
        if (GetFaceTrackingField(descriptor, "faceTrackingMeshes") is IEnumerable<SkinnedMeshRenderer> configuredMeshes)
        {
            // The SDK only uses the first 16 list entries, including null entries.
            meshes.UnionWith(configuredMeshes.Take(16).Where(r => r != null && r.transform.IsChildOf(root)));
        }
        void AddNames(object value)
        {
            if (value is IEnumerable<string> entries)
                names.UnionWith(entries.Where(n => !string.IsNullOrEmpty(n)));
        }
        if (GetFaceTrackingField(descriptor, "faceTrackingBlendshapesSetList") is System.Collections.IEnumerable sets)
        {
            foreach (var set in sets)
            {
                AddNames(GetFaceTrackingField(set, "faceTrackingBlendshapeNames"));
                AddNames(GetFaceTrackingField(set, "visemeBlendshapeNames"));
            }
        }
        if (GetFaceTrackingField(descriptor, "faceTrackingParameterBehaviors") is System.Collections.IEnumerable behaviors)
        {
            foreach (var behavior in behaviors)
            {
                var mode = GetFaceTrackingField(behavior, "targetMode")?.ToString();
                if (mode == "ApplyToBlendshape" || mode == "LimitBlendshapeMin" || mode == "LimitBlendshapeMax")
                    AddNames(new[] { GetFaceTrackingField(behavior, "targetBlendshapeName") as string });
            }
        }
        LogToFile($"Face tracking: {meshes.Count} meshes, {names.Count} declared blendshape names:");
        foreach (var name in names.OrderBy(n => n, System.StringComparer.Ordinal))
            LogToFile($"- Blendshape: '{name}'", 1);
        foreach (var renderer in meshes.OrderBy(r => GetPathToRoot(r), System.StringComparer.Ordinal))
            LogToFile($"- Mesh: '{GetPathToRoot(renderer)}'", 1);
        cache_FaceTrackingSetup = (meshes, names);
        return (meshes, names);
    }

    private HashSet<Type> cache_GetAllComponentTypesInAvatar = null;
    public HashSet<Type> GetAllComponentTypesInAvatar()
    {
        if (cache_GetAllComponentTypesInAvatar != null)
            return cache_GetAllComponentTypesInAvatar;
        var descriptor = GetAvatarDescriptor();
        if (descriptor == null)
            return new HashSet<Type>();
        var allComponentTypes = new HashSet<Type>(descriptor.GetComponentsInChildren<Component>(true).Where(c => c != null).Select(c => c.GetType()));
        return cache_GetAllComponentTypesInAvatar = allComponentTypes;
    }

    private HashSet<string> cache_toolsUsedOnAvatar = null;
    private long lastToolsCheckTime = 0;
    public HashSet<string> GetNonDestructiveToolsUsedOnAvatar()
    {
        if (System.DateTime.Now.Ticks - lastToolsCheckTime > System.TimeSpan.TicksPerMinute * 2)
        {
            cache_toolsUsedOnAvatar = null;
            cache_GetAllComponentTypesInAvatar = null;
            lastToolsCheckTime = System.DateTime.Now.Ticks;
        }
        if (cache_toolsUsedOnAvatar != null)
            return cache_toolsUsedOnAvatar;
        var tools = new HashSet<string>();
        var allComponentTypes = GetAllComponentTypesInAvatar();
        if (allComponentTypes.Any(t => t.FullName.StartsWithSimple("nadena.dev.modular_avatar.core.")))
        {
            tools.Add("Modular Avatar");
        }
        if (allComponentTypes.Any(t => t.FullName == "VF.Model.VRCFury" || t.FullName.StartsWithSimple("VF.Component.")))
        {
            tools.Add("VRCFury");
        }
        if (allComponentTypes.Any(t => t.FullName.StartsWithSimple("Prefabulous.Universal.Common.Runtime.")))
        {
            tools.Add("Prefabulous Avatar");
        }
        return cache_toolsUsedOnAvatar = tools;
    }

    public Transform GetRootTransform()
    {
        var descriptor = GetAvatarDescriptor();
        return descriptor == null ? transform : descriptor.transform;
    }

    private static string GetTransformPathTo(Transform t, Transform root)
    {
        if (t == root)
            return "";
        string path = t.name;
        while ((t = t.parent) != root)
        {
            if (t == null)
                return null;
            path = $"{t.name}/{path}";
        }
        return path;
    }

    public string GetTransformPathToRoot(Transform t)
    {
        return GetTransformPathTo(t, GetRootTransform());
    }

    public Transform GetTransformFromPath(string path, Transform current = null, int index = 0)
    {
        if (string.IsNullOrEmpty(path))
            return GetRootTransform();
        if (current == null)
            current = GetRootTransform();
        foreach (Transform t in current)
        {
            if (path.StartsWithSimple(t.name, index))
            {
                int nextIndex = index + t.name.Length;
                if (path.Length == nextIndex)
                    return t;
                if (path[nextIndex] != '/')
                    continue;
                var result = GetTransformFromPath(path, t, nextIndex + 1);
                if (result != null)
                    return result;
            }
        }
        return null;
    }

    public string GetPathToRoot(GameObject obj)
    {
        return GetTransformPathToRoot(obj.transform);
    }

    public string GetPathToRoot(Component component)
    {
        return GetTransformPathToRoot(component.transform);
    }

    public void ClearCaches()
    {
        var fields = GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance);
        foreach (var field in fields)
        {
            if (field.Name.StartsWithSimple("cache_"))
            {
                field.SetValue(this, null);
            }
        }
        MaterialAssetComparer.ClearCache();
    }

    public long GetRendererPolyCount(Renderer renderer)
    {
        if (renderer == null || !(renderer is SkinnedMeshRenderer || renderer is MeshRenderer))
            return 0;
        var mesh = renderer.GetSharedMesh();
        if (mesh == null)
            return 0;
        // this only counts triangles & quads properly to match vrchat's triangle performance stat way of counting
        return Enumerable.Range(0, mesh.subMeshCount).Sum(i => mesh.GetIndexCount(i) / (mesh.GetTopology(i) == MeshTopology.Quads ? 2 : 3));
    }

    public long GetRendererExtraMaterialSlotPolyCount(Renderer renderer)
    {
        if (renderer == null || !(renderer is SkinnedMeshRenderer || renderer is MeshRenderer))
            return 0;
        var mesh = renderer.GetSharedMesh();
        if (mesh == null || mesh.subMeshCount == 0 || renderer.sharedMaterials.Length <= mesh.subMeshCount)
            return 0;
        int extraMaterialSlotCount = renderer.sharedMaterials.Length - mesh.subMeshCount;
        int lastSubMeshIndex = mesh.subMeshCount - 1;
        long polygonsPerExtraSlot = mesh.GetIndexCount(lastSubMeshIndex) / (mesh.GetTopology(lastSubMeshIndex) == MeshTopology.Quads ? 2 : 3);
        return polygonsPerExtraSlot * extraMaterialSlotCount;
    }

    private Dictionary<Renderer, List<ParticleSystem>> cache_ParticleSystemsUsingRenderer = null;
    private List<ParticleSystem> GetParticleSystemsUsingRenderer(Renderer candidate)
    {
        if (cache_ParticleSystemsUsingRenderer == null)
        {
            cache_ParticleSystemsUsingRenderer = new Dictionary<Renderer, List<ParticleSystem>>();
            foreach (var ps in GetUsedComponentsInChildren<ParticleSystem>())
            {
                Renderer renderer = ps.shape.shapeType == ParticleSystemShapeType.SkinnedMeshRenderer ? ps.shape.skinnedMeshRenderer : null;
                renderer = ps.shape.shapeType == ParticleSystemShapeType.MeshRenderer ? ps.shape.meshRenderer : renderer;
                if (renderer != null)
                {
                    if (!cache_ParticleSystemsUsingRenderer.TryGetValue(renderer, out var list))
                    {
                        list = new List<ParticleSystem>();
                        cache_ParticleSystemsUsingRenderer[renderer] = list;
                    }
                    list.Add(ps);
                }
            }
        }
        if (cache_ParticleSystemsUsingRenderer.TryGetValue(candidate, out var result))
            return result;
        return cache_ParticleSystemsUsingRenderer[candidate] = new List<ParticleSystem>();
    }

    // Material contact fields only exist in newer VRChat SDKs; read them via reflection so the
    // optimizer still compiles and runs on SDK 3.10.5 and older (where no receiver can write to materials).
    private static readonly FieldInfo ContactWriteToMaterialsField = typeof(VRCContactReceiver).GetField("writeToMaterials");
    private static readonly FieldInfo ContactShaderTargetRenderersField = typeof(VRCContactReceiver).GetField("shaderTargetRenderers");
    private static bool WritesToMaterials(VRCContactReceiver receiver) => ContactWriteToMaterialsField?.GetValue(receiver) is bool value && value;
    private static Renderer[] GetShaderTargetRenderers(VRCContactReceiver receiver) => ContactShaderTargetRenderersField?.GetValue(receiver) as Renderer[];
    private static void SetShaderTargetRenderers(VRCContactReceiver receiver, Renderer[] targets) => ContactShaderTargetRenderersField?.SetValue(receiver, targets);

    private List<VRCContactReceiver> cache_MaterialContactReceivers = null;    public List<VRCContactReceiver> FindAllMaterialContactReceivers()
    {
        if (cache_MaterialContactReceivers != null)
            return cache_MaterialContactReceivers;
        cache_MaterialContactReceivers = new List<VRCContactReceiver>();
        foreach (var receiver in GetUsedComponentsInChildren<VRCContactReceiver>())
        {
            if (receiver != null &&
                receiver.receiverType == ContactReceiver.ReceiverType.Proximity &&
                WritesToMaterials(receiver) &&
                GetShaderTargetRenderers(receiver) != null)
                cache_MaterialContactReceivers.Add(receiver);
        }
        return cache_MaterialContactReceivers;
    }

    private Dictionary<Renderer, HashSet<VRCContactReceiver>> cache_MaterialContactReceiversUsingRenderer = null;
    private HashSet<VRCContactReceiver> GetMaterialContactReceiversUsingRenderer(Renderer candidate)
    {
        if (cache_MaterialContactReceiversUsingRenderer == null)
        {
            cache_MaterialContactReceiversUsingRenderer = new Dictionary<Renderer, HashSet<VRCContactReceiver>>();
            foreach (var receiver in FindAllMaterialContactReceivers())
            {
                var targets = GetShaderTargetRenderers(receiver);
                for (int i = 0; i < targets.Length; i++)
                {
                    var renderer = targets[i];
                    if (renderer == null)
                        continue;
                    if (!cache_MaterialContactReceiversUsingRenderer.TryGetValue(renderer, out var set))
                    {
                        set = new HashSet<VRCContactReceiver>();
                        cache_MaterialContactReceiversUsingRenderer[renderer] = set;
                    }
                    set.Add(receiver);
                }
            }
        }
        if (cache_MaterialContactReceiversUsingRenderer.TryGetValue(candidate, out var result))
            return result;
        return cache_MaterialContactReceiversUsingRenderer[candidate] = new HashSet<VRCContactReceiver>();
    }

    private void AddAnimationPathChange((string path, string name, Type type) source, (string path, string name, Type type) target)
    {
        if (source == target)
            return;
        newAnimationPaths[source] = target;
    }

    // Meshes are never merged, so every renderer with materials is its own group.
    public List<List<Renderer>> FindPossibleSkinnedMeshMerges()
    {
        slotSwapMaterials = FindAllMaterialSwapMaterials();
        return GetUsedComponentsInChildren<Renderer>()
            .Where(r => r.sharedMaterials.Length > 0)
            .OrderBy(r => r is SkinnedMeshRenderer || r is MeshRenderer ? 0 : 1)
            .Select(r => new List<Renderer> { r })
            .ToList();
    }

    private HashSet<string> cache_TargetPathHasAnyMaterialSwap = null;    private bool TargetPathHasAnyMaterialSwap(string path)
    {
        if (cache_TargetPathHasAnyMaterialSwap == null) {
            cache_TargetPathHasAnyMaterialSwap = new HashSet<string>();
            foreach (var oldPath in FindAllMaterialSwapMaterials().Keys.Select(key => key.path).Distinct()) {
                var newPath = oldPath;
                if (oldPathToMergedPath.TryGetValue(oldPath, out var mergedPath)) {
                    newPath = mergedPath;
                }
                if (transformFromOldPath.TryGetValue(newPath, out var t) && t != null) {
                    newPath = GetPathToRoot(t);
                }
                cache_TargetPathHasAnyMaterialSwap.Add(newPath);
            }
        }
        return cache_TargetPathHasAnyMaterialSwap.Contains(path);
    }

    private EditorCurveBinding FixAnimationBindingPath(EditorCurveBinding binding, ref bool changed)
    {
        var newBinding = binding;
        if (transformFromOldPath.TryGetValue(newBinding.path, out var transform))
        {
            if (transform != null)
            {
                var path = GetPathToRoot(transform);
                // merged meshes move all their sibling components to a new child object
                // the general remap in transformFromOldPath points to that new child object
                // which means transform and renderer animations should still point to the original parent object
                // while gameobject toggles as well as other component animations should not
                if (path.EndsWith("/d4rkAO_mergeTargetRoot") &&
                    (binding.type == typeof(Transform) || typeof(Renderer).IsAssignableFrom(binding.type)))
                {
                    path = path.Substring(0, path.Length - "/d4rkAO_mergeTargetRoot".Length);
                }
                changed = changed || path != newBinding.path;
                newBinding.path = path;
                if (binding.type == typeof(MeshRenderer) && !transform.TryGetComponent(out MeshRenderer renderer))
                {
                    newBinding.type = typeof(SkinnedMeshRenderer);
                    changed = true;
                }
            }
        }
        return newBinding;
    }

    private EditorCurveBinding FixAnimationBinding(EditorCurveBinding binding, ref bool changed)
    {
        var currentPath = (binding.path, binding.propertyName, binding.type);
        var newBinding = binding;
        if (newAnimationPaths.TryGetValue(currentPath, out var modifiedPath))
        {
            newBinding.path = modifiedPath.Item1;
            newBinding.propertyName = modifiedPath.Item2;
            newBinding.type = modifiedPath.Item3;
            changed = true;
        }
        else if (typeof(Renderer).IsAssignableFrom(binding.type) && newAnimationPaths.TryGetValue((binding.path, binding.propertyName, typeof(SkinnedMeshRenderer)), out modifiedPath))
        {
            newBinding.path = modifiedPath.Item1;
            newBinding.propertyName = modifiedPath.Item2;
            newBinding.type = modifiedPath.Item3;
            changed = true;
        }
        return FixAnimationBindingPath(newBinding, ref changed);
    }

    private static readonly Dictionary<char, char> otherVectorOrColorComponent = new Dictionary<char, char> {
        { 'x', 'r' }, { 'y', 'g' }, { 'z', 'b' }, { 'w', 'a' },
        { 'r', 'x' }, { 'g', 'y' }, { 'b', 'z' }, { 'a', 'w' },
    };
    Dictionary<string, Dictionary<Type, HashSet<string>>> cache_IsAnimatableBinding = null;
    public bool IsAnimatableBinding(EditorCurveBinding binding) {
        cache_IsAnimatableBinding ??= new();
        if (!cache_IsAnimatableBinding.TryGetValue(binding.path, out var animatableBindings)) {
            animatableBindings = new Dictionary<Type, HashSet<string>>();
            GameObject targetObject = GetTransformFromPath(binding.path)?.gameObject;
            if (targetObject != null) {
                foreach (var animatableBinding in AnimationUtility.GetAnimatableBindings(targetObject, GetRootTransform().gameObject)) {
                    var name = animatableBinding.propertyName;
                    var type = animatableBinding.type;
                    if (!animatableBindings.TryGetValue(type, out var animatableProperties)) {
                        animatableProperties = new HashSet<string>();
                        animatableBindings[type] = animatableProperties;
                    }
                    animatableProperties.Add(name);
                    if (name.Length > 2 && name[^2] == '.' && otherVectorOrColorComponent.TryGetValue(name[^1], out var otherComponent)) {
                        // Color & Vector properties can both be animated by .xyzw or .rgba but only one of them gets returned by GetAnimatableBindings
                        animatableProperties.Add(name[..^1] + otherComponent);
                    }
                }
                if (!animatableBindings.ContainsKey(typeof(GameObject))) {
                    animatableBindings[typeof(GameObject)] = new HashSet<string>();
                }
                animatableBindings[typeof(GameObject)].Add("ComponentExists");
                foreach (var component in targetObject.GetNonNullComponents()) {
                    var componentType = component.GetType();
                    if (!animatableBindings.ContainsKey(componentType)) {
                        animatableBindings[componentType] = new HashSet<string>();
                    }
                    animatableBindings[componentType].Add("ComponentExists");
                }
                if (targetObject.TryGetComponent(out VRCStation station)) {
                    // even if box collider doesn't exist right now, the station script will create one at runtime
                    var boxColliderType = typeof(BoxCollider);
                    if (!animatableBindings.ContainsKey(boxColliderType)) {
                        animatableBindings[boxColliderType] = new HashSet<string>();
                    }
                    animatableBindings[boxColliderType].UnionWith(new string[] {
                        "ComponentExists", "m_IsTrigger", "m_Enabled",
                        "m_Center.x", "m_Center.y", "m_Center.z",
                        "m_Size.x", "m_Size.y", "m_Size.z"
                    });
                }
            }
            cache_IsAnimatableBinding[binding.path] = animatableBindings;
        }
        if (GetAllExcludedTransformPaths().Contains(binding.path)) {
            return true;
        }
        if (animatableBindings.Count == 0) {
            return false;
        }
        if (binding.propertyName.StartsWithSimple("material.") && TargetPathHasAnyMaterialSwap(binding.path)) {
            return true;
        }
        // only check for the property name when the type is a Renderer as GetAnimatableBindings seems to be very unreliable
        // otherwise only check if the component exists
        foreach (var kvp in animatableBindings) {
            if (binding.type.IsAssignableFrom(kvp.Key) && (!typeof(Renderer).IsAssignableFrom(binding.type) || kvp.Value.Contains(binding.propertyName))) {
                return true;
            }
        }

        return false;
    }

    private static readonly string DummyAnimationClipPrefix = "d4rkAO_DummyClip_";
        private Dictionary<(float length, float frameRate, bool isLooping), AnimationClip> cache_DummyAnimationClipBySettings = null;
        private static string GetDummyAnimationClipName((float length, float frameRate, bool isLooping) dummyClipInfo)
        {
            return $"{DummyAnimationClipPrefix}{dummyClipInfo.length:G9}_fr{dummyClipInfo.frameRate:G9}_loop{(dummyClipInfo.isLooping ? 1 : 0)}";
        }

        private static bool TryGetDummyAnimationClipInfo(Motion motion, out AnimationClip dummyClip, out (float length, float frameRate, bool isLooping) dummyClipInfo)
        {
            dummyClipInfo = default;
            dummyClip = motion as AnimationClip;
            if (dummyClip == null || string.IsNullOrEmpty(dummyClip.name) || !dummyClip.name.StartsWith(DummyAnimationClipPrefix))
                return false;
            dummyClipInfo = (dummyClip.length, dummyClip.frameRate, AnimationUtility.GetAnimationClipSettings(dummyClip).loopTime);
            return true;
    }

    private AnimationClip FixAnimationClipPaths(AnimationClip clip)
    {
        if (clip.name == "SynthosFxOptimizer_MergedLayers_Constants")
            return clip;
        var newClip = Instantiate(clip);
        newClip.ClearCurves();
        newClip.name = clip.name;
        bool changed = false;
        var removedBindings = new List<EditorCurveBinding>();
        var addedBindings = new List<EditorCurveBinding>();
        var changedBindings = new List<(EditorCurveBinding from, EditorCurveBinding to)>();
        float lastUsedKeyframeTime = -1;
        float lastUnusedKeyframeTime = -1;
        void SetFloatCurve(AnimationClip clipToSet, EditorCurveBinding bindingToSet, AnimationCurve curveToSet) {
            if (IsAnimatableBinding(bindingToSet)) {
                lastUsedKeyframeTime = Mathf.Max(curveToSet.length > 0 ? curveToSet.keys[^1].time : 0, lastUsedKeyframeTime);
                AnimationUtility.SetEditorCurve(clipToSet, bindingToSet, curveToSet);
            } else {
                lastUnusedKeyframeTime = Mathf.Max(curveToSet.length > 0 ? curveToSet.keys[^1].time : 0, lastUnusedKeyframeTime);
                changed = true;
                removedBindings.Add(bindingToSet);
            }
        }
        void SetObjectReferenceCurve(AnimationClip clipToSet, EditorCurveBinding bindingToSet, ObjectReferenceKeyframe[] curveToSet) {
            if (IsAnimatableBinding(bindingToSet)) {
                lastUsedKeyframeTime = Mathf.Max(curveToSet.Length > 0 ? curveToSet[^1].time : 0, lastUsedKeyframeTime);
                AnimationUtility.SetObjectReferenceCurve(clipToSet, bindingToSet, curveToSet);
            } else {
                lastUnusedKeyframeTime = Mathf.Max(curveToSet.Length > 0 ? curveToSet[^1].time : 0, lastUnusedKeyframeTime);
                changed = true;
                removedBindings.Add(bindingToSet);
            }
        }
        foreach (var binding in AnimationUtility.GetCurveBindings(clip))
        {
            var curve = AnimationUtility.GetEditorCurve(clip, binding);
            var fixedBinding = FixAnimationBinding(binding, ref changed);
            bool isRendererToggleBinding = (typeof(Renderer).IsAssignableFrom(binding.type) && binding.propertyName == "m_Enabled")
                || (binding.type == typeof(GameObject) && binding.propertyName == "m_IsActive");
            if (binding.type == typeof(GameObject) && binding.propertyName == "m_IsActive")
            {
                SetFloatCurve(newClip, FixAnimationBindingPath(binding, ref changed), curve);
            }
            if (fixedBinding != binding)
                changedBindings.Add((binding, fixedBinding));
            SetFloatCurve(newClip, fixedBinding, curve);
            if (isRendererToggleBinding && physBonesToDisable.TryGetValue(binding.path, out var physBonePaths))
            {
                var physBoneBinding = binding;
                physBoneBinding.propertyName = "m_Enabled";
                physBoneBinding.type = typeof(VRCPhysBone);
                foreach (var physBonePath in physBonePaths)
                {
                    physBoneBinding.path = physBonePath;
                    var b = FixAnimationBindingPath(physBoneBinding, ref changed);
                    SetFloatCurve(newClip, b, curve);
                    addedBindings.Add(b);
                    changed = true;
                }
            }
        }
        foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
        {
            var curve = AnimationUtility.GetObjectReferenceCurve(clip, binding);
            if (GetAllMaterialSwapBindingsToRemove().Contains(binding))
            {
                lastUnusedKeyframeTime = Mathf.Max(curve.Length > 0 ? curve[^1].time : 0, lastUnusedKeyframeTime);
                changed = true;
                removedBindings.Add(binding);
                continue;
            }
            for (int i = 0; i < curve.Length; i++)
            {
                var oldMat = curve[i].value as Material;
                if (oldMat == null)
                    continue;
                if (!int.TryParse(binding.propertyName.Substring(binding.propertyName.LastIndexOf('[') + 1).TrimEnd(']'), out int index))
                    continue;
                if (optimizedSlotSwapMaterials.TryGetValue((binding.path, index), out var newMats))
                {
                    if (newMats.TryGetValue(oldMat, out var newMat))
                    {
                        curve[i].value = newMat;
                        changed = true;
                    }
                }
            }
            var newBinding = FixAnimationBinding(binding, ref changed);
            SetObjectReferenceCurve(newClip, newBinding, curve);
        }
        if (lastUnusedKeyframeTime > lastUsedKeyframeTime && lastUnusedKeyframeTime > -1) {
            // add dummy curve referencing nothing to make sure the clip still has the same length as before
            var dummyBinding = EditorCurveBinding.FloatCurve("ThisHopefullyDoesntExist", typeof(GameObject), "m_IsActive");
            var dummyCurve = AnimationCurve.Constant(0, lastUnusedKeyframeTime, 1);
            AnimationUtility.SetEditorCurve(newClip, dummyBinding, dummyCurve);
            changed = true;
            if (lastUsedKeyframeTime == -1) {
                cache_DummyAnimationClipBySettings ??= new();
                var clipSettings = AnimationUtility.GetAnimationClipSettings(clip);
                var shouldCache = clipSettings.hasAdditiveReferencePose == false
                    && clipSettings.startTime == 0
                    && clipSettings.orientationOffsetY == 0
                    && clipSettings.level == 0
                    && clipSettings.cycleOffset == 0;
                if (shouldCache) {
                    var dummyClipKey = (length: lastUnusedKeyframeTime, clip.frameRate, isLooping: clipSettings.loopTime);
                    LogToFile($"- clip '{clip.name}' has no used keyframes but unused keyframes up to time {lastUnusedKeyframeTime}, using dummy clip ({dummyClipKey.frameRate} fps, looping: {dummyClipKey.isLooping})");
                    if (!cache_DummyAnimationClipBySettings.TryGetValue(dummyClipKey, out var dummyClip)) {
                        newClip.name = GetDummyAnimationClipName(dummyClipKey);
                        CreateUniqueAsset(newClip, newClip.name + ".anim");
                        cache_DummyAnimationClipBySettings[dummyClipKey] = dummyClip = newClip;
                    }
                    return dummyClip;
                } else {
                    LogToFile($"- clip '{clip.name}' has no used keyframes but unused keyframes up to time {lastUnusedKeyframeTime}, not using dummy clip because of non-default clip settings");
                    CreateUniqueAsset(newClip, newClip.name + ".anim");
                    return newClip;
                }
            }
        }
        if (changed)
        {
            LogToFile($"- clip '{clip.name}' got modified");
            foreach (var (from, to) in changedBindings)
            {
                if (from.type == to.type)
                {
                    LogToFile($"  * {from.path}.{from.propertyName} => {to.path}.{to.propertyName} ({from.type})");
                }
                else
                {
                    LogToFile($"  * {from.path}.{from.propertyName} ({from.type})  =>  {to.path}.{to.propertyName} ({to.type})");
                }
            }
            foreach (var binding in removedBindings)
            {
                LogToFile($"  - {binding.path}.{binding.propertyName} ({binding.type})");
            }
            foreach (var binding in addedBindings)
            {
                LogToFile($"  + {binding.path}.{binding.propertyName} ({binding.type})");
            }
            CreateUniqueAsset(newClip, newClip.name + ".anim");
            return newClip;
        }
        return clip;
    }

    private Motion FixMotion(Motion motion, Dictionary<(Motion motion, bool stateUsesWriteDefaults), Motion> fixedMotions, string assetPath, bool stateUsesWriteDefaults)
    {
        if (motion == null)
            return null;
        if (fixedMotions.TryGetValue((motion, stateUsesWriteDefaults), out var fixedMotionValue))
            return fixedMotionValue;
        if (motion is BlendTree oldTree)
        {
            var childNodes = oldTree.children;
            for (int j = 0; j < childNodes.Length; j++)
            {
                childNodes[j].motion = FixMotion(childNodes[j].motion, fixedMotions, assetPath, stateUsesWriteDefaults);
            }

            if (OptimizeFXLayer && childNodes.Length > 0)
            {
                bool allChildrenAreDummyClips = true;
                AnimationClip longestDummyClip = null;
                (float length, float frameRate, bool isLooping) longestDummyClipInfo = default;
                for (int j = 0; j < childNodes.Length; j++)
                {
                    if (!TryGetDummyAnimationClipInfo(childNodes[j].motion, out var dummyClip, out var dummyClipInfo))
                    {
                        allChildrenAreDummyClips = false;
                        break;
                    }
                    if (longestDummyClip == null || dummyClipInfo.length > longestDummyClipInfo.length)
                    {
                        longestDummyClip = dummyClip;
                        longestDummyClipInfo = dummyClipInfo;
                    }
                }
                if (allChildrenAreDummyClips)
                {
                    LogToFile($"- Replacing blend tree '{oldTree.name}' with dummy clip of length {longestDummyClipInfo.length} ({longestDummyClipInfo.frameRate} fps, looping: {longestDummyClipInfo.isLooping})");
                    return fixedMotions[(motion, stateUsesWriteDefaults)] = longestDummyClip;
                }
                if (stateUsesWriteDefaults && oldTree.blendType == BlendTreeType.Direct)
                {
                    int originalChildCount = childNodes.Length;
                    childNodes = childNodes.Where(child => !TryGetDummyAnimationClipInfo(child.motion, out _, out _)).ToArray();
                    if (childNodes.Length != originalChildCount)
                    {
                        LogToFile($"- Removed {originalChildCount - childNodes.Length} dummy clips from direct blend tree '{oldTree.name}'");
                    }
                }
            }

            var newTree = new BlendTree();
            newTree.name = oldTree.name;
            newTree.blendType = oldTree.blendType;
            newTree.blendParameter = oldTree.blendParameter;
            newTree.blendParameterY = oldTree.blendParameterY;
            newTree.minThreshold = oldTree.minThreshold;
            newTree.maxThreshold = oldTree.maxThreshold;
            newTree.useAutomaticThresholds = oldTree.useAutomaticThresholds;
            newTree.children = childNodes;
            fixedMotions[(motion, stateUsesWriteDefaults)] = newTree;
            newTree.hideFlags = HideFlags.HideInHierarchy;
            AnimatorOptimizer.CopyNormalizedBlendValuesProperty(oldTree, newTree);
            Profiler.StartSection("AssetDatabase.AddObjectToAsset()");
            AssetDatabase.AddObjectToAsset(newTree, assetPath);
            Profiler.EndSection();
            return newTree;
        }
        return motion;
    }

    private Dictionary<AvatarMask, AvatarMask> cache_FixAvatarMask = null;
    private AvatarMask FixAvatarMask(AvatarMask mask)
    {
        if (mask == null)
            return null;
        cache_FixAvatarMask ??= new Dictionary<AvatarMask, AvatarMask>();
        if (cache_FixAvatarMask.TryGetValue(mask, out var cachedMask))
            return cachedMask;
        var newMask = Instantiate(mask);
        newMask.name = mask.name;
        bool changed = false;
        List<string> logEntries = new();
        for (int i = 0; i < newMask.transformCount; i++)
        {
            var oldPath = newMask.GetTransformPath(i);
            if (transformFromOldPath.TryGetValue(oldPath, out var transform) && transform != null)
            {
                var newPath = GetPathToRoot(transform);
                if (newPath != oldPath)
                {
                    newMask.SetTransformPath(i, newPath);
                    changed = true;
                    logEntries.Add($"- '{oldPath}' => '{newPath}'");
                }
            }
        }
        if (changed)
        {
            CreateUniqueAsset(newMask, mask.name + ".mask");
            LogToFile($"- Changes to avatar mask '{mask.name}' ({logEntries.Count}):");
            foreach (var logEntry in logEntries)
            {
                LogToFile(logEntry, 1);
            }
            return cache_FixAvatarMask[mask] = newMask;
        }
        return cache_FixAvatarMask[mask] = mask;
    }
    
    private void FixAllAnimationPaths()
    {
        var avDescriptor = GetAvatarDescriptor();
        if (avDescriptor == null)
            return;
        
        int totalControllerCount = avDescriptor.baseAnimationLayers.Length + avDescriptor.specialAnimationLayers.Length;
        var layerCopyPaths = new string[totalControllerCount];
        var optimizedControllers = new AnimatorController[totalControllerCount];

        var fxLayersToMerge = new List<int>();
        var fxLayersToDestroy = new List<int>();
        var fxLayerMap = new Dictionary<int, int>();
        if (OptimizeFXLayer && GetFXLayer() != null)
        {
            var nonErrors = new HashSet<string>() {"toggle", "motion time", "blend tree", "multi toggle"};
            var analysisResult = AnalyzeFXLayerMergeAbility();
            var uselessLayers = FindUselessFXLayers();
            List<(int layerIndex, List<string> errors)> layersWithErrors = new();
            int currentLayer = 0;
            for (int i = 0; i < GetFXLayerLayers().Length; i++)
            {
                fxLayerMap[i] = currentLayer;
                if (uselessLayers.Contains(i))
                {
                    fxLayersToDestroy.Add(i);
                    continue;
                }
                if (analysisResult[i].All(e => nonErrors.Contains(e)))
                {
                    fxLayersToMerge.Add(i);
                    continue;
                }
                layersWithErrors.Add((i, analysisResult[i]));
                currentLayer++;
            }
            if (fxLayersToMerge.Count < 2 && fxLayersToDestroy.Count == 0)
            {
                fxLayersToMerge.Clear();
                fxLayerMap.Clear();
            }
            LogToFile($"Optimizing FX Layer with {GetFXLayerLayers().Length} original layers");
            using var _ = log.IndentScope();
            if (fxLayersToDestroy.Count > 0)
            {
                LogToFile($"- Removing {fxLayersToDestroy.Count} layers:");
                foreach (var layerIndex in fxLayersToDestroy)
                {
                    LogToFile($"- ({layerIndex}) {GetFXLayerLayers()[layerIndex].name}", 1);
                }
            }
            if (fxLayersToMerge.Count > 0)
            {
                LogToFile($"- Merging {fxLayersToMerge.Count} layers:");
                foreach (var layerIndex in fxLayersToMerge)
                {
                    LogToFile($"- {analysisResult[layerIndex][0],-12} ({layerIndex}) {GetFXLayerLayers()[layerIndex].name}", 1);
                }
            }
            if (layersWithErrors.Count > 0)
            {
                LogToFile($"- Cannot optimize {layersWithErrors.Count} layers:");
                foreach (var (layerIndex, errors) in layersWithErrors)
                {
                    LogToFile($"- {errors.Count,-3} reasons in ({layerIndex}) {GetFXLayerLayers()[layerIndex].name}", 1);
                    foreach (var error in errors)
                    {
                        LogToFile($"- {error}", 2);
                    }
                }
            }
        }

        Profiler.StartSection("AnimatorOptimizer.Run()");
        try
        {
            static string GetControllerFileName(AnimatorController controller)
            {
                string sanitized = SanitizeName(controller.name);
                if (sanitized.Length > 32)
                {
                    sanitized = sanitized[..30] + (sanitized.GetHashCode() & 0xFF).ToString("X");
                }
                return sanitized;
            }
            AssetDatabase.StartAssetEditing();
            List<(string msg, int indent)> messages = new();
            void LogMessages(string controllerName)
            {
                if (messages.Count == 0)
                    return;
                LogToFile($"Processing animator controller '{controllerName}':");
                foreach ((var message, int indent) in messages)
                {
                    LogToFile($"- {message}", 1 + indent);
                }
                messages.Clear();
            }
            for (int i = 0; i < avDescriptor.baseAnimationLayers.Length; i++)
            {
                var controller = avDescriptor.baseAnimationLayers[i].animatorController as AnimatorController;
                if (controller == null)
                    continue;
                layerCopyPaths[i] = $"{trashBinPath}c{i}_{GetControllerFileName(controller)}.controller";
                optimizedControllers[i] = controller == GetFXLayer()
                    ? AnimatorOptimizer.Run(controller, layerCopyPaths[i], fxLayerMap, messages, DeleteUnusedAnimatorParameters, fxLayersToMerge, fxLayersToDestroy, constantAnimatedValuesToAdd.Select(kvp => (kvp.Key, kvp.Value)).ToList())
                    : AnimatorOptimizer.Run(controller, layerCopyPaths[i], fxLayerMap, messages, DeleteUnusedAnimatorParameters);
                optimizedControllers[i].name = $"Base{i}_{GetControllerFileName(controller)}";
                avDescriptor.baseAnimationLayers[i].animatorController = optimizedControllers[i];
                LogMessages(controller.name);
            }
            for (int i = 0; i < avDescriptor.specialAnimationLayers.Length; i++)
            {
                var controller = avDescriptor.specialAnimationLayers[i].animatorController as AnimatorController;
                if (controller == null)
                    continue;
                var index = i + avDescriptor.baseAnimationLayers.Length;
                layerCopyPaths[index] = $"{trashBinPath}c{index}_{GetControllerFileName(controller)}.controller";
                optimizedControllers[index] = AnimatorOptimizer.Run(controller, layerCopyPaths[index], fxLayerMap, messages, DeleteUnusedAnimatorParameters);
                optimizedControllers[index].name = $"Special{index}_{GetControllerFileName(controller)}";
                avDescriptor.specialAnimationLayers[i].animatorController = optimizedControllers[index];
                LogMessages(controller.name);
            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
        }
        Profiler.EndSection();

        var animations = new HashSet<AnimationClip>();
        for (int i = 0; i < optimizedControllers.Length; i++)
        {
            if (optimizedControllers[i] == null)
                continue;
            animations.UnionWith(optimizedControllers[i].animationClips);
        }
        AddReferencePoseClips(animations);

        var fixedMotions = new Dictionary<(Motion motion, bool stateUsesWriteDefaults), Motion>();
        LogToFile($"Fixing animation paths in {animations.Count} animation clips");
        using (log.IndentScope())
        {
            using var _ = new Profiler.Section("FixAnimationClipPaths()");
            foreach (var clip in animations)
            {
                var fixedClip = FixAnimationClipPaths(clip);
                fixedMotions[(clip, false)] = fixedClip;
                fixedMotions[(clip, true)] = fixedClip;
            }
            bool changedReferencePoseClip = true;
            while (changedReferencePoseClip)
            {
                changedReferencePoseClip = false;
                foreach (var key in fixedMotions.Keys.Where(key => !key.stateUsesWriteDefaults).ToList())
                {
                    var sourceClip = key.motion as AnimationClip;
                    var currentFixedClip = fixedMotions[key] as AnimationClip;
                    var clipSettings = AnimationUtility.GetAnimationClipSettings(currentFixedClip);
                    if (clipSettings.additiveReferencePoseClip == null)
                        continue;
                    var sourceReferencePoseClip = clipSettings.additiveReferencePoseClip;
                    if (!fixedMotions.TryGetValue((sourceReferencePoseClip, false), out var fixedReferencePoseMotion))
                        continue;
                    var fixedReferencePoseClip = fixedReferencePoseMotion as AnimationClip;
                    if (sourceReferencePoseClip == fixedReferencePoseClip)
                        continue;
                    LogToFile($"- clip '{currentFixedClip.name}' updating additive reference pose clip '{sourceReferencePoseClip.name}'");
                    var rewrittenClip = Instantiate(currentFixedClip);
                    rewrittenClip.name = sourceClip.name;
                    clipSettings.additiveReferencePoseClip = fixedReferencePoseClip;
                    AnimationUtility.SetAnimationClipSettings(rewrittenClip, clipSettings);
                    CreateUniqueAsset(rewrittenClip, rewrittenClip.name + ".anim");
                    fixedMotions[(sourceClip, false)] = rewrittenClip;
                    fixedMotions[(sourceClip, true)] = rewrittenClip;
                    changedReferencePoseClip = true;
                }
            }
        }
        
        LogToFile($"Fixing animator controllers");
        for (int i = 0; i < optimizedControllers.Length; i++)
        {
            using var _ = log.IndentScope();
            var newController = optimizedControllers[i];
            if (newController == null)
                continue;

            LogToFile($"- '{newController.name}'");
            using var __ = log.IndentScope();

            var layers = newController.layers;
            bool applyLayerChanges = false;
            var states = layers.SelectMany(layer => layer.stateMachine.EnumerateAllStates()).ToArray();

            LogToFile($"- Fixing motions in {states.Length} states");
            using (log.IndentScope())
            foreach (var state in states)
            {
                state.motion = FixMotion(state.motion, fixedMotions, layerCopyPaths[i], state.writeDefaultValues);
            }

            var syncedLayerIndices = layers.Select((layer, index) => (layer, index)).Where(p => p.layer != null && p.layer.syncedLayerIndex >= 0).Select(p => p.index).ToArray();
            if (syncedLayerIndices.Length > 0)
                LogToFile($"- Fixing synced layers: {string.Join(", ", syncedLayerIndices)}");
            foreach (var syncedLayerIndex in syncedLayerIndices)
            {
                var syncedLayer = layers[syncedLayerIndex];
                foreach (var stateMotionPair in syncedLayer.EnumerateAllMotionOverrides())
                {
                    syncedLayer.SetOverrideMotion(stateMotionPair.state, FixMotion(stateMotionPair.motion, fixedMotions, layerCopyPaths[i], stateMotionPair.state.writeDefaultValues));
                    applyLayerChanges = true;
                }
            }

            foreach (var behavior in layers.SelectMany(layer => layer.stateMachine.EnumerateAllBehaviours()))
            {
                if (behavior is VRC.SDKBase.VRC_AnimatorPlayAudio playAudio)
                {
                    var path = playAudio.SourcePath ?? "";
                    if (transformFromOldPath.TryGetValue(path, out var transform) && transform != null)
                    {
                        playAudio.SourcePath = GetPathToRoot(transform);
                    }
                }
            }

            if (layers.Any(layer => layer.avatarMask != null))
            {
                LogToFile($"- Fixing avatar masks");
                using (log.IndentScope())
                foreach (var layer in layers)
                {
                    var oldMask = layer.avatarMask;
                    var newMask = FixAvatarMask(oldMask);
                    if (newMask != oldMask)
                    {
                        layer.avatarMask = newMask;
                        applyLayerChanges = true;
                    }
                }
            }

            if (applyLayerChanges)
            {
                newController.layers = layers;
            }
        }
        Profiler.StartSection("AssetDatabase.SaveAssets()");
        AssetDatabase.SaveAssets();
        Profiler.EndSection();
    }

    private HashSet<(string path, Type type)> GetAllCurveBindings(AnimatorStateMachine stateMachine)
    {
        var result = new HashSet<(string, Type)>();
        if (stateMachine == null)
            return result;
        foreach (var state in stateMachine.EnumerateAllStates())
        {
            if (state.motion == null)
                continue;
            foreach (var clip in state.motion.EnumerateAllClips())
            {
                var bindings = AnimationUtility.GetCurveBindings(clip);
                foreach (var binding in bindings)
                {
                    result.Add(($"{binding.path}.{binding.propertyName}", binding.type));
                }
                bindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
                foreach (var binding in bindings)
                {
                    result.Add(($"{binding.path}.{binding.propertyName}", binding.type));
                }
            }
        }
        return result;
    }

    private List<List<string>> cache_AnalyzeFXLayerMergeAbility;
    public List<List<string>> AnalyzeFXLayerMergeAbility()
    {
        if (cache_AnalyzeFXLayerMergeAbility != null)
            return cache_AnalyzeFXLayerMergeAbility;
        var fxLayer = GetFXLayer();
        if (fxLayer == null)
            return new List<List<string>>();

        var fxLayerLayers = GetFXLayerLayers();
        var errorMessages = fxLayerLayers.Select(layer => new List<string>()).ToList();

        foreach (var controller in GetAvDescriptorControllers())
        {
            var controllerLayers = controller.layers;
            for (int i = 0; i < controllerLayers.Length; i++)
            {
                var layer = controllerLayers[i];
                var stateMachine = layer.stateMachine;
                if (stateMachine == null)
                    continue;
                foreach (var behaviour in stateMachine.EnumerateAllBehaviours())
                {
                    if (behaviour is VRCAnimatorLayerControl layerControl)
                    {
                        if (layerControl.layer >= 0 && layerControl.layer < errorMessages.Count && layerControl.playable == BlendableLayer.FX)
                        {
                            errorMessages[layerControl.layer].Add($"layer control from {controller.name} {i} {layer.name}");
                        }
                    }
                }
            }
        }

        var fxLayerBindings = fxLayerLayers.Select(layer => GetAllCurveBindings(layer.stateMachine)).ToList();
        var uselessLayers = FindUselessFXLayers();
        var fxLayerParameters = fxLayer.parameters;
        var intParams = new HashSet<string>(fxLayerParameters.Where(p => p.type == AnimatorControllerParameterType.Int).Select(p => p.name));
        var intParamsWithNotEqualConditions = new HashSet<string>();

        for (int i = 0; i < fxLayerLayers.Length; i++) {
            if (uselessLayers.Contains(i)) {
                continue;
            }
            foreach (var condition in fxLayerLayers[i].stateMachine.EnumerateAllTransitions().SelectMany(t => t.conditions)) {
                if (condition.mode == AnimatorConditionMode.NotEqual && intParams.Contains(condition.parameter)) {
                    intParamsWithNotEqualConditions.Add(condition.parameter);
                }
            }
        }

        for (int i = 0; i < fxLayerLayers.Length; i++)
        {
            if (uselessLayers.Contains(i))
            {
                errorMessages[i].Add("useless");
                continue;
            }
            if (i <= 2 && MMDCompatibility)
            {
                errorMessages[i].Add("MMD compatibility requires the first 3 layers to be kept as is");
                continue;
            }
            var layer = fxLayerLayers[i];
            if (layer.syncedLayerIndex != -1)
            {
                errorMessages[i].Add($"synced with layer {layer.syncedLayerIndex}");
                if (layer.syncedLayerIndex >= 0 && layer.syncedLayerIndex < fxLayerLayers.Length)
                {
                    errorMessages[layer.syncedLayerIndex].Insert(0, $"layer {i} is synced with this layer");
                }
                continue;
            }
            var stateMachine = layer.stateMachine;
            if (stateMachine == null)
            {
                errorMessages[i].Add("no state machine");
                continue;
            }
            if (stateMachine.stateMachines.Length != 0)
            {
                errorMessages[i].Add($"{stateMachine.stateMachines.Length} sub state machines");
                continue;
            }
            if (stateMachine.EnumerateAllBehaviours().Any())
            {
                errorMessages[i].Add($"has state machine behaviours");
                continue;
            }
            if (stateMachine.entryTransitions.Length > 0)
            {
                errorMessages[i].Add($"has extra entry transitions");
                continue;
            }
            if (layer.defaultWeight != 1)
            {
                errorMessages[i].Add($"default weight {layer.defaultWeight}");
            }
            // The merged direct blend tree is an override layer without a mask, so additive or masked layers
            // would change behavior once merged. Rotations blend as quaternions in blend trees, which breaks
            // euler animations (spins, >180 degree turns), so those layers are kept as is too (same as VRCFury).
            if (layer.blendingMode == AnimatorLayerBlendingMode.Additive)
            {
                errorMessages[i].Add($"layer is additive");
                continue;
            }
            if (layer.avatarMask != null && fxLayerBindings[i].Any(b => b.type == typeof(Transform) || b.type == typeof(Animator)))
            {
                errorMessages[i].Add($"layer has an avatar mask and animates transforms or muscles");
                continue;
            }
            var rotationBinding = stateMachine.EnumerateAllStates()
                .Where(s => s.motion != null)
                .SelectMany(s => s.motion.EnumerateAllClips())
                .SelectMany(c => AnimationUtility.GetCurveBindings(c))
                .FirstOrDefault(b => b.type == typeof(Transform) && (b.propertyName.StartsWith("localEulerAngles") || b.propertyName.StartsWith("m_LocalRotation") || b.propertyName.StartsWith("m_LocalEuler")));
            if (rotationBinding.type != null)
            {
                errorMessages[i].Add($"{rotationBinding.path} animates transform rotation, which works differently in blend trees");
                continue;
            }
            var states = stateMachine.states;
            if (states.Length == 0)
            {
                errorMessages[i].Add($"{states.Length} states");
                continue;
            }

            bool IsStateConvertableToAnimationOrBlendTree(AnimatorState state) {
                if (state.motion == null) {
                    errorMessages[i].Add($"{state.name} has no motion");
                    return false;
                }
                if (state.motion is AnimationClip clip) {
                    if (state.timeParameterActive) {
                        // motion time approximation is lossy, so motion time layers are never merged
                        errorMessages[i].Add($"{state.name} has motion time");
                        return false;
                    } else {
                        // check if all curves are constant
                        var bindings = AnimationUtility.GetCurveBindings(clip);
                        foreach (var binding in bindings) {
                            var curve = AnimationUtility.GetEditorCurve(clip, binding);
                            if (curve.keys.Length <= 1)
                                continue;
                            var firstValue = curve.keys[0].value;
                            for (int j = 1; j < curve.keys.Length; j++) {
                                if (curve.keys[j].value != firstValue) {
                                    errorMessages[i].Add($"{state.name} has non-constant curve for {binding.propertyName}");
                                    return false;
                                }
                            }
                        }
                    }
                    if (AnimationUtility.GetObjectReferenceCurveBindings(clip).Any()) {
                        errorMessages[i].Add($"{state.name} has object reference curves");
                        return false;
                    }
                    return true;
                }
                if (state.motion is BlendTree blendTree) {
                    if (state.timeParameterActive) {
                        errorMessages[i].Add($"{state.name} is blend tree and has motion time");
                        return false;
                    }
                    if (!state.writeDefaultValues && blendTree.blendType == BlendTreeType.Direct) {
                        errorMessages[i].Add($"{state.name} is direct blend tree and does not have write defaults");
                        return false;
                    }
                    return true;
                }
                errorMessages[i].Add($"{state.name} is not null, animation clip or blend tree ???");
                return false;
            }
            bool DoesClipHaveExactlyTwoKeyframes(AnimationClip clip) {
                var bindings = AnimationUtility.GetCurveBindings(clip);
                float secondKeyframeTime = 0;
                foreach (var binding in bindings) {
                    var keys = AnimationUtility.GetEditorCurve(clip, binding).keys;
                    foreach (var key in keys) {
                        if (key.time == 0)
                            continue;
                        if (secondKeyframeTime == 0) {
                            secondKeyframeTime = key.time;
                        } else if (key.time != secondKeyframeTime) {
                            return false;
                        }
                    }
                }
                return true;
            }

            HashSet<EditorCurveBinding> GetAllMotionBindings(AnimatorState state) {
                return new HashSet<EditorCurveBinding>(state.motion.EnumerateAllClips().SelectMany(c => AnimationUtility.GetCurveBindings(c)));
            }

            if (states.Length == 1) {
                var state = states[0].state;
                if (!IsStateConvertableToAnimationOrBlendTree(state))
                    continue;
                errorMessages[i].Add(state.motion is AnimationClip ? $"motion time" : $"blend tree");
            }

            var param = states.SelectMany(s => s.state.transitions).Concat(stateMachine.anyStateTransitions).SelectMany(t => t.conditions).Select(c => c.parameter).Distinct().ToList();
            var paramLookup = param.ToDictionary(p => p, p => fxLayerParameters.FirstOrDefault(p2 => p2.name == p));
            if (paramLookup.Values.Any(p => p == null)) {
                errorMessages[i].Add($"didn't find parameter {paramLookup.First(p => p.Value == null).Key}");
                continue;
            }

            if (states.Length == 2) {
                var allTransitions = AnimatorOptimizer.GetNonEntryTransitions(stateMachine);
                var transitionDestinationIndices = allTransitions.Select(t => Array.FindIndex(states, s => s.state == t.destinationState)).ToList();
                if (transitionDestinationIndices.Any(i => i < 0 || i >= states.Length))
                {
                    errorMessages[i].Add($"a transition destination state is not in the states array");
                    continue;
                }
                var state0transitions = transitionDestinationIndices.Count(i => i == 0);
                var state1transitions = transitionDestinationIndices.Count(i => i == 1);
                if (state0transitions != 1 && state1transitions != 1)
                {
                    errorMessages[i].Add($"no single transition state");
                    continue;
                }
                var singleTransitionStateIndex = state0transitions == 1 ? 1 : 0;
                var orState = states[singleTransitionStateIndex].state;
                var andState = states[1 - singleTransitionStateIndex].state;
                var orStateTransitions = allTransitions.Where(t => t.destinationState == andState).ToArray();
                var andStateTransitions = allTransitions.Where(t => t.destinationState == orState).ToArray();
                var stateTransitions = singleTransitionStateIndex == 0
                    ? new [] { orStateTransitions, andStateTransitions }
                    : new [] { andStateTransitions, orStateTransitions };
                if (orStateTransitions[0].conditions.Length != andStateTransitions.Length)
                {
                    errorMessages[i].Add($"OR state has {orStateTransitions[0].conditions.Length} conditions but AND state has {andStateTransitions.Length} transitions");
                    continue;
                }
                if (andStateTransitions.Length == 0) {
                    errorMessages[i].Add($"AND state has no transitions");
                    continue;
                }
                if (andStateTransitions.Any(t => t.conditions.Length != 1)) {
                    errorMessages[i].Add($"a AND state transition has multiple conditions");
                    continue;
                }
                if (allTransitions.Any(t => t.mute || t.solo)) {
                    errorMessages[i].Add($"has a mute or solo transition");
                    continue;
                }
                bool conditionError = false;
                foreach (var condition in orStateTransitions[0].conditions) {
                    if (condition.mode == AnimatorConditionMode.Equals || condition.mode == AnimatorConditionMode.NotEqual) {
                        errorMessages[i].Add($"a transition condition mode is {condition.mode}");
                        conditionError = true;
                        break;
                    }
                    if (intParamsWithNotEqualConditions.Contains(condition.parameter)) {
                        errorMessages[i].Add($"parameter {condition.parameter} has a not equal condition somewhere");
                        conditionError = true;
                        break;
                    }
                    var inverseConditionMode = condition.mode == AnimatorConditionMode.If ? AnimatorConditionMode.IfNot : AnimatorConditionMode.If;
                    if (condition.mode == AnimatorConditionMode.Greater)
                        inverseConditionMode = AnimatorConditionMode.Less;
                    if (condition.mode == AnimatorConditionMode.Less)
                        inverseConditionMode = AnimatorConditionMode.Greater;
                    if ((condition.mode == AnimatorConditionMode.If || condition.mode == AnimatorConditionMode.IfNot)
                        && !andStateTransitions.Any(t => t.conditions.Any(c => c.parameter == condition.parameter && c.mode == inverseConditionMode))) {
                        errorMessages[i].Add($"condition with parameter {condition.parameter} has no inverse transition");
                        conditionError = true;
                        break;
                    }
                    if (paramLookup[condition.parameter].type == AnimatorControllerParameterType.Float) {
                        errorMessages[i].Add($"parameter {condition.parameter} is float");
                        conditionError = true;
                        break;
                    }
                    bool isInt = paramLookup[condition.parameter].type == AnimatorControllerParameterType.Int;
                    float inverseThreshold = condition.threshold + (isInt ? (condition.mode == AnimatorConditionMode.Greater ? 1 : -1) : 0);
                    if ((condition.mode == AnimatorConditionMode.Greater || condition.mode == AnimatorConditionMode.Less)
                        && !andStateTransitions.Any(t => t.conditions.Any(c => c.parameter == condition.parameter && c.mode == inverseConditionMode && c.threshold == inverseThreshold))) {
                        errorMessages[i].Add($"condition with parameter {condition.parameter} has no inverse transition");
                        conditionError = true;
                        break;
                    }
                }
                if (conditionError) {
                    continue;
                }

                bool reliesOnWriteDefaults = false;
                for (int j = 0; j < 2; j++) {
                    var state = states[j].state;
                    var otherState = states[1 - j].state;
                    if (stateTransitions[j].Any(t => t.destinationState != otherState)) {
                        errorMessages[i].Add($"{state} transition destination state is not the other state");
                        break;
                    }
                    if (stateTransitions[j].Any(t => t.hasExitTime && t.exitTime != 0.0f)) {
                        errorMessages[i].Add($"{state} transition has exit time");
                        break;
                    }
                    if (AnimatorOptimizer.IsNullOrEmpty(state.motion)) {
                        if (AnimatorOptimizer.IsNullOrEmpty(otherState.motion)) {
                            errorMessages[i].Add($"both states have no motion or are empty clips");
                            break;
                        } else if (!(otherState.motion is AnimationClip)) {
                            errorMessages[i].Add($"state {j} has no motion or an empty clip but {1 - j} has non animation clip motion");
                            break;
                        } else if (!otherState.writeDefaultValues || !state.writeDefaultValues) {
                            errorMessages[i].Add($"state {j} has no motion or an empty clip but states do not have write defaults");
                            break;
                        } else {
                            reliesOnWriteDefaults = true;
                        }
                        continue;
                    } else if (state.motion == otherState.motion) {
                        if (state.motion is AnimationClip clip) {
                            if (AnimationUtility.GetObjectReferenceCurveBindings(clip).Any()) {
                                errorMessages[i].Add($"both states use the same motion but it has object reference curves");
                                break;
                            }
                            if (!DoesClipHaveExactlyTwoKeyframes(clip)) {
                                errorMessages[i].Add($"both states use the same motion but it doesn't have exactly 2 keyframes");
                                break;
                            }
                            if (states.Any(s => s.state.speedParameterActive || s.state.timeParameterActive)) {
                                errorMessages[i].Add($"both states use the same motion but at least one state has speed or time parameter active");
                                break;
                            }
                            if (!((state.speed > 0 && otherState.speed < 0) || (state.speed < 0 && otherState.speed > 0))) {
                                errorMessages[i].Add($"both states use the same motion but neither has negative speed");
                                break;
                            }
                        } else {
                            errorMessages[i].Add($"both states use the same motion but it's not an animation clip");
                            break;
                        }
                    } else if (!IsStateConvertableToAnimationOrBlendTree(state)) {
                        break;
                    }
                }
                bool onlyBoolBindings = true;
                foreach (var binding in fxLayerBindings[i]) {
                    if (!binding.path.EndsWith("m_Enabled") && !binding.path.EndsWith("m_IsActive")) {
                        onlyBoolBindings = false;
                        break;
                    }
                }
                if (!reliesOnWriteDefaults) {
                    var bindings0 = GetAllMotionBindings(states[0].state);
                    var bindings1 = GetAllMotionBindings(states[1].state);
                    if (!bindings1.SetEquals(bindings0)) {
                        bindings1.Except(bindings0).Concat(bindings0.Except(bindings1)).ToList()
                            .ForEach(b => errorMessages[i].Add($"{b.path}/{b.propertyName} is not animated in both states"));
                        continue;
                    }
                }
                if (reliesOnWriteDefaults && !onlyBoolBindings) {
                    errorMessages[i].Add($"relies on write defaults and animates something other than m_Enabled/m_IsActive");
                }
                if (stateTransitions.Any(s => s.Any(t => t.duration != 0.0f)) && !onlyBoolBindings) {
                    errorMessages[i].Add($"transition has non 0 duration and animates something other than m_Enabled/m_IsActive");
                }
                errorMessages[i].Add($"toggle");
            }

            if (states.Length > 2) {
                if (paramLookup.Count != 1 || (paramLookup.Count == 1 && paramLookup.First().Value.type != AnimatorControllerParameterType.Int)) {
                    errorMessages[i].Add($"more than 2 states and not a single int parameter");
                    continue;
                }
                if (states.Any(s => s.state.transitions.Length != 0)) {
                    errorMessages[i].Add($"more than 2 states and a state has transitions");
                    continue;
                }
                if (intParamsWithNotEqualConditions.Contains(paramLookup.First().Key)) {
                    errorMessages[i].Add($"parameter {paramLookup.First().Key} has a not equal condition somewhere");
                    continue;
                }
                if (stateMachine.anyStateTransitions.Length != states.Length) {
                    errorMessages[i].Add($"any state transitions length is not the same as states length");
                    continue;
                }
                if (stateMachine.anyStateTransitions.Any(t => t.conditions.Length != 1)) {
                    errorMessages[i].Add($"a transition has multiple conditions");
                    continue;
                }
                if (stateMachine.anyStateTransitions.Any(t => (t.hasExitTime && t.exitTime != 0.0f) || t.duration != 0.0f)) {
                    errorMessages[i].Add($"a transition has non 0 exit time or duration");
                    continue;
                }
                var thresholdsNeeded = Enumerable.Range(0, states.Length).Select(number => (float)number).ToList();
                if (thresholdsNeeded.Any(t => !stateMachine.anyStateTransitions.Any(tr => tr.conditions[0].threshold == t && tr.conditions[0].mode == AnimatorConditionMode.Equals))) {
                    errorMessages[i].Add($"missing some transition with correct threshold and condition mode");
                    continue;
                }
                if (states.Any(s => s.state.motion == null)) {
                    errorMessages[i].Add($"a state has no motion");
                    continue;
                }
                if (states.Any(s => !IsStateConvertableToAnimationOrBlendTree(s.state))) {
                    continue;
                }
                var firstBindings = GetAllMotionBindings(states[0].state);
                bool hadError = false;
                for (int j = 1; j < states.Length; j++) {
                    var bindings = GetAllMotionBindings(states[j].state);
                    if (!bindings.SetEquals(firstBindings)) {
                        bindings.Except(firstBindings).Concat(firstBindings.Except(bindings)).ToList()
                            .ForEach(b => errorMessages[i].Add($"{b.path}/{b.propertyName} is not animated in all states"));
                        hadError = true;
                        break;
                    }
                }
                if (hadError) {
                    continue;
                }
                errorMessages[i].Add($"multi toggle");
            }

            for (int j = 0; j < fxLayerLayers.Length; j++) {
                if (i == j || uselessLayers.Contains(j))
                    continue;
                foreach (var binding in fxLayerBindings[i]) {
                    if (fxLayerBindings[j].Contains(binding)) {
                        errorMessages[i].Add($"{binding.path} is used in {j} {fxLayerLayers[j].name}");
                    }
                }
            }
        }
        for (int i = 0; i < errorMessages.Count; i++)
        {
            errorMessages[i] = errorMessages[i].Distinct().ToList();
        }
        return cache_AnalyzeFXLayerMergeAbility = errorMessages;
    }

    private HashSet<int> cache_FindUselessFXLayers = null;
    public HashSet<int> FindUselessFXLayers()
    {
        if (cache_FindUselessFXLayers != null)
            return cache_FindUselessFXLayers;
        var fxLayer = GetFXLayer();
        if (fxLayer == null || !OptimizeFXLayer)
            return new HashSet<int>();
        Profiler.StartSection("FindUselessFXLayers()");

        var isAffectedByLayerWeightControl = new HashSet<int>();
        foreach (var controller in GetAvDescriptorControllers())
        {
            var controllerLayers = controller.layers;
            for (int i = 0; i < controllerLayers.Length; i++)
            {
                var stateMachine = controllerLayers[i].stateMachine;
                if (stateMachine == null)
                    continue;
                foreach (var behaviour in stateMachine.EnumerateAllBehaviours())
                {
                    if (behaviour is VRCAnimatorLayerControl layerControl && layerControl.playable == BlendableLayer.FX)
                    {
                        isAffectedByLayerWeightControl.Add(layerControl.layer);
                    }
                }
            }
        }

        var uselessLayers = new HashSet<int>();

        var possibleBindingTypes = new Dictionary<string, Type[]>();
        bool IsPossibleBinding(EditorCurveBinding binding)
        {
            if (!possibleBindingTypes.TryGetValue(binding.path, out var possibleTypes))
            {
                var uniquePossibleTypes = new HashSet<Type>();
                var transform = GetTransformFromPath(binding.path);
                if (transform != null)
                {
                    // AnimationUtility.GetAnimatableBindings(transform.gameObject, gameObject)
                    // is too slow, so we just check if the components mentioned in the bindings exist at that path
                    uniquePossibleTypes.UnionWith(transform.GetNonNullComponents().Select(c => c.GetType()));
                    uniquePossibleTypes.Add(typeof(GameObject));
                }
                possibleTypes = possibleBindingTypes[binding.path] = uniquePossibleTypes.ToArray();
            }
            return possibleTypes.Any(t => binding.type.IsAssignableFrom(t));
        }

        var fxLayerLayers = GetFXLayerLayers();
        int lastNonUselessLayer = fxLayerLayers.Length;
        for (int i = fxLayerLayers.Length - 1; i >= 0; i--)
        {
            if (i <= 2 && MMDCompatibility)
                break;
            var layer = fxLayerLayers[i];
            if (layer.syncedLayerIndex != -1)
                continue;
            bool isNotFirstLayerOrLastNonUselessLayerCanBeFirst = i != 0 ||
                (lastNonUselessLayer < fxLayerLayers.Length && fxLayerLayers[lastNonUselessLayer].avatarMask == layer.avatarMask
                    && fxLayerLayers[lastNonUselessLayer].defaultWeight == 1 && !isAffectedByLayerWeightControl.Contains(lastNonUselessLayer));
            var stateMachine = layer.stateMachine;
            if (stateMachine == null || (stateMachine.stateMachines.Length == 0 && stateMachine.states.Length == 0))
            {
                if (isNotFirstLayerOrLastNonUselessLayerCanBeFirst)
                {
                    uselessLayers.Add(i);
                }
                continue;
            }
            if (i == 0 || stateMachine.EnumerateAllBehaviours().Any())
            {
                lastNonUselessLayer = i;
                continue;
            }
            if (layer.defaultWeight == 0 && !isAffectedByLayerWeightControl.Contains(i))
            {
                uselessLayers.Add(i);
                continue;
            }
            var clips = stateMachine.EnumerateAllStates()
                .SelectMany(s => s.motion == null ? new AnimationClip[0] : s.motion.EnumerateAllClips()).Distinct().ToList();
            var layerBindings = clips.SelectMany(c => AnimationUtility.GetCurveBindings(c).Concat(AnimationUtility.GetObjectReferenceCurveBindings(c))).Distinct();
            if (layerBindings.All(b => IsMaterialSwapBinding(b) ? ShouldRemoveMaterialSwapBinding(b) : !IsPossibleBinding(b)))
            {
                uselessLayers.Add(i);
                continue;
            }
            lastNonUselessLayer = i;
        }
        for (int i = 0; i < fxLayerLayers.Length; i++)
        {
            if (fxLayerLayers[i].syncedLayerIndex != -1)
            {
                uselessLayers.Remove(fxLayerLayers[i].syncedLayerIndex);
            }
        }
        Profiler.EndSection();
        return cache_FindUselessFXLayers = uselessLayers;
    }

    private HashSet<AnimationClip> cache_GetAllUsedFXLayerAnimationClips = null;
    private HashSet<AnimationClip> GetAllUsedFXLayerAnimationClips()
    {
        if (cache_GetAllUsedFXLayerAnimationClips != null)
            return cache_GetAllUsedFXLayerAnimationClips;
        var fxLayer = GetFXLayer();
        if (fxLayer == null)
            return new HashSet<AnimationClip>();
        var unusedLayers = FindUselessFXLayers();
        var usedClips = new HashSet<AnimationClip>();
        var fxLayerLayers = GetFXLayerLayers();
        for (int i = 0; i < fxLayerLayers.Length; i++)
        {
            usedClips.UnionWith(fxLayerLayers[i].EnumerateAllMotionOverrides().Select(p => p.motion as AnimationClip).Where(c => c != null));
            var stateMachine = fxLayerLayers[i].stateMachine;
            if (stateMachine == null || unusedLayers.Contains(i))
                continue;
            foreach (var state in stateMachine.EnumerateAllStates())
            {
                if (state.motion == null)
                    continue;
                usedClips.UnionWith(state.motion.EnumerateAllClips());
            }
        }
        return cache_GetAllUsedFXLayerAnimationClips = usedClips;
    }

    private void AddReferencePoseClips(HashSet<AnimationClip> clips)
    {
        void AddReferencePoseClips(AnimationClip clip)
        {
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            if (settings.additiveReferencePoseClip != null
                && clips.Add(settings.additiveReferencePoseClip))
            {
                AddReferencePoseClips(settings.additiveReferencePoseClip);
            }
        }
        foreach (var clip in clips.ToArray())
        {
            AddReferencePoseClips(clip);
        }
    }

    private HashSet<AnimationClip> cache_GetAllUsedAnimationClips = null;
    private HashSet<AnimationClip> GetAllUsedAnimationClips()
    {
        if (cache_GetAllUsedAnimationClips != null)
            return cache_GetAllUsedAnimationClips;
        var usedClips = new HashSet<AnimationClip>();
        var fxLayer = GetFXLayer();
        foreach (var controller in GetAvDescriptorControllers())
        {
            if (controller == fxLayer)
                continue;
            usedClips.UnionWith(controller.animationClips);
        }
        usedClips.UnionWith(GetAllUsedFXLayerAnimationClips());
        AddReferencePoseClips(usedClips);
        return cache_GetAllUsedAnimationClips = usedClips;
    }

    private HashSet<EditorCurveBinding> cache_GetAllUsedCurveBindings = null;
    private HashSet<EditorCurveBinding> GetAllUsedCurveBindings()
    {
        if (cache_GetAllUsedCurveBindings != null)
            return cache_GetAllUsedCurveBindings;
        var result = new HashSet<EditorCurveBinding>();
        foreach (var clip in GetAllUsedAnimationClips())
        {
            result.UnionWith(AnimationUtility.GetCurveBindings(clip));
            result.UnionWith(AnimationUtility.GetObjectReferenceCurveBindings(clip));
        }
        return cache_GetAllUsedCurveBindings = result;
    }

    private Dictionary<VRCPhysBoneBase, HashSet<Object>> cache_FindAllPhysBoneDependencies = null;
    public Dictionary<VRCPhysBoneBase, HashSet<Object>> FindAllPhysBoneDependencies()
    {
        if (cache_FindAllPhysBoneDependencies != null)
            return cache_FindAllPhysBoneDependencies;
        var result = new Dictionary<VRCPhysBoneBase, HashSet<Object>>();
        var physBonePath = new Dictionary<string, VRCPhysBoneBase>();
        var avDescriptor = GetAvatarDescriptor();
        var physBones = avDescriptor.GetComponentsInChildren<VRCPhysBoneBase>(true);
        foreach (var physBone in physBones)
        {
            result.Add(physBone, new HashSet<Object>());
            physBonePath[GetPathToRoot(physBone)] = physBone;
        }
        var parameterSuffixes = new string[] { "_IsGrabbed", "_IsPosed", "_Angle", "_Stretch", "_Squish" };
        foreach (var controller in GetAvDescriptorControllers())
        {
            var parameterNames = new HashSet<string>(controller.parameters.Select(p => p.name));
            foreach (var physBone in physBones)
            {
                if (parameterSuffixes.Any(s => parameterNames.Contains(physBone.parameter + s)))
                {
                    result[physBone].Add(controller);
                }
            }
        }
        foreach (var clip in GetAllUsedAnimationClips())
        {
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (binding.propertyName == "m_Enabled" && typeof(VRCPhysBoneBase).IsAssignableFrom(binding.type) && physBonePath.TryGetValue(binding.path, out var physBone))
                {
                    result[physBone].Add(clip);
                }
            }
        }
        var transformToDependency = new Dictionary<Transform, HashSet<Object>>();
        void AddDependency(Transform t, Object obj)
        {
            if (t == null)
                return;
            if (!transformToDependency.TryGetValue(t, out var dependencies))
            {
                transformToDependency[t] = dependencies = new HashSet<Object>();
            }
            dependencies.Add(obj);
        }
        foreach (var skinnedMesh in avDescriptor.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (skinnedMesh.bones.Length == 0)
            {
                var root = skinnedMesh.rootBone != null ? skinnedMesh.rootBone : skinnedMesh.transform;
                AddDependency(skinnedMesh.rootBone, skinnedMesh);
                continue;
            }
            if (skinnedMesh.sharedMesh == null)
                continue;
            var meshBones = skinnedMesh.bones;
            var usedBoneIDs = new bool[meshBones.Length];
            var boneWeights = skinnedMesh.sharedMesh.boneWeights;
            int outOfRangeBoneCount = 0;
            void MarkUsedBone(int boneIndex) {
                if (boneIndex < 0 || boneIndex >= meshBones.Length) {
                    outOfRangeBoneCount++;
                    return;
                }
                usedBoneIDs[boneIndex] = true;
            }
            for (int i = 0; i < boneWeights.Length; i++)
            {
                MarkUsedBone(boneWeights[i].boneIndex0);
                if (boneWeights[i].weight1 > 0) {
                    MarkUsedBone(boneWeights[i].boneIndex1);
                    if (boneWeights[i].weight2 > 0) {
                        MarkUsedBone(boneWeights[i].boneIndex2);
                        if (boneWeights[i].weight3 > 0) {
                            MarkUsedBone(boneWeights[i].boneIndex3);
                        }
                    }
                }
            }
            for (int i = 0; i < usedBoneIDs.Length; i++)
            {
                if (usedBoneIDs[i])
                {
                    AddDependency(meshBones[i], skinnedMesh);
                }
            }
            if (outOfRangeBoneCount > 0)
            {
                Debug.LogWarning($"Skinned mesh renderer {GetPathToRoot(skinnedMesh)} has {outOfRangeBoneCount} out of range bone indices");
            }
        }
        foreach (var behavior in avDescriptor.GetComponentsInChildren<Behaviour>(true)
            .Where(b => b != null && (b.GetType().Name.Contains("Constraint") || b.GetType().FullName.StartsWithSimple("RootMotion.FinalIK"))))
        {
            foreach (var t in FindReferencedTransforms(behavior))
            {
                AddDependency(t, behavior);
            }
        }
        foreach (var skinnedRenderer in avDescriptor.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            AddDependency(skinnedRenderer.rootBone, skinnedRenderer);
        }
        foreach (var renderer in avDescriptor.GetComponentsInChildren<Renderer>(true))
        {
            AddDependency(renderer.probeAnchor, renderer);
            AddDependency(renderer.transform, renderer);
        }
        foreach (var contact in avDescriptor.GetComponentsInChildren<ContactBase>(true))
        {
            AddDependency(contact.GetRootTransform(), contact);
        }

        var componentTypesToIgnore = new HashSet<string>() {
            "UnityEngine.Transform",
            "nadena.dev.ndmf.multiplatform.components.PortableDynamicBone",
            "nadena.dev.ndmf.multiplatform.components.PortableDynamicBoneCollider",
            "VRC.Dynamics.ParentChangeDetector",
        };

        foreach (var physBone in physBones)
        {
            var root = physBone.GetRootTransform();
            foreach (Transform current in root.GetAllDescendants().Concat(new Transform[] { root }))
            {
                if (transformToDependency.TryGetValue(current, out var dependencies))
                {
                    result[physBone].UnionWith(dependencies);
                }
                result[physBone].UnionWith(current.GetNonNullComponents()
                    .Where(c => c != physBone && !componentTypesToIgnore.Contains(c.GetType().FullName)));
            }
        }

        return cache_FindAllPhysBoneDependencies = result;
    }

    public Dictionary<string, List<string>> FindAllPhysBonesToDisable()
    {
        var result = new Dictionary<string, List<string>>();
        if (!DisablePhysBonesWhenUnused)
            return result;
        var physBoneDependencies = FindAllPhysBoneDependencies();
        foreach (var dependencies in physBoneDependencies.Values)
        {
            dependencies.RemoveWhere(o => o == null);
        }
        if (physBoneDependencies.Count > 0)
        {
            LogToFile($"PhysBone dependencies:");
        }
        foreach ((var physBone, var dependencies) in physBoneDependencies)
        {
            if (physBone == null)
                continue;
            if (dependencies.Count(o => !(o is AnimatorController)) == 1 && dependencies.First(o => !(o is AnimatorController)) is SkinnedMeshRenderer target)
            {
                var targetPath = GetPathToRoot(target);
                if (!result.TryGetValue(targetPath, out var physBonePaths))
                {
                    result[targetPath] = physBonePaths = new List<string>();
                }
                physBonePaths.Add(GetPathToRoot(physBone));
                continue;
            }
            LogToFile($"- '{GetPathToRoot(physBone)}' has {dependencies.Count} dependencies:", 1);
            foreach (var dependency in dependencies)
            {
                LogToFile($"- {dependency.GetType().Name}: '{dependency.name}'", 2);
            }
        }
        if (result.Count > 0)
        {
            LogToFile($"Found {result.Count} meshes with exclusive physbone dependencies:");
            foreach ((var meshPath, var physBonePaths) in result)
            {
                LogToFile($"- {physBonePaths.Count} physbones owned by '{meshPath}':", 1);
                foreach (var physBonePath in physBonePaths)
                {
                    LogToFile($"- {physBonePath}", 2);
                }
            }
        }
        return result;
    }

    private bool IsMaterialSwapBinding(EditorCurveBinding binding)
    {
        return typeof(Renderer).IsAssignableFrom(binding.type)
            && binding.propertyName.StartsWithSimple("m_Materials.Array.data[")
            && binding.propertyName.EndsWith("]");
    }

    private Dictionary<EditorCurveBinding, bool> cache_MaterialSwapBindingsToRemove = null;
    private bool ShouldRemoveMaterialSwapBinding(EditorCurveBinding binding)
    {
        cache_MaterialSwapBindingsToRemove ??= new Dictionary<EditorCurveBinding, bool>();
        if (cache_MaterialSwapBindingsToRemove.TryGetValue(binding, out var result))
            return result;
        int slot = int.Parse(binding.propertyName.Substring("m_Materials.Array.data[".Length, binding.propertyName.Length - "m_Materials.Array.data[]".Length));
        var renderer = GetTransformFromPath(binding.path)?.GetComponent<Renderer>();
        return cache_MaterialSwapBindingsToRemove[binding]
            = renderer == null || renderer.sharedMaterials.Length <= slot;
    }

    private HashSet<EditorCurveBinding> cache_GetAllMaterialSwapBindingsToRemove = null;
    private HashSet<EditorCurveBinding> GetAllMaterialSwapBindingsToRemove()
    {
        if (cache_GetAllMaterialSwapBindingsToRemove != null)
            return cache_GetAllMaterialSwapBindingsToRemove;
        var result = new HashSet<EditorCurveBinding>();
        if (cache_MaterialSwapBindingsToRemove != null)
        {
            foreach (var entry in cache_MaterialSwapBindingsToRemove)
            {
                if (entry.Value)
                    result.Add(entry.Key);
            }
        }
        return cache_GetAllMaterialSwapBindingsToRemove = result;
    }

    private Dictionary<(string path, int index), HashSet<Material>> cache_FindAllMaterialSwapMaterials;
    public Dictionary<(string path, int index), HashSet<Material>> FindAllMaterialSwapMaterials()
    {
        if (cache_FindAllMaterialSwapMaterials != null)
            return cache_FindAllMaterialSwapMaterials;
        var result = new Dictionary<(string path, int index), HashSet<Material>>();
        foreach (var clip in GetAllUsedAnimationClips())
        {
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                if (!IsMaterialSwapBinding(binding))
                    continue;
                int start = binding.propertyName.IndexOf('[') + 1;
                int end = binding.propertyName.IndexOf(']') - start;
                int slot = int.Parse(binding.propertyName.Substring(start, end));
                if (ShouldRemoveMaterialSwapBinding(binding))
                    continue;
                var index = (binding.path, slot);
                var curve = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                var curveMaterials = curve.Select(c => c.value as Material).Where(m => m != null).Distinct().ToList();
                if (!result.TryGetValue(index, out var materials))
                {
                    result[index] = materials = new HashSet<Material>();
                }
                materials.UnionWith(curveMaterials);
            }
        }
        return cache_FindAllMaterialSwapMaterials = result;
    }

    public int GetBaseLayerCount()
    {
        var av = GetAvatarDescriptor();
        if (av == null)
            return 0;
        if (!av.TryGetComponent<Animator>(out var animator) || animator == null)
            return 5;
        if (animator.avatar == null || !animator.avatar.isHuman)
            return 3;
        return 5;
    }

    public AnimatorController GetFXLayer()
    {
        var avDescriptor = GetAvatarDescriptor();
        var baseLayerCount = GetBaseLayerCount();
        if (avDescriptor == null || avDescriptor.baseAnimationLayers.Length != baseLayerCount)
            return null;
        return avDescriptor.baseAnimationLayers[baseLayerCount - 1].animatorController as AnimatorController;
    }

    private AnimatorControllerLayer[] cache_GetFXLayerLayers = null;
    public AnimatorControllerLayer[] GetFXLayerLayers()
    {
        if (cache_GetFXLayerLayers != null)
            return cache_GetFXLayerLayers;
        var fxLayer = GetFXLayer();
        return cache_GetFXLayerLayers = fxLayer != null ? fxLayer.layers : new AnimatorControllerLayer[0];
    }

    public void CalculateUsedBlendShapePaths()
    {
        usedBlendShapes.Clear();
        blendShapesToBake.Clear();
        var avDescriptor = GetAvatarDescriptor();
        var (faceTrackingMeshes, faceTrackingNames) = GetFaceTrackingSetup();
        foreach (var renderer in faceTrackingMeshes)
        {
            var mesh = renderer.sharedMesh;
            if (mesh == null)
                continue;
            string path = GetPathToRoot(renderer) + "/blendShape.";
            foreach (var name in faceTrackingNames)
            {
                if (mesh.GetBlendShapeIndex(name) >= 0)
                    usedBlendShapes.Add(path + name);
            }
        }
        if (avDescriptor.lipSync == VRC.SDKBase.VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShape
            && avDescriptor.VisemeSkinnedMesh != null)
        {
            var meshRenderer = avDescriptor.VisemeSkinnedMesh;
            string path = GetPathToRoot(meshRenderer) + "/blendShape.";
            foreach (var blendShapeName in avDescriptor.VisemeBlendShapes)
            {
                usedBlendShapes.Add(path + blendShapeName);
            }
        }
        if (avDescriptor.lipSync == VRC.SDKBase.VRC_AvatarDescriptor.LipSyncStyle.JawFlapBlendShape
            && avDescriptor.VisemeSkinnedMesh != null)
        {
            var meshRenderer = avDescriptor.VisemeSkinnedMesh;
            string path = GetPathToRoot(meshRenderer) + "/blendShape.";
            usedBlendShapes.Add(path + avDescriptor.MouthOpenBlendShapeName);
        }
        if (avDescriptor.customEyeLookSettings.eyelidType == VRCAvatarDescriptor.EyelidType.Blendshapes
            && avDescriptor.customEyeLookSettings.eyelidsSkinnedMesh != null
            && avDescriptor.customEyeLookSettings.eyelidsSkinnedMesh.sharedMesh != null)
        {
            var meshRenderer = avDescriptor.customEyeLookSettings.eyelidsSkinnedMesh;
            string path = GetPathToRoot(meshRenderer) + "/blendShape.";
            foreach (var blendShapeID in avDescriptor.customEyeLookSettings.eyelidsBlendshapes)
            {
                if (blendShapeID >= 0 && blendShapeID < meshRenderer.sharedMesh.blendShapeCount)
                {
                    usedBlendShapes.Add(path + meshRenderer.sharedMesh.GetBlendShapeName(blendShapeID));
                }
            }
        }
        foreach (var clip in GetAllUsedAnimationClips())
        {
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (binding.type != typeof(SkinnedMeshRenderer)
                    || !binding.propertyName.StartsWithSimple("blendShape."))
                    continue;
                var t = GetTransformFromPath(binding.path);
                if (t == null)
                    continue;
                if (!t.TryGetComponent<SkinnedMeshRenderer>(out var smr))
                    continue;
                var mesh = smr.sharedMesh;
                if (mesh == null)
                    continue;
                var blendShapeName = binding.propertyName.Substring("blendShape.".Length);
                var blendShapeID = mesh.GetBlendShapeIndex(blendShapeName);
                if (blendShapeID < 0)
                    continue;
                var keyframes = AnimationUtility.GetEditorCurve(clip, binding).keys;
                if (keyframes.All(k => k.value == 0) && smr.GetBlendShapeWeight(blendShapeID) == 0)
                    continue;
                usedBlendShapes.Add(binding.path + "/" + binding.propertyName);
            }
        }
        foreach (var skinnedMeshRenderer in avDescriptor.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            var mesh = skinnedMeshRenderer.sharedMesh;
            if (mesh == null)
                continue;
            var blendShapeIDsToBake = new List<int>();
            blendShapesToBake[skinnedMeshRenderer] = blendShapeIDsToBake;
            string path = GetPathToRoot(skinnedMeshRenderer) + "/blendShape.";
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                var name = mesh.GetBlendShapeName(i);
                if (MMDCompatibility && MMDBlendShapes.Contains(name))
                {
                    usedBlendShapes.Add(path + name);
                    continue;
                }
                if (skinnedMeshRenderer.GetBlendShapeWeight(i) != 0 && !usedBlendShapes.Contains(path + name))
                {
                    if (mesh.GetBlendShapeFrameCount(i) > 1)
                    {
                        usedBlendShapes.Add(path + name);
                    }
                    else
                    {
                        blendShapeIDsToBake.Add(i);
                    }
                }
            }
        }
    }

    public HashSet<string> GetUsedBlendShapePaths()
    {
        return new HashSet<string>(usedBlendShapes);
    }

    public List<List<(string blendshape, float value)>> FindMergeableBlendShapes(IEnumerable<Renderer> mergedMeshBlob)
    {
        var avDescriptor = GetAvatarDescriptor();
        var (faceTrackingMeshes, faceTrackingNames) = GetFaceTrackingSetup();
        var exclusions = GetAllExcludedTransforms();
        var validPaths = new HashSet<string>();
        var blendShapeNameToID = new Dictionary<string, int>();
        var blendShapeIDToName = new List<string>();
        int GetBlendShapeID(string name)
        {
            if (blendShapeNameToID.TryGetValue(name, out var id))
                return id;
            id = blendShapeIDToName.Count;
            blendShapeNameToID[name] = id;
            blendShapeIDToName.Add(name);
            return id;
        }
        var ratiosDict = new List<Dictionary<int, float>>() { new() };
        foreach (var renderer in mergedMeshBlob)
        {
            var skinnedMeshRenderer = renderer as SkinnedMeshRenderer;
            var mesh = skinnedMeshRenderer?.sharedMesh;
            if (mesh == null || exclusions.Contains(skinnedMeshRenderer.transform))
                continue;
            string path = GetPathToRoot(skinnedMeshRenderer) + "/blendShape.";
            for (int i = 0; i < mesh.blendShapeCount; i++)
            {
                var name = mesh.GetBlendShapeName(i);
                if (MMDCompatibility && MMDBlendShapes.Contains(name))
                    continue;
                if (faceTrackingMeshes.Contains(skinnedMeshRenderer) && faceTrackingNames.Contains(name))
                    continue;
                if (mesh.GetBlendShapeFrameCount(i) == 1)
                {
                    validPaths.Add(path + name);
                    ratiosDict[0][GetBlendShapeID(path + name)] = skinnedMeshRenderer.GetBlendShapeWeight(i);
                }
            }
        }
        if (validPaths.Count == 0)
            return new List<List<(string blendshape, float value)>>();
        if (avDescriptor.lipSync == VRC.SDKBase.VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShape
            && avDescriptor.VisemeSkinnedMesh != null)
        {
            var meshRenderer = avDescriptor.VisemeSkinnedMesh;
            string path = GetPathToRoot(meshRenderer) + "/blendShape.";
            foreach (var blendShapeName in avDescriptor.VisemeBlendShapes)
            {
                validPaths.Remove(path + blendShapeName);
            }
        }
        if (avDescriptor.lipSync == VRC.SDKBase.VRC_AvatarDescriptor.LipSyncStyle.JawFlapBlendShape
            && avDescriptor.VisemeSkinnedMesh != null)
        {
            var meshRenderer = avDescriptor.VisemeSkinnedMesh;
            string path = GetPathToRoot(meshRenderer) + "/blendShape.";
            validPaths.Remove(path + avDescriptor.MouthOpenBlendShapeName);
        }
        if (avDescriptor.customEyeLookSettings.eyelidType == VRCAvatarDescriptor.EyelidType.Blendshapes
            && avDescriptor.customEyeLookSettings.eyelidsSkinnedMesh != null
            && avDescriptor.customEyeLookSettings.eyelidsSkinnedMesh.sharedMesh != null)
        {
            var meshRenderer = avDescriptor.customEyeLookSettings.eyelidsSkinnedMesh;
            string path = GetPathToRoot(meshRenderer) + "/blendShape.";
            foreach (var blendShapeID in avDescriptor.customEyeLookSettings.eyelidsBlendshapes)
            {
                if (blendShapeID >= 0 && blendShapeID < meshRenderer.sharedMesh.blendShapeCount)
                {
                    validPaths.Remove(path + meshRenderer.sharedMesh.GetBlendShapeName(blendShapeID));
                }
            }
        }
        var mergeableBlendShapes = new List<List<(int blendshapeID, float value)>>();
        var hasEntryInMergeableBlendShapes = new HashSet<int>();
        foreach (var clip in GetAllUsedAnimationClips())
        {
            var blendShapes = new List<(int blendShapeID, EditorCurveBinding binding)>();
            var keyframes = new HashSet<float>();
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (binding.type != typeof(SkinnedMeshRenderer)
                    || !binding.propertyName.StartsWithSimple("blendShape."))
                    continue;
                var path = $"{binding.path}/{binding.propertyName}";
                if (!validPaths.Contains(path))
                    continue;
                blendShapes.Add((GetBlendShapeID(path), binding));
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                keyframes.UnionWith(curve.keys.Select(x => x.time));
            }
            foreach (var key in keyframes)
            {
                var blendShapeValues = new Dictionary<int, float>();
                foreach (var blendShape in blendShapes)
                {
                    var curve = AnimationUtility.GetEditorCurve(clip, blendShape.binding);
                    blendShapeValues[blendShape.blendShapeID] = curve.Evaluate(key);
                }
                NormalizeBlendShapeValues(blendShapeValues);
                if (!ratiosDict.Any(list => list.SequenceEqual(blendShapeValues)))
                    ratiosDict.Add(blendShapeValues);
            }
            foreach (var blendshape in blendShapes)
            {
                if (!hasEntryInMergeableBlendShapes.Contains(blendshape.blendShapeID))
                {
                    hasEntryInMergeableBlendShapes.Add(blendshape.blendShapeID);
                    mergeableBlendShapes.Add(new List<(int blendshapeID, float value)>() { (blendshape.blendShapeID, 1) });
                }
            }
        }
        var ratiosArray = ratiosDict.Select(x => {
            var array = Enumerable.Repeat(float.NegativeInfinity, blendShapeIDToName.Count).ToArray();
            foreach (var entry in x) {
                array[entry.Key] = entry.Value;
            }
            return array;
        }).ToArray();
        for (int i = 0; i < mergeableBlendShapes.Count - 1; i++)
        {
            for (int j = i + 1; j < mergeableBlendShapes.Count; j++)
            {
                var subList = mergeableBlendShapes[i];
                var candidate = mergeableBlendShapes[j][0].blendshapeID;
                float value = -1;
                bool canAddToRatio = true;
                for (int k = 0; k < ratiosArray.Length; k++) {
                    if (!TryAddBlendShapeToSubList(subList, candidate, ref value, ratiosArray[k])) {
                        canAddToRatio = false;
                        break;
                    }
                }
                if (canAddToRatio && value != -1) {
                    subList.Add((candidate, value));
                    NormalizeBlendShapeValues(subList);
                    mergeableBlendShapes.RemoveAt(j);
                    j--;
                }
            }
        }
        mergeableBlendShapes.RemoveAll(x => x.Count == 1);
        return mergeableBlendShapes.Select(x => x.OrderByDescending(y => y.value).Select(z => (blendShapeIDToName[z.blendshapeID], z.value)).ToList()).ToList();
    }

    private void NormalizeBlendShapeValues(List<(int blendshape, float value)> blendShapeValues)
    {
        var maxValue = blendShapeValues.Max(x => x.value);
        if (maxValue == 0 || maxValue == 1)
            return;
        for (int i = 0; i < blendShapeValues.Count; i++)
        {
            blendShapeValues[i] = (blendShapeValues[i].blendshape, blendShapeValues[i].value / maxValue);
        }
    }

    private void NormalizeBlendShapeValues(Dictionary<int, float> blendShapeValues)
    {
        var maxValue = blendShapeValues.Max(x => x.Value);
        if (maxValue == 0 || maxValue == 1)
            return;
        foreach (var key in blendShapeValues.Keys.ToList())
        {
            blendShapeValues[key] /= maxValue;
        }
    }

    private bool TryAddBlendShapeToSubList(List<(int blendshapeID, float value)> subList, int blendshapeID, ref float value, float[] ratioToCheckAgainst)
    {
        int intersectionCount = 0;
        float intersectionMax = 0;
        int subListCount = subList.Count;
        for (int i = 0; i < subListCount; i++)
        {
            float ratioValue = ratioToCheckAgainst[subList[i].blendshapeID];
            if (ratioValue != float.NegativeInfinity)
            {
                intersectionCount++;
                intersectionMax = Mathf.Max(intersectionMax, ratioValue);
            }
            else if (intersectionCount > 0)
            {
                return false;
            }
        }
        float candidateValue = ratioToCheckAgainst[blendshapeID];
        bool hasCandidate = candidateValue != float.NegativeInfinity;
        if (intersectionCount == 0 && !hasCandidate)
            return true;
        if (intersectionCount != subListCount || !hasCandidate)
            return false;
        if (intersectionMax == 0)
            return candidateValue == 0;
        if (candidateValue == 0)
            return false;
        for (int i = 0; i < subListCount; i++)
        {
            var match = ratioToCheckAgainst[subList[i].blendshapeID];
            if (Mathf.Abs(subList[i].value - match / intersectionMax) > 0.01f)
                return false;
        }
        if (value < 0)
            value = candidateValue / intersectionMax;
        else if (Mathf.Abs(value - candidateValue / intersectionMax) > 0.01f)
            return false;
        return true;
    }

    public HashSet<string> FindAllGameObjectTogglePaths()
    {
        AnalyzeTogglesAndExclusions();
        return cache_FindAllGameObjectTogglePaths;
    }

    public HashSet<Transform> FindAllAlwaysDisabledGameObjects()
    {
        AnalyzeTogglesAndExclusions();
        return cache_FindAllAlwaysDisabledGameObjects;
    }

    public HashSet<Component> FindAllUnusedComponents()
    {
        AnalyzeTogglesAndExclusions();
        return cache_FindAllUnusedComponents;
    }

    public HashSet<Transform> GetAllExcludedTransforms()
    {
        AnalyzeTogglesAndExclusions();
        return cache_GetAllExcludedTransforms;
    }
    private HashSet<string> cache_FindAllGameObjectTogglePaths = null;
    private HashSet<Transform> cache_FindAllAlwaysDisabledGameObjects = null;
    private HashSet<Component> cache_FindAllUnusedComponents = null;
    private HashSet<Transform> cache_GetAllExcludedTransforms = null;
    private Dictionary<string, string> cache_GetSkinnedMeshMergeBarrierPaths = null;
    private void AnalyzeTogglesAndExclusions()
    {
        if (cache_FindAllGameObjectTogglePaths != null
            && cache_FindAllAlwaysDisabledGameObjects != null
            && cache_FindAllUnusedComponents != null
            && cache_GetAllExcludedTransforms != null
            && cache_GetSkinnedMeshMergeBarrierPaths != null)
            return;
        using var _ = new Profiler.Section("AnalyzeTogglesAndExclusions()");

        bool IsSameOrChildPath(string path, string parentPath)
        {
            return path == parentPath
                || (path.Length > parentPath.Length && path.StartsWithSimple(parentPath) && path[parentPath.Length] == '/');
        }

        List<(Transform t, string exclusionSource, bool includeChildren)> DeduplicateAndCollapseExclusions(IEnumerable<(Transform t, string exclusionSource, bool includeChildren)> exclusions)
        {
            var uniqueExclusions = exclusions.Where(p => p.t != null)
                .Distinct()
                .Select((p, index) => (p.t, p.exclusionSource, p.includeChildren, path: GetPathToRoot(p.t), index))
                .GroupBy(p => p.path)
                .Select(g => g.OrderByDescending(p => p.includeChildren).ThenBy(p => p.index).First())
                .OrderBy(p => p.path.Count(c => c == '/'))
                .ThenBy(p => p.index)
                .ToList();
            var keptPaths = new List<string>();
            var result = new List<(Transform t, string exclusionSource, bool includeChildren)>();
            foreach (var exclusion in uniqueExclusions)
            {
                if (keptPaths.Any(parentPath => IsSameOrChildPath(exclusion.path, parentPath)))
                    continue;
                if (exclusion.includeChildren)
                    keptPaths.Add(exclusion.path);
                result.Add((exclusion.t, exclusion.exclusionSource, exclusion.includeChildren));
            }
            return result;
        }

        var curveBindings = GetAllUsedCurveBindings();
        var root = GetRootTransform();
        var subAnimators = root.GetComponentsInChildren<Animator>(true).Where(a => a != null && a.transform != root).ToList();
        var nonEditorOnlyMonoBehaviours = GetNonEditorOnlyComponentsInChildren<MonoBehaviour>();
        var manualExclusions = ExcludeTransforms
            .Select((t, index) => (t, includeChildren: index >= ExcludeTransformChildren.Count || ExcludeTransformChildren[index]))
            .Where(p => p.t != null)
            .ToList();

        var animatedTogglePaths = new HashSet<string>();
        var behaviourToggles = new HashSet<string>();
        foreach (var binding in curveBindings)
        {
            if (binding.type == typeof(GameObject) && binding.propertyName == "m_IsActive")
                animatedTogglePaths.Add(binding.path);
            if (binding.propertyName == "m_Enabled"
                && (typeof(Behaviour).IsAssignableFrom(binding.type) || typeof(Renderer).IsAssignableFrom(binding.type)))
            {
                behaviourToggles.Add(binding.path);
            }
        }

        var baseAutomaticExclusions = new List<(Transform t, string exclusionSource, bool includeChildren)>();
        baseAutomaticExclusions.Add((GetTransformFromPath("_VirtualLens_Root"), "Virtual Lens Root", true));
        baseAutomaticExclusions.AddRange(root.GetComponentsInChildren<VRCContactSender>(true)
            .Where(c => c.collisionTags.Any(t => t == "superneko.realkiss.contact.mouth"))
            .Select(c => c.transform.parent)
            .Where(t => t != null)
            .Select(t => t.Cast<Transform>().FirstOrDefault(child => child.TryGetComponent(out SkinnedMeshRenderer _)))
            .Where(t => t != null)
            .Select(t => (t, "Real Kiss System Mesh", true)));
        baseAutomaticExclusions.AddRange(FindAllPenetrators().Select(p => (p.transform, "Penetrator Mesh", true)));
        var threshold = AvatarOptimizerSettings.SharedMeshVertexCountExclusionThreshold;
        baseAutomaticExclusions.AddRange(root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .Where(r => r.sharedMesh != null)
            .GroupBy(r => r.sharedMesh)
            .Where(g => g.Key.vertexCount > threshold && g.Count() > 1)
            .SelectMany(g => g.Select(r => (r.transform, $"Shared Mesh '{g.Key.name}'", false))));

        HashSet<Transform> CalculateAlwaysDisabledGameObjects(HashSet<Transform> exclusions)
        {
            var disabledGameObjects = new HashSet<Transform>();
            var queue = new Queue<Transform>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (exclusions.Contains(current))
                    continue;
                if (current != root && !current.gameObject.activeSelf && !animatedTogglePaths.Contains(GetPathToRoot(current)))
                {
                    disabledGameObjects.Add(current);
                    foreach (var child in current.GetAllDescendants())
                    {
                        disabledGameObjects.Add(child);
                    }
                }
                else
                {
                    foreach (Transform child in current)
                    {
                        queue.Enqueue(child);
                    }
                }
            }
            return disabledGameObjects;
        }

        HashSet<Component> CalculateUnusedComponents(HashSet<Transform> alwaysDisabledGameObjects, HashSet<Transform> exclusions)
        {
            var alwaysDisabledBehaviours = new HashSet<Component>(root.GetComponentsInChildren<Behaviour>(true)
                .Where(b => b != null && !b.enabled)
                .Where(b => !(b is VRCPhysBoneColliderBase))
                .Where(b => !behaviourToggles.Contains(GetPathToRoot(b)))
                .Where(b => !b.GetType().FullName.StartsWithSimple("RootMotion.FinalIK")));

            alwaysDisabledBehaviours.UnionWith(root.GetComponentsInChildren<Renderer>(true)
                .Where(r => r != null && !r.enabled && !(r is ParticleSystemRenderer))
                .Where(r => !behaviourToggles.Contains(GetPathToRoot(r))));

            alwaysDisabledBehaviours.UnionWith(alwaysDisabledGameObjects
                .Where(t => t != null)
                .SelectMany(t => t.GetNonNullComponents()
                    .Where(c => !(c is Transform)))
                    .Where(c => !c.GetType().FullName.StartsWithSimple("RootMotion.FinalIK")));

            foreach (var entry in FindAllPhysBoneDependencies())
            {
                if (exclusions.Contains(entry.Key.transform))
                    continue;
                var dependencies = entry.Value.Select(d => d as Component).Where(d => d != null);
                if (!entry.Value.Any(d => d is AnimatorController) && dependencies.All(d => alwaysDisabledBehaviours.Contains(d)))
                {
                    alwaysDisabledBehaviours.Add(entry.Key);
                }
            }

            static bool HasGlobalCollisionFlags(VRCPhysBoneColliderBase collider)
            {
                if (collider == null || PhysBoneColliderGlobalCollisionFlagsField == null)
                    return false;
                var value = PhysBoneColliderGlobalCollisionFlagsField.GetValue(collider);
                return value != null && System.Convert.ToInt32(value) != 0;
            }

            var usedPhysBoneColliders = root.GetComponentsInChildren<VRCPhysBoneBase>(true)
                .Where(pb => !alwaysDisabledBehaviours.Contains(pb) || exclusions.Contains(pb.transform))
                .SelectMany(pb => pb.colliders)
                .Concat(root.GetComponentsInChildren<VRCPhysBoneColliderBase>(true)
                    .Where(HasGlobalCollisionFlags))
                .ToHashSet();

            alwaysDisabledBehaviours.UnionWith(root.GetComponentsInChildren<VRCPhysBoneColliderBase>(true)
                .Where(c => !usedPhysBoneColliders.Contains(c)));

            alwaysDisabledBehaviours.RemoveWhere(c => exclusions.Contains(c.transform) || c.transform == root);

            return alwaysDisabledBehaviours;
        }

        List<(Transform t, string exclusionSource, bool includeChildren)> GetSubAnimatorExclusions(HashSet<Transform> alwaysDisabledGameObjects, HashSet<Component> unusedComponents)
        {
            var result = new List<(Transform t, string exclusionSource, bool includeChildren)>();
            foreach (var animator in subAnimators)
            {
                if (alwaysDisabledGameObjects.Contains(animator.transform) || unusedComponents.Contains(animator))
                    continue;
                var animatorController = animator.runtimeAnimatorController;
                if (animatorController == null)
                    continue;
                var animatorPath = GetPathToRoot(animator);
                var exclusionSource = $"Sub Animator at '{animatorPath}'";
                var animatedPaths = animatorController.animationClips
                    .Where(clip => clip != null)
                    .SelectMany(clip => AnimationUtility.GetCurveBindings(clip).Concat(AnimationUtility.GetObjectReferenceCurveBindings(clip)))
                    .Select(binding => string.IsNullOrEmpty(binding.path) ? animatorPath : $"{animatorPath}/{binding.path}");
                result.AddRange(DeduplicateAndCollapseExclusions(animatedPaths.Select(path => (GetTransformFromPath(path), exclusionSource, false))));
            }
            return result;
        }

        HashSet<Transform> CalculateExcludedTransforms(HashSet<Transform> alwaysDisabledGameObjects, HashSet<Component> unusedComponents, out List<(Transform t, string exclusionSource, bool includeChildren)> automaticExclusions, out HashSet<string> mergeBarrierPaths)
        {
            var exclusions = DeduplicateAndCollapseExclusions(manualExclusions.Select(p => (p.t, string.Empty, p.includeChildren))
                .Concat(baseAutomaticExclusions)
                .Concat(GetSubAnimatorExclusions(alwaysDisabledGameObjects, unusedComponents)));
            automaticExclusions = exclusions
                .Where(p => !string.IsNullOrEmpty(p.exclusionSource))
                .ToList();
            mergeBarrierPaths = exclusions.Where(p => !p.includeChildren).Select(p => GetPathToRoot(p.t)).ToHashSet();

            var allExcludedTransforms = new HashSet<Transform>();
            foreach (var exclusion in exclusions)
            {
                allExcludedTransforms.Add(exclusion.t);
                if (exclusion.includeChildren)
                    allExcludedTransforms.UnionWith(exclusion.t.GetAllDescendants());
            }
            return allExcludedTransforms;
        }

        var exclusions = CalculateExcludedTransforms(new HashSet<Transform>(), new HashSet<Component>(), out var automaticExclusions, out var mergeBarrierPaths);
        var maxIterations = subAnimators.Count + 1;

        for (int iteration = 0; iteration < maxIterations; iteration++)
        {
            var currentExclusions = exclusions;
            var alwaysDisabledGameObjects = CalculateAlwaysDisabledGameObjects(currentExclusions);
            var unusedComponents = CalculateUnusedComponents(alwaysDisabledGameObjects, currentExclusions);
            var nextExclusions = CalculateExcludedTransforms(alwaysDisabledGameObjects, unusedComponents, out automaticExclusions, out mergeBarrierPaths);

            exclusions = nextExclusions;
            if (nextExclusions.SetEquals(currentExclusions))
                break;
        }

        cache_FindAllGameObjectTogglePaths = animatedTogglePaths;
        cache_FindAllAlwaysDisabledGameObjects = CalculateAlwaysDisabledGameObjects(exclusions);
        cache_FindAllUnusedComponents = CalculateUnusedComponents(cache_FindAllAlwaysDisabledGameObjects, exclusions);
        cache_GetAllExcludedTransforms = CalculateExcludedTransforms(cache_FindAllAlwaysDisabledGameObjects, cache_FindAllUnusedComponents, out automaticExclusions, out mergeBarrierPaths);
        cache_GetSkinnedMeshMergeBarrierPaths = mergeBarrierPaths.ToDictionary(path => path, path => "exclusion");
        foreach (var path in cache_FindAllGameObjectTogglePaths)
        {
            cache_GetSkinnedMeshMergeBarrierPaths.TryAdd(path, "GameObject toggle");
        }
        cache_GetAllExcludedTransformPaths = new(cache_GetAllExcludedTransforms.Select(t => GetPathToRoot(t)));

        if (automaticExclusions.Count > 0)
        {
            LogToFile($"Automatically excluding {automaticExclusions.Count} transforms from optimization:");
            var groupedBySource = automaticExclusions.GroupBy(p => p.exclusionSource);
            foreach (var group in groupedBySource)
            {
                LogToFile($"- {group.Key}", 1);
                foreach (var exclusion in group)
                {
                    LogToFile($"- {GetPathToRoot(exclusion.t)}{(exclusion.includeChildren ? " (including children)" : string.Empty)}", 2);
                }
            }
        }
        if (manualExclusions.Count > 0)
        {
            LogToFile($"Excluding {manualExclusions.Count} user-specified transforms from optimization:");
            foreach (var exclusion in manualExclusions)
            {
                LogToFile($"- {GetPathToRoot(exclusion.t)}{(exclusion.includeChildren ? " (including children)" : string.Empty)}", 1);
            }
        }
    }

    private HashSet<Transform> cache_FindAllMovingTransforms = null;
    public HashSet<Transform> FindAllMovingTransforms()
    {
        if (cache_FindAllMovingTransforms != null)
            return cache_FindAllMovingTransforms;
        var avDescriptor = GetAvatarDescriptor();
        if (avDescriptor == null)
            return new HashSet<Transform>();
        var transforms = new HashSet<Transform>();

        if (avDescriptor.enableEyeLook)
        {
            transforms.Add(avDescriptor.customEyeLookSettings.leftEye);
            transforms.Add(avDescriptor.customEyeLookSettings.rightEye);
        }
        if (avDescriptor.customEyeLookSettings.eyelidType == VRCAvatarDescriptor.EyelidType.Bones)
        {
            transforms.Add(avDescriptor.customEyeLookSettings.lowerLeftEyelid);
            transforms.Add(avDescriptor.customEyeLookSettings.lowerRightEyelid);
            transforms.Add(avDescriptor.customEyeLookSettings.upperLeftEyelid);
            transforms.Add(avDescriptor.customEyeLookSettings.upperRightEyelid);
        }

        if (avDescriptor.lipSync == VRC.SDKBase.VRC_AvatarDescriptor.LipSyncStyle.JawFlapBone)
        {
            transforms.Add(avDescriptor.lipSyncJawBone);
        }

        foreach (var clip in GetAllUsedAnimationClips())
        {
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (binding.type == typeof(Transform))
                {
                    transforms.Add(GetTransformFromPath(binding.path));
                }
            }
        }

        var animators = avDescriptor.GetComponentsInChildren<Animator>(true);
        foreach (var animator in animators)
        {
            if (animator == null || animator.avatar == null || !animator.avatar.isHuman)
                continue;
            foreach (var boneId in System.Enum.GetValues(typeof(HumanBodyBones)).Cast<HumanBodyBones>())
            {
                if (boneId < 0 || boneId >= HumanBodyBones.LastBone)
                    continue;
                transforms.Add(animator.GetBoneTransform(boneId));
            }
        }

        var alwaysDisabledComponents = FindAllUnusedComponents();
        var raycastType = System.AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("VRC.SDK3.Avatars.Components.VRCRaycast", false))
            .FirstOrDefault(type => type != null);
        if (raycastType != null)
        {
            var resultTransformField = raycastType.GetField("resultTransform", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (resultTransformField != null)
            {
                foreach (var raycast in GetNonEditorOnlyComponentsInChildren<MonoBehaviour>()
                    .Where(component => component != null && raycastType == component.GetType())
                    .Where(component => !alwaysDisabledComponents.Contains(component)))
                {
                    if (resultTransformField.GetValue(raycast) is Transform resultTransform)
                    {
                        transforms.Add(resultTransform);
                    }
                }
            }
        }
        var physBones = avDescriptor.GetComponentsInChildren<VRCPhysBoneBase>(true)
            .Where(pb => !alwaysDisabledComponents.Contains(pb)).ToList();
        foreach (var physBone in physBones)
        {
            var root = physBone.GetRootTransform();
            var exclusions = new HashSet<Transform>(physBone.ignoreTransforms);
            var stack = new Stack<Transform>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                transforms.Add(current);
                if (exclusions.Contains(current))
                    continue;
                foreach (Transform child in current)
                {
                    stack.Push(child);
                }
            }
        }

        var constraints = avDescriptor.GetComponentsInChildren<Behaviour>(true)
            .Where(b => b != null && !alwaysDisabledComponents.Contains(b))
            .Where(b => b.GetType().Name.Contains("Constraint")).ToList();
        foreach (var constraint in constraints)
        {
            transforms.Add(constraint.transform);
            if (constraint.GetType().Name.StartsWithSimple("VRC"))
            {
                using var so = new SerializedObject(constraint);
                var targetTransformProperty = so.FindProperty("TargetTransform");
                if (targetTransformProperty != null)
                {
                    var targetTransform = targetTransformProperty.objectReferenceValue as Transform;
                    if (targetTransform != null)
                    {
                        transforms.Add(targetTransform);
                    }
                }
            }
        }

        var finalIKScripts = avDescriptor.GetComponentsInChildren<Behaviour>(true)
            .Where(b => b != null && !alwaysDisabledComponents.Contains(b))
            .Where(b => b.GetType().FullName.StartsWithSimple("RootMotion.FinalIK")).ToList();
        foreach (var finalIKScript in finalIKScripts)
        {
            transforms.UnionWith(FindReferencedTransforms(finalIKScript));
        }

        foreach (var headChop in avDescriptor.GetComponentsInChildren<VRCHeadChop>(true))
        {
            foreach (var targetBone in headChop.targetBones)
            {
                if (targetBone.transform != null)
                {
                    transforms.Add(targetBone.transform);
                }
            }
        }

        transforms.UnionWith(avDescriptor.transform.GetAllDescendants().Where(t => t.localScale != Vector3.one));

        transforms.UnionWith(avDescriptor.GetComponentsInChildren<Rigidbody>(true)
            .Where(rb => !alwaysDisabledComponents.Contains(rb)).Select(rb => rb.transform));

        return cache_FindAllMovingTransforms = transforms;
    }

    private bool IsDPSPenetratorTipLight(Light light)
    {
        return light.type == LightType.Point && light.renderMode == LightRenderMode.ForceVertex
            && light.color.r < 0.01 && light.color.g < 0.01 && light.color.b < 0.01
            && light.range % 0.1 - 0.09 < 0.001;
    }

    private bool IsDPSPenetratorRoot(Transform t)
    {
        if (t == null)
            return false;
        if (t.GetComponentsInChildren<Light>(true).Count(IsDPSPenetratorTipLight) != 1)
            return false;
        var renderers = t.GetComponentsInChildren<MeshRenderer>(true);
        if (renderers.Length != 1)
            return false;
        if (renderers[0].sharedMaterials.Length == 0)
            return false;
        var material = renderers[0].sharedMaterials[0];
        if (material == null)
            return false;
        return material.HasProperty("_Length");
    }

    private bool IsTPSPenetratorRoot(Transform t)
    {
        if (t == null)
            return false;
        var renderers = t.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length != 1)
            return false;
        if (renderers[0].sharedMaterials.Length == 0)
            return false;
        var material = renderers[0].sharedMaterials[0];
        if (material == null)
            return false;
        return material.HasProperty("_TPSPenetratorEnabled") && material.GetFloat("_TPSPenetratorEnabled") > 0.5f;
    }

    private bool IsSPSPenetratorRoot(Transform t) {
		if (t == null)
			return false;
        var renderers = t.GetComponentsInChildren<Renderer>(true);
        return renderers.Any(r => {
            if (r.sharedMaterials.Length == 0)
                return false;
            var material = r.sharedMaterials[0];
            if (material == null)
                return false;
            return material.HasProperty("_SPS_Length") || material.HasProperty("_SPS_BakedLength") || material.HasProperty("_SPS_Configured");
        });
	}

    private HashSet<Renderer> cache_FindAllPenetrators = null;
    public HashSet<Renderer> FindAllPenetrators()
    {
        if (cache_FindAllPenetrators != null)
            return cache_FindAllPenetrators;
        var root = GetRootTransform();
        var penetratorTipLights = root.GetComponentsInChildren<Light>(true)
            .Where(l => IsDPSPenetratorTipLight(l)).ToList();
        var penetrators = new HashSet<Renderer>();
        foreach (var light in penetratorTipLights)
        {
            var candidate = light.transform;
            while (candidate != null && !IsDPSPenetratorRoot(candidate))
            {
                candidate = candidate.parent;
            }
            if (IsDPSPenetratorRoot(candidate))
            {
                penetrators.Add(candidate.GetComponentsInChildren<MeshRenderer>(true).First());
            }
        }
        penetrators.UnionWith(root.GetComponentsInChildren<Renderer>(true).Where(m => IsTPSPenetratorRoot(m.transform) || IsSPSPenetratorRoot(m.transform)));
        return cache_FindAllPenetrators = penetrators;
    }

    public List<T> GetNonEditorOnlyComponentsInChildren<T>() where T : Component
    {
        var components = new List<T>();
        var stack = new Stack<Transform>();
        stack.Push(GetRootTransform());
        while (stack.Count > 0)
        {
            var currentTransform = stack.Pop();
            if (currentTransform.gameObject.CompareTag("EditorOnly"))
                continue;
            components.AddRange(currentTransform.GetComponents<T>());
            foreach (Transform child in currentTransform)
            {
                stack.Push(child);
            }
        }
        return components;
    }

    public List<T> GetUsedComponentsInChildren<T>() where T : Component
    {
        using var _ = new Profiler.Section("GetUsedComponentsInChildren()");
        List<T> result = new();
        Stack<Transform> stack = new();
        HashSet<Transform> alwaysDisabledGameObjects = new();
        HashSet<Component> unusedComponents = new();
        if (DeleteUnusedComponents)
        {
            alwaysDisabledGameObjects = FindAllAlwaysDisabledGameObjects();
            unusedComponents = FindAllUnusedComponents();
        }
        stack.Push(GetRootTransform());
        while (stack.Count > 0)
        {
            var currentTransform = stack.Pop();
            if (currentTransform.gameObject.CompareTag("EditorOnly") || alwaysDisabledGameObjects.Contains(currentTransform))
                continue;
            result.AddRange(currentTransform.GetComponents<T>().Where(c => c != null && !unusedComponents.Contains(c)));
            foreach (Transform child in currentTransform)
            {
                stack.Push(child);
            }
        }
        return result;
    }

    private string GenerateUniqueName(string name, HashSet<string> usedNames)
    {
        if (usedNames.Add(name))
        {
            return name;
        }
        int count = 1;
        while (!usedNames.Add(name + " " + count))
        {
            count++;
        }
        return name + " " + count;
    }

    private Dictionary<string, Material> cache_GetFirstMaterialOnPath = null;
    public Material GetFirstMaterialOnPath(string path)
    {
        cache_GetFirstMaterialOnPath ??= new Dictionary<string, Material>();
        if (cache_GetFirstMaterialOnPath.TryGetValue(path, out var mat))
            return mat;
        var renderer = GetTransformFromPath(path)?.GetComponent<Renderer>();
        if (renderer == null || renderer.sharedMaterials.Length == 0)
            return cache_GetFirstMaterialOnPath[path] = null;
        return cache_GetFirstMaterialOnPath[path] = renderer.sharedMaterials[0];
    }

    public string CanCombineMaterialsError(List<MaterialSlot> list, MaterialSlot candidate)
    {
        var candidateMat = candidate.material;
        var firstMat = list[0].material;
        bool UsesRepeatedLastSubMesh(MaterialSlot slot)
        {
            if (slot.renderer == null)
                return false;
            var mesh = slot.renderer.GetSharedMesh();
            return mesh != null
                && slot.renderer.sharedMaterials.Length > mesh.subMeshCount
                && slot.index >= Math.Max(0, mesh.subMeshCount - 1);
        }
        if (UsesRepeatedLastSubMesh(candidate) || list.Any(UsesRepeatedLastSubMesh))
            return "Material slot uses the last sub mesh repeated by extra material slots";
        if (candidateMat == null || firstMat == null)
            return "One of the materials is null";
        if (firstMat.shader != candidateMat.shader)
            return "Shaders do not match";
        if (list.Any(slot => slot.GetTopology() != candidate.GetTopology()))
            return "Topologies do not match";
        bool IsAffectedByMaterialSwap(MaterialSlot slot) =>
            slotSwapMaterials.ContainsKey((GetPathToRoot(slot.renderer), slot.index))
            || (materialSlotRemap.TryGetValue((GetPathToRoot(slot.renderer), slot.index), out var remap) && slotSwapMaterials.ContainsKey(remap));
        if (IsAffectedByMaterialSwap(list[0]) || IsAffectedByMaterialSwap(candidate))
            return "Affected by material swap";
        if (GetParticleSystemsUsingRenderer(candidate.renderer).Any(ps => ps.shape.useMeshMaterialIndex && ps.shape.meshMaterialIndex == candidate.index))
            return "Affected by particle system using mesh material index";
        if (GetParticleSystemsUsingRenderer(list[0].renderer).Any(ps => ps.shape.useMeshMaterialIndex && ps.shape.meshMaterialIndex == list[0].index))
            return "Affected by particle system using mesh material index";
        var listMaterials = list.Select(slot => slot.material).ToArray();
        var materialComparer = new MaterialAssetComparer();
        bool allTheSameAsCandidate = listMaterials.All(mat => materialComparer.Equals(mat, candidateMat));
        return allTheSameAsCandidate ? null : "Not the same materials";
    }

    public List<List<MaterialSlot>> FindAllMergeAbleMaterials(IEnumerable<Renderer> renderers)
    {
        using var _ = new Profiler.Section("FindAllMergeAbleMaterials()");
        var matched = new List<List<MaterialSlot>>();
        foreach (var renderer in renderers)
        {
            foreach (var candidate in MaterialSlot.GetAllSlotsFrom(renderer))
            {
                bool foundMatch = false;
                for (int i = 0; i < matched.Count; i++)
                {
                    if (CanCombineMaterialsError(matched[i], candidate) == null)
                    {
                        matched[i].Add(candidate);
                        foundMatch = true;
                        break;
                    }
                }
                if (!foundMatch)
                {
                    matched.Add(new List<MaterialSlot> { candidate });
                }
            }
        }
        return matched;

    }

    // Rebuilds each skinned mesh so material slots using the exact same material share one sub mesh,
    // and trims unused vertex data (unreferenced vertices, all-white colors, unused uv components).
    private void MergeIdenticalMaterialSlots()
    {
        var exclusions = GetAllExcludedTransforms();
        var skinnedMeshRenderers = GetRootTransform().GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .Where(smr => !exclusions.Contains(smr.transform) && smr.sharedMesh != null).ToArray();
        if (skinnedMeshRenderers.Length == 0)
            return;
        LogToFile($"Merging identical material slots on {skinnedMeshRenderers.Length} SkinnedMeshRenderers:");
        using var _ = log.IndentScope();
        for (int meshRenderIndex = 0; meshRenderIndex < skinnedMeshRenderers.Length; meshRenderIndex++)
        {
            var meshRenderer = skinnedMeshRenderers[meshRenderIndex];
            var mesh = meshRenderer.sharedMesh;
            
            DisplayProgressBar($"Merging material slots on {meshRenderer.name} ({meshRenderIndex + 1}/{skinnedMeshRenderers.Length})");

            string meshPath = GetPathToRoot(meshRenderer);

            var matchedSlots = FindAllMergeAbleMaterials(new [] { meshRenderer });
            var uniqueMatchedSlots = matchedSlots.Select(list => list.Select(slot => list.First(slot2 => slot.material == slot2.material)).Distinct().ToList()).ToList();
            var mergedMeshIndices = new List<List<int>>();

            LogToFile($"- '{meshPath}' ({meshRenderer.sharedMaterials.Length} => {matchedSlots.Count})");
            using var __ = log.IndentScope();
            LogToFile($"- Material slot merge layout (slot id, material name):");
            foreach (var slotGroup in matchedSlots)
            {
                for (int k = 0; k < slotGroup.Count; k++)
                {
                    var slot = slotGroup[k];
                    LogToFile($"- {slot.index,2} {(slot.material == null ? "null" : slot.material.name)}", k == 0 ? 1 : 2);
                }
            }

            var sourceVertices = mesh.vertices;
            var hasUvSet = new bool[8] {
                true,
                mesh.HasVertexAttribute(VertexAttribute.TexCoord1),
                mesh.HasVertexAttribute(VertexAttribute.TexCoord2),
                mesh.HasVertexAttribute(VertexAttribute.TexCoord3),
                mesh.HasVertexAttribute(VertexAttribute.TexCoord4),
                mesh.HasVertexAttribute(VertexAttribute.TexCoord5),
                mesh.HasVertexAttribute(VertexAttribute.TexCoord6),
                mesh.HasVertexAttribute(VertexAttribute.TexCoord7),
            };
            int highestUsedUvSet = hasUvSet.Select((b, i) => (b, i)).Where(t => t.b).Select(t => t.i).LastOrDefault();
            var sourceUv = Enumerable.Range(0, 8).Select(i => hasUvSet[i] ? new List<Vector4>(sourceVertices.Length) : null).ToArray();
            for(int i = 0; i <= highestUsedUvSet; i++)
            {
                if (!hasUvSet[i])
                    continue;
                mesh.GetUVs(i, sourceUv[i]);
                sourceUv[i] = sourceUv[i].Count != sourceVertices.Length ? Enumerable.Repeat(Vector4.zero, sourceVertices.Length).ToList() : sourceUv[i];
            }
            Color[] sourceColor = null;
            Color32[] sourceColor32 = null;
            var sourceNormals = mesh.normals;
            var sourceTangents = mesh.tangents;
            var sourceWeights = mesh.boneWeights;

            var targetUv = Enumerable.Range(0, 8).Select(i => hasUvSet[i] ? new List<Vector4>(sourceVertices.Length) : null).ToArray();
            List<Color> targetColor = null;
            List<Color32> targetColor32 = null;
            if (mesh.HasVertexAttribute(VertexAttribute.Color))
            {
                if (mesh.GetVertexAttributeFormat(VertexAttribute.Color) == VertexAttributeFormat.UNorm8)
                {
                    targetColor32 = new List<Color32>(sourceVertices.Length);
                    sourceColor32 = mesh.colors32;
                }
                else
                {
                    targetColor = new List<Color>(sourceVertices.Length);
                    sourceColor = mesh.colors;
                }
            }
            var targetVertices = new List<Vector3>(sourceVertices.Length);
            var targetIndices = new List<List<int>>();
            var targetTopology = new List<MeshTopology>();
            var targetNormals = new List<Vector3>(sourceVertices.Length);
            var targetTangents = new List<Vector4>(sourceVertices.Length);
            var targetWeights = new List<BoneWeight>(sourceVertices.Length);

            var targetOldVertexIndex = new List<int>();

            for (int i = 0; i < matchedSlots.Count; i++)
            {
                var uniqueMeshIndices = new HashSet<int>();
                var indexList = new List<int>();
                bool createSubMesh = mesh.subMeshCount > 0 && matchedSlots[i][0].index < mesh.subMeshCount;
                for (int k = 0; k < matchedSlots[i].Count; k++)
                {
                    int internalMaterialID = uniqueMatchedSlots[i].Select((slot, index) => (slot, index)).First(t => t.slot.material == matchedSlots[i][k].material).index;
                    var sourceIndices = mesh.subMeshCount == 0
                        ? Array.Empty<int>()
                        : mesh.GetIndices(Math.Min(mesh.subMeshCount - 1, matchedSlots[i][k].index));
                    if (!createSubMesh)
                    {
                        foreach (int oldIndex in sourceIndices)
                            uniqueMeshIndices.Add((int)sourceUv[0][oldIndex].z >> 12);
                        continue;
                    }
                    var indexMap = new Dictionary<int, int>();
                    for (int j = 0; j < sourceIndices.Length; j++)
                    {
                        int oldIndex = sourceIndices[j];
                        if (indexMap.TryGetValue(oldIndex, out int newIndex))
                        {
                            indexList.Add(newIndex);
                        }
                        else
                        {
                            newIndex = targetVertices.Count;
                            indexList.Add(newIndex);
                            indexMap[oldIndex] = newIndex;
                            targetUv[0].Add(new Vector4(sourceUv[0][oldIndex].x, sourceUv[0][oldIndex].y, sourceUv[0][oldIndex].z + internalMaterialID, sourceUv[0][oldIndex].w));
                            for (int a = 1; a <= highestUsedUvSet; a++)
                            {
                                targetUv[a]?.Add(sourceUv[a][oldIndex]);
                            }
                            targetColor?.Add(sourceColor[oldIndex]);
                            targetColor32?.Add(sourceColor32[oldIndex]);
                            targetVertices.Add(sourceVertices[oldIndex]);
                            targetNormals.Add(sourceNormals[oldIndex]);
                            targetTangents.Add(sourceTangents[oldIndex]);
                            targetWeights.Add(sourceWeights[oldIndex]);
                            targetOldVertexIndex.Add(oldIndex);
                            uniqueMeshIndices.Add((int)sourceUv[0][oldIndex].z >> 12);
                        }
                    }
                }
                if (createSubMesh)
                {
                    targetIndices.Add(indexList);
                    targetTopology.Add(mesh.GetTopology(matchedSlots[i][0].index));
                }
                mergedMeshIndices.Add(uniqueMeshIndices.ToList());
            }

            {
                Mesh newMesh = new Mesh();
                newMesh.name = mesh.name;
                newMesh.indexFormat = targetVertices.Count >= 65536
                    ? IndexFormat.UInt32
                    : IndexFormat.UInt16;
                newMesh.SetVertices(targetVertices);
                newMesh.bindposes = mesh.bindposes;
                newMesh.SetBoneWeights(targetWeights.ToArray());
                bool particleSystemUsesMeshColor = GetParticleSystemsUsingRenderer(meshRenderer).Any(ps => ps.shape.useMeshColors);
                if (targetColor != null && (particleSystemUsesMeshColor || targetColor.Any(c => !c.Equals(Color.white))))
                {
                    newMesh.colors = targetColor.ToArray();
                }
                else if (targetColor32 != null && (particleSystemUsesMeshColor || targetColor32.Any(c => !c.Equals(new Color32(255, 255, 255, 255)))))
                {
                    newMesh.colors32 = targetColor32.ToArray();
                }
                for (int i = 0; i <= highestUsedUvSet; i++)
                {
                    if (!hasUvSet[i])
                        continue;
                    if (targetUv[i].Any(uv => uv.w != 0))
                    {
                        newMesh.SetUVs(i, targetUv[i]);
                    }
                    else if (targetUv[i].Any(uv => uv.z != 0))
                    {
                        newMesh.SetUVs(i, targetUv[i].Select(uv => new Vector3(uv.x, uv.y, uv.z)).ToArray());
                    }
                    else if (targetUv[i].Any(uv => uv.x != 0 || uv.y != 0))
                    {
                        newMesh.SetUVs(i, targetUv[i].Select(uv => new Vector2(uv.x, uv.y)).ToArray());
                    }
                }
                newMesh.bounds = mesh.bounds;
                newMesh.SetNormals(targetNormals);
                if (targetTangents.Any(t => t != Vector4.zero))
                    newMesh.SetTangents(targetTangents.Select(t => t == Vector4.zero ? new Vector4(1, 0, 0, 1) : t).ToArray());
                newMesh.subMeshCount = targetIndices.Count;
                for (int i = 0; i < targetIndices.Count; i++)
                {
                    newMesh.SetIndices(targetIndices[i].ToArray(), targetTopology[i], i);
                }

                int meshVertexCount = mesh.vertexCount;
                int newMeshVertexCount = newMesh.vertexCount;
                for (int i = 0; i < mesh.blendShapeCount; i++)
                {
                    for (int j = 0; j < mesh.GetBlendShapeFrameCount(i); j++)
                    {
                        var sourceDeltaVertices = new Vector3[meshVertexCount];
                        var sourceDeltaNormals = new Vector3[meshVertexCount];
                        var sourceDeltaTangents = new Vector3[meshVertexCount];
                        mesh.GetBlendShapeFrameVertices(i, j, sourceDeltaVertices, sourceDeltaNormals, sourceDeltaTangents);
                        var targetDeltaVertices = new Vector3[newMeshVertexCount];
                        var targetDeltaNormals = new Vector3[newMeshVertexCount];
                        var targetDeltaTangents = new Vector3[newMeshVertexCount];
                        for (int k = 0; k < newMeshVertexCount; k++)
                        {
                            var oldIndex = targetOldVertexIndex[k];
                            targetDeltaVertices[k] = sourceDeltaVertices[oldIndex];
                            targetDeltaNormals[k] = sourceDeltaNormals[oldIndex];
                            targetDeltaTangents[k] = sourceDeltaTangents[oldIndex];
                        }
                        var name = mesh.GetBlendShapeName(i);
                        var weight = mesh.GetBlendShapeFrameWeight(i, j);
                        newMesh.AddBlendShapeFrame(name, weight, targetDeltaVertices, targetDeltaNormals, targetDeltaTangents);
                    }
                }

                CreateUniqueAsset(newMesh, newMesh.name + ".asset");

                meshRenderer.sharedMesh = newMesh;
            }

            (string path, int index) GetOriginalSlot((string path, int index) slot) {
                if (!materialSlotRemap.TryGetValue(slot, out var remap)) {
                    Debug.LogWarning($"Could not find original material slot for {slot.path}.{slot.index}");
                    materialSlotRemap[slot] = remap = slot;
                }
                return remap;
            }

            var allSlots = matchedSlots.SelectMany(list => list).ToList();
            var uniqueMatchedMaterials = uniqueMatchedSlots.Select(list => list.Select(slot =>
                (slot.material, allSlots.Where(slot2 => slot2.material == slot.material).Select(slot2 => GetOriginalSlot((meshPath, slot2.index)).path).ToList())
            ).ToList()).ToList();
            // slots only merge when they use the same material, so each group keeps its material as is
            var optimizedMaterials = uniqueMatchedMaterials.Select(list => list[0].material).ToArray();

            for (int i = 0; i < uniqueMatchedMaterials.Count; i++)
            {
                if (uniqueMatchedMaterials[i][0].material == null)
                    continue;
                var originalSlot = GetOriginalSlot((meshPath, matchedSlots[i][0].index));
                if (uniqueMatchedMaterials[i].Count == 1)
                {
                    AddAnimationPathChange((originalSlot.path, $"m_Materials.Array.data[{originalSlot.index}]", typeof(SkinnedMeshRenderer)),
                        (meshPath, $"m_Materials.Array.data[{i}]", typeof(SkinnedMeshRenderer)));
                    if (!optimizedSlotSwapMaterials.TryGetValue(originalSlot, out var optimizedSwapMaterials))
                    {
                        optimizedSlotSwapMaterials[originalSlot] = optimizedSwapMaterials = new Dictionary<Material, Material>();
                    }
                    optimizedSwapMaterials[uniqueMatchedMaterials[i][0].material] = optimizedMaterials[i];
                }
            }

            meshRenderer.sharedMaterials = optimizedMaterials;

            foreach (var ps in GetParticleSystemsUsingRenderer(meshRenderer))
            {
                var shape = ps.shape;
                if (shape.useMeshMaterialIndex)
                {
                    shape.meshMaterialIndex = uniqueMatchedSlots.FindIndex(l => l.Any(slot => slot.index == shape.meshMaterialIndex));
                }
            }
        }
    }

    private void CombineSkinnedMeshes()
    {
        transformFromOldPath = new Dictionary<string, Transform>();
        foreach (var t in GetRootTransform().GetAllDescendants())
        {
            transformFromOldPath[GetPathToRoot(t)] = t;
        }
        var avDescriptor = GetAvatarDescriptor();
        var combinableMeshList = FindPossibleSkinnedMeshMerges();
        var (faceTrackingMeshes, faceTrackingNames) = GetFaceTrackingSetup();
        oldPathToMergedPaths.Clear();
        oldPathToMergedPath.Clear();
        var exclusions = GetAllExcludedTransforms();
        var movingTransforms = FindAllMovingTransforms();
        materialSlotRemap = new Dictionary<(string, int), (string, int)>();
        var combinableSkinnedMeshList = combinableMeshList
            .Select(l => l.Select(m => m as SkinnedMeshRenderer).Where(m => m != null).ToList())
            .Where(l => l.Count > 0)
            .Where(l => l[0].sharedMesh != null)
            .Where(l => l.All(m => !exclusions.Contains(m.transform)))
            .ToArray();
        if (combinableSkinnedMeshList.Length == 0)
            return;
        HashSet<Transform> humanoidBones = new();
        HashSet<Transform> childrenOfHumanoidBones = new();
        if (avDescriptor.TryGetComponent<Animator>(out var animator) && animator != null && animator.isHuman)
        {
            foreach (var humanBone in System.Enum.GetValues(typeof(HumanBodyBones)).Cast<HumanBodyBones>())
            {
                if (humanBone == HumanBodyBones.LastBone)
                    continue;
                var boneTransform = animator.GetBoneTransform(humanBone);
                if (boneTransform != null)
                {
                    humanoidBones.Add(boneTransform);
                    childrenOfHumanoidBones.UnionWith(boneTransform.GetAllDescendants());
                }
            }
            childrenOfHumanoidBones.ExceptWith(humanoidBones);
        }
        var originalRootPosition = GetRootTransform().position;
        var originalRootRotation = GetRootTransform().rotation;
        GetRootTransform().SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        int totalMeshCount = combinableSkinnedMeshList.Sum(l => l.Count);
        int currentMeshCount = 0;
        LogToFile($"Combining {totalMeshCount} SkinnedMeshRenderers into {combinableSkinnedMeshList.Length} combined meshes:");
        using var _ = log.IndentScope();
        for (int combinedMeshID = 0; combinedMeshID < combinableSkinnedMeshList.Length; combinedMeshID++)
        {
            var combinableSkinnedMeshes = combinableSkinnedMeshList[combinedMeshID];
            var basicMergedMeshes = combinableSkinnedMeshes.Select(r => new List<Renderer> { r }).ToList();

            Profiler.StartSection("CombineMeshData");

            int totalVertexCount = combinableSkinnedMeshes.Sum(m => m.sharedMesh.vertexCount);

            var targetBones = new List<Transform>();
            var targetBindPoses = new List<Matrix4x4>();
            var targetBoneIndexMap = new Dictionary<(int meshID, int boneID), int>();
            var transformToTargetIndices = new Dictionary<Transform, HashSet<int>>();

            int AddNewBone(Transform boneTransform, Matrix4x4 bindPose)
            {
                targetBones.Add(boneTransform);
                targetBindPoses.Add(bindPose);
                if (!transformToTargetIndices.TryGetValue(boneTransform, out var existingTransformIndices))
                {
                    transformToTargetIndices[boneTransform] = existingTransformIndices = new HashSet<int>();
                }
                existingTransformIndices.Add(targetBones.Count - 1);
                return targetBones.Count - 1;
            }
            int GetNewBoneIndex(int boneID, int meshID, Transform boneTransform, Matrix4x4 bindPose)
            {
                if (targetBoneIndexMap.TryGetValue((meshID, boneID), out int index))
                    return index;
                if (!transformToTargetIndices.TryGetValue(boneTransform, out var existingTransformIndices))
                {
                    transformToTargetIndices[boneTransform] = existingTransformIndices = new HashSet<int>();
                }
                foreach (var i in existingTransformIndices)
                {
                    if (targetBones[i] == boneTransform && targetBindPoses[i] == bindPose)
                    {
                        return targetBoneIndexMap[(meshID, boneID)] = i;
                    }
                }
                return targetBoneIndexMap[(meshID, boneID)] = AddNewBone(boneTransform, bindPose);
            }

            var anyHasUvSet = new bool[8] {
                true,
                combinableSkinnedMeshes.Any(m => m.sharedMesh.HasVertexAttribute(VertexAttribute.TexCoord1)),
                combinableSkinnedMeshes.Any(m => m.sharedMesh.HasVertexAttribute(VertexAttribute.TexCoord2)),
                combinableSkinnedMeshes.Any(m => m.sharedMesh.HasVertexAttribute(VertexAttribute.TexCoord3)),
                combinableSkinnedMeshes.Any(m => m.sharedMesh.HasVertexAttribute(VertexAttribute.TexCoord4)),
                combinableSkinnedMeshes.Any(m => m.sharedMesh.HasVertexAttribute(VertexAttribute.TexCoord5)),
                combinableSkinnedMeshes.Any(m => m.sharedMesh.HasVertexAttribute(VertexAttribute.TexCoord6)),
                combinableSkinnedMeshes.Any(m => m.sharedMesh.HasVertexAttribute(VertexAttribute.TexCoord7)),
            };
            var targetUv = Enumerable.Range(0, 8).Select(i => anyHasUvSet[i] ? new List<Vector4>(totalVertexCount) : null).ToArray();
            bool useColor32 = !combinableSkinnedMeshes.Any(m => m.sharedMesh.HasVertexAttribute(VertexAttribute.Color)
                && m.sharedMesh.GetVertexAttributeFormat(VertexAttribute.Color) != VertexAttributeFormat.UNorm8);
            var targetColor = new List<Color>(useColor32 ? 0 : totalVertexCount);
            var targetColor32 = new List<Color32>(useColor32 ? totalVertexCount : 0);
            var targetVertices = new List<Vector3>(totalVertexCount);
            var targetIndices = new List<int[]>();
            var targetTopology = new List<MeshTopology>();
            var targetNormals = new List<Vector3>(totalVertexCount);
            var targetTangents = new List<Vector4>(totalVertexCount);
            var targetWeights = new List<BoneWeight>(totalVertexCount);
            var targetBounds = combinableSkinnedMeshes[0].localBounds;

            int GetTransformPriority(Transform t)
            {
                if (t == null)
                    return 9_000;
                if (humanoidBones.Contains(t))
                    return 0;
                if (childrenOfHumanoidBones.Contains(t))
                    return 1_000;
                return 2_000;
            }
            var rootBoneGrouping = combinableSkinnedMeshes.Select((mesh, index) =>
                (mesh, transform: mesh.rootBone == null ? mesh.transform : mesh.rootBone, index: GetTransformPriority(mesh.rootBone) + index))
                .GroupBy(t => t.transform)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.First().index)
                .ToList();
            var probeAnchorGrouping = combinableSkinnedMeshes.Select((mesh, index) =>
                (mesh, transform: mesh.probeAnchor == null ? mesh.transform : mesh.probeAnchor, index: GetTransformPriority(mesh.probeAnchor) + index))
                .GroupBy(t => t.transform)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.First().index)
                .ToList();

            var targetRootBone = rootBoneGrouping[0].Key;
            var targetProbeAnchor = probeAnchorGrouping[0].Key;


            keepTransforms.Add(targetProbeAnchor);
            keepTransforms.Add(targetRootBone);

            var toLocal = targetRootBone.worldToLocalMatrix;

            AddNewBone(combinableSkinnedMeshes[0].transform, combinableSkinnedMeshes[0].transform.worldToLocalMatrix);

            string newMeshName = combinableSkinnedMeshes[0].name;
            string newPath = GetPathToRoot(combinableSkinnedMeshes[0]);

            var basicMergedMeshesList = basicMergedMeshes.SelectMany(list => list.Cast<SkinnedMeshRenderer>()).ToList();
            var mergedMeshPaths = basicMergedMeshes.Select(list => list.Select(r => GetPathToRoot(r)).ToList()).ToList();
            basicMergedMeshesList.ForEach(r => oldPathToMergedPaths[GetPathToRoot(r)] = mergedMeshPaths);
            basicMergedMeshesList.ForEach(r => oldPathToMergedPath[GetPathToRoot(r)] = newPath);

            // this caches the results for later use when optimizing the materials
            basicMergedMeshesList.ForEach(r => GetFirstMaterialOnPath(GetPathToRoot(r)));

            int bindPoseMeshID = -1;

            LogToFile($"- Target path '{newPath}':");
            using var __ = log.IndentScope();
            if (combinableSkinnedMeshes.Count > 1)
            {
                LogToFile($"- Merging {combinableSkinnedMeshes.Count} skinned meshes:");
                foreach (var blob in basicMergedMeshes)
                {
                    for (int i = 0; i < blob.Count; i++)
                    {
                        LogToFile($"- {GetPathToRoot(blob[i])}", i == 0 ? 1 : 2);
                    }
                }
            }
            LogToFile($"- Basic mesh info:");
            using (log.IndentScope())
            {
                LogToFile($"- Total vertices: {totalVertexCount}");
                LogToFile($"- Total submeshes: {basicMergedMeshesList.Sum(m => m.sharedMesh.subMeshCount)}");
                LogToFile($"- Target root bone: '{GetPathToRoot(targetRootBone)}'");
                LogToFile($"- Target light anchor: '{GetPathToRoot(targetProbeAnchor)}'");
                var usedAttributes = new Dictionary<string, int>();
                var sourcesWithExtraMaterialSlots = new List<SkinnedMeshRenderer>();
                foreach (var skinnedMesh in basicMergedMeshesList)
                {
                    var mesh = skinnedMesh.sharedMesh;
                    for (int i = 0; i < mesh.vertexAttributeCount; i++)
                    {
                        var a = mesh.GetVertexAttribute(i);
                        var s = $"- {a.attribute,-12}  {a.dimension}x{a.format,-8}";
                        if (!usedAttributes.TryGetValue(s, out int count))
                            count = 0;
                        usedAttributes[s] = count + 1;
                    }
                    if (skinnedMesh.sharedMaterials.Length > mesh.subMeshCount)
                    {
                        sourcesWithExtraMaterialSlots.Add(skinnedMesh);
                    }
                }
                LogToFile($"- Source vertex attributes:");
                foreach ((var attr, var count) in usedAttributes.OrderBy(s => s.Key))
                {
                    LogToFile($"{attr} {count,3}", 1);
                }
                void LogGrouping(List<IGrouping<Transform, (SkinnedMeshRenderer mesh, Transform transform, int index)>> grouping, string title)
                {
                    if (grouping.Count == 1)
                    {
                        LogToFile($"- {title}: '{GetPathToRoot(grouping.First().Key)}'");
                    }
                    else
                    {
                        LogToFile($"- {title} ({grouping.Count}):");
                        bool showGroupCounts = grouping.Any(g => g.Count() != 1);
                        foreach (var group in grouping)
                        {
                            LogToFile($"- {GetPathToRoot(group.Key)}{(showGroupCounts ? $" ({group.Count()})" : "")}", 1);
                        }
                    }
                }
                LogGrouping(rootBoneGrouping, "Source root bone");
                LogGrouping(probeAnchorGrouping, "Source light anchor");
                if (sourcesWithExtraMaterialSlots.Count > 0)
                    LogGrouping(sourcesWithExtraMaterialSlots.Select((mesh, index) => (mesh, mesh.transform, index)).GroupBy(x => x.transform).ToList(), "Extra material slot mesh");
            }

            int targetMaterialSlotIndex = 0;
            foreach (SkinnedMeshRenderer skinnedMesh in basicMergedMeshesList)
            {
                DisplayProgressBar($"Combining mesh ({++currentMeshCount}/{totalMeshCount}) {skinnedMesh.name}");

                bindPoseMeshID++;
                var blobMeshID = basicMergedMeshes.FindIndex(blob => blob.Contains(skinnedMesh));
                var currentMeshPath = GetPathToRoot(skinnedMesh);
                var mesh = skinnedMesh.sharedMesh;
                var bindPoseIDMap = new Dictionary<int, int>();
                var indexOffset = targetVertices.Count;
                var sourceVertices = mesh.vertices;
                var sourceNormals = mesh.normals;
                var sourceTangents = mesh.tangents;
                var sourceWeights = mesh.boneWeights;
                var rootBone = skinnedMesh.rootBone == null ? skinnedMesh.transform : skinnedMesh.rootBone;
                var sourceBones = skinnedMesh.bones;
                for (int i = 0; i < sourceBones.Length; i++)
                {
                    if (sourceBones[i] == null)
                        sourceBones[i] = rootBone;
                }
                var sourceBindPoses = mesh.bindposes;
                var bindPoseCount = sourceBindPoses.Length;
                if (sourceBones.Length != bindPoseCount)
                {
                    Debug.LogWarning($"Bone count ({sourceBones.Length}) does not match bind pose count ({bindPoseCount}) on {currentMeshPath}");
                    LogToFile($"- Warning: Bone count ({sourceBones.Length}) does not match bind pose count ({bindPoseCount}) on {currentMeshPath}");
                    bindPoseCount = Math.Min(sourceBones.Length, bindPoseCount);
                }
                var aabb = skinnedMesh.localBounds;
                var m = toLocal * rootBone.localToWorldMatrix;
                targetBounds.Encapsulate(m.MultiplyPoint3x4(aabb.extents.Multiply(1, 1, 1) + aabb.center));
                targetBounds.Encapsulate(m.MultiplyPoint3x4(aabb.extents.Multiply(1, 1, -1) + aabb.center));
                targetBounds.Encapsulate(m.MultiplyPoint3x4(aabb.extents.Multiply(1, -1, 1) + aabb.center));
                targetBounds.Encapsulate(m.MultiplyPoint3x4(aabb.extents.Multiply(1, -1, -1) + aabb.center));
                targetBounds.Encapsulate(m.MultiplyPoint3x4(aabb.extents.Multiply(-1, 1, 1) + aabb.center));
                targetBounds.Encapsulate(m.MultiplyPoint3x4(aabb.extents.Multiply(-1, 1, -1) + aabb.center));
                targetBounds.Encapsulate(m.MultiplyPoint3x4(aabb.extents.Multiply(-1, -1, 1) + aabb.center));
                targetBounds.Encapsulate(m.MultiplyPoint3x4(aabb.extents.Multiply(-1, -1, -1) + aabb.center));
                if (sourceWeights.Length != sourceVertices.Length || bindPoseCount == 0)
                {
                    var defaultWeight = new BoneWeight
                    {
                        boneIndex0 = 0,
                        boneIndex1 = 0,
                        boneIndex2 = 0,
                        boneIndex3 = 0,
                        weight0 = 1,
                        weight1 = 0,
                        weight2 = 0,
                        weight3 = 0
                    };
                    sourceWeights = Enumerable.Repeat(defaultWeight, sourceVertices.Length).ToArray();
                    sourceBones = new Transform[1] { rootBone.transform };
                    sourceBindPoses = new Matrix4x4[1] { Matrix4x4.identity };
                    keepTransforms.Add(rootBone.transform);
                    bindPoseCount = 1;
                }

                var currentHasUvSet = new bool[8] {
                    true,
                    mesh.HasVertexAttribute(VertexAttribute.TexCoord1),
                    mesh.HasVertexAttribute(VertexAttribute.TexCoord2),
                    mesh.HasVertexAttribute(VertexAttribute.TexCoord3),
                    mesh.HasVertexAttribute(VertexAttribute.TexCoord4),
                    mesh.HasVertexAttribute(VertexAttribute.TexCoord5),
                    mesh.HasVertexAttribute(VertexAttribute.TexCoord6),
                    mesh.HasVertexAttribute(VertexAttribute.TexCoord7),
                };

                for (int i = 1; i < 8; i++)
                {
                    if (!anyHasUvSet[i])
                        continue;
                    var uvs = new List<Vector4>();
                    var j = i;
                    while (!currentHasUvSet[j] && j > 0)
                    {
                        j--;
                    }
                    mesh.GetUVs(j, uvs);
                    targetUv[i].AddRange(uvs.Count == sourceVertices.Length ? uvs : Enumerable.Repeat(Vector4.zero, sourceVertices.Length));
                }

                var sourceUv = new List<Vector4>();
                mesh.GetUVs(0, sourceUv);
                if (sourceUv.Count != sourceVertices.Length)
                {
                    sourceUv = Enumerable.Repeat(Vector4.zero, sourceVertices.Length).ToList();
                }

                if (mesh.HasVertexAttribute(VertexAttribute.Color))
                {
                    if (useColor32)
                    {
                        targetColor32.AddRange(mesh.colors32);
                    }
                    else
                    {
                        targetColor.AddRange(mesh.colors);
                    }
                }
                else
                {
                    if (useColor32)
                    {
                        targetColor32.AddRange(Enumerable.Repeat(new Color32(255, 255, 255, 255), sourceVertices.Length));
                    }
                    else
                    {
                        targetColor.AddRange(Enumerable.Repeat(Color.white, sourceVertices.Length));
                    }
                }

                sourceNormals = sourceNormals.Length != sourceVertices.Length ? new Vector3[sourceVertices.Length] : sourceNormals;
                sourceTangents = sourceTangents.Length != sourceVertices.Length ? new Vector4[sourceVertices.Length] : sourceTangents;

                if (!blendShapesToBake.TryGetValue(skinnedMesh, out var blendShapeIDs))
                {
                    blendShapeIDs = new List<int>();
                }

                if (blendShapeIDs.Count > 0)
                {
                    LogToFile($"- Baking {blendShapeIDs.Count} blendshapes from '{currentMeshPath}':");
                }
                foreach (int blendShapeID in blendShapeIDs)
                {
                    var weight = Mathf.Clamp(skinnedMesh.GetBlendShapeWeight(blendShapeID) / 100f, 0, 1);
                    var deltaVertices = new Vector3[sourceVertices.Length];
                    var deltaNormals = new Vector3[sourceVertices.Length];
                    var deltaTangents = new Vector3[sourceVertices.Length];
                    mesh.GetBlendShapeFrameVertices(blendShapeID, 0, deltaVertices, deltaNormals, deltaTangents);
                    for (int i = 0; i < sourceVertices.Length; i++)
                    {
                        sourceVertices[i] += deltaVertices[i] * weight;
                        sourceNormals[i] += deltaNormals[i] * weight;
                        sourceTangents[i] += (Vector4)(deltaTangents[i] * weight);
                    }
                    LogToFile($"- {mesh.GetBlendShapeName(blendShapeID)} ({weight * 100f:F1}%)", 1);
                }

                for (int vertIndex = 0; vertIndex < sourceVertices.Length; vertIndex++)
                {
                    int GetNewBoneIndexForCurrentMesh(int oldIndex)
                    {
                        oldIndex = oldIndex >= bindPoseCount ? 0 : Math.Max(0, oldIndex);
                        if (!bindPoseIDMap.TryGetValue(oldIndex, out int newIndex))
                        {
                            newIndex = GetNewBoneIndex(oldIndex, bindPoseMeshID, sourceBones[oldIndex], sourceBindPoses[oldIndex]);
                            bindPoseIDMap[oldIndex] = newIndex;
                        }
                        return newIndex;
                    }
                    var boneWeight = sourceWeights[vertIndex];
                    boneWeight.boneIndex0 = GetNewBoneIndexForCurrentMesh(boneWeight.boneIndex0);
                    boneWeight.boneIndex1 = GetNewBoneIndexForCurrentMesh(boneWeight.boneIndex1);
                    boneWeight.boneIndex2 = GetNewBoneIndexForCurrentMesh(boneWeight.boneIndex2);
                    boneWeight.boneIndex3 = GetNewBoneIndexForCurrentMesh(boneWeight.boneIndex3);
                    targetWeights.Add(boneWeight);
                    targetVertices.Add(sourceVertices[vertIndex]);
                    targetNormals.Add(sourceNormals[vertIndex]);
                    targetTangents.Add(sourceTangents[vertIndex]);
                    targetUv[0].Add(new Vector4(sourceUv[vertIndex].x, sourceUv[vertIndex].y, sourceUv[vertIndex].z + (blobMeshID << 12), sourceUv[vertIndex].w));
                }

                for (var matID = 0; matID < skinnedMesh.sharedMaterials.Length; matID++)
                {
                    materialSlotRemap[(newPath, targetMaterialSlotIndex++)] = (currentMeshPath, matID);
                }
                int materialSubMeshCount = Math.Min(skinnedMesh.sharedMaterials.Length, mesh.subMeshCount);
                for (var matID = 0; matID < materialSubMeshCount; matID++)
                {
                    int[] indices = mesh.GetIndices(matID);
                    for (uint i = 0; i < indices.Length; i++)
                    {
                        indices[i] += indexOffset;
                    }
                    targetIndices.Add(indices);
                    targetTopology.Add(mesh.GetTopology(matID));
                }
            }
            Profiler.EndSection();

            var blendShapeWeights = new Dictionary<string, float>();

            var combinedMesh = new Mesh();
            combinedMesh.indexFormat = targetVertices.Count >= 65536 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            combinedMesh.SetVertices(targetVertices);
            combinedMesh.bindposes = targetBindPoses.ToArray();
            combinedMesh.SetBoneWeights(targetWeights.ToArray());
            bool hasParticleSystemUsingMeshColor = basicMergedMeshesList.Any(r => GetParticleSystemsUsingRenderer(r).Any(ps => ps.shape.useMeshColors));
            if (!useColor32 && (hasParticleSystemUsingMeshColor || targetColor.Any(c => !c.Equals(Color.white))))
            {
                combinedMesh.colors = targetColor.ToArray();
            }
            else if (useColor32 && (hasParticleSystemUsingMeshColor || targetColor32.Any(c => !c.Equals(new Color32(255, 255, 255, 255)))))
            {
                combinedMesh.colors32 = targetColor32.ToArray();
            }
            for (int i = 0; i < 8; i++)
            {
                if (anyHasUvSet[i] && targetUv[i].Any(uv => !uv.Equals(Vector4.zero)))
                {
                    combinedMesh.SetUVs(i, targetUv[i]);
                }
            }
            combinedMesh.bounds = combinableSkinnedMeshes[0].sharedMesh.bounds;
            combinedMesh.SetNormals(targetNormals);
            combinedMesh.SetTangents(targetTangents);
            combinedMesh.subMeshCount = targetIndices.Count;
            combinedMesh.name = newMeshName;
            for (int i = 0; i < targetIndices.Count; i++)
            {
                combinedMesh.SetIndices(targetIndices[i], targetTopology[i], i);
            }

            Profiler.StartSection("CopyCombinedMeshBlendShapes");
            var usedBlendShapeNames = new HashSet<string>();
            bool blobHasFaceTracking = basicMergedMeshesList.Any(faceTrackingMeshes.Contains);
            if (blobHasFaceTracking)
                usedBlendShapeNames.UnionWith(faceTrackingNames);
            var blendShapeMeshIDtoNewName = new Dictionary<(int meshID, int blendShapeID), string>();
            var combinableMeshPaths = new HashSet<string>(basicMergedMeshesList.Select(s => GetPathToRoot(s)));
            var meshPathToID = basicMergedMeshesList.Select((s, i) => (GetPathToRoot(s), i)).ToDictionary(s => s.Item1, s => s.Item2);
            var usedBlendShapesInCombinedMesh = new HashSet<string>(
                usedBlendShapes.Where(s => combinableMeshPaths.Contains(s.Substring(0, s.IndexOf("/blendShape.")))));
            var faceTrackingShapes = new Dictionary<string, List<int>>(System.StringComparer.Ordinal);
            for (int meshID = 0; meshID < basicMergedMeshesList.Count; meshID++)
            {
                var renderer = basicMergedMeshesList[meshID];
                if (!faceTrackingMeshes.Contains(renderer))
                    continue;
                foreach (var name in faceTrackingNames)
                {
                    if (renderer.sharedMesh.GetBlendShapeIndex(name) < 0)
                        continue;
                    if (!faceTrackingShapes.TryGetValue(name, out var meshIDs))
                        faceTrackingShapes[name] = meshIDs = new List<int>();
                    meshIDs.Add(meshID);
                    // These paths are copied by name below, not by the generic ratio/name merging pass.
                    usedBlendShapesInCombinedMesh.Remove(GetPathToRoot(renderer) + "/blendShape." + name);
                }
            }
            var allMergedBlendShapes = new List<List<(string blendshape, float weight)>>();
            if (MergeSameRatioBlendShapes)
            {
                allMergedBlendShapes.AddRange(FindMergeableBlendShapes(basicMergedMeshesList));
                var usedBlendShapesInMergedBlobs = new HashSet<string>(allMergedBlendShapes.SelectMany(s => s).Select(s => s.blendshape));
                allMergedBlendShapes.AddRange(usedBlendShapesInCombinedMesh.Where(s => !usedBlendShapesInMergedBlobs.Contains(s)).Select(s => new List<(string blendshape, float weight)> { (s, 1) }));
            }
            else
            {
                allMergedBlendShapes.AddRange(usedBlendShapesInCombinedMesh.Select(s => new List<(string blendshape, float weight)> { (s, 1) }));
            }
            var vertexOffset = new List<int>() {0};
            for (int i = 0; i < basicMergedMeshesList.Count - 1; i++)
            {
                vertexOffset.Add(vertexOffset[i] + basicMergedMeshesList[i].sharedMesh.vertexCount);
            }
            int combinedMeshVertexCount = combinedMesh.vertexCount;
            foreach (var entry in faceTrackingShapes.OrderBy(e => e.Key, System.StringComparer.Ordinal))
            {
                string name = entry.Key;
                var frameWeights = new SortedSet<float>();
                var sources = new List<(int meshID, float[] weights, Vector3[][] vertices, Vector3[][] normals, Vector3[][] tangents)>();
                foreach (int meshID in entry.Value)
                {
                    var renderer = basicMergedMeshesList[meshID];
                    var mesh = renderer.sharedMesh;
                    int shapeID = mesh.GetBlendShapeIndex(name);
                    int frameCount = mesh.GetBlendShapeFrameCount(shapeID);
                    var weights = new float[frameCount];
                    var vertices = new Vector3[frameCount][];
                    var normals = new Vector3[frameCount][];
                    var tangents = new Vector3[frameCount][];
                    for (int frame = 0; frame < frameCount; frame++)
                    {
                        weights[frame] = mesh.GetBlendShapeFrameWeight(shapeID, frame);
                        frameWeights.Add(weights[frame]);
                        vertices[frame] = new Vector3[mesh.vertexCount];
                        normals[frame] = new Vector3[mesh.vertexCount];
                        tangents[frame] = new Vector3[mesh.vertexCount];
                        mesh.GetBlendShapeFrameVertices(shapeID, frame, vertices[frame], normals[frame], tangents[frame]);
                    }
                    sources.Add((meshID, weights, vertices, normals, tangents));
                    blendShapeMeshIDtoNewName[(meshID, shapeID)] = name;
                    if (!blendShapeWeights.ContainsKey(name))
                        blendShapeWeights[name] = renderer.GetBlendShapeWeight(shapeID);
                    AddAnimationPathChange(
                        (GetPathToRoot(renderer), "blendShape." + name, typeof(SkinnedMeshRenderer)),
                        (newPath, "blendShape." + name, typeof(SkinnedMeshRenderer)));
                }
                // Different source frame layouts need the union of their interpolation breakpoints.
                // Unity supplies an implicit zero frame for shapes whose frames are all on one side of zero.
                if (sources.Count > 1 && frameWeights.Count > 0 && frameWeights.Min < 0 && frameWeights.Max > 0
                    && sources.Any(s => s.weights.Length > 0
                    && (s.weights[0] > 0 || s.weights[^1] < 0)))
                    frameWeights.Add(0);
                foreach (float frameWeight in frameWeights)
                {
                    var vertices = new Vector3[combinedMeshVertexCount];
                    var normals = new Vector3[combinedMeshVertexCount];
                    var tangents = new Vector3[combinedMeshVertexCount];
                    foreach (var source in sources)
                    {
                        if (source.weights.Length == 0)
                            continue;
                        var weights = source.weights.ToList();
                        var frameIDs = Enumerable.Range(0, weights.Count).ToList();
                        if (weights[0] > 0)
                        {
                            weights.Insert(0, 0);
                            frameIDs.Insert(0, -1);
                        }
                        else if (weights[^1] < 0)
                        {
                            weights.Add(0);
                            frameIDs.Add(-1);
                        }
                        int upper = 1;
                        while (upper < weights.Count - 1 && frameWeight > weights[upper])
                            upper++;
                        int lower = weights.Count == 1 ? 0 : upper - 1;
                        upper = Math.Min(upper, weights.Count - 1);
                        float factor = lower == upper ? 0 : (frameWeight - weights[lower]) / (weights[upper] - weights[lower]);
                        Vector3 Sample(Vector3[][] frames, int vertex)
                        {
                            var a = frameIDs[lower] < 0 ? Vector3.zero : frames[frameIDs[lower]][vertex];
                            var b = frameIDs[upper] < 0 ? Vector3.zero : frames[frameIDs[upper]][vertex];
                            if (frameWeight == weights[lower] || lower == upper)
                                return a;
                            if (frameWeight == weights[upper])
                                return b;
                            return Vector3.LerpUnclamped(a, b, factor);
                        }
                        int vertexCount = basicMergedMeshesList[source.meshID].sharedMesh.vertexCount;
                        for (int vertex = 0; vertex < vertexCount; vertex++)
                        {
                            int targetVertex = vertexOffset[source.meshID] + vertex;
                            vertices[targetVertex] = Sample(source.vertices, vertex);
                            normals[targetVertex] = Sample(source.normals, vertex);
                            tangents[targetVertex] = Sample(source.tangents, vertex);
                        }
                    }
                    combinedMesh.AddBlendShapeFrame(name, frameWeight, vertices, normals, tangents);
                }
                LogToFile($"- Kept face tracking blendshape '{name}' on '{newPath}' from {sources.Count} meshes");
            }
            foreach (var mergedBlendShapes in allMergedBlendShapes)
            {
                if (mergedBlendShapes.Count == 1)
                {
                    var path = mergedBlendShapes[0].blendshape.Substring(0, mergedBlendShapes[0].blendshape.IndexOf("/blendShape."));
                    var skinnedMesh = GetTransformFromPath(path).GetComponent<SkinnedMeshRenderer>();
                    var mesh = skinnedMesh.sharedMesh;
                    var oldName = mergedBlendShapes[0].blendshape.Substring(path.Length + 12);
                    var name = GenerateUniqueName(oldName, usedBlendShapeNames);
                    var meshID = meshPathToID[path];
                    var blendShapeID = mesh.GetBlendShapeIndex(oldName);
                    if (blendShapeID == -1)
                        continue;
                    blendShapeMeshIDtoNewName[(meshID, blendShapeID)] = name;
                    blendShapeWeights[name] = skinnedMesh.GetBlendShapeWeight(blendShapeID);
                    AddAnimationPathChange(
                        (path, "blendShape." + oldName, typeof(SkinnedMeshRenderer)),
                        (newPath, "blendShape." + name, typeof(SkinnedMeshRenderer)));
                    for (int j = 0; j < mesh.GetBlendShapeFrameCount(blendShapeID); j++)
                    {
                        int meshVertexCount = mesh.vertexCount;
                        var sourceDeltaVertices = new Vector3[meshVertexCount];
                        var sourceDeltaNormals = new Vector3[meshVertexCount];
                        var sourceDeltaTangents = new Vector3[meshVertexCount];
                        mesh.GetBlendShapeFrameVertices(blendShapeID, j, sourceDeltaVertices, sourceDeltaNormals, sourceDeltaTangents);
                        var targetDeltaVertices = new Vector3[combinedMeshVertexCount];
                        var targetDeltaNormals = new Vector3[combinedMeshVertexCount];
                        var targetDeltaTangents = new Vector3[combinedMeshVertexCount];
                        for (int k = 0; k < meshVertexCount; k++)
                        {
                            int vertIndex = k + vertexOffset[meshID];
                            targetDeltaVertices[vertIndex] = sourceDeltaVertices[k];
                            targetDeltaNormals[vertIndex] = sourceDeltaNormals[k];
                            targetDeltaTangents[vertIndex] = sourceDeltaTangents[k];
                        }
                        var weight = mesh.GetBlendShapeFrameWeight(blendShapeID, j);
                        combinedMesh.AddBlendShapeFrame(name, weight, targetDeltaVertices, targetDeltaNormals, targetDeltaTangents);
                    }
                }
                else
                {
                    var oldPath = mergedBlendShapes[0].blendshape.Substring(0, mergedBlendShapes[0].blendshape.IndexOf("/blendShape."));
                    var oldName = mergedBlendShapes[0].blendshape.Substring(mergedBlendShapes[0].blendshape.IndexOf("/blendShape.") + 12);
                    var name = GenerateUniqueName(oldName, usedBlendShapeNames);
                    AddAnimationPathChange(
                        (oldPath, "blendShape." + oldName, typeof(SkinnedMeshRenderer)),
                        (newPath, "blendShape." + name, typeof(SkinnedMeshRenderer)));
                    var targetDeltaVertices = new Vector3[combinedMeshVertexCount];
                    var targetDeltaNormals = new Vector3[combinedMeshVertexCount];
                    var targetDeltaTangents = new Vector3[combinedMeshVertexCount];
                    LogToFile($"- Merging {mergedBlendShapes.Count} blendshapes into {name}");
                    bool first = true;
                    foreach (var toMerge in mergedBlendShapes)
                    {
                        var path = toMerge.blendshape[..toMerge.blendshape.IndexOf("/blendShape.")];
                        var blendShapeName = toMerge.blendshape[(path.Length + 12)..];
                        LogToFile($"- {path}.{blendShapeName} with weight {toMerge.weight * 100:F2}%", 1);
                        var skinnedMesh = GetTransformFromPath(path).GetComponent<SkinnedMeshRenderer>();
                        var mesh = skinnedMesh.sharedMesh;
                        var blendShapeID = mesh.GetBlendShapeIndex(blendShapeName);
                        if (blendShapeID == -1)
                            continue;
                        var meshID = meshPathToID[path];
                        blendShapeMeshIDtoNewName[(meshID, blendShapeID)] = name;
                        if (first)
                        {
                            blendShapeWeights[name] = skinnedMesh.GetBlendShapeWeight(blendShapeID);
                            first = false;
                        }
                        int meshVertexCount = mesh.vertexCount;
                        var sourceDeltaVertices = new Vector3[meshVertexCount];
                        var sourceDeltaNormals = new Vector3[meshVertexCount];
                        var sourceDeltaTangents = new Vector3[meshVertexCount];
                        mesh.GetBlendShapeFrameVertices(blendShapeID, 0, sourceDeltaVertices, sourceDeltaNormals, sourceDeltaTangents);
                        for (int k = 0; k < meshVertexCount; k++)
                        {
                            int vertIndex = k + vertexOffset[meshID];
                            targetDeltaVertices[vertIndex] += sourceDeltaVertices[k] * toMerge.weight;
                            targetDeltaNormals[vertIndex] += sourceDeltaNormals[k] * toMerge.weight;
                            targetDeltaTangents[vertIndex] += sourceDeltaTangents[k] * toMerge.weight;
                        }
                    }
                    combinedMesh.AddBlendShapeFrame(name, 100, targetDeltaVertices, targetDeltaNormals, targetDeltaTangents);
                }
            }
            Profiler.EndSection();
            
            var targetRenderer = combinableSkinnedMeshes[0];

            if (HasFaceTrackingEnabled()
                && GetFaceTrackingField(avDescriptor, "faceTrackingMeshes") is IList<SkinnedMeshRenderer> configuredFaceTrackingMeshes
                && configuredFaceTrackingMeshes.Any(r => r != null && basicMergedMeshesList.Contains(r)))
            {
                var originalEntries = configuredFaceTrackingMeshes.ToArray();
                var rewrittenEntries = new List<SkinnedMeshRenderer>();
                SkinnedMeshRenderer Remap(SkinnedMeshRenderer renderer) =>
                    renderer != null && basicMergedMeshesList.Contains(renderer) ? targetRenderer : renderer;
                foreach (var renderer in originalEntries.Take(16).Select(Remap))
                {
                    if (!rewrittenEntries.Contains(renderer))
                        rewrittenEntries.Add(renderer);
                }
                configuredFaceTrackingMeshes.Clear();
                foreach (var renderer in rewrittenEntries)
                    configuredFaceTrackingMeshes.Add(renderer);
            }

            if (avDescriptor.customEyeLookSettings.eyelidType == VRCAvatarDescriptor.EyelidType.Blendshapes
                && avDescriptor.customEyeLookSettings.eyelidsSkinnedMesh != null)
            {
                var eyeLookMeshRenderer = avDescriptor.customEyeLookSettings.eyelidsSkinnedMesh;
                var ids = avDescriptor.customEyeLookSettings.eyelidsBlendshapes;
                for (int i = 0; i < ids.Length; i++)
                {
                    if (ids[i] < 0)
                        continue;
                    for (int meshID = 0; meshID < basicMergedMeshesList.Count; meshID++)
                    {
                        if (basicMergedMeshesList[meshID] == eyeLookMeshRenderer)
                        {
                            avDescriptor.customEyeLookSettings.eyelidsSkinnedMesh = targetRenderer;
                            ids[i] = combinedMesh.GetBlendShapeIndex(blendShapeMeshIDtoNewName[(meshID, ids[i])]);
                        }
                    }
                }
                avDescriptor.customEyeLookSettings.eyelidsBlendshapes = ids;
            }

            for (int meshID = 0; meshID < basicMergedMeshesList.Count; meshID++)
            {
                var oldVisemeMesh = basicMergedMeshesList[meshID];
                if (avDescriptor.VisemeSkinnedMesh == oldVisemeMesh)
                {
                    avDescriptor.VisemeSkinnedMesh = targetRenderer;
                    string CalculateNewBlendShapeName(string blendShapeName) {
                        var blendShapeID = oldVisemeMesh.sharedMesh.GetBlendShapeIndex(blendShapeName ?? "");
                        return blendShapeMeshIDtoNewName.TryGetValue((meshID, blendShapeID), out string newName)
                            ? newName : $"MISSING \"{blendShapeName}\"";
                    }
                    avDescriptor.VisemeBlendShapes = avDescriptor.VisemeBlendShapes.Select(CalculateNewBlendShapeName).ToArray();
                    avDescriptor.MouthOpenBlendShapeName = CalculateNewBlendShapeName(avDescriptor.MouthOpenBlendShapeName);
                }
            }

            var materials = basicMergedMeshesList.SelectMany(r => r.sharedMaterials).ToArray();
            var originalMeshSlots = basicMergedMeshesList.SelectMany(r => MaterialSlot.GetAllSlotsFrom(r)).ToList();
            foreach (var renderer in basicMergedMeshesList)
            {
                foreach (var ps in GetParticleSystemsUsingRenderer(renderer))
                {
                    var shape = ps.shape;
                    shape.skinnedMeshRenderer = targetRenderer;
                    if (shape.useMeshMaterialIndex)
                    {
                        shape.meshMaterialIndex = originalMeshSlots.FindIndex(s => s.renderer == renderer && s.index == shape.meshMaterialIndex);
                    }
                }
            }
            targetRenderer.rootBone = targetRootBone;
            targetRenderer.probeAnchor = targetProbeAnchor;
            targetRenderer.sharedMesh = combinedMesh;
            targetRenderer.sharedMaterials = materials;

            foreach (var blendShape in blendShapeWeights)
            {
                for (int j = 0; j < combinedMesh.blendShapeCount; j++)
                {
                    if (blendShape.Key == combinedMesh.GetBlendShapeName(j))
                    {
                        targetRenderer.SetBlendShapeWeight(j, blendShape.Value);
                        break;
                    }
                }
            }

            targetRenderer.bones = targetBones.ToArray();
            targetRenderer.localBounds = targetBounds;

            foreach (var receiver in GetMaterialContactReceiversUsingRenderer(targetRenderer))
            {
                var targets = GetShaderTargetRenderers(receiver);
                var validTargets = targets.Where(r => r != null).ToArray();
                if (validTargets.Length != targets.Length)
                    SetShaderTargetRenderers(receiver, validTargets);
            }

            Profiler.StartSection("AssetDatabase.SaveAssets()");
            AssetDatabase.SaveAssets();
            Profiler.EndSection();

        }
        GetRootTransform().SetPositionAndRotation(originalRootPosition, originalRootRotation);

        // flush particle system & material contact receiver caches since we merged meshes
        cache_ParticleSystemsUsingRenderer = null;
        cache_MaterialContactReceivers = null;
        cache_MaterialContactReceiversUsingRenderer = null;
    }

    private HashSet<string> cache_GetAllExcludedTransformPaths;
    public HashSet<string> GetAllExcludedTransformPaths() {
        if (cache_GetAllExcludedTransformPaths != null)
            return cache_GetAllExcludedTransformPaths;
        return cache_GetAllExcludedTransformPaths = new HashSet<string>(GetAllExcludedTransforms().Select(t => GetPathToRoot(t)));
    }
    
    HashSet<Transform> FindReferencedTransforms(Component component)
    {
        using (var serializedObject = new SerializedObject(component))
        {
            var visitedIds = new HashSet<long>();
            var iterator = serializedObject.GetIterator();
            var referencedTransforms = new HashSet<Transform>();
            bool enterChildren = true;
            while (iterator.Next(enterChildren))
            {
                enterChildren = true;
                if (iterator.propertyType == SerializedPropertyType.ObjectReference && iterator.objectReferenceValue != null)
                {
                    if (iterator.objectReferenceValue is Transform transform)
                    {
                        referencedTransforms.Add(transform);
                    }
                }
                else if (iterator.propertyType == SerializedPropertyType.ManagedReference)
                {
                    if (!visitedIds.Add(iterator.managedReferenceId))
                    {
                        enterChildren = false;
                    }
                }
            }
            return referencedTransforms;
        }
    }

    private void DestroyEditorOnlyGameObjects()
    {
        var stack = new Stack<Transform>();
        stack.Push(GetRootTransform());
        var deletedPaths = new List<string>();
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current.gameObject.CompareTag("EditorOnly"))
            {
                deletedPaths.Add(GetPathToRoot(current));
                DestroyImmediate(current.gameObject);
                continue;
            }
            foreach (Transform child in current)
            {
                stack.Push(child);
            }
        }
        if (deletedPaths.Count > 0)
        {
            LogToFile($"Deleted {deletedPaths.Count} EditorOnly GameObjects:");
            foreach (var path in deletedPaths)
            {
                LogToFile($"- {path}", 1);
            }
        }
    }

    private void DestroyUnusedComponents()
    {
        if (!DeleteUnusedComponents)
            return;
        var list = FindAllUnusedComponents().Where(c => c != null).ToList();
        if (list.Count == 0)
            return;
        var typesToDeleteInSecondPass = new HashSet<Type>() {
            typeof(Rigidbody),
            typeof(AudioSource),
        };
        componentsDeletedCount = list.Count;
        LogToFile($"Deleting {list.Count} unused components:");
        foreach (var component in list.Where(c => c != null && !typesToDeleteInSecondPass.Contains(c.GetType()))
                            .Concat(list.Where(c => c != null && typesToDeleteInSecondPass.Contains(c.GetType()))))
        {
            if (component == null)
                continue;
            LogToFile($"- {component.GetType().Name} at '{GetPathToRoot(component.transform)}'", 1);
            DestroyImmediate(component);
        }
    }

    public class MaterialAssetComparer : IEqualityComparer<Material> {
        private static Dictionary<(Material a, Material b), bool> comparisonCache = new ();
        public static void ClearCache() => comparisonCache.Clear();
        public bool Equals(Material a, Material b) {
            if (a == b)
                return true;
            if (a == null || b == null)
                return false;
            if (a.shader != b.shader)
                return false;
            if (comparisonCache.TryGetValue((a, b), out bool cachedResult))
                return cachedResult;
            
            try {
            if (a.renderQueue != b.renderQueue)
                return false;
            if (a.doubleSidedGI != b.doubleSidedGI)
                return false;
            if (a.enableInstancing != b.enableInstancing)
                return false;
            if (a.globalIlluminationFlags != b.globalIlluminationFlags)
                return false;
            
            var aKeywords = a.shaderKeywords;
            var bKeywords = b.shaderKeywords;
            if (aKeywords.Length != bKeywords.Length)
                return false;
            Array.Sort(aKeywords);
            Array.Sort(bKeywords);
            if (!aKeywords.SequenceEqual(bKeywords))
                return false;

            for (int i = 0; i < a.shader.passCount; i++) {
                var lightModeValue = a.shader.FindPassTagValue(i, new ShaderTagId("LightMode"));
                if (!string.IsNullOrEmpty(lightModeValue.name)) {
                    if (a.GetShaderPassEnabled(lightModeValue.name) != b.GetShaderPassEnabled(lightModeValue.name)) {
                        return false;
                    }
                }
            }

            string[] aFloats = a.GetPropertyNames(MaterialPropertyType.Float);
            string[] bFloats = b.GetPropertyNames(MaterialPropertyType.Float);
            if (!aFloats.SequenceEqual(bFloats))
                return false;
            if (!aFloats.Select(x => a.GetFloat(x)).SequenceEqual(bFloats.Select(x => b.GetFloat(x))))
                return false;

            string[] aInts = a.GetPropertyNames(MaterialPropertyType.Int);
            string[] bInts = b.GetPropertyNames(MaterialPropertyType.Int);
            if (!aInts.SequenceEqual(bInts))
                return false;
            if (!aInts.Select(x => a.GetInteger(x)).SequenceEqual(bInts.Select(x => b.GetInteger(x))))
                return false;

            string[] aVectors = a.GetPropertyNames(MaterialPropertyType.Vector);
            string[] bVectors = b.GetPropertyNames(MaterialPropertyType.Vector);
            if (!aVectors.SequenceEqual(bVectors))
                return false;
            if (!aVectors.Select(x => a.GetVector(x)).SequenceEqual(bVectors.Select(x => b.GetVector(x))))
                return false;

            string[] aTextures = a.GetPropertyNames(MaterialPropertyType.Texture);
            string[] bTextures = b.GetPropertyNames(MaterialPropertyType.Texture);
            if (!aTextures.SequenceEqual(bTextures))
                return false;
            if (!aTextures.Select(x => a.GetTexture(x)).SequenceEqual(bTextures.Select(x => b.GetTexture(x))))
                return false;
            } finally {
                comparisonCache[(a, b)] = comparisonCache[(b, a)] = false;
            }

            return comparisonCache[(a, b)] = comparisonCache[(b, a)] = true;
        }

        public int GetHashCode(Material m) {
            int hash = m.shader.GetHashCode() ^ m.renderQueue;
            if (m.HasTexture("_MainTex")) {
                var tex = m.GetTexture("_MainTex");
                if (tex != null) {
                    hash ^= tex.GetHashCode();
                }
            }
            if (m.HasProperty("_Color")) {
                hash ^= m.GetColor("_Color").GetHashCode();
            }
            return hash;
        }
    }

    private void DeduplicateMaterials()
    {
        var allRenderers = GetRootTransform().GetComponentsInChildren<Renderer>(true);
        var exclusions = GetAllExcludedTransforms();
        allRenderers = allRenderers.Where(r => !exclusions.Contains(r.transform)).ToArray();
        
        var allUsedMaterials = allRenderers.SelectMany(x => x.sharedMaterials).Where(m => m != null).Distinct().ToArray();
        var materialGroups = allUsedMaterials.GroupBy(x => x, new MaterialAssetComparer()).ToList();

        var deduplicatedMaterialLookup = new Dictionary<Material, Material>();
        foreach (var group in materialGroups) {
            Material finalMaterial = group.Key;
            foreach (var mat in group) {
                deduplicatedMaterialLookup[mat] = finalMaterial;
            }
        }

        foreach (var renderer in allRenderers) {
            var materials = renderer.sharedMaterials;
            for (int i = 0; i < materials.Length; i++) {
                if (materials[i] != null && deduplicatedMaterialLookup.TryGetValue(materials[i], out var newMaterial)) {
                    materials[i] = newMaterial;
                }
            }
            renderer.sharedMaterials = materials;
        }
    }
#endif
}
