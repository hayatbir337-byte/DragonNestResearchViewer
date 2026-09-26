using System.Numerics;

namespace DragonNestResearchViewer.Core;

public static class DnParsers
{
    static readonly Matrix4x4 Orientation = new(
        1,0,0,0,
        0,0,1,0,
        0,1,0,0,
        0,0,0,1);

    public static DnSkin LoadSkn(string path)
    {
        using var r = new DnBinaryReader(File.OpenRead(path));
        var type = r.ReadFixedString(256);
        if (!type.StartsWith("Eternity Engine Skin File", StringComparison.Ordinal))
            throw new InvalidDataException("Not an Eternity Engine SKN.");

        var skin = new DnSkin { MeshFile = r.ReadFixedString(256), Version = r.ReadInt32() };
        int materialCount = r.ReadInt32();
        int bodySize = r.ReadInt32();
        int fragmentsOrder = r.ReadInt32();
        r.Position = 1024;

        byte[] body;
        if (skin.Version < 11)
        {
            body = r.ReadBytes((int)r.Remaining);
        }
        else
        {
            int[][] orders =
            [
                [2,3,0,4,1],
                [1,0,4,2,3],
                [4,3,0,1,2],
                [3,2,1,4,0],
                [3,2,4,0,1]
            ];
            if (fragmentsOrder < 0 || fragmentsOrder >= orders.Length)
                throw new InvalidDataException($"Unknown SKN fragment order {fragmentsOrder}.");

            int fragmentSize = bodySize / 5 / 2 * 2;
            int lastSize = bodySize - fragmentSize * 4;
            var physical = orders[fragmentsOrder];
            var fragments = new Dictionary<int, byte[]>();
            foreach (int logicalIndex in physical)
            {
                int size = logicalIndex == 4 ? lastSize : fragmentSize;
                fragments[logicalIndex] = r.ReadBytes(size);
            }
            using var ms = new MemoryStream();
            for (int i = 0; i < 5; i++) ms.Write(fragments[i]);
            body = ms.ToArray();
        }

        using var br = new DnBinaryReader(new MemoryStream(body));
        for (int i = 0; i < materialCount; i++)
        {
            var m = new DnMaterial
            {
                Name = br.ReadFixedString(256),
                Effect = br.ReadFixedString(256),
                Alpha = br.ReadSingle(),
                AlphaBlend = br.ReadInt32()
            };
            br.Skip(504);
            int propCount = br.ReadInt32();
            for (int p = 0; p < propCount; p++)
            {
                string name = br.ReadLengthString();
                int typeId = br.ReadInt32();
                object? value = typeId switch
                {
                    0 => br.ReadInt32(),
                    1 => br.ReadSingle(),
                    2 => br.ReadVector4(),
                    3 => br.ReadLengthString(),
                    4 => null,
                    _ => throw new InvalidDataException($"Unknown SKN property type {typeId}.")
                };
                m.Properties[name] = value;
            }
            skin.Materials.Add(m);
        }
        return skin;
    }

