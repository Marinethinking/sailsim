using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace Nami
{
    public class CameraMetaPublisher : IDisposable
    {
        [Serializable]
        public struct CameraMeta
        {
            public string camera_id;
            public ulong frame_id;
            public double timestamp; // Unix epoch seconds or simulation time
            public long sequence;
        }

        private readonly UdpClient _udpClient;
        private readonly IPEndPoint _remoteEndPoint;
        private long _sequence = 0;

        public CameraMetaPublisher()
        {
            _udpClient = UdpPublisher.CreateCameraMetaSender();
            _remoteEndPoint = UdpPublisher.CameraMetaEndpoint;
        }

        public void Publish(string cameraId, ulong frameId, double timestamp)
        {
            var meta = new CameraMeta
            {
                camera_id = cameraId,
                frame_id = frameId,
                timestamp = timestamp,
                sequence = _sequence++
            };

            string json = JsonUtility.ToJson(meta);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            _udpClient.Send(bytes, bytes.Length, _remoteEndPoint);
        }

        public void Dispose()
        {
            _udpClient?.Close();
        }
    }
}
