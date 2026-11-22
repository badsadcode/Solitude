using System;
using System.Collections.Generic;
using Godot;

namespace Solitude.Core;

/// <summary>
/// Collects flat-shaded triangles in plain C# lists (thread-safe until Build()).
/// Everything is low-poly and faceted on purpose: one normal per triangle.
///
/// Winding: callers may pass triangles in any order together with an "outward" hint;
/// the builder flips them so the geometric normal faces the hint, then emits them in
/// Godot's clockwise front-face order. This removes a whole class of invisible-mesh bugs.
/// </summary>
public sealed class MeshBuilder
{
    private readonly List<Vector3> _v = new();
    private readonly List<Vector3> _n = new();
    private readonly List<Color> _c = new();
    private readonly List<Vector2> _uv = new();

    /// <summary>Transform applied to every vertex added (local building).</summary>
    public Transform3D Xform = Transform3D.Identity;

    /// <summary>Metres per texture repeat for the planar UV projection.</summary>
    public float UvScale = 1f;

    public int VertexCount => _v.Count;

    public void Clear()
    {
        _v.Clear(); _n.Clear(); _c.Clear(); _uv.Clear();
    }

    /// <summary>Add a triangle. If hint is non-zero the face is turned to face it.</summary>
    public void Tri(Vector3 a, Vector3 b, Vector3 c, Color col, Vector3 hint = default)
    {
        a = Xform * a; b = Xform * b; c = Xform * c;
        if (hint != Vector3.Zero) hint = Xform.Basis * hint;
        TriWorld(a, b, c, col, hint);
    }

    /// <summary>Like Tri but vertices are already in the builder's output space.</summary>
    public void TriWorld(Vector3 a, Vector3 b, Vector3 c, Color col, Vector3 hint = default)
    {
        Vector3 n = (b - a).Cross(c - a);
        if (n.LengthSquared() < 1e-12f) return;
        n = n.Normalized();
        if (hint != Vector3.Zero && n.Dot(hint) < 0f)
        {
            (b, c) = (c, b);
            n = -n;
        }
        // (a, b, c) is counter-clockwise around n; Godot wants clockwise front faces.
        Emit(a, n, col);
        Emit(c, n, col);
        Emit(b, n, col);
    }

    private void Emit(Vector3 p, Vector3 n, Color col)
    {
        _v.Add(p);
        _n.Add(n);
        _c.Add(col);
        // Planar projection on the dominant axis, in metres.
        Vector3 an = n.Abs();
        Vector2 uv = an.X > an.Y && an.X > an.Z ? new Vector2(p.Z, -p.Y)
                   : an.Y > an.Z ? new Vector2(p.X, p.Z)
                   : new Vector2(p.X, -p.Y);
        _uv.Add(uv / UvScale);
    }

