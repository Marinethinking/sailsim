# Sailsim

SailSim is an on-water simulation which simulated a boat of sailing physics, sensors etc.
SailSim is built by Unity URP, water system is from unity sample project.
https://github.com/Unity-Technologies/BoatAttack

![Alt text](simsim.png)

## Sample

Web sample, <https://sailsim.web.app/>

Try your keyboard WSAD
\*\* First load is slow, about 1 minute depending on you network status.

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
3. git clone https://github.com/Marinethinking/sailsim.git You probably need to install https://git-lfs.com/
4. From Assets/Nami/ open NamiScene
5. Download google-service.json from your firebase project, and copy to the Assets folder.
6. Download firebase unity package and import to your unity project, only auth and database is enough.
7. In Firebase Auth, create an email account. sim@mt.com/123456 user name and password are hardcoded in code for now, you can change it in NamiCloud.cs
8. Run in editor. Default vehicle id is simboat, you can change it by input and enter.

## Get Start from Binary file

Under construction...

## Connect to ROS

Depends on UnitySensors<https://github.com/Field-Robotics-Japan/UnitySensors/tree/master>

1. Setup ROS connector: https://github.com/Unity-Technologies/Unity-Robotics-Hub/blob/main/tutorials/ros_unity_integration/setup.md
2. Install UnitySensorsROS, just follow the README on https://github.com/Field-Robotics-Japan/UnitySensors
3. In your Unity Editor Hierarchy, Open NamiScene/Acadia/VLP-16/VelodyneSensor, change it to ROS sensor.

### cloud points to laser scan

Lets say you are on ros2 humble, (or change to your version name)

1. In unity editor, install package by git url : https://github.com/Field-Robotics-Japan/UnitySensors.git?path=/Assets/UnitySensorsROS#v1.0b
2. Setup Unity Ros connector: https://github.com/Unity-Technologies/Unity-Robotics-Hub/blob/main/tutorials/ros_unity_integration/setup.md
3. apt install ros-humble-pointcloud-to-laserscan
4. ros2 run pointcloud_to_laserscan pointcloud_to_laserscan_node
5. Start ros_tcp_endpoint:
   ros2 run ros_tcp_endpoint default_server_endpoint --ros-args -p ROS_IP:=192.168.20.104

### Webrtc to RTMP

1. Install unity package https://github.com/ossrs/srs-unity
2. Run RSR container on your local media server
   docker run --name srs -it -p 1935:1935 -p 1985:1985 -p 8080:8080 \
    --env CANDIDATE=192.168.20.104 -p 8000:8000/udp \
    ossrs/srs:5 ./objs/srs -c conf/docker.conf
3.  HTTPS for remote media server
    docker run --name srs -it -p 1935:1935 -p 1985:1985 -p 8080:8080 -p 1990:1990 -p 8088:8088\
    --env CANDIDATE=192.168.20.104 -p 8000:8000/udp \
    ossrs/srs:5 ./objs/srs -c conf/https.docker.conf


### Data channel broadcast
https://www.meetecho.com/blog/data-channels-broadcasting-with-janus/