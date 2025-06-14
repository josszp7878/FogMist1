using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using System.Collections.Generic;
using System;
#if UNITY_2023_3_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
#endif

namespace VolumetricFogAndMist2 {

    public class DepthRenderPrePassFeature : ScriptableRendererFeature {

        public class DepthRenderPass : ScriptableRenderPass {

            const string m_ProfilerTag = "CustomDepthPrePass";
            const string m_DepthOnlyShader = "Hidden/VolumetricFog2/DepthOnly";

            public static readonly List<Renderer> cutOutRenderers = new List<Renderer>();
            public static int transparentLayerMask;
            public static int alphaCutoutLayerMask;

            static FilteringSettings filterSettings;
            static int currentCutoutLayerMask;
            static readonly List<ShaderTagId> shaderTagIdList = new List<ShaderTagId>();

            RTHandle m_Depth;
            static Material depthOnlyMaterial, depthOnlyMaterialCutOff;
            static Material[] depthOverrideMaterials;
            static Shader fogShader;
            static DepthRenderPrePassFeature options;

            public DepthRenderPass () {
                RenderTargetIdentifier rti = new RenderTargetIdentifier(ShaderParams.CustomDepthTexture, 0, CubemapFace.Unknown, -1);
                m_Depth = RTHandles.Alloc(rti, name: ShaderParams.CustomDepthTextureName);
                shaderTagIdList.Clear();
                shaderTagIdList.Add(new ShaderTagId("SRPDefaultUnlit"));
                shaderTagIdList.Add(new ShaderTagId("UniversalForward"));
                shaderTagIdList.Add(new ShaderTagId("LightweightForward"));
                filterSettings = new FilteringSettings(RenderQueueRange.transparent, 0);
                SetupKeywords();
                currentCutoutLayerMask = 0;
                fogShader = Shader.Find("VolumetricFog2/VolumetricFog2DURP");
                FindAlphaClippingRenderers();
            }

            public void Setup (DepthRenderPrePassFeature options) {
                DepthRenderPass.options = options;
            }

            void SetupKeywords () {
                if (transparentLayerMask != 0 || alphaCutoutLayerMask != 0) {
                    Shader.EnableKeyword(ShaderParams.SKW_DEPTH_PREPASS);
                } else {
                    Shader.DisableKeyword(ShaderParams.SKW_DEPTH_PREPASS);
                }
            }

            public static void SetupLayerMasks (int transparentLayerMask, int alphaCutoutLayerMask) {
                DepthRenderPass.transparentLayerMask = transparentLayerMask;
                DepthRenderPass.alphaCutoutLayerMask = alphaCutoutLayerMask;
                if (alphaCutoutLayerMask != 0) {
                    FindAlphaClippingRenderers();
                }
            }

            public static void FindAlphaClippingRenderers () {
                cutOutRenderers.Clear();
                if (alphaCutoutLayerMask == 0) return;
                Renderer[] rr = Misc.FindObjectsOfType<Renderer>();
                for (int r = 0; r < rr.Length; r++) {
                    if (((1 << rr[r].gameObject.layer) & alphaCutoutLayerMask) != 0) {
                        cutOutRenderers.Add(rr[r]);
                    }
                }
            }

            public static void AddAlphaClippingObject (Renderer renderer) {
                if (!cutOutRenderers.Contains(renderer)) cutOutRenderers.Add(renderer);
            }

