# Sailsim

SailSim is an on-water simulation which simulated a boat of sailing physics, sensors etc.
SailSim is built by Unity URP, water system is from unity sample project.
https://github.com/Unity-Technologies/BoatAttack

![Alt text](simsim.png)

## Sample

Web sample, <https://sailsim.web.app/> 

Try your keyboard WSAD
** First load is slow, about 1 minute depending on you network status.  

## Feature

1. Simulate boat on the water including: Steering wheel, throttle, GeoLocation, Camera, On water physics.
2. Data synchronized to cloud, remotely control by Firebase. Will add a remote control repo.
3. Webrtc stream simulated camera.

## TODO

1. Lidar, there is no URP lidar simulator, will try HDRP with better water system.
2. Perception, including 3D reconstructin, object detection.
3. More models, including wind tower, animals, boats etc.
4. Multiple cameras.
5. Add adjustable parameters like speed, mass, initial location.

## Get Started from source code

1. Create a Firebase project and enable Auth and Realtime database.
2. Install unity 2022 or later, https://unity.com/download
3. git clone https://github.com/Marinethinking/sailsim.git  You probably need to install https://git-lfs.com/
4. From Assets/Nami/ open NamiScene
5. Download google-service.json from your firebase project, and copy to the Assets folder.
6. Download firebase unity package and import to your unity project, only auth and database is enough.
7. In Firebase Auth, create an email account. sim@mt.com/123456 user name and password are hardcoded in code for now, you can change it in NamiCloud.cs
8. Run in editor. Default vehicle id is vehicle_sim, you can change it by input and enter.

## Get Start from Binary file

Under construction...
