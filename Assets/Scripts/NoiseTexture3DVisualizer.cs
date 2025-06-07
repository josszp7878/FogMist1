using UnityEngine;

/// <summary>
/// 用于可视化3D噪声纹理的组件
/// </summary>
public class NoiseTexture3DVisualizer : MonoBehaviour
{
    [Header("纹理设置")]
    [Tooltip("要可视化的3D纹理")]
    public Texture3D noiseTexture;
    
    [Tooltip("切片的数量")]
    [Range(1, 100)]
    public int sliceCount = 32;
    
    [Tooltip("切片之间的距离")]
    public float sliceDistance = 0.1f;
    
    [Header("可视化设置")]
    [Tooltip("是否使用颜色渐变")]
    public bool useGradient = true;
    
    [Tooltip("颜色渐变")]
    public Gradient colorGradient;
    
    [Tooltip("是否旋转")]
    public bool rotate = true;
    
    [Tooltip("旋转速度")]
    public float rotationSpeed = 10f;
    
    [Tooltip("是否动画化")]
    public bool animate = true;
    
    [Tooltip("动画速度")]
    public float animationSpeed = 0.5f;
    
    [Tooltip("动画偏移")]
    private Vector3 animationOffset = Vector3.zero;
    
    // 切片游戏对象
    private GameObject[] slices;
    
    // 材质
    private Material sliceMaterial;
    
    void Start()
    {
        // 创建材质
        sliceMaterial = new Material(Shader.Find("Unlit/Transparent"));
        
        if (noiseTexture != null)
        {
            // 设置纹理
            sliceMaterial.SetTexture("_MainTex", noiseTexture);
            
            // 创建切片
            CreateSlices();
        }
        else
        {
            Debug.LogError("请分配3D噪声纹理!");
        }
    }
    
    void Update()
    {
        if (slices == null || noiseTexture == null)
            return;
        
        // 旋转
        if (rotate)
        {
            transform.Rotate(0, rotationSpeed * Time.deltaTime, 0);
        }
        
        // 动画
        if (animate)
        {
            animationOffset += new Vector3(
                animationSpeed * Time.deltaTime,
                animationSpeed * Time.deltaTime,
                animationSpeed * Time.deltaTime
            );
            
            UpdateSlices();
        }
    }
    
    /// <summary>
    /// 创建可视化切片
    /// </summary>
    private void CreateSlices()
    {
        // 销毁现有切片
        if (slices != null)
        {
            foreach (var slice in slices)
            {
                if (slice != null)
                    Destroy(slice);
            }
        }
        
        // 创建新切片
        slices = new GameObject[sliceCount];
        
        for (int i = 0; i < sliceCount; i++)
        {
            // 创建平面
            slices[i] = GameObject.CreatePrimitive(PrimitiveType.Quad);
            slices[i].transform.SetParent(transform);
            slices[i].transform.localPosition = new Vector3(0, 0, i * sliceDistance);
            slices[i].transform.localRotation = Quaternion.identity;
            slices[i].transform.localScale = Vector3.one;
            
            // 设置材质
            Material sliceMat = new Material(sliceMaterial);
            slices[i].GetComponent<Renderer>().material = sliceMat;
            
            // 设置纹理坐标
            float z = (float)i / (sliceCount - 1);
            sliceMat.SetFloat("_SliceZ", z);
        }
        
        // 更新切片
        UpdateSlices();
    }
    
    /// <summary>
    /// 更新切片的纹理和颜色
    /// </summary>
    private void UpdateSlices()
    {
        if (slices == null || noiseTexture == null)
            return;
        
        for (int i = 0; i < sliceCount; i++)
        {
            if (slices[i] == null)
                continue;
            
            // 获取材质
            Material mat = slices[i].GetComponent<Renderer>().material;
            
            // 计算Z坐标
            float z = (float)i / (sliceCount - 1);
            
            // 采样3D纹理
            float noiseValue = SampleNoiseTexture(0.5f, 0.5f, z);
            
            // 设置颜色
            if (useGradient)
            {
                mat.color = colorGradient.Evaluate(noiseValue);
            }
            else
            {
                mat.color = new Color(noiseValue, noiseValue, noiseValue, noiseValue);
            }
        }
    }
    
    /// <summary>
    /// 采样3D噪声纹理
    /// </summary>
    private float SampleNoiseTexture(float x, float y, float z)
    {
        // 应用动画偏移
        x = (x + animationOffset.x) % 1.0f;
        y = (y + animationOffset.y) % 1.0f;
        z = (z + animationOffset.z) % 1.0f;
        
        // 采样纹理
        Color color = noiseTexture.GetPixelBilinear(x, y, z);
        
        // 返回红色通道作为噪声值
        return color.r;
    }
    
    /// <summary>
    /// 在编辑器中添加一个按钮来重新创建切片
    /// </summary>
    #if UNITY_EDITOR
    [UnityEditor.CustomEditor(typeof(NoiseTexture3DVisualizer))]
    public class NoiseTexture3DVisualizerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            
            NoiseTexture3DVisualizer visualizer = (NoiseTexture3DVisualizer)target;
            
            UnityEditor.EditorGUILayout.Space();
            if (GUILayout.Button("重新创建切片"))
            {
                visualizer.CreateSlices();
            }
        }
    }
    #endif
}
