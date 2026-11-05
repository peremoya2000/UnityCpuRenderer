using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

public struct MeshEntry
{
    public int FirstVertex;
    public int VertexCount;
    public int FirstTriangle;
    public int TriangleCount;
}

public class MeshLibrary : IDisposable
{
    public NativeList<float3> VertexBuffer = new(Allocator.Persistent);
    public NativeList<int> IndexBuffer = new(Allocator.Persistent);
    public NativeList<float3> FaceNormals = new(Allocator.Persistent);

    private readonly Dictionary<Mesh, MeshEntry> meshToEntry = new();

    public MeshEntry GetOrAdd(Mesh mesh)
    {
        if (!meshToEntry.TryGetValue(mesh, out MeshEntry entry))
        {
            entry = Add(mesh);
            meshToEntry.Add(mesh, entry);
            Debug.Log($"Mesh library: added {mesh.name} ({entry.VertexCount} vertices, {entry.TriangleCount} triangles)");
        }
        return entry;
    }

    public void Dispose()
    {
        if (VertexBuffer.IsCreated) VertexBuffer.Dispose();
        if (IndexBuffer.IsCreated) IndexBuffer.Dispose();
        if (FaceNormals.IsCreated) FaceNormals.Dispose();
    }

    private MeshEntry Add(Mesh mesh) 
    {
        Vector3[] vertices = mesh.vertices;
        int[] indices = mesh.triangles;

        MeshEntry meshRange = new MeshEntry
        {
            FirstVertex = VertexBuffer.Length,
            VertexCount = vertices.Length,
            FirstTriangle = FaceNormals.Length,
            TriangleCount = indices.Length / 3
        };

        VertexBuffer.Capacity = Mathf.Max(VertexBuffer.Capacity, VertexBuffer.Length + vertices.Length);
        for (int i = 0; i < vertices.Length; ++i)
        {
            VertexBuffer.Add(vertices[i]);
        }

        IndexBuffer.Capacity = Mathf.Max(IndexBuffer.Capacity, IndexBuffer.Length + indices.Length);
        for (int i = 0; i < indices.Length; ++i)
        {
            IndexBuffer.Add(indices[i]);
        }

        FaceNormals.Capacity = Mathf.Max(FaceNormals.Capacity, FaceNormals.Length + meshRange.TriangleCount);
        for (int i = 0; i < meshRange.TriangleCount; ++i)
        {
            float3 v0 = vertices[indices[i * 3 + 0]];
            float3 v1 = vertices[indices[i * 3 + 1]];
            float3 v2 = vertices[indices[i * 3 + 2]];

            float3 normal = math.normalizesafe(math.cross(v1 - v0, v2 - v0));
            FaceNormals.Add(normal);
        }

        return meshRange;
    }
}
