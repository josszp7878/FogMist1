//#define FOG_ROTATION

//------------------------------------------------------------------------------------------------------------------
// Volumetric Fog & Mist 2
// Created by Kronnect
//------------------------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace VolumetricFogAndMist2 {

    public delegate void OnUpdateMaterialPropertiesEvent (VolumetricFog fogVolume);

    public enum VolumetricFogFollowMode {
        FullXYZ = 0,
        RestrictToXZPlane = 1
    }

    public enum VolumetricFogUpdateMode {
        WhenFogVolumeIsVisible = 1,
        WhenCameraIsInsideArea = 2
    }

    [ExecuteInEditMode]
    [DefaultExecutionOrder(100)]
    [HelpURL("https://kronnect.com/guides/volumetric-fog-urp-introduction/")]
    public partial class VolumetricFog : MonoBehaviour {

        public VolumetricFogProfile profile;

        public event OnUpdateMaterialPropertiesEvent OnUpdateMaterialProperties;

        [Tooltip("Supports Unity native lights including point and spot lights.")]
        public bool enableNativeLights;
        [Tooltip("Multiplier to native lights intensity")]
        public float nativeLightsMultiplier = 1f;
        [Tooltip("Enable fast point lights. This option is much faster than native lights. However, if you enable native lights, this option can't be enabled as point lights are already included in the native lights support.")]
        public bool enablePointLights;
        [Tooltip("Supports Adaptative Probe Volumes (Unity 2023.1+)")]
        public bool enableAPV;
        [Tooltip("Multiplier to native lights intensity")]
        public float apvIntensityMultiplier = 1f;
        public bool enableVoids;
        [Tooltip("Makes this fog volume follow another object automatically")]
        public bool enableFollow;
        public Transform followTarget;
        public VolumetricFogFollowMode followMode = VolumetricFogFollowMode.RestrictToXZPlane;
        public bool followIncludeDistantFog;
        public Vector3 followOffset;
        [Tooltip("Fades in/out fog effect when reference controller enters the fog volume.")]
        public bool enableFade;
        [Tooltip("Fog volume blending starts when reference controller is within this fade distance to any volume border.")]
        public float fadeDistance = 1;
        [Tooltip("If this option is disabled, the fog disappears when the reference controller exits the volume and appears when the controller enters the volume. Enable this option to fade out the fog volume when the controller enters the volume. ")]
        public bool fadeOut;
        [Tooltip("The controller (player or camera) to check if enters the fog volume.")]
        public Transform fadeController;
        [Tooltip("Enable sub-volume blending.")]
        public bool enableSubVolumes;
        [Tooltip("Allowed subVolumes. If no subvolumes are specified, any subvolume entered by this controller will affect this fog volume.")]
        public List<VolumetricFogSubVolume> subVolumes;
        [Tooltip("Customize how this fog volume data is updated and animated")]
        public bool enableUpdateModeOptions;
        public VolumetricFogUpdateMode updateMode = VolumetricFogUpdateMode.WhenFogVolumeIsVisible;
        [Tooltip("Camera used to compute visibility of this fog volume. If not set, the system will use the main camera.")]
        public Camera updateModeCamera;
        public Bounds updateModeBounds = new Bounds(Vector3.zero, Vector3.one * 100);
        [Tooltip("Shows the fog volume boundary in Game View")]
        public bool showBoundary;

        [NonSerialized]
        public MeshRenderer meshRenderer;
        MeshFilter mf;
        Material fogMat, noiseMat, turbulenceMat;
        Shader fogShader;
        RenderTexture rtNoise, rtTurbulence;
        float turbAcum;
        Vector4 windAcum, detailNoiseWindAcum;
        Vector3 sunDir;
        float dayLight, moonLight;
        Texture3D detailTex, refDetailTex;
        Mesh debugMesh;
        Material fogDebugMat;
        VolumetricFogProfile activeProfile, lerpProfile;
        Vector3 lastControllerPosition;
        float alphaMultiplier = 1f;
        Material distantFogMat;

        bool profileIsInstanced;
        bool requireUpdateMaterial;
        ColorSpace currentAppliedColorSpace;
        static Texture2D blueNoiseTex;
        Color ambientMultiplied;

        float lastVolumeHeight;
        Bounds cachedBounds;

        /// <summary>
        /// This property will return an instanced copy of the profile and use it for this volumetric fog from now on. Works similarly to Unity's material vs sharedMaterial.
        /// </summary>
        public VolumetricFogProfile settings {
            get {
                if (!profileIsInstanced && profile != null) {
                    profile = Instantiate(profile);
                    profileIsInstanced = true;
                }
                requireUpdateMaterial = true;
                return profile;
            }
            set {
                profile = value;
                profileIsInstanced = false;
                requireUpdateMaterial = true;
            }
        }

        [NonSerialized]
        public bool forceTerrainCaptureUpdate;

        public readonly static List<VolumetricFog> volumetricFogs = new List<VolumetricFog>();

        public Material material => fogMat;



        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Init () {
            volumetricFogs.Clear();
        }


        void OnEnable () {
            volumetricFogs.Add(this);
            VolumetricFogManager manager = Tools.CheckMainManager();
            FogOfWarInit();
            CheckSurfaceCapture();
            UpdateMaterialPropertiesNow();
        }

        void OnDisable () {
            if (volumetricFogs.Contains(this)) volumetricFogs.Remove(this);
            if (profile != null) {
                profile.onSettingsChanged -= UpdateMaterialProperties;
            }
        }

        void OnDidApplyAnimationProperties () {  // support for animating property based fields
            UpdateMaterialProperties();
        }

        void OnValidate () {
            nativeLightsMultiplier = Mathf.Max(0, nativeLightsMultiplier);
            apvIntensityMultiplier = Mathf.Max(0, apvIntensityMultiplier);
            UpdateMaterialProperties();
        }

        void OnDestroy () {
            if (rtNoise != null) {
                rtNoise.Release();
            }
            if (rtTurbulence != null) {
                rtTurbulence.Release();
            }
            if (fogMat != null) {
                DestroyImmediate(fogMat);
                fogMat = null;
            }
            if (distantFogMat != null) {
                DestroyImmediate(distantFogMat);
                distantFogMat = null;
            }
            FogOfWarDestroy();
            DisposeSurfaceCapture();
        }

        void OnDrawGizmosSelected () {
            if (enableFogOfWar && fogOfWarShowCoverage) {
                Gizmos.color = new Color(1, 0, 0, 0.75F);
                Vector3 position = anchoredFogOfWarCenter;
                position.y = transform.position.y;
                Vector3 size = fogOfWarSize;
                size.y = transform.localScale.y;
                Gizmos.DrawWireCube(position, size);
            }

            if (enableUpdateModeOptions && updateMode == VolumetricFogUpdateMode.WhenCameraIsInsideArea) {
                Gizmos.color = new Color(0, 1, 0, 0.75F);
                Gizmos.DrawWireCube(updateModeBounds.center, updateModeBounds.size);
            }

            Gizmos.color = new Color(1, 1, 0, 0.75F);
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
        }

        public Bounds GetBounds () {
            return new Bounds(transform.position, transform.localScale);
        }

        public void SetBounds (Bounds bounds) {
            transform.position = bounds.center;
            transform.localScale = bounds.size;
        }

        /// <summary>
        /// 每帧更新雾效的主要方法，处理位置、旋转、缩放和材质属性的更新
        /// </summary>
        void LateUpdate () {
            // 检查必要组件是否存在
            if (fogMat == null || meshRenderer == null || profile == null) return;

            // 如果启用了更新模式选项，检查是否需要更新（基于可见性或位置）
            if (enableUpdateModeOptions && !CanUpdate()) return;

            // 如果需要更新材质属性，立即执行
            if (requireUpdateMaterial) {
                requireUpdateMaterial = false;
                UpdateMaterialPropertiesNow();
            }

#if FOG_ROTATION
            // 如果启用了雾效旋转，计算旋转矩阵并应用到所有材质
            Matrix4x4 rot = Matrix4x4.TRS(Vector3.zero, transform.rotation, Vector3.one);
            foreach (var mat in fogMats) {
                mat.SetMatrix(ShaderParams.RotationMatrix, rot);
                mat.SetMatrix(ShaderParams.RotationInvMatrix, rot.inverse);
            }
#else
            // 默认情况下，雾效不支持旋转，强制设为单位旋转
            transform.rotation = Quaternion.identity;
#endif

            // 计算当前活动的配置文件（考虑淡入淡出和子体积）
            ComputeActiveProfile();

            // 如果启用了自定义高度，应用高度设置
            if (activeProfile.customHeight) {
                Vector3 scale = transform.localScale;
                // 如果高度未设置，使用当前缩放
                if (activeProfile.height == 0) {
                    activeProfile.height = scale.y;
                }
                // 如果当前缩放与设置的高度不匹配，更新缩放
                if (scale.y != activeProfile.height) {
                    scale.y = activeProfile.height;
                    transform.localScale = scale;
                }
            }

            // 如果启用了根据高度缩放噪声，且高度已更改，重新应用配置文件设置
            if (activeProfile.scaleNoiseWithHeight > 0 && lastVolumeHeight != transform.localScale.y) {
                ApplyProfileSettings();
            }

            // 如果启用了跟随目标，更新位置
            if (enableFollow && followTarget != null) {
                Vector3 position = followTarget.position;
                // 如果限制在XZ平面，保持当前Y坐标
                if (followMode == VolumetricFogFollowMode.RestrictToXZPlane) {
                    position.y = transform.position.y;
                }
                // 应用位置偏移
                transform.position = position + followOffset;
            }

            // 计算雾效体积的中心和范围
            Vector3 center, extents;
            if (activeProfile.shape == VolumetricFogShape.Custom && activeProfile.customMesh != null) {
                // 如果使用自定义网格形状，计算网格边界
                Bounds meshBounds = activeProfile.customMesh.bounds;
                Vector3 boundsCenter = transform.TransformPoint(meshBounds.center);
                Vector3 boundsExtents = Vector3.Scale(meshBounds.extents, transform.lossyScale);
                center = boundsCenter;
                extents = boundsExtents;
            } else {
                // 默认使用变换位置和缩放
                center = transform.position;
                extents = transform.lossyScale * 0.5f;
            }

            // 创建边界盒，用于可见性检测
            Bounds bounds = new Bounds(center, extents * 2f);

            // 检查是否需要重新应用配置文件设置
            bool requireApplyProfileSettings = enableFade || enableSubVolumes;
#if UNITY_EDITOR
            // 在编辑器中，如果颜色空间改变，也需要重新应用设置
            if (currentAppliedColorSpace != QualitySettings.activeColorSpace) {
                requireApplyProfileSettings = true;
            }
#endif
            if (requireApplyProfileSettings) {
                ApplyProfileSettings();
            }

            // 如果是球形雾效，确保X和Z缩放相同
            if (activeProfile.shape == VolumetricFogShape.Sphere) {
                Vector3 scale = transform.localScale;
                if (scale.z != scale.x) {
                    scale.z = scale.x;
                    transform.localScale = scale;
                    extents = transform.lossyScale * 0.5f;
                }
                // 对于球形，X范围需要平方（用于球体相交计算）
                extents.x *= extents.x;
            }

            // 计算边界过渡区域
            // x: 内边界X (最小值)
            // y: 外边界X (最大值)
            // z: 内边界Z (最小值)
            // w: 外边界Z (最大值)
            Vector4 border = new Vector4(
                extents.x * activeProfile.border + 0.0001f,  // 添加小偏移避免除零错误
                extents.x * (1f - activeProfile.border),
                extents.z * activeProfile.border + 0.0001f,
                extents.z * (1f - activeProfile.border)
            );

            // 如果启用了地形适配，确保Y范围至少等于地形雾高度
            if (activeProfile.terrainFit) {
                extents.y = Mathf.Max(extents.y, activeProfile.terrainFogHeight);
            }

            // 设置边界数据
            // x: 垂直偏移
            // y: 底部Y坐标
            // z: 高度
            // w: 未使用
            Vector4 boundsData = new Vector4(activeProfile.verticalOffset, center.y - extents.y, extents.y * 2f, 0);

            // 获取环境光颜色和强度
            Color ambientColor = RenderSettings.ambientLight;
            float ambientIntensity = RenderSettings.ambientIntensity;
            ambientMultiplied = ambientColor * ambientIntensity;

            // 获取全局管理器和太阳光源
            VolumetricFogManager globalManager = VolumetricFogManager.instance;
            Light sun = globalManager.sun;
            Color lightColor;
            Color sunColor;
            float sunIntensity;

            // 设置太阳方向、颜色和强度
            if (sun != null) {
                if (activeProfile.dayNightCycle) {
                    // 如果启用了昼夜循环，使用场景中的太阳
                    sunDir = -sun.transform.forward;
                    sunColor = sun.color;
                    sunIntensity = sun.intensity;
                } else {
                    // 否则使用配置文件中的设置
                    sunDir = activeProfile.sunDirection.normalized;
                    sunColor = activeProfile.sunColor;
                    sunIntensity = activeProfile.sunIntensity;
                }
            } else {
                // 如果没有太阳，使用默认值
                sunDir = activeProfile.sunDirection.normalized;
                sunColor = Color.white;
                sunIntensity = 1f;
            }

            // 计算日光因子（基于太阳高度）
            dayLight = 1f + sunDir.y * 2f;
            if (dayLight < 0) dayLight = 0; else if (dayLight > 1f) dayLight = 1f;
            float brightness = activeProfile.brightness;

            // 根据颜色空间调整颜色乘数
            float colorSpaceMultiplier = QualitySettings.activeColorSpace == ColorSpace.Gamma ? 2f : 1.33f;
            // 计算最终光照颜色
            lightColor = sunColor * (dayLight * sunIntensity * brightness * colorSpaceMultiplier);

            // 处理月光（如果有）
            Light moon = globalManager.moon;
            moonLight = 0;
            if (activeProfile.dayNightCycle && !enableNativeLights && moon != null) {
                Vector3 moonDir = -moon.transform.forward;
                // 计算月光因子（基于月亮高度）
                moonLight = 1f + moonDir.y * 2f;
                if (moonLight < 0) moonLight = 0; else if (moonLight > 1f) moonLight = 1f;
                // 添加月光颜色
                lightColor += moon.color * (moonLight * moon.intensity * brightness * colorSpaceMultiplier);
            }

            // 设置透明度
            lightColor.a = activeProfile.albedo.a;

            // 应用淡入淡出效果
            if (enableFade && fadeOut && Application.isPlaying) {
                // 淡出模式：当控制器进入体积时，雾效消失
                lightColor.a *= 1f - alphaMultiplier;
            } else {
                // 淡入模式：当控制器进入体积时，雾效出现
                lightColor.a *= alphaMultiplier;
            }

            // 添加环境光贡献
            lightColor.r += ambientMultiplied.r * activeProfile.ambientLightMultiplier;
            lightColor.g += ambientMultiplied.g * activeProfile.ambientLightMultiplier;
            lightColor.b += ambientMultiplied.b * activeProfile.ambientLightMultiplier;

            // 只有当密度大于0且透明度大于0时才启用渲染
            meshRenderer.enabled = activeProfile.density > 0 && lightColor.a > 0;

            // 获取帧时间，用于动画
            float deltaTime = Time.deltaTime;

            // 累积风向偏移，创建风吹动雾效的动画
            windAcum.x += activeProfile.windDirection.x * deltaTime;
            windAcum.y += activeProfile.windDirection.y * deltaTime;
            windAcum.z += activeProfile.windDirection.z * deltaTime;
            // 防止数值过大
            windAcum.x %= 10000;
            windAcum.y %= 10000;
            windAcum.z %= 10000;

            // 设置细节噪声的风向
            Vector4 detailWindDirection = windAcum;
            // 如果启用了自定义细节噪声风向，使用单独的风向设置
            if (activeProfile.useCustomDetailNoiseWindDirection) {
                // 累积细节噪声风向偏移
                detailNoiseWindAcum.x += activeProfile.detailNoiseWindDirection.x * deltaTime;
                detailNoiseWindAcum.y += activeProfile.detailNoiseWindDirection.y * deltaTime;
                detailNoiseWindAcum.z += activeProfile.detailNoiseWindDirection.z * deltaTime;
                // 防止数值过大
                detailNoiseWindAcum.x %= 10000;
                detailNoiseWindAcum.y %= 10000;
                detailNoiseWindAcum.z %= 10000;
                detailWindDirection = detailNoiseWindAcum;
            } else {
                // 否则使用与基础噪声相同的风向
                detailWindDirection = windAcum;
            }

            // 将所有计算好的参数设置到所有雾效材质
            foreach (var mat in fogMats) {
                // 设置边界和形状参数
                mat.SetVector(ShaderParams.BoundsCenter, center);
                mat.SetVector(ShaderParams.BoundsExtents, extents);
                mat.SetVector(ShaderParams.BoundsBorder, border);
                mat.SetVector(ShaderParams.BoundsData, boundsData);
                // 设置光照参数
                mat.SetVector(ShaderParams.SunDir, sunDir);
                mat.SetVector(ShaderParams.LightColor, lightColor);
                // 设置风向动画参数
                mat.SetVector(ShaderParams.WindDirection, windAcum);
                mat.SetVector(ShaderParams.DetailWindDirection, detailWindDirection);
            }

            // 更新噪声纹理
            UpdateNoise();

            // 如果启用了战争迷雾，更新战争迷雾
            if (enableFogOfWar) {
                UpdateFogOfWar();
            }

            // 如果启用了边界显示，绘制调试边界
            if (showBoundary) {
                // 创建调试材质（如果需要）
                if (fogDebugMat == null) {
                    fogDebugMat = new Material(Shader.Find("Hidden/VolumetricFog2/VolumeDebug"));
                }
                // 获取调试网格（如果需要）
                if (debugMesh == null) {
                    if (mf != null) {
                        debugMesh = mf.sharedMesh;
                    }
                }
                // 绘制调试边界
                Matrix4x4 m = Matrix4x4.TRS(
                    transform.position,
                    transform.rotation,
                    transform.lossyScale
                );
                Graphics.DrawMesh(debugMesh, m, fogDebugMat, 0);
            }

            // 如果启用了点光源但未启用原生光源，通知点光源管理器
            if (enablePointLights && !enableNativeLights) {
                PointLightManager.usingPointLights = true;
            }

            // 如果启用了雾效空洞，通知空洞管理器
            if (enableVoids) {
                FogVoidManager.usingVoids = true;
            }

            // 如果启用了地形适配，更新地形捕获
            if (activeProfile.terrainFit) {
                SurfaceCaptureUpdate();
            }

            // 如果启用了远距雾效，渲染远距雾效
            if (activeProfile.distantFog) {
                if (mf != null && distantFogMat != null) {
                    // 设置太阳方向和光照颜色
                    distantFogMat.SetVector(ShaderParams.SunDir, sunDir);
                    distantFogMat.SetVector(ShaderParams.LightColor, lightColor);

                    // 计算基础高度
                    float baseAltitude = activeProfile.distantFogBaseAltitude;
                    // 如果启用了跟随且包含远距雾效，调整基础高度
                    if (enableFollow
                        && followIncludeDistantFog
                        && followMode == VolumetricFogFollowMode.FullXYZ
                        && followTarget != null
                    ) {
                        baseAltitude += followTarget.position.y + followOffset.y;
                    }

                    // 设置远距雾效数据
                    distantFogMat.SetVector(ShaderParams.DistantFogData2,
                        new Vector4(
                            baseAltitude,
                            activeProfile.distantFogSymmetrical ? -1e6f : 1,
                            0,
                            0
                        )
                    );

                    // 如果不使用深度剥离，直接绘制远距雾效
                    if (!VolumetricFogRenderFeature.isUsingDepthPeeling) {
                        distantFogMat.renderQueue = activeProfile.distantFogRenderQueue;
                        const float bs = 50000; // 远距雾效使用非常大的尺寸
                        Matrix4x4 m = Matrix4x4.TRS(
                            transform.position,
                            Quaternion.identity, // 远距雾效不需要旋转
                            new Vector3(bs, bs, bs)
                        );
                        Graphics.DrawMesh(mf.sharedMesh, m, distantFogMat, gameObject.layer);
                    }
                }
            }
        }


        public void RenderDistantFog (CommandBuffer cmd) {
            if (mf == null || distantFogMat == null || !activeProfile.distantFog) return;
            const float bs = 50000;
            Matrix4x4 m = Matrix4x4.TRS(transform.position, Quaternion.identity, new Vector3(bs, bs, bs));
            UpdateDistantFogPropertiesNow();
            cmd.DrawMesh(mf.sharedMesh, m, distantFogMat);
        }

        Bounds cameraFrustumBounds;
        static readonly Vector3[] frustumVertices = new Vector3[8];
        Vector3 cameraFrustumLastPosition;
        Quaternion cameraFrustumLastRotation;


        bool CanUpdate () {

#if UNITY_EDITOR
            if (!Application.isPlaying) return true;
#endif

            Camera cam = updateModeCamera;
            if (cam == null) {
                cam = Camera.main;
                if (cam == null) return true;
            }

            bool isVisible;
            Transform camTransform = cam.transform;
            Vector3 camPos = camTransform.position;
            if (updateMode == VolumetricFogUpdateMode.WhenFogVolumeIsVisible) {
                Quaternion camRot = camTransform.rotation;
                if (camPos != cameraFrustumLastPosition || camRot != cameraFrustumLastRotation) {
                    cameraFrustumLastPosition = camPos;
                    cameraFrustumLastRotation = camRot;
                    CalculateFrustumBounds(cam);
                }
                if (transform.hasChanged || cachedBounds.size.x == 0) {
                    cachedBounds = meshRenderer.bounds;
                    transform.hasChanged = false;
                }
                isVisible = cameraFrustumBounds.Intersects(cachedBounds);
            } else {
                isVisible = updateModeBounds.Contains(camPos);
            }
            return isVisible;
        }

        void CalculateFrustumBounds (Camera camera) {
            CalculateFrustumVertices(camera);
            cameraFrustumBounds = new Bounds(frustumVertices[0], Vector3.zero);
            for (int k = 1; k < 8; k++) {
                cameraFrustumBounds.Encapsulate(frustumVertices[k]);
            }
        }

        void CalculateFrustumVertices (Camera cam) {
            float nearClipPlane = cam.nearClipPlane;
            frustumVertices[0] = cam.ViewportToWorldPoint(new Vector3(0, 0, nearClipPlane));
            frustumVertices[1] = cam.ViewportToWorldPoint(new Vector3(0, 1, nearClipPlane));
            frustumVertices[2] = cam.ViewportToWorldPoint(new Vector3(1, 0, nearClipPlane));
            frustumVertices[3] = cam.ViewportToWorldPoint(new Vector3(1, 1, nearClipPlane));
            float farClipPlane = cam.farClipPlane;
            frustumVertices[4] = cam.ViewportToWorldPoint(new Vector3(0, 0, farClipPlane));
            frustumVertices[5] = cam.ViewportToWorldPoint(new Vector3(0, 1, farClipPlane));
            frustumVertices[6] = cam.ViewportToWorldPoint(new Vector3(1, 0, farClipPlane));
            frustumVertices[7] = cam.ViewportToWorldPoint(new Vector3(1, 1, farClipPlane));
        }

        /// <summary>
        /// 更新噪声纹理，用于创建动态雾效
        /// </summary>
        void UpdateNoise () {
            // 检查必要的组件和资源
            if (activeProfile == null) return;
            Texture noiseTex = activeProfile.noiseTexture;
            if (noiseTex == null) return;

            // 根据日光和月光计算雾效强度
            float fogIntensity = 1.15f;
            fogIntensity *= dayLight + moonLight;
            // 在环境光和配置的反照率之间进行插值，创建基础颜色
            Color textureBaseColor = Color.Lerp(ambientMultiplied, activeProfile.albedo * fogIntensity, fogIntensity);

            // 如果不使用常量密度，则生成动态噪声
            if (!activeProfile.constantDensity) {
                // 创建湍流渲染纹理（如果需要）
                if (rtTurbulence == null || rtTurbulence.width != noiseTex.width) {
                    RenderTextureDescriptor desc = new RenderTextureDescriptor(noiseTex.width, noiseTex.height, RenderTextureFormat.ARGB32, 0);
                    rtTurbulence = new RenderTexture(desc);
                    rtTurbulence.wrapMode = TextureWrapMode.Repeat; // 设置为重复模式，实现无缝平铺
                }

                // 累积湍流时间，创建动画效果
                turbAcum += Time.deltaTime * activeProfile.turbulence;
                turbAcum %= 10000; // 防止数值过大

                // 设置湍流材质参数
                turbulenceMat.SetFloat(ShaderParams.TurbulenceAmount, turbAcum);
                turbulenceMat.SetFloat(ShaderParams.NoiseStrength, activeProfile.noiseStrength);
                turbulenceMat.SetFloat(ShaderParams.NoiseFinalMultiplier, activeProfile.noiseFinalMultiplier);

                // 将基础噪声纹理通过湍流材质渲染到湍流渲染纹理
                Graphics.Blit(noiseTex, rtTurbulence, turbulenceMat);

                // 创建最终噪声渲染纹理，可能使用较小的尺寸以提高性能
                int noiseSize = Mathf.Min(noiseTex.width, (int)activeProfile.noiseTextureOptimizedSize);
                if (rtNoise == null || rtNoise.width != noiseSize) {
                    RenderTextureDescriptor desc = new RenderTextureDescriptor(noiseSize, noiseSize, RenderTextureFormat.ARGB32, 0);
                    rtNoise = new RenderTexture(desc);
                    rtNoise.wrapMode = TextureWrapMode.Repeat;
                }

                // 设置高光参数
                noiseMat.SetColor(ShaderParams.SpecularColor, activeProfile.specularColor);
                noiseMat.SetFloat(ShaderParams.SpecularIntensity, activeProfile.specularIntensity);

                // 计算高光阈值，基于太阳方向
                float spec = 1.0001f - activeProfile.specularThreshold;
                float nlighty = sunDir.y > 0 ? (1.0f - sunDir.y) : (1.0f + sunDir.y);
                float nyspec = nlighty / spec;

                // 设置高光和太阳方向参数
                noiseMat.SetFloat(ShaderParams.SpecularThreshold, nyspec);
                noiseMat.SetVector(ShaderParams.SunDir, sunDir);

                // 设置基础颜色并渲染最终噪声纹理
                noiseMat.SetColor(ShaderParams.Color, textureBaseColor);
                Graphics.Blit(rtTurbulence, rtNoise, noiseMat);
            }

            // 创建细节颜色，用于3D噪声
            Color detailColor = new Color(textureBaseColor.r * 0.5f, textureBaseColor.g * 0.5f, textureBaseColor.b * 0.5f, 0);

            // 将噪声纹理和细节颜色应用到所有雾效材质
            foreach (var mat in fogMats) {
                mat.SetColor(ShaderParams.DetailColor, detailColor);
                mat.SetTexture(ShaderParams.NoiseTex, rtNoise);
            }
        }


        public void UpdateMaterialProperties () {
            UpdateMaterialProperties(false);
        }

        /// <summary>
        /// Schedules an update of the fog properties at end of this frame
        /// </summary>
        /// <param name="forceTerrainCaptureUpdate">In addition to apply any fog property change, perform a terrain heightmap capture (if Terrain Fit option is enabled)</param>
        public void UpdateMaterialProperties (bool forceTerrainCaptureUpdate) {
#if UNITY_EDITOR
            if (!Application.isPlaying && activeProfile != null) {
                UpdateMaterialPropertiesNow(true);
            }
#endif
            if (forceTerrainCaptureUpdate) {
                this.forceTerrainCaptureUpdate = true;
            }
            requireUpdateMaterial = true;
        }

        /// <summary>
        /// Forces an immediate material update
        /// </summary>
        /// <param name="skipTerrainCapture">Applies all fog properties changes but do not perform a terrain heightmap capture (if Terrain Fit option is enabled)</param>
        /// <param name="forceTerrainCaptureUpdate">In addition to apply any fog property change, perform a terrain heightmap capture (if Terrain Fit option is enabled).</param>
        public void UpdateMaterialPropertiesNow (bool skipTerrainCapture = false, bool forceTerrainCaptureUpdate = false) {

            if (gameObject == null || !gameObject.activeInHierarchy) {
                return;
            }

            if (forceTerrainCaptureUpdate) {
                this.forceTerrainCaptureUpdate = true;
            }

            if (gameObject.layer == 0) { // fog layer cannot be default so terrain fit culling mask can work properly
                gameObject.layer = 1;
            }

            fadeDistance = Mathf.Max(0.1f, fadeDistance);

            if (meshRenderer == null) {
                meshRenderer = GetComponent<MeshRenderer>();
            }
            if (mf == null) {
                mf = GetComponent<MeshFilter>();
            }
            
            // 如果profile为空，则创建一个空的材质
            if (profile == null) {
                if (fogMat == null && meshRenderer != null) {
                    fogMat = new Material(Shader.Find("Hidden/VolumetricFog2/Empty"));
                    fogMat.hideFlags = HideFlags.DontSave;
                    meshRenderer.sharedMaterial = fogMat;
                }
                return;
            }

            // 订阅profile变化事件
            profile.onSettingsChanged -= UpdateMaterialProperties;
            profile.onSettingsChanged += UpdateMaterialProperties;

            // 订阅子体积profile变化事件
            if (subVolumes != null) {
                foreach (VolumetricFogSubVolume subVol in subVolumes) {
                    if (subVol != null && subVol.profile != null) {
                        subVol.profile.onSettingsChanged -= UpdateMaterialProperties;
                        subVol.profile.onSettingsChanged += UpdateMaterialProperties;
                    }
                }
            }

            // 如果湍流材质为空，则创建一个湍流材质
            if (turbulenceMat == null) {
                turbulenceMat = new Material(Shader.Find("Hidden/VolumetricFog2/Turbulence2D"));
            }

            // 如果噪声材质为空，则创建一个噪声材质
            if (noiseMat == null) {
                noiseMat = new Material(Shader.Find("Hidden/VolumetricFog2/Noise2DGen"));
            }

            // 如果蓝色噪声纹理为空，则加载一个蓝色噪声纹理
            if (blueNoiseTex == null) {
                blueNoiseTex = Resources.Load<Texture2D>("Textures/BlueNoiseVF128");
            }

            // 如果雾效材质为空，则创建一个雾效材质
            if (meshRenderer != null) {
                fogMat = meshRenderer.sharedMaterial;
                if (fogShader == null) {
                    fogShader = Shader.Find("VolumetricFog2/VolumetricFog2DURP");
                    if (fogShader == null) return;
                    // 确保这个雾效材质不会复制其他雾效体积（当在场景中复制一个雾效体积时发生）
                    foreach (VolumetricFog fog in volumetricFogs) {
                        if (fog != null && fog != this && fog.fogMat == fogMat) {
                            fogMat = null;
                            break;
                        }
                    }
                }
                // 如果雾效材质为空，或者雾效材质的shader不是雾效shader，则创建一个新的雾效材质
                if (fogMat == null || fogMat.shader != fogShader) {
                    fogMat = new Material(fogShader);
                    meshRenderer.sharedMaterial = fogMat;
                }
            }

            // 如果雾效材质为空，则返回
            if (fogMat == null) return;

            // 验证配置文件设置
            profile.ValidateSettings();

            // 更新控制器位置
            lastControllerPosition.x = float.MaxValue;

            // 设置活动配置文件
            activeProfile = profile;

            // 计算活动配置文件
            ComputeActiveProfile();

            // 应用配置文件设置
            ApplyProfileSettings();

            // 如果不需要地形捕获，则检查地形捕获支持
            if (!skipTerrainCapture) {
                SurfaceCaptureSupportCheck();
            }

            OnUpdateMaterialProperties?.Invoke(this);
        }

        void ComputeActiveProfile () {

            if (Application.isPlaying) {
                if (enableFade || enableSubVolumes) {
                    if (fadeController == null) {
                        Camera cam = Camera.main;
                        if (cam != null) {
                            fadeController = Camera.main.transform;
                        }
                    }
                    if (fadeController != null && lastControllerPosition != fadeController.position) {

                        lastControllerPosition = fadeController.position;
                        activeProfile = profile;
                        alphaMultiplier = 1f;

                        // Self volume
                        if (enableFade) {
                            float t = ComputeVolumeFade(transform, fadeDistance);
                            alphaMultiplier *= t;
                        }

                        // Check sub-volumes
                        if (enableSubVolumes) {
                            int subVolumeCount = VolumetricFogSubVolume.subVolumes.Count;
                            int allowedSubVolumesCount = subVolumes != null ? subVolumes.Count : 0;
                            for (int k = 0; k < subVolumeCount; k++) {
                                VolumetricFogSubVolume subVolume = VolumetricFogSubVolume.subVolumes[k];
                                if (subVolume == null || subVolume.profile == null) continue;
                                if (allowedSubVolumesCount > 0 && !subVolumes.Contains(subVolume)) continue;
                                float t = ComputeVolumeFade(subVolume.transform, subVolume.fadeDistance);
                                if (t > 0) {
                                    if (lerpProfile == null) {
                                        lerpProfile = ScriptableObject.CreateInstance<VolumetricFogProfile>();
                                    }
                                    lerpProfile.Lerp(activeProfile, subVolume.profile, t);
                                    activeProfile = lerpProfile;
                                }
                            }
                        }
                    }
                } else {
                    alphaMultiplier = 1f;
                }
            }

            if (activeProfile == null) {
                activeProfile = profile;
            }
        }

        float ComputeVolumeFade (Transform transform, float fadeDistance) {
            Vector3 diff = transform.position - fadeController.position;
            diff.x = diff.x < 0 ? -diff.x : diff.x;
            diff.y = diff.y < 0 ? -diff.y : diff.y;
            diff.z = diff.z < 0 ? -diff.z : diff.z;
            Vector3 extents = transform.lossyScale * 0.5f;
            Vector3 gap = diff - extents;
            float maxDiff = gap.x > gap.y ? gap.x : gap.y;
            maxDiff = maxDiff > gap.z ? maxDiff : gap.z;
            fadeDistance += 0.0001f;
            float t = 1f - Mathf.Clamp01(maxDiff / fadeDistance);
            return t;
        }


        /// <summary>
        /// 应用当前活动配置文件的所有设置到雾效材质
        /// </summary>
        void ApplyProfileSettings () {

            // 记录当前颜色空间，用于后续颜色调整
            currentAppliedColorSpace = QualitySettings.activeColorSpace;

            // 保存当前体积高度，用于噪声缩放计算
            lastVolumeHeight = transform.localScale.y;

            // 设置渲染排序属性
            meshRenderer.sortingLayerID = activeProfile.sortingLayerID;
            meshRenderer.sortingOrder = activeProfile.sortingOrder;
            fogMat.renderQueue = activeProfile.renderQueue;

            // 注册主雾效材质
            RegisterFogMat(fogMat);

            // 为所有注册的雾效材质应用属性
            foreach (var mat in fogMats) {
                SetFogMaterialProperties(mat);
            }

            // 如果启用了远距雾效，更新其属性
            if (activeProfile.distantFog) {
                UpdateDistantFogPropertiesNow();
            }
        }

        /// <summary>
        /// 设置雾效材质的所有属性，包括噪声、光照、阴影和光线步进设置
        /// </summary>
        /// <param name="mat">要设置属性的材质</param>
        void SetFogMaterialProperties (Material mat) {
            // 设置雾效材质的所有属性，包括噪声、光照、阴影和光线步进设置
            if (activeProfile == null) return;

            // 计算噪声缩放比例，可以根据体积高度进行调整
            float noiseScale = activeProfile.noiseScale;
            if (activeProfile.scaleNoiseWithHeight > 0) {
                // 根据体积高度调整噪声缩放，创建更自然的高度变化
                noiseScale *= Mathf.Lerp(1f, transform.localScale.y * 0.04032f, activeProfile.scaleNoiseWithHeight);
            }
            // 转换为shader中使用的格式（倒数）
            noiseScale = 0.1f / noiseScale;

            // 设置基本噪声参数
            mat.SetFloat(ShaderParams.NoiseScale, noiseScale);

            // 设置深度遮蔽效果，根据颜色空间进行调整
            mat.SetFloat(ShaderParams.DeepObscurance, activeProfile.deepObscurance * (currentAppliedColorSpace == ColorSpace.Gamma ? 1f : 1.2f));

            // 设置光散射数据
            // x: 散射强度 - 对于平滑和强散射模型，需要除以256.1以适应相位函数
            // y: 散射强度乘数
            // z: 近距离深度衰减
            mat.SetVector(ShaderParams.LightDiffusionData, new Vector4(
                activeProfile.lightDiffusionModel != DiffusionModel.Simple ? activeProfile.lightDiffusionPower / 256.1f : activeProfile.lightDiffusionPower,
                activeProfile.lightDiffusionIntensity,
                activeProfile.lightDiffusionNearDepthAtten
            ));

            // 设置阴影数据
            // x: 阴影强度
            // y: 阴影对透明度的影响（阴影消除）
            // z: 阴影最大距离
            mat.SetVector(ShaderParams.ShadowData, new Vector4(
                activeProfile.shadowIntensity,
                activeProfile.shadowCancellation,
                activeProfile.shadowMaxDistance,
                0
            ));

            // 设置基本密度
            mat.SetFloat(ShaderParams.Density, activeProfile.density);

            // 设置原生光源和APV强度乘数
            mat.SetFloat(ShaderParams.NativeLightsMultiplier, nativeLightsMultiplier);
            mat.SetFloat(ShaderParams.APVIntensityMultiplier, apvIntensityMultiplier);

            // 设置光线步进参数
            // x: 光线步进质量的倒数（较小的值 = 更高质量）
            // y: 抖动强度（减少条带伪影）
            // z: 抖动量（随机偏移光线起点）
            // w: 最小步长
            mat.SetVector(ShaderParams.RaymarchSettings, new Vector4(
                1f / activeProfile.raymarchQuality,
                activeProfile.dithering * 0.01f,
                activeProfile.jittering,
                activeProfile.raymarchMinStep
            ));

            // 以下是各种shader关键字的启用/禁用，根据当前配置文件的设置
            // ... 之后的代码处理各种渲染特性的开关和参数设置
        }

        void UpdateDistantFogPropertiesNow () {
            if (distantFogMat == null) {
                distantFogMat = new Material(Shader.Find("Hidden/VolumetricFog2/DistantFog"));
            }
            distantFogMat.SetColor(ShaderParams.Color, activeProfile.distantFogColor);
            distantFogMat.SetVector(ShaderParams.DistantFogData, new Vector4(activeProfile.distantFogStartDistance, activeProfile.distantFogDistanceDensity, activeProfile.distantFogMaxHeight, activeProfile.distantFogHeightDensity));
            distantFogMat.SetVector(ShaderParams.LightDiffusionData, new Vector4(activeProfile.lightDiffusionPower, activeProfile.distantFogDiffusionIntensity * activeProfile.lightDiffusionIntensity, activeProfile.lightDiffusionNearDepthAtten, 0));
        }

        /// <summary>
        /// Issues a refresh of the depth pre-pass alpha clipping renderers list
        /// </summary>
        public static void FindAlphaClippingObjects () {
            DepthRenderPrePassFeature.DepthRenderPass.FindAlphaClippingRenderers();
        }

        /// <summary>
        /// Adds a specific renderer to the alpha clipping objects managed by the semitransparent depth prepass option
        /// </summary>
        public static void AddAlphaClippingObject (Renderer renderer) {
            DepthRenderPrePassFeature.DepthRenderPass.AddAlphaClippingObject(renderer);
        }

        /// <summary>
        /// Removes a specific renderer to the alpha clipping objects managed by the semitransparent depth prepass option
        /// </summary>
        public static void RemoveAlphaClippingObject (Renderer renderer) {
            DepthRenderPrePassFeature.DepthRenderPass.RemoveAlphaClippingObject(renderer);
        }

        readonly List<Material> fogMats = new List<Material>();

        public void RegisterFogMat (Material fogMat) {
            if (fogMat == null) return;
            if (!fogMats.Contains(fogMat)) {
                fogMats.Add(fogMat);
            }
        }

        public void UnregisterFogMat (Material fogMat) {
            if (fogMat == null) return;
            fogMats.Remove(fogMat);
        }


    }

}
