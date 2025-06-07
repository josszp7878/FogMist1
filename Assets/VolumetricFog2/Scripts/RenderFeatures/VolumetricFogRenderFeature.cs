//------------------------------------------------------------------------------------------------------------------
// Volumetric Fog & Mist 2
// Created by Kronnect
//------------------------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_2023_3_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
#endif
using UnityEngine.Rendering.Universal;

namespace VolumetricFogAndMist2 {

    public class VolumetricFogRenderFeature : ScriptableRendererFeature {

        public static class ShaderParams {
            public const string LightBufferName = "_LightBuffer";
            public static int LightBuffer = Shader.PropertyToID(LightBufferName);
            public static int LightBufferSize = Shader.PropertyToID("_VFRTSize");
            public static int MainTex = Shader.PropertyToID("_MainTex");
            public static int BlurRT = Shader.PropertyToID("_BlurTex");
            public static int BlurRT2 = Shader.PropertyToID("_BlurTex2");
            public static int MiscData = Shader.PropertyToID("_MiscData");
            public static int ForcedInvisible = Shader.PropertyToID("_ForcedInvisible");
            public static int DownsampledDepth = Shader.PropertyToID("_DownsampledDepth");
            public static int BlueNoiseTexture = Shader.PropertyToID("_BlueNoise");
            public static int BlurScale = Shader.PropertyToID("_BlurScale");
            public static int Downscaling = Shader.PropertyToID("_Downscaling");
            public static int ScatteringData = Shader.PropertyToID("_ScatteringData");
            public static int ScatteringTint = Shader.PropertyToID("_ScatteringTint");

            public static int BlurredTex = Shader.PropertyToID("_BlurredTex");

            public const string SKW_DITHER = "DITHER";
            public const string SKW_EDGE_PRESERVE = "EDGE_PRESERVE";
            public const string SKW_EDGE_PRESERVE_UPSCALING = "EDGE_PRESERVE_UPSCALING";
            public const string SKW_SCATTERING_HQ = "SCATTERING_HQ";
            public const string SKW_DEPTH_PEELING = "VF2_DEPTH_PEELING";
            public const string SKW_DEPTH_PREPASS = "VF2_DEPTH_PREPASS";
        }

        // 获取缩放后的尺寸，用于降采样渲染
        public static int GetScaledSize(int size, float factor) {
            // 根据缩放因子计算新尺寸
            size = (int)(size / factor);
            // 确保尺寸是偶数
            size /= 2;
            if (size < 1)
                size = 1;
            // 返回偶数尺寸
            return size * 2;
        }

        class VolumetricFogRenderPass : ScriptableRenderPass {

            const string m_ProfilerTag = "Volumetric Fog Buffer Rendering";

            static FilteringSettings filteringSettings = new FilteringSettings(RenderQueueRange.transparent, -1);
            static readonly List<ShaderTagId> shaderTagIdList = new List<ShaderTagId>();
            RTHandle m_LightBuffer;
            VolumetricFogRenderFeature settings;

            public VolumetricFogRenderPass () {
                shaderTagIdList.Clear();
                shaderTagIdList.Add(new ShaderTagId("UniversalForward"));
                shaderTagIdList.Add(new ShaderTagId("SRPDefaultUnlit"));
                RenderTargetIdentifier lightBuffer = new RenderTargetIdentifier(ShaderParams.LightBuffer, 0, CubemapFace.Unknown, -1);
                m_LightBuffer = RTHandles.Alloc(lightBuffer, name: ShaderParams.LightBufferName);
            }

            public void CleanUp () {
                RTHandles.Release(m_LightBuffer);
            }

