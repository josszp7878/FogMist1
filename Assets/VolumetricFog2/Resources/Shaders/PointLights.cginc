// PointLights.cginc
// 处理点光源对体积雾的影响
#ifndef VOLUMETRIC_FOG_2_POINT_LIGHTS
#define VOLUMETRIC_FOG_2_POINT_LIGHTS

#if VF2_POINT_LIGHTS

// 取消注释以启用快速点光源遮挡检测（使用深度缓冲）
//#define FAST_POINT_LIGHTS_OCCLUSION

// 最大支持的点光源数量
#define FOG_MAX_POINT_LIGHTS 16

// 点光源数据缓冲区
CBUFFER_START(VolumetricFog2PointLightBuffers)
    float4 _VF2_FogPointLightPosition[FOG_MAX_POINT_LIGHTS]; // xyz: 位置, w: 未使用
    half4 _VF2_PointLightColor[FOG_MAX_POINT_LIGHTS];        // rgb: 颜色, w: 范围的平方
    float _VF2_PointLightInsideAtten;                        // 内部衰减（防止光源在雾内部时过亮）
    int _VF2_PointLightCount;                                // 当前活动的点光源数量
CBUFFER_END

// 计算点积的平方，用于距离计算
#define dot2(x) dot(x,x)

/**
 * 计算点到线段的最小距离的平方
 * @param fogLengthSqr 雾效光线长度的平方
 * @param w 光线方向向量（已乘以长度）
 * @param p 点光源相对于光线起点的位置
 * @return 最小距离的平方
 */
float minimum_distance_sqr(float fogLengthSqr, float3 w, float3 p) {
    // 计算点到线段的最小距离
    // 参数化线段：fogCeilingCut + t * rayDir，其中t∈[0,1]
    float t = saturate(dot(p, w) / fogLengthSqr);
    // 计算p在线段上的投影点
    float3 projection = t * w;
    // 计算p到投影点的距离平方
    float distSqr = dot2(p - projection);
    return distSqr;
}

/**
 * 将点光源的贡献添加到雾效颜色中
 * @param rayStart 光线起点（相机位置）
 * @param rayDir 光线方向（已归一化）
 * @param sum 当前累积的雾效颜色和透明度
 * @param t0 光线起点到雾效体积入口点的距离
 * @param fogLength 雾效体积的长度
 */
void AddPointLights(float3 rayStart, float3 rayDir, inout half4 sum, float t0, float fogLength) {
    // 计算光线与雾效体积的交点（入口点）
    float3 fogCeilingCut = rayStart + rayDir * t0;
    // 向内偏移一点，避免在边界处的计算问题
    fogCeilingCut += rayDir * _VF2_PointLightInsideAtten;
    // 相应地减少雾效长度
    fogLength -= _VF2_PointLightInsideAtten;
    // 将方向向量缩放为实际长度
    rayDir *= fogLength;
    // 计算长度的平方（用于后续计算）
    float fogLengthSqr = fogLength * fogLength;

    // 遍历所有活动的点光源
    for (int k=0;k<_VF2_PointLightCount;k++) {
        // 获取点光源位置
        float3 pointLightPosition = _VF2_FogPointLightPosition[k].xyz;

        #if defined(FAST_POINT_LIGHTS_OCCLUSION)
            // 快速遮挡检测：如果点光源被场景物体遮挡，则跳过该光源
	        float4 clipPos = TransformWorldToHClip(pointLightPosition);
            float4 scrPos  = ComputeScreenPos(clipPos);
            float  depth   = LinearEyeDepth(SampleSceneDepth(scrPos.xy / scrPos.w), _ZBufferParams);
            if (depth < clipPos.w) continue; // 如果深度缓冲中的深度小于光源深度，说明光源被遮挡
        #endif

        // 计算点光源对当前光线的影响
        // 首先计算点光源到光线的最小距离平方，然后除以光源范围平方
        half pointLightInfluence = minimum_distance_sqr(fogLengthSqr, rayDir, pointLightPosition - fogCeilingCut) / _VF2_PointLightColor[k].w;
        // 计算散射强度：基于雾效密度和距离的函数
        half scattering = sum.a / (1.0 + pointLightInfluence);
        // 将点光源颜色乘以散射强度，添加到累积颜色中
        sum.rgb += _VF2_PointLightColor[k].rgb * scattering;
    }
}

/**
 * 获取指定世界空间位置处的点光源颜色贡献
 * @param wpos 世界空间位置
 * @return 点光源颜色贡献
 */
half3 GetPointLights(float3 wpos) {
    half3 color = half3(0,0,0);
    // 遍历所有活动的点光源
    for (int k=0;k<_VF2_PointLightCount;k++) {
        // 计算从位置到光源的向量
        float3 toLight = _VF2_FogPointLightPosition[k].xyz - wpos;
        // 计算距离的平方
        float dist = dot2(toLight);
        // 应用平方反比衰减：颜色 * 范围平方 / 距离平方
        color += _VF2_PointLightColor[k].rgb * _VF2_PointLightColor[k].w / dist;
    }
    return color;
}

#endif // VF2_POINT_LIGHTS

#endif // VOLUMETRIC_FOG_2_POINT_LIGHTS