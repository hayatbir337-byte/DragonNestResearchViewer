using OpenTK.GLControl;
using OpenTK.Graphics.OpenGL4;
using DragonNestResearchViewer.Core;
using Pfim;
using NumMat = System.Numerics.Matrix4x4;
using NumVec2 = System.Numerics.Vector2;
using NumVec3 = System.Numerics.Vector3;
using NumVec4 = System.Numerics.Vector4;
using TkMat = OpenTK.Mathematics.Matrix4;
using TkVec3 = OpenTK.Mathematics.Vector3;
using TkVec4 = OpenTK.Mathematics.Vector4;

namespace DragonNestResearchViewer.Rendering;

public sealed class GlViewport : UserControl
{
    readonly GLControl _gl;
    readonly List<GpuPart> _parts = [];

    int _program;
    int _lineVao;
    int _lineVbo;
    bool _ready;

    DnMesh? _mesh;
    DnSkin? _skin;
    DnAnimationSet? _ani;
    AnimationEvaluator? _evaluator;
    string _directory = "";

    int _animationIndex;
    float _frame;
    bool _useAnimation;

    float _yaw = -30f;
    float _pitch = 15f;
    float _viewRadius = 1f;
    NumVec3 _center = NumVec3.Zero;

    Point _lastMouse;
    MouseButtons _dragButton;

    sealed class GpuPart
    {
        public int Vao;
        public int Vbo;
        public int Ebo;
        public int Texture;
        public int IndexCount;
        public DnMeshPart Part = null!;
        public float[] Dynamic = [];
        public int[] SafeIndices = [];
        public string TextureName = "";
    }

    public bool ShowSkeleton { get; set; }
    public bool ShowGrid { get; set; } = true;
    public bool Wireframe { get; set; }

    public bool UseAnimation
    {
        get => _useAnimation;
        set
        {
            if (_useAnimation == value) return;
            _useAnimation = value;
            UpdateDynamicVertices();
            _gl.Invalidate();
        }
    }

    public int AnimationIndex
    {
        get => _animationIndex;
        set
        {
            _animationIndex = Math.Max(0, value);
            UpdateDynamicVertices();
            _gl.Invalidate();
        }
    }

    public float Frame
    {
        get => _frame;
        set
        {
            _frame = Math.Max(0, value);
            UpdateDynamicVertices();
            _gl.Invalidate();
        }
    }

    public GlViewport()
    {
        Dock = DockStyle.Fill;

        _gl = new GLControl(new GLControlSettings
        {
            APIVersion = new Version(3, 3),
            NumberOfSamples = 4
        })
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(12, 16, 23)
        };

        Controls.Add(_gl);

        _gl.Load += (_, _) => InitGl();
        _gl.Paint += (_, _) => Render();
        _gl.Resize += (_, _) =>
        {
            if (!_ready) return;
            _gl.MakeCurrent();
            GL.Viewport(0, 0, Math.Max(1, _gl.Width), Math.Max(1, _gl.Height));
        };

        _gl.MouseDown += (_, e) =>
        {
            _lastMouse = e.Location;
            _dragButton = e.Button;
        };

