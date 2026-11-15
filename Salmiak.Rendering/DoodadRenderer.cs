using System;
using System.Collections.Generic;
using System.IO;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using Salmiak.Core.Formats;
using Salmiak.Core.IO;
using Salmiak.Core.Animation;

namespace Salmiak.Rendering;

public sealed class DoodadRenderer : IDisposable
{
    private const float MapHalf = 32f * AdtFile.TileSize;

    private readonly MpqManager _mpq;
    private TextureCache? _tex;
    private int _shader, _uMVP, _uModel, _uTex, _uCamPos, _uFogColor, _uFogRange, _uAlpha, _uHighlight, _uLightTint;

    // Texture animation: a model's texture units can offset, rotate and scale their uv
    // coordinates over time, which is what makes waterfall and torch-flame doodads move. The
    // transform is a mat4 in the same row-vector convention as the model matrices, so the shader
    // reads it the way it reads the bone matrices and the model matrix; only its x and y columns
    // matter because it moves texture coordinates.
    private int _uUvXform, _uIdUvXform;

    // The uv transform is one uniform per program, so a model without a texture animation only
    // has to clear it when an animated model left it scrolled. These record which of the two
    // programs currently holds a scrolled transform.
    private bool _uvDirty, _uvDirtyId;

    // GPU skinning for classic (v.256) rigged models. The static path keeps its own vertex array:
    // the bone slot and weight attributes are disabled there, which feeds them GL's generic value
    // (0,0,0,1) rather than zeros, so the only way a model that is not rigged at all stays where
    // the shadow baker traced it is that the bone count uniform reads 0 while it is drawn, which
    // makes the shader's BoneMatrix return the identity and the blend collapse to it.
    // 192 slots is what the shader's uBones array holds, and it is enough for the whole 1.12
    // client: measured over the 7480 classic models of model.MPQ whose vertices name bones at all,
    // once the vertex bytes are bound to the bones they name, the busiest skeleton is
    // transportship_sails.m2 with 176 slots and only five models pass 128 at all, so every model
    // of the 1.12 client fits and one over the cap is drawn unposed instead.
    private const int MaxBones = 192;
    private int _uBones, _uBoneCount;
    // One matrix at a time, in OpenTK's own matrix type: the model and view matrices already
    // reach the shader through that call form, so the bone and texture matrices use the form
    // whose behavior the renderer has already proven rather than a second upload path.
    private Matrix4 _uploadMatrix = new Matrix4();

    // The bone count and the bone matrices are one uniform per program, so what each program holds
    // now has to be recorded: the count goes back to 0 for the models that are not posed, the model
    // whose matrices are in the array is kept next to it, and the time they were posed for is kept
    // with it so that a new frame, whose stopwatch has advanced, poses them again.
    private int _boneCountMain, _boneCountId;

    // How far apart two uBones elements are in their program's locations, as the driver answered.
    private int _boneStrideMain = 4, _boneStrideId = 4;

    // Where each uBones element is, asked for once per program by the bracketed name the shader
    // uses; Unqueried means the name has not been asked for yet and a negative answer is kept so
    // the question is not asked again.
    private int[] _boneLocationsMain = [], _boneLocationsId = [];
    private const int Unqueried = -2;
    private int _posedElapsedMain = -1, _posedElapsedId = -1;
    private GpuModel? _posedMain, _posedId;

    private sealed class GpuModel
    {
        public int Vao, Vbo, Ebo;
        public (int Start, int Count, int TexId)[] Submeshes = [];
        public Vector3 Center;
        public float Radius;
        public Vector3 Min, Max;

        // Animated models only: this VAO adds the bone-slot/weight attributes, Pose is the
        // live bone hierarchy, DefaultSequence the sequence played when none was requested,
        // UvSampler/UvAnims the texture animation and which submesh uses it.
        public int SkinVao, SkinVbo, BoneSlots, DefaultSequence = -1;
        public M2Pose? Pose;
        public M2UvAnimation? UvSampler;
        public int[] UvAnims = [];
    }

    private readonly Dictionary<string, GpuModel?> _models = new(StringComparer.OrdinalIgnoreCase);

    public sealed class CpuModel
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

