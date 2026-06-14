using System;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace Salmiak.Rendering;

public sealed class BrushOverlay : IDisposable
{
    private int _vao, _vbo, _shader, _uMVP, _uColor, _uAlpha, _capacity;

    public void Render(Camera camera, float aspect, float[] xyz, int pointCount) =>
        RenderColored(camera, aspect, xyz, pointCount, PrimitiveType.LineLoop, 1.0f, 0.85f, 0.15f);

    public void RenderColored(Camera camera, float aspect, float[] xyz, int pointCount,
                              PrimitiveType prim, float r, float g, float b, float alpha = 1f)
    {
        if (pointCount < 2) return;
        if (_shader == 0) Create();

        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        int needed = pointCount * 3;
        if (needed > _capacity)
        {
            _capacity = needed;
            GL.BufferData(BufferTarget.ArrayBuffer, _capacity * sizeof(float), IntPtr.Zero, BufferUsageHint.DynamicDraw);
        }
        GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, needed * sizeof(float), xyz);

        var mvp = camera.View * camera.Projection(aspect);
        GL.Disable(EnableCap.DepthTest);
        bool blend = alpha < 0.999f;
        if (blend) { GL.Enable(EnableCap.Blend); GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha); }
        GL.UseProgram(_shader);
        GL.UniformMatrix4(_uMVP, false, ref mvp);
        GL.Uniform3(_uColor, r, g, b);
        GL.Uniform1(_uAlpha, alpha);
        GL.LineWidth(2f);
        GL.DrawArrays(prim, 0, pointCount);
        GL.BindVertexArray(0);
        if (blend) GL.Disable(EnableCap.Blend);
        GL.Enable(EnableCap.DepthTest);
    }

    private void Create()
    {
        _vao = GL.GenVertexArray();
        GL.BindVertexArray(_vao);
        _vbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);
        GL.BindVertexArray(0);

        const string vert = """
            #version 330 core
            layout(location=0) in vec3 aPos;
            uniform mat4 uMVP;
            void main() { gl_Position = uMVP * vec4(aPos, 1.0); }
            """;
        const string frag = """
            #version 330 core
            uniform vec3 uColor;
            uniform float uAlpha;
            out vec4 FragColor;
            void main() { FragColor = vec4(uColor, uAlpha); }
            """;
        int vs = Compile(ShaderType.VertexShader, vert), fs = Compile(ShaderType.FragmentShader, frag);
        _shader = GL.CreateProgram();
        GL.AttachShader(_shader, vs); GL.AttachShader(_shader, fs); GL.LinkProgram(_shader);
        GL.DetachShader(_shader, vs); GL.DetachShader(_shader, fs);
        GL.DeleteShader(vs); GL.DeleteShader(fs);
        _uMVP = GL.GetUniformLocation(_shader, "uMVP");
        _uColor = GL.GetUniformLocation(_shader, "uColor");
        _uAlpha = GL.GetUniformLocation(_shader, "uAlpha");
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
