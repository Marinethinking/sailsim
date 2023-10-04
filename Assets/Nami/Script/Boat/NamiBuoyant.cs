using UnityEngine;
using WaterSystem;
using Unity.Collections;
using Unity.Mathematics;

public class NamiBuoyant : MonoBehaviour
{
    public float waterOffset = 0.5f;
    public float waterHeight;
    public float offset;

    private float3[] waterHeights = new float3[1];
    private float3[] waterNormals = new float3[1];
    private NativeArray<float3> _samplePoints = new NativeArray<float3>(1, Allocator.Persistent);
    private int guid;
    private Rigidbody rb;

    void OnEnable()
    {
        guid = GetInstanceID();
        if (!TryGetComponent<Rigidbody>(out rb))
        {
            Debug.LogError($"Buoyancy:Object \"{name}\" had no Rigidbody. Rigidbody has been added.");
        }
    }

    void Update()
    {
        GerstnerWavesJobs.UpdateSamplePoints(ref _samplePoints, guid);
        GerstnerWavesJobs.GetData(guid, ref waterHeights, ref waterNormals);
        waterHeight = waterHeights[0].y;
        var pos = transform.position;
        offset = pos.y - waterHeight - waterOffset;
        if (offset > 0) return;
        Vector3 force = new Vector3(0, offset * Physics.gravity.y, 0);
        rb.AddForce(force, ForceMode.Acceleration);
    }
}