using System;
using System.Collections.Generic;
using System.IO;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using Salmiak.Core.Formats;
using Salmiak.Core.IO;

namespace Salmiak.Rendering;

public sealed class DoodadRenderer : IDisposable
{
    private const float MapHalf = 32f * AdtFile.TileSize;

    private readonly MpqManager _mpq;
    private TextureCache? _tex;
    private int _shader, _uMVP, _uModel, _uTex, _uCamPos, _uFogColor, _uFogRange, _uAlpha, _uHighlight, _uLightTint;

    private sealed class GpuModel
    {
        public int Vao, Vbo, Ebo;
        public (int Start, int Count, int TexId)[] Submeshes = [];
        public Vector3 Center;
        public float Radius;
        public Vector3 Min, Max;
    }

    private readonly Dictionary<string, GpuModel?> _models = new(StringComparer.OrdinalIgnoreCase);

    private sealed class CpuModel
    {
        public string Path = "";
        public NpcLook? Look;
        public string Key = "";
        public float[] Vertices = [];
        public uint[] Indices = [];
        public (int Start, int Count, string Tex)[] Submeshes = [];
        public readonly Dictionary<string, (byte[] Rgba, int W, int H)> Textures = new(StringComparer.OrdinalIgnoreCase);
        public Vector3 Min, Max, Center;
        public float Radius;
        public bool Failed;
    }
    public sealed class NpcLook
    {
        public string? Skin;
        public string? Hair;
        public int HairGeoset = -1;
        public bool Character;
        public string Key => $"{Skin}|{Hair}|{HairGeoset}|{(Character ? 1 : 0)}";
    }

    private readonly System.Collections.Concurrent.BlockingCollection<(string Path, NpcLook? Look)> _loadQueue = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<CpuModel> _ready = new();
    private readonly HashSet<string> _loading = new(StringComparer.OrdinalIgnoreCase);
    private System.Threading.Thread? _worker;
    private volatile bool _workerStop;

    private struct Instance { public GpuModel Model; public Matrix4 Transform; public AdtFile? Adt; public int Index; public string? Path; }
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

    public readonly record struct DoodadHit(AdtFile? Adt, int Index, string? ExternalPath, float ExternalScale);

    private int _idShader, _uIdMVP, _uIdTex, _uIdColor;
    private int _fbo, _fboColor, _fboDepth, _fboW, _fboH;

    private int _thumbFbo, _thumbColor, _thumbDepth, _thumbSize;
    private readonly List<Instance> _externalInstances = new();

    public bool Enabled { get; set; } = true;

    public bool Highlight { get; set; }

    public float RenderDistance { get; set; } = 450f;

    private readonly HashSet<(AdtFile, int)> _selected = new();

    public void SetSelection(IEnumerable<(AdtFile Adt, int Index)> selection)
    {
        _selected.Clear();
        foreach (var s in selection) _selected.Add(s);
    }

    public DoodadRenderer(MpqManager mpq) => _mpq = mpq;

    public bool TryGetModelBounds(string path, out Vector3 center, out float radius)
    {
        var m = GetModel(path);
        if (m == null) { center = default; radius = 0; return false; }
        center = m.Center; radius = m.Radius;
        return true;
    }

    public bool TryGetModelAabb(string path, out Vector3 min, out Vector3 max)
    {
        var m = GetModel(path);
        if (m == null) { min = default; max = default; return false; }
        min = m.Min; max = m.Max;
        return true;
    }

    public void Load(IEnumerable<AdtFile> adts, TextureCache tex)
    {
        _tex = tex;
        if (_shader == 0) CreateShader();
        _instances.Clear();
        _instanceIndex.Clear();
        _externalInstances.Clear();

        foreach (var adt in adts)
        for (int i = 0; i < adt.Doodads.Count; i++)
        {
            var d = adt.Doodads[i];
            if (string.IsNullOrEmpty(d.ModelPath)) continue;
            var model = GetModel(d.ModelPath);
            if (model == null) continue;
            _instanceIndex[(adt, i)] = _instances.Count;
            _instances.Add(new Instance { Model = model, Transform = BuildTransform(d), Adt = adt, Index = i });
        }
    }