            public void Setup (VolumetricFogRenderFeature settings, RenderPassEvent renderPassEvent) {
                this.settings = settings;
                this.renderPassEvent = renderPassEvent;
            }

#if UNITY_2023_3_OR_NEWER
            [Obsolete]
#endif
            public override void Configure (CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor) {
                RenderTextureDescriptor lightBufferDesc = cameraTextureDescriptor;
                VolumetricFogManager manager = VolumetricFogManager.GetManagerIfExists();
                if (manager != null) {
                    if (manager.downscaling > 1f) {
                        int size = GetScaledSize(cameraTextureDescriptor.width, manager.downscaling);
                        lightBufferDesc.width = size;
                        lightBufferDesc.height = size;
                    }
                    lightBufferDesc.colorFormat = manager.blurHDR ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32;
                    cmd.SetGlobalVector(ShaderParams.LightBufferSize, new Vector4(lightBufferDesc.width, lightBufferDesc.height, manager.downscaling > 1f ? 1f : 0, 0));
                }
                lightBufferDesc.depthBufferBits = 0;
                lightBufferDesc.msaaSamples = 1;
                lightBufferDesc.useMipMap = false;

                cmd.GetTemporaryRT(ShaderParams.LightBuffer, lightBufferDesc, FilterMode.Bilinear);
                ConfigureTarget(m_LightBuffer);
                ConfigureClear(ClearFlag.Color, new Color(0, 0, 0, 0));
                ConfigureInput(ScriptableRenderPassInput.Depth);
            }

#if UNITY_2023_3_OR_NEWER
            [Obsolete]
#endif
            public override void Execute (ScriptableRenderContext context, ref RenderingData renderingData) {
                // 获取VolumetricFogManager实例，它控制全局雾效设置
                VolumetricFogManager manager = VolumetricFogManager.GetManagerIfExists();

                // 创建命令缓冲区用于发送渲染命令
                CommandBuffer cmd = CommandBufferPool.Get(m_ProfilerTag);
                // 设置全局变量，使雾效可见（0=可见）
                cmd.SetGlobalInt(ShaderParams.ForcedInvisible, 0);
                context.ExecuteCommandBuffer(cmd);

                // 如果没有管理器或不需要特殊处理（无降采样、无模糊、无散射、无深度剥离），则提前退出
                if (manager == null || (manager.downscaling <= 1f && manager.blurPasses < 1 && manager.scattering <= 0 && !isUsingDepthPeeling)) {
                    CommandBufferPool.Release(cmd);
                    return;
                }

                cmd.Clear();

                // 处理场景中的所有体积雾对象
                foreach (VolumetricFog vg in VolumetricFog.volumetricFogs) {
                    if (vg != null) {
                        // 设置渲染层掩码，确保雾效被正确渲染
                        vg.meshRenderer.renderingLayerMask |= VolumetricFogManager.FOG_VOLUMES_RENDERING_LAYER;
                        // 如果使用深度剥离且在透明物体渲染前，则渲染远距雾效
                        if (isUsingDepthPeeling && renderPassEvent < RenderPassEvent.AfterRenderingTransparents) {
                            vg.RenderDistantFog(cmd);
                        }
                    }
                }

                // 深度剥离处理 - 用于正确处理透明物体与雾效的交互
                if (isUsingDepthPeeling) {
                    if (renderPassEvent < RenderPassEvent.AfterRenderingTransparents) {
                        // 在透明物体渲染前，启用深度剥离
                        cmd.DisableShaderKeyword(ShaderParams.SKW_DEPTH_PREPASS);
                        cmd.EnableShaderKeyword(ShaderParams.SKW_DEPTH_PEELING);
                    } else {
                        // 在透明物体渲染后，启用深度预处理
                        cmd.DisableShaderKeyword(ShaderParams.SKW_DEPTH_PEELING);
                        cmd.EnableShaderKeyword(ShaderParams.SKW_DEPTH_PREPASS);
                    }
                    context.ExecuteCommandBuffer(cmd);
                }

                // 设置渲染排序标志 - 使用透明物体的标准排序
                var sortFlags = SortingCriteria.CommonTransparent;
                // 创建绘制设置，指定要使用的shader标签
                var drawSettings = CreateDrawingSettings(shaderTagIdList, ref renderingData, sortFlags);
                // 设置过滤条件，指定要渲染的图层
                var filterSettings = filteringSettings;
                filterSettings.layerMask = settings.fogLayerMask;
                filterSettings.renderingLayerMask = VolumetricFogManager.FOG_VOLUMES_RENDERING_LAYER;

                // 执行渲染器绘制，使用设置的过滤条件
                context.DrawRenderers(renderingData.cullResults, ref drawSettings, ref filterSettings);

                // 释放命令缓冲区
                CommandBufferPool.Release(cmd);
            }

#if UNITY_2023_3_OR_NEWER

