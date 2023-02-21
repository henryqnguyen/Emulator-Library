using ERCA_Library.Network;
using Microsoft.Win32;
using System;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

namespace EPCS_Emulator.DataStructures
{
    public sealed class Atalin : IDisposable
    {
        static class NativeMethods
        {
            [return: MarshalAs(UnmanagedType.Bool)]
            [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
            public static extern bool PostMessage(HandleRef hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

            public const uint WM_User = 0x400;
            public const uint WMU_ADDTOGUNELEV = WM_User + 0x80;
            public const uint WMU_ADDTOGUNAZIM = WM_User + 0x81;
            public const uint WMU_VEHICLEMOVE = WM_User + 0x88;
            public const uint WMU_TUBEMOVE = WM_User + 0x8A;
        }

        private readonly UDPConnection _talinConnection;
        private TcpClient _talinTCPConnection;
        private object _howitzerHandle;
        private UdpClient _udpClient;

        private readonly System.Timers.Timer updateTimer;
        private bool _updateAvailable = false;
        private bool _receivedFromTalin = false;
        DateTime retry;

        private enum DataRate
        {
            RATE_300HZ = 1,
            RATE_150HZ,
            RATE_7HZ,
            RATE_37_5HZ,
            RATE_18_75HZ
        }

        private const int PDI_SOM = 0x70717883; //hex
        private const uint PDI_EOM = 0x83787170;
        private const uint PDI_MAX_DATA = 256;
        private const int PDI_Header_Length = 9;
        private const int PDI_Footer_Length = 2;
        private const int PDI_Msg_Setup_MsgID = 8050;
        private const int PDI_PAR_Data_MsgID = 9200;
        private const int PAR_Request_Length = 3;
        private int seqNum = 0;
        private bool stopButtonSelected;

        public Atalin(UDPConnection inTalinConnection)
        {
            _talinConnection = inTalinConnection;
            _talinTCPConnection = new TcpClient();
            updateTimer = new System.Timers.Timer(25); // 40Hz
            updateTimer.Elapsed += SendUpdate;
            retry = DateTime.Now;
        }

        public void Start()
        {
            updateTimer.Start();
            stopButtonSelected = false;
      ReceiveGunDriveDataAsync();
        }

        public void Stop()
        {
            _talinTCPConnection.Close();
            _udpClient.Close();
            stopButtonSelected = true;
        }

        public bool ReceivedFromTalin
        {
            get
            {
                return _receivedFromTalin;
            }
        }

        public void TriggerUpdate()
        {
            if (!_receivedFromTalin)
            {
                _updateAvailable = true;
            }
        }

        private void SendUpdate(object source, EventArgs e)
        {
            if (_updateAvailable)
            {
                // check if trainer emulator is running
                RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Paladin\AFCS NT\SWB 9E\Windows");
                if (key != null)
                {
                    _howitzerHandle = key.GetValue("Howitzer");
                }

                // try to send to trainer or ATSD emulators
                if (_howitzerHandle != null)
                {
                    bool result = NativeMethods.PostMessage(new HandleRef(this, new IntPtr((int)_howitzerHandle)),
                        NativeMethods.WMU_TUBEMOVE,
                        new IntPtr((int)Math.Round(Core.Instance.Position.Earth.Azimuth * 10)),
                        new IntPtr((int)Math.Round(Core.Instance.Position.Earth.Elevation * 10)));
                    if (!result)
                    {
                        // trainer may have shutdown
                        _howitzerHandle = null;
                    }
                }
                if (!_talinConnection.IsClientNull())
                {
                    try
                    {
                        byte[] data = new byte[16];
                        float vehicleAz = (Core.Instance.Position.Earth.Azimuth - Core.Instance.Orientation.Yaw + 9600) % 6400;
                        Buffer.BlockCopy(BitConverter.GetBytes(IPAddress.HostToNetworkOrder((int)(vehicleAz * 1000))), 0, data, 0, 4);
                        Buffer.BlockCopy(BitConverter.GetBytes(IPAddress.HostToNetworkOrder((int)(Core.Instance.Position.Earth.Elevation * 1000))), 0, data, 4, 4);
                        Buffer.BlockCopy(BitConverter.GetBytes(IPAddress.HostToNetworkOrder((int)(1 * 1000))), 0, data, 8, 4);
                        Buffer.BlockCopy(BitConverter.GetBytes(IPAddress.HostToNetworkOrder((int)(1 * 1000))), 0, data, 12, 4);
                        _talinConnection.Send(data, Properties.Settings.Default.TalinIPAddress, Properties.Settings.Default.TalinUDPPort);
                    }
                    catch (Exception ex)
                    {
                        // Emulator not running
                        Console.WriteLine("[Atalin](SendUpdate) ERROR Exception");
                        Console.WriteLine(ex.Message);
                    }
                }

                _updateAvailable = false;
            }

            if (_talinTCPConnection == null)
            {
                _talinTCPConnection = new TcpClient();
            }

            if (_talinTCPConnection.Client == null)
                return;

            if (!_talinTCPConnection.Connected && retry.AddSeconds(3) < DateTime.Now)
            { // try to connect every 3 seconds
                retry = DateTime.Now;

                if (_talinTCPConnection == null || _talinTCPConnection.Client.Connected == false)
                {
                    _talinTCPConnection = new TcpClient();
                }

                try
                {
                    if (!stopButtonSelected)
                        _talinTCPConnection.Connect(Properties.Settings.Default.TalinIPAddress, Int32.Parse(Properties.Settings.Default.TalinPort));
                }
                catch (SocketException ex)
                {
                    Console.WriteLine("[Atalin](SendUpdate) ERROR Exception");
                    Console.WriteLine(ex.Message);
                }

                if (_talinTCPConnection.Connected)
                {
                    //Task.Run(() => Listen());
                    //GetPar();
                }
            }
            else if (_talinTCPConnection.Connected && retry.AddSeconds(3) < DateTime.Now)
            { // haven't received data in 3 seconds so re-request
                retry = DateTime.Now;
                //GetPar();
            }
        }

        private void GetPar()
        {
            if (_talinTCPConnection.Connected)
            {
                byte[] msg = new byte[(PDI_Header_Length + PAR_Request_Length + PDI_Footer_Length) * sizeof(uint)]; //14 words * 4 bytes = 56 
                // header
                Buffer.BlockCopy(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(PDI_SOM)), 0, msg, 0, 4); //0 Start of Message
                Buffer.BlockCopy(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(PDI_Msg_Setup_MsgID)), 0, msg, 4, 4);//1 
                Buffer.BlockCopy(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(seqNum)), 0, msg, 8, 4); //2
                Buffer.BlockCopy(BitConverter.GetBytes(0), 0, msg, 12, 4); //3 Words 3&4 hold a double representing transmit time
                Buffer.BlockCopy(BitConverter.GetBytes(0), 0, msg, 16, 4); //4 
                Buffer.BlockCopy(BitConverter.GetBytes(0), 0, msg, 20, 4); //5 Param Word #1 filled by eTALIN on response
                Buffer.BlockCopy(BitConverter.GetBytes(0), 0, msg, 24, 4); //6 Reserved
                Buffer.BlockCopy(BitConverter.GetBytes(0), 0, msg, 28, 4); //7 Reserved
                Buffer.BlockCopy(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(PAR_Request_Length)), 0, msg, 32, 4); //8