        _gl.MouseUp += (_, _) => _dragButton = MouseButtons.None;
        _gl.MouseMove += OnMouseMove;
        _gl.MouseWheel += (_, e) =>
        {
            _viewRadius *= e.Delta > 0 ? 0.88f : 1.14f;
            _viewRadius = Math.Clamp(_viewRadius, 0.0001f, 1_000_000f);
            _gl.Invalidate();
        };
    }

    void InitGl()
    {
        _gl.MakeCurrent();

        GL.ClearColor(0.035f, 0.045f, 0.065f, 1f);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Lequal);

        // DN assets contain mixed winding. A research viewer should show both sides.
        GL.Disable(EnableCap.CullFace);

        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        _program = CreateProgram(VertexShader, FragmentShader);

        _lineVao = GL.GenVertexArray();
        _lineVbo = GL.GenBuffer();

        GL.BindVertexArray(_lineVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _lineVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, sizeof(float) * 8, IntPtr.Zero, BufferUsageHint.DynamicDraw);
        SetupVertexLayout();

        _ready = true;

        if (_mesh != null)
            UploadModel();
    }

    static void SetupVertexLayout()
    {
        const int stride = 8 * sizeof(float);

        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(0);

        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);

        GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
        GL.EnableVertexAttribArray(2);
    }

    public void ClearScene()
    {
        _ani = null;
        _evaluator = null;
        _animationIndex = 0;
        _frame = 0;
        _useAnimation = false;
        _mesh = null;
        _skin = null;
        _directory = "";

        if (_ready)
        {
            _gl.MakeCurrent();
            DeleteGpuParts();
        }

        _center = NumVec3.Zero;
        _viewRadius = 1f;
        _gl.Invalidate();
    }

    public void SetModel(DnMesh mesh, DnSkin? skin, string directory)
    {
        _mesh = mesh;
        _skin = skin;
        _directory = directory;

        _ani = null;
        _evaluator = null;
        _animationIndex = 0;
        _frame = 0;
        _useAnimation = false;

        FitToModel();

        if (_ready)
        {
            _gl.MakeCurrent();
            UploadModel();
        }

        _gl.Invalidate();
    }

    public void SetAnimation(DnAnimationSet? ani)
    {
        _ani = ani;
        _evaluator = (_mesh != null && ani != null)
            ? new AnimationEvaluator(_mesh, ani)
            : null;

        _animationIndex = 0;
        _frame = 0;
        _useAnimation = false;

        UpdateDynamicVertices();
        _gl.Invalidate();
    }

    public void FitToModel()
    {
        if (_mesh == null)
            return;

        var finite = _mesh.Parts
            .SelectMany(p => p.Vertices)
            .Where(IsFinite)
            .ToArray();

        if (finite.Length == 0)
        {
            _center = NumVec3.Zero;
            _viewRadius = 1f;
            return;
        }

        // Robust bounds: a single malformed vertex must not throw the camera kilometers away.
        var xs = finite.Select(v => v.X).OrderBy(x => x).ToArray();
        var ys = finite.Select(v => v.Y).OrderBy(x => x).ToArray();
        var zs = finite.Select(v => v.Z).OrderBy(x => x).ToArray();

        int lo = finite.Length >= 100 ? Math.Max(0, (int)(finite.Length * 0.01f)) : 0;
        int hi = finite.Length >= 100 ? Math.Min(finite.Length - 1, (int)(finite.Length * 0.99f)) : finite.Length - 1;

        var min = new NumVec3(xs[lo], ys[lo], zs[lo]);
        var max = new NumVec3(xs[hi], ys[hi], zs[hi]);

        if (!IsFinite(min) || !IsFinite(max) || (max - min).LengthSquared() < 1e-12f)
        {
            min = finite.Aggregate(new NumVec3(float.PositiveInfinity), NumVec3.Min);
            max = finite.Aggregate(new NumVec3(float.NegativeInfinity), NumVec3.Max);
        }

        _center = (min + max) * 0.5f;
        _viewRadius = Math.Max(0.001f, (max - min).Length() * 0.62f);

        _yaw = -28f;
        _pitch = 12f;
        _gl.Invalidate();
    }

    public string TextureSummary()
    {
        if (_parts.Count == 0)
            return "Texture: yok";

        int loaded = _parts.Count(p => p.Texture != 0);
        var names = _parts
            .Where(p => !string.IsNullOrWhiteSpace(p.TextureName))
            .Select(p => Path.GetFileName(p.TextureName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return $"Texture: {loaded}/{_parts.Count}" +
               (names.Length > 0 ? " | " + string.Join(", ", names) : "");
    }

    void DeleteGpuParts()
    {
        foreach (var p in _parts)
        {
            if (p.Vbo != 0) GL.DeleteBuffer(p.Vbo);
            if (p.Ebo != 0) GL.DeleteBuffer(p.Ebo);
            if (p.Vao != 0) GL.DeleteVertexArray(p.Vao);
            if (p.Texture != 0) GL.DeleteTexture(p.Texture);
        }

        _parts.Clear();
    }

    void UploadModel()
    {
        DeleteGpuParts();

        if (_mesh == null)
            return;

        foreach (var part in _mesh.Parts)
        {
            if (part.Vertices.Length == 0)
                continue;

            int[] safeIndices = SanitizeIndices(part);
            if (safeIndices.Length == 0)
                continue;

            var gp = new GpuPart
            {
                Part = part,
                SafeIndices = safeIndices,
                IndexCount = safeIndices.Length,
                Dynamic = new float[part.Vertices.Length * 8]
            };

            gp.Vao = GL.GenVertexArray();
            gp.Vbo = GL.GenBuffer();
            gp.Ebo = GL.GenBuffer();

            GL.BindVertexArray(gp.Vao);

            GL.BindBuffer(BufferTarget.ArrayBuffer, gp.Vbo);
            GL.BufferData(
                BufferTarget.ArrayBuffer,
                gp.Dynamic.Length * sizeof(float),
                gp.Dynamic,
                BufferUsageHint.DynamicDraw);

            GL.BindBuffer(BufferTarget.ElementArrayBuffer, gp.Ebo);
            GL.BufferData(
                BufferTarget.ElementArrayBuffer,
                gp.SafeIndices.Length * sizeof(int),
                gp.SafeIndices,
                BufferUsageHint.StaticDraw);

            SetupVertexLayout();

            if (_skin != null && part.MaterialIndex >= 0 && part.MaterialIndex < _skin.Materials.Count)
            {
                string? texName = _skin.Materials[part.MaterialIndex].DiffuseTexture;
                if (!string.IsNullOrWhiteSpace(texName))
                {
                    gp.TextureName = texName;
                    string? path = ResolveTexturePath(_directory, texName);

                    if (path != null)
                        gp.Texture = TryLoadTexture(path);
                }
            }

            _parts.Add(gp);
        }

        UpdateDynamicVertices();
    }

    static int[] SanitizeIndices(DnMeshPart part)
    {
        var result = new List<int>(part.Indices.Length);

        int n = part.Vertices.Length;
        for (int i = 0; i + 2 < part.Indices.Length; i += 3)
        {
            int a = part.Indices[i];
            int b = part.Indices[i + 1];
            int c = part.Indices[i + 2];

            if ((uint)a >= (uint)n || (uint)b >= (uint)n || (uint)c >= (uint)n)
                continue;

            var va = part.Vertices[a];
            var vb = part.Vertices[b];
            var vc = part.Vertices[c];

            if (!IsFinite(va) || !IsFinite(vb) || !IsFinite(vc))
                continue;

            var normal = NumVec3.Cross(vb - va, vc - va);
            if (normal.LengthSquared() < 1e-14f)
                continue;

            result.Add(a);
            result.Add(b);
            result.Add(c);
        }

        return result.ToArray();
    }

    static bool IsFinite(NumVec3 p) =>
        float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);

    string? ResolveTexturePath(string dir, string raw)
    {
        string normalized = raw.Trim().Replace('\\', Path.DirectorySeparatorChar)
                                      .Replace('/', Path.DirectorySeparatorChar);

        string direct = Path.Combine(dir, normalized);
        if (File.Exists(direct))
            return direct;

        string name = Path.GetFileName(normalized);
        string sibling = Path.Combine(dir, name);
        if (File.Exists(sibling))
            return sibling;

        var cursor = new DirectoryInfo(dir);
        for (int up = 0; up < 10 && cursor != null; up++, cursor = cursor.Parent)
        {
            string candidate = Path.Combine(cursor.FullName, normalized);
            if (File.Exists(candidate))
                return candidate;

            candidate = Path.Combine(cursor.FullName, name);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    int TryLoadTexture(string path)
    {
        try
        {
            using var image = Pfimage.FromFile(path);

            PixelFormat format;
            PixelInternalFormat internalFormat;

            switch (image.Format)
            {
                case ImageFormat.Rgba32:
                    format = PixelFormat.Bgra;
                    internalFormat = PixelInternalFormat.Rgba;
                    break;
                case ImageFormat.Rgb24:
                    format = PixelFormat.Bgr;
                    internalFormat = PixelInternalFormat.Rgb;
                    break;
                case ImageFormat.Rgb8:
                    format = PixelFormat.Red;
                    internalFormat = PixelInternalFormat.R8;
                    break;
                default:
                    return 0;
            }

            int id = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, id);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);

            GL.TexImage2D(
                TextureTarget.Texture2D,
                0,
                internalFormat,
                image.Width,
                image.Height,
                0,
                format,
                PixelType.UnsignedByte,
                image.Data);

            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);

            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
            return id;
        }
        catch
        {
            return 0;
        }
    }

    void UpdateDynamicVertices()
    {
        if (!_ready || _mesh == null)
            return;

        _gl.MakeCurrent();

        NumMat[]? pose = (_useAnimation && _evaluator != null)
            ? _evaluator.EvaluatePose(_animationIndex, _frame)
            : null;

        foreach (var gp in _parts)
        {
            var p = gp.Part;

            for (int i = 0; i < p.Vertices.Length; i++)
            {
                NumVec3 pos = p.Vertices[i];
                NumVec3 normal = i < p.Normals.Length ? p.Normals[i] : NumVec3.UnitY;

                if (!IsFinite(pos))
                    pos = NumVec3.Zero;

                if (!IsFinite(normal) || normal.LengthSquared() < 1e-10f)
                    normal = NumVec3.UnitY;

                if (pose != null &&
                    p.RigIndices.Length == p.Vertices.Length &&
                    p.RigWeights.Length == p.Vertices.Length)
                {
                    NumVec3 skinnedPos = NumVec3.Zero;
                    NumVec3 skinnedNormal = NumVec3.Zero;
                    NumVec4 weights = p.RigWeights[i];
                    float[] ws = [weights.X, weights.Y, weights.Z, weights.W];
                    float total = 0f;

                    for (int k = 0; k < 4; k++)
                    {
                        float w = ws[k];
                        if (!float.IsFinite(w) || w <= 0f)
                            continue;

                        int localIndex = p.RigIndices[i][k];
                        if (localIndex < 0 || localIndex >= p.RigNames.Length)
                            continue;

                        if (!_mesh.BoneIndexByName.TryGetValue(p.RigNames[localIndex], out int boneIndex))
                            continue;

                        NumMat skinMatrix = _mesh.Bones[boneIndex].InverseBindGlobal * pose[boneIndex];

                        var tp = NumVec3.Transform(pos, skinMatrix);
                        var tn = NumVec3.TransformNormal(normal, skinMatrix);

                        if (!IsFinite(tp) || !IsFinite(tn))
                            continue;

                        skinnedPos += tp * w;
                        skinnedNormal += tn * w;
                        total += w;
                    }

                    if (total > 0.0001f && IsFinite(skinnedPos))
                    {
                        pos = skinnedPos / total;

                        if (IsFinite(skinnedNormal) && skinnedNormal.LengthSquared() > 1e-8f)
                            normal = NumVec3.Normalize(skinnedNormal);
                    }
                }

                int o = i * 8;

                gp.Dynamic[o] = pos.X;
                gp.Dynamic[o + 1] = pos.Y;
                gp.Dynamic[o + 2] = pos.Z;

                gp.Dynamic[o + 3] = normal.X;
                gp.Dynamic[o + 4] = normal.Y;
                gp.Dynamic[o + 5] = normal.Z;

                NumVec2 uv = i < p.UV0.Length ? p.UV0[i] : NumVec2.Zero;
                gp.Dynamic[o + 6] = float.IsFinite(uv.X) ? uv.X : 0f;
                gp.Dynamic[o + 7] = float.IsFinite(uv.Y) ? uv.Y : 0f;
            }

            GL.BindBuffer(BufferTarget.ArrayBuffer, gp.Vbo);
            GL.BufferSubData(
                BufferTarget.ArrayBuffer,
                IntPtr.Zero,
                gp.Dynamic.Length * sizeof(float),
                gp.Dynamic);
        }
    }

    void Render()
    {
        if (!_ready || !_gl.HasValidContext)
            return;

        _gl.MakeCurrent();

        GL.Viewport(0, 0, Math.Max(1, _gl.Width), Math.Max(1, _gl.Height));
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        float aspect = Math.Max(0.01f, _gl.Width / (float)Math.Max(1, _gl.Height));
        float cameraDistance = Math.Max(0.01f, _viewRadius * 3.0f);

        NumVec3 eyeNum = CameraPosition(cameraDistance);
        var eye = new TkVec3(eyeNum.X, eyeNum.Y, eyeNum.Z);
        var target = new TkVec3(_center.X, _center.Y, _center.Z);

        TkMat view = TkMat.LookAt(eye, target, TkVec3.UnitY);

        // Orthographic default: stable model-browser framing and no near-plane "exploding" triangles.
        float halfHeight = Math.Max(0.001f, _viewRadius * 1.2f);
        float halfWidth = halfHeight * aspect;

        TkMat projection = TkMat.CreateOrthographicOffCenter(
            -halfWidth,
            halfWidth,
            -halfHeight,
            halfHeight,
            -cameraDistance * 4f,
            cameraDistance * 4f);

        TkMat mvp = view * projection;

        GL.UseProgram(_program);
        GL.UniformMatrix4(GL.GetUniformLocation(_program, "uMvp"), false, ref mvp);
        GL.Uniform3(GL.GetUniformLocation(_program, "uLight"), 0.35f, 0.85f, 0.45f);

        if (ShowGrid)
            DrawGrid(mvp);

        GL.PolygonMode(
            MaterialFace.FrontAndBack,
            Wireframe ? PolygonMode.Line : PolygonMode.Fill);

        foreach (var p in _parts)
        {
            GL.BindVertexArray(p.Vao);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, p.Texture);

            GL.Uniform1(GL.GetUniformLocation(_program, "uTex"), 0);
            GL.Uniform1(GL.GetUniformLocation(_program, "uUseTex"), p.Texture != 0 ? 1 : 0);
            GL.Uniform4(GL.GetUniformLocation(_program, "uColor"), 0.76f, 0.82f, 0.92f, 1f);

            GL.DrawElements(
                PrimitiveType.Triangles,
                p.IndexCount,
                DrawElementsType.UnsignedInt,
                0);
        }

        GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);

        if (ShowSkeleton && _mesh != null)
            DrawSkeleton(mvp);

        _gl.SwapBuffers();
    }

    NumVec3 CameraPosition(float distance)
    {
        float yaw = MathF.PI * _yaw / 180f;
        float pitch = MathF.PI * _pitch / 180f;

        var direction = new NumVec3(
            MathF.Cos(pitch) * MathF.Cos(yaw),
            MathF.Sin(pitch),
            MathF.Cos(pitch) * MathF.Sin(yaw));

        return _center - direction * distance;
    }

    void DrawGrid(TkMat mvp)
    {
        var verts = new List<float>();

        float span = Math.Max(_viewRadius * 1.7f, 0.1f);
        float raw = span / 12f;
        float step = MathF.Pow(10f, MathF.Floor(MathF.Log10(Math.Max(raw, 0.0001f))));
        if (raw / step >= 5f) step *= 5f;
        else if (raw / step >= 2f) step *= 2f;

        int n = 12;
        for (int i = -n; i <= n; i++)
        {
            float x = i * step;
            AddLine(verts, new NumVec3(x + _center.X, _center.Y, -n * step + _center.Z),
                           new NumVec3(x + _center.X, _center.Y, n * step + _center.Z));

            AddLine(verts, new NumVec3(-n * step + _center.X, _center.Y, x + _center.Z),
                           new NumVec3(n * step + _center.X, _center.Y, x + _center.Z));
        }

        DrawLines(verts, mvp, new TkVec4(0.16f, 0.19f, 0.24f, 1f));
    }

    void DrawSkeleton(TkMat mvp)
    {
        if (_mesh == null)
            return;

        NumMat[] pose = (_useAnimation && _evaluator != null)
            ? _evaluator.EvaluatePose(_animationIndex, _frame)
            : _mesh.Bones.Select(b => b.BindGlobal).ToArray();

        var verts = new List<float>();

        if (_ani != null)
        {
            var parents = _ani.Bones.ToDictionary(
                b => b.Name,
                b => b.ParentName,
                StringComparer.OrdinalIgnoreCase);

            foreach (var bone in _mesh.Bones)
            {
                if (!parents.TryGetValue(bone.Name, out string? parentName) ||
                    string.IsNullOrWhiteSpace(parentName))
                    continue;

                if (!_mesh.BoneIndexByName.TryGetValue(parentName, out int parentIndex))
                    continue;

                int childIndex = _mesh.BoneIndexByName[bone.Name];

                var a = Translation(pose[parentIndex]);
                var b = Translation(pose[childIndex]);

                if (IsFinite(a) && IsFinite(b))
                    AddLine(verts, a, b);
            }
        }

        DrawLines(verts, mvp, new TkVec4(1f, 0.62f, 0.04f, 1f));
    }

    static NumVec3 Translation(NumMat m) => new(m.M41, m.M42, m.M43);

    static void AddLine(List<float> data, NumVec3 a, NumVec3 b)
    {
        foreach (var p in new[] { a, b })
        {
            data.Add(p.X); data.Add(p.Y); data.Add(p.Z);
            data.Add(0f); data.Add(1f); data.Add(0f);
            data.Add(0f); data.Add(0f);
        }
    }

    void DrawLines(List<float> verts, TkMat mvp, TkVec4 color)
    {
        if (verts.Count == 0)
            return;

        GL.UseProgram(_program);
        GL.UniformMatrix4(GL.GetUniformLocation(_program, "uMvp"), false, ref mvp);
        GL.Uniform1(GL.GetUniformLocation(_program, "uUseTex"), 0);
        GL.Uniform4(GL.GetUniformLocation(_program, "uColor"), color);

        GL.BindVertexArray(_lineVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _lineVbo);
        GL.BufferData(
            BufferTarget.ArrayBuffer,
            verts.Count * sizeof(float),
            verts.ToArray(),
            BufferUsageHint.DynamicDraw);

        GL.DrawArrays(PrimitiveType.Lines, 0, verts.Count / 8);
    }

    void OnMouseMove(object? sender, MouseEventArgs e)
    {
        int dx = e.X - _lastMouse.X;
        int dy = e.Y - _lastMouse.Y;
        _lastMouse = e.Location;

        if (_dragButton == MouseButtons.Left)
        {
            _yaw += dx * 0.45f;
            _pitch = Math.Clamp(_pitch + dy * 0.45f, -89f, 89f);
        }
        else if (_dragButton is MouseButtons.Middle or MouseButtons.Right)
        {
            float scale = _viewRadius * 0.0025f;

            // Simple screen-space pan; sufficient for inspection.
            _center += new NumVec3(-dx * scale, dy * scale, 0f);
        }

        _gl.Invalidate();
    }

    static int CreateProgram(string vertexSource, string fragmentSource)
    {
        int vertex = GL.CreateShader(ShaderType.VertexShader);
        GL.ShaderSource(vertex, vertexSource);
        GL.CompileShader(vertex);
        CheckShader(vertex);

        int fragment = GL.CreateShader(ShaderType.FragmentShader);
        GL.ShaderSource(fragment, fragmentSource);
        GL.CompileShader(fragment);
        CheckShader(fragment);

        int program = GL.CreateProgram();
        GL.AttachShader(program, vertex);
        GL.AttachShader(program, fragment);
        GL.LinkProgram(program);

        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int ok);
        if (ok == 0)
            throw new InvalidOperationException(GL.GetProgramInfoLog(program));

        GL.DeleteShader(vertex);
        GL.DeleteShader(fragment);

        return program;
    }

    static void CheckShader(int shader)
    {
        GL.GetShader(shader, ShaderParameter.CompileStatus, out int ok);
        if (ok == 0)
            throw new InvalidOperationException(GL.GetShaderInfoLog(shader));
    }

    const string VertexShader = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNormal;
layout(location=2) in vec2 aUv;

uniform mat4 uMvp;

out vec3 vNormal;
out vec2 vUv;

void main()
{
    gl_Position = vec4(aPos, 1.0) * uMvp;
    vNormal = aNormal;
    vUv = aUv;
}";

    const string FragmentShader = @"#version 330 core
in vec3 vNormal;
in vec2 vUv;

uniform sampler2D uTex;
uniform int uUseTex;
uniform vec4 uColor;
uniform vec3 uLight;

out vec4 FragColor;

void main()
{
    vec4 c = (uUseTex == 1) ? texture(uTex, vUv) : uColor;

    vec3 n = length(vNormal) > 0.00001
        ? normalize(vNormal)
        : vec3(0.0, 1.0, 0.0);

    float lightValue = 0.55 + 0.45 * abs(dot(n, normalize(uLight)));

    // Research mode: keep alpha materials visible instead of accidentally hiding entire models.
    FragColor = vec4(c.rgb * lightValue, 1.0);
}";
}
