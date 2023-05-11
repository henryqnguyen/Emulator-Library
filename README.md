# EPCS Emulator

**Atalin.cs**

Q1. Why converts to Earth before sending to atsd atalin emulator?

```c#
float vehicleAz = (Position.ToEarthAzimuth(_azimuthUpdated) - Core.Instance.Orientation.Yaw + 9600) % 6400f;
Buffer.BlockCopy(BitConverter.GetBytes(IPAddress.HostToNetworkOrder((int)(vehicleAz * 1000))), 0, data, 0, 4);
Buffer.BlockCopy(BitConverter.GetBytes(IPAddress.HostToNetworkOrder((int)(_elevationUpdated * 1000))), 0, data, 4, 4);
```


