# Volumetric Fog 2 Shader函数导航图

## 核心渲染函数
- `ComputeFog(float3 wpos, float2 uv)` - Raymarch2D.cginc:408
- `GetFogColor(float3 rayStart, float3 viewDir, float2 uv, float t0, float t1)` - Raymarch2D.cginc:328
- `AddFog(float3 rayStart, float3 wpos, float2 uv, half energyStep, half4 baseColor, inout half4 sum)` - Raymarch2D.cginc:111

## 光散射函数
- `GetDiffusionIntensity(float3 viewDir)` - Raymarch2D.cginc:293
- `GetDiffusionColor(float3 viewDir, float t1)` - Raymarch2D.cginc:307
- `SimpleDiffusionIntensity(half cosTheta, half power)` - Raymarch2D.cginc:272
- `HenyeyGreenstein(half cosTheta, half g)` - Raymarch2D.cginc:276
- `MiePhase(half cosTheta, half g)` - Raymarch2D.cginc:285

## 噪声和密度函数
- `SampleDensity(float3 wpos)` - Raymarch2D.cginc:58
- `SetJitter(float2 uv)` - Raymarch2D.cginc:9

## 辅助函数
- `ProjectOnPlane(float3 v, float3 planeNormal)` - Raymarch2D.cginc:23
- `GetRayStart(float3 wpos)` - Raymarch2D.cginc:28
- `Brightness(half3 color)` - Raymarch2D.cginc:41

## 着色器入口点
- `vert(appdata v)` - VolumetricFog2DURP.shader:196
- `frag(v2f i)` - VolumetricFog2DURP.shader:227 