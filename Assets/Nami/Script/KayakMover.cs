using UnityEngine;

/// <summary>
/// Moves a kayak (or any GameObject) within a defined rectangular area.
/// Supports directional movement (+X) or random wandering.
/// Supports physics-based movement with Rigidbody for realistic collisions.
/// Useful for testing perception nodes by providing a moving target.
/// </summary>
public class KayakMover : MonoBehaviour
{
    public enum MovementMode
    {
        Random,         // Wander randomly within area
        Forward         // Move forward, turn at boundaries
    }

    public enum ForwardAxis
    {
        Z,      // Unity default: transform.forward (local +Z)
        X       // Model's forward is local +X (transform.right)
    }

    [Header("Movement Mode")]
    [Tooltip("Random: wander randomly. Forward: move straight ahead in the direction the kayak faces")]
    public MovementMode movementMode = MovementMode.Forward;

    [Tooltip("Which local axis is the model's forward direction")]
    public ForwardAxis modelForwardAxis = ForwardAxis.X;

    [Header("Physics")]
    [Tooltip("Use Rigidbody physics for realistic collisions")]
    public bool usePhysics = true;

    [Tooltip("Mass of the kayak (affects collision response)")]
    public float mass = 100f;

    [Tooltip("Drag (water resistance) - higher = slower deceleration")]
    public float drag = 0.5f;

    [Tooltip("Angular drag (rotation resistance)")]
    public float angularDrag = 1f;

    [Header("Movement Area (X/Z)")]
    [Tooltip("World-space center of the movement area (X, Z)")]
    public Vector2 areaCenter = Vector2.zero;

    [Tooltip("Width (X) and Length (Z) of the movement area")]
    public Vector2 areaSize = new Vector2(50f, 50f);

    [Header("Motion Settings")]
    [Tooltip("Forward movement force/speed")]
    public float speed = 5f;

    [Tooltip("Rotation speed for turning")]
    public float turnSpeed = 2f;

    [Tooltip("Distance threshold to consider target reached (used in Random mode)")]
    public float arrivalThreshold = 1f;

    [Header("Optional")]
    [Tooltip("If true, maintains the object's current Y position (good for water surface)")]
    public bool lockYPosition = true;

    private Vector3 _targetPos;
    private float _initialY;
    private Rigidbody _rb;

    void Start()
    {
        _initialY = transform.position.y;

        if (usePhysics)
        {
            SetupRigidbody();
        }

        if (movementMode == MovementMode.Random)
        {
            PickNewTarget();
        }
    }

    /// <summary>
    /// Sets up the Rigidbody component for physics-based movement.
    /// </summary>
    void SetupRigidbody()
    {
        _rb = GetComponent<Rigidbody>();
        if (_rb == null)
        {
            _rb = gameObject.AddComponent<Rigidbody>();
        }

        _rb.mass = mass;
        _rb.linearDamping = drag;
        _rb.angularDamping = angularDrag;
        _rb.useGravity = false; // Floating on water
        _rb.interpolation = RigidbodyInterpolation.Interpolate;

        // Constrain to XZ plane (no Y movement, no X/Z rotation)
        if (lockYPosition)
        {
            _rb.constraints = RigidbodyConstraints.FreezePositionY | 
                              RigidbodyConstraints.FreezeRotationX | 
                              RigidbodyConstraints.FreezeRotationZ;
        }
        else
        {
            _rb.constraints = RigidbodyConstraints.FreezeRotationX | 
                              RigidbodyConstraints.FreezeRotationZ;
        }

        // Add collider if missing
        if (GetComponent<Collider>() == null)
        {
            BoxCollider col = gameObject.AddComponent<BoxCollider>();
            // Estimate size from renderer bounds
            Renderer rend = GetComponentInChildren<Renderer>();
            if (rend != null)
            {
                col.size = rend.bounds.size;
                col.center = transform.InverseTransformPoint(rend.bounds.center);
            }
        }
    }

    void Update()
    {
        // Non-physics movement in Update
        if (!usePhysics)
        {
            if (movementMode == MovementMode.Forward)
            {
                UpdateForwardMovement();
            }
            else
            {
                UpdateRandomMovement();
            }
        }
    }

