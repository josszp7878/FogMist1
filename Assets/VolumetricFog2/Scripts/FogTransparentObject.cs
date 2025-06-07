#if UNITY_EDITOR
using UnityEditor.SceneManagement;
#endif
using UnityEngine;

namespace VolumetricFogAndMist2 {

    [ExecuteAlways]
    public class FogTransparentObject : MonoBehaviour {

        public VolumetricFog fogVolume;

        Renderer thisRenderer;
        Material mat;

        void OnEnable () {
            CheckSettings();
#if UNITY_EDITOR
            // workaround for volumetric effect disappearing when saving the scene
            EditorSceneManager.sceneSaving += OnSceneSaving;
#endif            
        }

        void OnDisable () {
#if UNITY_EDITOR
            EditorSceneManager.sceneSaving -= OnSceneSaving;
#endif
            if (fogVolume != null) {
                fogVolume.UnregisterFogMat(mat);
            }
        }

        void OnSceneSaving (UnityEngine.SceneManagement.Scene scene, string path) {
            CheckSettings();
        }


        void OnValidate () {
            CheckSettings();
        }

        void CheckSettings () {
            thisRenderer = GetComponent<Renderer>();
            if (thisRenderer == null) return;

            mat = thisRenderer.sharedMaterial;
            if (mat == null) return;

            if (fogVolume == null && VolumetricFog.volumetricFogs.Count > 0) {
                fogVolume = VolumetricFog.volumetricFogs[0];
                if (fogVolume == null) return;
            }

            fogVolume.RegisterFogMat(thisRenderer.sharedMaterial);
            fogVolume.UpdateMaterialProperties();
        }
    }
}
