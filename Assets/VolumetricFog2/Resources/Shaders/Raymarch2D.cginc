#ifndef VOLUMETRIC_FOG_2_RAYMARCH
#define VOLUMETRIC_FOG_2_RAYMARCH

#if defined(_LIGHT_LAYERS)
    static uint meshRenderingLayers;
#endif

// 设置抖动值，用于减少体积雾渲染中的条带伪影
void SetJitter(float2 uv) {

    // 获取屏幕尺寸，考虑可能的降采样
    float2 screenSize = lerp(_ScreenParams.xy, _VFRTSize.xy, _VFRTSize.z);
    float2 pixelPos = uv * screenSize;

    #if defined(FOG_BLUE_NOISE)
        // 使用蓝噪声纹理获取更高质量的抖动模式
        float2 noiseUV = pixelPos * _BlueNoise_TexelSize.xy;
        jitter = SAMPLE_TEXTURE2D(_BlueNoise, sampler_BlueNoise_PointRepeat, noiseUV).r;
    #else
        // 使用数学方法生成伪随机抖动值
        const float3 magic = float3(0.06711056, 0.00583715, 52.9829189);
        jitter = frac(magic.z * frac(dot(pixelPos, magic.xy)));
    #endif
}


// 将向量投影到平面上
inline float3 ProjectOnPlane(float3 v, float3 planeNormal) {
    // 假设平面法线已归一化
    float dt = dot(v, planeNormal);
	return v - planeNormal * dt; // 从向量中减去法线方向的分量
}

// 获取光线起点（相机位置或正交投影的起点）
inline float3 GetRayStart(float3 wpos) {
    float3 cameraPosition = GetCameraPositionWS();
    #if defined(ORTHO_SUPPORT)
        // 正交相机需要特殊处理
	    float3 cameraForward = UNITY_MATRIX_V[2].xyz; // 相机前方向量
	    float3 rayStart = ProjectOnPlane(wpos - cameraPosition, cameraForward) + cameraPosition;
        // 在透视和正交模式之间插值
        return lerp(cameraPosition, rayStart, unity_OrthoParams.w);
    #else
        // 透视相机直接使用相机位置作为光线起点
        return cameraPosition;
    #endif
}

inline half Brightness(half3 color) {
    return max(color.r, max(color.g, color.b));
}


// 采样给定世界空间位置的雾效密度
half4 SampleDensity(float3 wpos) {

    // 应用垂直偏移
    wpos.y -= BOUNDS_VERTICAL_OFFSET;
    float3 boundsCenter = _BoundsCenter;
    float3 boundsExtents = _BoundsExtents;

#if VF2_SURFACE
    // 应用表面修改（如果启用）
    SurfaceApply(boundsCenter, boundsExtents);
#endif

#if VF2_DETAIL_NOISE
    // 使用3D细节噪声
    #if !defined(USE_WORLD_SPACE_NOISE)
        // 转换到局部空间
        wpos.xyz -= boundsCenter;
    #endif
    // 从3D纹理采样细节噪声，并应用风向动画
    half detail = tex3Dlod(_DetailTex, float4(wpos * DETAIL_SCALE - _DetailWindDirection, 0)).a;
    half4 density = _DetailColor;
    if (USE_BASE_NOISE) {
        // 如果同时使用基础噪声
        #if defined(USE_WORLD_SPACE_NOISE)
            wpos.y -= boundsCenter.y;
        #endif
        // 归一化高度
        wpos.y /= boundsExtents.y;
        // 从2D纹理采样基础噪声，并应用风向动画
        density = tex2Dlod(_NoiseTex, float4(wpos.xz * _NoiseScale - _WindDirection.xz, 0, 0));
        // 根据高度减少密度
        density.a -= abs(wpos.y);
    }
    // 将细节噪声添加到密度中
    density.a += (detail + DETAIL_OFFSET) * DETAIL_STRENGTH;
#else
    // 仅使用2D基础噪声
    #if defined(USE_WORLD_SPACE_NOISE) || VF2_CONSTANT_DENSITY
        wpos.y -= boundsCenter.y;
    #else
        wpos.xyz -= boundsCenter;
    #endif
    // 归一化高度
    wpos.y /= boundsExtents.y;
    #if VF2_CONSTANT_DENSITY
        // 使用常量密度（无噪声）
        half4 density = half4(_DetailColor.rgb, 1.0);
    #else
        // 从2D纹理采样噪声
        half4 density = tex2Dlod(_NoiseTex, float4(wpos.xz * _NoiseScale - _WindDirection.xz, 0, 0));
    #endif
    // 根据高度减少密度
    density.a -= abs(wpos.y);
#endif

    return density;
}