            class PassData {
                public RendererListHandle rendererListHandle;
                public UniversalCameraData cameraData;
                public RenderPassEvent renderPassEvent;
            }

            /// <summary>
            /// 在Unity URP的渲染图(RenderGraph)系统中记录渲染过程
            /// 
            /// 该函数在Unity 2023.3或更高版本中由渲染管线自动调用，用于替代旧版的Execute方法
            /// 作用是在RenderGraph API中设置和配置体积雾的渲染过程
            /// 
            /// 调用时机：
            /// 1. 每帧渲染时，由URP渲染管线框架自动调用
            /// 2. 在RenderGraph执行阶段，按照renderPassEvent指定的时间点调用
            /// 3. 在BeforeRenderingTransparents或AfterRenderingTransparents阶段执行，取决于设置
            /// 
            /// 该方法主要完成以下工作：
            /// - 创建和配置渲染通道(Pass)
            /// - 设置所需的纹理和缓冲区
            /// - 配置渲染器列表(RendererList)
            /// - 定义实际的渲染函数
            /// </summary>
            /// <param name="renderGraph">渲染图系统实例</param>
            /// <param name="frameData">当前帧的上下文数据容器</param>
            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData) {

                using (var builder = renderGraph.AddUnsafePass<PassData>(m_ProfilerTag, out var passData)) {
                    builder.AllowPassCulling(false);
                    // 获取资源数据
                    UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                    UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
                    UniversalLightData lightData = frameData.Get<UniversalLightData>();
                    UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                    passData.cameraData = cameraData;
                    passData.renderPassEvent = renderPassEvent;
                    // 获取深度纹理
                    builder.UseTexture(resourceData.activeDepthTexture, AccessFlags.Read);
                    ConfigureInput(ScriptableRenderPassInput.Depth);

                    SortingCriteria sortingCriteria = SortingCriteria.CommonTransparent;
                    // 创建绘制设置
                    var drawingSettings = CreateDrawingSettings(shaderTagIdList, renderingData, cameraData, lightData, sortingCriteria);
                    var filterSettings = filteringSettings;
                    filterSettings.layerMask = settings.fogLayerMask;
                    filterSettings.renderingLayerMask = VolumetricFogManager.FOG_VOLUMES_RENDERING_LAYER;
                    // 创建渲染器列表
                    RendererListParams listParams = new RendererListParams(renderingData.cullResults, drawingSettings, filterSettings);
                    passData.rendererListHandle = renderGraph.CreateRendererList(listParams);
                    // 使用渲染器列表
                    builder.UseRendererList(passData.rendererListHandle);

                    builder.SetRenderFunc(static (PassData passData, UnsafeGraphContext context) => {
                        // 获取命令缓冲区
                        CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                        // 获取渲染目标描述
                        RenderTextureDescriptor lightBufferDesc = passData.cameraData.cameraTargetDescriptor;
                        // 获取体积雾管理器
                        VolumetricFogManager manager = VolumetricFogManager.GetManagerIfExists();
                        if (manager != null) {
                            if (manager.downscaling > 1f) {
                                // 获取缩放后的尺寸
                                int size = GetScaledSize(lightBufferDesc.width, manager.downscaling);
                                lightBufferDesc.width = size;
                                lightBufferDesc.height = size;
                            }
                            // 设置颜色格式
                            lightBufferDesc.colorFormat = manager.blurHDR ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32;
                            // 设置全局变量
                            cmd.SetGlobalVector(ShaderParams.LightBufferSize, new Vector4(lightBufferDesc.width, lightBufferDesc.height, manager.downscaling > 1f ? 1f : 0, 0));
                        }
                        // 设置深度缓冲区
                        lightBufferDesc.depthBufferBits = 0;
                        // 设置多重采样
                        lightBufferDesc.msaaSamples = 1;
                        // 设置mipmap
                        lightBufferDesc.useMipMap = false;
                        // 获取临时渲染目标
                        cmd.GetTemporaryRT(ShaderParams.LightBuffer, lightBufferDesc, FilterMode.Bilinear);
                        // 设置渲染目标
                        RenderTargetIdentifier rti = new RenderTargetIdentifier(ShaderParams.LightBuffer, 0, CubemapFace.Unknown, -1);
                        cmd.SetRenderTarget(rti);
                        // 清除渲染目标
                        cmd.ClearRenderTarget(false, true, new Color(0, 0, 0, 0));
                        // 设置全局变量
                        cmd.SetGlobalInt(ShaderParams.ForcedInvisible, 0);
                        // 如果体积雾管理器不存在或不需要特殊处理（无降采样、无模糊、无散射、无深度剥离），则提前退出
                        if (manager == null || (manager.downscaling <= 1f && manager.blurPasses < 1 && manager.scattering <= 0 && !isUsingDepthPeeling)) {
                            return;
                        }
                        // 获取体积雾数量
                        int vgCount = VolumetricFog.volumetricFogs.Count;
                        for (int i = 0; i < vgCount; i++) {
                            // 获取体积雾
                            VolumetricFog vg = VolumetricFog.volumetricFogs[i];
                            // 如果体积雾存在
                            if (vg != null) {
                                vg.meshRenderer.renderingLayerMask |= VolumetricFogManager.FOG_VOLUMES_RENDERING_LAYER;
                                if (isUsingDepthPeeling && passData.renderPassEvent < RenderPassEvent.AfterRenderingTransparents) {
                                    vg.RenderDistantFog(cmd);
                                }
                            }
                        }
                        // 如果使用深度剥离
                        if (isUsingDepthPeeling) {
                            // 如果渲染事件在透明物体渲染前
                            if (passData.renderPassEvent < RenderPassEvent.AfterRenderingTransparents) {
                                cmd.DisableShaderKeyword(ShaderParams.SKW_DEPTH_PREPASS);
                                cmd.EnableShaderKeyword(ShaderParams.SKW_DEPTH_PEELING);
                            } else {
                                // 如果渲染事件在透明物体渲染后
                                cmd.DisableShaderKeyword(ShaderParams.SKW_DEPTH_PEELING);
                                cmd.EnableShaderKeyword(ShaderParams.SKW_DEPTH_PREPASS);
                            }
                        }
                        // 绘制渲染器列表
                        context.cmd.DrawRendererList(passData.rendererListHandle);
                    });
                }
            }
#endif

        }


