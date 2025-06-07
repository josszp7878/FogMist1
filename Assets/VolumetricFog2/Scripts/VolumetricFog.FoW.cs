//------------------------------------------------------------------------------------------------------------------
// Volumetric Fog & Mist 2
// Created by Kronnect
//------------------------------------------------------------------------------------------------------------------
using UnityEngine;
using System.Collections.Generic;
using UnityEngine.Serialization;


namespace VolumetricFogAndMist2 {


    public enum MaskTextureBrushMode {
        AddFog = 0,
        RemoveFog = 1,
        ColorFog = 2
    }


    public enum FoWUpdateMethod {
        MainThread,
        BackgroundThread
    }

    public partial class VolumetricFog : MonoBehaviour {

        // 战争迷雾是否开启
        public bool enableFogOfWar;
        // 战争迷雾中心
        public Vector3 fogOfWarCenter;
        // 战争迷雾是否本地
        public bool fogOfWarIsLocal;
        // 战争迷雾大小
        public Vector3 fogOfWarSize = new Vector3(1024, 0, 1024);
        // 战争迷雾显示覆盖
        public bool fogOfWarShowCoverage;
        // 战争迷雾纹理宽度
        [FormerlySerializedAs("fogOfWarTextureSize")]
        [Range(32, 2048)] public int fogOfWarTextureWidth = 256;
        // 战争迷雾纹理高度
        [Range(32, 2048)] public int fogOfWarTextureHeight;
        // 战争迷雾恢复延迟
        [Tooltip("Delay before the fog alpha is restored. A value of 0 keeps the fog cleared forever.")]
        [Range(0, 100)] public float fogOfWarRestoreDelay;
        // 战争迷雾恢复持续时间
        [Range(0, 25)] public float fogOfWarRestoreDuration = 2f;
        // 战争迷雾平滑度
        [Range(0, 1)] public float fogOfWarSmoothness = 1f;
        // 战争迷雾模糊
        public bool fogOfWarBlur;

        // 最大同时过渡数量
        const int MAX_SIMULTANEOUS_TRANSITIONS = 64000;
        // 是否可以销毁战争迷雾纹理
        bool canDestroyFOWTexture;
        // 当前时间
        float now;

        #region In-Editor fog of war painter
        // 是否启用战争迷雾编辑器
        public bool maskEditorEnabled;
        // 战争迷雾刷子模式
        public MaskTextureBrushMode maskBrushMode = MaskTextureBrushMode.RemoveFog;
        // 战争迷雾刷子颜色
        public Color maskBrushColor = Color.white;
        // 战争迷雾刷子宽度
        [Range(1, 128)] public int maskBrushWidth = 20;
        // 战争迷雾刷子模糊
        [Range(0, 1)] public float maskBrushFuzziness = 0.5f;
        // 战争迷雾刷子透明度
        [Range(0, 1)] public float maskBrushOpacity = 0.15f;

        #endregion

        // 战争迷雾纹理
        [SerializeField]
        Texture2D _fogOfWarTexture;

        // 锚定战争迷雾中心
        public Vector3 anchoredFogOfWarCenter => fogOfWarIsLocal ? transform.position + fogOfWarCenter : fogOfWarCenter;

        // 战争迷雾纹理
        public Texture2D fogOfWarTexture {
            get { return _fogOfWarTexture; }
            set {
                if (_fogOfWarTexture != value) {
                    if (value != null) {
                        _fogOfWarTexture = value;
                        canDestroyFOWTexture = false;
                        ReloadFogOfWarTexture();
                    } else {
                        if (canDestroyFOWTexture && _fogOfWarTexture != null) {
                            DestroyImmediate(_fogOfWarTexture);
                        }
                        _fogOfWarTexture = null;
                        canDestroyFOWTexture = false;
                    }
                    if (fogMat != null) {
                        fogMat.SetTexture(ShaderParams.FogOfWarTexture, _fogOfWarTexture);
                    }
                }
            }
        }


        // 战争迷雾颜色缓冲区
        Color32[] fogOfWarColorBuffer;

        // 战争迷雾过渡
        struct FogOfWarTransition {
            public bool enabled;
            public int x, y;
            public float startTime, startDelay;
            public float duration;
            public int initialAlpha;
            public int targetAlpha;

            public int restoreToAlpha;
            public float restoreDelay;
            public float restoreDuration;
        }

        // 战争迷雾过渡列表
        FogOfWarTransition[] fowTransitionList;
        // 最后过渡位置
        int lastTransitionPos;
        // 战争迷雾过渡索引
        Dictionary<int, int> fowTransitionIndices;
        // 战争迷雾自由索引
        Stack<int> fowFreeIndices;
        // 是否需要纹理上传
        bool requiresTextureUpload;
        // 是否后台线程繁忙
        bool backgroundThreadBusy;
        // 战争迷雾模糊材质
        Material fowBlurMat;
        // 战争迷雾模糊渲染纹理1
        RenderTexture fowBlur1;
        // 战争迷雾模糊渲染纹理2
        RenderTexture fowBlur2;
        // 锁
        static readonly object _lock = new object();

        // 战争迷雾初始化
        void FogOfWarInit () {
            if (fogOfWarTextureHeight == 0) {
                fogOfWarTextureHeight = fogOfWarTextureWidth;
            }
            // 如果过渡列表为空或者过渡列表长度不等于最大同时过渡数量
            if (fowTransitionList == null || fowTransitionList.Length != MAX_SIMULTANEOUS_TRANSITIONS) {
                // 设置过渡列表
                fowTransitionList = new FogOfWarTransition[MAX_SIMULTANEOUS_TRANSITIONS];
            }
            // 如果自由索引为空或者自由索引长度不等于最大同时过渡数量
            if (fowFreeIndices == null || fowFreeIndices.Count != MAX_SIMULTANEOUS_TRANSITIONS) {
                // 设置自由索引
                fowFreeIndices = new Stack<int>(MAX_SIMULTANEOUS_TRANSITIONS);
            } else {
                // 清空自由索引
                fowFreeIndices.Clear();
            }
            // 从最大同时过渡数量-1开始到0
            for (int k = MAX_SIMULTANEOUS_TRANSITIONS - 1; k >= 0; k--) {
                // 将k推入自由索引
                fowFreeIndices.Push(k);
            }
            // 初始化过渡
            InitTransitions();
            // 如果战争迷雾纹理为空
            if (_fogOfWarTexture == null) {
                // 更新战争迷雾纹理
                FogOfWarUpdateTexture();
            } else if (enableFogOfWar && (fogOfWarColorBuffer == null || fogOfWarColorBuffer.Length == 0)) {
                // 重新加载战争迷雾纹理
                ReloadFogOfWarTexture();
            }
            backgroundThreadBusy = false;
        }

