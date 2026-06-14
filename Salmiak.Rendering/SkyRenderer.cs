using System;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace Salmiak.Rendering;

public sealed class SkyRenderer : IDisposable
{
    private int _vao, _vbo, _shader;
    private int _uForward, _uRight, _uUp, _uTanAspect, _uHorizon, _uZenith;

    public void Render(Camera camera, float aspect)
    {
        if (_shader == 0) Create();

        var fwd = camera.Forward;
        var right = camera.Right;
        var up = Vector3.Normalize(Vector3.Cross(right, fwd));
        float tanHalf = MathF.Tan(MathHelper.DegreesToRadians(60f) * 0.5f);

        GL.Disable(EnableCap.DepthTest);
        GL.DepthMask(false);
        GL.UseProgram(_shader);
        GL.Uniform3(_uForward, ref fwd);
        GL.Uniform3(_uRight, ref right);
        GL.Uniform3(_uUp, ref up);
        GL.Uniform2(_uTanAspect, tanHalf * aspect, tanHalf);
        var hz = SceneEnv.HorizonColor; var ze = SceneEnv.ZenithColor;
        GL.Uniform3(_uHorizon, ref hz);
        GL.Uniform3(_uZenith, ref ze);

        GL.BindVertexArray(_vao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        GL.BindVertexArray(0);

        GL.DepthMask(true);
        GL.Enable(EnableCap.DepthTest);
    }

    private void Create()
    {
        float[] verts = { -1f, -1f, 3f, -1f, -1f, 3f };
        _vao = GL.GenVertexArray();
        GL.BindVertexArray(_vao);
        _vbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, verts.Length * sizeof(float), verts, BufferUsageHint.StaticDraw);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);
        GL.BindVertexArray(0);

        const string vert = """
            #version 330 core
            layout(location=0) in vec2 aPos;
            out vec2 vNdc;
            void main() { vNdc = aPos; gl_Position = vec4(aPos, 0.0, 1.0); }
            """;
        const string frag = """
            #version 330 core
            in vec2 vNdc;
            uniform vec3 uForward, uRight, uUp;
            uniform vec2 uTanAspect;
            uniform vec3 uHorizon, uZenith;
            out vec4 FragColor;
            void main() {
                vec3 dir = normalize(uForward + vNdc.x * uTanAspect.x * uRight + vNdc.y * uTanAspect.y * uUp);
                float t = clamp(dir.y * 1.5, 0.0, 1.0);
                FragColor = vec4(mix(uHorizon, uZenith, t), 1.0);
            }
            """;

        int vs = Compile(ShaderType.VertexShader, vert), fs = Compile(ShaderType.FragmentShader, frag);
        _shader = GL.CreateProgram();
        GL.AttachShader(_shader, vs); GL.AttachShader(_shader, fs); GL.LinkProgram(_shader);
        GL.DetachShader(_shader, vs); GL.DetachShader(_shader, fs);
        GL.DeleteShader(vs); GL.DeleteShader(fs);
        _uForward = GL.GetUniformLocation(_shader, "uForward");
        _uRight = GL.GetUniformLocation(_shader, "uRight");
        _uUp = GL.GetUniformLocation(_shader, "uUp");
        _uTanAspect = GL.GetUniformLocation(_shader, "uTanAspect");
        _uHorizon = GL.GetUniformLocation(_shader, "uHorizon");
        _uZenith = GL.GetUniformLocation(_shader, "uZenith");
    }

    private static int Compile(ShaderType t, string src)
    {
        int id = GL.CreateShader(t); GL.ShaderSource(id, src); GL.CompileShader(id);
        GL.GetShader(id, ShaderParameter.CompileStatus, out int ok);
        if (ok == 0) throw new Exception(GL.GetShaderInfoLog(id));
        return id;
    }

    public void Dispose()
    {
        if (_vao != 0) { GL.DeleteVertexArray(_vao); GL.DeleteBuffer(_vbo); }
        if (_shader != 0) GL.DeleteProgram(_shader);
        _shader = 0;
    }
}
