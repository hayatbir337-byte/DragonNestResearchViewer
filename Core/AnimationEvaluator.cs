using System.Numerics;

namespace DragonNestResearchViewer.Core;

public sealed class AnimationEvaluator
{
    readonly DnMesh _mesh;
    readonly DnAnimationSet _ani;
    readonly Dictionary<string, AnimatedBone> _aniBones;
    readonly Dictionary<string, string> _parent;

    public AnimationEvaluator(DnMesh mesh, DnAnimationSet ani)
    {
        _mesh = mesh;
        _ani = ani;
        _aniBones = ani.Bones.ToDictionary(b => b.Name, StringComparer.OrdinalIgnoreCase);
        _parent = ani.Bones.ToDictionary(b => b.Name, b => b.ParentName, StringComparer.OrdinalIgnoreCase);
    }

    public Matrix4x4[] EvaluatePose(int animationIndex, float frame)
    {
        var result = Enumerable.Repeat(Matrix4x4.Identity, _mesh.Bones.Count).ToArray();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Matrix4x4 Eval(string name)
        {
            if (!_mesh.BoneIndexByName.TryGetValue(name, out int meshIndex))
                return Matrix4x4.Identity;
            if (result[meshIndex] != Matrix4x4.Identity)
                return result[meshIndex];
            if (!visiting.Add(name))
                return _mesh.Bones[meshIndex].BindGlobal;

            Matrix4x4 global;
            if (_aniBones.TryGetValue(name, out var ab) && animationIndex < ab.Animations.Count)
            {
                var a = ab.Animations[animationIndex];
                var loc = DnParsers.ToViewer(EvalVec(a.BaseLocation, a.Locations, frame));
                var scl = DnParsers.ToViewer(EvalVec(a.BaseScale, a.Scales, frame));
                var rot = EvalQuat(a.BaseRotation, a.Rotations, frame);

                Matrix4x4 rDn = Matrix4x4.CreateFromQuaternion(rot);
                Matrix4x4 rView = DnParsers.ToViewerMatrix(rDn);
                var local = Matrix4x4.CreateScale(scl) * rView * Matrix4x4.CreateTranslation(loc);

                string parentName = ab.ParentName;
                global = string.IsNullOrEmpty(parentName) ? local : local * Eval(parentName);
            }
            else
            {
                global = _mesh.Bones[meshIndex].BindGlobal;
            }

            visiting.Remove(name);
            result[meshIndex] = global;
            return global;
        }

        foreach (var b in _mesh.Bones) Eval(b.Name);
        return result;
    }

    static Vector3 EvalVec(Vector3 baseValue, List<VecKey> keys, float frame)
    {
        if (keys.Count == 0) return baseValue;
        if (frame <= keys[0].Frame)
        {
            if (keys[0].Frame <= 0) return keys[0].Value;
            return Vector3.Lerp(baseValue, keys[0].Value, frame / keys[0].Frame);
        }
        for (int i = 0; i < keys.Count - 1; i++)
        {
            if (frame <= keys[i + 1].Frame)
            {
                float t = (frame - keys[i].Frame) / Math.Max(1f, keys[i + 1].Frame - keys[i].Frame);
                return Vector3.Lerp(keys[i].Value, keys[i + 1].Value, t);
            }
        }
        return keys[^1].Value;
    }

    static Quaternion EvalQuat(Quaternion baseValue, List<QuatKey> keys, float frame)
    {
        if (keys.Count == 0) return baseValue;
        if (frame <= keys[0].Frame)
        {
            if (keys[0].Frame <= 0) return keys[0].Value;
            return Quaternion.Slerp(baseValue, keys[0].Value, frame / keys[0].Frame);
        }
        for (int i = 0; i < keys.Count - 1; i++)
        {
            if (frame <= keys[i + 1].Frame)
            {
                float t = (frame - keys[i].Frame) / Math.Max(1f, keys[i + 1].Frame - keys[i].Frame);
                return Quaternion.Slerp(keys[i].Value, keys[i + 1].Value, t);
            }
        }
        return keys[^1].Value;
    }
}