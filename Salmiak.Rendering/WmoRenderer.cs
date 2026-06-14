using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using Salmiak.Core.Formats;
using Salmiak.Core.IO;

namespace Salmiak.Rendering;

public sealed class WmoRenderer : IDisposable
{
    private const float MapHalf = 32f * AdtFile.TileSize;

    private readonly MpqManager _mpq;
    private TextureCache? _tex;
    private int _shader, _uMVP, _uModel, _uTex, _uHasVColor, _uAlphaTest, _uCamPos, _uFogColor, _uFogRange, _uAlpha, _uHighlight, _uLightTint;

    private int _idShader, _uIdMVP, _uIdTex, _uIdColor;
    private int _fbo, _fboColor, _fboDepth, _fboW, _fboH;
    private int _thumbFbo, _thumbColor, _thumbDepth, _thumbSize;

    public bool Highlight { get; set; }

    private sealed class GpuGroup
    {
        public int Vao, Vbo, Ebo;
        public bool HasVColor;
        public (int Start, int Count, int TexId, bool AlphaTest)[] Batches = [];
    }
    private sealed class GpuWmo
    {
        public List<GpuGroup> Groups = new();
        public WmoFile.Root Root = null!;
        public Vector3 Center;
        public float Radius;
        public Vector3 BoundsMin, BoundsMax;
    }

    private readonly Dictionary<string, GpuWmo?> _models = new(StringComparer.OrdinalIgnoreCase);

    private struct Instance { public GpuWmo Wmo; public Matrix4 Transform; public AdtFile? Adt; public int Index; }
    private readonly List<Instance> _instances = new();
    private readonly Dictionary<(AdtFile, int), int> _instanceIndex = new();

    public bool UpdateInstance(AdtFile adt, int index, Matrix4 transform)
    {
        if (!_instanceIndex.TryGetValue((adt, index), out int k) || k >= _instances.Count) return false;
        var inst = _instances[k];
        inst.Transform = transform;
        _instances[k] = inst;
        return true;
    }

    public bool Enabled { get; set; } = true;

    public float RenderDistance { get; set; } = 900f;

    public DoodadRenderer? DoodadSink { get; set; }

    public WmoRenderer(MpqManager mpq) => _mpq = mpq;

    public bool TryGetModelAabb(string path, out Vector3 min, out Vector3 max)
    {
        var w = GetWmo(path);
        if (w == null) { min = max = default; return false; }
        min = w.BoundsMin; max = w.BoundsMax;
        return true;
    }

    public bool TryGetRadius(string path, out float radius)
    {
        var w = GetWmo(path);
        if (w == null) { radius = 0f; return false; }
        radius = w.Radius;
        return true;
    }

    public void Load(IEnumerable<AdtFile> adts, TextureCache tex)
    {
        _tex = tex;
        if (_shader == 0) CreateShader();
        _instances.Clear();
        _instanceIndex.Clear();

        foreach (var adt in adts)
        for (int i = 0; i < adt.Wmos.Count; i++)
        {
            var w = adt.Wmos[i];
            if (string.IsNullOrEmpty(w.ModelPath)) continue;
            var wmo = GetWmo(w.ModelPath);
            if (wmo == null) continue;
            var transform = BuildTransform(w);
            _instanceIndex[(adt, i)] = _instances.Count;
            _instances.Add(new Instance { Wmo = wmo, Transform = transform, Adt = adt, Index = i });
            EmitInteriorDoodads(wmo.Root, w.DoodadSet, transform);
        }
    }

    private void EmitInteriorDoodads(WmoFile.Root root, int doodadSet, Matrix4 wmoTransform)
    {
        if (DoodadSink == null || root.Doodads.Count == 0 || root.DoodadSets.Count == 0) return;

        void Emit(int start, int count)
        {
            for (int i = start; i < start + count && i < root.Doodads.Count; i++)
            {
                var dd = root.Doodads[i];
                if (string.IsNullOrEmpty(dd.Path)) continue;
                var local = Matrix4.CreateScale(dd.Scale)
                          * Matrix4.CreateFromQuaternion(new Quaternion(dd.Rotation.X, dd.Rotation.Y, dd.Rotation.Z, dd.Rotation.W))
                          * Matrix4.CreateTranslation(dd.Position.X, dd.Position.Y, dd.Position.Z);
                DoodadSink.AddExternal(dd.Path, local * wmoTransform);
            }
        }

        Emit(root.DoodadSets[0].Start, root.DoodadSets[0].Count);
        if (doodadSet > 0 && doodadSet < root.DoodadSets.Count)
            Emit(root.DoodadSets[doodadSet].Start, root.DoodadSets[doodadSet].Count);
    }

