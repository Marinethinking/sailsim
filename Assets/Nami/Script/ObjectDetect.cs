

using UnityEngine;


//https://discussions.unity.com/t/how-can-i-know-if-a-gameobject-is-seen-by-a-particular-camera/248/2
public class ObjectDetect : MonoBehaviour
{
    Camera camera;
    MeshRenderer meshRenderer;
    Plane[] cameraFrustum;
    Bounds bounds;

    void Start()
    {
        camera = Camera.main;
        meshRenderer = GetComponent<MeshRenderer>();
        bounds = GetComponent<Collider2D>().bounds;
    }

    // Update is called once per frame
    void Update()
    {
        cameraFrustum = GeometryUtility.CalculateFrustumPlanes(camera);
        boundaryCheck();
    }

    void boundaryCheck()
    {
        var isBoundVisible = GeometryUtility.TestPlanesAABB(GeometryUtility.CalculateFrustumPlanes(Camera.main), GetComponent<Collider2D>().bounds);
        if (isBoundVisible)
        {
            var bounds = GetComponent<Collider>().bounds;
            Debug.Log(bounds);
        }
        else
        {
            Debug.Log(gameObject.name + "  is not visible");
        }
    }

    void MinMaxOnScreen()
    {
        Bounds bounds = gameObject.GetComponent<MeshRenderer>().bounds;

        Vector3 ssMin = camera.WorldToScreenPoint(bounds.min);
        Vector3 ssMax = camera.WorldToScreenPoint(bounds.max);
        //Add more Bounds Corners for more accuracy

        float pixelBoundary = 1.5f * Screen.dpi; //A simple way to add a clearance border (1.5" of screen)

        float minX = Mathf.Min(ssMin.x, ssMax.x) - pixelBoundary;
        float maxX = Mathf.Max(ssMin.x, ssMax.x) + pixelBoundary;

        float minY = Mathf.Min(ssMin.y, ssMax.y) - pixelBoundary;
        float maxY = Mathf.Max(ssMin.y, ssMax.y) + pixelBoundary;

        if (minX < 0 || minY < 0 || maxX > Screen.width || maxY > Screen.height)
            Debug.Log("Partially OffScreen ");
    }
}
