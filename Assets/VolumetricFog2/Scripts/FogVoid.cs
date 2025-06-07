using UnityEngine;

namespace VolumetricFogAndMist2 {

    [ExecuteInEditMode]
    public class FogVoid : MonoBehaviour {

        [Range(0, 1)] public float roundness = 0.5f; // 控制雾效空洞的圆润度，值越大越圆润
        [Range(0, 1)] public float falloff = 0.5f;   // 控制雾效空洞边缘的过渡效果，值越大过渡越平滑

        private void OnEnable() {
            // 当组件启用时，向FogVoidManager注册此雾效空洞
            FogVoidManager.RegisterFogVoid(this);
        }

        private void OnDisable() {
            // 当组件禁用时，从FogVoidManager注销此雾效空洞
            FogVoidManager.UnregisterFogVoid(this);
        }

        void OnDrawGizmosSelected() {
            // 在Scene视图中绘制空洞的可视化边界
            Gizmos.color = new Color(1, 1, 0, 0.75F); // 黄色半透明

            if (VolumetricFogManager.allowFogVoidRotation) {
                // 如果允许旋转，使用本地到世界的变换矩阵
                Gizmos.matrix = transform.localToWorldMatrix;
                Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
            } else {
                // 否则只考虑位置和缩放
                Gizmos.DrawWireCube(transform.position, transform.lossyScale);
            }
        }
    }
}