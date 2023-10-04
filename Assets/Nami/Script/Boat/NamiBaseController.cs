using System;
using UnityEngine;

namespace Nami
{
    public class NamiBaseController : MonoBehaviour
    {
        [NonSerialized] protected NamiBoat controller; // the boat controller
        [NonSerialized] protected NamiEngine engine; // the engine script

        public virtual void OnEnable()
        {
            if (TryGetComponent(out controller)) // get the controller script
                engine = controller.engine;
        }
    }
}