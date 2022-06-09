using System.Collections;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;
using UnityEngine.Rendering;
using static Unity.Mathematics.math;

public class RealTimeMeshOptimizer : MonoBehaviour
{
	public MeshFilter meshFilter;
	public Mesh baseMesh;
	public Mesh resultMesh;
	public int threadCount = 4;
	public Transform[] viewPoints;

	int indexCount;
	int vertexCount;
	NativeArray<int> visibleTriangleCountHolder;
	NativeArray<int> jobTriangleIndexes;
	NativeArray<Vector3> jobVertices;
	Mesh.MeshDataArray readonlyMeshData;
	NativeArray<VertexAttributeDescriptor> vertexAttributeDescriptors;
	NativeArray<float3x2> jobBounds;
	NativeArray<float3> tempVertices;
	NativeArray<float3> tempNormals;
	NativeArray<float2> tempcoord;
	void Start()
	{
		if (resultMesh == null)
			resultMesh = new Mesh();

		indexCount = (int)baseMesh.GetIndexCount(0);
		vertexCount = baseMesh.vertexCount;

		visibleTriangleCountHolder = new NativeArray<int>(1, Allocator.Persistent);
		jobBounds = new NativeArray<float3x2>(1, Allocator.Persistent);
		jobTriangleIndexes = new NativeArray<int>(indexCount, Allocator.Persistent);
		jobVertices = new NativeArray<Vector3>(vertexCount, Allocator.Persistent);
		tempVertices = new NativeArray<float3>(vertexCount, Allocator.Persistent);
		tempNormals = new NativeArray<float3>(vertexCount, Allocator.Persistent);
		tempcoord = new NativeArray<float2>(vertexCount, Allocator.Persistent);

		vertexAttributeDescriptors = new NativeArray<VertexAttributeDescriptor>(3, Allocator.Persistent);
		vertexAttributeDescriptors[0] = new VertexAttributeDescriptor(VertexAttribute.Position);
		vertexAttributeDescriptors[1] = new VertexAttributeDescriptor(VertexAttribute.Normal, stream: 1);
		vertexAttributeDescriptors[2] = new VertexAttributeDescriptor(VertexAttribute.TexCoord0, dimension: 2, stream: 2);

		readonlyMeshData = Mesh.AcquireReadOnlyMeshData(baseMesh);
	}


	//Put in an update just for testing it works, you can remove Update() if you want, and just call the activate method instead, or even run the job in a System
	bool active;
	void Update()
	{
		if (Input.GetKeyDown(KeyCode.C))
			Activate();
		if (Input.GetKeyDown(KeyCode.Space))
			ProcessSingle();
	}

	//Deactivates the real time generation	
	public void Activate()
	{
		if (active)
			return;
		
		active = true;
		StartCoroutine(WaitAndComplete());
	}

	//Deactivates the real time generation
	public void Deactivate() => active = false;

	//Processes a single frame of the mesh only once
	public void ProcessSingle() => StartCoroutine(WaitAndComplete(1));



	IEnumerator WaitAndComplete(int iterations = 0)
	{
		int leftIterations = iterations == 0 ? 1 : iterations;
		while (leftIterations>0)
		{
			if (!active && leftIterations == 0)
				yield break;
			
			if(iterations != 0)
				leftIterations--;

			ProcessTriangleVisibility processVisibleTriangles = new ProcessTriangleVisibility
			{
				meshData = readonlyMeshData,
				pos = viewPoints[0].position,
				forth = viewPoints[0].forward,
				visibleTriangleCount = visibleTriangleCountHolder,
				triangles = jobTriangleIndexes,
				vertices = jobVertices,
			};
			JobHandle processVisibleTrianglesHandle = processVisibleTriangles.Schedule(1, threadCount);
			while (!processVisibleTrianglesHandle.IsCompleted)
				yield return null;
			processVisibleTrianglesHandle.Complete();

			int visibleIndexCount = visibleTriangleCountHolder[0] * 3;

			Mesh.MeshDataArray writableMeshData = Mesh.AllocateWritableMeshData(1); //No se puede cachear porque aplicarla la disposa
			writableMeshData[0].SetIndexBufferParams(visibleIndexCount, IndexFormat.UInt32);
			writableMeshData[0].SetVertexBufferParams(vertexCount, vertexAttributeDescriptors);


			jobBounds[0] = new float3x2(new float3(Mathf.Infinity), new float3(Mathf.NegativeInfinity));
			FaceRemovalJob removeInvisibleTriangles = new FaceRemovalJob
			{
				meshData = readonlyMeshData,
				outputMesh = writableMeshData[0],
				pos = processVisibleTriangles.pos,
				forth = processVisibleTriangles.forth,
				bounds = jobBounds,
				inputMeshVertexCount = vertexCount,
				tempVertices = tempVertices,
				tempNormals = tempNormals,
				tempcoord = tempcoord,
			};
			JobHandle removeInvisibleTrianglesHandle = removeInvisibleTriangles.Schedule(1, threadCount/*, processVisibleTrianglesHandle*/);
			while (!removeInvisibleTrianglesHandle.IsCompleted)
				yield return null;
			removeInvisibleTrianglesHandle.Complete();

			var b = removeInvisibleTriangles.bounds[0];

			var sm = new SubMeshDescriptor(0, visibleIndexCount, MeshTopology.Triangles);
			sm.firstVertex = 0;
			sm.vertexCount = vertexCount;
			sm.bounds = new Bounds((b.c0 + b.c1) * 0.5f, b.c1 - b.c0);

			removeInvisibleTriangles.outputMesh.subMeshCount = 1;
			removeInvisibleTriangles.outputMesh.SetSubMesh(0, sm,
				MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers);

			Mesh.ApplyAndDisposeWritableMeshData(writableMeshData, resultMesh,
				MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers);
			
			resultMesh.bounds = sm.bounds;
			meshFilter.sharedMesh = resultMesh;

			yield return null;
		}
	}
}