    void FixedUpdate()
    {
        // Physics-based movement in FixedUpdate
        if (usePhysics && _rb != null)
        {
            if (movementMode == MovementMode.Forward)
            {
                FixedUpdateForwardMovement();
            }
            else
            {
                FixedUpdateRandomMovement();
            }

            // Keep Y position locked
            if (lockYPosition)
            {
                Vector3 pos = _rb.position;
                pos.y = _initialY;
                _rb.position = pos;
            }
        }
    }

    /// <summary>
    /// Returns the forward direction based on model's forward axis setting.
    /// </summary>
    Vector3 GetModelForward()
    {
        return modelForwardAxis == ForwardAxis.X ? transform.right : transform.forward;
    }

    [Header("Boundary Settings")]
    [Tooltip("Distance from boundary to start turning")]
    public float boundaryMargin = 5f;

    /// <summary>
    /// Calculates turn direction when approaching boundaries.
    /// </summary>
    private (bool needsTurn, Vector3 turnDirection) CheckBoundaries(Vector3 currentPos)
    {
        float minX = areaCenter.x - areaSize.x * 0.5f;
        float maxX = areaCenter.x + areaSize.x * 0.5f;
        float minZ = areaCenter.y - areaSize.y * 0.5f;
        float maxZ = areaCenter.y + areaSize.y * 0.5f;

        bool needsTurn = false;
        Vector3 turnDirection = Vector3.zero;

        if (currentPos.x >= maxX - boundaryMargin)
        {
            needsTurn = true;
            turnDirection += Vector3.left;
        }
        else if (currentPos.x <= minX + boundaryMargin)
        {
            needsTurn = true;
            turnDirection += Vector3.right;
        }

        if (currentPos.z >= maxZ - boundaryMargin)
        {
            needsTurn = true;
            turnDirection += Vector3.back;
        }
        else if (currentPos.z <= minZ + boundaryMargin)
        {
            needsTurn = true;
            turnDirection += Vector3.forward;
        }

        return (needsTurn, turnDirection);
    }

