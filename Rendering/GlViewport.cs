using System.Numerics;
using OpenTK.GLControl;
using OpenTK.Graphics.OpenGL4;
using Pfim;
using DragonNestResearchViewer.Core;
using NumMat = System.Numerics.Matrix4x4;
using NumVec3 = System.Numerics.Vector3;
using NumVec2 = System.Numerics.Vector2;
using TkMat = OpenTK.Mathematics.Matrix4;
using TkVec3 = OpenTK.Mathematics.Vector3;
using TkVec4 = OpenTK.Mathematics.Vector4;
using TkMathHelper = OpenTK.Mathematics.MathHelper;

namespace DragonNestResearchViewer.Rendering;

public sealed class GlViewport : UserControl
{
    readonly GLControl _gl;
    int _program;
    int _lineVao, _lineVbo;
    bool _ready;
    DnMesh? _mesh;
    DnSkin? _skin;
    DnAnimationSet? _ani;
    AnimationEvaluator? _evaluator;
    string _directory = "";
    int _animationIndex;
    float _frame;
    bool _useAnimation;

    sealed class GpuPart
    {
        public int Vao, Vbo, Ebo, Texture;
        public int IndexCount;
        public DnMeshPart Part = null!;
        public float[] Dynamic = [];
        public string TextureName = "";
    }

    readonly List<GpuPart> _parts = [];

    float _yaw = -35f;
    float _pitch = 18f;
    float _distance = 5f;
    NumVec3 _center = NumVec3.Zero;
    Point _lastMouse;
    MouseButtons _dragButton;

    public bool ShowSkeleton { get; set; }
    public bool ShowGrid { get; set; } = true;
    public bool Wireframe { get; set; }
    public bool UseAnimation
    {
        get => _useAnimation;
        set
        {
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
        }) { Dock = DockStyle.Fill };

        Controls.Add(_gl);