                // data
                Buffer.BlockCopy(BitConverter.GetBytes(0), 0, msg, 36, 4); //Reserved
                Buffer.BlockCopy(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(PDI_PAR_Data_MsgID)), 0, msg, 40, 4); //msgID for PAR data
                Buffer.BlockCopy(BitConverter.GetBytes(IPAddress.HostToNetworkOrder((int)DataRate.RATE_150HZ)), 0, msg, 44, 4); //rate to receive PAR data

                //footer
                byte[] checkmsg = new byte[(PDI_Header_Length + PAR_Request_Length - 1) * sizeof(uint)]; //44
                Buffer.BlockCopy(msg, 4, checkmsg, 0, 44);

                Buffer.BlockCopy(BitConverter.GetBytes(IPAddress.HostToNetworkOrder((int)PDI_CRC(checkmsg))), 0, msg, 48, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(PDI_EOM)), 4, msg, 52, 4); //End of message

                try
                {
                    _talinTCPConnection.GetStream().Write(msg, 0, msg.Length);
                }
                catch (SocketException ex)
                {
                    _talinTCPConnection.Close();
                    Console.WriteLine("[Atalin](GetPar) ERROR Exception");
                    Console.WriteLine(ex.Message);
                }

                seqNum++;
            }
        }

        private void Listen()
        {
            byte[] receivedBytes = new byte[(PDI_Header_Length + PDI_MAX_DATA + PDI_Footer_Length) * sizeof(uint)]; // (9 + 256 + 2) * 4
            var stream = _talinTCPConnection.GetStream();
            byte[] pitchBytes = new byte[8]; //64 bit double
            byte[] yawBytes = new byte[8]; //64 bit double
            //byte[] userPitchBytes = new byte[8]; //64 bit double
            //byte[] userYawBytes = new byte[8]; //64 bit double

            if (_talinTCPConnection.Connected && stream.CanRead)
            {
                int count;

                if (!stream.CanRead)
                    return;

                if (stopButtonSelected)
                    return;

                try {
                    while ((count = stream.Read(receivedBytes, 0, receivedBytes.Length)) != 0)
                    {

                        if (count > 0)
                        {
                            uint messageID = (uint)IPAddress.NetworkToHostOrder((int)BitConverter.ToUInt32(receivedBytes, 4));

                            if (messageID == PDI_PAR_Data_MsgID)
                            {
                                //Copy body frame pitch and yaw data from receivedBytes array
                                Buffer.BlockCopy(receivedBytes, 36 * 4, pitchBytes, 0, 8);
                                Buffer.BlockCopy(receivedBytes, 38 * 4, yawBytes, 0, 8);

                                //Copy user frame pitch and yaw data from receivedBytes array
                                //Buffer.BlockCopy(receivedBytes, 44 * 4, userPitchBytes, 0, 8);
                                //Buffer.BlockCopy(receivedBytes, 46 * 4, userYawBytes, 0, 8);

                                //Convert to Big-Endian
                                Array.Reverse(pitchBytes);
                                Array.Reverse(yawBytes);

                                //Convert from bytes to double
                                double pitchDouble = BitConverter.ToDouble(pitchBytes, 0);
                                double yawDouble = BitConverter.ToDouble(yawBytes, 0);

                                //Convert from radians to mils
                                double pitchMils = radiansToMils(pitchDouble);
                                double yawMils = radiansToMils(yawDouble);

                                // if epcs is in a move, accept talin data
                                if (IsInMove())
                                {

                                    if (pitchMils != Core.Instance.Position.Platform.Elevation)
                                    {
                                        Core.Instance.Position.Platform.Elevation = (float)pitchMils;
                                    }
                                    if (yawMils != Core.Instance.Position.Platform.Azimuth)
                                    {
                                        Core.Instance.Position.Platform.Azimuth = (float)yawMils;
                                    }
                                    _receivedFromTalin = true;
                                }
                                else _receivedFromTalin = false;
                            }
                            retry = DateTime.Now;
                        }
                    }// end while

                }
                catch (IOException ex)
                {
                    if (ex.InnerException is SocketException)
                    {
                        stream.Close();
                        _talinTCPConnection.Close();
                    }
                }
            }
        }

    private async void ReceiveGunDriveDataAsync(bool stopListening = false)
    {
      // Create a new endpoint to listen to aTALIN
      IPEndPoint endPoint;

      endPoint = new IPEndPoint(IPAddress.Any, 50121);

      // Create a new UDP client and bind it to the endpoint
      _udpClient = new UdpClient(endPoint);

      // Loop indefinitely to receive and process gun position data messages
      while (!stopButtonSelected)
      {
        try
        {
          // Receive the gun position data message
          UdpReceiveResult result = await _udpClient.ReceiveAsync();

          byte[] data = result.Buffer;

          // Reverse the byte order of the timestamp has no effect
          Array.Reverse(data, 4, 4);
          Array.Reverse(data, 8, 4);  // Reverse the byte order of the azimuth field
          Array.Reverse(data, 12, 4); // Reverse the byte order of the pitch field
          Array.Reverse(data, 16, 4); // Reverse the byte order of the roll field

          if (data.Length == 20)
          {
            // Extract the fields from the message using BitConverter
            UInt16 sequenceNumber = BitConverter.ToUInt16(data, 0);
            UInt16 validity = BitConverter.ToUInt16(data, 2);
            UInt32 timestamp = BitConverter.ToUInt32(data, 4);
            float azimuthMils = BitConverter.ToSingle(data, 8);
            float pitchMils = BitConverter.ToSingle(data, 12);
            float roll = BitConverter.ToSingle(data, 16);

            if (IsInMove())
            {

              if (pitchMils != Core.Instance.Position.Platform.Elevation)
              {
                Core.Instance.Position.Platform.Elevation = pitchMils;
              }
              if (azimuthMils != Core.Instance.Position.Platform.Azimuth)
              {
                Core.Instance.Position.Platform.Azimuth = azimuthMils;
              }
              _receivedFromTalin = true;

            }
            else 
            {
              _receivedFromTalin = false;
            }

            Console.WriteLine($"az: {azimuthMils}, pitch: {pitchMils}");
          }
          else
          {
            Console.WriteLine("Received invalid gun position data message");
          }
        }
        catch (Exception e)
        {
          Console.WriteLine($"Error receiving gun position data message: {e.Message}");
        }

        //await Task.Delay(100); // wait 100 milliseconds before next iteration
      }
    }
        private bool IsInMove()
        {
                // if epcs is in a move, return true
                if (Core.Instance.Motion.Elevation.MoveStatus == Enumerations.MoveStatus.movingToTarget ||
                    Core.Instance.Motion.Elevation.MoveStatus == Enumerations.MoveStatus.movingToStow ||
                    Core.Instance.Motion.Elevation.MoveStatus == Enumerations.MoveStatus.movingByJoystick ||
                    Core.Instance.Motion.Elevation.MoveStatus == Enumerations.MoveStatus.stopping ||
                    Core.Instance.Motion.Azimuth.MoveStatus == Enumerations.MoveStatus.movingToTarget ||
                    Core.Instance.Motion.Azimuth.MoveStatus == Enumerations.MoveStatus.movingToStow ||
                    Core.Instance.Motion.Azimuth.MoveStatus == Enumerations.MoveStatus.movingByJoystick ||
                    Core.Instance.Motion.Azimuth.MoveStatus == Enumerations.MoveStatus.stopping)
                {
                    return true;
                }
                else return false;
        }

        private double radiansToMils(double radians)
        {
            return (radians * 6400.0) / (2 * Math.PI);
        }

        static uint[] m_crc32Tbl = { /* CRC polynomial 0xedb88320 */
	       0x00000000, 0x77073096, 0xee0e612c, 0x990951ba, 0x076dc419, 0x706af48f,
           0xe963a535, 0x9e6495a3, 0x0edb8832, 0x79dcb8a4, 0xe0d5e91e, 0x97d2d988,
           0x09b64c2b, 0x7eb17cbd, 0xe7b82d07, 0x90bf1d91, 0x1db71064, 0x6ab020f2,
           0xf3b97148, 0x84be41de, 0x1adad47d, 0x6ddde4eb, 0xf4d4b551, 0x83d385c7,
           0x136c9856, 0x646ba8c0, 0xfd62f97a, 0x8a65c9ec, 0x14015c4f, 0x63066cd9,
           0xfa0f3d63, 0x8d080df5, 0x3b6e20c8, 0x4c69105e, 0xd56041e4, 0xa2677172,
           0x3c03e4d1, 0x4b04d447, 0xd20d85fd, 0xa50ab56b, 0x35b5a8fa, 0x42b2986c,
           0xdbbbc9d6, 0xacbcf940, 0x32d86ce3, 0x45df5c75, 0xdcd60dcf, 0xabd13d59,
           0x26d930ac, 0x51de003a, 0xc8d75180, 0xbfd06116, 0x21b4f4b5, 0x56b3c423,
           0xcfba9599, 0xb8bda50f, 0x2802b89e, 0x5f058808, 0xc60cd9b2, 0xb10be924,
           0x2f6f7c87, 0x58684c11, 0xc1611dab, 0xb6662d3d, 0x76dc4190, 0x01db7106,
           0x98d220bc, 0xefd5102a, 0x71b18589, 0x06b6b51f, 0x9fbfe4a5, 0xe8b8d433,
           0x7807c9a2, 0x0f00f934, 0x9609a88e, 0xe10e9818, 0x7f6a0dbb, 0x086d3d2d,
           0x91646c97, 0xe6635c01, 0x6b6b51f4, 0x1c6c6162, 0x856530d8, 0xf262004e,
           0x6c0695ed, 0x1b01a57b, 0x8208f4c1, 0xf50fc457, 0x65b0d9c6, 0x12b7e950,
           0x8bbeb8ea, 0xfcb9887c, 0x62dd1ddf, 0x15da2d49, 0x8cd37cf3, 0xfbd44c65,
           0x4db26158, 0x3ab551ce, 0xa3bc0074, 0xd4bb30e2, 0x4adfa541, 0x3dd895d7,
           0xa4d1c46d, 0xd3d6f4fb, 0x4369e96a, 0x346ed9fc, 0xad678846, 0xda60b8d0,
           0x44042d73, 0x33031de5, 0xaa0a4c5f, 0xdd0d7cc9, 0x5005713c, 0x270241aa,
           0xbe0b1010, 0xc90c2086, 0x5768b525, 0x206f85b3, 0xb966d409, 0xce61e49f,
           0x5edef90e, 0x29d9c998, 0xb0d09822, 0xc7d7a8b4, 0x59b33d17, 0x2eb40d81,
           0xb7bd5c3b, 0xc0ba6cad, 0xedb88320, 0x9abfb3b6, 0x03b6e20c, 0x74b1d29a,
           0xead54739, 0x9dd277af, 0x04db2615, 0x73dc1683, 0xe3630b12, 0x94643b84,
           0x0d6d6a3e, 0x7a6a5aa8, 0xe40ecf0b, 0x9309ff9d, 0x0a00ae27, 0x7d079eb1,
           0xf00f9344, 0x8708a3d2, 0x1e01f268, 0x6906c2fe, 0xf762575d, 0x806567cb,
           0x196c3671, 0x6e6b06e7, 0xfed41b76, 0x89d32be0, 0x10da7a5a, 0x67dd4acc,
           0xf9b9df6f, 0x8ebeeff9, 0x17b7be43, 0x60b08ed5, 0xd6d6a3e8, 0xa1d1937e,
           0x38d8c2c4, 0x4fdff252, 0xd1bb67f1, 0xa6bc5767, 0x3fb506dd, 0x48b2364b,
           0xd80d2bda, 0xaf0a1b4c, 0x36034af6, 0x41047a60, 0xdf60efc3, 0xa867df55,
           0x316e8eef, 0x4669be79, 0xcb61b38c, 0xbc66831a, 0x256fd2a0, 0x5268e236,
           0xcc0c7795, 0xbb0b4703, 0x220216b9, 0x5505262f, 0xc5ba3bbe, 0xb2bd0b28,
           0x2bb45a92, 0x5cb36a04, 0xc2d7ffa7, 0xb5d0cf31, 0x2cd99e8b, 0x5bdeae1d,
           0x9b64c2b0, 0xec63f226, 0x756aa39c, 0x026d930a, 0x9c0906a9, 0xeb0e363f,
           0x72076785, 0x05005713, 0x95bf4a82, 0xe2b87a14, 0x7bb12bae, 0x0cb61b38,
           0x92d28e9b, 0xe5d5be0d, 0x7cdcefb7, 0x0bdbdf21, 0x86d3d2d4, 0xf1d4e242,
           0x68ddb3f8, 0x1fda836e, 0x81be16cd, 0xf6b9265b, 0x6fb077e1, 0x18b74777,
           0x88085ae6, 0xff0f6a70, 0x66063bca, 0x11010b5c, 0x8f659eff, 0xf862ae69,
           0x616bffd3, 0x166ccf45, 0xa00ae278, 0xd70dd2ee, 0x4e048354, 0x3903b3c2,
           0xa7672661, 0xd06016f7, 0x4969474d, 0x3e6e77db, 0xaed16a4a, 0xd9d65adc,
           0x40df0b66, 0x37d83bf0, 0xa9bcae53, 0xdebb9ec5, 0x47b2cf7f, 0x30b5ffe9,
           0xbdbdf21c, 0xcabac28a, 0x53b39330, 0x24b4a3a6, 0xbad03605, 0xcdd70693,
           0x54de5729, 0x23d967bf, 0xb3667a2e, 0xc4614ab8, 0x5d681b02, 0x2a6f2b94,
           0xb40bbe37, 0xc30c8ea1, 0x5a05df1b, 0x2d02ef8d
           };

        private uint PDI_CRC(byte[] buffer)
        {
            uint crc = 0;

            crc = crc ^ ~0U;

            for (int i = 0; i < buffer.Length; i++)
            {
                crc = m_crc32Tbl[(crc ^ buffer[i]) & 0xFF] ^ (crc >> 8);
            }

            return crc ^ ~0U;
        }
        public void Dispose()
        {
            updateTimer.Dispose();
        }
    }
}