        // 初始化过渡
        void InitTransitions () {
            lock (_lock) {
                // 如果过渡索引为空
                if (fowTransitionIndices == null) {
                    // 设置过渡索引
                    fowTransitionIndices = new Dictionary<int, int>(MAX_SIMULTANEOUS_TRANSITIONS);
                } else {
                    // 清空过渡索引
                    fowTransitionIndices.Clear();
                }
                // 设置lastTransitionPos
                lastTransitionPos = -1;
            }
        }

        // 销毁战争迷雾
        void FogOfWarDestroy () {
            // 如果可以销毁战争迷雾纹理并且战争迷雾纹理不为空
            if (canDestroyFOWTexture && _fogOfWarTexture != null) {
                // 销毁战争迷雾纹理
                DestroyImmediate(_fogOfWarTexture);
            }
            if (fowBlur1 != null) {
                fowBlur1.Release();
            }
            if (fowBlur2 != null) {
                fowBlur2.Release();
            }
            if (fowBlurMat != null) {
                DestroyImmediate(fowBlurMat);
            }
        }

        // 重新加载战争迷雾纹理
        public void ReloadFogOfWarTexture () {
            if (_fogOfWarTexture == null || profile == null) return;
            // 设置战争迷雾纹理宽度
            fogOfWarTextureWidth = _fogOfWarTexture.width;
            // 设置战争迷雾纹理高度
            fogOfWarTextureHeight = _fogOfWarTexture.height;
            // 确保纹理可读
            EnsureTextureIsReadable(_fogOfWarTexture);
            // 获取战争迷雾纹理像素
            fogOfWarColorBuffer = _fogOfWarTexture.GetPixels32();
            // 初始化过渡
            InitTransitions();
            // 如果战争迷雾未启用
            if (!enableFogOfWar) {
                // 启用战争迷雾
                enableFogOfWar = true;
                // 更新战争迷雾材质属性
                UpdateMaterialPropertiesNow();
            }
        }

        // 确保纹理可读
        void EnsureTextureIsReadable (Texture2D tex) {
#if UNITY_EDITOR
            string path = UnityEditor.AssetDatabase.GetAssetPath(tex);
            if (string.IsNullOrEmpty(path))
                return;
            UnityEditor.TextureImporter imp = UnityEditor.AssetImporter.GetAtPath(path) as UnityEditor.TextureImporter;
            if (imp != null && !imp.isReadable) {
                imp.isReadable = true;
                imp.SaveAndReimport();
            }
#endif
        }

        // 更新战争迷雾纹理
        void FogOfWarUpdateTexture () {
            // 如果战争迷雾未启用或者不在播放
            if (!enableFogOfWar || !Application.isPlaying)
                return;
            // 获取战争迷雾纹理宽度
            int width = GetScaledSize(fogOfWarTextureWidth, 1.0f);
            int height = GetScaledSize(fogOfWarTextureHeight, 1.0f);
            // 如果战争迷雾纹理为空或者战争迷雾纹理宽度不等于宽度或者战争迷雾纹理高度不等于高度
            if (_fogOfWarTexture == null || _fogOfWarTexture.width != width || _fogOfWarTexture.height != height) {
                // 创建战争迷雾纹理
                _fogOfWarTexture = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
                // 设置战争迷雾纹理隐藏标志
                _fogOfWarTexture.hideFlags = HideFlags.DontSave;
                // 设置战争迷雾纹理过滤模式
                _fogOfWarTexture.filterMode = FilterMode.Bilinear;
                // 设置战争迷雾纹理包装模式
                _fogOfWarTexture.wrapMode = TextureWrapMode.Clamp;
                // 设置可以销毁战争迷雾纹理
                canDestroyFOWTexture = true;
                // 重置战争迷雾
                ResetFogOfWar();
            }
        }

        int GetScaledSize (int size, float factor) {
            size = (int)(size / factor);
            size /= 4;
            if (size < 1)
                size = 1;
            return size * 4;
        }

        void UpdateFogOfWarMaterialBoundsProperties () {
            // 获取战争迷雾中心
            Vector3 fogOfWarCenter = anchoredFogOfWarCenter;
            // 设置战争迷雾中心
            fogMat.SetVector(ShaderParams.FogOfWarCenter, fogOfWarCenter);
            // 设置战争迷雾大小
            fogMat.SetVector(ShaderParams.FogOfWarSize, fogOfWarSize);
            // 计算战争迷雾中心调整
            Vector3 ca = fogOfWarCenter - 0.5f * fogOfWarSize;
            fogMat.SetVector(ShaderParams.FogOfWarCenterAdjusted, new Vector4(ca.x / fogOfWarSize.x, 1f, ca.z / (fogOfWarSize.z + 0.0001f), 0));
        }

        // 更新战争迷雾
        /// <summary>
        /// Updates fog of war transitions and uploads texture changes to GPU if required
        /// </summary>
        public void UpdateFogOfWar (bool forceUpload = false) {
            if (!enableFogOfWar || _fogOfWarTexture == null)
                return;

            if (forceUpload) {
                requiresTextureUpload = true;
            }

            int tw = _fogOfWarTexture.width;
            now = Time.time;

            lock (_lock) {
                // 遍历过渡列表
                for (int k = 0; k <= lastTransitionPos; k++) {
                    // 获取过渡
                    FogOfWarTransition fw = fowTransitionList[k];
                    // 如果过渡未启用
                    if (!fw.enabled)
                        continue;
                    float elapsed = now - fw.startTime - fw.startDelay;
                    if (elapsed > 0) {
                        // 计算过渡进度
                        float t = fw.duration <= 0 ? 1 : elapsed / fw.duration;
                        if (t < 0) t = 0; else if (t > 1f) t = 1f;
                        // 计算过渡alpha值
                        int alpha = (int)(fw.initialAlpha + (fw.targetAlpha - fw.initialAlpha) * t);
                        // 获取过渡颜色位置
                        int colorPos = fw.y * tw + fw.x;
                        // 设置过渡颜色alpha值
                        fogOfWarColorBuffer[colorPos].a = (byte)alpha;
                        // 设置需要上传战争迷雾纹理
                        requiresTextureUpload = true;
                        if (t >= 1f) {
                            // 添加填充槽如果需要
                            if (fw.targetAlpha != fw.restoreToAlpha && fw.restoreDelay > 0) {
                                // 设置过渡持续时间
                                fowTransitionList[k].duration = fw.restoreDuration;
                                // 设置过渡开始时间
                                fowTransitionList[k].startTime = now;
                                // 设置过渡开始延迟
                                fowTransitionList[k].startDelay = fw.restoreDelay;
                                // 设置过渡初始alpha值
                                fowTransitionList[k].initialAlpha = fw.targetAlpha;
                                // 设置过渡目标alpha值
                                fowTransitionList[k].targetAlpha = fw.restoreToAlpha;
                                // 设置过渡恢复延迟
                                fowTransitionList[k].restoreDelay = 0;
                                // 设置过渡恢复持续时间
                                fowTransitionList[k].restoreDuration = 0;
                            } else {
                                // 禁用过渡
                                fowTransitionList[k].enabled = false;
                                // 将过渡索引推入自由索引堆栈
                                fowFreeIndices.Push(k);
                                // 移除过渡索引
                                int key = fw.y * 64000 + fw.x;
                                fowTransitionIndices.Remove(key);
                            }
                        }
                    }
                }
            }
            if (requiresTextureUpload) {
                // 如果不需要后台线程繁忙
                if (!backgroundThreadBusy) {
                    requiresTextureUpload = false;
                    UploadFogOfWarTextureEditsToGPU();
                }
            }

            // 如果战争迷雾是本地
            if (fogOfWarIsLocal) {
                // 更新战争迷雾材质边界属性
                UpdateFogOfWarMaterialBoundsProperties();
            }
        }