        _gl.Load += (_, _) => InitGl();
        _gl.Paint += (_, _) => Render();
        _gl.Resize += (_, _) =>
        {
            if (_ready)
            {
                _gl.MakeCurrent();
                GL.Viewport(0, 0, Math.Max(1, _gl.Width), Math.Max(1, _gl.Height));
            }
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
            _distance *= e.Delta > 0 ? 0.88f : 1.14f;
            _distance = Math.Clamp(_distance, 0.01f, 100000f);
            _gl.Invalidate();
        };
    }

    void InitGl()
    {
        _gl.MakeCurrent();
        GL.ClearColor(0.055f, 0.065f, 0.08f, 1f);
        GL.Enable(EnableCap.DepthTest);

        // Dragon Nest mesh winding varies by asset/version.
        // V1 deliberately renders both sides so a valid mesh never disappears due to culling.
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
        if (_mesh != null) UploadModel();
    }

    static void SetupVertexLayout()
    {
        int stride = 8 * sizeof(float);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
        GL.EnableVertexAttribArray(2);
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
            _gl.Invalidate();
        }
    }

    public void SetAnimation(DnAnimationSet? ani)
    {
        _ani = ani;
        _evaluator = (_mesh != null && ani != null) ? new AnimationEvaluator(_mesh, ani) : null;
        _animationIndex = 0;
        _frame = 0;

        // Important: keep the raw bind/static model visible after loading ANI.
        // Animation deformation begins only when Play or timeline scrub is used.
        _useAnimation = false;
        UpdateDynamicVertices();
        _gl.Invalidate();
    }

    public void FitToModel()
    {
        if (_mesh == null || _mesh.Parts.Count == 0)
            return;

        bool any = false;
        var min = new NumVec3(float.PositiveInfinity);
        var max = new NumVec3(float.NegativeInfinity);

        foreach (var part in _mesh.Parts)
        {
            foreach (var p in part.Vertices)
            {
                if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z))
                    continue;
                min = NumVec3.Min(min, p);
                max = NumVec3.Max(max, p);
                any = true;
            }
        }

        if (!any)
        {
            min = _mesh.BoundsMin;
            max = _mesh.BoundsMax;
        }

        _center = (min + max) * 0.5f;
        float radius = Math.Max(0.001f, (max - min).Length() * 0.5f);
        _distance = Math.Clamp(radius * 2.8f, 0.02f, 100000f);
        _yaw = -35f;
        _pitch = 18f;
        _gl.Invalidate();
    }

    public string TextureSummary()
    {
        if (_parts.Count == 0) return "Texture: model yüklenmedi";
        int loaded = _parts.Count(p => p.Texture != 0);
        var names = _parts.Where(p => !string.IsNullOrWhiteSpace(p.TextureName))
                          .Select(p => Path.GetFileName(p.TextureName))
                          .Distinct(StringComparer.OrdinalIgnoreCase)
                          .ToArray();
        return $"Texture: {loaded}/{_parts.Count} yüklendi" +
               (names.Length > 0 ? " | " + string.Join(", ", names) : "");
    }

    void UploadModel()
    {
        foreach (var p in _parts)
        {
            GL.DeleteBuffer(p.Vbo);
            GL.DeleteBuffer(p.Ebo);
            GL.DeleteVertexArray(p.Vao);
            if (p.Texture != 0) GL.DeleteTexture(p.Texture);
        }
        _parts.Clear();

        if (_mesh == null)
            return;

        foreach (var part in _mesh.Parts)
        {
            var gp = new GpuPart
            {
                Part = part,
                IndexCount = part.Indices.Length,
                Dynamic = new float[part.Vertices.Length * 8]
            };

            gp.Vao = GL.GenVertexArray();
            gp.Vbo = GL.GenBuffer();
            gp.Ebo = GL.GenBuffer();

            GL.BindVertexArray(gp.Vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, gp.Vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, gp.Dynamic.Length * sizeof(float), gp.Dynamic, BufferUsageHint.DynamicDraw);

            GL.BindBuffer(BufferTarget.ElementArrayBuffer, gp.Ebo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, part.Indices.Length * sizeof(int), part.Indices, BufferUsageHint.StaticDraw);

            SetupVertexLayout();

            if (_skin != null && part.MaterialIndex < _skin.Materials.Count)
            {
                var texName = _skin.Materials[part.MaterialIndex].DiffuseTexture;
                if (!string.IsNullOrWhiteSpace(texName))
                {
                    gp.TextureName = texName;
                    var texPath = ResolveTexturePath(_directory, texName);
                    if (texPath != null)
                        gp.Texture = TryLoadTexture(texPath);
                }
            }

            _parts.Add(gp);
        }

        UpdateDynamicVertices();
    }

    string? ResolveTexturePath(string dir, string raw)
    {
        string normalized = raw.Replace('\\', Path.DirectorySeparatorChar)
                               .Replace('/', Path.DirectorySeparatorChar);

        string direct = Path.Combine(dir, normalized);
        if (File.Exists(direct)) return direct;

        string byName = Path.Combine(dir, Path.GetFileName(normalized));
        if (File.Exists(byName)) return byName;

        // Some SKN paths contain a resource-relative path.
        if (!string.IsNullOrWhiteSpace(_directory))
        {
            var cursor = new DirectoryInfo(_directory);
            for (int up = 0; up < 8 && cursor != null; up++, cursor = cursor.Parent)
            {
                string candidate = Path.Combine(cursor.FullName, normalized);
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    int TryLoadTexture(string path)
    {
        try
        {
            using var image = Pfimage.FromFile(path);

            PixelFormat pf;
            PixelInternalFormat internalFmt;

            switch (image.Format)
            {
                case ImageFormat.Rgba32:
                    pf = PixelFormat.Bgra;
                    internalFmt = PixelInternalFormat.Rgba;
                    break;
                case ImageFormat.Rgb24:
                    pf = PixelFormat.Bgr;
                    internalFmt = PixelInternalFormat.Rgb;
                    break;
                case ImageFormat.Rgb8:
                    pf = PixelFormat.Red;
                    internalFmt = PixelInternalFormat.R8;
                    break;
                default:
                    return 0;
            }

            int id = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, id);
            GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            GL.TexImage2D(
                TextureTarget.Texture2D, 0, internalFmt,
                image.Width, image.Height, 0,
                pf, PixelType.UnsignedByte, image.Data);

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

                if (pose != null &&
                    p.RigIndices.Length == p.Vertices.Length &&
                    p.RigWeights.Length == p.Vertices.Length)
                {
                    NumVec3 skinnedPos = NumVec3.Zero;
                    NumVec3 skinnedNormal = NumVec3.Zero;
                    Vector4 weights = p.RigWeights[i];
                    float[] ws = [weights.X, weights.Y, weights.Z, weights.W];
                    float total = 0f;

                    for (int k = 0; k < 4; k++)
                    {
                        float w = ws[k];
                        if (w <= 0f) continue;

                        int localIndex = p.RigIndices[i][k];
                        if (localIndex < 0 || localIndex >= p.RigNames.Length) continue;

                        if (!_mesh.BoneIndexByName.TryGetValue(p.RigNames[localIndex], out int boneIndex))
                            continue;

                        var skinMatrix = _mesh.Bones[boneIndex].InverseBindGlobal * pose[boneIndex];
                        skinnedPos += NumVec3.Transform(pos, skinMatrix) * w;
                        skinnedNormal += NumVec3.TransformNormal(normal, skinMatrix) * w;
                        total += w;
                    }

                    if (total > 0.0001f)
                    {
                        pos = skinnedPos / total;
                        if (skinnedNormal.LengthSquared() > 0.000001f)
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
                gp.Dynamic[o + 6] = uv.X;
                gp.Dynamic[o + 7] = uv.Y;
            }

            GL.BindBuffer(BufferTarget.ArrayBuffer, gp.Vbo);
            GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, gp.Dynamic.Length * sizeof(float), gp.Dynamic);
        }
    }

    void Render()
    {
        if (!_ready || !_gl.HasValidContext)
            return;

        _gl.MakeCurrent();
        GL.Viewport(0, 0, Math.Max(1, _gl.Width), Math.Max(1, _gl.Height));
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        var eye = CameraPosition();
        var view = TkMat.LookAt(
            new TkVec3(eye.X, eye.Y, eye.Z),
            new TkVec3(_center.X, _center.Y, _center.Z),
            TkVec3.UnitY);

        float aspect = Math.Max(0.01f, _gl.Width / (float)Math.Max(1, _gl.Height));
        var projection = TkMat.CreatePerspectiveFieldOfView(
            TkMathHelper.DegreesToRadians(50f),
            aspect,
            Math.Max(0.0001f, _distance / 5000f),
            Math.Max(100f, _distance * 100f));

        var mvp = view * projection;

        GL.UseProgram(_program);
        GL.UniformMatrix4(GL.GetUniformLocation(_program, "uMvp"), false, ref mvp);
        GL.Uniform3(GL.GetUniformLocation(_program, "uLight"), 0.35f, 0.85f, 0.45f);

        if (ShowGrid)
            DrawGrid(mvp);

        GL.PolygonMode(MaterialFace.FrontAndBack, Wireframe ? PolygonMode.Line : PolygonMode.Fill);

        foreach (var p in _parts)
        {
            GL.BindVertexArray(p.Vao);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, p.Texture);
            GL.Uniform1(GL.GetUniformLocation(_program, "uTex"), 0);
            GL.Uniform1(GL.GetUniformLocation(_program, "uUseTex"), p.Texture != 0 ? 1 : 0);
            GL.Uniform4(GL.GetUniformLocation(_program, "uColor"), 0.78f, 0.82f, 0.90f, 1f);
            GL.DrawElements(PrimitiveType.Triangles, p.IndexCount, DrawElementsType.UnsignedInt, 0);
        }

        GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);

        if (ShowSkeleton && _mesh != null)
            DrawSkeleton(mvp);

        _gl.SwapBuffers();
    }

    void DrawGrid(TkMat mvp)
    {
        var verts = new List<float>();
        float span = Math.Max(0.5f, _distance);
        float step = (float)Math.Pow(10, Math.Floor(Math.Log10(Math.Max(span / 10f, 0.0001f))));
        int n = 12;

        for (int i = -n; i <= n; i++)
        {
            float x = i * step;
            AddLine(verts, new NumVec3(x, 0, -n * step), new NumVec3(x, 0, n * step));
            AddLine(verts, new NumVec3(-n * step, 0, x), new NumVec3(n * step, 0, x));
        }

        DrawLines(verts, mvp, new TkVec4(0.19f, 0.22f, 0.27f, 1f));
    }

    void DrawSkeleton(TkMat mvp)
    {
        if (_mesh == null) return;

        NumMat[] pose = (_useAnimation && _evaluator != null)
            ? _evaluator.EvaluatePose(_animationIndex, _frame)
            : _mesh.Bones.Select(b => b.BindGlobal).ToArray();

        var verts = new List<float>();

        if (_ani != null)
        {
            var parents = _ani.Bones.ToDictionary(b => b.Name, b => b.ParentName, StringComparer.OrdinalIgnoreCase);

            foreach (var b in _mesh.Bones)
            {
                if (!parents.TryGetValue(b.Name, out var parentName) || string.IsNullOrEmpty(parentName))
                    continue;
                if (!_mesh.BoneIndexByName.TryGetValue(parentName, out int parentIndex))
                    continue;

                int childIndex = _mesh.BoneIndexByName[b.Name];
                AddLine(verts, Translation(pose[parentIndex]), Translation(pose[childIndex]));
            }
        }
        else
        {
            float marker = Math.Max(0.001f, _distance * 0.02f);
            foreach (var b in _mesh.Bones)
            {
                var p = Translation(b.BindGlobal);
                AddLine(verts, p, p + NumVec3.UnitY * marker);
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
            data.Add(0); data.Add(1); data.Add(0);
            data.Add(0); data.Add(0);
        }
    }

    void DrawLines(List<float> verts, TkMat mvp, TkVec4 color)
    {
        if (verts.Count == 0) return;

        GL.UseProgram(_program);
        GL.UniformMatrix4(GL.GetUniformLocation(_program, "uMvp"), false, ref mvp);
        GL.Uniform1(GL.GetUniformLocation(_program, "uUseTex"), 0);
        GL.Uniform4(GL.GetUniformLocation(_program, "uColor"), color);

        GL.BindVertexArray(_lineVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _lineVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, verts.Count * sizeof(float), verts.ToArray(), BufferUsageHint.DynamicDraw);
        GL.DrawArrays(PrimitiveType.Lines, 0, verts.Count / 8);
    }

    NumVec3 CameraPosition()
    {
        float yaw = MathF.PI * _yaw / 180f;
        float pitch = MathF.PI * _pitch / 180f;

        var direction = new NumVec3(
            MathF.Cos(pitch) * MathF.Cos(yaw),
            MathF.Sin(pitch),
            MathF.Cos(pitch) * MathF.Sin(yaw));

        return _center - direction * _distance;
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
        else if (_dragButton == MouseButtons.Middle || _dragButton == MouseButtons.Right)
        {
            float s = _distance * 0.0025f;
            _center += new NumVec3(-dx * s, dy * s, 0);
        }

        _gl.Invalidate();
    }

    static int CreateProgram(string vs, string fs)
    {
        int v = GL.CreateShader(ShaderType.VertexShader);
        GL.ShaderSource(v, vs);
        GL.CompileShader(v);
        CheckShader(v);

        int f = GL.CreateShader(ShaderType.FragmentShader);
        GL.ShaderSource(f, fs);
        GL.CompileShader(f);
        CheckShader(f);

        int p = GL.CreateProgram();
        GL.AttachShader(p, v);
        GL.AttachShader(p, f);
        GL.LinkProgram(p);

        GL.GetProgram(p, GetProgramParameterName.LinkStatus, out int ok);
        if (ok == 0)
            throw new InvalidOperationException(GL.GetProgramInfoLog(p));

        GL.DeleteShader(v);
        GL.DeleteShader(f);
        return p;
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
    if (c.a < 0.03) discard;
    vec3 n = normalize(vNormal);
    float diffuse = 0.45 + 0.55 * abs(dot(n, normalize(uLight)));
    FragColor = vec4(c.rgb * diffuse, c.a);
}";
}