    public void AddExternal(string modelPath, Matrix4 transform)
    {
        if (string.IsNullOrEmpty(modelPath)) return;
        var model = GetModel(modelPath);
        if (model == null) return;
        _externalInstances.Add(new Instance { Model = model, Transform = transform, Path = modelPath });
    }

    private List<(string Path, NpcLook? Look, Matrix4 Transform)> _npcDefs = new();
    private readonly List<Instance> _npcInstances = new();
    private bool _npcDirty;

    public int SelectedNpc { get; set; } = -1;

    public void SetNpcs(List<(string Path, NpcLook? Look, Matrix4 Transform)> defs)
    {
        _npcDefs = defs;
        _npcInstances.Clear();
        _npcDirty = true;
        SelectedNpc = -1;
    }

    public void ClearNpcs()
    {
        _npcDefs = new List<(string, NpcLook?, Matrix4)>();
        _npcInstances.Clear();
        _npcDirty = false;
        SelectedNpc = -1;
    }

    private void RebuildNpcs()
    {
        _npcInstances.Clear();
        int pending = 0;
        for (int i = 0; i < _npcDefs.Count; i++)
        {
            var (path, look, t) = _npcDefs[i];
            var m = GetModel(path, look);
            if (m != null) _npcInstances.Add(new Instance { Model = m, Transform = t, Path = path, Index = i });
            else if (!_models.ContainsKey(CacheKey(path, look))) pending++;
        }
        _npcDirty = pending > 0;
    }

    public static Matrix4 BuildTransform(DoodadDef d)
    {
        var axisFix = new Matrix4(
            1, 0,  0, 0,
            0, 0, -1, 0,
            0, 1,  0, 0,
            0, 0,  0, 1);

        var scale = Matrix4.CreateScale(d.Scale);
        
        var rot = Matrix4.CreateRotationX(MathHelper.DegreesToRadians(d.Rotation.Z))
                * Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(-d.Rotation.X))
                * Matrix4.CreateRotationY(MathHelper.DegreesToRadians(d.Rotation.Y - 90f));
        var translate = Matrix4.CreateTranslation(
            d.Position.X - MapHalf,
            d.Position.Y,
            d.Position.Z - MapHalf);