        // 上传战争迷雾纹理编辑到GPU
        void UploadFogOfWarTextureEditsToGPU () {
            _fogOfWarTexture.SetPixels32(fogOfWarColorBuffer);
            _fogOfWarTexture.Apply();

            // 如果启用了战争迷雾模糊
            if (fogOfWarBlur) {
                // 设置战争迷雾模糊纹理
                SetFowBlurTexture();
            }

            // 如果不在播放
#if UNITY_EDITOR
            if (!Application.isPlaying) {
                // 设置战争迷雾模糊纹理为脏
                UnityEditor.EditorUtility.SetDirty(_fogOfWarTexture);
            }
#endif
        }

        // 设置战争迷雾模糊纹理
        void SetFowBlurTexture () {
            if (fogMat == null)
                return;

            if (fowBlurMat == null) {
                fowBlurMat = new Material(Shader.Find("Hidden/VolumetricFog2/FoWBlur"));
            }

            if (fowBlur1 == null || fowBlur1.width != _fogOfWarTexture.width || fowBlur2 == null || fowBlur2.width != _fogOfWarTexture.width) {
                CreateFoWBlurRTs();
            }
            // 丢弃内容
            fowBlur1.DiscardContents();
            // 将内容复制到fowBlur1,使用fowBlurMat,0
            Graphics.Blit(_fogOfWarTexture, fowBlur1, fowBlurMat, 0);
            // 丢弃内容
            fowBlur2.DiscardContents();
            // 将内容复制到fowBlur2,使用fowBlurMat,1
            Graphics.Blit(fowBlur1, fowBlur2, fowBlurMat, 1);
            // 设置战争迷雾模糊纹理
            fogMat.SetTexture(ShaderParams.FogOfWarTexture, fowBlur2);
        }

        // 创建战争迷雾模糊渲染纹理
        void CreateFoWBlurRTs () {
            // 如果fowBlur1不为空
            if (fowBlur1 != null) {
                // 释放fowBlur1
                fowBlur1.Release();
            }
            if (fowBlur2 != null) {
                fowBlur2.Release();
            }
            // 创建渲染纹理描述
            RenderTextureDescriptor desc = new RenderTextureDescriptor(_fogOfWarTexture.width, _fogOfWarTexture.height, RenderTextureFormat.ARGB32, 0);
            // 创建fowBlur1
            fowBlur1 = new RenderTexture(desc);
            // 创建fowBlur2
            fowBlur2 = new RenderTexture(desc);
        }

        // 设置战争迷雾alpha值
        /// <summary>
        /// 改变战争迷雾alpha值在世界位置创建一个过渡从当前alpha值到指定的目标alpha。它考虑了FogOfWarCenter和FogOfWarSize。
        /// 注意只使用x和z坐标。Y（垂直）坐标被忽略。
        /// </summary>
        /// <param name="worldPosition">在世界空间坐标中。</param>
        /// <param name="radius">应用的半径在世界单位中。</param>
        /// <param name="fogNewAlpha">目标alpha值。</param>
        /// <param name="duration">过渡持续时间（0 = 立即应用fogNewAlpha）。</param>
        public void SetFogOfWarAlpha (Vector3 worldPosition, float radius, float fogNewAlpha, float duration = 0) {
            SetFogOfWarAlpha(worldPosition, radius, fogNewAlpha, true, duration, fogOfWarSmoothness, fogOfWarRestoreDelay, fogOfWarRestoreDuration);
        }


        /// <summary>
        /// 改变战争迷雾alpha值在世界位置创建一个过渡从当前alpha值到指定的目标alpha。它考虑了FogOfWarCenter和FogOfWarSize。
        /// 注意只使用x和z坐标。Y（垂直）坐标被忽略。
        /// </summary>
        /// <param name="worldPosition">在世界空间坐标中。</param>
        /// <param name="radius">应用的半径在世界单位中。</param>
        /// <param name="fogNewAlpha">目标alpha值。</param>
        /// <param name="duration">过渡持续时间（0 = 立即应用fogNewAlpha）。</param>
        /// <param name="smoothness">边界平滑度（0 = 锐边，1 = 平滑过渡）。</param>
        public void SetFogOfWarAlpha (Vector3 worldPosition, float radius, float fogNewAlpha, float duration, float smoothness) {
            SetFogOfWarAlpha(worldPosition, radius, fogNewAlpha, true, duration, smoothness, fogOfWarRestoreDelay, fogOfWarRestoreDuration);
        }

        /// <summary>
        /// 改变战争迷雾alpha值在世界位置创建一个过渡从当前alpha值到指定的目标alpha。它考虑了FogOfWarCenter和FogOfWarSize。
        /// 注意只使用x和z坐标。Y（垂直）坐标被忽略。
        /// </summary>
        /// <param name="worldPosition">在世界空间坐标中。</param>
        /// <param name="radius">应用的半径在世界单位中。</param>
        /// <param name="blendAlpha">if new alpha is combined with preexisting alpha value or replaced.</param>
        /// <param name="fogNewAlpha">目标alpha值。</param>
        /// <param name="duration">过渡持续时间（0 = 立即应用fogNewAlpha）。</param>
        /// <param name="smoothness">边界平滑度（0 = 锐边，1 = 平滑过渡）。</param>
        /// <param name="updateMethod">如果方法应该在主线程上运行（立即效果）或在一个后台线程中运行（更新可以更慢，但不会影响主线程性能）。</param>
        public void SetFogOfWarAlpha (Vector3 worldPosition, float radius, float fogNewAlpha, float duration, float smoothness, FoWUpdateMethod updateMethod = FoWUpdateMethod.BackgroundThread, bool blendAlpha = true) {
            SetFogOfWarAlpha(worldPosition, radius, fogNewAlpha, blendAlpha, duration, smoothness, fogOfWarRestoreDelay, fogOfWarRestoreDuration, restoreToAlphaValue: 1f, updateMethod);
        }

