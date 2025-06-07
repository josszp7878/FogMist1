using UnityEngine;
using UnityEditor;
using System.IO;

/// <summary>
/// 使用Compute Shader生成3D噪声纹理的高性能工具
/// </summary>
public class ComputeNoiseTexture3D : MonoBehaviour
{
    [Header("Compute Shader")]
    [Tooltip("用于生成3D噪声的Compute Shader")]
    public ComputeShader noiseComputeShader;
    
    [Header("纹理设置")]
    [Tooltip("3D纹理的分辨率")]
    public int resolution = 128;
    
    [Tooltip("噪声的缩放比例")]
    public float noiseScale = 5.0f;
    
    [Tooltip("噪声的阈值，用于创建更清晰的边界")]
    [Range(0.0f, 1.0f)]
    public float threshold = 0.5f;
    
    [Tooltip("噪声的锐度，值越高边界越锐利")]
    [Range(0.0f, 1.0f)]
    public float sharpness = 0.5f;
    
    [Tooltip("噪声的偏移量")]
    public Vector3 offset = Vector3.zero;
    
    [Tooltip("噪声的层数（八度）")]
    [Range(1, 8)]
    public int octaves = 4;
    
    [Tooltip("每个八度的持续度")]
    [Range(0.0f, 1.0f)]
    public float persistence = 0.5f;
    
    [Tooltip("每个八度的频率倍增")]
    public float lacunarity = 2.0f;
    
    [Header("噪声类型")]
    [Tooltip("选择噪声类型")]
    public NoiseType noiseType = NoiseType.Perlin;
    
    [Header("保存设置")]
    [Tooltip("生成的纹理的名称")]
    public string textureName = "ComputeNoiseTexture3D";
    
    [Tooltip("保存路径（相对于Assets文件夹）")]
    public string savePath = "VolumetricFog2/Resources/Textures";
    
    // 噪声类型枚举
    public enum NoiseType
    {
        Perlin,
        Simplex,
        Worley,
        PerlinWorley // 混合Perlin和Worley噪声，适合云效果
    }
    
    // Compute Shader内核ID
    private int perlinNoiseKernelId;
    private int simplexNoiseKernelId;
    private int worleyNoiseKernelId;
    private int perlinWorleyNoiseKernelId;
    
    // 用于存储结果的RenderTexture
    private RenderTexture outputTexture;
    
    /// <summary>
    /// 初始化Compute Shader
    /// </summary>
    private void InitializeComputeShader()
    {
        if (noiseComputeShader == null)
        {
            Debug.LogError("请分配Compute Shader!");
            return;
        }
        
        // 获取各种噪声类型的内核ID
        perlinNoiseKernelId = noiseComputeShader.FindKernel("PerlinNoise3D");
        simplexNoiseKernelId = noiseComputeShader.FindKernel("SimplexNoise3D");
        worleyNoiseKernelId = noiseComputeShader.FindKernel("WorleyNoise3D");
        perlinWorleyNoiseKernelId = noiseComputeShader.FindKernel("PerlinWorleyNoise3D");
    }
    
