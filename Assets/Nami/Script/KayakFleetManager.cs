using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Spawns and manages a fleet of kayaks moving within a defined area.
/// Useful for testing perception nodes with multiple moving targets.
/// </summary>
public class KayakFleetManager : MonoBehaviour
{
    [Header("Fleet Settings")]
    [Tooltip("Prefab to spawn as kayak (assign your kayak model here)")]
    public GameObject kayakPrefab;

    [Tooltip("Number of kayaks to spawn")]
    [Range(1, 50)]
    public int fleetSize = 5;

    [Tooltip("If true, spawns kayaks on Start. Otherwise call SpawnFleet() manually.")]
    public bool spawnOnStart = true;

    [Header("Movement Area (X/Z)")]
    [Tooltip("Offset from this object's position to the center of movement area (X, Z)")]
    public Vector2 areaCenterOffset = Vector2.zero;

    [Tooltip("Width (X) and Length (Z) of the movement area")]
    public Vector2 areaSize = new Vector2(100f, 100f);

    /// <summary>
    /// Returns the world-space center of the movement area.
    /// </summary>
    public Vector2 WorldAreaCenter => new Vector2(transform.position.x + areaCenterOffset.x, transform.position.z + areaCenterOffset.y);

    [Header("Motion Settings")]
    [Tooltip("Movement mode: Forward moves straight ahead, Random wanders")]
    public KayakMover.MovementMode movementMode = KayakMover.MovementMode.Forward;

    [Tooltip("Which local axis is the kayak model's forward direction")]
    public KayakMover.ForwardAxis modelForwardAxis = KayakMover.ForwardAxis.X;

    [Tooltip("Base forward movement speed")]
    public float baseSpeed = 5f;

    [Tooltip("Random speed variation (+/-)")]
    public float speedVariation = 1f;

    [Tooltip("Base rotation speed for turning at boundaries")]
    public float baseTurnSpeed = 2f;

    [Tooltip("Random turn speed variation (+/-)")]
    public float turnSpeedVariation = 0.5f;

    [Header("Physics Settings")]
    [Tooltip("Enable physics for realistic collisions between kayaks")]
    public bool usePhysics = true;

    [Tooltip("Mass of each kayak (affects collision response)")]
    public float mass = 100f;

    [Tooltip("Drag (water resistance)")]
    public float drag = 0.5f;

    [Tooltip("Angular drag (rotation resistance)")]
    public float angularDrag = 1f;

    [Header("Spawn Settings")]
    [Tooltip("Y position (height) for spawned kayaks")]
    public float spawnHeight = 0f;

    [Tooltip("Minimum distance between spawned kayaks")]
    public float minSpawnDistance = 5f;

    [Tooltip("Maximum attempts to find valid spawn position")]
    public int maxSpawnAttempts = 30;

    [Tooltip("Minimum random scale for spawned kayaks")]
    public float minScale = 1f;

    [Tooltip("Maximum random scale for spawned kayaks")]
    public float maxScale = 4f;

    // List of spawned kayaks
    private List<GameObject> _spawnedKayaks = new List<GameObject>();

    void Start()
    {
        if (spawnOnStart && kayakPrefab != null)
        {
            SpawnFleet();
        }
        else if (kayakPrefab == null)
        {
            Debug.LogWarning("KayakFleetManager: No kayak prefab assigned!");
        }
    }

    /// <summary>
    /// Spawns the kayak fleet within the defined area.
    /// </summary>
    public void SpawnFleet()
    {
        ClearFleet();

        List<Vector3> spawnPositions = new List<Vector3>();

        for (int i = 0; i < fleetSize; i++)
        {
            Vector3 spawnPos = GetValidSpawnPosition(spawnPositions);
            spawnPositions.Add(spawnPos);

            // Spawn kayak
            GameObject kayak = Instantiate(kayakPrefab, spawnPos, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f));
            kayak.name = $"Kayak_{i + 1}";
            
            // Only parent to fleet manager if NOT using physics
            // Parenting non-kinematic Rigidbodies can interfere with physics movement
            if (!usePhysics)
            {
                kayak.transform.parent = transform;
            }

            // Apply random scale
            float randomScale = Random.Range(minScale, maxScale);
            kayak.transform.localScale = Vector3.one * randomScale;

            // Add or configure KayakMover component
            KayakMover mover = kayak.GetComponent<KayakMover>();
            if (mover == null)
            {
                mover = kayak.AddComponent<KayakMover>();
            }

            // Configure the mover with fleet settings + random variation
            mover.movementMode = movementMode;
            mover.modelForwardAxis = modelForwardAxis;
            mover.areaCenter = WorldAreaCenter;
            mover.areaSize = areaSize;
            mover.speed = baseSpeed + Random.Range(-speedVariation, speedVariation);
            mover.turnSpeed = baseTurnSpeed + Random.Range(-turnSpeedVariation, turnSpeedVariation);
            mover.lockYPosition = true;

            // Physics settings
            mover.usePhysics = usePhysics;
            mover.mass = mass;
            mover.drag = drag;
            mover.angularDrag = angularDrag;

            _spawnedKayaks.Add(kayak);
        }

