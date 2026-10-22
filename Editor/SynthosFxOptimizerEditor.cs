#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;
using d4rkpl4y3r.AvatarOptimizer;
using d4rkpl4y3r.AvatarOptimizer.Util;
using d4rkpl4y3r.AvatarOptimizer.Extensions;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.Dynamics;
using VRC.SDKBase.Validation.Performance;

using MaterialSlot = SynthosFxOptimizer.MaterialSlot;

[CustomEditor(typeof(SynthosFxOptimizer))]
public class SynthosFxOptimizerEditor : Editor
{
    private static SynthosFxOptimizer optimizer;
    private static long longestTimeUsed = -2;

    public override void OnInspectorGUI()
    {
        optimizer = (SynthosFxOptimizer)target;
        OnSelectionChange();
        // exclude OnSelectionChange from timing since it parses shaders which will stay cached after the first time
        var stopWatch = new System.Diagnostics.Stopwatch();
        stopWatch.Start();

        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.FlexibleSpace();
        }
        float currentViewWidth = GUILayoutUtility.GetLastRect().width + 23;

        var path = AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(this));
        var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(path);
        EditorGUILayout.Space();

        Rect settingsRect = new Rect();

        using (new EditorGUI.IndentLevelScope())
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                settingsRect = GUILayoutUtility.GetRect(32, 1, GUILayout.ExpandWidth(false));
                EditorGUILayout.LabelField(
                    $"<size=20>Synthos FX Optimizer</size>",
                    new GUIStyle(EditorStyles.label) { richText = true, alignment = TextAnchor.LowerCenter });
            }
            EditorGUILayout.LabelField($"v{packageInfo.version}", EditorStyles.centeredGreyMiniLabel);
        }

        settingsRect.width = 24;
        settingsRect.height = 24;
        if (GUI.Button(settingsRect, new GUIContent("",
            "Global settings and defaults for new optimizer components can be changed here."
            + "\nAlso a couple buttons for quick access to the log file & github.")))
        {
            EditorWindow.GetWindow(typeof(AvatarOptimizerSettings));
        }
        GUI.DrawTexture(settingsRect, EditorGUIUtility.IconContent("Settings@2x").image);

        var logFileRect = settingsRect;
        logFileRect.x += settingsRect.width + 2;
        if (GUI.Button(logFileRect, new GUIContent("", "Open log file of last optimization")))
        {
            AvatarOptimizerSettings.OpenLogFile();
        }
        const float pad = 2;
        logFileRect.x += pad;
        logFileRect.y += pad;
        logFileRect.width -= pad * 2;
        logFileRect.height -= pad * 2;
        GUI.DrawTexture(logFileRect, EditorGUIUtility.IconContent("d_TextAsset Icon").image);

        if (Application.isPlaying)
        {
            EditorGUILayout.HelpBox("Exit play mode to use the optimizer.", MessageType.Info);
            return;
        }

        var presets = optimizer.GetPresetNames();
        if (presets.Count > 1)
        {
            using (new EditorGUILayout.HorizontalScope(GUI.skin.box))
            {
                EditorGUILayout.LabelField(GetLabelWithTooltip("Presets"), EditorStyles.boldLabel, GUILayout.Width(50));
                foreach (var preset in presets)
                {
                    using var cc = new EditorGUI.ChangeCheckScope();
                    bool clicked = GUILayout.Toggle(optimizer.IsPresetActive(preset), GetLabelWithTooltip(preset), GUI.skin.button);
                    if (cc.changed && clicked)
                    {
                        optimizer.SetPreset(preset);
                        ClearUICaches();
                        EditorUtility.SetDirty(optimizer);
                    }
                }
            }
        }

        ToggleOptimizerProperty(nameof(optimizer.ApplyOnUpload));
        ToggleOptimizerProperty(nameof(optimizer.OptimizeFXLayer));
        ToggleOptimizerProperty(nameof(optimizer.DeleteUnusedAnimatorParameters));
        ToggleOptimizerProperty(nameof(optimizer.DisablePhysBonesWhenUnused));
        ToggleOptimizerProperty(nameof(optimizer.MergeSameRatioBlendShapes));
        ToggleOptimizerProperty(nameof(optimizer.MMDCompatibility));
        ToggleOptimizerProperty(nameof(optimizer.DeleteUnusedComponents));

        optimizer.ExcludeTransforms ??= new List<Transform>();
        optimizer.ExcludeTransformChildren ??= new List<bool>();
        if (Foldout($"Exclusions ({optimizer.ExcludeTransforms.Count})", ref optimizer.ShowExcludedTransforms))
        {
            using (new EditorGUI.IndentLevelScope())
            {
                DynamicExclusionList(optimizer);
            }
        }

        Profiler.enabled = AvatarOptimizerSettings.ProfileTimeUsedInUI;
        Profiler.Reset();

        Profiler.StartSection("Validate");
        Validate();
        Profiler.EndSection();
        if (optimizer.GetAvatarDescriptor() == null)
        {
            return;
        }
        EditorGUILayout.Separator();

        if (longestTimeUsed > AvatarOptimizerSettings.AutoRefreshPreviewTimeout)
        {
            EditorGUILayout.HelpBox($"Preview auto refresh is disabled because it took {longestTimeUsed}ms which is longer than the threshold of {AvatarOptimizerSettings.AutoRefreshPreviewTimeout}ms to refresh.\n"
                + "The preview might still be refreshed manually by clicking the refresh button.", MessageType.Info);
            if (GUILayout.Button("Refresh Preview"))
            {
                longestTimeUsed = 0;
                ClearUICaches();
            }
        }

        Profiler.StartSection("Show Perf Rank Change");
        static int ParticleSystemMaterialCount(ParticleSystemRenderer psr) => psr.trailMaterial != null ? 2 : 1;
        var allRenderers = optimizer.GetNonEditorOnlyComponentsInChildren<Renderer>();
        var particleSystemCount = allRenderers.OfType<ParticleSystemRenderer>().Sum(ParticleSystemMaterialCount);
        var trailRendererCount = allRenderers.OfType<TrailRenderer>().Count();
        var skinnedMeshes = allRenderers.OfType<SkinnedMeshRenderer>().ToList();
        var meshRenderers = allRenderers.OfType<MeshRenderer>().ToList();
        int totalMaterialCount = skinnedMeshes.Cast<Renderer>().Concat(meshRenderers)
            .Sum(r => r.GetSharedMesh() == null ? 0 : r.GetSharedMesh().subMeshCount) + particleSystemCount + trailRendererCount;
        var totalBlendShapePaths = new HashSet<string>(skinnedMeshes.SelectMany(r => {
            if (r.sharedMesh == null)
                return new string[0];
            return Enumerable.Range(0, r.sharedMesh.blendShapeCount)
                .Select(i => $"{optimizer.GetPathToRoot(r.transform)}/blendShape.{r.sharedMesh.GetBlendShapeName(i)}");
        }));
        int optimizedSkinnedMeshCount = 0;
        int optimizedMeshCount = 0;
        int optimizedTotalMaterialCount = 0;
        var exclusions = optimizer.GetAllExcludedTransforms();
        foreach (var matched in MergedMaterialPreview)
        {
            var renderers = matched.SelectMany(m => m).Select(slot => slot.renderer).Distinct().ToArray();
            if (renderers.Any(r => r is SkinnedMeshRenderer) || renderers.Length > 1)
            {
                optimizedSkinnedMeshCount++;
                if (exclusions.Contains(renderers[0].transform))
                    optimizedTotalMaterialCount += renderers[0].GetSharedMesh().subMeshCount;
                else
                    optimizedTotalMaterialCount += matched.Count;
            }
            else if (renderers[0] is MeshRenderer)
            {
                optimizedMeshCount++;
                var mesh = renderers[0].GetSharedMesh();
                optimizedTotalMaterialCount += mesh == null ? 0 : mesh.subMeshCount;
            }
            else if (renderers[0] is ParticleSystemRenderer psr)
            {
                optimizedTotalMaterialCount += ParticleSystemMaterialCount(psr);
            }
            else // TrailRenderer
            {
                optimizedTotalMaterialCount += 1;
            }
        }
        var perfRankChanges = new List<(string label, int oldValue, int newValue, PerformanceCategory category)> {
            ("Skinned Mesh Renderers", skinnedMeshes.Count, optimizedSkinnedMeshCount, PerformanceCategory.SkinnedMeshCount),
            ("Mesh Renderers", meshRenderers.Count, optimizedMeshCount, PerformanceCategory.MeshCount),
            ("Material Slots", totalMaterialCount, optimizedTotalMaterialCount, PerformanceCategory.MaterialCount),
        };
        if (optimizer.GetFXLayer() != null)
        {
            var nonErrors = new HashSet<string>() {"toggle", "motion time", "blend tree", "multi toggle"};
            var mergedLayerCount = optimizer.OptimizeFXLayer ? optimizer.AnalyzeFXLayerMergeAbility().Count(list => list.All(e => nonErrors.Contains(e))) : 0;
            var layerCount = optimizer.GetFXLayerLayers().Length;
            var optimizedLayerCount = mergedLayerCount > 1 ? layerCount - mergedLayerCount + 1 : layerCount;
            if (optimizer.OptimizeFXLayer)
                optimizedLayerCount -= optimizer.FindUselessFXLayers().Count;
            perfRankChanges.Add(("FX Layers", layerCount, optimizedLayerCount, PerformanceCategory.FXLayerCount));
        }
        perfRankChanges.Add(("Blend Shapes", totalBlendShapePaths.Count, KeptBlendShapePaths.Count, PerformanceCategory.BlendShapeCount));
        PerfRankChangeLabel(perfRankChanges);
        Profiler.EndSection();

        EditorGUILayout.Separator();

        if (Foldout("Show Mesh & Material Merge Preview", ref optimizer.ShowMeshAndMaterialMergePreview, showNonDestructiveToolingWarning: true))
        {
            Profiler.StartSection("Show Merge Preview");
            foreach (var matched in MergedMaterialPreview)
            {
                for (int i = 0; i < matched.Count; i++)
                {
                    using var horizontalScope = new EditorGUILayout.HorizontalScope();
                    using (new EditorGUILayout.VerticalScope())
                    {
                        for (int j = 0; j < matched[i].Count; j++)
                        {
                            int indent = j == 0 ? 0 : 1;
                            DrawMatchedMaterialSlot(matched[i][j], indent);
                        }
                    }
                    var materials = matched[i].Select(slot => slot.material).Distinct().ToArray();
                    var buttonContent = new GUIContent("S", $"Selects this group of {materials.Length} materials");
                    if (GUILayout.Button(buttonContent, GUILayout.Width(20)))
                    {
                        Selection.objects = materials;
                    }
                }
                EditorGUILayout.Space(8);
            }
            Profiler.EndSection();
        }

        EditorGUILayout.Separator();

        if (optimizer.OptimizeFXLayer && optimizer.GetFXLayer() != null)
        {
            if (Foldout("Show FX Layer Merge Result", ref optimizer.ShowFXLayerMergeResults, showNonDestructiveToolingWarning: true))
            {
                Profiler.StartSection("Show FX Layer Merge Errors");
                ToggleOptimizerProperty(nameof(optimizer.ShowFXLayerMergeErrors));
                var errorMessages = optimizer.AnalyzeFXLayerMergeAbility();
                var uselessLayers = optimizer.FindUselessFXLayers();
                var fxLayer = optimizer.GetFXLayer();
                var fxLayerLayers = optimizer.GetFXLayerLayers();
                var nonErrors = new HashSet<string>() {"toggle", "motion time", "useless", "blend tree", "multi toggle"};
                for (int i = 0; i < errorMessages.Count; i++)
                {
                    var perfRating = PerformanceRating.VeryPoor;
                    if (errorMessages[i].Count == 1 && (errorMessages[i][0] == "toggle" || errorMessages[i][0] == "multi toggle" || errorMessages[i][0] == "blend tree"))
                        perfRating = PerformanceRating.Good;
                    else if (errorMessages[i].Count == 1 && errorMessages[i][0] == "motion time")
                        perfRating = PerformanceRating.Medium;
                    if (uselessLayers.Contains(i))
                        perfRating = PerformanceRating.Excellent;

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(GetPerformanceIconForRating(perfRating), GUILayout.Width(20));
                        EditorGUILayout.LabelField(new GUIContent($"{i}{fxLayerLayers[i].name}", string.Join("\n", errorMessages[i])));
                    }
                    if (optimizer.ShowFXLayerMergeErrors)
                    {
                        using (new EditorGUI.IndentLevelScope(2))
                        {
                            foreach (var error in errorMessages[i].Where(e => !nonErrors.Contains(e)))
                            {
                                EditorGUILayout.LabelField(error);
                            }
                        }
                    }
                }
                Profiler.EndSection();
            }
            EditorGUILayout.Separator();
        }

        if (Foldout("Debug Info", ref optimizer.ShowDebugInfo, showNonDestructiveToolingWarning: true))
        {
            using var indent = new EditorGUI.IndentLevelScope();
            if (Foldout("NonBC5 Normal Maps", ref optimizer.DebugShowNonBC5NormalMaps))
            {
                Profiler.StartSection("NonBC5 Normal Maps");
                DrawDebugList(NonBC5NormalMaps);
                Profiler.EndSection();
            }
            if (optimizer.MergeSameRatioBlendShapes && Foldout("Same Ratio Blend Shapes", ref optimizer.DebugShowMergeableBlendShapes))
            {
                Profiler.StartSection("Same Ratio Blend Shapes");
                foreach (var list in MergeableBlendShapes)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.Space(15, false);
                        using (new EditorGUILayout.VerticalScope(GUI.skin.box))
                        {
                            foreach (var ratio in list)
                            {
                                using (new EditorGUILayout.HorizontalScope())
                                {
                                    EditorGUILayout.LabelField($"{ratio.value * 100:F1}".Replace(".0", ""), GUILayout.Width(60));
                                    EditorGUILayout.LabelField(ratio.blendshape.Replace("/blendShape.", "."));
                                }
                            }
                        }
                    }
                    EditorGUILayout.Separator();
                }
                Profiler.EndSection();
            }
            if (Foldout("Native Face Tracking", ref optimizer.DebugShowFaceTracking))
            {
                using var faceTrackingIndent = new EditorGUI.IndentLevelScope();
                var (meshes, names) = optimizer.GetFaceTrackingSetup();
                EditorGUILayout.LabelField($"Declared Blendshape Names ({names.Count})");
                DrawDebugList(names.OrderBy(n => n, System.StringComparer.Ordinal).ToArray());
                var descriptor = optimizer.GetAvatarDescriptor();
                var field = descriptor?.GetType().GetField("faceTrackingMeshes");
                if (field?.GetValue(descriptor) is IEnumerable<SkinnedMeshRenderer> configuredMeshes)
                {
                    int index = 0;
                    foreach (var renderer in configuredMeshes)
                    {
                        bool tracked = renderer != null && meshes.Contains(renderer) && index < 16;
                        EditorGUILayout.ObjectField($"Mesh {++index}{(tracked ? "" : " (ignored)")}", renderer, typeof(SkinnedMeshRenderer), true);
                        if (!tracked)
                            continue;
                        var mesh = renderer.sharedMesh;
                        var missing = names.Where(n => mesh == null || mesh.GetBlendShapeIndex(n) < 0)
                            .OrderBy(n => n, System.StringComparer.Ordinal).ToArray();
                        if (missing.Length > 0)
                            EditorGUILayout.HelpBox("Declared names missing on this mesh (informational):\n" + string.Join("\n", missing), MessageType.Info);
                    }
                }
            }
            if (Foldout("Phys Bone Dependencies", ref optimizer.DebugShowPhysBoneDependencies))
            {
                Profiler.StartSection("Phys Bone Dependencies");
                foreach (var pair in optimizer.FindAllPhysBoneDependencies())
                {
                    if (pair.Key.gameObject.CompareTag("EditorOnly"))
                        continue;
                    EditorGUILayout.ObjectField(pair.Key, typeof(VRCPhysBoneBase), true);
                    using (new EditorGUI.IndentLevelScope())
                    {
                        DrawDebugList(pair.Value.ToArray());
                    }
                }
            }
            if (Foldout("Unused Components", ref optimizer.DebugShowUnusedComponents))
            {
                Profiler.StartSection("Unused Components");
                DrawDebugList(optimizer.FindAllUnusedComponents().ToArray());
                Profiler.EndSection();
            }
            if (Foldout("Always Disabled Game Objects", ref optimizer.DebugShowAlwaysDisabledGameObjects))
            {
                Profiler.StartSection("Always Disabled Game Objects");
                DrawDebugList(optimizer.FindAllAlwaysDisabledGameObjects().ToArray());
                Profiler.EndSection();
            }
            if (Foldout("Material Swaps", ref optimizer.DebugShowMaterialSwaps))
            {
                Profiler.StartSection("Material Swaps");
                var map = optimizer.FindAllMaterialSwapMaterials();
                foreach (var pair in map)
                {
                    EditorGUILayout.LabelField(pair.Key.path + " -> " + pair.Key.index);
                    using (new EditorGUI.IndentLevelScope())
                    {
                        DrawDebugList(pair.Value.ToArray());
                    }
                }
                if (map.Count == 0)
                {
                    EditorGUILayout.LabelField("---");
                }
                Profiler.EndSection();
            }
            if (Foldout("Animated Material Property Paths", ref optimizer.DebugShowAnimatedMaterialPropertyPaths))
            {
                Profiler.StartSection("Animated Material Property Paths");
                DrawDebugList(AnimatedMaterialPropertyPaths);
                Profiler.EndSection();
            }
            if (Foldout("Game Objects with Toggle Animation", ref optimizer.DebugShowGameObjectsWithToggle))
            {
                Profiler.StartSection("Game Objects with Toggle Animation");
                DrawDebugList(GameObjectsWithToggleAnimations);
                Profiler.EndSection();
            }
            if (Foldout("Unmoving Bones", ref optimizer.DebugShowUnmovingBones))
            {
                Profiler.StartSection("Unmoving Bones");
                DrawDebugList(UnmovingBones);
                Profiler.EndSection();
            }
        }
        if (AvatarOptimizerSettings.ProfileTimeUsedInUI)
        {
            EditorGUILayout.Separator();
            var timeUsed = Profiler.FormatTimeUsed().Take(6).ToArray();
            foreach (var time in timeUsed)
            {
                EditorGUILayout.LabelField(time);
            }
        }
        stopWatch.Stop();
        if (stopWatch.ElapsedMilliseconds > longestTimeUsed && stopWatch.ElapsedMilliseconds > AvatarOptimizerSettings.AutoRefreshPreviewTimeout)
        {
            // longestTimeUsed < 0 means it's one of the first times the optimizer is used
            // first couple times are always slower since the JIT compiler has to compile the code
            longestTimeUsed = longestTimeUsed < 0 ? longestTimeUsed + 1 : stopWatch.ElapsedMilliseconds;
        }
    }

    readonly HashSet<string> knownVRCComponentTypeNames = new()
    {
        "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor",
        "VRC.SDK3.Avatars.Components.VRCHeadChop",
        "VRC.SDK3.Avatars.Components.VRCImpostorEnvironment",
        "VRC.SDK3.Avatars.Components.VRCImpostorSettings",
        "VRC.SDK3.Avatars.Components.VRCPerPlatformOverrides",
        "VRC.SDK3.Avatars.Components.VRCRaycast",
        "VRC.SDK3.Avatars.Components.VRCSpatialAudioSource",
        "VRC.SDK3.Avatars.Components.VRCStation",
        "VRC.SDK3.Dynamics.Constraint.Components.VRCAimConstraint",
        "VRC.SDK3.Dynamics.Constraint.Components.VRCLookAtConstraint",
        "VRC.SDK3.Dynamics.Constraint.Components.VRCParentConstraint",
        "VRC.SDK3.Dynamics.Constraint.Components.VRCPositionConstraint",
        "VRC.SDK3.Dynamics.Constraint.Components.VRCRotationConstraint",
        "VRC.SDK3.Dynamics.Constraint.Components.VRCScaleConstraint",
        "VRC.SDK3.Dynamics.Contact.Components.VRCContactReceiver",
        "VRC.SDK3.Dynamics.Contact.Components.VRCContactSender",
        "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone",
        "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBoneCollider",
        "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBoneRoot",
        "VRC.SDK3.Dynamics.PhysBone.PhysBoneGrabHelper",
        "VRC.SDK3.VRCTestMarker"
    };

    private bool Validate()
    {
        static void HelpBox(string message, MessageType type, string tooltip = null)
        {
            var style = new GUIStyle(EditorStyles.helpBox) { fontSize = EditorStyles.helpBox.fontSize + 2 };
            Texture icon = type switch
            {
                MessageType.Info => EditorGUIUtility.IconContent("console.infoicon").image,
                MessageType.Warning => EditorGUIUtility.IconContent("console.warnicon").image,
                MessageType.Error => EditorGUIUtility.IconContent("console.erroricon").image,
                _ => null
            };
            EditorGUILayout.LabelField(new GUIContent(message, icon, tooltip ?? ""), style);
        }

        static string TexturesTooltip(Texture[] textures, string header = null)
        {
            var lines = TooltipLines(textures.Select(t => t == null ? "null" : t.name), "textures");
            return header == null ? string.Join("\n", lines) : $"{header}\n{string.Join("\n", lines)}";
        }

        var avDescriptor = optimizer.GetAvatarDescriptor();

        if (avDescriptor == null)
        {
            HelpBox("No VRCAvatarDescriptor found.", MessageType.Error);
            return false;
        }

        var allOptimizerComponents = avDescriptor.GetComponentsInChildren<SynthosFxOptimizer>(true);
        if (allOptimizerComponents.Length > 1)
        {
            HelpBox("Multiple Synthos FX Optimizer components found on the avatar.\n" +
                "Remove the duplicates. Components are at paths:\n - " +
                string.Join("\n - ", allOptimizerComponents.Select(c => avDescriptor.name + (c.transform == avDescriptor.transform ? "" : "/" + c.GetPathToRoot(c)))),
                MessageType.Error);
            return false;
        }

        int expectedBaseLayerCount = optimizer.GetBaseLayerCount();
        int actualBaseLayerCount = avDescriptor.baseAnimationLayers == null ? 0 : avDescriptor.baseAnimationLayers.Length;
        if (actualBaseLayerCount != expectedBaseLayerCount)
        {
            HelpBox($"Expected base layer count: {expectedBaseLayerCount}, but found: {actualBaseLayerCount}.\n" +
                "Try to reimport the avatar fbx.", MessageType.Error);
            return false;
        }

        var allComponentTypes = optimizer.GetAllComponentTypesInAvatar().Select(t => t.FullName).ToList();
        var unknownVRCComponentTypes = allComponentTypes.Where(t => t.StartsWith("VRC.SDK3.") && !knownVRCComponentTypeNames.Contains(t)).ToList();
        if (unknownVRCComponentTypes.Count > 0)
        {
            HelpBox("Unknown VRC component types found on the avatar:\n - " +
                string.Join("\n - ", unknownVRCComponentTypes) + 
                "\nYou need to update the optimizer or not use it if there is no new version available.\n" +
                "Not doing so will most likely break things on your avatar.", MessageType.Error);
        }

        if (avDescriptor.lipSync == VRC.SDKBase.VRC_AvatarDescriptor.LipSyncStyle.VisemeBlendShape
            && avDescriptor.VisemeSkinnedMesh != null)
        {
            var meshRenderer = avDescriptor.VisemeSkinnedMesh;
            if (avDescriptor.GetComponentsInChildren<SkinnedMeshRenderer>(true).All(r => r != meshRenderer))
            {
                HelpBox("Viseme SkinnedMeshRenderer is not a child of the avatar root.", MessageType.Error);
            }
        }

        if (avDescriptor.customEyeLookSettings.eyelidType == VRCAvatarDescriptor.EyelidType.Blendshapes
            && avDescriptor.customEyeLookSettings.eyelidsSkinnedMesh != null)
        {
            var meshRenderer = avDescriptor.customEyeLookSettings.eyelidsSkinnedMesh;
            if (avDescriptor.GetComponentsInChildren<SkinnedMeshRenderer>(true).All(r => r != meshRenderer))
            {
                HelpBox("Eyelid SkinnedMeshRenderer is not a child of the avatar root.", MessageType.Error);
            }
        }

        if (NonBC5NormalMaps.Length > 0)
        {
            HelpBox(
                "Some normal maps are not BC5 compressed.\n" +
                "BC5 compressed normal maps are highest quality for the same VRAM size as the other compression options.\n" +
                "Check the Debug Info foldout for a full list or click the button to automatically change them all to BC5.",
                MessageType.Info,
                TexturesTooltip(NonBC5NormalMaps, "Non BC5 normal maps:"));
            if (GUILayout.Button($"Convert all ({NonBC5NormalMaps.Length}) normal maps to BC5"))
            {
                foreach (var tex in NonBC5NormalMaps)
                {
                    var path = AssetDatabase.GetAssetPath(tex);
                    var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                    var platformSettings = importer.GetPlatformTextureSettings("Standalone");
                    platformSettings.resizeAlgorithm = TextureResizeAlgorithm.Bilinear;
                    platformSettings.overridden = true;
                    platformSettings.format = TextureImporterFormat.BC5;
                    platformSettings.maxTextureSize = Mathf.Max(tex.width, tex.height);
                    importer.SetPlatformTextureSettings(platformSettings);
                    importer.SaveAndReimport();
                }
                ClearUICaches();
            }
        }

        var tools = optimizer.GetNonDestructiveToolsUsedOnAvatar();
        if (tools.Contains("VRCFury"))
        {
            HelpBox(
                "VRCFury is used on the avatar. This means the perf rank change and merge result previews can be inaccurate as the optimizer does not take VRCFury into account for those.\n" +
                "To test in editor built a VRCFury test avatar and use the optimizer on that.\n" +
                $"For uploading use the {SynthosFxOptimizer.GetDisplayName(nameof(SynthosFxOptimizer.ApplyOnUpload))} feature as that ensures fury and the optimizer get used in the correct order.", MessageType.Warning);
            return false;
        }

        if (tools.Contains("Modular Avatar"))
        {
            HelpBox(
                "Modular Avatar is used on the avatar. This means the perf rank change and merge result previews " + 
                "can be inaccurate as the optimizer does not take Modular Avatar into account for those.\n" +
                "To test in editor use \"Manual bake avatar\" before clicking the optimize button.\n" +
                $"For uploading use the {SynthosFxOptimizer.GetDisplayName(nameof(SynthosFxOptimizer.ApplyOnUpload))} feature as that ensures Modular Avatar and the optimizer get used in the correct order.", MessageType.Warning);
            return false;
        }

        return true;
    }

    private string GetNonDestructiveToolingWarning(string sectionName)
    {
        var tools = optimizer.GetNonDestructiveToolsUsedOnAvatar();
        if (tools.Count == 0)
            return null;
        return $"The following non-destructive tool{(tools.Count == 1 ? " is" : "s are")} found on the avatar:\n" +
               string.Join(", ", tools) +
               $"\nThis means the {sectionName} can be wrong as {(tools.Count == 1 ? "this tool" : "these tools")} can change the avatar at build time which the optimizer can't see before it happens.";
    }

    private static void AssignNewAvatarIDIfEmpty()
    {
        var avDescriptor = optimizer.GetAvatarDescriptor();
        if (avDescriptor == null)
            return;
        if (!avDescriptor.TryGetComponent<VRC.Core.PipelineManager>(out var pm))
        {
            pm = avDescriptor.gameObject.AddComponent<VRC.Core.PipelineManager>();
        }
        if (!string.IsNullOrEmpty(pm.blueprintId))
            return;
        pm.AssignId(VRC.Core.PipelineManager.ContentType.avatar);
    }

    private SynthosFxOptimizer lastSelected = null;
    private List<List<List<MaterialSlot>>> mergedMaterialPreviewCache = null;
    private Transform[] unmovingBonesCache = null;
    private GameObject[] gameObjectsWithToggleAnimationsCache = null;
    private Texture2D[] crunchedTexturesCache = null;
    private Texture2D[] nonBC5NormalMapsCache = null;
    private Renderer[] cantMergeNaNimationBecauseOfWDONAnimationsCache = null;
    private string[] animatedMaterialPropertyPathsCache = null;
    private HashSet<string> keptBlendShapePathsCache = null;
    private List<List<(string blendshape, float value)>> mergeableBlendShapesCache = null;
    private Dictionary<Mesh, (int count, float maxValue, float medianValue)[]> meshBoneWeightStatsCache = null;
    private Dictionary<Material, string[]> propertiesMarkedAsRenameAnimatedCache = null;

    private void ClearUICaches(bool force = false)
    {
        if (!force && longestTimeUsed > AvatarOptimizerSettings.AutoRefreshPreviewTimeout)
            return;
        mergedMaterialPreviewCache = null;
        unmovingBonesCache = null;
        gameObjectsWithToggleAnimationsCache = null;
        crunchedTexturesCache = null;
        nonBC5NormalMapsCache = null;
        cantMergeNaNimationBecauseOfWDONAnimationsCache = null;
        animatedMaterialPropertyPathsCache = null;
        keptBlendShapePathsCache = null;
        mergeableBlendShapesCache = null;
        propertiesMarkedAsRenameAnimatedCache = null;
        optimizer.ClearCaches();
    }

    private void OnSelectionChange()
    {
        if (lastSelected == optimizer)
            return;
        if (longestTimeUsed > 0)
            longestTimeUsed = 0;
        lastSelected = optimizer;
        ClearUICaches();
        if (optimizer.DoAutoSettings)
        {
            AvatarOptimizerSettings.ApplyDefaults(optimizer);
            optimizer.ApplyAutoSettings();
        }
        ShaderAnalyzer.ParseAndCacheAllShaders(optimizer.FindAllUsedMaterials().Select(m => m.shader), false);
    }

    private (int count, float maxValue, float medianValue)[] GetMeshBoneWeightStats(Mesh mesh)
    {
        if (meshBoneWeightStatsCache == null)
            meshBoneWeightStatsCache = new Dictionary<Mesh, (int count, float maxValue, float medianValue)[]>();
        if (mesh == null)
            return new (int count, float maxValue, float medianValue)[4];
        if (!meshBoneWeightStatsCache.TryGetValue(mesh, out var stats))
        {
            stats = new (int count, float maxValue, float medianValue)[4];
            var nonZeroWeights = new List<float>[4]
            {
                new List<float>(),
                new List<float>(),
                new List<float>(),
                new List<float>(),
            };
            var boneWeights = mesh.boneWeights;
            for (int i = 0; i < boneWeights.Length; i++)
            {
                var weight = boneWeights[i];
                if (weight.weight0 > 0)
                    nonZeroWeights[0].Add(weight.weight0);
                if (weight.weight1 > 0)
                    nonZeroWeights[1].Add(weight.weight1);
                if (weight.weight2 > 0)
                    nonZeroWeights[2].Add(weight.weight2);
                if (weight.weight3 > 0)
                    nonZeroWeights[3].Add(weight.weight3);
            }
            for (int i = 0; i < 4; i++)
            {
                stats[i].count = nonZeroWeights[i].Count;
                if (stats[i].count == 0)
                {
                    stats[i].maxValue = 0;
                    stats[i].medianValue = 0;
                    continue;
                }
                nonZeroWeights[i].Sort();
                stats[i].maxValue = nonZeroWeights[i][stats[i].count - 1];
                stats[i].medianValue = nonZeroWeights[i][stats[i].count / 2];
            }
            meshBoneWeightStatsCache.Add(mesh, stats);
        }
        return stats;
    }

    private List<List<List<MaterialSlot>>> MergedMaterialPreview
    {
        get
        {
            if (mergedMaterialPreviewCache == null)
            {
                mergedMaterialPreviewCache = new List<List<List<MaterialSlot>>>();
                var matchedSkinnedMeshes = optimizer.FindPossibleSkinnedMeshMerges();
                foreach (var mergedMeshes in matchedSkinnedMeshes)
                {
                    var matched = optimizer.FindAllMergeAbleMaterials(mergedMeshes);
                    mergedMaterialPreviewCache.Add(matched);
                }
            }
            return mergedMaterialPreviewCache;
        }
    }

    private List<List<(string blendshape, float value)>> MergeableBlendShapes
    {
        get
        {
            if (mergeableBlendShapesCache == null)
            {
                mergeableBlendShapesCache = new List<List<(string blendshape, float value)>>();
                if (optimizer.MergeSameRatioBlendShapes)
                {
                    foreach (var matched in MergedMaterialPreview)
                    {
                        var renderers = matched.SelectMany(m => m).Select(slot => slot.renderer).Distinct().ToArray();
                        var mergedBlendShapes = optimizer.FindMergeableBlendShapes(renderers);
                        mergeableBlendShapesCache.AddRange(mergedBlendShapes);
                    }
                }
            }
            return mergeableBlendShapesCache;
        }
    }

    private Renderer[] CantMergeNaNimationBecauseOfWDONAnimations
    {
        get
        {
            if (cantMergeNaNimationBecauseOfWDONAnimationsCache != null)
                return cantMergeNaNimationBecauseOfWDONAnimationsCache;
            return cantMergeNaNimationBecauseOfWDONAnimationsCache =
                optimizer.FindAllPathsWhereMeshOrGameObjectHasOnlyOnAnimation()
                    .Select(p => optimizer.GetTransformFromPath(p))
                    .Where(t => t != null)
                    .Select(t => t.GetComponent<Renderer>())
                    .Where(r => r != null && !optimizer.GetRendererDefaultEnabledState(r))
                    .ToArray();
        }
    }

    private HashSet<string> KeptBlendShapePaths
    {
        get
        {
            if (keptBlendShapePathsCache == null)
            {
                optimizer.CalculateUsedBlendShapePaths();
                var skinnedMeshes = optimizer.GetUsedComponentsInChildren<SkinnedMeshRenderer>();
                keptBlendShapePathsCache = new HashSet<string>(skinnedMeshes.SelectMany(r => {
                    if (r.sharedMesh == null)
                        return new string[0];
                    return Enumerable.Range(0, r.sharedMesh.blendShapeCount)
                        .Select(i => $"{optimizer.GetPathToRoot(r.transform)}/blendShape.{r.sharedMesh.GetBlendShapeName(i)}");
                }));
                foreach (var list in MergeableBlendShapes)
                {
                    for (int i = 1; i < list.Count; i++)
                        keptBlendShapePathsCache.Remove(list[i].blendshape);
                }
                keptBlendShapePathsCache.IntersectWith(optimizer.GetUsedBlendShapePaths());
            }
            return keptBlendShapePathsCache;
        }
    }

    private Transform[] UnmovingBones
    {
        get
        {
            if (unmovingBonesCache == null)
            {
                var bones = new HashSet<Transform>();
                var moving = optimizer.FindAllMovingTransforms();
                optimizer.GetUsedComponentsInChildren<SkinnedMeshRenderer>().ToList().ForEach(
                    r => bones.UnionWith(r.bones.Where(b => !moving.Contains(b))));
                unmovingBonesCache = bones.ToArray();
            }
            return unmovingBonesCache;
        }
    }

    private GameObject[] GameObjectsWithToggleAnimations
    {
        get
        {
            if (gameObjectsWithToggleAnimationsCache == null)
            {
                gameObjectsWithToggleAnimationsCache =
                    optimizer.FindAllGameObjectTogglePaths()
                    .Select(p => optimizer.GetTransformFromPath(p)?.gameObject)
                    .Where(obj => obj != null).ToArray();
            }
            return gameObjectsWithToggleAnimationsCache;
        }
    }

    private Texture2D[] CrunchedTextures
    {
        get
        {
            if (crunchedTexturesCache == null)
            {
                var exclusions = optimizer.GetAllExcludedTransforms();
                var tuple = optimizer.GetUsedComponentsInChildren<Renderer>()
                    .Where(r => !exclusions.Contains(r.transform))
                    .SelectMany(r => r.sharedMaterials).Distinct()
                    .Select(mat => (mat, ShaderAnalyzer.Parse(mat?.shader)))
                    .Where(t => t.Item2?.parsedCorrectly ?? false).ToArray();
                var textures = new HashSet<Texture2D>();
                foreach (var (mat, parsed) in tuple)
                {
                    if (!parsed.CanMergeTextures())
                        continue;
                    foreach (var prop in parsed.texture2DProperties)
                    {
                        var tex = mat.GetTexture(prop.name) as Texture2D;
                        if (tex != null && (tex.format == TextureFormat.DXT1Crunched || tex.format == TextureFormat.DXT5Crunched))
                            textures.Add(tex);
                    }
                }
                crunchedTexturesCache = textures.ToArray();
            }
            return crunchedTexturesCache;
        }
    }

    private Texture2D[] NonBC5NormalMaps
    {
        get
        {
            if (nonBC5NormalMapsCache == null)
            {
                var exclusions = optimizer.GetAllExcludedTransforms();
                var renderers = optimizer.GetUsedComponentsInChildren<Renderer>();
                var textures = new HashSet<Texture2D>();
                var materials = renderers
                    .Where(r => !exclusions.Contains(r.transform))
                    .SelectMany(r => r.sharedMaterials)
                    .Where(mat => mat != null && mat.shader != null)
                    .Distinct();
                foreach (var material in materials)
                {
                    var parsed = ShaderAnalyzer.Parse(material.shader);
                    if (parsed == null)
                        continue;
                    foreach (var prop in parsed.texture2DProperties)
                    {
                        var tex = material.GetTexture(prop.name) as Texture2D;
                        if (tex != null && tex.format != TextureFormat.BC5)
                        {
                            var assetImporter = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(tex)) as TextureImporter;
                            if (assetImporter != null && assetImporter.textureType == TextureImporterType.NormalMap)
                            {
                                textures.Add(tex);
                            }
                        }
                    }
                }
                nonBC5NormalMapsCache = textures.ToArray();
            }
            return nonBC5NormalMapsCache;
        }
    }

    private string[] AnimatedMaterialPropertyPaths
    {
        get
        {
            if (animatedMaterialPropertyPathsCache == null)
            {
                animatedMaterialPropertyPathsCache = optimizer.FindAllAnimatedMaterialProperties()
                    .SelectMany(kv => kv.Value.Select(prop => $"{kv.Key}.{prop}")).ToArray();
            }
            return animatedMaterialPropertyPathsCache;
        }
    }

    public bool CanLockIn(Material material)
    {
        if (material == null)
            return false;
        if (material.HasProperty("_ShaderOptimizer"))
            return true;
        if (material.HasProperty("_ShaderOptimizerEnabled"))
            return true;
        if (material.HasProperty("__Baked"))
            return true;
        return false;
    }

    public bool IsLockedIn(Material material)
    {
        if (material == null)
            return false;
        if (material.HasProperty("_ShaderOptimizer") && material.GetInt("_ShaderOptimizer") == 1)
            return true;
        if (material.HasProperty("_ShaderOptimizerEnabled") && material.GetInt("_ShaderOptimizerEnabled") == 1)
            return true;
        if (material.HasProperty("__Baked") && material.GetInt("__Baked") == 1)
            return true;
        return false;
    }

    private string PropertiesTooltip(Material material, string header = "Properties marked RA:")
        => $"{header}\n{string.Join("\n", TooltipLines(FindPropertiesMarkedAsRenameAnimated(material), "properties"))}";

    private static IEnumerable<string> TooltipLines(IEnumerable<string> names, string extraNoun)
    {
        var distinctNames = names.Distinct().ToList();
        var extraCount = distinctNames.Count - 15;
        foreach (var name in distinctNames.Take(15))
            yield return $"- {name}";
        if (extraCount == 1)
            yield return $"- {distinctNames[15]}";
        else if (extraCount > 1)
            yield return $"+{extraCount} more {extraNoun}";
    }

    public bool HasPropertyMarkedAsRenameAnimated(Material material)
        => FindPropertiesMarkedAsRenameAnimated(material).Length > 0;

    public string[] FindPropertiesMarkedAsRenameAnimated(Material material)
    {
        if (material == null)
            return System.Array.Empty<string>();
        propertiesMarkedAsRenameAnimatedCache ??= new();
        if (!CanLockIn(material))
            return System.Array.Empty<string>();
        if (propertiesMarkedAsRenameAnimatedCache.TryGetValue(material, out var cached))
            return cached;

        string CleanStringForPropertyNames(string s)
        {
            s = s.Trim().Replace(" ", "");
            var utf8Source = System.Text.Encoding.UTF8.GetBytes(s);
            string cleaned = "";
            for (var i = 0; i < utf8Source.Length; i++)
            {
                if ((utf8Source[i] >= 'a' && utf8Source[i] <= 'z') ||
                    (utf8Source[i] >= 'A' && utf8Source[i] <= 'Z') ||
                    (utf8Source[i] >= '0' && utf8Source[i] <= '9') || utf8Source[i] == '_')
                {
                    cleaned += (char)utf8Source[i];
                }
                else
                {
                    cleaned += utf8Source[i].ToString("X2");
                }
            }
            return cleaned;
        }
        var raSuffix = CleanStringForPropertyNames(material.GetTag("thry_rename_suffix", false, material.name));

        bool EntryHasRenameAnimatedTag(SerializedProperty entry, out string propertyName)
        {
            propertyName = null;
            if (entry == null)
                return false;
            var keyProp = entry.FindPropertyRelative("first");
            var valueProp = entry.FindPropertyRelative("second");
            if (keyProp == null || valueProp == null
                || keyProp.propertyType != SerializedPropertyType.String
                || valueProp.propertyType != SerializedPropertyType.String)
            {
                return false;
            }
            var tag = keyProp.stringValue;
            if (string.IsNullOrEmpty(tag))
                return false;
            if (!tag.EndsWith("Animated", System.StringComparison.Ordinal))
                return false;
            if (valueProp.stringValue != "2")
                return false;
            var propName = tag[..^"Animated".Length];
            if (material.HasProperty(propName))
            {
                propertyName = propName;
                return true;
            }
            if (material.HasProperty($"{propName}_{raSuffix}"))
            {
                propertyName = $"{propName}_{raSuffix}";
                return true;
            }
            return false;
        }

        var result = new List<string>();
        using (var serializedMaterial = new SerializedObject(material))
        {
            var tagMap = serializedMaterial.FindProperty("stringTagMap");

            if (tagMap != null && tagMap.isArray)
            {
                for (int i = 0; i < tagMap.arraySize; i++)
                {
                    if (EntryHasRenameAnimatedTag(tagMap.GetArrayElementAtIndex(i), out var propertyName))
                        result.Add(propertyName);
                }
            }
        }

        result = result.Distinct().ToList();
        var resultArray = result.ToArray();
        propertiesMarkedAsRenameAnimatedCache[material] = resultArray;
        return resultArray;
    }

    private static Dictionary<string, List<string>> tooltipCache = null;
    private Dictionary<string, List<string>> TooltipCache
    {
        get
        {
            if (tooltipCache == null)
            {
                tooltipCache = new Dictionary<string, List<string>>();
                var path = AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(this));
                path = path.Substring(0, path.LastIndexOf('/'));
                using (var reader = new System.IO.StreamReader(path + "/../README.md"))
                {
                    string line;
                    string currentTooltip = "DUMMY_SECTION";
                    tooltipCache[currentTooltip] = new List<string>();
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.StartsWith("#") && line.IndexOf(' ') != -1)
                        {
                            int index = line.IndexOf(' ');
                            currentTooltip = line.Substring(index + 1);
                            tooltipCache[currentTooltip] = new List<string>();
                        }
                        else
                        {
                            tooltipCache[currentTooltip].Add(line);
                        }
                    }
                }

                // trim empty lines at start and end of the tooltips
                foreach (var pair in tooltipCache)
                {
                    while (pair.Value.Count > 0 && string.IsNullOrWhiteSpace(pair.Value[0]))
                        pair.Value.RemoveAt(0);
                    while (pair.Value.Count > 0 && string.IsNullOrWhiteSpace(pair.Value[pair.Value.Count - 1]))
                        pair.Value.RemoveAt(pair.Value.Count - 1);
                }
            }
            return tooltipCache;
        }
    }

    private bool Button(string label)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Space(15 * EditorGUI.indentLevel);
            return GUILayout.Button(label);
        }
    }

    private bool ToggleOptimizerProperty(string propertyName)
    {
        var property = typeof(SynthosFxOptimizer).GetProperty(propertyName);
        if (property == null)
            return false;
        var value = (bool)property.GetValue(optimizer);
        var output = value;
        var content = GetLabelWithTooltip(SynthosFxOptimizer.GetDisplayName(propertyName));
        using (new EditorGUI.DisabledScope(!optimizer.CanChangeSetting(propertyName)))
        {
            output = EditorGUILayout.ToggleLeft(content, value);
        }
        if (!string.IsNullOrEmpty(content.tooltip))
        {
            var rect = GUILayoutUtility.GetLastRect();
            rect.x += rect.width - 20;
            rect.width = 20;
            GUI.DrawTexture(rect, EditorGUIUtility.IconContent("_Help").image);
        }
        if (value != output)
        {
            ClearUICaches();
            EditorUtility.SetDirty(optimizer);
            property.SetValue(optimizer, output);
        }
        return output;
    }

    private void DrawWarningIconWithTooltip(string tooltip, Rect rect)
    {
        GUI.Label(rect, new GUIContent("", tooltip));
        GUI.DrawTexture(rect, EditorGUIUtility.IconContent("console.warnicon.sml").image);
    }

    private bool Foldout(string label, ref bool value, bool showNonDestructiveToolingWarning = false)
    {
        var content = GetLabelWithTooltip(label);
        bool output = EditorGUILayout.Foldout(value, content, true);
        var rect = GUILayoutUtility.GetLastRect();
        if (Event.current.type == EventType.MouseDown && Event.current.button == 1 && rect.Contains(Event.current.mousePosition))
        {
            ClearUICaches(force: true);
            Event.current.Use();
        }
        rect.x += rect.width;
        rect.width = 20;
        if (!string.IsNullOrEmpty(content.tooltip))
        {
            rect.x -= 20;
            GUI.Label(rect, new GUIContent("", content.tooltip));
            GUI.DrawTexture(rect, EditorGUIUtility.IconContent("_Help").image);
        }
        if (showNonDestructiveToolingWarning)
        {
            var warning = GetNonDestructiveToolingWarning(label);
            if (!string.IsNullOrEmpty(warning))
            {
                rect.x -= 20;
                DrawWarningIconWithTooltip(warning, rect);
            }
        }
        if (value != output)
        {
            EditorUtility.SetDirty(optimizer);
        }
        return value = output;
    }

    private GUIContent GetLabelWithTooltip(string label, string tooltipKey = null)
    {
        tooltipKey = tooltipKey ?? label;
        if (tooltipKey.EndsWith(")") && !TooltipCache.ContainsKey(tooltipKey))
        {
            tooltipKey = tooltipKey.Substring(0, tooltipKey.LastIndexOf('(')).TrimEnd();
        }
        if (TooltipCache.TryGetValue(tooltipKey, out var tooltip))
        {
            return new GUIContent(label, string.Join("\n", tooltip));
        }
        return new GUIContent(label);
    }

    private void DrawMatchedMaterialSlot(MaterialSlot slot, int indent)
    {
        indent *= 15;
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Space(indent);
            EditorGUILayout.ObjectField(slot.renderer, typeof(Renderer), true, GUILayout.Width(EditorGUIUtility.currentViewWidth / 2 - 20 - (indent)));
            int originalIndent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;
            EditorGUILayout.ObjectField(slot.material, typeof(Material), false);
            EditorGUI.indentLevel = originalIndent;
        }
    }

    public void DrawDebugList<T>(T[] array, System.Func<T, string> tooltipSelector = null) where T : Object
    {
        foreach (var obj in array)
        {
            EditorGUILayout.ObjectField(obj, typeof(T), true);
            if (tooltipSelector != null)
                GUI.Label(GUILayoutUtility.GetLastRect(), new GUIContent("", tooltipSelector(obj)));
        }
        if (array.Length == 0)
        {
            EditorGUILayout.LabelField("---");
        }
        else if (Button("Select All"))
        {
            if (typeof(Component).IsAssignableFrom(typeof(T)))
            {
                Selection.objects = array.Select(o => (o as Component).gameObject).ToArray();
            }
            else
            {
                Selection.objects = array;
            }
        }
    }

    public void DrawDebugList(string[] array)
    {
        foreach (var obj in array)
        {
            EditorGUILayout.LabelField(obj);
        }
        if (array.Length == 0)
        {
            EditorGUILayout.LabelField("---");
        }
    }

    private void DynamicExclusionList(SynthosFxOptimizer optimizer)
    {
        using var serializedObject = new SerializedObject(optimizer);
        var transforms = serializedObject.FindProperty(nameof(optimizer.ExcludeTransforms));
        var includeChildren = serializedObject.FindProperty(nameof(optimizer.ExcludeTransformChildren));

        while (includeChildren.arraySize < transforms.arraySize)
        {
            includeChildren.InsertArrayElementAtIndex(includeChildren.arraySize);
            includeChildren.GetArrayElementAtIndex(includeChildren.arraySize - 1).boolValue = true;
        }
        while (includeChildren.arraySize > transforms.arraySize)
            includeChildren.DeleteArrayElementAtIndex(includeChildren.arraySize - 1);

        transforms.InsertArrayElementAtIndex(transforms.arraySize);
        transforms.GetArrayElementAtIndex(transforms.arraySize - 1).objectReferenceValue = null;
        includeChildren.InsertArrayElementAtIndex(includeChildren.arraySize);
        includeChildren.GetArrayElementAtIndex(includeChildren.arraySize - 1).boolValue = true;

        for (int i = 0; i < transforms.arraySize; i++)
        {
            var transform = transforms.GetArrayElementAtIndex(i);
            var children = includeChildren.GetArrayElementAtIndex(i);
            Transform output;

            using var cc = new EditorGUI.ChangeCheckScope();
            using (new EditorGUILayout.HorizontalScope())
            {
                output = EditorGUILayout.ObjectField(transform.objectReferenceValue, typeof(Transform), true) as Transform;
                if (i == transforms.arraySize - 1)
                {
                    GUILayout.Space(46);
                }
                else
                {
                    children.boolValue = GUILayout.Toggle(children.boolValue,
                        new GUIContent("C", "Also exclude all children of this transform recursively"), GUI.skin.button, GUILayout.Width(20));
                    if (GUILayout.Button("X", GUILayout.Width(20)))
                        output = null;
                }
            }

            if (cc.changed)
                ClearUICaches();
            if (output != null && optimizer.GetTransformPathToRoot(output) == null)
                output = null;
            transform.objectReferenceValue = output;
        }

        for (int i = transforms.arraySize - 1; i >= 0; i--)
        {
            if (transforms.GetArrayElementAtIndex(i).objectReferenceValue != null)
                continue;
            transforms.DeleteArrayElementAtIndex(i);
            includeChildren.DeleteArrayElementAtIndex(i);
        }

        serializedObject.ApplyModifiedProperties();
    }

    static GUIContent _perfIcon_Excellent;
    static GUIContent _perfIcon_Good;
    static GUIContent _perfIcon_Medium;
    static GUIContent _perfIcon_Poor;
    static GUIContent _perfIcon_VeryPoor;

    private GUIContent GetPerformanceIconForRating(PerformanceRating rating)
    {
        if (_perfIcon_Excellent == null)
        {
            _perfIcon_Excellent = new GUIContent(Resources.Load<Texture>("PerformanceIcons/Perf_Great_32"));
            _perfIcon_Good = new GUIContent(Resources.Load<Texture>("PerformanceIcons/Perf_Good_32"));
            _perfIcon_Medium = new GUIContent(Resources.Load<Texture>("PerformanceIcons/Perf_Medium_32"));
            _perfIcon_Poor = new GUIContent(Resources.Load<Texture>("PerformanceIcons/Perf_Poor_32"));
            _perfIcon_VeryPoor = new GUIContent(Resources.Load<Texture>("PerformanceIcons/Perf_Horrible_32"));
        }

        return rating switch
        {
            PerformanceRating.Excellent => _perfIcon_Excellent,
            PerformanceRating.Good => _perfIcon_Good,
            PerformanceRating.Medium => _perfIcon_Medium,
            PerformanceRating.Poor => _perfIcon_Poor,
            _ => _perfIcon_VeryPoor,
        };
    }

    PerformanceRating GetPerfRank(int count, int[] perfLevels)
    {
        int level = 0;
        while(level < perfLevels.Length && count > perfLevels[level])
        {
            level++;
        }
        level++;
        return (PerformanceRating)level;
    }

    enum PerformanceCategory
    {
        SkinnedMeshCount,
        MeshCount,
        MaterialCount,
        FXLayerCount,
        BlendShapeCount,
    }

    static Dictionary<PerformanceCategory, int[]> _perfLevelsWindows = new Dictionary<PerformanceCategory, int[]>()
    {
        { PerformanceCategory.SkinnedMeshCount, new int[] {1, 2, 8, 16, int.MaxValue} },
        { PerformanceCategory.MeshCount, new int[] {4, 8, 16, 24, int.MaxValue} },
        { PerformanceCategory.MaterialCount, new int[] {4, 8, 16, 32, int.MaxValue} },
        { PerformanceCategory.FXLayerCount, new int[] {4, 8, 16, 32, int.MaxValue} },
        { PerformanceCategory.BlendShapeCount, new int[] {32, 48, 64, 128, int.MaxValue} },
    };
    static Dictionary<PerformanceCategory, int[]> _perfLevelsAndroid = new Dictionary<PerformanceCategory, int[]>()
    {
        { PerformanceCategory.SkinnedMeshCount, new int[] {1, 1, 2, 2, int.MaxValue} },
        { PerformanceCategory.MeshCount, new int[] {1, 1, 2, 2, int.MaxValue} },
        { PerformanceCategory.MaterialCount, new int[] {1, 1, 2, 4, int.MaxValue} },
        { PerformanceCategory.FXLayerCount, new int[] {2, 4, 8, 16, int.MaxValue} },
        { PerformanceCategory.BlendShapeCount, new int[] {24, 32, 48, 64, int.MaxValue} },
    };

    private void PerfRankChangeLabel(List<(string label, int oldValue, int newValue, PerformanceCategory category)> changes)
    {
        float oldValueWidth = changes.Max(c => c.oldValue.ToString().Length) * 7 + 7;
        float newValueWidth = changes.Max(c => c.newValue.ToString().Length) * 7 + 7;
        for (int i = 0; i < changes.Count; i++)
        {
            var (label, oldValue, newValue, category) = changes[i];
            var oldRating = PerformanceRating.VeryPoor;
            var newRating = PerformanceRating.VeryPoor;
            var perfLevels = SynthosFxOptimizer.HasCustomShaderSupport ? _perfLevelsWindows : _perfLevelsAndroid;
            if (perfLevels.ContainsKey(category))
            {
                oldRating = GetPerfRank(oldValue, perfLevels[category]);
                newRating = GetPerfRank(newValue, perfLevels[category]);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(GetPerformanceIconForRating(oldRating), GUILayout.Width(20));
                EditorGUILayout.LabelField($"{oldValue}", GUILayout.Width(oldValueWidth));
                EditorGUILayout.LabelField($"->", GUILayout.Width(20));
                EditorGUILayout.LabelField(GetPerformanceIconForRating(newRating), GUILayout.Width(20));
                EditorGUILayout.LabelField($"{newValue}", GUILayout.Width(newValueWidth));
                EditorGUILayout.LabelField(label);
            }

            if (i == 0)
            {
                var warning = GetNonDestructiveToolingWarning("Performance Rank Change Preview");
                if (!string.IsNullOrEmpty(warning))
                {
                    var rect = GUILayoutUtility.GetLastRect();
                    rect.x += rect.width - 20;
                    rect.width = 20;
                    DrawWarningIconWithTooltip(warning, rect);
                }
            }
        }
    }
}
#endif