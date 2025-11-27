Sailsim Nami scripts provide the I/O surface for a self‑sailing boat simulation:

- Boat dynamics, vehicle and engine models
- Sensor simulators (IMU, radar, GPS, cameras)
- NMEA2000‑style telemetry bridge over UDP
- Video streaming via RTSP (`BevRtspStreamer`) and WebRTC (`WebRtcStreamer`)
- Common UDP endpoints are defined in `UdpPublisher` (telemetry multicast + control channel).

See `Messages.md` for a concise description of message formats and transport.