        Debug.Log($"KayakFleetManager: Spawned {_spawnedKayaks.Count} kayaks.");
    }

    /// <summary>
    /// Finds a valid spawn position that maintains minimum distance from other kayaks.
    /// </summary>
    private Vector3 GetValidSpawnPosition(List<Vector3> existingPositions)
    {
        for (int attempt = 0; attempt < maxSpawnAttempts; attempt++)
        {
            Vector3 candidate = GetRandomPositionInArea();

            bool isValid = true;
            foreach (Vector3 existing in existingPositions)
            {
                if (Vector3.Distance(candidate, existing) < minSpawnDistance)
                {
                    isValid = false;
                    break;
                }
            }

            if (isValid)
            {
                return candidate;
            }
        }

        // Fallback: return random position even if too close
        Debug.LogWarning("KayakFleetManager: Could not find valid spawn position with minimum distance.");
        return GetRandomPositionInArea();
    }

    /// <summary>
    /// Returns a random position within the movement area (world space).
    /// </summary>
    private Vector3 GetRandomPositionInArea()
    {
        Vector2 worldCenter = WorldAreaCenter;
        float x = worldCenter.x + (Random.value - 0.5f) * areaSize.x;
        float z = worldCenter.y + (Random.value - 0.5f) * areaSize.y;
        return new Vector3(x, spawnHeight, z);
    }

    /// <summary>
    /// Destroys all spawned kayaks.
    /// </summary>
    public void ClearFleet()
    {
        foreach (GameObject kayak in _spawnedKayaks)
        {
            if (kayak != null)
            {
                if (Application.isPlaying)
                {
                    Destroy(kayak);
                }
                else
                {
                    DestroyImmediate(kayak);
                }
            }
        }
        _spawnedKayaks.Clear();
    }

    /// <summary>
    /// Returns the list of spawned kayaks (useful for perception systems).
    /// </summary>
    public List<GameObject> GetKayaks()
    {
        return _spawnedKayaks;
    }

    /// <summary>
    /// Returns the count of active kayaks.
    /// </summary>
    public int GetKayakCount()
    {
        return _spawnedKayaks.Count;
    }

    /// <summary>
    /// Draws the movement area in the Scene view.
    /// </summary>
    void OnDrawGizmos()
    {
        // Draw area boundary (relative to this object's position)
        Gizmos.color = new Color(0f, 1f, 1f, 0.3f);
        Vector3 center = new Vector3(transform.position.x + areaCenterOffset.x, spawnHeight, transform.position.z + areaCenterOffset.y);
        Vector3 size = new Vector3(areaSize.x, 0.5f, areaSize.y);
        Gizmos.DrawWireCube(center, size);

        // Draw filled area with transparency
        Gizmos.color = new Color(0f, 1f, 1f, 0.05f);
        Gizmos.DrawCube(center, size);
    }

    void OnDrawGizmosSelected()
    {
        // Highlight when selected
        Gizmos.color = Color.cyan;
        Vector3 center = new Vector3(transform.position.x + areaCenterOffset.x, spawnHeight, transform.position.z + areaCenterOffset.y);
        Vector3 size = new Vector3(areaSize.x, 0.5f, areaSize.y);
        Gizmos.DrawWireCube(center, size);

        // Show spawn positions of existing kayaks
        Gizmos.color = Color.yellow;
        foreach (GameObject kayak in _spawnedKayaks)
        {
            if (kayak != null)
            {
                Gizmos.DrawWireSphere(kayak.transform.position, 1f);
            }
        }
    }

    void OnDestroy()
    {
        ClearFleet();
    }
}
