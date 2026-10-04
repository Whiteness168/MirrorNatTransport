using System.Net;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json;
using UltimateMatchmaker.Common;

namespace UltimateMatchmaker.Server;

public static class Tests
{

    public static void TestClient(string ip, int port, UdpClient? udpClient = null)
    {
        udpClient ??= new UdpClient();
        var endpoint = new IPEndPoint(IPAddress.Parse(ip), port);
        // udpClient.Connect(split[0], int.Parse(split[1]));
        // var bytes = Encoding.UTF8.GetBytes("Hello UltimateMatchmaker");
        Console.WriteLine($"go test {endpoint}");
        udpClient.Send(TestConnectData(), TestConnectData().Length, endpoint);
        Receive(udpClient, endpoint);
        Console.WriteLine("Done");
    }

    private static async void Receive(UdpClient? udpClient, EndPoint endpoint)
    {
        if (udpClient == null)
        {
            return;
        }

        try
        {
            var result = await udpClient.ReceiveAsync();
            Console.WriteLine($"RESULT {result.Buffer.Length} bytes");
        }
        catch (SocketException e)
        {
            Console.WriteLine("Socket exception!");
            Console.WriteLine(e);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
    }

    private static byte[] TestConnectData()
    {
        var base64 = "VVRQAQHzCJ6GiOpwhg=="; //VVRQAQGIY4rPgot5uw==
        return Convert.FromBase64String(base64);
    }

    public static async void TestClient2()
    {
        var endpoint = new IPEndPoint(IPAddress.Parse("194.24.161.46"), 52100);
        var udpClient = new UdpClient();
        
        
        var registrationMessage = new Message
        {
            Type = MessageType.RegisterRequest,
            // ClientId = "test2"
        };
        
        var jsonMessage = JsonConvert.SerializeObject(registrationMessage);
        var bytesMessage = Encoding.UTF8.GetBytes(jsonMessage);
        Console.WriteLine($"register");
        udpClient.Send(bytesMessage, endpoint);
        
        var punchMessage = new Message
        {
            Type = MessageType.PunchRequest,
            // ClientId = "test2",
            // TargetClientId = "test1",
        };
        jsonMessage = JsonConvert.SerializeObject(punchMessage);
        bytesMessage = Encoding.UTF8.GetBytes(jsonMessage);

        Console.WriteLine($"start punch");
        udpClient.Send(bytesMessage, endpoint);
        var responce = await udpClient.ReceiveAsync();
        Console.WriteLine($"skip first from {responce.RemoteEndPoint}");
        responce = await udpClient.ReceiveAsync();
        jsonMessage = Encoding.UTF8.GetString(responce.Buffer);
        var responceMessage = JsonConvert.DeserializeObject<Message>(jsonMessage)
            ?? throw new Exception("Failed to deserialize punch response");
        Console.WriteLine($"responceMessage {responceMessage.PrettyAddress()}");
        await Task.Delay(2000);
        TestClient(responceMessage.IpAddress, responceMessage.Port, udpClient);
    }

    public static async void StartAsTest1()
    {
        var udpClient = new UdpClient();
        var registrationMessage = new Message
        {
            Type = MessageType.RegisterRequest,
            // ClientId = "test1"
        };

        await SendToRelayServerAsync(registrationMessage, udpClient);
        var response = await ReceiveAsync(udpClient);

        if (response.Type != MessageType.RegisterResponse)
        {
            throw new Exception("Failed to register with relay server");
        }

        Console.WriteLine($"Registered host, testing");
        response = await ReceiveAsync(udpClient);
        var punchMessage = new Message
        {
            Type = MessageType.PunchRequest,
            // ClientId = "test1"
        };
        try
        {
            Console.WriteLine($"Send punch {response.GetEndPoint()}");
            var remoteEndPoint = response.GetEndPoint();
            if (remoteEndPoint == null)
            {
                throw new Exception("Failed to parse remote endpoint from relay response");
            }

            await SendToAsync(punchMessage, remoteEndPoint, udpClient);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
                
        Console.WriteLine($"Punch sent to {response.GetEndPoint()}, awaiting for an answer");
        response = await ReceiveAsync(udpClient);
                
        // _udpClient.Close();
        // _udpClient.Dispose();
        Console.WriteLine($"Registered host");
    }

    private static async Task SendToRelayServerAsync(Message message, UdpClient udpClient)
    {
        await SendToAsync(message, IPEndPoint.Parse("194.24.161.46:52100"), udpClient);
    }

    private static async Task SendToAsync(Message message, IPEndPoint endpoint, UdpClient udpClient)
    {
        var jsonData = JsonConvert.SerializeObject(message);
        var bytesData = Encoding.UTF8.GetBytes(jsonData);
        await udpClient.SendAsync(bytesData, bytesData.Length, endpoint);
    }
        
    private static async Task<Message> ReceiveAsync(UdpClient udpClient)
    {
        var result = await udpClient.ReceiveAsync();
        Console.WriteLine($"Received from {result.RemoteEndPoint} bytes {result.Buffer.Length}");
        var jsonData = Encoding.UTF8.GetString(result.Buffer);
        var message = JsonConvert.DeserializeObject<Message>(jsonData);
        return message ?? throw new Exception("Failed to deserialize message");
    }
}
