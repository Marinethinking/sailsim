using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Exports camera intrinsics and extrinsics for perception nodes.
/// Produces parameters in the format:
/// fx, fy, cx, cy, dist[5], r[9] (row-major), t[3]
/// </summary>
[ExecuteAlways]
public class CameraCalibrationExporter : MonoBehaviour
{
[Tooltip("If not set, will use the Camera on this GameObject; if none, Camera.main.")]
public Camera targetCamera;

    [Tooltip("If true, extrinsics use OpenCV convention (+Z forward); otherwise use Unity view matrix (-Z forward).")]
    public bool useOpenCVConvention = true;

    [Tooltip("Optional reference frame (e.g., Boat root). If enabled, extrinsics map reference-frame coordinates to camera.")]
    public Transform referenceFrame;

    [Tooltip("If true, compute extrinsics relative to 'referenceFrame' so they are constant for rigidly-mounted cameras.")]
    public bool extrinsicsRelativeToReference = true;

    [Tooltip("Optional: Rigidbody whose world center of mass can be used as the reference origin.")]
    public Rigidbody referenceRigidbody;

    [Tooltip("When true, place the reference origin at the Rigidbody's world center of mass (orientation from 'referenceFrame').")]
    public bool referenceAtRigidbodyCOM = true;

    [Tooltip("Override output width in pixels (0 = use camera pixelWidth, fallback to Screen.width).")]
    public int overrideWidth = 0;

    [Tooltip("Override output height in pixels (0 = use camera pixelHeight, fallback to Screen.height).")]
    public int overrideHeight = 0;

    [Tooltip("Write export files under Assets (causes asset reimport) or outside the project (recommended).")]
    public bool writeUnderAssetsFolder = false;

    public bool forceFrontPlusZ = true;



    void OnValidate()
    {
        if (targetCamera == null)
        {
            targetCamera = GetComponent<Camera>();
            if (targetCamera == null) targetCamera = Camera.main;
        }
    }

    [ContextMenu("Export Calibration (Selected)")]
    public void ExportSelected()
    {
        var cam = ResolveCamera(targetCamera);
        if (cam == null)
        {
            Debug.LogError("CameraCalibrationExporter: No camera found to export.");
            return;
        }

        ExportCalibrationForCamera(cam);
    }

    [ContextMenu("Export Calibration (All Cameras)")]
    public void ExportAll()
    {
        var cams = Camera.allCameras;
        if (cams == null || cams.Length == 0)
        {
            Debug.LogError("CameraCalibrationExporter: No cameras found in the scene.");
            return;
        }

        foreach (var cam in cams)
        {
            ExportCalibrationForCamera(cam);
        }
    }

    private Camera ResolveCamera(Camera candidate)
    {
        if (candidate != null) return candidate;
        var self = GetComponent<Camera>();
        if (self != null) return self;
        return Camera.main;
    }

    private void ExportCalibrationForCamera(Camera cam)
    {
        int W = ResolveWidth(cam);
        int H = ResolveHeight(cam);

        ComputeIntrinsics(cam, W, H, out float fx, out float fy, out float cx, out float cy);
        ComputeExtrinsics(cam, referenceFrame, referenceRigidbody, referenceAtRigidbodyCOM, extrinsicsRelativeToReference, useOpenCVConvention, forceFrontPlusZ, out float[] rRowMajor, out Vector3 t);

        var text = BuildPerceptionConfig(cam, W, H, fx, fy, cx, cy, rRowMajor, t);
        Debug.Log($"CameraCalibrationExporter for '{cam.name}':\n\n{text}");
    }

    private int ResolveWidth(Camera cam)
    {
        if (overrideWidth > 0) return overrideWidth;
        if (cam != null && cam.pixelWidth > 0) return cam.pixelWidth;
        return Screen.width;
    }

    private int ResolveHeight(Camera cam)
    {
        if (overrideHeight > 0) return overrideHeight;
        if (cam != null && cam.pixelHeight > 0) return cam.pixelHeight;
        return Screen.height;
    }

