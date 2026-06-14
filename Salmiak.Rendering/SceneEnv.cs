using OpenTK.Mathematics;

namespace Salmiak.Rendering;

public static class SceneEnv
{
    public static Vector3 HorizonColor = new(0.62f, 0.71f, 0.82f);
    public static Vector3 ZenithColor  = new(0.24f, 0.42f, 0.72f);
    public static float FogStart = 700f;
    public static float FogEnd   = 1350f;

    public static Vector3 LightTint = new(1f, 1f, 1f);
}