#define dot2(x) dot(x,x)

// 在光线步进中的单个采样点添加雾效贡献
// rayStart: 光线起点
// wpos: 当前采样点的世界坐标
// uv: 屏幕空间坐标
// energyStep: 当前步长的能量贡献
// baseColor: 基础光照颜色
// sum: 累积的雾效颜色和透明度
void AddFog(float3 rayStart, float3 wpos, float2 uv, half energyStep, half4 baseColor, inout half4 sum) {

   // 采样当前位置的雾效密度
   half4 density = SampleDensity(wpos);

   // 处理雾效体积旋转
   float3 rotatedWPos = wpos;
   #if defined(FOG_ROTATION)
        rotatedWPos = Rotate(rotatedWPos);
   #endif

   // 应用雾效空洞（如果启用）
   #if VF2_VOIDS
        density.a -= ApplyFogVoids(rotatedWPos);
   #endif

   // 应用边界过渡效果
   #if defined(FOG_BORDER)
        #if VF2_SHAPE_SPHERE
            // 球形边界
            float3 delta = wpos - _BoundsCenter;
            float distSqr = dot2(delta);
            // 计算平滑边界过渡
            float border = 1.0 - saturate((distSqr - BORDER_START_SPHERE) / BORDER_SIZE_SPHERE);
            density.a *= border * border; // 平方使过渡更平滑
        #else
            // 盒形边界
            float2 dist2 = abs(wpos.xz - _BoundsCenter.xz);
            float2 border2 = saturate((dist2 - BORDER_START_BOX) / BORDER_SIZE_BOX);
            float border = 1.0 - max(border2.x, border2.y);
            density.a *= border * border;
        #endif
   #endif

   // 应用距离衰减（如果启用）
   #if VF2_DISTANCE
        density.a -= ApplyFogDistance(rayStart, wpos);
   #endif

   // 只有当密度大于0时才进行计算，提高性能
   UNITY_BRANCH
   if (density.a > 0) {
        // 计算雾效颜色，应用深度遮蔽效果
        half4 fgCol = baseColor * half4((1.0 - density.a * _DeepObscurance).xxx, density.a);

        // 应用阴影（如果启用）
        #if VF2_RECEIVE_SHADOWS
            if (loop_t < loop_shadowMaxDistance) {
                // 获取阴影衰减值
                half shadowAtten = GetLightAttenuation(rotatedWPos);
                // 应用阴影到颜色
                fgCol.rgb *= lerp(1.0, shadowAtten, SHADOW_INTENSITY);
                // 可选：阴影也影响透明度
                #if defined(FOG_SHADOW_CANCELLATION)
                    fgCol.a *= lerp(1.0, shadowAtten, SHADOW_CANCELLATION);
                #endif
            }
        #endif
        #if VF2_NATIVE_LIGHTS
            // 如果使用前向加号，并且没有忽略聚类
            #if USE_FORWARD_PLUS && !defined(FOG_FORWARD_PLUS_IGNORE_CLUSTERING)
                // 额外的方向光源
                #if defined(FOG_FORWARD_PLUS_ADDITIONAL_DIRECTIONAL_LIGHTS)
                    for (uint lightIndex = 0; lightIndex < URP_FP_DIRECTIONAL_LIGHTS_COUNT; lightIndex++) {
                        Light light = GetAdditionalLight(lightIndex, rotatedWPos, 1.0.xxxx);
                        #if defined(_LIGHT_LAYERS)
                            if (IsMatchingLightLayer(light.layerMask, meshRenderingLayers))
                        #endif
                        {
                            fgCol.rgb += light.color * (light.distanceAttenuation * light.shadowAttenuation * _NativeLightsMultiplier);
                        }
                    }
                #endif
                // 聚类光源
                {
                    uint lightIndex;
                    ClusterIterator _urp_internal_clusterIterator = ClusterInit(uv, rotatedWPos, 0);
                    [loop] while (ClusterNext(_urp_internal_clusterIterator, lightIndex)) {
                        lightIndex += URP_FP_DIRECTIONAL_LIGHTS_COUNT;
                        Light light = GetAdditionalLight(lightIndex, rotatedWPos, 1.0.xxxx);
                        #if defined(_LIGHT_LAYERS)
                            if (IsMatchingLightLayer(light.layerMask, meshRenderingLayers))
                        #endif
                        {
                            fgCol.rgb += light.color * (light.distanceAttenuation * light.shadowAttenuation * _NativeLightsMultiplier);
                        }
                    }
                }
            #else
                // 如果使用前向加号，并且忽略聚类
                #if USE_FORWARD_PLUS
                    uint additionalLightCount = min(URP_FP_PROBES_BEGIN, 8); // more than 8 lights is too slow for raymarching
                #else
                    uint additionalLightCount = GetAdditionalLightsCount();
                #endif
                for (uint i = 0; i < additionalLightCount; ++i) {
                    #if UNITY_VERSION >= 202030
                        Light light = GetAdditionalLight(i, rotatedWPos, 1.0.xxxx);
                    #else
                        Light light = GetAdditionalLight(i, rotatedWPos);
                    #endif
                    #if defined(_LIGHT_LAYERS)
                        if (IsMatchingLightLayer(light.layerMask, meshRenderingLayers))
                    #endif
                    {
                        fgCol.rgb += light.color * (light.distanceAttenuation * light.shadowAttenuation * _NativeLightsMultiplier);
                    }
                }
            #endif
        #endif

        #if UNITY_VERSION >= 202310 && defined(VF2_APV)
            // 应用APV颜色
            //APV: 环境粒子体积
            fgCol.rgb += GetAPVColor(wpos);
        #endif

        // 应用光照Cookie纹理（如果启用）
        #if VF2_LIGHT_COOKIE
            half3 cookieColor = SampleMainLightCookie(wpos);
            fgCol.rgb *= cookieColor; // 应用Cookie颜色到雾效
            #if defined(V2F_LIGHT_COOKIE_CANCELLATION)
                // Cookie也可以影响透明度
                fgCol.a *= Brightness(cookieColor);
            #endif
        #endif

        // 应用深度渐变（如果启用）
		#if VF2_DEPTH_GRADIENT
			fgCol *= ApplyDepthGradient(rayStart, wpos);
		#endif

        // 应用高度渐变（如果启用）
		#if VF2_HEIGHT_GRADIENT
			fgCol *= ApplyHeightGradient(wpos);
		#endif

        // 将噪声颜色与雾效颜色结合
        //fgCol.aaa: 雾效的透明度
        fgCol.rgb *= density.rgb * fgCol.aaa;

        // 应用战争迷雾（如果启用）
        #if VF2_FOW
            fgCol *= ApplyFogOfWar(rotatedWPos);
        #endif

        // 应用能量步长（与步长大小成比例）
        fgCol *= energyStep;

        // 使用前向混合累积雾效颜色
        // (1.0 - sum.a)确保已经不透明的区域不再累积更多雾效
        sum += fgCol * (1.0 - sum.a);
   }
}

