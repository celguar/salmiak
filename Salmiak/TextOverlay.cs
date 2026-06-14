using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL4;

namespace Salmiak;

public sealed class TextOverlay : IDisposable
{
    private int _shader, _vao, _vbo, _uViewport, _uColor, _uUseTex, _uTexUnit;
    private readonly List<float> _verts = new();

    public TextOverlay()
    {
        BuildShader();
        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 2 * sizeof(float));
        GL.EnableVertexAttribArray(1);
        GL.BindVertexArray(0);
    }

    public void DrawRect(float x, float y, float w, float h, float r, float g, float b, float a,
                         int viewportW, int viewportH)
    {
        _verts.Clear();
        float x1 = x + w, y1 = y + h;
        _verts.AddRange(new[] { x, y, 0f, 0f,  x1, y, 0f, 0f,  x1, y1, 0f, 0f });
        _verts.AddRange(new[] { x, y, 0f, 0f,  x1, y1, 0f, 0f,  x, y1, 0f, 0f });
        Flush(0, 0, r, g, b, a, viewportW, viewportH);
    }

    public void DrawImage(int texId, float x, float y, float w, float h, float alpha, int viewportW, int viewportH)
    {
        _verts.Clear();
        float x1 = x + w, y1 = y + h;
        _verts.AddRange(new[] { x, y, 0f, 0f,  x1, y, 1f, 0f,  x1, y1, 1f, 1f });
        _verts.AddRange(new[] { x, y, 0f, 0f,  x1, y1, 1f, 1f,  x, y1, 0f, 1f });
        Flush(2, texId, 1f, 1f, 1f, alpha, viewportW, viewportH);
    }

    private void Flush(int mode, int texId, float r, float g, float b, float a, int vw, int vh)
    {
        if (_verts.Count == 0) return;
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.Disable(EnableCap.DepthTest);
        GL.UseProgram(_shader);
        GL.Uniform2(_uViewport, (float)vw, (float)vh);
        GL.Uniform4(_uColor, r, g, b, a);
        GL.Uniform1(_uUseTex, mode);
        if (mode != 0) { GL.Uniform1(_uTexUnit, 0); GL.ActiveTexture(TextureUnit.Texture0); GL.BindTexture(TextureTarget.Texture2D, texId); }
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        var arr = _verts.ToArray();
        GL.BufferData(BufferTarget.ArrayBuffer, arr.Length * sizeof(float), arr, BufferUsageHint.StreamDraw);
        GL.DrawArrays(PrimitiveType.Triangles, 0, arr.Length / 4);
        GL.BindVertexArray(0);
        GL.Enable(EnableCap.DepthTest);
        GL.Disable(EnableCap.Blend);
    }

    private void BuildShader()
    {
        const string vert = """
            #version 330 core
            layout(location=0) in vec2 aPos;
            layout(location=1) in vec2 aUV;
            uniform vec2 uViewport;
            out vec2 vUV;
            void main() {
                vUV = aUV;
                float nx = aPos.x / uViewport.x * 2.0 - 1.0;
                float ny = 1.0 - aPos.y / uViewport.y * 2.0;
                gl_Position = vec4(nx, ny, 0.0, 1.0);
            }
            """;
        const string frag = """
            #version 330 core
            in vec2 vUV;
            uniform sampler2D uTex;
            uniform vec4 uColor;
            uniform int uUseTex;
            out vec4 FragColor;
            void main() {
                if (uUseTex == 2) { FragColor = vec4(texture(uTex, vUV).rgb, uColor.a); return; }
                FragColor = uColor;
            }
            """;
        int vs = GL.CreateShader(ShaderType.VertexShader); GL.ShaderSource(vs, vert); GL.CompileShader(vs);
        GL.GetShader(vs, ShaderParameter.CompileStatus, out int okV); if (okV == 0) throw new Exception(GL.GetShaderInfoLog(vs));
        int fs = GL.CreateShader(ShaderType.FragmentShader); GL.ShaderSource(fs, frag); GL.CompileShader(fs);
        GL.GetShader(fs, ShaderParameter.CompileStatus, out int okF); if (okF == 0) throw new Exception(GL.GetShaderInfoLog(fs));
        _shader = GL.CreateProgram();
        GL.AttachShader(_shader, vs); GL.AttachShader(_shader, fs); GL.LinkProgram(_shader);
        GL.DetachShader(_shader, vs); GL.DetachShader(_shader, fs);
        GL.DeleteShader(vs); GL.DeleteShader(fs);
        _uViewport = GL.GetUniformLocation(_shader, "uViewport");
        _uColor = GL.GetUniformLocation(_shader, "uColor");
        _uUseTex = GL.GetUniformLocation(_shader, "uUseTex");
        _uTexUnit = GL.GetUniformLocation(_shader, "uTex");
    }

    public void Dispose()
    {
        if (_shader != 0) GL.DeleteProgram(_shader);
        if (_vbo != 0) GL.DeleteBuffer(_vbo);
        if (_vao != 0) GL.DeleteVertexArray(_vao);
        _shader = _vbo = _vao = 0;
    }
}
