#if UNITY_EDITOR
using System;
using UnityEngine;
using UnityEditor;
using VRC.SDKBase.Editor.BuildPipeline;
using System.Linq;

namespace d4rkpl4y3r.AvatarOptimizer
{
    [InitializeOnLoad]
    public class AvatarBuildHook : IVRCSDKPreprocessAvatarCallback
    {
        // Modular Avatar is at -25, we want to be after that. However usually vrcsdk removes IEditorOnly at -1024.
        // MA patches that to happen last so we can only be at -15 if MA is installed otherwise our component will be removed before getting run.
        #if MODULAR_AVATAR_EXISTS
        public int callbackOrder => -15;
        #else
        public int callbackOrder => -1025;
        #endif

        static private int playModeAvatarCount = 0;
        [InitializeOnEnterPlayMode]
        static void OnEnterPlaymodeInEditor(EnterPlayModeOptions options)
        {
            playModeAvatarCount = 0;
        }

        public bool OnPreprocessAvatar(GameObject avatarGameObject)
        {
            var optimizers = avatarGameObject.GetComponentsInChildren<SynthosFxOptimizer>(includeInactive: false);
            if (optimizers.Length > 1)
            {
                Debug.LogError($"SynthosFxOptimizer skipping avatar {avatarGameObject.name} because multiple active optimizer components found on avatar. Remove duplicates before uploading.");
                return false;
            }
            var optimizer = optimizers.Length == 1 ? optimizers[0] : null;
            if (optimizer == null && AvatarOptimizerSettings.DoOptimizeWithDefaultSettingsWhenNoComponent)
            {
                optimizer = avatarGameObject.AddComponent<SynthosFxOptimizer>();
                AvatarOptimizerSettings.ApplyDefaults(optimizer);
                optimizer.ApplyAutoSettings();
                optimizer.ApplyOnUpload = true;
                Debug.Log($"SynthosFxOptimizer added default optimizer component to avatar {avatarGameObject.name} because \"Always Optimize on Upload\" is enabled.");
            }
            if (optimizer == null)
            {
                var hasDisabledOptimizerComponent = avatarGameObject.GetComponentsInChildren<SynthosFxOptimizer>(includeInactive: true).Any();
                Debug.Log($"SynthosFxOptimizer skipping avatar {avatarGameObject.name} because "
                    + (hasDisabledOptimizerComponent ? "optimizer component is disabled." : "no optimizer component found."));
                return true;
            }
            if (!optimizer.ApplyOnUpload)
            {
                Debug.Log($"SynthosFxOptimizer skipping avatar {avatarGameObject.name} because \"Apply On Upload\" is disabled on the optimizer component.");
                return true;
            }
            try
            {
                if (Application.isPlaying && !AvatarOptimizerSettings.DoOptimizeInPlayMode)
                {
                    Debug.Log($"SynthosFxOptimizer skipping avatar {avatarGameObject.name} because \"Optimize in Play Mode\" is disabled.");
                    return true;
                }
                Debug.Log($"SynthosFxOptimizer optimizing avatar {avatarGameObject.name}.");
                optimizer.Optimize(Application.isPlaying ? ++playModeAvatarCount : 0);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return false;
            }
        }
    }
}
#endif