        class BlurRenderPass : ScriptableRenderPass {

            enum Pass {
                BlurHorizontal = 0,
                BlurVertical = 1,
                BlurVerticalAndBlend = 2,
                UpscalingBlend = 3,
                DownscaleDepth = 4,
                BlurVerticalFinal = 5,
                Resample = 6,
                ResampleAndCombine = 7,
                ScatteringPrefilter = 8,
                ScatteringBlend = 9,
                Blend = 10
            }

            class PassData {
#if UNITY_2022_3_OR_NEWER
                public RTHandle source;
#else
                public RenderTargetIdentifier source;
#endif
#if UNITY_2023_3_OR_NEWER
                public TextureHandle colorTexture;
                public UniversalCameraData cameraData;
#endif
                public RenderPassEvent renderPassEvent;
            }


            const string m_ProfilerTag = "Volumetric Fog Render Feature";
            ScriptableRenderer renderer;
            static Material mat;
            static RenderTextureDescriptor sourceDesc;
            static VolumetricFogManager manager;
            static readonly PassData passData = new PassData();

            public void Setup (Shader shader, ScriptableRenderer renderer, RenderPassEvent renderPassEvent) {
                this.renderPassEvent = renderPassEvent;
                this.renderer = renderer;
                manager = VolumetricFogManager.GetManagerIfExists();
                if (mat == null) {
                    mat = CoreUtils.CreateEngineMaterial(shader);
                    Texture2D noiseTex = Resources.Load<Texture2D>("Textures/blueNoiseVF128");
                    mat.SetTexture(ShaderParams.BlueNoiseTexture, noiseTex);
                }
            }

#if UNITY_2023_3_OR_NEWER
            [Obsolete]
#endif
            public override void Configure (CommandBuffer cmd, RenderTextureDescriptor cameraTextureDescriptor) {
                sourceDesc = cameraTextureDescriptor;
                ConfigureInput(ScriptableRenderPassInput.Depth);
            }

#if UNITY_2023_3_OR_NEWER
            [Obsolete]
#endif
            public override void Execute (ScriptableRenderContext context, ref RenderingData renderingData) {

#if UNITY_2022_1_OR_NEWER
                passData.source = renderer.cameraColorTargetHandle;
#else
                passData.source = renderer.cameraColorTarget;
#endif
                passData.renderPassEvent = renderPassEvent;
                CommandBuffer cmd = CommandBufferPool.Get(m_ProfilerTag);
                ExecutePass(passData, cmd);
                context.ExecuteCommandBuffer(cmd);

                CommandBufferPool.Release(cmd);

            }

#if UNITY_2023_3_OR_NEWER

