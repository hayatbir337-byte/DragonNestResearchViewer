using System.Numerics;
using OpenTK.GLControl;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using Pfim;
using DragonNestResearchViewer.Core;
using NumMat = System.Numerics.Matrix4x4;
using TkMat = OpenTK.Mathematics.Matrix4;

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

    sealed class GpuPart
    {
        public int Vao, Vbo, Ebo, Texture;
        public int IndexCount;
        public DnMeshPart Part = null!;
        public float[] Dynamic = [];
    }
    readonly List<GpuPart> _parts = [];

    float _yaw = -25f, _pitch = 15f, _distance = 5f;
    Vector3 _center = Vector3.Zero;
    Point _lastMouse;
    MouseButtons _dragButton;

    public bool ShowSkeleton { get; set; } = true;
    public bool ShowGrid { get; set; } = true;
    public bool Wireframe { get; set; }
    public int AnimationIndex { get => _animationIndex; set { _animationIndex = Math.Max(0, value); _gl.Invalidate(); } }
    public float Frame { get => _frame; set { _frame = Math.Max(0, value); UpdateDynamicVertices(); _gl.Invalidate(); } }

    public GlViewport()
    {
        Dock = DockStyle.Fill;
        _gl = new GLControl(new GLControlSettings { APIVersion = new Version(3,3), NumberOfSamples = 4 }) { Dock = DockStyle.Fill };
        Controls.Add(_gl);
        _gl.Load += (_,_) => InitGl();
        _gl.Paint += (_,_) => Render();
        _gl.Resize += (_,_) => { if (_ready) { _gl.MakeCurrent(); GL.Viewport(0,0,_gl.Width,_gl.Height); } };
        _gl.MouseDown += (_,e) => { _lastMouse = e.Location; _dragButton = e.Button; };
        _gl.MouseUp += (_,_) => _dragButton = MouseButtons.None;
        _gl.MouseMove += OnMouseMove;
        _gl.MouseWheel += (_,e) => { _distance *= e.Delta > 0 ? 0.88f : 1.14f; _distance = Math.Clamp(_distance, 0.05f, 100000f); _gl.Invalidate(); };
    }

    void InitGl()
    {
        _gl.MakeCurrent();
        GL.ClearColor(0.075f,0.085f,0.105f,1f);
        GL.Enable(EnableCap.DepthTest);
        GL.Enable(EnableCap.CullFace);
        GL.CullFace(CullFaceMode.Back);
        _program = CreateProgram(VertexShader, FragmentShader);
        _lineVao = GL.GenVertexArray();
        _lineVbo = GL.GenBuffer();
        GL.BindVertexArray(_lineVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _lineVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, 1, IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.VertexAttribPointer(0,3,VertexAttribPointerType.Float,false,8*sizeof(float),0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1,3,VertexAttribPointerType.Float,false,8*sizeof(float),3*sizeof(float));
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(2,2,VertexAttribPointerType.Float,false,8*sizeof(float),6*sizeof(float));
        GL.EnableVertexAttribArray(2);
        _ready = true;
        if (_mesh != null) UploadModel();
    }

    public void SetModel(DnMesh mesh, DnSkin? skin, string directory)
    {
        _mesh = mesh; _skin = skin; _directory = directory;
        _ani = null; _evaluator = null; _animationIndex = 0; _frame = 0;
        var size = mesh.BoundsMax - mesh.BoundsMin;
        _center = (mesh.BoundsMax + mesh.BoundsMin) * 0.5f;
        _distance = Math.Max(0.1f, size.Length() * 1.4f);
        if (_ready) { _gl.MakeCurrent(); UploadModel(); _gl.Invalidate(); }
    }

    public void SetAnimation(DnAnimationSet? ani)
    {
        _ani = ani;
        _evaluator = (_mesh != null && ani != null) ? new AnimationEvaluator(_mesh, ani) : null;
        _animationIndex = 0; _frame = 0;
        UpdateDynamicVertices();
        _gl.Invalidate();
    }

    void UploadModel()
    {
        foreach (var p in _parts)
        {
            GL.DeleteBuffer(p.Vbo); GL.DeleteBuffer(p.Ebo); GL.DeleteVertexArray(p.Vao);
            if (p.Texture != 0) GL.DeleteTexture(p.Texture);
        }
        _parts.Clear();
        if (_mesh == null) return;

        foreach (var part in _mesh.Parts)
        {
            var gp = new GpuPart { Part = part, IndexCount = part.Indices.Length, Dynamic = new float[part.Vertices.Length * 8] };
            gp.Vao = GL.GenVertexArray(); gp.Vbo = GL.GenBuffer(); gp.Ebo = GL.GenBuffer();
            GL.BindVertexArray(gp.Vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, gp.Vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, gp.Dynamic.Length*sizeof(float), gp.Dynamic, BufferUsageHint.DynamicDraw);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, gp.Ebo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, part.Indices.Length*sizeof(int), part.Indices, BufferUsageHint.StaticDraw);
            GL.VertexAttribPointer(0,3,VertexAttribPointerType.Float,false,8*sizeof(float),0); GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(1,3,VertexAttribPointerType.Float,false,8*sizeof(float),3*sizeof(float)); GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(2,2,VertexAttribPointerType.Float,false,8*sizeof(float),6*sizeof(float)); GL.EnableVertexAttribArray(2);

            if (_skin != null && part.MaterialIndex < _skin.Materials.Count)
            {
                var texName = _skin.Materials[part.MaterialIndex].DiffuseTexture;
                if (!string.IsNullOrWhiteSpace(texName))
                {
                    var texPath = ResolveTexturePath(_directory, texName);
                    if (texPath != null) gp.Texture = TryLoadTexture(texPath);
                }
            }
            _parts.Add(gp);
        }
        UpdateDynamicVertices();
    }

    string? ResolveTexturePath(string dir, string raw)
    {
        var normalized = raw.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        var direct = Path.Combine(dir, normalized);
        if (File.Exists(direct)) return direct;
        var byName = Path.Combine(dir, Path.GetFileName(normalized));
        if (File.Exists(byName)) return byName;
        return null;
    }

    int TryLoadTexture(string path)
    {
        try
        {
            using var image = Pfimage.FromFile(path);
            PixelFormat pf;
            PixelType pt = PixelType.UnsignedByte;
            PixelInternalFormat internalFmt;
            switch (image.Format)
            {
                case ImageFormat.Rgba32: pf = PixelFormat.Bgra; internalFmt = PixelInternalFormat.Rgba; break;
                case ImageFormat.Rgb24: pf = PixelFormat.Bgr; internalFmt = PixelInternalFormat.Rgb; break;
                case ImageFormat.Rgb8: pf = PixelFormat.Red; internalFmt = PixelInternalFormat.R8; break;
                default: return 0;
            }
            int id = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D,id);
            GL.TexImage2D(TextureTarget.Texture2D,0,internalFmt,image.Width,image.Height,0,pf,pt,image.Data);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.LinearMipmapLinear);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapS,(int)TextureWrapMode.Repeat);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapT,(int)TextureWrapMode.Repeat);
            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
            return id;
        }
        catch { return 0; }
    }

    void UpdateDynamicVertices()
    {
        if (!_ready || _mesh == null) return;
        _gl.MakeCurrent();
        NumMat[]? pose = _evaluator?.EvaluatePose(_animationIndex,_frame);

        foreach (var gp in _parts)
        {
            var p = gp.Part;
            for (int i=0;i<p.Vertices.Length;i++)
            {
                Vector3 pos = p.Vertices[i], normal = p.Normals[i];
                if (pose != null && p.RigIndices.Length == p.Vertices.Length && p.RigWeights.Length == p.Vertices.Length)
                {
                    Vector3 sp = Vector3.Zero, sn = Vector3.Zero;
                    var weights = p.RigWeights[i];
                    float[] ws = [weights.X,weights.Y,weights.Z,weights.W];
                    float total = 0;
                    for (int k=0;k<4;k++)
                    {
                        float w=ws[k]; if (w<=0) continue;
                        int local=p.RigIndices[i][k];
                        if (local<0 || local>=p.RigNames.Length) continue;
                        if (!_mesh.BoneIndexByName.TryGetValue(p.RigNames[local],out int bi)) continue;
                        var skinMat = _mesh.Bones[bi].InverseBindGlobal * pose[bi];
                        sp += Vector3.Transform(pos,skinMat)*w;
                        sn += Vector3.TransformNormal(normal,skinMat)*w;
                        total += w;
                    }
                    if (total>0) { pos=sp/total; normal=Vector3.Normalize(sn); }
                }
                int o=i*8;
                gp.Dynamic[o]=pos.X; gp.Dynamic[o+1]=pos.Y; gp.Dynamic[o+2]=pos.Z;
                gp.Dynamic[o+3]=normal.X; gp.Dynamic[o+4]=normal.Y; gp.Dynamic[o+5]=normal.Z;
                var uv = i<p.UV0.Length ? p.UV0[i] : Vector2.Zero;
                gp.Dynamic[o+6]=uv.X; gp.Dynamic[o+7]=uv.Y;
            }
            GL.BindBuffer(BufferTarget.ArrayBuffer,gp.Vbo);
            GL.BufferSubData(BufferTarget.ArrayBuffer,IntPtr.Zero,gp.Dynamic.Length*sizeof(float),gp.Dynamic);
        }
    }

    void Render()
    {
        if (!_ready || !_gl.HasValidContext) return;
        _gl.MakeCurrent();
        GL.Viewport(0,0,Math.Max(1,_gl.Width),Math.Max(1,_gl.Height));
        GL.Clear(ClearBufferMask.ColorBufferBit|ClearBufferMask.DepthBufferBit);

        var eye = CameraPosition();
        var view = TkMat.LookAt(new OpenTK.Mathematics.Vector3(eye.X,eye.Y,eye.Z),
            new OpenTK.Mathematics.Vector3(_center.X,_center.Y,_center.Z), OpenTK.Mathematics.Vector3.UnitY);
        float aspect=Math.Max(0.01f,_gl.Width/(float)Math.Max(1,_gl.Height));
        var projection=TkMat.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(50),aspect,Math.Max(0.001f,_distance/1000f),Math.Max(100f,_distance*50f));
        var mvp=view*projection;

        GL.UseProgram(_program);
        GL.UniformMatrix4(GL.GetUniformLocation(_program,"uMvp"),false,ref mvp);
        GL.Uniform3(GL.GetUniformLocation(_program,"uLight"),0.3f,0.8f,0.5f);

        if (ShowGrid) DrawGrid(mvp);

        GL.PolygonMode(MaterialFace.FrontAndBack,Wireframe?PolygonMode.Line:PolygonMode.Fill);
        foreach(var p in _parts)
        {
            GL.BindVertexArray(p.Vao);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D,p.Texture);
            GL.Uniform1(GL.GetUniformLocation(_program,"uTex"),0);
            GL.Uniform1(GL.GetUniformLocation(_program,"uUseTex"),p.Texture!=0?1:0);
            GL.Uniform4(GL.GetUniformLocation(_program,"uColor"),1f,1f,1f,1f);
            GL.DrawElements(PrimitiveType.Triangles,p.IndexCount,DrawElementsType.UnsignedInt,0);
        }
        GL.PolygonMode(MaterialFace.FrontAndBack,PolygonMode.Fill);

        if (ShowSkeleton && _mesh != null) DrawSkeleton(mvp);
        _gl.SwapBuffers();
    }

    void DrawGrid(TkMat mvp)
    {
        var verts=new List<float>();
        float span=Math.Max(5f,_distance);
        float step=(float)Math.Pow(10,Math.Floor(Math.Log10(span/10f)));
        int n=20;
        for(int i=-n;i<=n;i++)
        {
            float x=i*step;
            AddLine(verts,new Vector3(x,0,-n*step),new Vector3(x,0,n*step));
            AddLine(verts,new Vector3(-n*step,0,x),new Vector3(n*step,0,x));
        }
        DrawLines(verts,mvp,new OpenTK.Mathematics.Vector4(0.24f,0.27f,0.31f,1));
    }

    void DrawSkeleton(TkMat mvp)
    {
        if (_mesh==null) return;
        var pose=_evaluator?.EvaluatePose(_animationIndex,_frame) ?? _mesh.Bones.Select(b=>b.BindGlobal).ToArray();
        var aniParents=_ani?.Bones.ToDictionary(b=>b.Name,b=>b.ParentName,StringComparer.OrdinalIgnoreCase);
        var verts=new List<float>();
        if(aniParents!=null)
        {
            foreach(var b in _mesh.Bones)
            {
                if(!aniParents.TryGetValue(b.Name,out var pn)||string.IsNullOrEmpty(pn)) continue;
                if(!_mesh.BoneIndexByName.TryGetValue(pn,out int pi)) continue;
                int ci=_mesh.BoneIndexByName[b.Name];
                AddLine(verts,Translation(pose[pi]),Translation(pose[ci]));
            }
        }
        else
        {
            foreach(var b in _mesh.Bones)
            {
                var p=Translation(b.BindGlobal);
                AddLine(verts,p,p+Vector3.UnitY*Math.Max(0.01f,_distance*0.03f));
            }
        }
        DrawLines(verts,mvp,new OpenTK.Mathematics.Vector4(1f,0.65f,0.05f,1));
    }

    static Vector3 Translation(NumMat m)=>new(m.M41,m.M42,m.M43);
    static void AddLine(List<float> v,Vector3 a,Vector3 b)
    {
        foreach(var p in new[]{a,b}) { v.Add(p.X);v.Add(p.Y);v.Add(p.Z);v.Add(0);v.Add(1);v.Add(0);v.Add(0);v.Add(0); }
    }

    void DrawLines(List<float> verts,TkMat mvp,OpenTK.Mathematics.Vector4 color)
    {
        if(verts.Count==0)return;
        GL.UseProgram(_program);
        GL.UniformMatrix4(GL.GetUniformLocation(_program,"uMvp"),false,ref mvp);
        GL.Uniform1(GL.GetUniformLocation(_program,"uUseTex"),0);
        GL.Uniform4(GL.GetUniformLocation(_program,"uColor"),color);
        GL.BindVertexArray(_lineVao);GL.BindBuffer(BufferTarget.ArrayBuffer,_lineVbo);
        GL.BufferData(BufferTarget.ArrayBuffer,verts.Count*sizeof(float),verts.ToArray(),BufferUsageHint.DynamicDraw);
        GL.DrawArrays(PrimitiveType.Lines,0,verts.Count/8);
    }

    Vector3 CameraPosition()
    {
        float yaw=MathF.PI*_yaw/180f,pitch=MathF.PI*_pitch/180f;
        var dir=new Vector3(MathF.Cos(pitch)*MathF.Cos(yaw),MathF.Sin(pitch),MathF.Cos(pitch)*MathF.Sin(yaw));
        return _center-dir*_distance;
    }

    void OnMouseMove(object? sender,MouseEventArgs e)
    {
        var dx=e.X-_lastMouse.X;var dy=e.Y-_lastMouse.Y;_lastMouse=e.Location;
        if(_dragButton==MouseButtons.Left){_yaw+=dx*0.45f;_pitch=Math.Clamp(_pitch+dy*0.45f,-89f,89f);}
        else if(_dragButton==MouseButtons.Middle||_dragButton==MouseButtons.Right)
        {
            float s=_distance*0.0025f;
            _center+=new Vector3(-dx*s,dy*s,0);
        }
        _gl.Invalidate();
    }

    static int CreateProgram(string vs,string fs)
    {
        int V=GL.CreateShader(ShaderType.VertexShader);GL.ShaderSource(V,vs);GL.CompileShader(V);CheckShader(V);
        int F=GL.CreateShader(ShaderType.FragmentShader);GL.ShaderSource(F,fs);GL.CompileShader(F);CheckShader(F);
        int P=GL.CreateProgram();GL.AttachShader(P,V);GL.AttachShader(P,F);GL.LinkProgram(P);
        GL.GetProgram(P,GetProgramParameterName.LinkStatus,out int ok);
        if(ok==0)throw new InvalidOperationException(GL.GetProgramInfoLog(P));
        GL.DeleteShader(V);GL.DeleteShader(F);return P;
    }
    static void CheckShader(int s){GL.GetShader(s,ShaderParameter.CompileStatus,out int ok);if(ok==0)throw new InvalidOperationException(GL.GetShaderInfoLog(s));}

    const string VertexShader=@"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNormal;
layout(location=2) in vec2 aUv;
uniform mat4 uMvp;
out vec3 vNormal;
out vec2 vUv;
void main(){ gl_Position=vec4(aPos,1.0)*uMvp; vNormal=aNormal; vUv=aUv; }";
    const string FragmentShader=@"#version 330 core
in vec3 vNormal;
in vec2 vUv;
uniform sampler2D uTex;
uniform int uUseTex;
uniform vec4 uColor;
uniform vec3 uLight;
out vec4 FragColor;
void main(){
 vec4 c=uUseTex==1?texture(uTex,vUv):uColor;
 if(c.a<0.03) discard;
 float l=0.45+0.55*max(dot(normalize(vNormal),normalize(uLight)),0.0);
 FragColor=vec4(c.rgb*l,c.a);
}";
}