    public static DnMesh LoadMsh(string path)
    {
        using var r = new DnBinaryReader(File.OpenRead(path));
        var type = r.ReadFixedString(256);
        if (!type.StartsWith("Eternity Engine Mesh File", StringComparison.Ordinal))
            throw new InvalidDataException("Not an Eternity Engine MSH.");

        var mesh = new DnMesh { Version = r.ReadInt32() };
        int meshesNum = r.ReadInt32();
        _ = r.ReadInt32(); // lods
        r.Skip(4); // uv animation flags
        var bbMax = r.ReadVector3();
        var bbMin = r.ReadVector3();
        mesh.BoundsMax = ToViewer(bbMax);
        mesh.BoundsMin = ToViewer(bbMin);
        int bonesNum = r.ReadInt32();
        int collisionsNum = r.ReadInt32();
        int dummiesNum = r.ReadInt32();
        r.Position = 1024;

        for (int i = 0; i < bonesNum; i++)
        {
            string name = r.ReadFixedString(256);
            Matrix4x4 file = r.ReadMatrix4x4();
            // Blender reference: oriented(transpose(file).inverse())
            Matrix4x4 transposed = Matrix4x4.Transpose(file);
            if (!Matrix4x4.Invert(transposed, out var inv)) inv = Matrix4x4.Identity;
            var bind = Orientation * inv * Orientation;
            Matrix4x4.Invert(bind, out var inverseBind);
            mesh.BoneIndexByName[name] = mesh.Bones.Count;
            mesh.Bones.Add(new DnBone { Name = name, FileMatrix = file, BindGlobal = bind, InverseBindGlobal = inverseBind });
        }

        for (int mi = 0; mi < meshesNum; mi++)
        {
            var part = new DnMeshPart { MaterialIndex = mi };
            part.ParentName = r.ReadFixedString(256);
            part.Name = r.ReadFixedString(256);
            int verts = r.ReadInt32();
            int indices = r.ReadInt32();
            int uvSets = r.ReadInt32();
            part.TriangleStrip = r.ReadByte() != 0;
            bool useRig = r.ReadByte() != 0;
            bool useColor = r.ReadByte() != 0;
            r.ReadByte();
            r.Skip(496);

            var faces = new List<int>(Math.Max(3, indices));
            if (part.TriangleStrip)
            {
                if (indices >= 2)
                {
                    ushort v1 = r.ReadUInt16();
                    ushort v2 = r.ReadUInt16();
                    int direct = -1;
                    for (int x = 0; x < indices - 2; x++)
                    {
                        ushort v3 = r.ReadUInt16();
                        direct *= -1;
                        if (v1 != v2 && v2 != v3 && v1 != v3)
                        {
                            if (direct > 0) { faces.Add(v1); faces.Add(v3); faces.Add(v2); }
                            else { faces.Add(v1); faces.Add(v2); faces.Add(v3); }
                        }
                        v1 = v2; v2 = v3;
                    }
                }
            }
            else
            {
                for (int x = 0; x < indices / 3; x++)
                {
                    ushort a = r.ReadUInt16(), b = r.ReadUInt16(), c = r.ReadUInt16();
                    faces.Add(a); faces.Add(c); faces.Add(b);
                }
            }

            part.Vertices = new Vector3[verts];
            part.Normals = new Vector3[verts];
            for (int x = 0; x < verts; x++) part.Vertices[x] = ToViewer(r.ReadVector3());
            for (int x = 0; x < verts; x++)
            {
                var n = ToViewer(r.ReadVector3());
                part.Normals[x] = n.LengthSquared() > 0.000001f ? Vector3.Normalize(n) : Vector3.UnitY;
            }

            for (int set = 0; set < uvSets; set++)
            {
                var uv = new Vector2[verts];
                for (int x = 0; x < verts; x++)
                {
                    var t = r.ReadVector2();
                    uv[x] = new Vector2(t.X, 1f - t.Y);
                }
                if (set == 0) part.UV0 = uv;
            }
            if (part.UV0.Length == 0) part.UV0 = new Vector2[verts];

            if (useColor) r.Skip(verts * 4L);

            if (useRig)
            {
                part.RigIndices = new short[verts][];
                for (int x = 0; x < verts; x++)
                    part.RigIndices[x] = [r.ReadInt16(), r.ReadInt16(), r.ReadInt16(), r.ReadInt16()];
                part.RigWeights = new Vector4[verts];
                for (int x = 0; x < verts; x++)
                    part.RigWeights[x] = r.ReadVector4();
                int rigBones = r.ReadInt32();
                part.RigNames = new string[rigBones];
                for (int x = 0; x < rigBones; x++) part.RigNames[x] = r.ReadFixedString(256);
            }

            part.Indices = faces.ToArray();
            mesh.Parts.Add(part);
        }

        // Collisions/dummies deliberately ignored by V1; parser stops after renderable mesh data.
        _ = collisionsNum; _ = dummiesNum;
        return mesh;
    }