    private static void ComputeIntrinsics(Camera cam, int width, int height, out float fx, out float fy, out float cx, out float cy)
    {
        if (cam.usePhysicalProperties)
        {
            fx = cam.focalLength / cam.sensorSize.x * width;
            fy = cam.focalLength / cam.sensorSize.y * height;
            cx = width * (0.5f - cam.lensShift.x);
            cy = height * (0.5f - cam.lensShift.y);
        }
        else
        {
            float vfov = cam.fieldOfView * Mathf.Deg2Rad;
            fy = 0.5f * height / Mathf.Tan(0.5f * vfov);
            fx = fy * cam.aspect;
            cx = 0.5f * width;
            cy = 0.5f * height;
        }
    }

    private static void ComputeExtrinsics(Camera cam, Transform reference, Rigidbody referenceRb, bool useCOMOrigin, bool relativeToReference, bool openCVConvention, bool forceFrontPlusZ, out float[] rRowMajor, out Vector3 t)
    {
        Matrix4x4 V = cam.worldToCameraMatrix; // Unity world -> camera (-Z forward)
        if (relativeToReference && reference != null)
        {
            // Convert from reference frame to camera frame: V_rel = V_world_to_cam * M_ref_to_world
            V = V * BuildReferenceMatrix(reference, referenceRb, useCOMOrigin);
        }
        if (openCVConvention)
        {
            // Flip Z to convert to OpenCV (+Z forward)
            Matrix4x4 S = Matrix4x4.Scale(new Vector3(1f, 1f, -1f));
            V = S * V;
            if (forceFrontPlusZ)
            {
                // Rotate camera frame by +180° yaw: X_cam' = Y180 * X_cam => R' = Y180*R, t' = Y180*t
                Matrix4x4 Y180 = Matrix4x4.Rotate(Quaternion.Euler(0f, 180f, 0f));
                V = Y180 * V;
            }
        }

        rRowMajor = new float[9]
        {
            V.m00, V.m01, V.m02,
            V.m10, V.m11, V.m12,
            V.m20, V.m21, V.m22
        };
        t = new Vector3(V.m03, V.m13, V.m23);
    }

    private static Matrix4x4 BuildReferenceMatrix(Transform reference, Rigidbody referenceRb, bool useCOMOrigin)
    {
        if (useCOMOrigin && referenceRb != null)
        {
            // Use COM as origin; orientation from reference transform
            return Matrix4x4.TRS(referenceRb.worldCenterOfMass, reference.rotation, Vector3.one);
        }
        return reference.localToWorldMatrix;
    }

    private static string BuildPerceptionConfig(Camera cam, int width, int height, float fx, float fy, float cx, float cy, float[] rRowMajor, Vector3 t)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Camera calibration for '{cam.name}'");
        sb.AppendLine($"# Resolution: {width}x{height}");
        sb.AppendLine($"# Convention: {(Application.isPlaying ? "runtime" : "editor")}");
        sb.AppendLine($"fx = {fx:F6}");
        sb.AppendLine($"fy = {fy:F6}");
        sb.AppendLine($"cx = {cx:F6}");
        sb.AppendLine($"cy = {cy:F6}");
        sb.AppendLine("# Optional distortion coefficients (k1,k2,p1,p2,k3)");
        sb.AppendLine("dist = [0.0, 0.0, 0.0, 0.0, 0.0]");
        sb.AppendLine("# Rotation matrix (row-major R)");
        sb.Append("r = [");
        for (int i = 0; i < 9; i++)
        {
            sb.Append(rRowMajor[i].ToString("F6"));
            if (i < 8) sb.Append(", ");
        }
        sb.AppendLine("]");
        sb.AppendLine("# Translation vector (meters)");
        sb.AppendLine($"t = [{t.x:F6}, {t.y:F6}, {t.z:F6}]");
        return sb.ToString();
    }

}