//TODO: Maybe doing everything in a single job may avoid having to process the same algorithm twice, potentially increasing the performance.

//This job processes the visible triangle count for later use on the FaceRemovalJob.
[BurstCompile]
public struct ProcessTriangleVisibility : IJobParallelFor
{
	[ReadOnly] public Mesh.MeshDataArray meshData;
	public NativeArray<int> visibleTriangleCount;
	public float3 pos;
	public Vector3 forth;

	[NativeDisableContainerSafetyRestriction] public NativeArray<Vector3> vertices;
	[NativeDisableContainerSafetyRestriction] public NativeArray<int> triangles;

	public void Execute(int index)
	{
		meshData[index].GetVertices(vertices);
		meshData[index].GetIndices(triangles, 0);
		int visibleCount = 0;
		for (int i = 0; i < triangles.Length; i += 3)
		{
			float3 v1 = vertices[triangles[i]];
			float3 v2 = vertices[triangles[i + 1]];
			float3 v3 = vertices[triangles[i + 2]];
			float3 center = (vertices[triangles[i]] + vertices[triangles[i + 1]] + vertices[triangles[i + 2]]) / 3;
			float3 normal = cross(v2 - v1, v3 - v1);

			if (dot(forth, normalize(pos - center)) > 0) 
				continue;

			visibleCount +=select(0,1,dot(normal, normalize(pos - center)) > 0);
		}
		visibleTriangleCount[0] = visibleCount;
	}
}





//TODO: Maybe doing everything in a single job may avoid having to process the same algorithm twice, potentially increasing the performance.

//This job removes the invisible triangles from the mesh. It also calculates the bounds of the resulting mesh.
[BurstCompile]
public struct FaceRemovalJob : IJobParallelFor
{
	public int inputMeshVertexCount;
	public float3 pos;
	public float3 forth;
	[ReadOnly] public Mesh.MeshDataArray meshData;
	public NativeArray<float3x2> bounds;
	public Mesh.MeshData outputMesh;

	[NativeDisableContainerSafetyRestriction] public NativeArray<float3> tempVertices;
	[NativeDisableContainerSafetyRestriction] public NativeArray<float3> tempNormals;
	[NativeDisableContainerSafetyRestriction] public NativeArray<float2> tempcoord;


	public void Execute(int index)
	{
		var data = meshData[index];
		var vCount = data.vertexCount;

		data.GetVertices(tempVertices.Reinterpret<Vector3>());
		data.GetNormals(tempNormals.Reinterpret<Vector3>());
		data.GetUVs(0, tempcoord.Reinterpret<Vector2>());

		var outputVerts = outputMesh.GetVertexData<float3>();
		var outputNormals = outputMesh.GetVertexData<float3>(stream: 1);
		var outputcoords = outputMesh.GetVertexData<float2>(stream: 2);

		var b = bounds[index];
		for (var i = 0; i < vCount; ++i)
		{
			int bi = i + (vCount * index);

			float3 pos = tempVertices[bi];
			outputVerts[bi] = pos;
			var nor = tempNormals[bi];
			outputNormals[bi] = nor;
			var coo = tempcoord[bi];
			outputcoords[bi] = coo;
			b.c0 = min(b.c0, pos);
			b.c1 = max(b.c1, pos);
		}
		bounds[index] = b;

		var inputTris = data.GetIndexData<int>();
		var outputTris = outputMesh.GetIndexData<int>();
		var tCount = inputTris.Length;
		int p = 0;
		for (var i = 0; i < tCount; i += 3)
		{
			int bi = i + (index * tCount);

			float3 v1 = outputVerts[inputTris[bi]];
			float3 v2 = outputVerts[inputTris[bi + 1]];
			float3 v3 = outputVerts[inputTris[bi + 2]];
			float3 normal = normalize(cross(v2 - v1, v3 - v1));
			float3 center = (v1 + v2 + v3) / 3;

			if (dot(forth, normalize(pos - center)) > 0)
				continue;

			if (dot(normal, normalize(pos - center)) > 0)
			{
				outputTris[p] = inputTris[bi];
				outputTris[p + 1] = inputTris[bi + 1];
				outputTris[p + 2] = inputTris[bi + 2];
				p += 3;
			}
		}
	}
}
