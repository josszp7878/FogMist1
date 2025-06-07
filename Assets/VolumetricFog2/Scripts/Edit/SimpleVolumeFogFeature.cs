using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using System.Collections.Generic;
using System;
#if UNITY_2023_3_OR_NEWER
using UnityEngine.Rendering.RenderGraphModule;
#endif

public class SimpleVolumeFogFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class Settings
    {
        public ComputeShader fogCompute;
        public int stepCount = 32;
        public int maxFogVolumes = 32;
    }
    public Settings settings = new Settings();

    SimpleVolumeFogPass pass;

    public override void Create()
    {
        pass = new SimpleVolumeFogPass(settings);
    }

#if UNITY_2023_3_OR_NEWER
    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        pass.RecordRenderGraph(renderGraph, frameData);
    }
#endif

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
#if !UNITY_2023_3_OR_NEWER
        renderer.EnqueuePass(pass);
#endif
    }

    struct FogVolumeData
    {
        public Vector3 position;
        public Vector3 size;
        public float density;
        public Color color;
        public int shape;
        public int falloffMode;
        public float falloffDistance;
        public float noiseIntensity;
        public float noiseScale;
        public int blendMode;
        public Quaternion rotation;
        public Matrix4x4 invRotation;
    }

    struct MainLightData
    {
        public Vector3 direction;
        public Color color;
        public float intensity;
        public Matrix4x4 shadowMatrix;
        // 可选：阴影贴图索引等
    }

    struct PointLightData
    {
        public Vector3 position;
        public Color color;
        public float intensity;
        public float range;
    }

    struct SpotLightData
    {
        public Vector3 position;
        public Vector3 direction;
        public Color color;
        public float intensity;
        public float range;
        public float spotAngle;
    }

    class SimpleVolumeFogPass : ScriptableRenderPass
    {
        Settings settings;
        RenderTexture fogResult;
        ComputeBuffer fogBuffer;
        int lastVolumeCount = 0;
        ComputeBuffer mainLightBuffer;
        ComputeBuffer pointLightBuffer;
        ComputeBuffer spotLightBuffer;

        public SimpleVolumeFogPass(Settings s) { settings = s; }

        void PrepareFogBuffer()
        {
            var fogVolumes = GameObject.FindObjectsOfType<VolumetricFogVolume>();
            int count = Mathf.Min(fogVolumes.Length, settings.maxFogVolumes);
            FogVolumeData[] fogDataArray = new FogVolumeData[count];
            for (int i = 0; i < count; i++)
            {
                var v = fogVolumes[i];
                fogDataArray[i] = new FogVolumeData
                {
                    position = v.transform.position,
                    size = v.transform.localScale,
                    density = v.density,
                    color = v.color,
                    shape = (int)v.shape,
                    falloffMode = (int)v.falloffMode,
                    falloffDistance = v.falloffDistance,
                    noiseIntensity = v.noiseIntensity,
                    noiseScale = v.noiseScale,
                    blendMode = (int)v.blendMode,
                    rotation = v.transform.rotation,
                    invRotation = Matrix4x4.Rotate(Quaternion.Inverse(v.transform.rotation))
                };
            }
            if (fogBuffer == null || lastVolumeCount != count)
            {
                if (fogBuffer != null) fogBuffer.Release();
                fogBuffer = new ComputeBuffer(Mathf.Max(1, count), System.Runtime.InteropServices.Marshal.SizeOf(typeof(FogVolumeData)));
                lastVolumeCount = count;
            }
            if (count > 0)
                fogBuffer.SetData(fogDataArray);
            else
                fogBuffer.SetData(new FogVolumeData[1]);

            // foreach (var v in fogVolumes)
            // {
            //     Debug.Log($"FogVolume: pos={v.transform.position}, size={v.size}, radius={v.radius}, density={v.density}, color={v.color}");
            // }
        }

        void PrepareLightBuffers()
        {
            // 主光源
            var sun = RenderSettings.sun;
            MainLightData mainLight = new MainLightData
            {
                direction = sun ? -sun.transform.forward : Vector3.down,
                color = sun ? sun.color : Color.white,
                intensity = sun ? sun.intensity : 1f,
                shadowMatrix = sun ? sun.transform.localToWorldMatrix : Matrix4x4.identity
            };
            if (mainLightBuffer == null)
                mainLightBuffer = new ComputeBuffer(1, System.Runtime.InteropServices.Marshal.SizeOf(typeof(MainLightData)));
            mainLightBuffer.SetData(new MainLightData[] { mainLight });

            // 点光源（这里只做示例，实际可遍历场景所有点光源）
            var points = GameObject.FindObjectsOfType<Light>();
            var pointList = new List<PointLightData>();
            foreach (var l in points)
            {
                if (l.type == LightType.Point)
                {
                    pointList.Add(new PointLightData
                    {
                        position = l.transform.position,
                        color = l.color,
                        intensity = l.intensity,
                        range = l.range
                    });
                }
            }
            if (pointLightBuffer == null || pointLightBuffer.count != pointList.Count)
            {
                if (pointLightBuffer != null) pointLightBuffer.Release();
                pointLightBuffer = new ComputeBuffer(Mathf.Max(1, pointList.Count), System.Runtime.InteropServices.Marshal.SizeOf(typeof(PointLightData)));
            }
            if (pointList.Count > 0)
                pointLightBuffer.SetData(pointList.ToArray());
            else
                pointLightBuffer.SetData(new PointLightData[1]);

            // 聚光灯
            var lights = GameObject.FindObjectsOfType<Light>();
            var spotList = new List<SpotLightData>();
            foreach (var l in lights)
            {
                if (l.type == LightType.Spot)
                {
                    spotList.Add(new SpotLightData
                    {
                        position = l.transform.position,
                        direction = l.transform.forward,
                        color = l.color,
                        intensity = l.intensity,
                        range = l.range,
                        spotAngle = l.spotAngle
                    });
                }
            }
            if (spotLightBuffer == null || spotLightBuffer.count != spotList.Count)
            {
                if (spotLightBuffer != null) spotLightBuffer.Release();
                spotLightBuffer = new ComputeBuffer(Mathf.Max(1, spotList.Count), System.Runtime.InteropServices.Marshal.SizeOf(typeof(SpotLightData)));
            }
            if (spotList.Count > 0)
                spotLightBuffer.SetData(spotList.ToArray());
            else
                spotLightBuffer.SetData(new SpotLightData[1]);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            var cam = renderingData.cameraData.camera;
            // if (cam.cameraType != CameraType.Game)
            //     return;
            if (settings.fogCompute == null) return;
            int width = renderingData.cameraData.camera.pixelWidth;
            int height = renderingData.cameraData.camera.pixelHeight;
            PrepareFogBuffer();
            if (fogResult == null || fogResult.width != width || fogResult.height != height)
            {
                if (fogResult != null) fogResult.Release();
                fogResult = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
                fogResult.enableRandomWrite = true;
                fogResult.Create();
            }
            PrepareLightBuffers();
            int kernel = settings.fogCompute.FindKernel("CSMain");
            settings.fogCompute.SetInt("stepCount", settings.stepCount);
            settings.fogCompute.SetBuffer(kernel, "FogVolumes", fogBuffer);
            settings.fogCompute.SetInt("fogVolumeCount", lastVolumeCount);
            settings.fogCompute.SetTexture(kernel, "Result", fogResult);
            settings.fogCompute.SetBuffer(kernel, "MainLight", mainLightBuffer);
            settings.fogCompute.SetBuffer(kernel, "PointLights", pointLightBuffer);
            settings.fogCompute.SetBuffer(kernel, "SpotLights", spotLightBuffer);
            settings.fogCompute.SetInt("pointLightCount", pointLightBuffer.count);
            settings.fogCompute.SetInt("spotLightCount", spotLightBuffer.count);
            settings.fogCompute.SetVector("cameraPos", cam.transform.position);
            Matrix4x4 invViewProj = (cam.projectionMatrix * cam.worldToCameraMatrix).inverse;
            settings.fogCompute.SetMatrix("invViewProj", invViewProj);
            settings.fogCompute.SetFloat("cameraNear", cam.nearClipPlane);
            settings.fogCompute.SetFloat("cameraFar", cam.farClipPlane);
            // 传递场景颜色纹理
            var cameraColorTarget = renderingData.cameraData.renderer.cameraColorTargetHandle;
            // 1. 创建临时RT
            RenderTextureDescriptor desc = new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGB32, 0);
            desc.enableRandomWrite = true;
            RenderTexture sceneColorRT = RenderTexture.GetTemporary(desc);

            // 2. 拷贝当前场景颜色到sceneColorRT
            CommandBuffer preCmd = CommandBufferPool.Get("CopySceneColor");
            preCmd.Blit(cameraColorTarget, sceneColorRT);
            context.ExecuteCommandBuffer(preCmd);
            CommandBufferPool.Release(preCmd);

            // 3. 传递sceneColorRT给Compute Shader
            settings.fogCompute.SetTexture(kernel, "SceneColor", sceneColorRT);

            // 4. 运行Compute Shader
            settings.fogCompute.Dispatch(kernel, width / 8, height / 8, 1);

            // 5. Blit雾结果到主屏幕
            CommandBuffer cmd = CommandBufferPool.Get("SimpleVolumeFogBlit");
            cmd.Blit(fogResult, cameraColorTarget);
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);

            // 6. 释放临时RT
            RenderTexture.ReleaseTemporary(sceneColorRT);
        }        
