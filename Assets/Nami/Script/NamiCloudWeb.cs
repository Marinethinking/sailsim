using FirebaseWebGL.Examples.Utils;
using FirebaseWebGL.Scripts.FirebaseBridge;
using FirebaseWebGL.Scripts.Objects;
using UnityEngine;

namespace Nami
{

    public class NamiCloudWeb : NamiCloud
    {

        public NamiCloudWeb()
        {
            Debug.Log("NamiCloudWeb created....");
        }

        public override void SignIn()
        {

            FirebaseAuth.SignInWithEmailAndPassword(email, password, "boat", "DisplayInfo", "DisplayErrorObject");
        }
        public void DisplayInfo(string info)
        {

            Debug.Log(info);
        }

        public void DisplayErrorObject(string error)
        {
            var parsedError = StringSerializationAPI.Deserialize(typeof(FirebaseError), error) as FirebaseError;
            Debug.LogError(parsedError.message);
        }
        public override void Start() { }
        public override void End() { }

        public override void PublishState() { }
    }


}