    public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color col, Vector3 hint = default)
    {
        Tri(a, b, c, col, hint);
        Tri(a, c, d, col, hint);
    }

    /// <summary>Axis-aligned (in builder space) box.</summary>
    public void Box(Vector3 center, Vector3 size, Color col)
    {
        Vector3 h = size * 0.5f;
        Span<Vector3> p = stackalloc Vector3[8];
        for (int i = 0; i < 8; i++)
            p[i] = center + new Vector3((i & 1) == 0 ? -h.X : h.X, (i & 2) == 0 ? -h.Y : h.Y, (i & 4) == 0 ? -h.Z : h.Z);
        Quad(p[1], p[3], p[7], p[5], col, Vector3.Right);
        Quad(p[0], p[4], p[6], p[2], col, Vector3.Left);
        Quad(p[2], p[6], p[7], p[3], col, Vector3.Up);
        Quad(p[0], p[1], p[5], p[4], col, Vector3.Down);
        Quad(p[4], p[5], p[7], p[6], col, Vector3.Back);
        Quad(p[0], p[2], p[3], p[1], col, Vector3.Forward);
    }

    /// <summary>Box with an arbitrary local basis (rotation and non-uniform size).</summary>
    public void BoxOriented(Transform3D t, Vector3 size, Color col)
    {
        Transform3D saved = Xform;
        Xform = saved * t;
        Box(Vector3.Zero, size, col);
        Xform = saved;
    }

    /// <summary>Cylinder / frustum along +Y from y0 to y1.</summary>
    public void Cylinder(Vector3 baseCenter, float r0, float r1, float height, int segments, Color col, bool caps = true)
    {
        for (int i = 0; i < segments; i++)
        {
            float a0 = Mathf.Tau * i / segments, a1 = Mathf.Tau * (i + 1) / segments;
            Vector3 d0 = new(Mathf.Cos(a0), 0, Mathf.Sin(a0));
            Vector3 d1 = new(Mathf.Cos(a1), 0, Mathf.Sin(a1));
            Vector3 b0 = baseCenter + d0 * r0, b1 = baseCenter + d1 * r0;
            Vector3 t0 = baseCenter + d0 * r1 + Vector3.Up * height;
            Vector3 t1 = baseCenter + d1 * r1 + Vector3.Up * height;
            Vector3 hint = (d0 + d1) * 0.5f;
            Quad(b0, b1, t1, t0, col, hint);
            if (caps)
            {
                if (r1 > 0.001f) Tri(baseCenter + Vector3.Up * height, t0, t1, col, Vector3.Up);
                if (r0 > 0.001f) Tri(baseCenter, b0, b1, col, Vector3.Down);
            }
        }
    }

    /// <summary>
    /// Surface of revolution around the Z axis. profile = (z, radius) pairs.
    /// squashY scales the vertical radius (for flattened hull sections).
    /// </summary>
    public void LatheZ(IReadOnlyList<Vector2> profile, int segments, Color col, float squashY = 1f, Func<int, int, Color> colorFn = null)
    {
        for (int r = 0; r < profile.Count - 1; r++)
        {
            float z0 = profile[r].X, rad0 = profile[r].Y;
            float z1 = profile[r + 1].X, rad1 = profile[r + 1].Y;
            for (int i = 0; i < segments; i++)
            {
                float a0 = Mathf.Tau * i / segments + Mathf.Pi / segments;
                float a1 = Mathf.Tau * (i + 1) / segments + Mathf.Pi / segments;
                Vector3 d0 = new(Mathf.Cos(a0), Mathf.Sin(a0) * squashY, 0);
                Vector3 d1 = new(Mathf.Cos(a1), Mathf.Sin(a1) * squashY, 0);
                Vector3 p00 = d0 * rad0 + new Vector3(0, 0, z0);
                Vector3 p01 = d1 * rad0 + new Vector3(0, 0, z0);
                Vector3 p10 = d0 * rad1 + new Vector3(0, 0, z1);
                Vector3 p11 = d1 * rad1 + new Vector3(0, 0, z1);
                Vector3 hint = (d0 + d1);
                Color cc = colorFn?.Invoke(r, i) ?? col;
                if (rad0 < 0.001f) Tri(p00, p10, p11, cc, hint);
                else if (rad1 < 0.001f) Tri(p00, p01, p10, cc, hint);
                else Quad(p00, p01, p11, p10, cc, hint);
            }
        }
    }

    /// <summary>Low-poly icosahedron with optional per-vertex jitter (rocks).</summary>
    public void Rock(Vector3 center, Vector3 scale, Color col, int seed, float roughness = 0.3f)
    {
        float t = (1f + MathF.Sqrt(5f)) / 2f;
        Vector3[] v =
        {
            new(-1, t, 0), new(1, t, 0), new(-1, -t, 0), new(1, -t, 0),
            new(0, -1, t), new(0, 1, t), new(0, -1, -t), new(0, 1, -t),
            new(t, 0, -1), new(t, 0, 1), new(-t, 0, -1), new(-t, 0, 1),
        };
        int[] f =
        {
            0,11,5, 0,5,1, 0,1,7, 0,7,10, 0,10,11, 1,5,9, 5,11,4, 11,10,2, 10,7,6, 7,1,8,
            3,9,4, 3,4,2, 3,2,6, 3,6,8, 3,8,9, 4,9,5, 2,4,11, 6,2,10, 8,6,7, 9,8,1,
        };
        for (int i = 0; i < v.Length; i++)
        {
            float j = 1f + (Perlin.Hash(i, seed, 77) - 0.5f) * 2f * roughness;
            v[i] = v[i].Normalized() * j * scale + center;
        }
        for (int i = 0; i < f.Length; i += 3)
        {
            Vector3 a = v[f[i]], b = v[f[i + 1]], c = v[f[i + 2]];
            float shade = 0.85f + Perlin.Hash(i, seed, 13) * 0.3f;
            Tri(a, b, c, new Color(col.R * shade, col.G * shade, col.B * shade, col.A), (a + b + c) / 3f - center);
        }
    }

    public bool HasGeometry => _v.Count > 0;

    /// <summary>Raw arrays (safe to call off-thread).</summary>
    public (Vector3[] verts, Vector3[] normals, Color[] colors, Vector2[] uvs) ToArrays()
        => (_v.ToArray(), _n.ToArray(), _c.ToArray(), _uv.ToArray());

    /// <summary>Main thread only.</summary>
    public ArrayMesh Build(Material material = null)
    {
        var mesh = new ArrayMesh();
        if (_v.Count == 0) return mesh;
        AddSurface(mesh, ToArrays(), material);
        return mesh;
    }

    public static void AddSurface(ArrayMesh mesh, (Vector3[] verts, Vector3[] normals, Color[] colors, Vector2[] uvs) a, Material material)
    {
        if (a.verts.Length == 0) return;
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = a.verts;
        arrays[(int)Mesh.ArrayType.Normal] = a.normals;
        arrays[(int)Mesh.ArrayType.Color] = a.colors;
        arrays[(int)Mesh.ArrayType.TexUV] = a.uvs;
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        if (material != null) mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, material);
    }

    /// <summary>Triangle soup for ConcavePolygonShape3D (CW order to match render).</summary>
    public Vector3[] CollisionFaces() => _v.ToArray();
}