    public static Matrix4 BuildTransform(WmoDef w)
    {
        var axisFix = new Matrix4(
            1, 0,  0, 0,
            0, 0, -1, 0,
            0, 1,  0, 0,
            0, 0,  0, 1);
        var rot = Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(w.Rotation.Z))
                * Matrix4.CreateRotationX(MathHelper.DegreesToRadians(w.Rotation.X))
                * Matrix4.CreateRotationY(MathHelper.DegreesToRadians(w.Rotation.Y - 90f));
        var translate = Matrix4.CreateTranslation(w.Position.X - MapHalf, w.Position.Y, w.Position.Z - MapHalf);
        return axisFix * rot * translate;
    }

    public void Invalidate(string rootPath)
    {
        if (!_models.TryGetValue(rootPath, out var m)) return;
        if (m != null)
            foreach (var g in m.Groups)
            { GL.DeleteVertexArray(g.Vao); GL.DeleteBuffer(g.Vbo); GL.DeleteBuffer(g.Ebo); }
        _models.Remove(rootPath);
    }

    private GpuWmo? GetWmo(string rootPath)
    {
        if (_models.TryGetValue(rootPath, out var cached)) return cached;

        GpuWmo? wmo = null;
        try
        {
            WmoFile.Root root;
            using (var rs = _mpq.OpenFile(rootPath))
                root = WmoFile.ParseRoot(rs);

            var built = new GpuWmo { Root = root };
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            string baseName = rootPath.Substring(0, rootPath.Length - 4);
            for (int g = 0; g < root.GroupCount; g++)
            {
                string groupPath = $"{baseName}_{g:000}.wmo";
                if (!_mpq.FileExists(groupPath)) continue;
                using var gs = _mpq.OpenFile(groupPath);
                var grp = WmoFile.ParseGroup(gs, root);
                if (grp == null) continue;
                int n = grp.Vertices.Length / WmoFile.GroupVertexStride;
                for (int v = 0; v < n; v++)
                {
                    int o = v * WmoFile.GroupVertexStride;
                    var p = new Vector3(grp.Vertices[o], grp.Vertices[o + 1], grp.Vertices[o + 2]);
                    min = Vector3.ComponentMin(min, p);
                    max = Vector3.ComponentMax(max, p);
                }
                built.Groups.Add(Upload(grp));
            }
            if (built.Groups.Count > 0)
            {
                if (min.X > max.X) { min = max = Vector3.Zero; }
                built.Center = (min + max) * 0.5f;
                built.Radius = (max - min).Length * 0.5f;
                built.BoundsMin = min;
                built.BoundsMax = max;
                wmo = built;
            }
        }
        catch { wmo = null; }

        _models[rootPath] = wmo;
        return wmo;
    }

    private GpuGroup Upload(WmoFile.Group grp)
    {
        var g = new GpuGroup();
        g.Vao = GL.GenVertexArray();
        GL.BindVertexArray(g.Vao);

        g.Vbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, g.Vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, grp.Vertices.Length * sizeof(float), grp.Vertices, BufferUsageHint.StaticDraw);

        int stride = WmoFile.GroupVertexStride * sizeof(float);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
        GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, stride, 8 * sizeof(float));
        GL.EnableVertexAttribArray(3);
        g.HasVColor = grp.HasVertexColors;

        g.Ebo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, g.Ebo);
        GL.BufferData(BufferTarget.ElementArrayBuffer, grp.Indices.Length * sizeof(uint), grp.Indices, BufferUsageHint.StaticDraw);

        GL.BindVertexArray(0);

        var batches = new (int, int, int, bool)[grp.Batches.Count];
        for (int i = 0; i < grp.Batches.Count; i++)
        {
            var b = grp.Batches[i];
            int texId = !string.IsNullOrEmpty(b.TexturePath) && _tex != null ? _tex.Get(b.TexturePath) : 0;
            batches[i] = (b.IndexStart, b.IndexCount, texId, b.AlphaTest);
        }
        g.Batches = batches;
        return g;
    }

    public void Render(Camera camera, float aspect)
    {
        if (!Enabled || _instances.Count == 0 || _shader == 0) return;

        var vp = camera.View * camera.Projection(aspect);
        var frustum = new Frustum(vp);
        GL.UseProgram(_shader);
        GL.Uniform1(_uTex, 0);
        var camPos = camera.Position;
        GL.Uniform3(_uCamPos, ref camPos);
        var fogColor = SceneEnv.HorizonColor;
        GL.Uniform3(_uFogColor, ref fogColor);
        GL.Uniform2(_uFogRange, SceneEnv.FogStart, SceneEnv.FogEnd);
        var tint = SceneEnv.LightTint; GL.Uniform3(_uLightTint, ref tint);
        GL.Uniform1(_uAlpha, 1f);
        GL.Uniform1(_uHighlight, Highlight ? 1 : 0);
        GL.ActiveTexture(TextureUnit.Texture0);

        foreach (var inst in _instances)
        {
            var center = Vector3.TransformPosition(inst.Wmo.Center, inst.Transform);
            float scale = MathF.Max(inst.Transform.Row0.Xyz.Length,
                          MathF.Max(inst.Transform.Row1.Xyz.Length, inst.Transform.Row2.Xyz.Length));
            float radius = inst.Wmo.Radius * scale;
            if ((center - camPos).Length - radius > RenderDistance) continue;
            if (!frustum.IntersectsSphere(center, radius)) continue;

            var mvp = inst.Transform * vp;
            var model = inst.Transform;
            GL.UniformMatrix4(_uMVP, false, ref mvp);
            GL.UniformMatrix4(_uModel, false, ref model);
            foreach (var grp in inst.Wmo.Groups)
            {
                GL.Uniform1(_uHasVColor, grp.HasVColor ? 1 : 0);
                GL.BindVertexArray(grp.Vao);
                foreach (var (start, count, texId, alphaTest) in grp.Batches)
                {
                    GL.Uniform1(_uAlphaTest, alphaTest ? 1 : 0);
                    GL.BindTexture(TextureTarget.Texture2D, texId);
                    GL.DrawElements(PrimitiveType.Triangles, count, DrawElementsType.UnsignedInt, start * sizeof(uint));
                }
            }
        }
        GL.BindVertexArray(0);
    }

    private void CreateShader()
    {
        const string vert = """
            #version 330 core
            layout(location=0) in vec3 aPos;
            layout(location=1) in vec3 aNorm;
            layout(location=2) in vec2 aUV;
            layout(location=3) in vec4 aColor;
            uniform mat4 uMVP;
            uniform mat4 uModel;
            out vec3 vNorm;
            out vec2 vUV;
            out vec4 vColor;
            out vec3 vWorld;
            void main() {
                vNorm = mat3(uModel) * aNorm;
                vUV = aUV;
                vColor = aColor;
                vWorld = (uModel * vec4(aPos, 1.0)).xyz;
                gl_Position = uMVP * vec4(aPos, 1.0);
            }
            """;
        const string frag = """
            #version 330 core
            in vec3 vNorm;
            in vec2 vUV;
            in vec4 vColor;
            in vec3 vWorld;
            uniform sampler2D uTex;
            uniform int uHasVColor;
            uniform int uAlphaTest;
            uniform vec3 uCamPos;
            uniform vec3 uFogColor;
            uniform vec2 uFogRange;
            uniform float uAlpha;
            uniform int uHighlight;
            uniform vec3 uLightTint;
            out vec4 FragColor;
            void main() {
                vec4 tex = texture(uTex, vUV);
                if (uAlphaTest == 1 && tex.a < 0.5) discard;
                vec3 rgb;
                if (uHasVColor == 1) {
                    rgb = tex.rgb * clamp(vColor.rgb * 2.0, 0.0, 1.0);
                } else {
                    vec3 sun = normalize(vec3(0.6, 1.0, 0.4));
                    float l = 0.5 + 0.5 * max(dot(normalize(vNorm), sun), 0.0);
                    rgb = tex.rgb * l;
                }
                rgb *= uLightTint;
                if (uHighlight == 1) rgb = mix(rgb, vec3(0.35, 0.7, 1.0), 0.25);
                float dist = length(vWorld - uCamPos);
                float fog = clamp((dist - uFogRange.x) / (uFogRange.y - uFogRange.x), 0.0, 1.0);
                FragColor = vec4(mix(rgb, uFogColor, fog), uAlpha);
            }
            """;

        int vs = Compile(ShaderType.VertexShader, vert), fs = Compile(ShaderType.FragmentShader, frag);
        _shader = GL.CreateProgram();
        GL.AttachShader(_shader, vs); GL.AttachShader(_shader, fs); GL.LinkProgram(_shader);
        GL.DetachShader(_shader, vs); GL.DetachShader(_shader, fs);
        GL.DeleteShader(vs); GL.DeleteShader(fs);
        _uMVP = GL.GetUniformLocation(_shader, "uMVP");
        _uModel = GL.GetUniformLocation(_shader, "uModel");
        _uTex = GL.GetUniformLocation(_shader, "uTex");
        _uHasVColor = GL.GetUniformLocation(_shader, "uHasVColor");
        _uAlphaTest = GL.GetUniformLocation(_shader, "uAlphaTest");
        _uCamPos = GL.GetUniformLocation(_shader, "uCamPos");
        _uFogColor = GL.GetUniformLocation(_shader, "uFogColor");
        _uFogRange = GL.GetUniformLocation(_shader, "uFogRange");
        _uAlpha = GL.GetUniformLocation(_shader, "uAlpha");
        _uHighlight = GL.GetUniformLocation(_shader, "uHighlight");
        _uLightTint = GL.GetUniformLocation(_shader, "uLightTint");
    }

    public byte[]? RenderThumbnail(string path, int size, float yawDeg = 0f)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (_shader == 0) CreateShader();
        var wmo = GetWmo(path);
        if (wmo == null) return null;
        EnsureThumbFbo(size);

        var center = wmo.Center;
        float r = MathF.Max(wmo.Radius, 0.5f);
        var dir = Vector3.Normalize(new Vector3(0.9f, -1.3f, 0.7f));
        if (yawDeg != 0f)
        {
            float a = MathHelper.DegreesToRadians(yawDeg);
            float c = MathF.Cos(a), s = MathF.Sin(a);
            dir = new Vector3(dir.X * c - dir.Y * s, dir.X * s + dir.Y * c, dir.Z);
        }
        var eye = center + dir * (r * 3.0f);
        var view = Matrix4.LookAt(eye, center, Vector3.UnitZ);
        var proj = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(40f), 1f, r * 0.05f, r * 12f);
        var vp = view * proj;
        var identity = Matrix4.Identity;

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _thumbFbo);
        GL.Viewport(0, 0, size, size);
        GL.ClearColor(0.60f, 0.62f, 0.66f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthMask(true);
        GL.Disable(EnableCap.Blend);

        GL.UseProgram(_shader);
        GL.Uniform1(_uTex, 0);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.UniformMatrix4(_uMVP, false, ref vp);
        GL.UniformMatrix4(_uModel, false, ref identity);
        GL.Uniform3(_uCamPos, ref eye);
        var fc = SceneEnv.HorizonColor; GL.Uniform3(_uFogColor, ref fc);
        GL.Uniform2(_uFogRange, 1e9f, 1e9f + 1f);
        GL.Uniform3(_uLightTint, 1f, 1f, 1f);
        GL.Uniform1(_uAlpha, 1f);
        GL.Uniform1(_uHighlight, 0);

        foreach (var grp in wmo.Groups)
        {
            GL.Uniform1(_uHasVColor, grp.HasVColor ? 1 : 0);
            GL.BindVertexArray(grp.Vao);
            foreach (var (start, count, texId, alphaTest) in grp.Batches)
            {
                GL.Uniform1(_uAlphaTest, alphaTest ? 1 : 0);
                GL.BindTexture(TextureTarget.Texture2D, texId);
                GL.DrawElements(PrimitiveType.Triangles, count, DrawElementsType.UnsignedInt, start * sizeof(uint));
            }
        }
        GL.BindVertexArray(0);

        var px = new byte[size * size * 4];
        GL.ReadPixels(0, 0, size, size, PixelFormat.Rgba, PixelType.UnsignedByte, px);

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, RenderTarget.Screen);
        var hz = SceneEnv.HorizonColor; GL.ClearColor(hz.X, hz.Y, hz.Z, 1f);
        return px;
    }

    private void EnsureThumbFbo(int size)
    {
        if (_thumbFbo != 0 && _thumbSize == size) return;
        if (_thumbFbo != 0) { GL.DeleteFramebuffer(_thumbFbo); GL.DeleteTexture(_thumbColor); GL.DeleteRenderbuffer(_thumbDepth); }
        _thumbSize = size;
        _thumbFbo = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _thumbFbo);
        _thumbColor = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _thumbColor);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, size, size, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _thumbColor, 0);
        _thumbDepth = GL.GenRenderbuffer();
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _thumbDepth);
        GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, size, size);
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, _thumbDepth);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, RenderTarget.Screen);
    }

    public void RenderPreview(Camera camera, float aspect, WmoDef def)
    {
        if (string.IsNullOrEmpty(def.ModelPath)) return;
        if (_shader == 0) CreateShader();
        var wmo = GetWmo(def.ModelPath);
        if (wmo == null) return;

        var vp = camera.View * camera.Projection(aspect);
        var transform = BuildTransform(def);
        var mvp = transform * vp;
        var model = transform;

        GL.UseProgram(_shader);
        GL.Uniform1(_uTex, 0);
        var camPos = camera.Position; GL.Uniform3(_uCamPos, ref camPos);
        var fogColor = SceneEnv.HorizonColor; GL.Uniform3(_uFogColor, ref fogColor);
        GL.Uniform2(_uFogRange, SceneEnv.FogStart, SceneEnv.FogEnd);
        var ptint = SceneEnv.LightTint; GL.Uniform3(_uLightTint, ref ptint);
        GL.Uniform1(_uAlpha, 0.5f);
        GL.Uniform1(_uHighlight, 0);
        GL.UniformMatrix4(_uMVP, false, ref mvp);
        GL.UniformMatrix4(_uModel, false, ref model);
        GL.ActiveTexture(TextureUnit.Texture0);

        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.DepthMask(false);
        foreach (var grp in wmo.Groups)
        {
            GL.Uniform1(_uHasVColor, grp.HasVColor ? 1 : 0);
            GL.BindVertexArray(grp.Vao);
            foreach (var (start, count, texId, alphaTest) in grp.Batches)
            {
                GL.Uniform1(_uAlphaTest, alphaTest ? 1 : 0);
                GL.BindTexture(TextureTarget.Texture2D, texId);
                GL.DrawElements(PrimitiveType.Triangles, count, DrawElementsType.UnsignedInt, start * sizeof(uint));
            }
        }
        GL.BindVertexArray(0);
        GL.DepthMask(true);
        GL.Disable(EnableCap.Blend);
        GL.Uniform1(_uAlpha, 1f);
    }

    public (AdtFile Adt, int Index)? Pick(Camera camera, float aspect, int mx, int my, int width, int height)
    {
        if (_instances.Count == 0 || width <= 0 || height <= 0) return null;
        if (mx < 0 || my < 0 || mx >= width || my >= height) return null;
        if (_idShader == 0) CreateIdShader();
        EnsureFbo(width, height);

        var vp = camera.View * camera.Projection(aspect);
        var frustum = new Frustum(vp);

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        GL.Viewport(0, 0, width, height);
        GL.ClearColor(0f, 0f, 0f, 0f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthMask(true);
        GL.Disable(EnableCap.Blend);
        GL.Disable(EnableCap.Dither);
        GL.UseProgram(_idShader);
        GL.Uniform1(_uIdTex, 0);
        GL.ActiveTexture(TextureUnit.Texture0);

        for (int k = 0; k < _instances.Count; k++)
        {
            var inst = _instances[k];
            var center = Vector3.TransformPosition(inst.Wmo.Center, inst.Transform);
            float scale = MathF.Max(inst.Transform.Row0.Xyz.Length,
                          MathF.Max(inst.Transform.Row1.Xyz.Length, inst.Transform.Row2.Xyz.Length));
            if (!frustum.IntersectsSphere(center, inst.Wmo.Radius * scale)) continue;

            int id = k + 1;
            GL.Uniform3(_uIdColor, (id & 0xFF) / 255f, ((id >> 8) & 0xFF) / 255f, ((id >> 16) & 0xFF) / 255f);
            var mvp = inst.Transform * vp;
            GL.UniformMatrix4(_uIdMVP, false, ref mvp);
            foreach (var grp in inst.Wmo.Groups)
            {
                GL.BindVertexArray(grp.Vao);
                foreach (var (start, count, texId, alphaTest) in grp.Batches)
                {
                    GL.BindTexture(TextureTarget.Texture2D, texId);
                    GL.DrawElements(PrimitiveType.Triangles, count, DrawElementsType.UnsignedInt, start * sizeof(uint));
                }
            }
        }
        GL.BindVertexArray(0);

        var px = new byte[4];
        GL.ReadPixels(mx, height - 1 - my, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, px);
        int pid = px[0] | (px[1] << 8) | (px[2] << 16);

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, RenderTarget.Screen);
        GL.Viewport(0, 0, width, height);
        var hz = SceneEnv.HorizonColor;
        GL.ClearColor(hz.X, hz.Y, hz.Z, 1f);

        if (pid <= 0 || pid > _instances.Count) return null;
        var hit = _instances[pid - 1];
        return hit.Adt != null ? (hit.Adt, hit.Index) : null;
    }

    private void CreateIdShader()
    {
        const string vert = """
            #version 330 core
            layout(location=0) in vec3 aPos;
            layout(location=2) in vec2 aUV;
            uniform mat4 uMVP;
            out vec2 vUV;
            void main() { vUV = aUV; gl_Position = uMVP * vec4(aPos, 1.0); }
            """;
        const string frag = """
            #version 330 core
            in vec2 vUV;
            uniform sampler2D uTex;
            uniform vec3 uIdColor;
            out vec4 FragColor;
            void main() {
                if (texture(uTex, vUV).a < 0.1) discard;
                FragColor = vec4(uIdColor, 1.0);
            }
            """;
        int vs = Compile(ShaderType.VertexShader, vert), fs = Compile(ShaderType.FragmentShader, frag);
        _idShader = GL.CreateProgram();
        GL.AttachShader(_idShader, vs); GL.AttachShader(_idShader, fs); GL.LinkProgram(_idShader);
        GL.DetachShader(_idShader, vs); GL.DetachShader(_idShader, fs);
        GL.DeleteShader(vs); GL.DeleteShader(fs);
        _uIdMVP = GL.GetUniformLocation(_idShader, "uMVP");
        _uIdTex = GL.GetUniformLocation(_idShader, "uTex");
        _uIdColor = GL.GetUniformLocation(_idShader, "uIdColor");
    }

    private void EnsureFbo(int width, int height)
    {
        if (_fbo != 0 && _fboW == width && _fboH == height) return;
        if (_fbo != 0) { GL.DeleteFramebuffer(_fbo); GL.DeleteTexture(_fboColor); GL.DeleteRenderbuffer(_fboDepth); }

        _fboW = width; _fboH = height;
        _fbo = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _fboColor = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _fboColor);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, width, height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _fboColor, 0);
        _fboDepth = GL.GenRenderbuffer();
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _fboDepth);
        GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, width, height);
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, _fboDepth);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, RenderTarget.Screen);
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
        foreach (var m in _models.Values)
            if (m != null)
                foreach (var g in m.Groups)
                { GL.DeleteVertexArray(g.Vao); GL.DeleteBuffer(g.Vbo); GL.DeleteBuffer(g.Ebo); }
        _models.Clear();
        _instances.Clear();
        if (_shader != 0) GL.DeleteProgram(_shader);
        if (_idShader != 0) GL.DeleteProgram(_idShader);
        if (_fbo != 0) { GL.DeleteFramebuffer(_fbo); GL.DeleteTexture(_fboColor); GL.DeleteRenderbuffer(_fboDepth); }
        if (_thumbFbo != 0) { GL.DeleteFramebuffer(_thumbFbo); GL.DeleteTexture(_thumbColor); GL.DeleteRenderbuffer(_thumbDepth); }
        _shader = 0; _idShader = 0; _fbo = 0; _thumbFbo = 0;
    }
}