            public static void RemoveAlphaClippingObject (Renderer renderer) {
                if (cutOutRenderers.Contains(renderer)) cutOutRenderers.Remove(renderer);
            }


#if UNITY_2023_3_OR_NEWER
            [Obsolete]
#endif
            public override void Configure (CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor) {
                if (transparentLayerMask != filterSettings.layerMask || alphaCutoutLayerMask != currentCutoutLayerMask) {
                    filterSettings = new FilteringSettings(RenderQueueRange.transparent, transparentLayerMask);
                    currentCutoutLayerMask = alphaCutoutLayerMask;
                    SetupKeywords();
                }
                RenderTextureDescriptor depthDesc = cameraTextureDescriptor;
                VolumetricFogManager manager = VolumetricFogManager.GetManagerIfExists();
                if (manager != null) {
                    depthDesc.width = VolumetricFogRenderFeature.GetScaledSize(depthDesc.width, manager.downscaling);
                    depthDesc.height = VolumetricFogRenderFeature.GetScaledSize(depthDesc.height, manager.downscaling);
                }
                depthDesc.colorFormat = RenderTextureFormat.Depth;
                depthDesc.depthBufferBits = 24;
                depthDesc.msaaSamples = 1;

                cmd.GetTemporaryRT(ShaderParams.CustomDepthTexture, depthDesc, FilterMode.Point);
                cmd.SetGlobalTexture(ShaderParams.CustomDepthTexture, m_Depth);
                ConfigureTarget(m_Depth);
                ConfigureClear(ClearFlag.All, Color.black);
            }

#if UNITY_2023_3_OR_NEWER
            [Obsolete]
#endif
            public override void Execute (ScriptableRenderContext context, ref RenderingData renderingData) {
                if (transparentLayerMask == 0 && alphaCutoutLayerMask == 0) return;
                CommandBuffer cmd = CommandBufferPool.Get(m_ProfilerTag);
                context.ExecuteCommandBuffer(cmd);
                cmd.Clear();

                VolumetricFogManager manager = VolumetricFogManager.GetManagerIfExists();

                if (alphaCutoutLayerMask != 0) {
                    if (manager != null) {
                        if (depthOnlyMaterialCutOff == null) {
                            Shader depthOnlyCutOff = Shader.Find(m_DepthOnlyShader);
                            depthOnlyMaterialCutOff = new Material(depthOnlyCutOff);
                        }
                        int renderersCount = cutOutRenderers.Count;
                        if (depthOverrideMaterials == null || depthOverrideMaterials.Length < renderersCount) {
                            depthOverrideMaterials = new Material[renderersCount];
                        }
                        bool listNeedsPacking = false;
                        for (int k = 0; k < renderersCount; k++) {
                            Renderer renderer = cutOutRenderers[k];
                            if (renderer == null) {
                                listNeedsPacking = true;
                            } else if (renderer.isVisible) {
                                Material mat = renderer.sharedMaterial;
                                if (mat != null && mat.shader != fogShader) {
                                    if (depthOverrideMaterials[k] == null) {
                                        depthOverrideMaterials[k] = Instantiate(depthOnlyMaterialCutOff);
                                        depthOverrideMaterials[k].EnableKeyword(ShaderParams.SKW_CUSTOM_DEPTH_ALPHA_TEST);
                                    }
                                    Material overrideMaterial = depthOverrideMaterials[k];
                                    overrideMaterial.SetFloat(ShaderParams.CustomDepthAlphaCutoff, manager.alphaCutOff);
                                    if (mat.HasProperty(ShaderParams.CustomDepthBaseMap)) {
                                        overrideMaterial.SetTexture(ShaderParams.CustomDepthBaseMap, mat.GetTexture(ShaderParams.CustomDepthBaseMap));
                                    } else if (mat.HasProperty(ShaderParams.MainTex)) {
                                        overrideMaterial.SetTexture(ShaderParams.CustomDepthBaseMap, mat.GetTexture(ShaderParams.MainTex));
                                    }
                                    if (mat.HasProperty(ShaderParams.CullMode)) {
                                        overrideMaterial.SetInt(ShaderParams.CullMode, mat.GetInt(ShaderParams.CullMode));
                                    } else {
                                        overrideMaterial.SetInt(ShaderParams.CullMode, (int)manager.semiTransparentCullMode);
                                    }
                                    cmd.DrawRenderer(renderer, overrideMaterial);
                                }
                            }
                        }
                        if (listNeedsPacking) {
                            cutOutRenderers.RemoveAll(item => item == null);
                        }
                    }
                }

                if (transparentLayerMask != 0) {

                    foreach (VolumetricFog vg in VolumetricFog.volumetricFogs) {
                        if (vg != null) {
                            vg.meshRenderer.renderingLayerMask = VolumetricFogManager.FOG_VOLUMES_RENDERING_LAYER;
                        }
                    }
                    filterSettings.renderingLayerMask = ~VolumetricFogManager.FOG_VOLUMES_RENDERING_LAYER;

                    SortingCriteria sortingCriteria = SortingCriteria.CommonTransparent;
                    var drawSettings = CreateDrawingSettings(shaderTagIdList, ref renderingData, sortingCriteria);
                    drawSettings.perObjectData = PerObjectData.None;
                    if (options.useOptimizedDepthOnlyShader) {
                        if (depthOnlyMaterial == null) {
                            Shader depthOnly = Shader.Find(m_DepthOnlyShader);
                            depthOnlyMaterial = new Material(depthOnly);
                        }
                        if (manager != null) {
                            depthOnlyMaterial.SetInt(ShaderParams.CullMode, (int)manager.transparentCullMode);
                        }
                        drawSettings.overrideMaterial = depthOnlyMaterial;
                    }
                    context.DrawRenderers(renderingData.cullResults, ref drawSettings, ref filterSettings);
                }

                context.ExecuteCommandBuffer(cmd);

                CommandBufferPool.Release(cmd);
            }

#if UNITY_2023_3_OR_NEWER

