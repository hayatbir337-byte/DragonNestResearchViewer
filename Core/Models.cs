using System.Numerics;

namespace DragonNestResearchViewer.Core;

public sealed class DnMaterial
{
    public string Name { get; set; } = "";
    public string Effect { get; set; } = "";
    public float Alpha { get; set; }
    public int AlphaBlend { get; set; }
    public Dictionary<string, object?> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? DiffuseTexture => Properties.TryGetValue("g_DiffuseTex", out var v) ? v as string : null;
}

public sealed class DnSkin
{
    public string MeshFile { get; set; } = "";
    public int Version { get; set; }
    public List<DnMaterial> Materials { get; } = [];
}

public sealed class DnBone
{
    public string Name { get; set; } = "";
    public Matrix4x4 FileMatrix { get; set; } = Matrix4x4.Identity;
    public Matrix4x4 BindGlobal { get; set; } = Matrix4x4.Identity;
    public Matrix4x4 InverseBindGlobal { get; set; } = Matrix4x4.Identity;
}

public sealed class DnMeshPart
{
    public string ParentName { get; set; } = "";
    public string Name { get; set; } = "";
    public bool TriangleStrip { get; set; }
    public Vector3[] Vertices { get; set; } = [];
    public Vector3[] Normals { get; set; } = [];
    public Vector2[] UV0 { get; set; } = [];
    public int[] Indices { get; set; } = [];
    public short[][] RigIndices { get; set; } = [];
    public Vector4[] RigWeights { get; set; } = [];
    public string[] RigNames { get; set; } = [];
    public int MaterialIndex { get; set; }
}

public sealed class DnMesh
{
    public int Version { get; set; }
    public Vector3 BoundsMin { get; set; }
    public Vector3 BoundsMax { get; set; }
    public List<DnBone> Bones { get; } = [];
    public List<DnMeshPart> Parts { get; } = [];
    public Dictionary<string, int> BoneIndexByName { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public readonly record struct VecKey(int Frame, Vector3 Value);
public readonly record struct QuatKey(int Frame, Quaternion Value);

public sealed class BoneAnimation
{
    public Vector3 BaseLocation { get; set; }
    public Quaternion BaseRotation { get; set; } = Quaternion.Identity;
    public Vector3 BaseScale { get; set; } = Vector3.One;
    public List<VecKey> Locations { get; } = [];
    public List<QuatKey> Rotations { get; } = [];
    public List<VecKey> Scales { get; } = [];
}

public sealed class AnimatedBone
{
    public string Name { get; set; } = "";
    public string ParentName { get; set; } = "";
    public List<BoneAnimation> Animations { get; } = [];
}

public sealed class DnAnimationSet
{
    public int Version { get; set; }
    public List<string> Names { get; } = [];
    public List<int> FrameCounts { get; } = [];
    public List<AnimatedBone> Bones { get; } = [];
    public bool IsAnimStream { get; set; }
}