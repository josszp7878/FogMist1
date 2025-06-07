#ifndef VOLUMETRIC_FOG_2_FOW
#define VOLUMETRIC_FOG_2_FOW

sampler2D _FogOfWar;

half4 ApplyFogOfWar(float3 wpos) {
    // 计算雾化纹理坐标, 
    //_FogOfWarCenterAdjusted: 战争迷雾中心（调整后） 
    //_FogOfWarSize: 战争迷雾大小
    float2 fogTexCoord = wpos.xz / _FogOfWarSize.xz - _FogOfWarCenterAdjusted.xz;
    // 采样雾化纹理
    half4 fowColor = tex2Dlod(_FogOfWar, float4(fogTexCoord, 0, 0));
    // 返回雾化颜色
    return half4(fowColor.rgb * fowColor.a, fowColor.a);
}

#endif