            class PassData {
                public RendererListHandle rendererListHandle;
                public UniversalCameraData cameraData;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData) {

                if (transparentLayerMask == 0 && alphaCutoutLayerMask == 0) {
                    SetupKeywords();
                    return;
                }

                using (var builder = renderGraph.AddUnsafePass<PassData>(m_ProfilerTag, out var passData)) {

                    builder.AllowPassCulling(false);

                    UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                    UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
                    UniversalLightData lightData = frameData.Get<UniversalLightData>();
                    UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                    passData.cameraData = cameraData;

                    SortingCriteria sortingCriteria = SortingCriteria.CommonTransparent;
                    var drawingSettings = CreateDrawingSettings(shaderTagIdList, renderingData, cameraData, lightData, sortingCriteria);
                    drawingSettings.perObjectData = PerObjectData.None;
                    if (options.useOptimizedDepthOnlyShader) {
                        if (depthOnlyMaterial == null) {
                            Shader depthOnly = Shader.Find(m_DepthOnlyShader);
                            depthOnlyMaterial = new Material(depthOnly);
                        }
                        VolumetricFogManager manager = VolumetricFogManager.GetManagerIfExists();
                        if (manager != null) {
                            depthOnlyMaterial.SetInt(ShaderParams.CullMode, (int)manager.transparentCullMode);
                        }
                        drawingSettings.overrideMaterial = depthOnlyMaterial;
                    }
                    
                    if (transparentLayerMask != filterSettings.layerMask || alphaCutoutLayerMask != currentCutoutLayerMask) {
                        filterSettings = new FilteringSettings(RenderQueueRange.transparent, transparentLayerMask);
                        currentCutoutLayerMask = alphaCutoutLayerMask;
                        SetupKeywords();
                    }
                    filterSettings.renderingLayerMask = ~VolumetricFogManager.FOG_VOLUMES_RENDERING_LAYER;

                    RendererListParams listParams = new RendererListParams(renderingData.cullResults, drawingSettings, filterSettings);
                    passData.rendererListHandle = renderGraph.CreateRendererList(listParams);
                    builder.UseRendererList(passData.rendererListHandle);

                    builder.SetRenderFunc((PassData passData, UnsafeGraphContext context) => {

                        CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);

                        RenderTextureDescriptor depthDesc = passData.cameraData.cameraTargetDescriptor;
                        // 获取管理器
                        VolumetricFogManager manager = VolumetricFogManager.GetManagerIfExists();
                        if (manager != null) {
                            // 缩放
                            depthDesc.width = VolumetricFogRenderFeature.GetScaledSize(depthDesc.width, manager.downscaling);
                            depthDesc.height = VolumetricFogRenderFeature.GetScaledSize(depthDesc.height, manager.downscaling);
                        }
                        // 深度格式
                        depthDesc.colorFormat = RenderTextureFormat.Depth;
                        // 深度缓冲区位数
                        depthDesc.depthBufferBits = 24;
                        // 多重采样样本数
                        depthDesc.msaaSamples = 1;

                        cmd.GetTemporaryRT(ShaderParams.CustomDepthTexture, depthDesc, FilterMode.Point);
                        // 渲染目标标识符
                        RenderTargetIdentifier rti = new RenderTargetIdentifier(ShaderParams.CustomDepthTexture, 0, CubemapFace.Unknown, -1);
                        cmd.SetRenderTarget(rti);
                        cmd.ClearRenderTarget(true, true, Color.black);

                        // 如果alpha剪裁层掩码不为0
                        if (alphaCutoutLayerMask != 0) {
                            if (manager != null) {
                                if (depthOnlyMaterialCutOff == null) {
                                    Shader depthOnlyCutOff = Shader.Find(m_DepthOnlyShader);
                                    depthOnlyMaterialCutOff = new Material(depthOnlyCutOff);
                                }
                                // 获取渲染器数量
                                int renderersCount = cutOutRenderers.Count;
                                // 如果深度覆盖材料为空或者深度覆盖材质的长度小于渲染器数量
                                if (depthOverrideMaterials == null || depthOverrideMaterials.Length < renderersCount) {
                                    // 创建深度覆盖材质
                                    depthOverrideMaterials = new Material[renderersCount];
                                }
                                // 遍历渲染器
                                for (int k = 0; k < renderersCount; k++) {
                                    // 获取渲染器
                                    Renderer renderer = cutOutRenderers[k];
                                    // 如果渲染器不为空并且渲染器可见
                                    if (renderer != null && renderer.isVisible) {
                                        // 获取渲染器材质
                                        Material mat = renderer.sharedMaterial;
                                        // 如果材质不为空并且材质的着色器不是雾化着色器
                                        if (mat != null && mat.shader != fogShader) {
                                            // 如果深度覆盖材质为空
                                            if (depthOverrideMaterials[k] == null) {
                                                // 创建深度覆盖材质
                                                depthOverrideMaterials[k] = Instantiate(depthOnlyMaterialCutOff);
                                                // 启用深度覆盖材质的关键字
                                                depthOverrideMaterials[k].EnableKeyword(ShaderParams.SKW_CUSTOM_DEPTH_ALPHA_TEST);
                                            }
                                            // 获取深度覆盖材质
                                            Material overrideMaterial = depthOverrideMaterials[k];
                                            // 设置深度覆盖材质的alpha剪裁偏移
                                            overrideMaterial.SetFloat(ShaderParams.CustomDepthAlphaCutoff, manager.alphaCutOff);
                                            // 如果材质有深度基础地图属性
                                            if (mat.HasProperty(ShaderParams.CustomDepthBaseMap)) {
                                                // 设置深度覆盖材质的深度基础地图
                                                overrideMaterial.SetTexture(ShaderParams.MainTex, mat.GetTexture(ShaderParams.CustomDepthBaseMap));
                                            } else if (mat.HasProperty(ShaderParams.MainTex)) {
                                                // 设置深度覆盖材质的主纹理
                                                overrideMaterial.SetTexture(ShaderParams.MainTex, mat.GetTexture(ShaderParams.MainTex));
                                            }
                                            // 如果材质有裁剪模式属性
                                            if (mat.HasProperty(ShaderParams.CullMode)) {
                                                // 设置深度覆盖材质的裁剪模式
                                                overrideMaterial.SetInt(ShaderParams.CullMode, mat.GetInt(ShaderParams.CullMode));
                                            } else {
                                                overrideMaterial.SetInt(ShaderParams.CullMode, (int)manager.semiTransparentCullMode);
                                            }
                                            // 绘制渲染器
                                            cmd.DrawRenderer(renderer, overrideMaterial);
                                        }
                                    }
                                }
                            }
                        }

                        if (transparentLayerMask != 0) {

                            foreach (VolumetricFog vg in VolumetricFog.volumetricFogs) {
                                if (vg != null) {
                                    vg.meshRenderer.renderingLayerMask = VolumetricFogManager.FOG_VOLUMES_RENDERING_LAYER;
                                }
                            }
                            context.cmd.DrawRendererList(passData.rendererListHandle);
                        }

                    });
                }
            }

#endif