            /// <summary>
            /// 在Unity URP的渲染图(RenderGraph)系统中记录模糊处理的渲染过程
            /// 
            /// 该函数在Unity 2023.3或更高版本中由渲染管线自动调用，用于替代旧版的Execute方法
            /// 作用是在RenderGraph API中设置和配置体积雾模糊效果的渲染过程
            /// 
            /// 调用时机：
            /// 1. 每帧渲染时，在体积雾主要渲染后由URP渲染管线框架自动调用
            /// 2. 在RenderGraph执行阶段，按照renderPassEvent指定的时间点调用
            /// 
            /// 该方法主要完成以下工作：
            /// - 创建和配置模糊处理渲染通道
            /// - 设置颜色和深度纹理的读写访问
            /// - 设置执行模糊和合成效果的实际渲染函数
            /// </summary>
            /// <param name="renderGraph">渲染图系统实例</param>
            /// <param name="frameData">当前帧的上下文数据容器</param>
            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData) {

                using (var builder = renderGraph.AddUnsafePass<PassData>(m_ProfilerTag, out var passData)) {
                    builder.AllowPassCulling(false);

                    UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                    UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
                    UniversalLightData lightData = frameData.Get<UniversalLightData>();
                    UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                    passData.cameraData = cameraData;
                    passData.colorTexture = resourceData.activeColorTexture;
                    builder.UseTexture(resourceData.activeColorTexture, AccessFlags.ReadWrite);
                    builder.UseTexture(resourceData.activeDepthTexture, AccessFlags.Read);

                    ConfigureInput(ScriptableRenderPassInput.Depth);
                    passData.renderPassEvent = renderPassEvent;

                    sourceDesc = cameraData.cameraTargetDescriptor;

                    builder.SetRenderFunc(static (PassData passData, UnsafeGraphContext context) => {
                        CommandBuffer cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                        passData.source = passData.colorTexture;
                        ExecutePass(passData, cmd);
                    });
                }
            }
#endif