    /// <summary>
    /// Applies rotation toward a direction, accounting for model forward axis.
    /// </summary>
    private void ApplyRotation(Vector3 direction, float deltaTime, bool useRigidbody = false)
    {
        if (direction == Vector3.zero) return;

        Quaternion targetRotation = Quaternion.LookRotation(direction.normalized, Vector3.up);

        // Adjust for model's forward axis
        if (modelForwardAxis == ForwardAxis.X)
        {
            targetRotation *= Quaternion.Euler(0f, -90f, 0f);
        }

        if (useRigidbody && _rb != null)
        {
            Quaternion newRot = Quaternion.Slerp(_rb.rotation, targetRotation, turnSpeed * deltaTime);
            _rb.MoveRotation(newRot);
        }
        else
        {
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, turnSpeed * deltaTime);
        }
    }

    /// <summary>
    /// Physics-based forward movement (FixedUpdate).
    /// </summary>
    void FixedUpdateForwardMovement()
    {
        Vector3 currentPos = _rb.position;

        // Check boundaries and turn if needed
        var (needsTurn, turnDirection) = CheckBoundaries(currentPos);
        if (needsTurn)
        {
            ApplyRotation(turnDirection, Time.fixedDeltaTime, true);
        }

        // Apply forward acceleration (ignores mass for consistent speed control)
        Vector3 forwardDir = GetModelForward();
        _rb.AddForce(forwardDir * speed, ForceMode.Acceleration);

        // Clamp position to area bounds
        float minX = areaCenter.x - areaSize.x * 0.5f;
        float maxX = areaCenter.x + areaSize.x * 0.5f;
        float minZ = areaCenter.y - areaSize.y * 0.5f;
        float maxZ = areaCenter.y + areaSize.y * 0.5f;

        Vector3 clampedPos = _rb.position;
        clampedPos.x = Mathf.Clamp(clampedPos.x, minX, maxX);
        clampedPos.z = Mathf.Clamp(clampedPos.z, minZ, maxZ);
        if (clampedPos != _rb.position)
        {
            _rb.position = clampedPos;
        }
    }

    /// <summary>
    /// Physics-based random movement (FixedUpdate).
    /// </summary>
    void FixedUpdateRandomMovement()
    {
        Vector3 currentPos = _rb.position;
        Vector3 flatPos = new Vector3(currentPos.x, 0f, currentPos.z);
        Vector3 flatTarget = new Vector3(_targetPos.x, 0f, _targetPos.z);

        Vector3 direction = flatTarget - flatPos;
        float distance = direction.magnitude;

        if (distance < arrivalThreshold)
        {
            PickNewTarget();
            return;
        }

        // Turn toward target
        ApplyRotation(direction, Time.fixedDeltaTime, true);

        // Apply forward acceleration (ignores mass for consistent speed control)
        _rb.AddForce(GetModelForward() * speed, ForceMode.Acceleration);
    }

    /// <summary>
    /// Non-physics forward movement (Update).
    /// </summary>
    void UpdateForwardMovement()
    {
        Vector3 currentPos = transform.position;

        // Check boundaries and turn if needed
        var (needsTurn, turnDirection) = CheckBoundaries(currentPos);
        if (needsTurn)
        {
            ApplyRotation(turnDirection, Time.deltaTime, false);
        }

        // Move forward
        Vector3 newPos = currentPos + GetModelForward() * speed * Time.deltaTime;

        // Clamp position to area bounds
        float minX = areaCenter.x - areaSize.x * 0.5f;
        float maxX = areaCenter.x + areaSize.x * 0.5f;
        float minZ = areaCenter.y - areaSize.y * 0.5f;
        float maxZ = areaCenter.y + areaSize.y * 0.5f;

        newPos.x = Mathf.Clamp(newPos.x, minX, maxX);
        newPos.z = Mathf.Clamp(newPos.z, minZ, maxZ);

        if (lockYPosition)
        {
            newPos.y = _initialY;
        }

        transform.position = newPos;
    }

    /// <summary>
    /// Non-physics random wandering movement (Update).
    /// </summary>
    void UpdateRandomMovement()
    {
        Vector3 currentPos = transform.position;
        Vector3 flatPos = new Vector3(currentPos.x, 0f, currentPos.z);
        Vector3 flatTarget = new Vector3(_targetPos.x, 0f, _targetPos.z);

        Vector3 direction = flatTarget - flatPos;
        float distance = direction.magnitude;

        if (distance < arrivalThreshold)
        {
            PickNewTarget();
            return;
        }

        // Turn toward target
        ApplyRotation(direction, Time.deltaTime, false);

        // Move forward
        Vector3 newPos = currentPos + GetModelForward() * speed * Time.deltaTime;

        if (lockYPosition)
        {
            newPos.y = _initialY;
        }

        transform.position = newPos;
    }

    /// <summary>
    /// Picks a new random target position within the defined area.
    /// </summary>
    void PickNewTarget()
    {
        float x = areaCenter.x + (Random.value - 0.5f) * areaSize.x;
        float z = areaCenter.y + (Random.value - 0.5f) * areaSize.y;

        _targetPos = new Vector3(x, _initialY, z);
    }

    /// <summary>
    /// Draws the movement area in the Scene view when the object is selected.
    /// </summary>
    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.cyan;
        Vector3 center = new Vector3(areaCenter.x, transform.position.y, areaCenter.y);
        Vector3 size = new Vector3(areaSize.x, 0.2f, areaSize.y);
        Gizmos.DrawWireCube(center, size);

        // Draw current target if in play mode
        if (Application.isPlaying)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawSphere(_targetPos, 0.5f);
            Gizmos.DrawLine(transform.position, _targetPos);
        }
    }

    /// <summary>
    /// Public method to manually set a new random target (can be called from other scripts).
    /// </summary>
    public void SetNewRandomTarget()
    {
        PickNewTarget();
    }

    /// <summary>
    /// Public method to set a specific target position.
    /// </summary>
    public void SetTarget(Vector3 target)
    {
        _targetPos = target;
    }
}