            // Cleanup any allocated resources that were created during the execution of this render pass.
            public override void FrameCleanup (CommandBuffer cmd) {
                if (cmd == null) return;
                cmd.ReleaseTemporaryRT(ShaderParams.CustomDepthTexture);
            }

            public void CleanUp () {
                Shader.DisableKeyword(ShaderParams.SKW_DEPTH_PREPASS);
                RTHandles.Release(m_Depth);
            }
        }

        DepthRenderPass m_ScriptablePass;
        public static bool installed;

        [Tooltip("Specify which cameras can execute this render feature. If you have several cameras in your scene, make sure only the correct cameras use this feature in order to optimize performance.")]
        public LayerMask cameraLayerMask = -1;

        [Tooltip("Ignores reflection probes from executing this render feature")]
        public bool ignoreReflectionProbes = true;

        [Tooltip("Uses an optimized shader to compute depth for the objects included in the transparent pass. If this option is disabled, the original shader of the objects will be used instead (this can be useful if the original shaders include vertex transformations).")]
        public bool useOptimizedDepthOnlyShader = true;

        public override void Create () {
            m_ScriptablePass = new DepthRenderPass() {
                // Configures where the render pass should be injected.
                renderPassEvent = RenderPassEvent.AfterRenderingOpaques
            };
        }

        void OnDestroy () {
            installed = false;
            if (m_ScriptablePass != null) {
                m_ScriptablePass.CleanUp();
            }
        }

        // Here you can inject one or multiple render passes in the renderer.
        // This method is called when setting up the renderer once per-camera.
        public override void AddRenderPasses (ScriptableRenderer renderer, ref RenderingData renderingData) {
            if (DepthRenderPass.transparentLayerMask == 0 && DepthRenderPass.alphaCutoutLayerMask == 0) return;
            Camera cam = renderingData.cameraData.camera;
            if ((cameraLayerMask & (1 << cam.gameObject.layer)) == 0) return;
            if (ignoreReflectionProbes && cam.cameraType == CameraType.Reflection) return;
            if (cam.targetTexture != null && cam.targetTexture.format == RenderTextureFormat.Depth) return; // ignore occlusion cams!

            installed = true;
            m_ScriptablePass.Setup(this);
            renderer.EnqueuePass(m_ScriptablePass);
        }

    }



}