#pragma warning (disable : 3571) // disable pow() negative warning

// 简单的散射强度计算 - 基于视线与光源方向的夹角的幂函数
half SimpleDiffusionIntensity(half cosTheta, half power) {
    return pow(cosTheta, power); // 简单的余弦幂函数
}

// Henyey-Greenstein相位函数 - 更精确的光散射模型
// 参数g控制散射的方向性：g>0前向散射，g<0后向散射，g=0各向同性
half HenyeyGreenstein(half cosTheta, half g) {
    half g2 = g * g;
    half denom = 1.0 + g2 - 2.0 * g * cosTheta;
    // 标准的Henyey-Greenstein公式
    return (1.0 - g2) / (4.0 * 3.14159265 * pow(denom, 1.5));
}

// Mie散射相位函数 - 适用于大气中的颗粒散射
// 比Henyey-Greenstein提供更强的前向散射峰值
half MiePhase(half cosTheta, half g) {
    half g2 = g * g;
    half denom = 1.0 + g2 - 2.0 * g * cosTheta;
    // 修改版的Mie散射公式，增强了前向散射效果
    return 1.5 * ((1.0 - g2) / (2.0 + g2)) * (1.0 + cosTheta * cosTheta) / pow(denom, 1.5);
}

// 根据视线方向计算散射强度
half GetDiffusionIntensity(float3 viewDir) {
    // 计算视线方向与光源方向的夹角余弦值
    half cosTheta = max(dot(viewDir, _SunDir.xyz), 0);

    // 根据选择的散射模型计算散射强度
    #if VF2_DIFFUSION_SMOOTH
        // 平滑散射 - 使用Henyey-Greenstein模型
        half diffusion = HenyeyGreenstein(cosTheta, LIGHT_DIFFUSION_POWER);
    #elif VF2_DIFFUSION_STRONG
        // 强散射 - 使用Mie散射模型
        half diffusion = MiePhase(cosTheta, LIGHT_DIFFUSION_POWER);
    #else
        // 简单散射 - 使用余弦幂函数
        half diffusion = SimpleDiffusionIntensity(cosTheta, LIGHT_DIFFUSION_POWER);
    #endif

    // 应用散射强度系数
    return diffusion * LIGHT_DIFFUSION_INTENSITY;
}

