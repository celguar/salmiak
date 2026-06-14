using System;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace Salmiak.Rendering;

public sealed class GizmoRenderer : IDisposable
{
    private const int Seg = 48;
    public const float TranslateLen = 1.4f;
    public const float ScalePos = 0.8f;
    private int _shader, _uMVP, _uColor, _vao, _vbo;

    private int OffRings => 0;
    private int OffTransLines => Seg * 3;
    private int OffTransTips => Seg * 3 + 6;
    private int OffScale => Seg * 3 + 9;
    private int OffCenter => Seg * 3 + 10;

    private static Vector3 AxisColor(int axis) => axis switch
    {
        0 => new Vector3(0.4f, 0.6f, 1f),
        1 => new Vector3(0.4f, 1f, 0.4f),
        _ => new Vector3(1f, 0.35f, 0.35f),
    };
    private static readonly Vector3 Hot = new(1f, 0.95f, 0.4f);

    public void EnsureCreated()
    {
        if (_shader != 0) return;
        BuildShader();
        BuildGeometry();
    }

    private void BuildGeometry()
    {
        var verts = new float[(Seg * 3 + 6 + 3 + 1 + 1) * 3];
        int o = 0;
        for (int axis = 0; axis < 3; axis++)
        for (int i = 0; i < Seg; i++)
        {
            float a = MathF.PI * 2f * i / Seg;
            float c = MathF.Cos(a), s = MathF.Sin(a);
            float x = 0, y = 0, z = 0;
            if (axis == 0) { y = c; z = s; }
            else if (axis == 1) { x = c; z = s; }
            else { x = c; y = s; }
            verts[o++] = x; verts[o++] = y; verts[o++] = z;
        }
        for (int axis = 0; axis < 3; axis++)
        {
            verts[o++] = 0; verts[o++] = 0; verts[o++] = 0;
            verts[o++] = axis == 0 ? TranslateLen : 0;
            verts[o++] = axis == 1 ? TranslateLen : 0;
            verts[o++] = axis == 2 ? TranslateLen : 0;
        }
        for (int axis = 0; axis < 3; axis++)
        {
            verts[o++] = axis == 0 ? TranslateLen : 0;
            verts[o++] = axis == 1 ? TranslateLen : 0;
            verts[o++] = axis == 2 ? TranslateLen : 0;
        }
        verts[o++] = ScalePos; verts[o++] = ScalePos; verts[o++] = ScalePos;
        verts[o++] = 0f; verts[o++] = 0f; verts[o++] = 0f;

        _vao = GL.GenVertexArray();
        GL.BindVertexArray(_vao);
        _vbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, verts.Length * sizeof(float), verts, BufferUsageHint.StaticDraw);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);
        GL.BindVertexArray(0);
    }

    public void Render(Camera camera, float aspect, Vector3 center, float radius, int activeAxis, bool showScale)
    {
        EnsureCreated();
        var vp = camera.View * camera.Projection(aspect);
        var model = Matrix4.CreateScale(radius) * Matrix4.CreateTranslation(center);
        var mvp = model * vp;

        GL.UseProgram(_shader);
        GL.UniformMatrix4(_uMVP, false, ref mvp);
        GL.Disable(EnableCap.DepthTest);
        GL.BindVertexArray(_vao);
        GL.LineWidth(2f);

        for (int axis = 0; axis < 3; axis++)
        {
            var col = axis == activeAxis ? Hot : AxisColor(axis);
            GL.Uniform3(_uColor, ref col);
            GL.DrawArrays(PrimitiveType.LineLoop, OffRings + axis * Seg, Seg);
        }

        for (int axis = 0; axis < 3; axis++)
        {
            var col = (4 + axis) == activeAxis ? Hot : AxisColor(axis);
            GL.Uniform3(_uColor, ref col);
            GL.DrawArrays(PrimitiveType.Lines, OffTransLines + axis * 2, 2);
            GL.PointSize(11f);
            GL.DrawArrays(PrimitiveType.Points, OffTransTips + axis, 1);
        }

        if (showScale)
        {
            var col = activeAxis == 3 ? Hot : new Vector3(0.9f, 0.9f, 0.9f);
            GL.Uniform3(_uColor, ref col);
            GL.PointSize(13f);
            GL.DrawArrays(PrimitiveType.Points, OffScale, 1);
        }

        var centreCol = activeAxis == 7 ? Hot : new Vector3(0.85f, 0.85f, 0.9f);
        GL.Uniform3(_uColor, ref centreCol);
        GL.PointSize(10f);
        GL.DrawArrays(PrimitiveType.Points, OffCenter, 1);

        GL.BindVertexArray(0);
        GL.Enable(EnableCap.DepthTest);
    }

    private void BuildShader()
    {
        const string vert = """
            #version 330 core
            layout(location=0) in vec3 aPos;
            uniform mat4 uMVP;
            void main() { gl_Position = uMVP * vec4(aPos, 1.0); }
            """;
        const string frag = """
            #version 330 core
            uniform vec3 uColor;
            out vec4 FragColor;
            void main() { FragColor = vec4(uColor, 1.0); }
            """;
        int vs = GL.CreateShader(ShaderType.VertexShader); GL.ShaderSource(vs, vert); GL.CompileShader(vs);
        GL.GetShader(vs, ShaderParameter.CompileStatus, out int okV); if (okV == 0) throw new Exception(GL.GetShaderInfoLog(vs));
        int fs = GL.CreateShader(ShaderType.FragmentShader); GL.ShaderSource(fs, frag); GL.CompileShader(fs);
        GL.GetShader(fs, ShaderParameter.CompileStatus, out int okF); if (okF == 0) throw new Exception(GL.GetShaderInfoLog(fs));
        _shader = GL.CreateProgram();
        GL.AttachShader(_shader, vs); GL.AttachShader(_shader, fs); GL.LinkProgram(_shader);
        GL.DetachShader(_shader, vs); GL.DetachShader(_shader, fs);
        GL.DeleteShader(vs); GL.DeleteShader(fs);
        _uMVP = GL.GetUniformLocation(_shader, "uMVP");
        _uColor = GL.GetUniformLocation(_shader, "uColor");
    }

    public void Dispose()
    {
        if (_vbo != 0) GL.DeleteBuffer(_vbo);
        if (_vao != 0) GL.DeleteVertexArray(_vao);
        if (_shader != 0) GL.DeleteProgram(_shader);
        _vbo = _vao = _shader = 0;
    }
}
