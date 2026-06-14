using System;
using OpenTK.Mathematics;

namespace Salmiak.Rendering;

public sealed class Camera
{
    public Vector3 Position { get; set; } = new(0, 0, 200);
    public float Yaw   { get; set; } = -MathF.PI / 2f;
    public float Pitch { get; set; } = -0.5f;

    public float MoveSpeed  { get; set; } = 100f;
    public float MouseSensitivity { get; set; } = 0.003f;

    public Matrix4? ViewOverride { get; set; }

    public Matrix4? ProjectionOverride { get; set; }

    public Matrix4 View => ViewOverride ?? Matrix4.LookAt(Position, Position + Forward, Vector3.UnitY);

    public Matrix4 Projection(float aspectRatio) => ProjectionOverride ??
        Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(60f), aspectRatio, 0.5f, 10000f);

    public Vector3 Forward
    {
        get
        {
            var x = MathF.Cos(Pitch) * MathF.Cos(Yaw);
            var y = MathF.Sin(Pitch);
            var z = MathF.Cos(Pitch) * MathF.Sin(Yaw);
            return Vector3.Normalize(new Vector3(x, y, z));
        }
    }

    public Vector3 Right => Vector3.Normalize(Vector3.Cross(Forward, Vector3.UnitY));

    public void Move(float dx, float dy, float dz, float deltaTime)
    {
        Position += Forward * dz * MoveSpeed * deltaTime
                  + Right   * dx * MoveSpeed * deltaTime
                  + Vector3.UnitY * dy * MoveSpeed * deltaTime;
    }

    public void Rotate(float deltaX, float deltaY)
    {
        Yaw   += deltaX * MouseSensitivity;
        Pitch -= deltaY * MouseSensitivity;
        Pitch  = Math.Clamp(Pitch, -MathF.PI / 2f + 0.01f, MathF.PI / 2f - 0.01f);
    }
}