        /// <summary>
        /// 改变战争迷雾alpha值在世界位置创建一个过渡从当前alpha值到指定的目标alpha。它考虑了FogOfWarCenter和FogOfWarSize。
        /// 注意只使用x和z坐标。Y（垂直）坐标被忽略。
        /// </summary>
        /// <param name="worldPosition">在世界空间坐标中。</param>
        /// <param name="radius">应用的半径在世界单位中。</param>
        /// <param name="fogNewAlpha">目标alpha值。</param>
        /// <param name="blendAlpha">如果新的alpha值与现有的alpha值结合或替换。</param>
        /// <param name="duration">过渡持续时间（0 = 立即应用fogNewAlpha）。</param>
        /// <param name="smoothness">边界平滑度（0 = 锐边，1 = 平滑过渡）。</param>
        /// <param name="restoreDelay">延迟恢复雾化alpha值。传递0以保持变化永久。</param>
        /// <param name="restoreDuration">恢复持续时间（秒）。</param>
        /// <param name="restoreToAlphaValue">恢复时最终的alpha值。</param>
        /// <param name="updateMethod">如果方法应该在主线程上运行（立即效果）或在一个后台线程中运行（更新可以更慢，但不会影响主线程性能）。</param>
        public void SetFogOfWarAlpha (Vector3 worldPosition, float radius, float fogNewAlpha, bool blendAlpha, float duration, float smoothness, float restoreDelay, float restoreDuration, float restoreToAlphaValue = 1f, FoWUpdateMethod updateMethod = FoWUpdateMethod.BackgroundThread) {

            if (_fogOfWarTexture == null || fogOfWarColorBuffer == null || fogOfWarColorBuffer.Length == 0)
                return;

            int tw = _fogOfWarTexture.width;
            int th = _fogOfWarTexture.height;
            now = Time.time;

            if (updateMethod == FoWUpdateMethod.BackgroundThread && Application.isPlaying) {
                System.Threading.Tasks.Task.Run(() => {
                    lock (_lock) {
                        try {
                            backgroundThreadBusy = true;
                            internal_SetFogOfWarAlpha(tw, th, worldPosition, radius, fogNewAlpha, blendAlpha, duration, smoothness, restoreDelay, restoreDuration, restoreToAlphaValue);
                        }
                        finally {
                            backgroundThreadBusy = false;
                        }
                    }
                });
                return;
            }
            internal_SetFogOfWarAlpha(tw, th, worldPosition, radius, fogNewAlpha, blendAlpha, duration, smoothness, restoreDelay, restoreDuration, restoreToAlphaValue);
        }

