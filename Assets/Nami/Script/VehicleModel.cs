using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Firebase.Database;
using UnityEngine;

public class VehicleModel
{
    public string id;
    public string name;
    public VehicleState State = new VehicleState();


    public string StateJsonString()
    {
        return JsonUtility.ToJson(State);

    }

    public Dictionary<string, object> StateDictionary()
    {
        Dictionary<string, object> result = new Dictionary<string, object>();

        result["battery_percentage"] = State.battery_percentage;
        result["latitude"] = State.latitude;
        result["longitude"] = State.longitude;
        result["armed"] = State.armed;
        result["mode"] = State.mode;
        result["rollDeg"] = State.rollDeg;
        result["pitchDeg"] = State.pitchDeg;
        result["yawDeg"] = State.yawDeg;
        result["altitude"] = State.altitude;
        result["velocity"] = State.velocity;
        result["rssi"] = State.rssi;
        result["satellites"] = State.satellites;
        result["time"] = State.time;


        return result;
    }


}

public class VehicleState
{
    public float battery_percentage = 1;
    public float latitude;
    public float longitude;
    public bool armed = true;
    public string mode = "GUIDED";
    public float rollDeg = 0;
    public float pitchDeg = 0;
    public float yawDeg = 0;
    public float altitude = 0;
    public float velocity = 0;
    public int rssi = 255;
    public int satellites = 10;
    public object time = ServerValue.Timestamp;
}

public class Command
{
    public int result;
    public int command;
    public int param1;
    public int param2;
    public int param3;
}