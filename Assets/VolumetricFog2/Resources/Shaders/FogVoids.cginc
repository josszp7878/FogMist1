// FogVoids.cginc
// 处理雾效空洞（在雾效中创建无雾区域）
#ifndef VOLUMETRIC_FOG_2_FOG_VOIDS
#define VOLUMETRIC_FOG_2_FOG_VOIDS

// 最大支持的雾效空洞数量
#define FOG_MAX_VOID 8

// 雾效空洞数据缓冲区
CBUFFER_START(VolumetricFog2FogVoidBuffers)
    float4 _VF2_FogVoidPositions[FOG_MAX_VOID]; // xyz: 位置, w: 边缘过渡强度
    float4 _VF2_FogVoidSizes[FOG_MAX_VOID];     // xyz: 尺寸, w: 圆形度（0=方形，1=圆形）
    #if defined(FOG_VOID_ROTATION)
        float4x4 _VF2_FogVoidMatrices[FOG_MAX_VOID]; // 旋转矩阵，用于支持旋转的空洞
    #endif
    int _VF2_FogVoidCount; // 当前活动的雾效空洞数量
CBUFFER_END

/**
 * 计算给定世界空间位置处的雾效空洞影响
 * 使用有符号距离场(SDF)技术来平滑混合多个空洞
 *
 * @param wpos 世界空间位置
 * @return 雾效密度修改因子（0=完全无雾，1=不受空洞影响）
 */
half ApplyFogVoids(float3 wpos) {
    // 初始化SDF值为一个较大的数，确保第一个空洞会覆盖它
    float sdf = 10.0;

    // 遍历所有活动的雾效空洞
    for (int k=0;k<_VF2_FogVoidCount;k++) {
        // 计算点到空洞中心的距离
        #if defined(FOG_VOID_ROTATION)
            // 如果启用了旋转，使用矩阵变换将世界坐标转换到空洞的局部空间
            float3 vd = mul(_VF2_FogVoidMatrices[k], float4(wpos.xyz, 1.0)).xyz;
            // 计算各分量的平方（用于SDF计算）
            vd *= vd;
        #else
            // 计算从空洞中心到点的向量
            float3 vd = _VF2_FogVoidPositions[k].xyz - wpos.xyz;
            // 计算各分量的平方
            vd *= vd;
            // 根据空洞尺寸缩放距离
            vd *= _VF2_FogVoidSizes[k].xyz;
        #endif

        // 计算方形SDF（取三个轴向距离的最大值）
        float rect = max(vd.x, max(vd.y, vd.z));

        // 计算圆形SDF（三个轴向距离的和）
        float circ = vd.x + vd.y + vd.z;

        // 根据圆形度参数在方形和圆形之间插值
        // 0 = 方形空洞，1 = 圆形空洞
        float voidd = lerp(rect, circ, _VF2_FogVoidSizes[k].w);

        // 应用边缘过渡效果
        // 较高的过渡值会创建更柔和的边缘
        voidd = lerp(1.0, voidd, _VF2_FogVoidPositions[k].w);

        // 合并SDF（取最小值，这样多个空洞可以重叠）
        sdf = min(sdf, voidd);
    }

    // 反转SDF值（1.0 - sdf），使内部为1，外部为0
    sdf = 1.0 - sdf;

    // 限制在[0,1]范围内并返回
    // 0 = 完全无雾（空洞内部）
    // 1 = 不受空洞影响（空洞外部）
    return saturate(sdf);
}

#endif