using System;
using UnityEngine;
using UnityEngine.InputSystem;
using TMPro;


namespace Nami
{
    public class Vehicle : MonoBehaviour
    {
        public NamiEngine engine;
        public Vector2 moveInput;
        public bool holdRight;
        public Vector2 lookInput;
        public GameObject CinemachineCameraTarget;
        NamiCloud nc;
        public string VehicleId = "vehicle_sim";
        public TMP_InputField VehicleIdInput;

        public float throttle;
        public float steering;
        public float brake = 0;


        public void OnMove(InputValue value)
        {
            moveInput = value.Get<Vector2>();

            throttle = moveInput.y;
            steering = moveInput.x;
            brake = 0;
        }
        public void OnHoldRight(InputValue value)
        {
            holdRight = value.isPressed;
        }
        public void OnLook(InputValue value) { lookInput = value.Get<Vector2>(); }


        private void Awake()
        {
            TryGetComponent(out engine.RB);

            VehicleIdInput.onSubmit.AddListener((vid) => onVehicleChange(vid));
        }

        private void OnEnable()
        {
            Debug.Log("Vehicle.OnEnable().....");
            SyncCloud();
        }

        void onVehicleChange(string vid)
        {
            Debug.Log("Vehicle Changed to " + vid);
            if (vid == VehicleId) return;
            VehicleId = vid;
            Restart();
        }

        private void SyncCloud()
        {
            nc = NamiCloud.Instance();
            nc.VehicleId = VehicleId;
            nc.vehicleObject = gameObject;
            nc.OnCommand = (com) => OnCommand(com);

            nc.SignIn();
            // wait for sign in
            InvokeRepeating("PublishState", 3, 1);
            Invoke("CloudStart", 3);
        }

        public void PublishState()
        {

            nc.PublishState();
        }

        public void CloudStart()
        {
            nc.Start();
        }

        public void Restart()
        {
            CancelInvoke();
            var camRtc = GetComponent<CameraRTC>();

            if (camRtc == null)
            {
                Debug.LogError("Camera RTC script is null.");
                return;
            }
            camRtc.VehicleId = VehicleId;
            camRtc.Restart();

            SyncCloud();
        }


        private void FixedUpdate()
        {
            if (brake == 1)
            {
                throttle -= brake * Time.deltaTime * 0.1f;
                if (steering > 0)
                    steering -= brake * Time.deltaTime * 0.1f;
                else steering += brake * Time.deltaTime * 0.1f;
                if (throttle < 0.01) throttle = 0;
                if (steering < 0.01 && steering > -0.01) steering = 0;
            }


            engine.Accelerate(throttle);
            engine.Turn(steering);

        }

        private void LateUpdate()
        {
            RotateCamera();
        }




        private void RotateCamera()
        {

            if (throttle > 0.1)
                CinemachineCameraTarget.transform.localRotation = Quaternion.identity;

            if (!holdRight)
            {
                return;
            }

            CinemachineCameraTarget.transform.Rotate(Vector3.up * lookInput.x * Time.deltaTime);

        }

        private void OnCommand(Command com)
        {
            Debug.Log("Vehicle OnCommand: " + com.command);
            if (com.command == 3559) CloudMove(com);

        }

        private void CloudMove(Command com)
        {
            Debug.Log("Cloud Move: " + com.result + " " + com.param1);
            if (com.result == 0)
            {
                brake = 1;
                return;
            }
            brake = 0;
            if (com.param1 > 0.1) steering = 1;
            else if (com.param1 < -0.1) steering = -1;
            else steering = 0;

            if (com.param2 > 0.1) throttle = 1;
            else if (com.param2 < -0.1) throttle = -1;
            else throttle = 0;
        }


    }

}