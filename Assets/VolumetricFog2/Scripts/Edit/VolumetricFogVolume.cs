using UnityEngine;

public enum FogVolumeShape { Box, Sphere }
public enum FogFalloffMode { Linear, Exp, Exp2 }
public enum FogBlendMode { Add, Overwrite, Multiply }

[ExecuteAlways]
[DisallowMultipleComponent]
[AddComponentMenu("Rendering/Volumetric Fog Volume")]
public class VolumetricFogVolume : MonoBehaviour
{
    public FogVolumeShape shape = FogVolumeShape.Box;
    [Range(0, 1)]
    public float density = 0.5f;
    public Color color = Color.white;
    public FogFalloffMode falloffMode = FogFalloffMode.Linear;
    public float falloffDistance = 10f;
    [Range(0, 1)]
    public float noiseIntensity = 0f;
    public float noiseScale = 1f;
    public FogBlendMode blendMode = FogBlendMode.Add;
    public bool visibleInEditor = true;

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        if (!visibleInEditor) return;
        Gizmos.color = new Color(color.r, color.g, color.b, 0.3f);
        if (shape == FogVolumeShape.Box)
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
            Gizmos.DrawCube(Vector3.zero, Vector3.one);
        }
        else if (shape == FogVolumeShape.Sphere)
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireSphere(Vector3.zero, 1);
            Gizmos.DrawSphere(Vector3.zero, 1);
        }
    }
#endif
} 