// 计算最终的散射光颜色
half3 GetDiffusionColor(float3 viewDir, float t1) {
    // 获取散射强度
    half diffusion = GetDiffusionIntensity(viewDir);

    // 根据距离调整散射效果，远处的散射更明显
    // dot2(t1/ATTEN)计算距离的平方，然后用saturate限制在[0,1]范围
    half3 diffusionColor = _LightColor.rgb * (1.0 + diffusion * saturate(dot2(t1 / LIGHT_DIFFUSION_DEPTH_ATTEN)));

    return diffusionColor;
}

// 执行光线步进，计算给定光线的雾效颜色
half4 GetFogColor(float3 rayStart, float3 viewDir, float2 uv, float t0, float t1) {

    // 计算光线长度
    float len = t1 - t0;
    // 根据距离自适应调整步长 - 远处使用更大的步长以提高性能
    float rs = MIN_STEPPING + max(log(len), 0) * FOG_STEPPING;
    // 计算光散射颜色
    half3 diffusionColor = GetDiffusionColor(viewDir, t1);
    half4 lightColor = half4(diffusionColor, 1.0);

    // 计算光线起点和终点的世界坐标
    float3 wpos = rayStart + viewDir * t0;
    float3 endPos = rayStart + viewDir * t1;

    #if VF2_SURFACE
        // 如果启用了表面功能，调整光线端点
        SurfaceComputeEndPoints(wpos, endPos);
    #endif

    // 确保步长不会导致迭代次数过多
    rs = max(rs, 1.0 / MAX_ITERATIONS);

    // 调整视线方向向量，使其长度等于步长
    viewDir *= rs;

    // 计算每步的能量贡献
    half energyStep = min(1.0, _Density * rs);
    // 初始化累积颜色
    half4 sum = half4(0,0,0,0);

    #if VF2_RECEIVE_SHADOWS
        // 计算阴影最大距离
        loop_shadowMaxDistance = (SHADOW_MAX_DISTANCE - t0) / len;
    #endif

    // 设置光照层级
    #if defined(_LIGHT_LAYERS)
        meshRenderingLayers = GetMeshRenderingLayer();
    #endif

    // 归一化步长（相对于总长度）
    rs /= len;

    // 使用展开循环以支持WebGL或特定功能
    #if defined(WEBGL_COMPATIBILITY_MODE)
        UNITY_UNROLLX(50)
    #elif VF2_LIGHT_COOKIE
        UNITY_LOOP
    #endif
    // 主光线步进循环
    for (loop_t = 0; loop_t < 1.0; loop_t += rs) {
        // 在当前位置添加雾效贡献
        AddFog(rayStart, wpos, uv, energyStep, lightColor, sum);
        // 如果雾效已经完全不透明，提前退出循环
        if (sum.a > 0.99) {
            break;
        }
        // 移动到下一个采样点
        wpos += viewDir;
    }

    // 处理最终结果
    if (sum.a > 0.99) {
        // 如果雾效完全不透明，设置alpha为1
        sum.a = 1;
    } else {
        // 处理最后一步（可能是部分步长）
        energyStep = _Density * len * (rs - (loop_t-1.0));
        energyStep = min(1.0, energyStep);
        AddFog(rayStart, endPos, uv, energyStep, lightColor, sum);
    }

    return sum;
}


