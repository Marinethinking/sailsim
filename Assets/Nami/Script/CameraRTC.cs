using UnityEngine;
using Unity.WebRTC;
using Firebase.Database;
using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;

namespace Nami
{

    struct RTCModel
    {
        public RTCPeerConnection pc;
        public List<RTCRtpSender> senders;
    }

    public class CameraRTC : MonoBehaviour
    {
        public string VehicleId = "simboat";

        [Tooltip("Camera Name")]
        public string CamId;
        public Camera cam;
        private MediaStream videoStream;
        private List<RTCModel> rtcs = new List<RTCModel>();

        NamiCloudNative nc = NamiCloud.Instance() as NamiCloudNative;

        private void Start()
        {
            Debug.Log("CameraRTC start...." + VehicleId);
            Invoke("ListenCameraRequest", 2);
            Invoke("captureStream", 2);
        }

        public void Restart()
        {
            Debug.Log("CameraRTC Restart...." + VehicleId);
            try
            {
                RemoveTracks();
            }
            catch (Exception e)
            {
                Debug.LogError("Remove tracks error.....");
            }

            DatabaseReference camRef = nc.GetCameraRequestRef();
            camRef.ValueChanged -= OnCameraRequest;

            Start();
        }

        private void RemoveTracks()
        {

            foreach (var rtc in rtcs)
            {

                foreach (var sender in rtc.senders)
                {
                    rtc.pc.RemoveTrack(sender);
                }
                rtc.pc.Close();
                rtc.pc.Dispose();
            }
            rtcs.Clear();
        }



        private void captureStream()
        {
            Debug.Log("captureStream...   ");
            if (videoStream == null)
            {
                videoStream = cam.CaptureStream(1920, 1080);
                // Cubemap cm=new Cubemap();
                // cam.RenderToCubemap(cm);
            }
        }

        public IEnumerator Connect(string reqId, string offer)
        {
            Debug.Log("Connect to " + reqId);
            var configuration = GetSelectedSdpSemantics();
            var pc = new RTCPeerConnection(ref configuration);
            var candidates = new ArrayList();
            pc.OnIceCandidate = candidate =>
            {
                pc.AddIceCandidate(candidate);
                candidates.Add(candidate.Candidate);
            };

            var senders = new List<RTCRtpSender>();

            foreach (var track in videoStream.GetTracks())
            {
                var sender = pc.AddTrack(track, videoStream);
                senders.Add(sender);
                // var parameters = sender.GetParameters();
            }

            StartCoroutine(WebRTC.Update());


            var caps = RTCRtpSender.GetCapabilities(TrackKind.Video);
            Debug.Log(caps);
            var codecs = caps.codecs.Where(c => c.mimeType == "video/H264").ToArray();
            foreach (var transceiver in pc.GetTransceivers())
            {
                transceiver.SetCodecPreferences(codecs);
            }



            var remoteSessionDesc = new RTCSessionDescription
            {
                type = RTCSdpType.Offer,
                sdp = offer
            };
            var rdOp = pc.SetRemoteDescription(ref remoteSessionDesc);
            yield return rdOp;
            if (rdOp.IsError)
            {
                Debug.LogError(rdOp.Error);
            }

            var answerOp = pc.CreateAnswer();
            yield return answerOp;

            var localSessionDesc = answerOp.Desc;
            var ldOp = pc.SetLocalDescription(ref localSessionDesc);
            yield return ldOp;
            if (ldOp.IsError)
            {
                Debug.LogError(ldOp.Error);
            }

            pc.OnNegotiationNeeded = () =>
            {
                Debug.Log("OnNegotiationNeeded");

            };

            pc.OnIceGatheringStateChange = (state) =>
            {
                Debug.Log(state);
                Debug.Log(candidates.Count);
                if (state == RTCIceGatheringState.Complete)
                {
                    PostAnswer(reqId, localSessionDesc.sdp, candidates);
                }
            };

            pc.OnConnectionStateChange = (state) =>
            {
                Debug.Log(state);
            };

            pc.OnIceConnectionChange = (con) =>
            {
                Debug.Log(con);
            };

            rtcs.Add(new RTCModel { pc = pc, senders = senders });

        }

        private static RTCConfiguration GetSelectedSdpSemantics()
        {
            RTCConfiguration config = default;
            var googleIce = new RTCIceServer { urls = new[] { "stun:stun.l.google.com:19302" } };

            config.iceServers = new[] { googleIce };
            config.iceCandidatePoolSize = 10;

            return config;
        }

        public void PostAnswer(string reqId, string answerSdp, ArrayList candidates)
        {
            string answer = answerSdp;
            for (int i = 0; i < candidates.Count; i++)
            {
                var can = candidates[i];
                answer += "a=" + can.ToString();
                answer += "\r\n";
            }

            Debug.Log(answer);

            DatabaseReference camResRef = nc.GetCameraResponseRef();
            camResRef.Child(reqId).SetValueAsync(encodeAnswer(answer));
            DatabaseReference camReqRef = nc.GetCameraRequestRef();
            camReqRef.Child(reqId).RemoveValueAsync();
        }



        private void ListenCameraRequest()
        {

            DatabaseReference camRef = nc.GetCameraRequestRef();

            camRef.ValueChanged += OnCameraRequest;

        }

        public void OnCameraRequest(object sender, ValueChangedEventArgs args)
        {
            if (args.DatabaseError != null)
            {
                Debug.LogError(args.DatabaseError.Message);
                return;
            }
            if (!args.Snapshot.Exists) return;
            IEnumerable<DataSnapshot> reqs = args.Snapshot.Children;


            foreach (DataSnapshot req in reqs)
            {
                string camName = req.Child("cam").Value as string;
                if (camName != CamId) continue;
                var reqId = req.Key;
                string offer = req.Child("offer").Value as string;
                Debug.Log(offer);
                string decoded = decodeOffer(offer);
                Debug.Log(decoded);

                StartCoroutine(Connect(reqId, decoded));
            }

        }



        private string decodeOffer(string encodedString)
        {
            byte[] data = Convert.FromBase64String(encodedString);
            string decodedString = System.Text.Encoding.UTF8.GetString(data);
            return decodedString;
        }
        private string encodeAnswer(string answerSdp)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(answerSdp);
            string encoded = Convert.ToBase64String(bytes);
            return encoded;
        }


    }
}