using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Wpf;
using Salmiak.Core.Formats;
using Salmiak.Core.IO;
using Salmiak.Rendering;

namespace Salmiak;

public sealed partial class GlViewport
{
    private readonly Camera _camera = new();

    public event Action<float>? CameraSpeedChanged;

    public float CameraSpeed => _camera.MoveSpeed;

    private static Vector3 WorldToGl((float X, float Y, float Z) w) => new(-w.Y, w.Z, -w.X);
    private static Vector3 WorldToGl(Salmiak.Core.Formats.FlightPaths.Waypoint w) => new(-w.Y, w.Z, -w.X);
    private static (float X, float Y, float Z) GlToWorld(Vector3 g) => (-g.Z, -g.X, g.Y);

    private static readonly Vector3 TeleportCamOffset = new(0f, 350f, 300f);

    public void TeleportTo(float glx, float gly, float glz)
    {
        var target = new Vector3(glx, gly, glz);
        var cam = target + TeleportCamOffset;
        _camera.Position = cam;
        var dir = Vector3.Normalize(target - cam);
        _camera.Pitch = MathF.Asin(Math.Clamp(dir.Y, -1f, 1f));
        _camera.Yaw = MathF.Atan2(dir.Z, dir.X);
        RefreshStreaming();
        CameraUpdated?.Invoke(_camera.Position);
    }

    public void TeleportCameraTo(float glx, float gly, float glz)
        => TeleportTo(glx - TeleportCamOffset.X, gly, glz - TeleportCamOffset.Z);

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    [System.ComponentModel.Browsable(false)]
    public (Vector3 Position, float Yaw, float Pitch) CameraPose
    {
        get => (_camera.Position, _camera.Yaw, _camera.Pitch);
        set
        {
            _camera.Position = value.Position;
            _camera.Yaw = value.Yaw;
            _camera.Pitch = value.Pitch;
            CameraUpdated?.Invoke(_camera.Position);
        }
    }

    private (int X, int Y) CameraTile()
    {
        const float tileSize = AdtFile.TileSize;
        float northX = -_camera.Position.Z;
        float westY  = -_camera.Position.X;
        int tileX = (int)MathF.Floor(32f - westY  / tileSize);
        int tileY = (int)MathF.Floor(32f - northX / tileSize);
        return (tileX, tileY);
    }
}
