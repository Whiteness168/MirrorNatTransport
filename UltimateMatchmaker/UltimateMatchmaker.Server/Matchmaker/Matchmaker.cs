using System.Net;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json;

namespace UltimateMatchmaker.Server.Matchmaker;

public class Matchmaker
{
    private readonly TcpListener _tcpListener;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Dictionary<TcpClient, Match> _matches = new();
    private const int BufferSize = 4096;
    private readonly byte[] _buffer = new byte[BufferSize];
    private readonly List<byte> _resultBuffer = new(BufferSize);

    public Matchmaker(int port = 42888)
    {
        _tcpListener = new TcpListener(IPAddress.Any, port);
    }

    public void Start()
    {
        _tcpListener.Start();
        AwaitConnection();
    }

    public void Stop()
    {
        _cancellationTokenSource.Cancel();
    }

    private async void AwaitConnection()
    {
        try
        {
            while (true)
            {
                Console.WriteLine($"Listening...");
                var tcpClient = await _tcpListener.AcceptTcpClientAsync(cancellationToken: _cancellationTokenSource.Token);
                Console.WriteLine($"Connected from {tcpClient.Client.RemoteEndPoint}");
                var receivedBytes = await tcpClient.Client.ReceiveAsync(_buffer, cancellationToken: _cancellationTokenSource.Token);
                _resultBuffer.Clear();
                _resultBuffer.AddRange(_buffer.Take(receivedBytes));
                bool addMatch;
                try
                {
                    addMatch = BitConverter.ToBoolean(_resultBuffer.ToArray());
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    continue;
                }
                Console.WriteLine($"add match? {addMatch}");

                if (addMatch)
                {
                    receivedBytes = await tcpClient.Client.ReceiveAsync(_buffer, cancellationToken: _cancellationTokenSource.Token);
                    _resultBuffer.Clear();
                    _resultBuffer.AddRange(_buffer.Take(receivedBytes));
                    try
                    {
                        var jsonData = Encoding.ASCII.GetString(_resultBuffer.ToArray());
                        var newMatch = JsonConvert.DeserializeObject<Match>(jsonData);
                        if (newMatch == null)
                            continue;
                        _matches.Add(tcpClient, newMatch);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                        continue;
                    }
                }
                else
                {
                    foreach (var match in _matches)
                    {
                        var jsonData = JsonConvert.SerializeObject(match.Value);
                        jsonData += "\r\n";
                        var bytesData = Encoding.UTF8.GetBytes(jsonData);
                        Console.WriteLine($"send {match.Value.Ip} : {bytesData.Length} bytes");
                        await tcpClient.Client.SendAsync(bytesData);
                    }
                }
                tcpClient.Close();
                Console.WriteLine($"Done.");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
        finally
        {
            _tcpListener.Dispose();
        }
    }
}

[Serializable]
public class Match
{
    public string? Ip { get; set; }
    public int Port { get; set; }
    public Dictionary<string, string>? Parameters { get; set; }
}