            static void ExecutePass (PassData passData, CommandBuffer cmd) {

                if (manager == null || (manager.downscaling <= 1f && manager.blurPasses < 1 && manager.scattering <= 0 && !isUsingDepthPeeling)) {
                    Cleanup();
                    return;
                }

                mat.SetVector(ShaderParams.MiscData, new Vector4(manager.ditherStrength * 0.1f, 0, manager.blurEdgeDepthThreshold, manager.downscalingEdgeDepthThreshold * 0.001f));
                if (manager.ditherStrength > 0) {
                    mat.EnableKeyword(ShaderParams.SKW_DITHER);
                } else {
                    mat.DisableKeyword(ShaderParams.SKW_DITHER);
                }
                mat.DisableKeyword(ShaderParams.SKW_EDGE_PRESERVE);
                mat.DisableKeyword(ShaderParams.SKW_EDGE_PRESERVE_UPSCALING);
                if (manager.blurPasses > 0 && manager.blurEdgePreserve) {
                    mat.EnableKeyword(manager.downscaling > 1f ? ShaderParams.SKW_EDGE_PRESERVE_UPSCALING : ShaderParams.SKW_EDGE_PRESERVE);
                }

#if UNITY_2022_3_OR_NEWER
                RTHandle source = passData.source;
#else
                RenderTargetIdentifier source = passData.source;
#endif

                cmd.SetGlobalInt(ShaderParams.ForcedInvisible, 1);

                RenderTextureDescriptor rtBlurDesc = sourceDesc;
                rtBlurDesc.width = GetScaledSize(sourceDesc.width, manager.downscaling);
                rtBlurDesc.height = GetScaledSize(sourceDesc.height, manager.downscaling);
                rtBlurDesc.useMipMap = false;
                rtBlurDesc.colorFormat = manager.blurHDR ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32;
                rtBlurDesc.msaaSamples = 1;
                rtBlurDesc.depthBufferBits = 0;

                bool usingDownscaling = manager.downscaling > 1f;
                bool isFrontDepthPeeling = isUsingDepthPeeling && passData.renderPassEvent >= RenderPassEvent.AfterRenderingTransparents;
                if (usingDownscaling && !isFrontDepthPeeling) {
                    RenderTextureDescriptor rtDownscaledDepth = rtBlurDesc;
                    rtDownscaledDepth.colorFormat = RenderTextureFormat.RFloat;
                    cmd.GetTemporaryRT(ShaderParams.DownsampledDepth, rtDownscaledDepth, FilterMode.Bilinear);
                    FullScreenBlit(cmd, source, ShaderParams.DownsampledDepth, mat, (int)Pass.DownscaleDepth);
                }

                if (isUsingDepthPeeling) {
                    mat.EnableKeyword(ShaderParams.SKW_DEPTH_PEELING);
                } else {
                    mat.DisableKeyword(ShaderParams.SKW_DEPTH_PEELING);
                }

                if (manager.blurPasses < 1) {
                    // no blur but downscaling
                    FullScreenBlit(cmd, ShaderParams.LightBuffer, source, mat, usingDownscaling ? (int)Pass.UpscalingBlend : (int)Pass.Blend);
                } else {
                    // blur (with or without downscaling)
                    rtBlurDesc.width = GetScaledSize(sourceDesc.width, manager.blurDownscaling);
                    rtBlurDesc.height = GetScaledSize(sourceDesc.height, manager.blurDownscaling);
                    cmd.GetTemporaryRT(ShaderParams.BlurRT, rtBlurDesc, FilterMode.Bilinear);
                    cmd.GetTemporaryRT(ShaderParams.BlurRT2, rtBlurDesc, FilterMode.Bilinear);
                    cmd.SetGlobalFloat(ShaderParams.BlurScale, manager.blurSpread * manager.blurDownscaling);
                    FullScreenBlit(cmd, ShaderParams.LightBuffer, ShaderParams.BlurRT, mat, (int)Pass.BlurHorizontal);
                    cmd.SetGlobalFloat(ShaderParams.BlurScale, manager.blurSpread);
                    for (int k = 0; k < manager.blurPasses - 1; k++) {
                        FullScreenBlit(cmd, ShaderParams.BlurRT, ShaderParams.BlurRT2, mat, (int)Pass.BlurVertical);
                        FullScreenBlit(cmd, ShaderParams.BlurRT2, ShaderParams.BlurRT, mat, (int)Pass.BlurHorizontal);
                    }
                    if (usingDownscaling) {
                        FullScreenBlit(cmd, ShaderParams.BlurRT, ShaderParams.BlurRT2, mat, (int)Pass.BlurVerticalFinal);
                        FullScreenBlit(cmd, ShaderParams.BlurRT2, source, mat, (int)Pass.UpscalingBlend);
                    } else {
                        FullScreenBlit(cmd, ShaderParams.BlurRT, source, mat, (int)Pass.BlurVerticalAndBlend);
                    }

                    cmd.ReleaseTemporaryRT(ShaderParams.BlurRT2);
                    cmd.ReleaseTemporaryRT(ShaderParams.BlurRT);
                }

                if (manager.scattering > 0 && (!isUsingDepthPeeling || isFrontDepthPeeling)) {
                    ComputeScattering(cmd, source, mat);
                }

                cmd.ReleaseTemporaryRT(ShaderParams.LightBuffer);
                if (usingDownscaling) {
                    cmd.ReleaseTemporaryRT(ShaderParams.DownsampledDepth);
                }
            }