        return scale * axisFix * rot * translate;
    }

    private static string CacheKey(string anyPath, NpcLook? look)
    {
        string p = Path.ChangeExtension(anyPath, ".m2");
        return look == null ? p : p + "|" + look.Key;
    }

    private GpuModel? GetModel(string path, NpcLook? look = null)
    {
        string key = CacheKey(path, look);
        if (_models.TryGetValue(key, out var cached)) return cached;
        if (_loading.Add(key)) { EnsureWorker(); _loadQueue.Add((Path.ChangeExtension(path, ".m2"), look)); }
        return null;
    }

    private GpuModel? GetModelSync(string path)
    {
        string key = CacheKey(path, (NpcLook?)null);
        if (_models.TryGetValue(key, out var cached)) return cached;
        var model = UploadCpu(BuildCpuModel(Path.ChangeExtension(path, ".m2"), null));
        _models[key] = model;
        _loading.Remove(key);
        return model;
    }

    public int DrainReady(int maxPerFrame = 6)
    {
        int n = 0;
        while (n < maxPerFrame && _ready.TryDequeue(out var cm))
        {
            _loading.Remove(cm.Key);
            if (_models.ContainsKey(cm.Key)) continue;
            _models[cm.Key] = UploadCpu(cm);
            n++;
        }
        if (n > 0 && _npcDefs.Count > 0) _npcDirty = true;
        return n;
    }

    private void EnsureWorker()
    {
        if (_worker != null) return;
        _worker = new System.Threading.Thread(WorkerLoop) { IsBackground = true, Name = "DoodadLoader" };
        _worker.Start();
    }

    private void WorkerLoop()
    {
        try { foreach (var (p, look) in _loadQueue.GetConsumingEnumerable()) { if (_workerStop) break; _ready.Enqueue(BuildCpuModel(p, look)); } }
        catch (Exception) { }
    }

    private CpuModel BuildCpuModel(string m2Path, NpcLook? look)
    {
        var cm = new CpuModel { Path = m2Path, Look = look, Key = CacheKey(m2Path, look) };
        try
        {
            M2File? m2;
            using (var s = _mpq.OpenFile(m2Path)) m2 = M2File.Parse(s);
            if (m2 == null || m2.Indices.Length == 0) { cm.Failed = true; return cm; }
            cm.Vertices = m2.Vertices;
            cm.Indices = m2.Indices;
            cm.Min = new Vector3(m2.BoundsMin.X, m2.BoundsMin.Y, m2.BoundsMin.Z);
            cm.Max = new Vector3(m2.BoundsMax.X, m2.BoundsMax.Y, m2.BoundsMax.Z);
            cm.Center = (cm.Min + cm.Max) * 0.5f;
            cm.Radius = (cm.Max - cm.Min).Length * 0.5f;
            bool charModel = look?.Character == true;

            var subs = new List<(int, int, string)>(m2.Submeshes.Count);
            foreach (var sm in m2.Submeshes)
            {
                if (charModel && !CharGeosetVisible(sm.MeshId, look!.HairGeoset)) continue;
                string tex = sm.TexturePath ?? "";
                if (tex.Length == 0 && look != null)
                {
                    if (sm.TextureType == 1 || sm.TextureType >= 11) tex = look.Skin ?? "";
                    else if (sm.TextureType == 6) tex = look.Hair ?? "";
                    if (tex.Length == 0) continue;
                }
                subs.Add((sm.IndexStart, sm.IndexCount, tex));
                if (tex.Length > 0 && !cm.Textures.ContainsKey(tex))
                {
                    try { using var ts = _mpq.OpenFile(tex.Replace('/', '\\')); var (rgba, w, h) = BlpReader.Decode(ts); cm.Textures[tex] = (rgba, w, h); }
                    catch { }
                }
            }
            cm.Submeshes = subs.ToArray();
        }
        catch { cm.Failed = true; }
        return cm;
    }

    private static bool CharGeosetVisible(int id, int hairGeoset)
    {
        if (id == 0) return true;
        int group = id / 100, variant = id % 100;
        if (group == 0) return id == hairGeoset;
        return group switch
        {
            1 or 2 or 3 => true,
            7 => variant <= 2,
            4 or 5 or 8 or 9 or 10 or 11 or 13 or 18 => variant == 1,
            _ => false,
        };
    }

    private GpuModel? UploadCpu(CpuModel cm)
    {
        if (cm.Failed || cm.Indices.Length == 0) return null;
        var g = new GpuModel { Center = cm.Center, Radius = cm.Radius, Min = cm.Min, Max = cm.Max };
        g.Vao = GL.GenVertexArray();
        GL.BindVertexArray(g.Vao);

        g.Vbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, g.Vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, cm.Vertices.Length * sizeof(float), cm.Vertices, BufferUsageHint.StaticDraw);

        int stride = 8 * sizeof(float);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
        GL.EnableVertexAttribArray(2);

        g.Ebo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, g.Ebo);
        GL.BufferData(BufferTarget.ElementArrayBuffer, cm.Indices.Length * sizeof(uint), cm.Indices, BufferUsageHint.StaticDraw);
        GL.BindVertexArray(0);

        var subs = new (int, int, int)[cm.Submeshes.Length];
        for (int i = 0; i < cm.Submeshes.Length; i++)
        {
            var (start, count, tex) = cm.Submeshes[i];
            int texId = 0;
            if (tex.Length > 0 && _tex != null)
                texId = cm.Textures.TryGetValue(tex, out var t) ? _tex.GetOrUploadDecoded(tex, t.Rgba, t.W, t.H) : _tex.Get(tex);
            subs[i] = (start, count, texId);
        }
        g.Submeshes = subs;
        return g;
    }

    public void Render(Camera camera, float aspect)
    {
        if (_npcDirty) RebuildNpcs();
        if (!Enabled || _shader == 0 ||
            (_instances.Count == 0 && _externalInstances.Count == 0 && _npcInstances.Count == 0)) return;

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
        GL.ActiveTexture(TextureUnit.Texture0);

        float maxDist2 = RenderDistance * RenderDistance;
        int baseHl = Highlight ? 1 : 0;
        DrawList(_instances, vp, frustum, camPos, maxDist2, baseHl, selectable: true);
        DrawList(_externalInstances, vp, frustum, camPos, maxDist2, baseHl, selectable: false);
        DrawNpcList(vp, frustum, camPos, maxDist2);
        GL.BindVertexArray(0);
    }

    private void DrawNpcList(Matrix4 vp, Frustum frustum, Vector3 camPos, float maxDist2)
    {
        if (_npcInstances.Count == 0) return;
        bool dim = SelectedNpc >= 0;
        if (dim) { GL.Enable(EnableCap.Blend); GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha); }
        foreach (var inst in _npcInstances)
        {
            var center = Vector3.TransformPosition(inst.Model.Center, inst.Transform);
            if ((center - camPos).LengthSquared > maxDist2) continue;
            float scale = MathF.Max(inst.Transform.Row0.Xyz.Length,
                          MathF.Max(inst.Transform.Row1.Xyz.Length, inst.Transform.Row2.Xyz.Length));
            if (!frustum.IntersectsSphere(center, inst.Model.Radius * scale)) continue;

            bool sel = inst.Index == SelectedNpc;
            GL.Uniform1(_uHighlight, sel ? 2 : 0);
            GL.Uniform1(_uAlpha, !dim || sel ? 1f : 0.3f);
            var mvp = inst.Transform * vp;
            var model = inst.Transform;
            GL.UniformMatrix4(_uMVP, false, ref mvp);
            GL.UniformMatrix4(_uModel, false, ref model);
            GL.BindVertexArray(inst.Model.Vao);
            foreach (var (start, count, texId) in inst.Model.Submeshes)
            {
                GL.BindTexture(TextureTarget.Texture2D, texId);
                GL.DrawElements(PrimitiveType.Triangles, count, DrawElementsType.UnsignedInt, start * sizeof(uint));
            }
        }
        if (dim) GL.Disable(EnableCap.Blend);
        GL.Uniform1(_uAlpha, 1f);
    }

    public int PickNpc(Camera camera, float aspect, int mx, int my, int width, int height)
    {
        if (_npcInstances.Count == 0 || width <= 0 || height <= 0) return -1;
        if (mx < 0 || my < 0 || mx >= width || my >= height) return -1;
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

        DrawIds(_npcInstances, 0, vp, frustum);
        GL.BindVertexArray(0);

        var px = new byte[4];
        GL.ReadPixels(mx, height - 1 - my, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, px);
        int pid = px[0] | (px[1] << 8) | (px[2] << 16);

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, RenderTarget.Screen);
        GL.Viewport(0, 0, width, height);
        var hz = SceneEnv.HorizonColor;
        GL.ClearColor(hz.X, hz.Y, hz.Z, 1f);

        return pid > 0 && pid <= _npcInstances.Count ? _npcInstances[pid - 1].Index : -1;
    }

    private void DrawList(List<Instance> list, Matrix4 vp, Frustum frustum, Vector3 camPos, float maxDist2, int baseHl, bool selectable)
    {
        foreach (var inst in list)
        {
            var center = Vector3.TransformPosition(inst.Model.Center, inst.Transform);
            if ((center - camPos).LengthSquared > maxDist2) continue;
            float scale = MathF.Max(inst.Transform.Row0.Xyz.Length,
                          MathF.Max(inst.Transform.Row1.Xyz.Length, inst.Transform.Row2.Xyz.Length));
            if (!frustum.IntersectsSphere(center, inst.Model.Radius * scale)) continue;

            int hl = selectable && inst.Adt != null && _selected.Contains((inst.Adt, inst.Index)) ? 2 : baseHl;
            GL.Uniform1(_uHighlight, hl);
            var mvp = inst.Transform * vp;
            var model = inst.Transform;
            GL.UniformMatrix4(_uMVP, false, ref mvp);
            GL.UniformMatrix4(_uModel, false, ref model);
            GL.BindVertexArray(inst.Model.Vao);
            foreach (var (start, count, texId) in inst.Model.Submeshes)
            {
                GL.BindTexture(TextureTarget.Texture2D, texId);
                GL.DrawElements(PrimitiveType.Triangles, count, DrawElementsType.UnsignedInt, start * sizeof(uint));
            }
        }
    }

    public void RenderPreview(Camera camera, float aspect, DoodadDef def)
    {
        if (string.IsNullOrEmpty(def.ModelPath)) return;
        if (_shader == 0) CreateShader();
        var model = GetModel(def.ModelPath);
        if (model == null) return;

        var vp = camera.View * camera.Projection(aspect);
        var transform = BuildTransform(def);
        var mvp = transform * vp;

        GL.UseProgram(_shader);
        GL.Uniform1(_uTex, 0);
        var camPos = camera.Position; GL.Uniform3(_uCamPos, ref camPos);
        var fogColor = SceneEnv.HorizonColor; GL.Uniform3(_uFogColor, ref fogColor);
        GL.Uniform2(_uFogRange, SceneEnv.FogStart, SceneEnv.FogEnd);
        var ptint = SceneEnv.LightTint; GL.Uniform3(_uLightTint, ref ptint);
        GL.Uniform1(_uAlpha, 0.5f);
        GL.Uniform1(_uHighlight, 0);
        GL.UniformMatrix4(_uMVP, false, ref mvp);
        GL.UniformMatrix4(_uModel, false, ref transform);
        GL.ActiveTexture(TextureUnit.Texture0);

        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.DepthMask(false);
        GL.BindVertexArray(model.Vao);
        foreach (var (start, count, texId) in model.Submeshes)
        {
            GL.BindTexture(TextureTarget.Texture2D, texId);
            GL.DrawElements(PrimitiveType.Triangles, count, DrawElementsType.UnsignedInt, start * sizeof(uint));
        }
        GL.BindVertexArray(0);
        GL.DepthMask(true);
        GL.Disable(EnableCap.Blend);
        GL.Uniform1(_uAlpha, 1f);
    }

    public DoodadHit? Pick(Camera camera, float aspect, int mx, int my, int width, int height)
    {
        if ((_instances.Count == 0 && _externalInstances.Count == 0) || width <= 0 || height <= 0) return null;
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

        DrawIds(_instances, 0, vp, frustum);
        DrawIds(_externalInstances, _instances.Count, vp, frustum);
        GL.BindVertexArray(0);

        var px = new byte[4];
        GL.ReadPixels(mx, height - 1 - my, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, px);
        int pid = px[0] | (px[1] << 8) | (px[2] << 16);

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, RenderTarget.Screen);
        GL.Viewport(0, 0, width, height);
        var hz = SceneEnv.HorizonColor;
        GL.ClearColor(hz.X, hz.Y, hz.Z, 1f);

        if (pid <= 0) return null;
        if (pid <= _instances.Count)
        {
            var hit = _instances[pid - 1];
            return hit.Adt != null ? new DoodadHit(hit.Adt, hit.Index, null, 0f) : null;
        }
        int ext = pid - 1 - _instances.Count;
        if (ext < 0 || ext >= _externalInstances.Count) return null;
        var e = _externalInstances[ext];
        float escale = MathF.Max(e.Transform.Row0.Xyz.Length,
                       MathF.Max(e.Transform.Row1.Xyz.Length, e.Transform.Row2.Xyz.Length));
        return new DoodadHit(null, -1, e.Path, escale);
    }

    private void DrawIds(List<Instance> list, int idBase, Matrix4 vp, Frustum frustum)
    {
        for (int k = 0; k < list.Count; k++)
        {
            var inst = list[k];
            var center = Vector3.TransformPosition(inst.Model.Center, inst.Transform);
            float scale = MathF.Max(inst.Transform.Row0.Xyz.Length,
                          MathF.Max(inst.Transform.Row1.Xyz.Length, inst.Transform.Row2.Xyz.Length));
            if (!frustum.IntersectsSphere(center, inst.Model.Radius * scale)) continue;

            int id = idBase + k + 1;
            GL.Uniform3(_uIdColor, (id & 0xFF) / 255f, ((id >> 8) & 0xFF) / 255f, ((id >> 16) & 0xFF) / 255f);
            var mvp = inst.Transform * vp;
            GL.UniformMatrix4(_uIdMVP, false, ref mvp);
            GL.BindVertexArray(inst.Model.Vao);
            foreach (var (start, count, texId) in inst.Model.Submeshes)
            {
                GL.BindTexture(TextureTarget.Texture2D, texId);
                GL.DrawElements(PrimitiveType.Triangles, count, DrawElementsType.UnsignedInt, start * sizeof(uint));
            }
        }
    }

    public byte[]? RenderThumbnail(string path, int size, float yawDeg = 0f)
    {
        var m = GetModelSync(path);
        if (m == null) return null;
        if (_shader == 0) CreateShader();
        EnsureThumbFbo(size);

        var center = m.Center;
        float r = MathF.Max(m.Radius, 0.5f);
        var baseDir = Vector3.Normalize(new Vector3(0.9f, -1.3f, 0.7f));
        float a = MathHelper.DegreesToRadians(yawDeg);
        var dir = new Vector3(
            baseDir.X * MathF.Cos(a) - baseDir.Y * MathF.Sin(a),
            baseDir.X * MathF.Sin(a) + baseDir.Y * MathF.Cos(a),
            baseDir.Z);
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

        GL.BindVertexArray(m.Vao);
        foreach (var (start, count, texId) in m.Submeshes)
        {
            GL.BindTexture(TextureTarget.Texture2D, texId);
            GL.DrawElements(PrimitiveType.Triangles, count, DrawElementsType.UnsignedInt, start * sizeof(uint));
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
                if (texture(uTex, vUV).a < 0.5) discard;
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

    private void CreateShader()
    {
        const string vert = """
            #version 330 core
            layout(location=0) in vec3 aPos;
            layout(location=1) in vec3 aNorm;
            layout(location=2) in vec2 aUV;
            uniform mat4 uMVP;
            uniform mat4 uModel;
            out vec3 vNorm;
            out vec2 vUV;
            out vec3 vWorld;
            void main() {
                vNorm = mat3(uModel) * aNorm;
                vUV = aUV;
                vWorld = (uModel * vec4(aPos, 1.0)).xyz;
                gl_Position = uMVP * vec4(aPos, 1.0);
            }
            """;
        const string frag = """
            #version 330 core
            in vec3 vNorm;
            in vec2 vUV;
            in vec3 vWorld;
            uniform sampler2D uTex;
            uniform vec3 uCamPos;
            uniform vec3 uFogColor;
            uniform vec2 uFogRange;
            uniform float uAlpha;
            uniform int uHighlight;
            uniform vec3 uLightTint;
            out vec4 FragColor;
            void main() {
                vec4 tex = texture(uTex, vUV);
                if (tex.a < 0.5) discard;
                vec3 sun = normalize(vec3(0.6, 1.0, 0.4));
                float l = 0.45 + 0.55 * max(dot(normalize(vNorm), sun), 0.0);
                vec3 rgb = tex.rgb * l * uLightTint;
                if (uHighlight == 1) rgb = mix(rgb, vec3(1.0, 0.9, 0.35), 0.25);
                else if (uHighlight == 2) rgb = mix(rgb, vec3(0.25, 1.0, 0.55), 0.55);
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
        _uCamPos = GL.GetUniformLocation(_shader, "uCamPos");
        _uFogColor = GL.GetUniformLocation(_shader, "uFogColor");
        _uFogRange = GL.GetUniformLocation(_shader, "uFogRange");
        _uAlpha = GL.GetUniformLocation(_shader, "uAlpha");
        _uHighlight = GL.GetUniformLocation(_shader, "uHighlight");
        _uLightTint = GL.GetUniformLocation(_shader, "uLightTint");
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
        _workerStop = true;
        _loadQueue.CompleteAdding();
        _worker?.Join(500);
        _loadQueue.Dispose();
        foreach (var m in _models.Values)
            if (m != null) { GL.DeleteVertexArray(m.Vao); GL.DeleteBuffer(m.Vbo); GL.DeleteBuffer(m.Ebo); }
        _models.Clear();
        _instances.Clear();
        if (_shader != 0) GL.DeleteProgram(_shader);
        if (_idShader != 0) GL.DeleteProgram(_idShader);
        if (_fbo != 0) { GL.DeleteFramebuffer(_fbo); GL.DeleteTexture(_fboColor); GL.DeleteRenderbuffer(_fboDepth); }
        if (_thumbFbo != 0) { GL.DeleteFramebuffer(_thumbFbo); GL.DeleteTexture(_thumbColor); GL.DeleteRenderbuffer(_thumbDepth); }
        _shader = 0; _idShader = 0; _fbo = 0; _thumbFbo = 0;
    }
}
