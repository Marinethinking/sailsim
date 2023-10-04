using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Firebase;
using Firebase.Auth;
using Firebase.Database;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework.Internal.Commands;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace Nami
{

    public class NamiCloudNative : NamiCloud
    {
        public FirebaseAuth auth;
        FirebaseDatabase rtdb;
        public DatabaseReference rtdbRef;
        private Rigidbody rigidbody;

        public NamiCloudNative()
        {
            Debug.Log("NamiCloudNative created....");
        }

        public AppOptions getFbOption()
        {
            var path = Application.dataPath + "/google-services.json";
            Debug.Log(path);
            var confStr = File.ReadAllText(path);
            var json = JsonConvert.DeserializeObject<Dictionary<string, object>>(confStr);
            var project_info = json["project_info"] as JObject;

            var pjId = project_info.GetValue("project_id").ToString();
            var url = project_info.GetValue("firebase_url").ToString();

            var client = json["client"] as JArray;
            var clientInfo = client[0]["client_info"];
            var appId = clientInfo["mobilesdk_app_id"].ToString();
            var keys = client[0]["api_key"];
            var key = keys[0]["current_key"].ToString();

            AppOptions options = new AppOptions
            {
                AppId = appId,
                ProjectId = pjId,
                ApiKey = key,
                DatabaseUrl = new System.Uri(url)
            };


            return options;
        }

        public void Init()
        {
#if UNITY_EDITOR
            Debug.Log("Debug in Editor.....");
            FirebaseApp app = FirebaseApp.Create(getFbOption());
            auth = FirebaseAuth.GetAuth(app);
            rtdb = FirebaseDatabase.GetInstance(app);
#else
            auth = FirebaseAuth.DefaultInstance;
            rtdb=FirebaseDatabase.DefaultInstance;
           
#endif
            rtdbRef = rtdb.RootReference;

            GPSEncoder.SetLocalOrigin(new Vector2(44.6374254f, -63.5636231f));
        }

        public override void SignIn()
        {
            Init();

            auth.SignInWithEmailAndPasswordAsync(email, password).ContinueWith(task =>
            {
                if (task.IsCanceled)
                {
                    Debug.LogError("SignInWithEmailAndPasswordAsync was canceled.");
                    return;
                }
                if (task.IsFaulted)
                {
                    Debug.LogError("SignInWithEmailAndPasswordAsync encountered an error: " + task.Exception);
                    return;
                }

                AuthResult result = task.Result;
                Debug.LogFormat("User signed in successfully: {0} ({1})",
                    result.User.DisplayName, result.User.UserId);
            });
        }

        public override void Start()
        {

            rigidbody = vehicleObject.GetComponent<Rigidbody>();
            ListenCommand();
        }

        public override void End()
        {
            DatabaseReference cmdRef = GetCommandRef();

            cmdRef.ValueChanged -= OnCommandChange;
        }

        public override void PublishState()
        {
            Vector3 degs = vehicleObject.transform.eulerAngles;
            VehicleModel vm = new VehicleModel();
            Vector2 geoLoc = GeoLocation();
            vm.State.latitude = geoLoc.x;
            vm.State.longitude = geoLoc.y;
            vm.State.yawDeg = degs.y - 180;
            vm.State.pitchDeg = degs.x;
            vm.State.rollDeg = degs.z;
            vm.State.battery_percentage -= 0.0001f;
            if (rigidbody != null)
                vm.State.velocity = rigidbody.velocity.magnitude;

            Dictionary<string, object> state = vm.StateDictionary();

            rtdbRef.Child("vehicle").Child(VehicleId).Child("state").UpdateChildrenAsync(state);
        }

        public DatabaseReference GetVehicleRef()
        {
            return rtdbRef.Child("vehicle").Child(VehicleId);
        }

        public DatabaseReference GetCameraRequestRef()
        {
            return GetVehicleRef().Child("camera").Child("request");
        }

        public DatabaseReference GetCameraResponseRef()
        {
            return GetVehicleRef().Child("camera").Child("response");
        }

        public DatabaseReference GetCommandRef()
        {
            return GetVehicleRef().Child("command");
        }


        private Vector2 GeoLocation()
        {

            var pos = vehicleObject.transform.position;
            Vector2 geoLoc = GPSEncoder.USCToGPS(pos);
            return geoLoc;
        }


        private void ListenCommand()
        {

            DatabaseReference cmdRef = GetCommandRef();

            cmdRef.ValueChanged += OnCommandChange;

        }

        public void OnCommandChange(object sender, ValueChangedEventArgs args)
        {
            if (args.DatabaseError != null)
            {
                Debug.LogError(args.DatabaseError.Message);
                return;
            }
            if (!args.Snapshot.Exists) return;
            DataSnapshot ds = args.Snapshot;
            object result = ds.Child("result").Value;
            object command = ds.Child("command").Value;
            object param1 = ds.Child("param1").Value;
            object param2 = ds.Child("param2").Value;
            object param3 = ds.Child("param3").Value;
            Debug.Log(command);

            Command com = new Command();
            com.result = result == null ? -1 : Convert.ToInt32(result);
            com.command = Convert.ToInt32(command);
            com.param1 = Convert.ToInt32(param1);
            com.param2 = Convert.ToInt32(param2);
            com.param3 = Convert.ToInt32(param3);
            OnCommand(com);
            CommandDone();
        }



        public void CommandDone()
        {
            DatabaseReference cmdRef = GetCommandRef();
            cmdRef.Child("result").SetValueAsync(0);
        }


    }
}