            struct ScatteringMipData {
                public int rtDown, rtUp, width, height;
            }
            static ScatteringMipData[] rt;
            const int PYRAMID_MAX_LEVELS = 5;


#if UNITY_2022_1_OR_NEWER
            static void ComputeScattering(CommandBuffer cmd, RTHandle source, Material mat) {
#else
            static void ComputeScattering (CommandBuffer cmd, RenderTargetIdentifier source, Material mat) {
#endif

                mat.SetVector(ShaderParams.ScatteringData, new Vector4(manager.scatteringThreshold, manager.scatteringIntensity, 1f - manager.scatteringAbsorption, manager.scattering));
                mat.SetColor(ShaderParams.ScatteringTint, manager.scatteringTint);
                float downscaling = manager.downscaling;

                // Initialize buffers descriptors
                if (rt == null || rt.Length != PYRAMID_MAX_LEVELS + 1) {
                    rt = new ScatteringMipData[PYRAMID_MAX_LEVELS + 1];
                    for (int k = 0; k < rt.Length; k++) {
                        rt[k].rtDown = Shader.PropertyToID("_VFogDownMip" + k);
                        rt[k].rtUp = Shader.PropertyToID("_VFogUpMip" + k);
                    }
                }

                int width = GetScaledSize(sourceDesc.width, downscaling);
                int height = GetScaledSize(sourceDesc.height, downscaling);
                if (downscaling > 1 && manager.scatteringHighQuality) {
                    mat.EnableKeyword(ShaderParams.SKW_SCATTERING_HQ);
                } else {
                    mat.DisableKeyword(ShaderParams.SKW_SCATTERING_HQ);
                }
                if (!manager.scatteringHighQuality) {
                    width /= 2;
                    height /= 2;
                }
                int mipCount = manager.scatteringHighQuality ? 5 : 4;
                RenderTextureDescriptor scatterDesc = sourceDesc;
                scatterDesc.colorFormat = RenderTextureFormat.ARGBHalf;
                scatterDesc.msaaSamples = 1;
                scatterDesc.depthBufferBits = 0;
                for (int k = 0; k <= mipCount; k++) {
                    if (width < 2) width = 2;
                    if (height < 2) height = 2;
                    scatterDesc.width = rt[k].width = width;
                    scatterDesc.height = rt[k].height = height;
                    cmd.GetTemporaryRT(rt[k].rtDown, scatterDesc, FilterMode.Bilinear);
                    cmd.GetTemporaryRT(rt[k].rtUp, scatterDesc, FilterMode.Bilinear);
                    width /= 2;
                    height /= 2;
                }

                RenderTargetIdentifier sourceMip = rt[0].rtDown;

                FullScreenBlit(cmd, source, sourceMip, mat, (int)Pass.ScatteringPrefilter);

                // Blitting down...
                cmd.SetGlobalFloat(ShaderParams.BlurScale, 1f);
                for (int k = 1; k <= mipCount; k++) {
                    FullScreenBlit(cmd, sourceMip, rt[k].rtDown, mat, (int)Pass.Resample);
                    sourceMip = rt[k].rtDown;
                }

                // Blitting up...
                cmd.SetGlobalFloat(ShaderParams.BlurScale, 1.5f);
                for (int k = mipCount; k > 0; k--) {
                    cmd.SetGlobalTexture(ShaderParams.BlurredTex, rt[k - 1].rtDown);
                    FullScreenBlit(cmd, sourceMip, rt[k - 1].rtUp, mat, (int)Pass.ResampleAndCombine);
                    sourceMip = rt[k - 1].rtUp;
                }

                FullScreenBlit(cmd, sourceMip, source, mat, (int)Pass.ScatteringBlend);
            }

            static void FullScreenBlit (CommandBuffer cmd, RenderTargetIdentifier source, RenderTargetIdentifier destination, Material material, int passIndex) {
                destination = new RenderTargetIdentifier(destination, 0, CubemapFace.Unknown, -1);
                cmd.SetRenderTarget(destination);
                cmd.SetGlobalTexture(ShaderParams.MainTex, source);
                cmd.DrawMesh(Tools.fullscreenMesh, Matrix4x4.identity, material, 0, passIndex);
            }

            static public void Cleanup () {
                Shader.SetGlobalInt(ShaderParams.ForcedInvisible, 0);
            }

        }

