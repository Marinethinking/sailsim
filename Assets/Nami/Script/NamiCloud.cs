using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Nami
{
    public abstract class NamiCloud
    {

        public string VehicleId;
        public GameObject vehicleObject;
        public Camera cam;

        protected string email = "sim@mt.com";
        protected string password = "123456";

        private static NamiCloud instance;


        public abstract void SignIn();

        public static NamiCloud Instance()
        {
            if (instance != null) return instance;
#if !UNITY_EDITOR && UNITY_WEBGL
            instance= new NamiCloudWeb();
#else
            instance = new NamiCloudNative();
#endif

            return instance;
        }

        public abstract void Start();
        public abstract void End();
        public abstract void PublishState();

        public delegate void DelegateOnCommand(Command com);
        public DelegateOnCommand OnCommand { get; set; }
    }
}