    /// <summary>
    /// 创建输出纹理
    /// </summary>
    private void CreateOutputTexture()
    {
        // 释放之前的纹理
        if (outputTexture != null)
        {
            outputTexture.Release();
        }
        
        // 创建3D渲染纹理
        outputTexture = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGB32);
        outputTexture.dimension = UnityEngine.Rendering.TextureDimension.Tex3D;
        outputTexture.volumeDepth = resolution;
        outputTexture.enableRandomWrite = true;
        outputTexture.wrapMode = TextureWrapMode.Repeat;
        outputTexture.filterMode = FilterMode.Trilinear;
        outputTexture.Create();
    }
    
    /// <summary>
    /// 生成3D噪声纹理
    /// </summary>
    [ContextMenu("生成3D纹理")]
    public void GenerateNoiseTexture()
    {
        InitializeComputeShader();
        CreateOutputTexture();
        
        // 选择合适的内核
        int kernelId;
        switch (noiseType)
        {
            case NoiseType.Simplex:
                kernelId = simplexNoiseKernelId;
                break;
            case NoiseType.Worley:
                kernelId = worleyNoiseKernelId;
                break;
            case NoiseType.PerlinWorley:
                kernelId = perlinWorleyNoiseKernelId;
                break;
            case NoiseType.Perlin:
            default:
                kernelId = perlinNoiseKernelId;
                break;
        }
        
        // 设置Compute Shader参数
        noiseComputeShader.SetTexture(kernelId, "Result", outputTexture);
        noiseComputeShader.SetInt("Resolution", resolution);
        noiseComputeShader.SetFloat("NoiseScale", noiseScale);
        noiseComputeShader.SetFloat("Threshold", threshold);
        noiseComputeShader.SetFloat("Sharpness", sharpness);
        noiseComputeShader.SetVector("Offset", offset);
        noiseComputeShader.SetInt("Octaves", octaves);
        noiseComputeShader.SetFloat("Persistence", persistence);
        noiseComputeShader.SetFloat("Lacunarity", lacunarity);
        
        // 计算线程组数量
        int threadGroupSize = 8; // 这个值应该与Compute Shader中定义的一致
        int threadGroups = Mathf.CeilToInt(resolution / (float)threadGroupSize);
        
        // 分派Compute Shader
        noiseComputeShader.Dispatch(kernelId, threadGroups, threadGroups, threadGroups);
        
        // 保存纹理
        #if UNITY_EDITOR
        SaveTextureAsAsset();
        #endif
        
        Debug.Log($"3D纹理已生成，分辨率: {resolution}³，类型: {noiseType}");
    }
    
    #if UNITY_EDITOR
    /// <summary>
    /// 将渲染纹理转换为Texture3D并保存为资源
    /// </summary>
    private void SaveTextureAsAsset()
    {
        // 创建临时的RenderTexture来读取像素
        RenderTexture tempRT = RenderTexture.GetTemporary(
            resolution, 
            resolution * resolution, 
            0, 
            RenderTextureFormat.ARGB32
        );
        
        // 将3D纹理数据复制到2D纹理中
        Graphics.CopyTexture(outputTexture, 0, 0, tempRT, 0, 0);
        
        // 创建临时的Texture2D来读取像素
        Texture2D tempTex = new Texture2D(resolution, resolution * resolution, TextureFormat.RGBA32, false);
        RenderTexture.active = tempRT;
        tempTex.ReadPixels(new Rect(0, 0, resolution, resolution * resolution), 0, 0);
        tempTex.Apply();
        RenderTexture.active = null;
        RenderTexture.ReleaseTemporary(tempRT);
        
        // 创建Texture3D并填充数据
        Texture3D texture3D = new Texture3D(resolution, resolution, resolution, TextureFormat.RGBA32, false);
        Color[] pixels = tempTex.GetPixels();
        texture3D.SetPixels(pixels);
        texture3D.wrapMode = TextureWrapMode.Repeat;
        texture3D.filterMode = FilterMode.Trilinear;
        texture3D.Apply();
        
        // 销毁临时纹理
        DestroyImmediate(tempTex);
        
        // 确保目录存在
        string directory = $"Assets/{savePath}";
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
        
        // 保存纹理
        string path = $"{directory}/{textureName}_{noiseType}.asset";
        AssetDatabase.CreateAsset(texture3D, path);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        
        Debug.Log($"3D纹理已保存到: {path}");
        
        // 选中新创建的资源
        Selection.activeObject = texture3D;
    }
    #endif
    
    /// <summary>
    /// 在编辑器中添加一个按钮来生成纹理
    /// </summary>
    #if UNITY_EDITOR
    [CustomEditor(typeof(ComputeNoiseTexture3D))]
    public class ComputeNoiseTexture3DEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            
            ComputeNoiseTexture3D generator = (ComputeNoiseTexture3D)target;
            
            EditorGUILayout.Space();
            if (GUILayout.Button("生成3D纹理"))
            {
                generator.GenerateNoiseTexture();
            }
        }
    }
    #endif
}