        [SerializeField, HideInInspector]
        Shader blurShader;
        VolumetricFogRenderPass fogRenderPass, fogRenderBackTranspPass;
        BlurRenderPass blurRenderPass, blurRenderBackTranspPass;
        public static bool installed;
        public static bool isRenderingBeforeTransparents;
        public static bool isUsingDepthPeeling;

        public RenderPassEvent renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;

        [Tooltip("Specify which fog volumes will be rendered by this feature.")]
        public LayerMask fogLayerMask = -1;

        [Tooltip("Specify which cameras can execute this render feature. If you have several cameras in your scene, make sure only the correct cameras use this feature in order to optimize performance.")]
        public LayerMask cameraLayerMask = -1;

        [Tooltip("Ignores reflection probes from executing this render feature")]
        public bool ignoreReflectionProbes = true;

        void OnDisable () {
            installed = false;
            isRenderingBeforeTransparents = false;
            isUsingDepthPeeling = false;
            BlurRenderPass.Cleanup();
        }

        private void OnDestroy () {
            if (fogRenderPass != null) {
                fogRenderPass.CleanUp();
            }
            if (fogRenderBackTranspPass != null) {
                fogRenderBackTranspPass.CleanUp();
            }
        }

        public override void Create () {
            name = "Volumetric Fog 2";
            fogRenderPass = new VolumetricFogRenderPass();
            blurRenderPass = new BlurRenderPass();
            fogRenderBackTranspPass = new VolumetricFogRenderPass();
            blurRenderBackTranspPass = new BlurRenderPass();
            blurShader = Shader.Find("Hidden/VolumetricFog2/Blur");
            if (blurShader == null) {
                Debug.LogWarning("Could not load Volumetric Fog composition shader.");
            }
        }

        // This method is called when setting up the renderer once per-camera.
        public override void AddRenderPasses (ScriptableRenderer renderer, ref RenderingData renderingData) {

            installed = true;

            if (VolumetricFog.volumetricFogs.Count == 0) return;

            VolumetricFogManager manager = VolumetricFogManager.GetManagerIfExists();
            if (manager == null) {
                Shader.SetGlobalInt(ShaderParams.ForcedInvisible, 0);
                return;
            }

            isUsingDepthPeeling = manager.includeTransparent != 0 && manager.depthPeeling;
            if (manager.downscaling <= 1f && manager.blurPasses < 1 && manager.scattering <= 0 && !isUsingDepthPeeling) {
                Shader.SetGlobalInt(ShaderParams.ForcedInvisible, 0);
                return;
            }

            Camera cam = renderingData.cameraData.camera;

            CameraType camType = cam.cameraType;
            if (camType == CameraType.Preview) return;
            if (ignoreReflectionProbes && camType == CameraType.Reflection) return;

            if ((fogLayerMask & cam.cullingMask) == 0) return;

            if ((cameraLayerMask & (1 << cam.gameObject.layer)) == 0) return;

            if (cam.targetTexture != null && cam.targetTexture.format == RenderTextureFormat.Depth) return; // ignore occlusion cams!

            RenderPassEvent injectionPoint = renderPassEvent;

            if (isUsingDepthPeeling) {
                fogRenderBackTranspPass.Setup(this, RenderPassEvent.AfterRenderingSkybox);
                renderer.EnqueuePass(fogRenderBackTranspPass);

                blurRenderBackTranspPass.Setup(blurShader, renderer, RenderPassEvent.AfterRenderingSkybox);
                renderer.EnqueuePass(blurRenderBackTranspPass);

                injectionPoint = (RenderPassEvent)Mathf.Max((int)injectionPoint, (int)RenderPassEvent.AfterRenderingTransparents);
            }

            isRenderingBeforeTransparents = injectionPoint < RenderPassEvent.AfterRenderingTransparents;

            fogRenderPass.Setup(this, injectionPoint);
            renderer.EnqueuePass(fogRenderPass);

            blurRenderPass.Setup(blurShader, renderer, injectionPoint);
            renderer.EnqueuePass(blurRenderPass);
        }
    }
}