#if UNITY_2023_3_OR_NEWER
        public void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var fogVolumes = GameObject.FindObjectsOfType<VolumetricFogVolume>();
            int count = Mathf.Min(fogVolumes.Length, settings.maxFogVolumes);
            FogVolumeData[] fogDataArray = new FogVolumeData[count];
            for (int i = 0; i < count; i++)
            {
                var v = fogVolumes[i];
                fogDataArray[i] = new FogVolumeData
                {
                    position = v.transform.position,
                    size = v.transform.localScale,
                    density = v.density,
                    color = v.color,
                    shape = (int)v.shape,
                    falloffMode = (int)v.falloffMode,
                    falloffDistance = v.falloffDistance,
                    noiseIntensity = v.noiseIntensity,
                    noiseScale = v.noiseScale,
                    blendMode = (int)v.blendMode,
                    rotation = v.transform.rotation,
                    invRotation = Matrix4x4.Rotate(Quaternion.Inverse(v.transform.rotation))
                };
            }
            ComputeBuffer fogBuffer = new ComputeBuffer(Mathf.Max(1, count), System.Runtime.InteropServices.Marshal.SizeOf(typeof(FogVolumeData)));
            if (count > 0)
                fogBuffer.SetData(fogDataArray);
            else
                fogBuffer.SetData(new FogVolumeData[1]);
            var cameraData = frameData.Get<UniversalCameraData>();
            int w = cameraData.camera.pixelWidth;
            int h = cameraData.camera.pixelHeight;
            TextureHandle fogResult = renderGraph.CreateTexture(new TextureDesc(w, h)
            {
                colorFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm,
                enableRandomWrite = true,
                name = "SimpleVolumeFogResult"
            });
            renderGraph.AddComputePass("SimpleVolumeFogDispatch", (ComputeGraphContext ctx) =>
            {
                int kernel = settings.fogCompute.FindKernel("CSMain");
                settings.fogCompute.SetInt("stepCount", settings.stepCount);
                settings.fogCompute.SetBuffer(kernel, "FogVolumes", fogBuffer);
                settings.fogCompute.SetInt("fogVolumeCount", count);
                settings.fogCompute.SetTexture(kernel, "Result", ctx.GetTexture(fogResult));
                var cam = cameraData.camera;
                settings.fogCompute.SetVector("cameraPos", cam.transform.position);
                Matrix4x4 invViewProj = (cam.projectionMatrix * cam.worldToCameraMatrix).inverse;
                settings.fogCompute.SetMatrix("invViewProj", invViewProj);
                settings.fogCompute.SetFloat("cameraNear", cam.nearClipPlane);
                settings.fogCompute.SetFloat("cameraFar", cam.farClipPlane);
                settings.fogCompute.Dispatch(kernel, w / 8, h / 8, 1);
            });
            var colorTarget = cameraData.renderer.cameraColorTargetHandle;
            renderGraph.AddRasterRenderPass("BlitFogToScreen", (RasterGraphContext ctx) =>
            {
                ctx.Blit(fogResult, colorTarget);
            });
            fogBuffer.Release();
        }
#endif
        ~SimpleVolumeFogPass()
        {
            if (fogBuffer != null) fogBuffer.Release();
        }
    }
} 