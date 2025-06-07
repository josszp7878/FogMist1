# Volumetric Fog 2 着色器处理流程

## 核心渲染流程

```mermaid
flowchart TD
    subgraph 渲染入口
        A[VolumetricFog2DURP.shader<br>片元着色器frag]
        A --> B[ComputeFog]
    end

    subgraph 光线步进核心流程
        B --> C[GetRayStart<br>获取光线起点]
        C --> D[计算光线方向]
        D --> E[计算光线与体积交点]
        E --> F[裁剪光线深度<br>CLAMP_RAY_DEPTH]
        F --> G[GetFogColor<br>执行光线步进]
    end

    subgraph 光线步进处理
        G --> H[计算光散射<br>GetDiffusionColor]
        H --> I[设置光线参数]
        I --> J[循环光线步进]
        J --> K[AddFog<br>添加单点雾效]
        K --> L{透明度>0.99?}
        L -- 是 --> M[提前退出循环]
        L -- 否 --> J
    end
```

## 雾效采样和计算流程

```mermaid
flowchart TD
    subgraph AddFog函数流程
        A[AddFog<br>处理单个采样点]
        A --> B[SampleDensity<br>采样噪声密度]
        B --> C{密度>0?}
        C -- 否 --> D[跳过计算]
        C -- 是 --> E[计算雾效基础颜色]
    end
    
    subgraph 光照处理
        E --> F{启用阴影?}
        F -- 是 --> G[应用阴影<br>GetLightAttenuation]
        F -- 否 --> H
        G --> H{启用原生光源?}
        H -- 是 --> I[添加其他光源贡献]
        H -- 否 --> J
        I --> J{启用APV?}
        J -- 是 --> K[添加APV颜色<br>GetAPVColor]
        J -- 否 --> L
        K --> L{启用光照Cookie?}
        L -- 是 --> M[应用Cookie纹理<br>SampleMainLightCookie]
        L -- 否 --> N
    end
    
    subgraph 渐变和累积
        M --> N{启用深度渐变?}
        N -- 是 --> O[ApplyDepthGradient]
        N -- 否 --> P
        O --> P{启用高度渐变?}
        P -- 是 --> Q[ApplyHeightGradient]
        P -- 否 --> R
        Q --> R[应用战争迷雾<br>ApplyFogOfWar]
        R --> S[应用能量步长]
        S --> T[累积雾效颜色]
    end
```

## 密度采样和噪声处理

```mermaid
flowchart TD
    subgraph SampleDensity函数流程
        A[SampleDensity<br>获取给定点的雾效密度]
        A --> B{使用细节噪声?}
        
        B -- 是 --> C[从3D纹理采样<br>细节噪声]
        C --> D{使用基础噪声?}
        D -- 是 --> E[从2D纹理采样<br>基础噪声]
        D -- 否 --> F[只使用细节噪声]
        E --> G[根据高度减少密度]
        F --> H[合并噪声结果]
        
        B -- 否 --> I{使用常量密度?}
        I -- 是 --> J[使用固定密度值]
        I -- 否 --> K[从2D纹理采样<br>基础噪声]
        J --> L[根据高度减少密度]
        K --> L
    end
```

## 光散射模型处理

```mermaid
flowchart TD
    subgraph 光散射计算
        A[GetDiffusionColor<br>计算光散射颜色]
        A --> B[GetDiffusionIntensity<br>计算散射强度]
        B --> C[计算视线方向与光源夹角]
        C --> D{选择散射模型}
        
        D -- 平滑散射 --> E[Henyey-Greenstein<br>相位函数]
        D -- 强散射 --> F[Mie散射<br>相位函数]
        D -- 简单散射 --> G[余弦幂<br>相位函数]
        
        E --> H[应用散射强度系数]
        F --> H
        G --> H
        
        H --> I[根据距离调整散射]
        I --> J[生成最终散射颜色]
    end
```

## 完整的光线步进过程

```mermaid
flowchart TD
    Start[开始] --> Init[初始化VolumetricFog2DURP]
    Init --> Setup[设置渲染参数]
    Setup --> Vert[顶点着色器<br>计算世界/屏幕坐标]
    Vert --> Frag[片元着色器]
    Frag --> Ray[计算光线]
    Ray --> Intersect[与雾效体积相交]
    Intersect --> ClampDepth[裁剪深度]
    ClampDepth --> Step[光线步进循环]
    
    subgraph 每步迭代
        Step --> Sample[SampleDensity<br>采样当前点密度]
        Sample --> Light[计算光照与散射]
        Light --> Shadow[处理阴影]
        Shadow --> Accumulate[累积颜色与透明度]
        Accumulate --> Check{检查透明度}
        Check -- >0.99 --> Exit[提前退出]
        Check -- <=0.99 --> Next[移动到下一点]
        Next --> Sample
    end
    
    Step --> FinalBlend[最终颜色混合]
    FinalBlend --> Output[输出到屏幕]
```

## 核心函数说明

| 函数名 | 文件 | 作用 |
|--------|------|------|
| `ComputeFog` | Raymarch2D.cginc | 主要雾效计算函数，处理光线与雾效体积的交互 |
| `GetFogColor` | Raymarch2D.cginc | 执行实际的光线步进，累积雾效颜色 |
| `AddFog` | Raymarch2D.cginc | 在光线步进中的单个采样点添加雾效贡献 |
| `SampleDensity` | Raymarch2D.cginc | 采样给定世界空间位置的雾效密度 |
| `GetDiffusionIntensity` | Raymarch2D.cginc | 根据视线方向计算散射强度 |
| `GetDiffusionColor` | Raymarch2D.cginc | 计算最终的散射光颜色 |
| `frag` | VolumetricFog2DURP.shader | 片元着色器，渲染入口点 |
| `vert` | VolumetricFog2DURP.shader | 顶点着色器，准备顶点数据 |

## 性能优化点

1. **自适应步长**: 根据距离动态调整步长，远处使用更大的步长以提高性能
2. **提前退出**: 当雾效完全不透明时提前结束光线步进
3. **降采样渲染**: 可选的降采样提高性能
4. **条件编译**: 使用预处理指令有选择地编译需要的功能

## 主要特效控制点

1. **噪声纹理**: 控制雾效的形状和细节
2. **光散射模型**: 控制光在雾中的散射方式 (简单/平滑/强散射)
3. **深度和高度渐变**: 根据深度和高度改变雾效的外观
4. **阴影接收**: 允许雾效接收和显示场景中的阴影
5. **风向动画**: 通过偏移噪声纹理UV创造风吹动的效果 