        // The skinning attributes and the pose belong to the drawn models only, while the shadow
        // baker reads Vertices and Indices and nothing else, so what it traces never changes while a
        // model plays: for the bake, BuildCpuModel writes the rest pose, the first frame of the
        // sequence the model stands on, straight into Vertices once, which is the pose the client had
        // when it baked the shadows this bake is meant to reproduce.
        public float[] SkinAttrib = [];
        public M2Pose? Pose;
        public M2UvAnimation? UvSampler;
        public int[] UvAnims = [];      // one per Submeshes entry, -1 when the unit is not animated
        // The sequence a model plays when none was asked for and the one its rest pose comes from.
        public int DefaultSequence = -1;
    }

    //CPU-side geometry for the shadow baker (kept separate from the render cache so BLPs can be freed).
    private readonly Dictionary<string, CpuModel?> _cpuModels = new(StringComparer.OrdinalIgnoreCase);

    public CpuModel? GetCpuGeometry(string m2Path)
    {
        if (m2Path == null || m2Path.Length == 0) return null;
        var key = Path.ChangeExtension(m2Path, ".m2");
        if (_cpuModels.TryGetValue(key, out var cached)) return cached;
        CpuModel? built = null;
        // The baker only needs geometry, so this build skips the skinning attributes and the pose.
        try { built = BuildCpuModel(key, null, false); }
        catch (Exception) { built = null; }
        _cpuModels[key] = built;
        return built;
    }

    public void ClearCpuGeometry() => _cpuModels.Clear();

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
    private int _uIdBones, _uIdBoneCount;
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

    // Classic skeletal playback for the NPC models. Shadow baking never sees these poses: it traces
    // the CPU geometry of the map's doodad models, which is posed once to its rest pose.
    public bool AnimateNpcs { get; set; } = true;

    // -1 plays the sequence each model stands on, which is animation 0 (Stand) resolved through the
    // model's own animation-id lookup.
    public int NpcSequence { get; set; } = -1;

    public float AnimationTimeScale { get; set; } = 1f;

    // Doodads animate too. Measured over the 1.12 client's doodads in model.MPQ and patch.MPQ,
    // 825 and 1063 of them key bones (palms sway, waterfalls and ships move), 88 and 98 scroll
    // their uv coordinates and 41 and 71 fade in and out; the remaining 4446 and 4702 carry no
    // keys at all, which is why most bushes, shrubs and grass simply stay put.
    public bool AnimateDoodads { get; set; } = true;

    // -1 plays the sequence each model stands on (animation 0, Stand, through its own lookup). A
    // doodad type asked for a sequence number another of the same kind does not have simply holds
    // the first frame of its own tracks.
    public int DoodadSequence { get; set; } = -1;

    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private int _animEpochMs;

    private int NpcElapsedMs()
        => (int)Math.Max(0d, (_clock.ElapsedMilliseconds - _animEpochMs) * (double)AnimationTimeScale);

    // Doodads play from the moment the renderer was built, so every waterfall in a zone scrolls
    // in step instead of each starting at its own spawn time.
    private int DoodadElapsedMs()
        => (int)Math.Max(0d, _clock.ElapsedMilliseconds * (double)AnimationTimeScale);

    public void SetNpcs(List<(string Path, NpcLook? Look, Matrix4 Transform)> defs)
    {
        _npcDefs = defs;
        _npcInstances.Clear();
        _npcDirty = true;
        SelectedNpc = -1;
        _animEpochMs = (int)_clock.ElapsedMilliseconds;
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
        var model = UploadCpu(BuildCpuModel(Path.ChangeExtension(path, ".m2"), null, true));
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
        try { foreach (var (p, look) in _loadQueue.GetConsumingEnumerable()) { if (_workerStop) break; _ready.Enqueue(BuildCpuModel(p, look, true)); } }
        catch (Exception) { }
    }

    // The render flag separates the two uses of a model: the shadow baker asks for geometry only,
    // which it gets posed once into its rest pose so the triangles it traces match the ones the
    // client had, while anything that will be drawn also gets the pose and the texture animation,
    // which live in their own attributes and never touch Vertices.
    private CpuModel BuildCpuModel(string m2Path, NpcLook? look, bool render)
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
            var uvAnims = new List<int>(m2.Submeshes.Count);
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
                uvAnims.Add(sm.UvAnim);
                if (tex.Length > 0 && !cm.Textures.ContainsKey(tex))
                {
                    try { using var ts = _mpq.OpenFile(tex.Replace('/', '\\')); var (rgba, w, h) = BlpReader.Decode(ts); cm.Textures[tex] = (rgba, w, h); }
                    catch { }
                }
            }
            cm.Submeshes = subs.ToArray();
            cm.UvAnims = uvAnims.ToArray();
            cm.DefaultSequence = IdleSequence(m2);
            if (render)
            {
                AddSkinning(cm, m2);
                AddTextureAnimation(cm, m2);
            }
            else RestPoseForBake(cm, m2);
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

    // Whether a model animates at all: bones that move (palms sway, waterfalls flow) or texture
    // units whose uv coordinates scroll. Measured over the 1.12 client's doodads, most of them
    // carry neither and are genuinely static, so skipping those costs the renderer nothing.
    private static bool HasAnimKeys(M2File m2)
    {
        foreach (var b in m2.Bones)
            if (b.Translation.KeyCount > 0 || b.Rotation.KeyCount > 0 || b.Scaling.KeyCount > 0) return true;
        foreach (var a in m2.TextureAnimations)
            if (a.Translation.KeyCount > 0 || a.Rotation.KeyCount > 0 || a.Scaling.KeyCount > 0) return true;
        return false;
    }

    // Maps the vertex record's bone indices onto the shader's uBones slots. Bones that did not
    // fit into the slots keep a zero weight, so such a model is drawn in the bind pose instead
    // of being deformed by an incomplete skeleton.
    private static void AddSkinning(CpuModel cm, M2File m2)
    {
        if (!HasAnimKeys(m2)) return;
        var pose = new M2Pose(m2);
        if (!pose.Animates()) return;

        int vertices = m2.Vertices.Length / 8;
        if (vertices <= 0 || m2.BoneIndices.Length < vertices * 4) return;

        var attrib = new float[vertices * 8];
        for (int i = 0; i < vertices; i++)
            for (int k = 0; k < 4; k++)
            {
                int slot = pose.SlotOfBone(pose.ResolvedBone(i * 4 + k));
                if (slot < 0) continue;
                attrib[i * 8 + k] = (float)slot;
                attrib[i * 8 + 4 + k] = m2.BoneWeights[i * 4 + k];
            }

        cm.SkinAttrib = attrib;
        cm.Pose = pose;
    }

    // The shadow baker traces a model's own triangles to decide what blocks the sun, and the shadows
    // it has to agree with are the ones Blizzard wrote into the client's MCSH payloads, which were
    // traced against the geometry the client draws: the model holds the first frame of the sequence
    // it arms, which is animation 0 (Stand), rather than the bind pose. The two differ a lot on the
    // rigged doodads. A gryphon or wyvern roost is a bind pose with the wings held straight out to
    // the sides, a T pose, while the wyphlets in the client's nests sit with their wings folded, and
    // the baked shadows under the Westfall and Durotar nests hold no wings at all, so baking the bind
    // pose lays a wing shadow on the nest floor that the client never had. CPU-skinning the vertices
    // into the idle sequence's first frame is what makes the traced triangles the client's own.
    private static void RestPoseForBake(CpuModel cm, M2File m2)
    {
        if (!HasAnimKeys(m2)) return;
        var pose = new M2Pose(m2);
        if (!pose.Animates()) return;
        pose.Evaluate(cm.DefaultSequence, 0);
        var mats = pose.Matrices();
        var weights = m2.BoneWeights;
        var verts = cm.Vertices;
        int vertices = verts.Length / 8;
        for (int v = 0; v < vertices; v++)
        {
            int inf = v * 4;
            var o = v * 8;
            var px = (float)verts[o];
            var py = (float)verts[o + 1];
            var pz = (float)verts[o + 2];
            var nx = (float)verts[o + 3];
            var ny = (float)verts[o + 4];
            var nz = (float)verts[o + 5];
            var qx = 0f;
            var qy = 0f;
            var qz = 0f;
            var dx = 0f;
            var dy = 0f;
            var dz = 0f;
            var posed = false;
            for (int k = 0; k < 4; k++)
            {
                if (inf + k >= weights.Length || weights[inf + k] <= 0f) continue;
                var bone = pose.ResolvedBone(inf + k);
                if (bone < 0 || bone >= mats.Length) continue;
                var w = weights[inf + k];
                var m = mats[bone];
                // The same blend the shader does, in System.Numerics' row-vector convention: the
                // position is the point times the bone matrix, weighted, summed over the influences.
                qx += w * (px * m.M11 + py * m.M21 + pz * m.M31 + m.M41);
                qy += w * (px * m.M12 + py * m.M22 + pz * m.M32 + m.M42);
                qz += w * (px * m.M13 + py * m.M23 + pz * m.M33 + m.M43);
                dx += w * (nx * m.M11 + ny * m.M21 + nz * m.M31);
                dy += w * (nx * m.M12 + ny * m.M22 + nz * m.M32);
                dz += w * (nx * m.M13 + ny * m.M23 + nz * m.M33);
                posed = true;
            }
            if (!posed) continue;
            verts[o] = qx; verts[o + 1] = qy; verts[o + 2] = qz;
            verts[o + 3] = dx; verts[o + 4] = dy; verts[o + 5] = dz;
        }

        // The box the baker keeps for the model, grown to hold the rest pose as well as the bind
        // pose it was measured from, so the sphere every instance of the model is culled by still
        // covers the triangles that are actually traced.
        var min = cm.Min;
        var max = cm.Max;
        for (int v = 0; v < vertices; v++)
        {
            var o = v * 8;
            min = new Vector3(Math.Min(min.X, verts[o]), Math.Min(min.Y, verts[o + 1]), Math.Min(min.Z, verts[o + 2]));
            max = new Vector3(Math.Max(max.X, verts[o]), Math.Max(max.Y, verts[o + 1]), Math.Max(max.Z, verts[o + 2]));
        }
        cm.Min = min;
        cm.Max = max;
        cm.Center = (min + max) * 0.5f;
        cm.Radius = (max - min).Length * 0.5f;
    }

    // The uv transform of the submeshes that carry one. Only the units whose texture unit points
    // at a texture animation are affected, which is how the water of a waterfall scrolls while
    // the rock around it stays put.
    private static void AddTextureAnimation(CpuModel cm, M2File m2)
    {
        if (m2.TextureAnimations.Count == 0) return;
        var uv = new M2UvAnimation(m2.TextureAnimations, m2.Sequences, m2.Raw);
        if (!uv.Animates()) return;
        cm.UvSampler = uv;
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
        g.UvAnims = cm.UvAnims;
        g.UvSampler = cm.UvSampler;
        g.DefaultSequence = cm.DefaultSequence;
        if (cm.Pose != null && cm.SkinAttrib.Length > 0) UploadSkin(g, cm, cm.Pose!);
        return g;
    }

    // A second vertex array for rigged models: the position/normal/uv attributes point at the
    // same buffer, and only the bone slot and weight attributes are new. Element indices are
    // per-array state in GL 4, so the element buffer is bound again.
    private static void UploadSkin(GpuModel g, CpuModel cm, M2Pose pose)
    {
        g.Pose = pose;
        g.BoneSlots = Math.Min(pose.SlotCount(), MaxBones);

        g.SkinVao = GL.GenVertexArray();
        GL.BindVertexArray(g.SkinVao);

        // Vertex attribute pointers capture the buffer that is bound at the moment they are set,
        // and that binding is global state, so the model's own buffer is bound again here: the
        // position/normal/uv attributes of this array read the same data as the static array.
        GL.BindBuffer(BufferTarget.ArrayBuffer, g.Vbo);
        const int stride = 8 * sizeof(float);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
        GL.EnableVertexAttribArray(2);

        g.SkinVbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, g.SkinVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, cm.SkinAttrib.Length * sizeof(float), cm.SkinAttrib, BufferUsageHint.StaticDraw);
        GL.VertexAttribPointer(3, 4, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(3);
        GL.VertexAttribPointer(4, 4, VertexAttribPointerType.Float, false, stride, 4 * sizeof(float));
        GL.EnableVertexAttribArray(4);

        GL.BindBuffer(BufferTarget.ElementArrayBuffer, g.Ebo);
        GL.BindVertexArray(0);
    }

    // The sequence a model plays when none was asked for: the one the client arms, which is
    // animation 0 (Stand) through the header's animation-id lookup, then the sequence record whose
    // own animation id is 0, and only as a last resort the first sequence that has a length. Benilla's
    // hosts.md says the default is "animation 0 when the model has the animation that id 0 resolves to,
    // else the first dword of the first sequence record", and its doodad host plays "Stand with a drawn
    // variation". Choosing by the lookup rather than by the order the records were written is what the
    // data forces: the 1.12 client's Raptor.m2 puts Run (id 5) first, Walk (id 4) second and Stand (id
    // 0) third with lookup entry 0 at sequence 2, and NagaMale puts Walk first with entry 0 at sequence
    // 2, so taking the first sequence that has a length makes those creatures run or walk in place
    // while the client has them stand. The race models people wear already have Stand as their first
    // sequence, which is why only the creature models looked wrong.
    private static int IdleSequence(M2File m2)
    {
        var lookup = m2.SequenceLookup;
        if (lookup.Length > 0 && lookup[0] < m2.Sequences.Count) return lookup[0];
        for (int q = 0; q < m2.Sequences.Count; q++)
            if (m2.Sequences[q].AnimId == 0u) return q;
        for (int q = 0; q < m2.Sequences.Count; q++)
            if ((long)m2.Sequences[q].TimeEnd - (long)m2.Sequences[q].TimeStart > 0) return q;
        return 0;
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
        // Static models draw unskinned and with unscrolled texture coordinates; the models that
        // animate set these again as they are drawn.
        SetBoneCount(_uBoneCount, 0, false);
        UploadUv(System.Numerics.Matrix4x4.Identity, _uUvXform);
        _uvDirty = false;
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

    // One matrix into one uniform slot, through the same call form the model and view matrices
    // already use. System.Numerics' matrices are row-major in memory and OpenTK's are filled here
    // in that same order, and uploading without transposing means GL's columns are those rows:
    // the shader then multiplies a matrix by a column vector, which is the same product as the
    // row-vector multiply the pose, the camera and the shadow bake already do.
    private void UploadMatrix(int location, System.Numerics.Matrix4x4 m)
    {
        _uploadMatrix = new Matrix4(
            m.M11, m.M12, m.M13, m.M14,
            m.M21, m.M22, m.M23, m.M24,
            m.M31, m.M32, m.M33, m.M34,
            m.M41, m.M42, m.M43, m.M44);
        GL.UniformMatrix4(location, false, ref _uploadMatrix);
    }

    // The location of the shader's uBones array and the distance between two of its elements. A
    // mat4 is one element of the array, so the element after the array's first is the next location
    // along rather than four: the driver says so directly, its answer for uBones[1] is one past
    // uBones[0] and its answer for the last element is MaxBones - 1 past the base. UploadBones
    // asks each slot by its bracketed name, which is what the shader's own indexing means, and
    // keeps this stride only as a fallback for a driver that refuses the bracketed name.
    private static (int, int) BoneArrayLocation(int program)
    {
        var at = GL.GetUniformLocation(program, "uBones");
        var zero = GL.GetUniformLocation(program, "uBones[0]");
        if (at < 0) at = zero;
        // The shader's uBones array is MaxBones elements wide, which is what the last element's
        // location is asked for: dividing the driver's span by MaxBones - 1 is a steadier reading
        // of the stride than one element pair is.
        var last = GL.GetUniformLocation(program, "uBones[" + (MaxBones - 1) + "]");
        var one = GL.GetUniformLocation(program, "uBones[1]");
        var stride = 4;
        if (last > at && (last - at) % (MaxBones - 1) == 0) stride = (last - at) / (MaxBones - 1);
        else if (one > at) stride = one - at;
        if (stride < 1 || stride > 16) stride = 4;
        return (at, stride);
    }

    // Posed bones in uBones slot order: the pose's matrices are the same ones the pose probe
    // measured against the model's own bounding box, and each slot is one array element, so a
    // slot's matrices cannot be mixed up with another model's. A slot's location is asked for once
    // per program by its bracketed name, which is what the shader's own indexing means, and only
    // the stride arithmetic is left as a fallback for a driver that refuses the bracketed name.
    private void UploadBones(M2Pose pose, int bonesLocation, int countLocation, bool idPass)
    {
        int slots = Math.Min(pose.SlotCount(), MaxBones);
        if (slots <= 0) { SetBoneCount(countLocation, 0, idPass); return; }

        var mats = pose.Matrices();
        var named = idPass ? _boneLocationsId : _boneLocationsMain;
        if (named.Length < MaxBones)
        {
            named = NewBoneLocations();
            if (idPass) _boneLocationsId = named; else _boneLocationsMain = named;
        }
        var stride = idPass ? _boneStrideId : _boneStrideMain;
        var program = idPass ? _idShader : _shader;
        for (int s = 0; s < slots; s++)
        {
            var m = mats[pose.BoneOfSlot(s)];
            var at = named[s];
            if (at == Unqueried)
            {
                at = GL.GetUniformLocation(program, "uBones[" + s + "]");
                named[s] = at;
            }
            UploadMatrix(at >= 0 ? at : bonesLocation + s * stride, m);
        }

        SetBoneCount(countLocation, slots, idPass);
    }

    // One program's bone count uniform, tracked so that switching it back to 0 for the models that
    // draw unskinned only costs an upload when it actually differs. A bone count above 0 makes the
    // disabled slot and weight attributes of a static vertex array pick up uBones[0], which would
    // deform a model that is not meant to be posed at all.
    private void SetBoneCount(int location, int count, bool idPass)
    {
        if (idPass)
        {
            if (_boneCountId == count) return;
            _boneCountId = count;
        }
        else
        {
            if (_boneCountMain == count) return;
            _boneCountMain = count;
        }
        // No bone is in the array, so no model is posed any more and the next rigged model has to
        // have its own matrices uploaded.
        if (count == 0)
        {
            if (idPass) _posedId = null;
            else _posedMain = null;
        }
        GL.Uniform1(location, count);
    }

    // One uv transform, in the same convention as the bone and model matrices, so it reaches the
    // shader through the same call form as they do.
    private void UploadUv(System.Numerics.Matrix4x4 uv, int location)
    {
        UploadMatrix(location, uv);
    }

    private void DrawNpcList(Matrix4 vp, Frustum frustum, Vector3 camPos, float maxDist2)
    {
        if (_npcInstances.Count == 0) return;
        bool dim = SelectedNpc >= 0;
        if (dim) { GL.Enable(EnableCap.Blend); GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha); }
        var npcElapsed = NpcElapsedMs();
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

            // Rigged models are posed per instance, so several instances of one model can play
            // different sequences.
            var g = inst.Model;
            var skinned = BeginModel(g, AnimateNpcs, NpcSequence, npcElapsed, _uBones, _uBoneCount, false);
            GL.BindVertexArray(skinned ? g.SkinVao : g.Vao);
            for (int i = 0; i < g.Submeshes.Length; i++)
            {
                var (start, count, texId) = g.Submeshes[i];
                if (AnimateNpcs && g.UvSampler != null) SetUv(g, NpcSequence, npcElapsed, i, false);
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
        UploadUv(System.Numerics.Matrix4x4.Identity, _uIdUvXform);
        _uvDirtyId = false;
        SetBoneCount(_uIdBoneCount, 0, true);

        DrawIds(_npcInstances, 0, vp, frustum, npcPass: true);
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

    // Poses one model and clears any texture transform an earlier model left behind, once per run
    // of a model rather than once per instance because all the instances of a model play the same
    // time. Whether the skinned vertex array has to be bound is what the caller needs to know.
    private bool BeginModel(GpuModel g, bool animate, int requestedSequence, int elapsedMs,
                            int bonesLocation, int boneCountLocation, bool idPass)
    {
        var skinned = false;
        if (g.Pose != null && g.SkinVao != 0 && g.BoneSlots > 0)
        {
            // A model whose animation is switched off does not fall back to the bind pose: it holds
            // the first frame of the sequence it plays, because that is the pose the client draws it
            // in and the pose the shadows under it were baked from, which is the rest pose the baker
            // traces. A gryphon roost drawn in its bind pose would hold the wyphlets' wings out
            // straight to the sides over a floor the client's own shadows show clean.
            var time = animate ? elapsedMs : 0;
            var seq = animate && requestedSequence >= 0 ? requestedSequence : g.DefaultSequence;
            // The bone uniforms are one array per program, so they only hold for as long as the
            // model that was posed last is the one being drawn: instances of one model are not
            // necessarily next to each other, so another rigged model in between has to have its
            // own bones uploaded again, or this model's slots would read the previous model's
            // matrices and deform it by a skeleton it does not own.
            if ((idPass ? _posedId : _posedMain) != g
                || (idPass ? _posedElapsedId : _posedElapsedMain) != time)
            {
                g.Pose.Evaluate(seq, time);
                UploadBones(g.Pose, bonesLocation, boneCountLocation, idPass);
                if (idPass) { _posedId = g; _posedElapsedId = time; }
                else { _posedMain = g; _posedElapsedMain = time; }
            }
            skinned = true;
        }
        // The static vertex array's slot and weight attributes are disabled, which feeds them the
        // generic value (0,0,0,1) instead of zeros, so a bone count left above 0 by an animated
        // model would deform this one by uBones[0]. Zero makes the shader blend to the identity.
        if (!skinned) SetBoneCount(boneCountLocation, 0, idPass);

        // A model whose texture units animate only writes its transform while it is being animated;
        // when animation is switched off it falls through to the identity below, so the transform an
        // animated model left in the uniform cannot leak onto it.
        if (animate && g.UvSampler != null) return skinned;

        // The model has no texture animation, so the identity only has to be written when an
        // animated model left a scrolled transform behind.
        if (idPass ? _uvDirtyId : _uvDirty)
        {
            UploadUv(System.Numerics.Matrix4x4.Identity, idPass ? _uIdUvXform : _uUvXform);
            if (idPass) _uvDirtyId = false;
            else _uvDirty = false;
        }
        return skinned;
    }

    // The texture transform of one submesh, only asked for by the models that animate their
    // texture units at all. A submesh whose texture unit is not animated draws untransformed.
    private void SetUv(GpuModel g, int requestedSequence, int elapsedMs, int submesh, bool idPass)
    {
        var seq = requestedSequence >= 0 ? requestedSequence : g.DefaultSequence;
        var anim = submesh >= 0 && submesh < g.UvAnims.Length ? g.UvAnims[submesh] : -1;
        var uv = anim >= 0 && g.UvSampler != null ? g.UvSampler.Evaluate(anim, seq, elapsedMs)
                                                    : System.Numerics.Matrix4x4.Identity;
        if (idPass) { UploadUv(uv, _uIdUvXform); _uvDirtyId = true; }
        else { UploadUv(uv, _uUvXform); _uvDirty = true; }
    }

    private void DrawList(List<Instance> list, Matrix4 vp, Frustum frustum, Vector3 camPos, float maxDist2, int baseHl, bool selectable)
    {
        var elapsed = DoodadElapsedMs();
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
            var g = inst.Model;
            var skinned = BeginModel(g, AnimateDoodads, DoodadSequence, elapsed, _uBones, _uBoneCount, false);
            GL.BindVertexArray(skinned ? g.SkinVao : g.Vao);
            for (int i = 0; i < g.Submeshes.Length; i++)
            {
                var (start, count, texId) = g.Submeshes[i];
                if (AnimateDoodads && g.UvSampler != null) SetUv(g, DoodadSequence, elapsed, i, false);
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
        UploadUv(System.Numerics.Matrix4x4.Identity, _uUvXform);
        _uvDirty = false;
        SetBoneCount(_uBoneCount, 0, false);
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
        UploadUv(System.Numerics.Matrix4x4.Identity, _uIdUvXform);
        _uvDirtyId = false;
        SetBoneCount(_uIdBoneCount, 0, true);

        DrawIds(_instances, 0, vp, frustum, npcPass: false);
        DrawIds(_externalInstances, _instances.Count, vp, frustum, npcPass: false);
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

    // The picking silhouette is drawn in the same pass state as the scene, so doodads and
    // creatures are posed and scrolled the way they are drawn.
    private void DrawIds(List<Instance> list, int idBase, Matrix4 vp, Frustum frustum, bool npcPass)
    {
        var animate = npcPass ? AnimateNpcs : AnimateDoodads;
        var requestedSequence = npcPass ? NpcSequence : DoodadSequence;
        var elapsed = npcPass ? NpcElapsedMs() : DoodadElapsedMs();
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

            // The picking silhouette has to match the drawn pose, or clicking a creature's
            // outstretched leg would miss it.
            var g = inst.Model;
            var skinned = BeginModel(g, animate, requestedSequence, elapsed, _uIdBones, _uIdBoneCount, true);
            GL.BindVertexArray(skinned ? g.SkinVao : g.Vao);
            for (int i = 0; i < g.Submeshes.Length; i++)
            {
                var (start, count, texId) = g.Submeshes[i];
                if (animate && g.UvSampler != null) SetUv(g, requestedSequence, elapsed, i, true);
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

        // Thumbnails hold whatever pose the model's own vertices carry, with unscrolled texture
        // coordinates, so a texture transform an earlier scene pass left behind has to be cleared
        // here as well.
        UploadUv(System.Numerics.Matrix4x4.Identity, _uUvXform);
        _uvDirty = false;
        SetBoneCount(_uBoneCount, 0, false);

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
            layout(location=3) in vec4 aBoneSlots;
            layout(location=4) in vec4 aBoneWeights;
            uniform mat4 uMVP;
            uniform mat4 uBones[192];
            uniform int uBoneCount;
            uniform mat4 uUvXform;
            out vec2 vUV;

            mat4 BoneMatrix(int s)
            {
                if (s < 0 || s >= uBoneCount)
                    return mat4(1.0);
                return uBones[s];
            }

            void main() {
                // Same skinning as the model program, so the picking silhouette follows the pose.
                float total = aBoneWeights.x + aBoneWeights.y + aBoneWeights.z + aBoneWeights.w;
                mat4 skin = mat4(1.0);
                if (total > 0.0001) {
                    skin = BoneMatrix(int(aBoneSlots.x + 0.5)) * aBoneWeights.x
                        + BoneMatrix(int(aBoneSlots.y + 0.5)) * aBoneWeights.y
                        + BoneMatrix(int(aBoneSlots.z + 0.5)) * aBoneWeights.z
                        + BoneMatrix(int(aBoneSlots.w + 0.5)) * aBoneWeights.w;
                    skin = skin / total;
                }
                vec4 local = skin * vec4(aPos, 1.0);
                // The same texture animation as the model program, so the alpha test that carves
                // the picking silhouette out of a waterfall cuts the same shape as is drawn.
                vUV = (uUvXform * vec4(aUV, 0.0, 1.0)).xy;
                gl_Position = uMVP * local;
            }
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
        var (idBoneAt, idBoneStride) = BoneArrayLocation(_idShader);
        _uIdBones = idBoneAt;
        _boneStrideId = idBoneStride;
        _uIdBoneCount = GL.GetUniformLocation(_idShader, "uBoneCount");
        _boneCountId = 0;
        _posedId = null;
        _posedElapsedId = -1;
        _uIdUvXform = GL.GetUniformLocation(_idShader, "uUvXform");
        _boneLocationsId = NewBoneLocations();
    }

    private void CreateShader()
    {
        const string vert = """
            #version 330 core
            layout(location=0) in vec3 aPos;
            layout(location=1) in vec3 aNorm;
            layout(location=2) in vec2 aUV;
            layout(location=3) in vec4 aBoneSlots;
            layout(location=4) in vec4 aBoneWeights;
            uniform mat4 uMVP;
            uniform mat4 uModel;
            uniform mat4 uBones[192];
            uniform int uBoneCount;
            uniform mat4 uUvXform;
            out vec3 vNorm;
            out vec2 vUV;
            out vec3 vWorld;

            mat4 BoneMatrix(int s)
            {
                if (s < 0 || s >= uBoneCount)
                    return mat4(1.0);
                return uBones[s];
            }

            void main() {
                // Static geometry (doodads, unrigged models) has no weights at all, which leaves
                // it where the CPU already posed it for the shadow baker. Blending the bone matrices is
                // the same result as blending deformed positions when the weights sum to one.
                float total = aBoneWeights.x + aBoneWeights.y + aBoneWeights.z + aBoneWeights.w;
                mat4 skin = mat4(1.0);
                if (total > 0.0001) {
                    skin = BoneMatrix(int(aBoneSlots.x + 0.5)) * aBoneWeights.x
                        + BoneMatrix(int(aBoneSlots.y + 0.5)) * aBoneWeights.y
                        + BoneMatrix(int(aBoneSlots.z + 0.5)) * aBoneWeights.z
                        + BoneMatrix(int(aBoneSlots.w + 0.5)) * aBoneWeights.w;
                    skin = skin / total;
                }
                vec4 local = skin * vec4(aPos, 1.0);
                vNorm = mat3(uModel) * (mat3(skin) * aNorm);
                // Texture animation: the uv coordinates are offset, rotated and scaled over time,
                // which is what scrolls water and flames. The transform is the identity for every
                // model that does not animate its texture units, which leaves the uv coordinates
                // numerically unchanged.
                vUV = (uUvXform * vec4(aUV, 0.0, 1.0)).xy;
                vec4 world = uModel * local;
                vWorld = world.xyz;
                gl_Position = uMVP * local;
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
        var (boneAt, boneStride) = BoneArrayLocation(_shader);
        _uBones = boneAt;
        _boneStrideMain = boneStride;
        _uBoneCount = GL.GetUniformLocation(_shader, "uBoneCount");
        _uUvXform = GL.GetUniformLocation(_shader, "uUvXform");
        // A fresh program starts with every uniform at zero, which the tracker has to know.
        _boneCountMain = 0;
        _posedMain = null;
        _posedElapsedMain = -1;
        _boneLocationsMain = NewBoneLocations();
    }

    // Fresh element-location slots for a program that was just built: the locations belong to the
    // program, so a rebuilt shader has to be asked again.
    private int[] NewBoneLocations()
    {
        var at = new int[MaxBones];
        for (int i = 0; i < at.Length; i++) at[i] = Unqueried;
        return at;
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
            if (m != null)
            {
                GL.DeleteVertexArray(m.Vao); GL.DeleteBuffer(m.Vbo); GL.DeleteBuffer(m.Ebo);
                if (m.SkinVao != 0) GL.DeleteVertexArray(m.SkinVao);
                if (m.SkinVbo != 0) GL.DeleteBuffer(m.SkinVbo);
            }
        _models.Clear();
        _instances.Clear();
        if (_shader != 0) GL.DeleteProgram(_shader);
        if (_idShader != 0) GL.DeleteProgram(_idShader);
        if (_fbo != 0) { GL.DeleteFramebuffer(_fbo); GL.DeleteTexture(_fboColor); GL.DeleteRenderbuffer(_fboDepth); }
        if (_thumbFbo != 0) { GL.DeleteFramebuffer(_thumbFbo); GL.DeleteTexture(_thumbColor); GL.DeleteRenderbuffer(_thumbDepth); }
        _shader = 0; _idShader = 0; _fbo = 0; _thumbFbo = 0;
    }
}