    public static DnAnimationSet LoadAni(string path)
    {
        if (path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
            return LoadAnimStream(path);

        using var r = new DnBinaryReader(File.OpenRead(path));
        var type = r.ReadFixedString(256);
        if (!type.StartsWith("Eternity Engine Ani File", StringComparison.Ordinal))
            throw new InvalidDataException("Not an Eternity Engine ANI.");

        var set = new DnAnimationSet { Version = r.ReadInt32() };
        int bones = r.ReadInt32();
        int anims = r.ReadInt32();
        r.Position = 1024;

        for (int i = 0; i < anims; i++) set.Names.Add(r.ReadFixedString(256));
        for (int i = 0; i < anims; i++) set.FrameCounts.Add(r.ReadInt32());

        for (int b = 0; b < bones; b++)
        {
            var bone = new AnimatedBone
            {
                Name = r.ReadFixedString(256),
                ParentName = r.ReadFixedString(256)
            };
            r.Skip(512);
            for (int a = 0; a < anims; a++)
                bone.Animations.Add(ReadAnimation(r, set.Version >= 11));
            set.Bones.Add(bone);
        }
        return set;
    }

    static DnAnimationSet LoadAnimStream(string path)
    {
        using var r = new DnBinaryReader(File.OpenRead(path));
        var set = new DnAnimationSet { Version = 11, IsAnimStream = true };
        set.Names.Add(Path.GetFileNameWithoutExtension(path));
        int maxFrame = 0;
        while (r.Remaining > 0)
        {
            var bone = new AnimatedBone { Name = r.ReadCString(), ParentName = r.ReadCString() };
            var anim = ReadAnimation(r, true);
            bone.Animations.Add(anim);
            set.Bones.Add(bone);
            maxFrame = Math.Max(maxFrame, MaxFrame(anim));
        }
        set.FrameCounts.Add(maxFrame + 1);
        return set;
    }

    static BoneAnimation ReadAnimation(DnBinaryReader r, bool compact)
    {
        var a = new BoneAnimation
        {
            BaseLocation = r.ReadVector3(),
            BaseRotation = ToQuaternion(r.ReadVector4()),
            BaseScale = r.ReadVector3()
        };
        int n = r.ReadInt32();
        for (int i = 0; i < n; i++) a.Locations.Add(new(ReadFrame(r, compact), r.ReadVector3()));
        n = r.ReadInt32();
        for (int i = 0; i < n; i++) a.Rotations.Add(new(ReadFrame(r, compact), compact ? ReadShortQuat(r) : ToQuaternion(r.ReadVector4())));
        n = r.ReadInt32();
        for (int i = 0; i < n; i++) a.Scales.Add(new(ReadFrame(r, compact), r.ReadVector3()));
        return a;
    }

    static int ReadFrame(DnBinaryReader r, bool compact) => compact ? r.ReadInt16() : r.ReadInt32();

    static Quaternion ReadShortQuat(DnBinaryReader r)
    {
        const float k = 1f / 32768f;
        return Quaternion.Normalize(new Quaternion(r.ReadInt16()*k, r.ReadInt16()*k, r.ReadInt16()*k, r.ReadInt16()*k));
    }

    static Quaternion ToQuaternion(Vector4 q) => Quaternion.Normalize(new Quaternion(q.X, q.Y, q.Z, q.W));
    static int MaxFrame(BoneAnimation a) =>
        new[] { a.Locations.LastOrDefault().Frame, a.Rotations.LastOrDefault().Frame, a.Scales.LastOrDefault().Frame }.Max();

    public static Vector3 ToViewer(Vector3 v) => new(v.X, v.Z, v.Y);
    public static Vector3 ToDn(Vector3 v) => new(v.X, v.Z, v.Y);
    public static Matrix4x4 ToViewerMatrix(Matrix4x4 m) => Orientation * m * Orientation;
}