// 主要的雾效计算函数，处理光线与雾体积的交互
half4 ComputeFog(float3 wpos, float2 uv) {

    // 获取光线起点（相机位置）
    float3 rayStart = GetRayStart(wpos);
    // 计算从相机到目标点的光线
    float3 ray = wpos - rayStart;
    float t1 = length(ray);

    #if defined(FOG_ROTATION)
        // 如果雾效体积有旋转，需要进行坐标变换
        float3 rayStartNonRotated = rayStart;
        float3 rayDirNonRotated = ray / t1;
        // 将光线起点和方向转换到雾效的局部空间
        rayStart = RotateInv(rayStart);
        ray = mul((float3x3)_InvRotMatrix, ray);
        float3 rayDir = ray / t1;
    #else
        // 无旋转情况下直接使用世界空间坐标
        float3 rayDir = ray / t1;
        float3 rayStartNonRotated = rayStart;
        float3 rayDirNonRotated = rayDir;
    #endif

    #if VF2_SHAPE_SPHERE
        // 球形雾效体积的光线交点计算
        float t0;
        SphereIntersection(rayStart, rayDir, t0, t1);
    #else
        // 盒形雾效体积的光线交点计算
        float t0 = BoxIntersection(rayStart, rayDir);
    #endif

    #if defined(FOG_MAX_DISTANCE_XZ)
        // 根据视线与水平面的夹角调整最大距离
        float slope = 1.0001 - abs(rayDir.y);
        FOG_MAX_LENGTH /= slope;
    #endif

    // 设置抖动值，用于减少条带伪影
    SetJitter(uv);

    // 限制最大渲染距离
    t1 = min(t1, FOG_MAX_LENGTH);

    // 应用抖动到光线起点和终点
    float jiterring = jitter * JITTERING;
    t0 += jiterring;
    t1 += jiterring;

    // 使用场景深度限制光线长度，避免穿过物体
    CLAMP_RAY_DEPTH(rayStartNonRotated, uv, t1);

    #if VF2_DEPTH_PEELING
        // 如果启用深度剥离，调整光线起点以处理透明物体
        CLAMP_RAY_START(rayStartNonRotated, uv, t0);
    #endif

    // 如果光线起点在终点之后，说明没有雾效贡献
    if (t0 >= t1) return 0;

    // 执行光线步进，计算雾效颜色
    half4 fogColor = GetFogColor(rayStart, rayDir, uv, t0, t1);

    // 应用抖动效果减少条带伪影
    #if !VF2_DEPTH_PEELING // 深度剥离模式下不使用抖动
        fogColor.rgb = max(0, fogColor.rgb - jitter * DITHERING);
    #endif

    // 应用全局透明度
    fogColor *= _LightColor.a;

    #if VF2_POINT_LIGHTS
        // 添加点光源对雾效的影响
        AddPointLights(rayStartNonRotated, rayDirNonRotated, fogColor, t0, t1 - t0);
    #endif

    // 应用距离衰减
    #if defined(FOG_MAX_DISTANCE_XZ)
        float fallOffFactor = FOG_MAX_LENGTH * FOG_MAX_LENGTH_FALLOFF + 1.0;
        half maxDistanceFallOff = (FOG_MAX_LENGTH - t0) / fallOffFactor;
    #else
        half maxDistanceFallOff = (FOG_MAX_LENGTH - t0) / FOG_MAX_LENGTH_FALLOFF_PRECOMPUTED;
    #endif
    // 使用四次方曲线实现平滑的距离衰减
    fogColor *= saturate(maxDistanceFallOff * maxDistanceFallOff * maxDistanceFallOff * maxDistanceFallOff);

    return fogColor;
}

#endif // VOLUMETRIC_FOG_2_RAYMARCH