        void internal_SetFogOfWarAlpha (int tw, int th, Vector3 worldPosition, float radius, float fogNewAlpha, bool blendAlpha = false, float duration = 0, float smoothness = 0, float restoreDelay = 0, float restoreDuration = 2, float restoreToAlphaValue = 1f) {


            Vector3 fogOfWarCenter = anchoredFogOfWarCenter;

            float tx = (worldPosition.x - fogOfWarCenter.x) / fogOfWarSize.x + 0.5f;
            if (tx < 0 || tx > 1f)
                return;
            float tz = (worldPosition.z - fogOfWarCenter.z) / fogOfWarSize.z + 0.5f;
            if (tz < 0 || tz > 1f)
                return;

            int px = (int)(tx * tw);
            int pz = (int)(tz * th);
            float sm = 0.0001f + smoothness;
            byte newAlpha8 = (byte)(fogNewAlpha * 255);
            float tr = radius / fogOfWarSize.z;
            int delta = (int)(th * tr);
            int deltaSqr = delta * delta;
            byte restoreAlpha = (byte)(255 * restoreToAlphaValue);

            int minR = Mathf.Max(0, pz - delta);
            int maxR = Mathf.Min(th - 1, pz + delta);
            int minC = Mathf.Max(0, px - delta);
            int maxC = Mathf.Min(tw - 1, px + delta);

            for (int r = minR; r <= maxR; r++) {
                int rOffset = r * tw;
                int rDistSqr = (pz - r) * (pz - r);
                for (int c = minC; c <= maxC; c++) {
                    int cDistSqr = (px - c) * (px - c);
                    int distanceSqr = rDistSqr + cDistSqr;
                    if (distanceSqr <= deltaSqr) {
                        int colorBufferPos = rOffset + c;
                        Color32 colorBuffer = fogOfWarColorBuffer[colorBufferPos];
                        if (!blendAlpha) {
                            colorBuffer.a = 255;
                        }
                        distanceSqr = deltaSqr - distanceSqr;
                        float t = (float)distanceSqr / (deltaSqr * sm);
                        t = 1f - t;
                        if (t < 0) {
                            t = 0;
                        } else if (t > 1f) {
                            t = 1f;
                        }
                        byte targetAlpha = (byte)(newAlpha8 + (colorBuffer.a - newAlpha8) * t);
                        if (targetAlpha < 255 && (colorBuffer.a != targetAlpha || restoreDelay > 0)) {
                            if (duration > 0) {
                                AddFogOfWarTransitionSlot(c, r, colorBuffer.a, targetAlpha, 0, duration, restoreAlpha, restoreDelay, restoreDuration);
                            } else {
                                colorBuffer.a = targetAlpha;
                                fogOfWarColorBuffer[colorBufferPos] = colorBuffer;
                                requiresTextureUpload = true;
                                if (restoreDelay > 0) {
                                    AddFogOfWarTransitionSlot(c, r, targetAlpha, restoreAlpha, restoreDelay, restoreDuration, targetAlpha, 0, 0);
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 改变战争迷雾alpha值在边界内创建一个过渡从当前alpha值到指定的目标alpha。它考虑了FogOfWarCenter和FogOfWarSize。
        /// 注意只使用x和z坐标。Y（垂直）坐标被忽略。
        /// </summary>
        /// <param name="bounds">在世界空间坐标中。</param>
        /// <param name="fogNewAlpha">目标alpha值。</param>
        /// <param name="duration">过渡持续时间（0 = 立即应用fogNewAlpha）。</param>
        public void SetFogOfWarAlpha (Bounds bounds, float fogNewAlpha, float duration = 0) {
            SetFogOfWarAlpha(bounds, fogNewAlpha, false, duration, fogOfWarSmoothness, fogOfWarRestoreDelay, fogOfWarRestoreDuration);
        }

        /// <summary>
        /// 改变战争迷雾alpha值在边界内创建一个过渡从当前alpha值到指定的目标alpha。它考虑了FogOfWarCenter和FogOfWarSize。
        /// 注意只使用x和z坐标。Y（垂直）坐标被忽略。
        /// </summary>
        /// <param name="bounds">在世界空间坐标中。</param>
        /// <param name="fogNewAlpha">目标alpha值。</param>
        /// <param name="duration">过渡持续时间（0 = 立即应用fogNewAlpha）。</param>
        /// <param name="smoothness">边界平滑度。</param>
        public void SetFogOfWarAlpha (Bounds bounds, float fogNewAlpha, float duration, float smoothness) {
            SetFogOfWarAlpha(bounds, fogNewAlpha, false, duration, smoothness, fogOfWarRestoreDelay, fogOfWarRestoreDuration);
        }


        /// <summary>
        /// 改变战争迷雾alpha值在边界内创建一个过渡从当前alpha值到指定的目标alpha。它考虑了FogOfWarCenter和FogOfWarSize。
        /// 注意只使用x和z坐标。Y（垂直）坐标被忽略。
        /// </summary>
        /// <param name="bounds">在世界空间坐标中。</param>
        /// <param name="fogNewAlpha">目标alpha值（0-1）。</param>
        /// <param name="blendAlpha">if new alpha is combined with preexisting alpha value or replaced.</param>
        /// <param name="duration">过渡持续时间（0 = 立即应用fogNewAlpha）。</param>
        /// <param name="smoothness">边界平滑度。</param>
        /// <param name="fuzzyness">边界噪声随机化。</param>
        /// <param name="restoreDelay">延迟恢复雾化alpha值。传递0以保持变化永久。</param>
        /// <param name="restoreDuration">恢复持续时间（秒）。</param>
        /// <param name="restoreToAlpha">恢复时最终的alpha值。</param>
        public void SetFogOfWarAlpha (Bounds bounds, float fogNewAlpha, bool blendAlpha, float duration, float smoothness, float restoreDelay, float restoreDuration, float restoreToAlpha = 1f, FoWUpdateMethod updateMethod = FoWUpdateMethod.BackgroundThread) {
            if (_fogOfWarTexture == null || fogOfWarColorBuffer == null || fogOfWarColorBuffer.Length == 0)
                return;

            int tw = _fogOfWarTexture.width;
            int th = _fogOfWarTexture.height;
            now = Time.time;

            if (updateMethod == FoWUpdateMethod.BackgroundThread && Application.isPlaying) {
                System.Threading.Tasks.Task.Run(() => {
                    lock (_lock) {
                        try {
                            backgroundThreadBusy = true;
                            internal_SetFogOfWarAlpha(tw, th, bounds, fogNewAlpha, blendAlpha, duration, smoothness, restoreDelay, restoreDuration, restoreToAlpha);
                        }
                        finally {
                            backgroundThreadBusy = false;
                        }
                    }
                });
                return;
            }

            internal_SetFogOfWarAlpha(tw, th, bounds, fogNewAlpha, blendAlpha, duration, smoothness, restoreDelay, restoreDuration, restoreToAlpha);
        }

        void internal_SetFogOfWarAlpha (int tw, int th, Bounds bounds, float fogNewAlpha, bool blendAlpha, float duration = 0, float smoothness = 0, float restoreDelay = 0, float restoreDuration = 2, float restoreToAlpha = 1f) {

            Vector3 fogOfWarCenter = anchoredFogOfWarCenter;
            Vector3 worldPosition = bounds.center;

            float tx = (worldPosition.x - fogOfWarCenter.x) / fogOfWarSize.x + 0.5f;
            if (tx < 0 || tx > 1f)
                return;
            float tz = (worldPosition.z - fogOfWarCenter.z) / fogOfWarSize.z + 0.5f;
            if (tz < 0 || tz > 1f)
                return;

            int px = (int)(tx * tw);
            int pz = (int)(tz * th);
            byte newAlpha8 = (byte)(fogNewAlpha * 255);
            float trz = bounds.extents.z / fogOfWarSize.z;
            float trx = bounds.extents.x / fogOfWarSize.x;
            float aspect1 = trx > trz ? 1f : trz / trx;
            float aspect2 = trx > trz ? trx / trz : 1f;
            int deltaz = (int)(th * trz);
            int deltazSqr = deltaz * deltaz;
            int deltax = (int)(tw * trx);
            int deltaxSqr = deltax * deltax;
            float sm = 0.0001f + smoothness;
            byte restoreAlpha = (byte)(restoreToAlpha * 255);

            int minR = Mathf.Max(0, pz - deltaz);
            int maxR = Mathf.Min(th - 1, pz + deltaz);
            int minC = Mathf.Max(0, px - deltax);
            int maxC = Mathf.Min(tw - 1, px + deltax);

            for (int r = minR; r <= maxR; r++) {
                int rOffset = r * tw;
                int distancezSqr = (pz - r) * (pz - r);
                distancezSqr = deltazSqr - distancezSqr;
                float t1 = (float)distancezSqr * aspect1 / (deltazSqr * sm);

                for (int c = minC; c <= maxC; c++) {
                    int distancexSqr = (px - c) * (px - c);
                    int colorBufferPos = rOffset + c;
                    Color32 colorBuffer = fogOfWarColorBuffer[colorBufferPos];
                    if (!blendAlpha) colorBuffer.a = 255;

                    distancexSqr = deltaxSqr - distancexSqr;
                    float t2 = (float)distancexSqr * aspect2 / (deltaxSqr * sm);
                    float t = t1 < t2 ? t1 : t2;
                    t = 1f - t;
                    if (t < 0) t = 0; else if (t > 1f) t = 1f;
                    byte targetAlpha = (byte)(newAlpha8 + (colorBuffer.a - newAlpha8) * t);
                    if (targetAlpha < 255 && (colorBuffer.a != targetAlpha || restoreDelay > 0)) {
                        if (duration > 0) {
                            AddFogOfWarTransitionSlot(c, r, colorBuffer.a, targetAlpha, 0, duration, restoreAlpha, restoreDelay, restoreDuration);
                        } else {
                            colorBuffer.a = targetAlpha;
                            fogOfWarColorBuffer[colorBufferPos] = colorBuffer;
                            requiresTextureUpload = true;
                            if (restoreDelay > 0) {
                                AddFogOfWarTransitionSlot(c, r, targetAlpha, restoreAlpha, restoreDelay, restoreDuration, restoreAlpha, 0, 0);
                            }
                        }
                    }
                }
            }
        }


        /// <summary>
        /// 改变战争迷雾alpha值在碰撞器内创建一个过渡从当前alpha值到指定的目标alpha。它考虑了FogOfWarCenter和FogOfWarSize。
        /// 注意只使用x和z坐标。Y（垂直）坐标被忽略。
        /// </summary>
        /// <param name="collider">碰撞器用于定义战争迷雾alpha将被设置的区域。碰撞器必须是凸的。</param>
        /// <param name="fogNewAlpha">目标alpha值（0-1）。</param>
        /// <param name="blendAlpha">如果新的alpha值与现有的alpha值结合或替换。</param>
        /// <param name="duration">过渡持续时间（0 = 立即应用fogNewAlpha）。</param>
        /// <param name="smoothness">边界平滑度。</param>
        /// <param name="restoreDelay">延迟恢复雾化alpha值。传递0以保持变化永久。</param>
        /// <param name="restoreDuration">恢复持续时间（秒）。</param>
        /// <param name="restoreToAlpha">恢复时最终的alpha值。</param>
        public void SetFogOfWarAlpha (Collider collider, float fogNewAlpha, bool blendAlpha = false, float duration = 0, float smoothness = 0, float restoreDelay = 0, float restoreDuration = 2, float restoreToAlpha = 1f) {
            if (_fogOfWarTexture == null || fogOfWarColorBuffer == null || fogOfWarColorBuffer.Length == 0)
                return;

            Vector3 fogOfWarCenter = anchoredFogOfWarCenter;

            Bounds bounds = collider.bounds;
            Vector3 worldPosition = bounds.center;
            float tx = (worldPosition.x - fogOfWarCenter.x) / fogOfWarSize.x + 0.5f;
            if (tx < 0 || tx > 1f)
                return;
            float tz = (worldPosition.z - fogOfWarCenter.z) / fogOfWarSize.z + 0.5f;
            if (tz < 0 || tz > 1f)
                return;

            now = Time.time;
            int tw = _fogOfWarTexture.width;
            int th = _fogOfWarTexture.height;
            int px = (int)(tx * tw);
            int pz = (int)(tz * th);
            byte newAlpha8 = (byte)(fogNewAlpha * 255);
            float trz = bounds.extents.z / fogOfWarSize.z;
            float trx = bounds.extents.x / fogOfWarSize.x;
            float aspect1 = trx > trz ? 1f : trz / trx;
            float aspect2 = trx > trz ? trx / trz : 1f;
            int deltaz = (int)(th * trz);
            int deltazSqr = deltaz * deltaz;
            int deltax = (int)(tw * trx);
            int deltaxSqr = deltax * deltax;
            float sm = 0.0001f + smoothness;
            byte restoreAlpha = (byte)(restoreToAlpha * 255);


            Vector3 wpos = bounds.min;
            wpos.y = bounds.center.y;
            for (int rr = 0; rr <= deltaz * 2; rr++) {
                int r = pz - deltaz + rr;
                if (r > 0 && r < th - 1) {
                    int distancezSqr = (pz - r) * (pz - r);
                    distancezSqr = deltazSqr - distancezSqr;
                    float t1 = (float)distancezSqr * aspect1 / (deltazSqr * sm);
                    wpos.z = bounds.min.z + bounds.size.z * rr / (deltaz * 2f);
                    for (int cc = 0; cc <= deltax * 2; cc++) {
                        int c = px - deltax + cc;
                        if (c > 0 && c < tw - 1) {
                            wpos.x = bounds.min.x + bounds.size.x * cc / (deltax * 2f);
                            Vector3 colliderPos = collider.ClosestPoint(wpos);
                            if (colliderPos != wpos) continue; // point is outside collider

                            int distancexSqr = (px - c) * (px - c);
                            int colorBufferPos = r * tw + c;
                            Color32 colorBuffer = fogOfWarColorBuffer[colorBufferPos];
                            if (!blendAlpha) colorBuffer.a = 255;
                            distancexSqr = deltaxSqr - distancexSqr;
                            float t2 = (float)distancexSqr * aspect2 / (deltaxSqr * sm);
                            float t = t1 < t2 ? t1 : t2;
                            t = 1f - t;
                            if (t < 0) t = 0; else if (t > 1f) t = 1f;
                            byte targetAlpha = (byte)(newAlpha8 + (colorBuffer.a - newAlpha8) * t);
                            if (targetAlpha < 255 && (colorBuffer.a != targetAlpha || restoreDelay > 0)) {
                                if (duration > 0) {
                                    AddFogOfWarTransitionSlot(c, r, colorBuffer.a, targetAlpha, 0, duration, restoreAlpha, restoreDelay, restoreDuration);
                                } else {
                                    colorBuffer.a = targetAlpha;
                                    fogOfWarColorBuffer[colorBufferPos] = colorBuffer;
                                    requiresTextureUpload = true;
                                    if (restoreDelay > 0) {
                                        AddFogOfWarTransitionSlot(c, r, targetAlpha, restoreAlpha, restoreDelay, restoreDuration, restoreAlpha, 0, 0);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 改变战争迷雾alpha值在边界内创建一个过渡从当前alpha值到指定的目标alpha。它考虑了FogOfWarCenter和FogOfWarSize。
        /// 注意只使用x和z坐标。Y（垂直）坐标被忽略。
        /// </summary>
        /// <param name="go">用于定义战争迷雾alpha将被设置的区域的游戏对象。游戏对象必须有一个关联的网格。</param>
        /// <param name="fogNewAlpha">目标alpha值（0-1）。</param>
        /// <param name="duration">过渡持续时间（0 = 立即应用fogNewAlpha）。</param>
        /// <param name="restoreDelay">延迟恢复雾化alpha值。传递0以保持变化永久。</param>
        /// <param name="restoreDuration">恢复持续时间（秒）。</param>
        /// <param name="restoreToAlpha">恢复时最终的alpha值。</param>
        public void SetFogOfWarAlpha (GameObject go, float fogNewAlpha, float duration = 0, float restoreDelay = 0, float restoreDuration = 2, float restoreToAlpha = 1f, FoWUpdateMethod updateMethod = FoWUpdateMethod.BackgroundThread) {
            if (_fogOfWarTexture == null || fogOfWarColorBuffer == null || fogOfWarColorBuffer.Length == 0)
                return;

            if (go == null) return;

            int tw = _fogOfWarTexture.width;
            int th = _fogOfWarTexture.height;
            now = Time.time;

            MeshRenderer meshRenderer = go.GetComponentInChildren<MeshRenderer>();
            if (meshRenderer == null) {
                Debug.LogError("No MeshRenderer found on this object.");
                return;
            }

            Bounds bounds = meshRenderer.bounds;

            MeshFilter mf = meshRenderer.GetComponent<MeshFilter>();
            if (mf == null) {
                Debug.LogError("No MeshFilter found on this object.");
                return;
            }
            Mesh mesh = mf.sharedMesh;
            if (mesh == null) {
                Debug.LogError("No Mesh found on this object.");
                return;
            }
            if (mesh.GetTopology(0) != MeshTopology.Triangles) {
                Debug.LogError("Only triangle topology is supported by this tool.");
                return;
            }

            // Get triangle info
            int[] indices = mesh.triangles;
            Vector3[] vertices = mesh.vertices;

            int verticesLength = vertices.Length;
            Transform t = meshRenderer.transform;
            for (int k = 0; k < verticesLength; k++) {
                vertices[k] = t.TransformPoint(vertices[k]);
            }

            if (updateMethod == FoWUpdateMethod.BackgroundThread && Application.isPlaying) {
                System.Threading.Tasks.Task.Run(() => {
                    lock (_lock) {
                        try {
                            backgroundThreadBusy = true;
                            internal_SetFogOfWarAlpha(tw, th, bounds, indices, vertices, fogNewAlpha, duration, restoreDelay, restoreDuration, restoreToAlpha);
                        }
                        finally {
                            backgroundThreadBusy = false;
                        }
                    }
                });
                return;
            }

            internal_SetFogOfWarAlpha(tw, th, bounds, indices, vertices, fogNewAlpha, duration, restoreDelay, restoreDuration, restoreToAlpha);
        }


        void internal_SetFogOfWarAlpha (int tw, int th, Bounds bounds, int[] indices, Vector3[] vertices, float fogNewAlpha, float duration = 0, float restoreDelay = 0, float restoreDuration = 2, float restoreToAlpha = 1f) {

            Vector3 fogOfWarCenter = anchoredFogOfWarCenter;
            Vector3 worldPosition = bounds.center;
            float tx = (worldPosition.x - fogOfWarCenter.x) / fogOfWarSize.x + 0.5f;
            if (tx < 0 || tx > 1f)
                return;
            float tz = (worldPosition.z - fogOfWarCenter.z) / fogOfWarSize.z + 0.5f;
            if (tz < 0 || tz > 1f)
                return;

            byte newAlpha8 = (byte)(fogNewAlpha * 255);
            byte restoreAlpha = (byte)(restoreToAlpha * 255);

            int indicesLength = indices.Length;

            Vector2[] triangles = new Vector2[indicesLength];
            for (int k = 0; k < indicesLength; k += 3) {
                triangles[k].x = vertices[indices[k]].x;
                triangles[k].y = vertices[indices[k]].z;
                triangles[k + 1].x = vertices[indices[k + 1]].x;
                triangles[k + 1].y = vertices[indices[k + 1]].z;
                triangles[k + 2].x = vertices[indices[k + 2]].x;
                triangles[k + 2].y = vertices[indices[k + 2]].z;
            }
            int index = 0;

            int px = (int)(tx * tw);
            int pz = (int)(tz * th);
            float trz = bounds.extents.z / fogOfWarSize.z;
            float trx = bounds.extents.x / fogOfWarSize.x;
            int deltaz = (int)(th * trz);
            int deltax = (int)(tw * trx);
            int r0 = pz - deltaz;
            if (r0 < 1) r0 = 1; else if (r0 >= th) r0 = th - 1;
            int r1 = pz + deltaz;
            if (r1 < 1) r1 = 1; else if (r1 >= th) r1 = th - 1;
            int c0 = px - deltax;
            if (c0 < 1) c0 = 1; else if (c0 >= tw) c0 = tw - 1;
            int c1 = px + deltax;
            if (c1 < 1) c1 = 1; else if (c1 >= tw) c1 = tw - 1;

            Vector2 v0 = triangles[index];
            Vector2 v1 = triangles[index + 1];
            Vector2 v2 = triangles[index + 2];

            for (int r = r0; r <= r1; r++) {
                int rr = r * tw;
                float wz = (((r + 0.5f) / th) - 0.5f) * fogOfWarSize.z + fogOfWarCenter.z;
                for (int c = c0; c <= c1; c++) {
                    float wx = (((c + 0.5f) / tw) - 0.5f) * fogOfWarSize.x + fogOfWarCenter.x;
                    // Check if any triangle contains this position
                    for (int i = 0; i < indicesLength; i += 3) {
                        if (PointInTriangle(wx, wz, v0.x, v0.y, v1.x, v1.y, v2.x, v2.y)) {
                            int colorBufferPos = rr + c;
                            Color32 colorBuffer = fogOfWarColorBuffer[colorBufferPos];
                            if (colorBuffer.a != newAlpha8 || restoreDelay > 0) {
                                if (duration > 0) {
                                    AddFogOfWarTransitionSlot(c, r, colorBuffer.a, newAlpha8, 0, duration, restoreAlpha, restoreDelay, restoreDuration);
                                } else {
                                    colorBuffer.a = newAlpha8;
                                    fogOfWarColorBuffer[colorBufferPos] = colorBuffer;
                                    requiresTextureUpload = true;
                                    if (restoreDelay > 0) {
                                        AddFogOfWarTransitionSlot(c, r, newAlpha8, restoreAlpha, restoreDelay, restoreDuration, restoreAlpha, 0, 0);
                                    }
                                }
                            }
                            break;
                        } else {
                            index += 3;
                            index %= indicesLength;
                            v0 = triangles[index];
                            v1 = triangles[index + 1];
                            v2 = triangles[index + 2];
                        }
                    }
                }
            }
        }


        float Sign (float p1x, float p1z, float p2x, float p2z, float p3x, float p3z) {
            return (p1x - p3x) * (p2z - p3z) - (p2x - p3x) * (p1z - p3z);
        }

        bool PointInTriangle (float x, float z, float v1x, float v1z, float v2x, float v2z, float v3x, float v3z) {
            float d1 = Sign(x, z, v1x, v1z, v2x, v2z);
            float d2 = Sign(x, z, v2x, v2z, v3x, v3z);
            float d3 = Sign(x, z, v3x, v3z, v1x, v1z);

            bool has_neg = (d1 < 0) || (d2 < 0) || (d3 < 0);
            bool has_pos = (d1 > 0) || (d2 > 0) || (d3 > 0);

            return !(has_neg && has_pos);
        }


        /// <summary>
        /// 恢复战争迷雾到完全不透明
        /// </summary>
        /// <param name="worldPosition">世界位置。</param>
        /// <param name="radius">半径。</param>
        /// <param name="alpha">alpha值（1f = 完全不透明）</param>
        public void ResetFogOfWarAlpha (Vector3 worldPosition, float radius, float alpha = 1f) {
            if (_fogOfWarTexture == null || fogOfWarColorBuffer == null || fogOfWarColorBuffer.Length == 0)
                return;

            Vector3 fogOfWarCenter = anchoredFogOfWarCenter;

            float tx = (worldPosition.x - fogOfWarCenter.x) / fogOfWarSize.x + 0.5f;
            if (tx < 0 || tx > 1f)
                return;
            float tz = (worldPosition.z - fogOfWarCenter.z) / fogOfWarSize.z + 0.5f;
            if (tz < 0 || tz > 1f)
                return;

            int tw = _fogOfWarTexture.width;
            int th = _fogOfWarTexture.height;
            int px = (int)(tx * tw);
            int pz = (int)(tz * th);
            float tr = radius / fogOfWarSize.z;
            int delta = (int)(th * tr);
            int deltaSqr = delta * delta;
            byte fogAlpha = (byte)(alpha * 255);
            for (int r = pz - delta; r <= pz + delta; r++) {
                if (r > 0 && r < th - 1) {
                    for (int c = px - delta; c <= px + delta; c++) {
                        if (c > 0 && c < tw - 1) {
                            int distanceSqr = (pz - r) * (pz - r) + (px - c) * (px - c);
                            if (distanceSqr <= deltaSqr) {
                                int colorBufferPos = r * tw + c;
                                Color32 colorBuffer = fogOfWarColorBuffer[colorBufferPos];
                                colorBuffer.a = fogAlpha;
                                fogOfWarColorBuffer[colorBufferPos] = colorBuffer;
                                requiresTextureUpload = true;
                            }
                        }
                    }
                }
            }
            requiresTextureUpload = true;
        }



        /// <summary>
        /// 恢复战争迷雾到完全不透明
        /// </summary>
        public void ResetFogOfWarAlpha (Bounds bounds, float alpha = 1f) {
            ResetFogOfWarAlpha(bounds.center, bounds.extents.x, bounds.extents.z, alpha);
        }

        /// <summary>
        /// 恢复战争迷雾到完全不透明
        /// </summary>
        public void ResetFogOfWarAlpha (Vector3 position, Vector3 size, float alpha = 1f) {
            ResetFogOfWarAlpha(position, size.x * 0.5f, size.z * 0.5f, alpha);
        }

        /// <summary>
        /// 恢复战争迷雾到完全不透明
        /// </summary>
        /// <param name="position">世界空间中的位置。</param>
        /// <param name="extentsX">矩形在X轴的一半长度。</param>
        /// <param name="extentsZ">矩形在Z轴的一半长度。</param>
        /// <param name="alpha">alpha值（1f = 完全不透明）</param>
        public void ResetFogOfWarAlpha (Vector3 position, float extentsX, float extentsZ, float alpha = 1f) {
            if (_fogOfWarTexture == null || fogOfWarColorBuffer == null || fogOfWarColorBuffer.Length == 0)
                return;

            Vector3 fogOfWarCenter = anchoredFogOfWarCenter;

            float tx = (position.x - fogOfWarCenter.x) / fogOfWarSize.x + 0.5f;
            if (tx < 0 || tx > 1f)
                return;
            float tz = (position.z - fogOfWarCenter.z) / fogOfWarSize.z + 0.5f;
            if (tz < 0 || tz > 1f)
                return;

            int tw = _fogOfWarTexture.width;
            int th = _fogOfWarTexture.height;
            int px = (int)(tx * tw);
            int pz = (int)(tz * th);
            float trz = extentsZ / fogOfWarSize.z;
            float trx = extentsX / fogOfWarSize.x;
            int deltaz = (int)(th * trz);
            int deltax = (int)(tw * trx);
            byte fogAlpha = (byte)(alpha * 255);
            for (int r = pz - deltaz; r <= pz + deltaz; r++) {
                if (r > 0 && r < th - 1) {
                    for (int c = px - deltax; c <= px + deltax; c++) {
                        if (c > 0 && c < tw - 1) {
                            int colorBufferPos = r * tw + c;
                            Color32 colorBuffer = fogOfWarColorBuffer[colorBufferPos];
                            colorBuffer.a = fogAlpha;
                            fogOfWarColorBuffer[colorBufferPos] = colorBuffer;
                            requiresTextureUpload = true;
                        }
                    }
                }
            }
        }


        public void ResetFogOfWar (float alpha = 1f) {
            if (_fogOfWarTexture == null)
                return;
            int h = _fogOfWarTexture.height;
            int w = _fogOfWarTexture.width;
            int newLength = h * w;
            if (fogOfWarColorBuffer == null || fogOfWarColorBuffer.Length != newLength) {
                fogOfWarColorBuffer = new Color32[newLength];
            }
            Color32 opaque = new Color32(255, 255, 255, (byte)(alpha * 255));
            for (int k = 0; k < newLength; k++) {
                fogOfWarColorBuffer[k] = opaque;
            }
            UploadFogOfWarTextureEditsToGPU();
            InitTransitions();
        }

        /// <summary>
        /// 获取或设置战争迷雾状态作为Color32缓冲区。alpha通道存储该位置的雾化透明度（0 = 无雾，1 = 不透明）。
        /// </summary>
        public Color32[] fogOfWarTextureData {
            get {
                return fogOfWarColorBuffer;
            }
            set {
                enableFogOfWar = true;
                fogOfWarColorBuffer = value;
                if (value == null || _fogOfWarTexture == null)
                    return;
                if (value.Length != _fogOfWarTexture.width * _fogOfWarTexture.height)
                    return;
                UploadFogOfWarTextureEditsToGPU();
            }
        }

        // 添加战争迷雾过渡槽
        void AddFogOfWarTransitionSlot (int x, int y, byte initialAlpha, byte targetAlpha, float delay, float duration, byte restoreToAlpha, float restoreDelay, float restoreDuration) {

            // 检查这个槽是否存在
            int key = y * 64000 + x;
            if (fowTransitionIndices.TryGetValue(key, out int index)) {
                // 槽已经存在
                if (fowTransitionList[index].enabled) {
                    if (fowTransitionList[index].x != x || fowTransitionList[index].y != y) {
                        index = -1;
                    } else {
                        if (fowTransitionList[index].targetAlpha <= targetAlpha && fowTransitionList[index].restoreToAlpha == restoreToAlpha && fowTransitionList[index].restoreDelay == restoreDelay && fowTransitionList[index].restoreDuration == restoreDuration) {
                            // 过渡已经在运行到目标alpha
                            return;
                        }
                    }
                }
            } else {
                index = -1;
            }

            if (index < 0) {
                // 如果索引小于0
                if (!fowFreeIndices.TryPop(out index)) return;
                // 设置过渡索引
                fowTransitionIndices[key] = index;
                // 如果索引大于等于lastTransitionPos
                if (index >= lastTransitionPos) {
                    // 设置lastTransitionPos
                    lastTransitionPos = index;
                }
            }

            // 设置过渡列表
            fowTransitionList[index].x = x;
            fowTransitionList[index].y = y;
            fowTransitionList[index].duration = duration;
            fowTransitionList[index].startTime = now;
            fowTransitionList[index].startDelay = delay;
            fowTransitionList[index].initialAlpha = initialAlpha;
            fowTransitionList[index].targetAlpha = targetAlpha;
            fowTransitionList[index].restoreToAlpha = restoreToAlpha;
            fowTransitionList[index].restoreDelay = restoreDelay;
            fowTransitionList[index].restoreDuration = restoreDuration;

            fowTransitionList[index].enabled = true;
        }


        /// <summary>
        /// Gets the current alpha value of the Fog of War at a given world position
        /// </summary>
        /// <returns>The fog of war alpha.</returns>
        /// <param name="worldPosition">World position.</param>
        public float GetFogOfWarAlpha (Vector3 worldPosition) {
            if (fogOfWarColorBuffer == null || fogOfWarColorBuffer.Length == 0 || _fogOfWarTexture == null)
                return 1f;

            float tx = (worldPosition.x - fogOfWarCenter.x) / fogOfWarSize.x + 0.5f;
            if (tx < 0 || tx > 1f)
                return 1f;
            float tz = (worldPosition.z - fogOfWarCenter.z) / fogOfWarSize.z + 0.5f;
            if (tz < 0 || tz > 1f)
                return 1f;

            int tw = _fogOfWarTexture.width;
            int th = _fogOfWarTexture.height;
            int px = (int)(tx * tw);
            int pz = (int)(tz * th);
            int colorBufferPos = pz * tw + px;
            if (colorBufferPos < 0 || colorBufferPos >= fogOfWarColorBuffer.Length)
                return 1f;
            return fogOfWarColorBuffer[colorBufferPos].a / 255f;
        }

    }


}