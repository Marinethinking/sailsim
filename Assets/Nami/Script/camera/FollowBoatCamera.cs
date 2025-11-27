using UnityEngine;

namespace Nami
{
    /// <summary>
    /// Simple smooth follow camera for the boat.
    /// Attach this to a camera and assign the boat's transform as the target.
    /// </summary>
    [DisallowMultipleComponent]
    public class FollowBoatCamera : MonoBehaviour
    {
        [Tooltip("Target transform to follow (usually the boat root or a camera mount).")]
        public Transform target;

        [Tooltip("Camera offset in the target's local space.")]
        public Vector3 localOffset = new Vector3(0f, 5f, -10f);

        [Tooltip("Extra local rotation offset (Euler) applied on top of target rotation.")]
        public Vector3 localEulerOffset = new Vector3(10f, 0f, 0f);

        [Tooltip("How fast the camera position catches up to the target.")]
        public float positionLerpSpeed = 5f;

        [Tooltip("How fast the camera rotation catches up to the target.")]
        public float rotationLerpSpeed = 5f;

        private void LateUpdate()
        {
            if (target == null) return;

            // Desired position in world space based on target orientation and local offset
            var desiredPos = target.TransformPoint(localOffset);
            transform.position = Vector3.Lerp(transform.position, desiredPos, positionLerpSpeed * Time.deltaTime);

            // Smoothly match target rotation plus local offset (e.g., tilt down from behind)
            var desiredRot = target.rotation * Quaternion.Euler(localEulerOffset);
            transform.rotation = Quaternion.Slerp(transform.rotation, desiredRot, rotationLerpSpeed * Time.deltaTime);
        }
    }
}


