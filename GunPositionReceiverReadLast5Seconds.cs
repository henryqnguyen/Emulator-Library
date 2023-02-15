using System;
using System.Net;
using System.Net.Sockets;


namespace GunPositionReceiver
{
  internal class Program
  {
    static void Main(string[] args)
    {
      // Set the IP address and port number for the eTALIN
      string ip = "127.0.0.1";
      IPAddress ipAddress = IPAddress.Parse(ip);
      int portNumber = 50121;

      // Create a new endpoint to listen on
      IPEndPoint endPoint = new IPEndPoint(IPAddress.Any, 50121);

      // Create a new UDP client and bind it to the endpoint
      UdpClient client = new UdpClient(endPoint);

      // Initialize the last received timestamp
      UInt32 lastTimestamp = 0;

      // Loop indefinitely to receive and process gun position data messages
      while (true)
      {
        try
        {
          // Receive the gun position data message
          byte[] data = client.Receive(ref endPoint);

          //=====================
          Array.Reverse(data, 4, 4);  // Reverse the byte order of the timestamp field
          Array.Reverse(data, 8, 4);  // Reverse the byte order of the azimuth field
          Array.Reverse(data, 12, 4); // Reverse the byte order of the pitch field
          Array.Reverse(data, 16, 4); // Reverse the byte order of the roll field
                                      //=====================

          // Extract the fields from the message using BitConverter
          UInt16 sequenceNumber = BitConverter.ToUInt16(data, 0);
          UInt16 validity = BitConverter.ToUInt16(data, 2);
          UInt32 timestamp = BitConverter.ToUInt32(data, 4);
          float azimuth = BitConverter.ToSingle(data, 8);
          float pitch = BitConverter.ToSingle(data, 12);
          float roll = BitConverter.ToSingle(data, 16);

          // Only process packets received within the last 5 seconds
          if (timestamp >= lastTimestamp && timestamp < lastTimestamp + 5000)
          {
            // Print the gun position data message to the console
            Console.WriteLine($"Sequence Number: {sequenceNumber}");
            Console.WriteLine($"Validity: {(validity == 1 ? "Valid" : "Invalid")}");
            Console.WriteLine($"Timestamp: {timestamp} ms");
            Console.WriteLine($"Azimuth: {azimuth} mils");
            Console.WriteLine($"Pitch: {pitch} mils");
            Console.WriteLine($"Roll: {roll} mils");
          }
          else
          {
            Console.WriteLine("Received invalid gun position data message");
          }

          // Update the last received timestamp
          lastTimestamp = timestamp;
        }
        catch (Exception e)
        {
          Console.WriteLine($"Error receiving gun position data message: {e.Message}");
        }

        // Pause for 500 milliseconds (i.e. 0.5 seconds)
        //System.Threading.Thread.Sleep(500);
      }

      // Wait for a key to be pressed before closing the console window
      Console.WriteLine("Press any key to exit...");
      Console.ReadLine();
    }
  }
}
