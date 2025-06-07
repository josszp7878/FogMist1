using UnityEngine;
using UnityEditor;

/// <summary>
/// 用于生成3D噪声纹理的工具类
/// </summary>
public class NoiseTexture3DGenerator : MonoBehaviour
{
    [Header("纹理设置")]
    [Tooltip("3D纹理的分辨率")]
    public int resolution = 64;
    
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
    
    [Header("保存设置")]
    [Tooltip("生成的纹理的名称")]
    public string textureName = "NoiseTexture3D";
    
    [Tooltip("保存路径（相对于Assets文件夹）")]
    public string savePath = "VolumetricFog2/Resources/Textures";
    
    /// <summary>
    /// 生成3D噪声纹理并保存为资源
    /// </summary>
    [ContextMenu("生成3D纹理")]
    public void GenerateAndSaveTexture()
    {
        // 创建3D纹理
        Texture3D texture = new Texture3D(resolution, resolution, resolution, TextureFormat.RGBA32, false);
        texture.wrapMode = TextureWrapMode.Repeat;
        texture.filterMode = FilterMode.Trilinear;
        
        Color[] colors = new Color[resolution * resolution * resolution];
        
        // 生成噪声
        for (int z = 0; z < resolution; z++)
        {
            for (int y = 0; y < resolution; y++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    // 计算3D噪声
                    float noiseValue = Generate3DPerlinNoise(x, y, z);
                    
                    // 应用阈值和锐化
                    float density = ApplyThresholdAndSharpness(noiseValue);
                    
                    // 设置颜色（RGBA都使用相同的值）
                    Color color = new Color(density, density, density, density);
                    int index = x + y * resolution + z * resolution * resolution;
                    colors[index] = color;
                }
            }
        }
        
        texture.SetPixels(colors);
        texture.Apply();
        
        // 保存纹理
        #if UNITY_EDITOR
        SaveTextureAsAsset(texture);
        #endif
        
        Debug.Log($"3D纹理已生成，分辨率: {resolution}³");
    }
    
    /// <summary>
    /// 生成3D Perlin噪声
    /// </summary>
    private float Generate3DPerlinNoise(int x, int y, int z)
    {
        float xCoord = (float)x / resolution * noiseScale + offset.x;
        float yCoord = (float)y / resolution * noiseScale + offset.y;
        float zCoord = (float)z / resolution * noiseScale + offset.z;
        
        float noise = 0.0f;
        float amplitude = 1.0f;
        float frequency = 1.0f;
        float totalAmplitude = 0.0f;
        
        // 叠加多层噪声（FBM - Fractal Brownian Motion）
        for (int i = 0; i < octaves; i++)
        {
            // 由于Unity的Mathf.PerlinNoise只支持2D，我们需要组合多个2D噪声来模拟3D
            float xy = Mathf.PerlinNoise(xCoord * frequency, yCoord * frequency);
            float yz = Mathf.PerlinNoise(yCoord * frequency, zCoord * frequency);
            float xz = Mathf.PerlinNoise(xCoord * frequency, zCoord * frequency);
            float yx = Mathf.PerlinNoise(yCoord * frequency, xCoord * frequency);
            float zy = Mathf.PerlinNoise(zCoord * frequency, yCoord * frequency);
            float zx = Mathf.PerlinNoise(zCoord * frequency, xCoord * frequency);
            
            // 组合所有噪声
            float n = (xy + yz + xz + yx + zy + zx) / 6.0f;
            
            noise += n * amplitude;
            totalAmplitude += amplitude;
            
            // 为下一个八度准备振幅和频率
            amplitude *= persistence;
            frequency *= lacunarity;
        }
        
        // 归一化
        return noise / totalAmplitude;
    }
    
    /// <summary>
    /// 应用阈值和锐化效果
    /// </summary>
    private float ApplyThresholdAndSharpness(float noiseValue)
    {
        // 应用阈值
        float density = noiseValue;
        
        if (threshold > 0.0f)
        {
            // 重新映射到[0,1]，基于阈值
            density = Mathf.Clamp01((noiseValue - threshold) / (1.0f - threshold));
        }
        
        // 应用锐化
        if (sharpness > 0.0f)
        {
            // 使用幂函数增加对比度
            density = Mathf.Pow(density, 1.0f + sharpness * 10.0f);
        }
        
        return density;
    }
    
    #if UNITY_EDITOR
    /// <summary>
    /// 将纹理保存为资源文件
    /// </summary>
    private void SaveTextureAsAsset(Texture3D texture)
    {
        // 确保目录存在
        string directory = $"Assets/{savePath}";
        if (!System.IO.Directory.Exists(directory))
        {
            System.IO.Directory.CreateDirectory(directory);
        }
        
        // 保存纹理
        string path = $"{directory}/{textureName}.asset";
        AssetDatabase.CreateAsset(texture, path);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        
        Debug.Log($"3D纹理已保存到: {path}");
        
        // 选中新创建的资源
        Selection.activeObject = texture;
    }
    #endif
    
    /// <summary>
    /// 在编辑器中添加一个按钮来生成纹理
    /// </summary>
    #if UNITY_EDITOR
    [CustomEditor(typeof(NoiseTexture3DGenerator))]
    public class NoiseTexture3DGeneratorEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            
            NoiseTexture3DGenerator generator = (NoiseTexture3DGenerator)target;
            
            EditorGUILayout.Space();
            if (GUILayout.Button("生成3D纹理"))
            {
                generator.GenerateAndSaveTexture();
            }
        }
    }
    #endif
}
