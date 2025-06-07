// VolumetricFog2DURP.shader
// 主要的体积雾渲染Shader，用于URP渲染管线
Shader "VolumetricFog2/VolumetricFog2DURP"
{
	Properties
	{
		// 基础纹理和颜色
		[HideInInspector] _MainTex("Main Texture", 2D) = "white" {}
		[HideInInspector] _Color("Color", Color) = (1,1,1)

		// 噪声纹理 - 2D基础噪声和3D细节噪声
		[HideInInspector] _NoiseTex("Noise Texture", 2D) = "white" {} // 2D噪声纹理，提供基本形状
		[HideInInspector] _DetailTex("Detail Texture", 3D) = "white" {} // 3D体素纹理，提供细节变化

		// 噪声参数
		[HideInInspector] _NoiseScale("Noise Scale", Float) = 0.025 // 噪声缩放比例
		[HideInInspector] _NoiseFinalMultiplier("Noise Scale", Float) = 1.0 // 最终噪声乘数
		[HideInInspector] _NoiseStrength("Noise Strength", Float) = 1.0 // 噪声强度

		// 雾效密度和深度参数
		[HideInInspector] _Density("Density", Float) = 1.0 // 整体密度
		[HideInInspector] _DeepObscurance("Deep Obscurance", Range(0, 2)) = 0.7 // 深度遮蔽效果

		// 光照参数
		[HideInInspector] _LightColor("Light Color", Color) = (1,1,1) // 光照颜色
		[HideInInspector] _LightDiffusionData("Sun Diffusion Data", Vector) = (32, 0.4, 100) // 光散射数据
		[HideInInspector] _SunDir("Sun Direction", Vector) = (1,0,0) // 太阳方向
		[HideInInspector] _ShadowData("Shadow Data", Vector) = (0.5, 0, 62500) // 阴影参数

		// 风向和动画参数
		[HideInInspector] _WindDirection("Wind Direction", Vector) = (1, 0, 0) // 基础噪声的风向
		[HideInInspector] _DetailWindDirection("Detail Wind Direction", Vector) = (1, 0, 0) // 细节噪声的风向

		// 光线步进设置
		[HideInInspector] _RayMarchSettings("Raymarch Settings", Vector) = (2, 0.01, 1.0, 0.1) // 光线步进质量、抖动、抖动和最小步长

		// 边界和形状参数
		[HideInInspector] _BoundsCenter("Bounds Center", Vector) = (0,0,0) // 边界中心
		[HideInInspector] _BoundsExtents("Bounds Size", Vector) = (0,0,0) // 边界大小
		[HideInInspector] _BoundsBorder("Bounds Border", Vector) = (0,1,0) // 边界过渡区域
		[HideInInspector] _BoundsData("Bounds Data", Vector) = (0,0,1) // 边界附加数据

		// 细节噪声参数
		[HideInInspector] _DetailData("Detail Data", Vector) = (0.5, 4, -0.5, 0) // 细节噪声数据
		[HideInInspector] _DetailColor("Detail Color", Color) = (0.5,0.5,0.5,0) // 细节颜色
		[HideInInspector] _DetailOffset("Detail Offset", Float) = -0.5 // 细节偏移

		// 距离和渐变参数
		[HideInInspector] _DistanceData("Distance Data", Vector) = (0, 5, 1, 1) // 距离衰减数据
		[HideInInspector] _DepthGradientTex("Depth Gradient Texture", 2D) = "white" {} // 深度渐变纹理
		[HideInInspector] _HeightGradientTex("Height Gradient Texture", 2D) = "white" {} // 高度渐变纹理

		// 高光参数
		[HideInInspector] _SpecularThreshold("Specular Threshold", Float) = 0.5 // 高光阈值
		[HideInInspector] _SpecularIntensity("Specular Intensity", Float) = 0 // 高光强度
		[HideInInspector] _SpecularColor("Specular Color", Color) = (0.5,0.5,0.5,0) // 高光颜色

		// 战争迷雾参数
		[HideInInspector] _FogOfWarCenterAdjusted("FoW Center Adjusted", Vector) = (0,0,0) // 战争迷雾中心（调整后）
		[HideInInspector] _FogOfWarSize("FoW Size", Vector) = (0,0,0) // 战争迷雾大小
		[HideInInspector] _FogOfWarCenter("FoW Center", Vector) = (0,0,0) // 战争迷雾中心
		[HideInInspector] _FogOfWar("FoW Texture", 2D) = "white" {} // 战争迷雾纹理

		// 其他参数
		[HideInInspector] _BlueNoise("_Blue Noise Texture", 2D) = "white" {} // 蓝噪声纹理，用于减少条带伪影
		[HideInInspector] _MaxDistanceData("Max Lengh Data", Vector) = (100000, 0.00001, 0) // 最大距离数据
		[HideInInspector] _NativeLightsMultiplier("Native Lights Multiplier", Float) = 1 // 原生光源乘数
		[HideInInspector] _APVIntensityMultiplier("APV Intensity Multiplier", Float) = 1 // 自适应探针体积强度乘数
	}
		// 主要的SubShader定义
		SubShader
		{
			Name "Volumetric Fog"
			// 设置渲染类型为透明，队列为Transparent+100，禁用批处理，忽略投影器，指定URP渲染管线
			Tags { "RenderType" = "Transparent" "Queue" = "Transparent+100" "DisableBatching" = "True" "IgnoreProjector" = "True" "RenderPipeline" = "UniversalPipeline" }
			// 使用前向混合模式
			Blend One OneMinusSrcAlpha
			// 始终通过深度测试
			ZTest Always
			// 剔除前面（内部渲染）
			Cull Front
			// 不写入深度缓冲
			ZWrite Off
			// 禁用Z裁剪
			ZClip False

			// 主渲染Pass
			Pass
			{
				// 使用URP前向渲染光照模式
				Tags { "LightMode" = "UniversalForward" }
				HLSLPROGRAM
				// 着色器编译指令
				#pragma prefer_hlslcc gles
				#pragma exclude_renderers d3d11_9x
				#pragma target 3.0
				#pragma vertex vert // 指定顶点着色器
				#pragma fragment frag // 指定片元着色器

				// 根据Unity版本选择适当的阴影关键字
				#if UNITY_VERSION < 202100
					#pragma multi_compile _ _MAIN_LIGHT_SHADOWS
					#pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE
				#else
					#pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
				#endif

                // 启用额外光源和阴影
                #pragma multi_compile _ _ADDITIONAL_LIGHTS
				#pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS

                // 深度处理选项 - 用于透明物体的正确渲染
                #pragma multi_compile _ VF2_DEPTH_PREPASS VF2_DEPTH_PEELING

				// 光照选项 - 点光源或原生光源
				#pragma multi_compile_local_fragment _ VF2_POINT_LIGHTS VF2_NATIVE_LIGHTS

				// 阴影接收
				#pragma multi_compile_local_fragment _ VF2_RECEIVE_SHADOWS

				// 形状选项 - 球形或盒形
				#pragma multi_compile_local_fragment _ VF2_SHAPE_SPHERE

				// 密度选项 - 使用3D细节噪声或常量密度
				#pragma multi_compile_local_fragment _ VF2_DETAIL_NOISE VF2_CONSTANT_DENSITY

				// 其他可选功能
				#pragma shader_feature_local_fragment VF2_DISTANCE // 距离雾
				#pragma shader_feature_local_fragment VF2_VOIDS // 雾效空洞
				#pragma shader_feature_local_fragment VF2_FOW // 战争迷雾
				#pragma shader_feature_local_fragment VF2_SURFACE // 表面适配
				#pragma shader_feature_local_fragment VF2_DEPTH_GRADIENT // 深度渐变
				#pragma shader_feature_local_fragment VF2_HEIGHT_GRADIENT // 高度渐变
				#pragma shader_feature_local_fragment VF2_LIGHT_COOKIE // 光照Cookie

				// 散射模型选项
				#pragma shader_feature_local_fragment _ VF2_DIFFUSION_SMOOTH VF2_DIFFUSION_STRONG

				// 定义和兼容性设置
				#define UNITY_FOVEATED_RENDERING_INCLUDED
				#define _SURFACE_TYPE_TRANSPARENT

				// Unity 2022及更高版本的前向+渲染支持
				#if UNITY_VERSION >= 202200
					#pragma multi_compile_fragment _ _FORWARD_PLUS
				#endif

				#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
				#undef SAMPLE_TEXTURE2D
				#define SAMPLE_TEXTURE2D(textureName, samplerName, coord2) SAMPLE_TEXTURE2D_LOD(textureName, samplerName, coord2, 0)
				#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
				#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

				#include "Input.hlsl"
				#include "CommonsURP.hlsl"

				#if UNITY_VERSION >= 202200 && defined(FOG_LIGHT_LAYERS)
					#pragma multi_compile_fragment _ _LIGHT_LAYERS
					#include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"
				#endif

				#if UNITY_VERSION >= 202310
		            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ProbeVolumeVariants.hlsl"
					#pragma shader_feature_local_fragment VF2_APV
				#endif

				#include "Primitives.cginc"
				#include "ShadowsURP.cginc"
				#include "APV.cginc"
				#include "PointLights.cginc"
				#include "FogVoids.cginc"
				#include "FogOfWar.cginc"
				#include "FogDistance.cginc"
				#include "Surface.cginc"
				#include "Raymarch2D.cginc"

				// 顶点着色器输入结构
				struct appdata
				{
					float4 vertex : POSITION; // 顶点位置
					UNITY_VERTEX_INPUT_INSTANCE_ID // 实例ID（用于GPU实例化）
				};

				// 顶点着色器输出/片元着色器输入结构
				struct v2f
				{
					float4 pos     : SV_POSITION; // 裁剪空间位置
                    float3 wpos    : TEXCOORD0;   // 世界空间位置
					float4 scrPos  : TEXCOORD1;   // 屏幕空间位置
					UNITY_VERTEX_INPUT_INSTANCE_ID  // 实例ID
					UNITY_VERTEX_OUTPUT_STEREO     // VR立体渲染支持
				};

				// 用于强制隐藏雾效的标志
				int _ForcedInvisible;

				// 顶点着色器
				v2f vert(appdata v)
				{
					v2f o;

					// 设置实例ID和VR立体渲染
					UNITY_SETUP_INSTANCE_ID(v);
					UNITY_TRANSFER_INSTANCE_ID(v, o);
					UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

					// 转换顶点位置到裁剪空间
					o.pos = TransformObjectToHClip(v.vertex.xyz);
					// 转换顶点位置到世界空间
				    o.wpos = TransformObjectToWorld(v.vertex.xyz);
					// 计算屏幕空间坐标
					o.scrPos = ComputeScreenPos(o.pos);

					// 调整深度值以确保雾效始终在其他物体之后渲染
					#if defined(UNITY_REVERSED_Z)
						// 如果使用反转深度，则将深度值设置为接近近平面. 
						// 其中o.pos.w是裁剪空间位置的w分量,裁剪空间位置的w分量是裁剪空间位置的w坐标，表示裁剪空间位置的深度值,取值范围是0到1
						//UNITY_NEAR_CLIP_VALUE是Unity定义的宏，表示近平面值。在URP中，UNITY_NEAR_CLIP_VALUE的值为0.000005。
						o.pos.z = o.pos.w * UNITY_NEAR_CLIP_VALUE * 0.99995; //  0.99995避免在某些Android设备上由于精度问题导致的意外裁剪
					#else
						o.pos.z = o.pos.w - 0.000005;
					#endif

					// 如果强制隐藏，将顶点移出视野
					if (_ForcedInvisible == 1) {
						o.pos.xy = -10000;
                    }

					return o;
				}

				// 片元着色器
				half4 frag(v2f i) : SV_Target
				{
					// 设置实例ID和VR立体渲染
					UNITY_SETUP_INSTANCE_ID(i);
					UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

					// 获取世界空间位置
					float3 wpos = i.wpos;
					// 计算屏幕UV坐标
					float2 screenUV = i.scrPos.xy / i.scrPos.w;

					// 调用主要的雾效计算函数
					return ComputeFog(wpos, screenUV);
				}
				ENDHLSL
			}

		}
}
