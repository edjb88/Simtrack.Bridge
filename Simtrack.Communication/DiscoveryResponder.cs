using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Simtrack.Communication;

/// <summary>
/// Anuncia el PC en la red local para que la app del iPhone lo encuentre sola,
/// sin tener la IP escrita a mano (el router cambia las IP al reiniciar).
///
/// Protocolo (UDP 4245):
///   iPhone  → broadcast {"protocol":"simtrack.bridge.v1","type":"bridge.discover"}
///   PC      → respuesta  {"protocol":"simtrack.bridge.v1","type":"bridge.reply",
///                         "trackingPort":4242,"controlPort":4243,
///                         "telemetryPort":4244,"simhubPort":8888,...}
///
/// El iPhone usa la IP de origen de la respuesta, así que funciona tanto por
/// Wi-Fi como por el cable de iPhone (tethering USB), aunque el PC tenga dos
/// direcciones a la vez.
/// </summary>
public sealed class DiscoveryResponder : IDisposable
{
    public const int DiscoveryPort = 4245;

    private readonly object _gate = new();

    private UdpClient? _client;
    private CancellationTokenSource? _cancellation;
    private Task? _receiveTask;
    private Task? _beaconTask;

    private long _requestsReceived;

    public event Action<string>? LogReceived;

    /// <summary>Puerto TCP donde escucha SimHub (se usa para el dashboard del iPhone).</summary>
    public int SimHubPort { get; set; } = 8888;

    public bool IsRunning
    {
        get { lock (_gate) { return _cancellation is not null; } }
    }

    public long RequestsReceived => Interlocked.Read(ref _requestsReceived);

    public void Start()
    {
        lock (_gate)
        {
            if (_cancellation is not null)
                return;

            try
            {
                var client = new UdpClient(AddressFamily.InterNetwork);
                client.EnableBroadcast = true;
                client.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
                _client = client;
            }
            catch (SocketException ex)
            {
                throw new InvalidOperationException(
                    $"No se pudo abrir el puerto UDP {DiscoveryPort} para el descubrimiento. " +
                    $"Detalle: {ex.Message}", ex);
            }

            _cancellation = new CancellationTokenSource();
            _receiveTask = ReceiveAsync(_client, _cancellation.Token);
            _beaconTask = BeaconAsync(_client, _cancellation.Token);

            string addresses = string.Join(", ", LocalAddresses().Select(a => a.ToString()));
            LogReceived?.Invoke($"Descubrimiento activo en UDP {DiscoveryPort} (IPs del PC: {addresses}).");
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cancellation;
        UdpClient? client;

        lock (_gate)
        {
            cancellation = _cancellation;
            client = _client;
            _cancellation = null;
            _client = null;
        }

        if (cancellation is null)
            return;

        cancellation.Cancel();
        client?.Dispose();

        try
        {
            Task.WaitAll(new[] { _receiveTask, _beaconTask }.OfType<Task>().ToArray(), TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        cancellation.Dispose();
        _receiveTask = null;
        _beaconTask = null;
    }

    private async Task ReceiveAsync(UdpClient client, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                UdpReceiveResult result = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);

                string json = Encoding.UTF8.GetString(result.Buffer);

                try
                {
                    using JsonDocument document = JsonDocument.Parse(json);
                    JsonElement root = document.RootElement;

                    string? type = root.TryGetProperty("type", out JsonElement typeElement)
                        ? typeElement.GetString()
                        : null;

                    if (type != "bridge.discover")
                        continue;

                    Interlocked.Increment(ref _requestsReceived);

                    byte[] reply = Encoding.UTF8.GetBytes(BuildPayload("bridge.reply"));
                    await client.SendAsync(reply, result.RemoteEndPoint.AddressFamily == AddressFamily.InterNetworkV6
                        ? new IPEndPoint(result.RemoteEndPoint.Address.MapToIPv4(), result.RemoteEndPoint.Port)
                        : result.RemoteEndPoint, cancellationToken).ConfigureAwait(false);

                    LogReceived?.Invoke($"iPhone buscando el PC desde {result.RemoteEndPoint.Address}; respondido.");
                }
                catch (JsonException)
                {
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            LogReceived?.Invoke($"Error en descubrimiento: {ex.Message}");
        }
    }

    private async Task BeaconAsync(UdpClient client, CancellationToken cancellationToken)
    {
        var targets = new List<IPAddress> { IPAddress.Broadcast };

        foreach (IPAddress broadcast in BroadcastAddresses())
        {
            if (!targets.Contains(broadcast))
                targets.Add(broadcast);
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                byte[] payload = Encoding.UTF8.GetBytes(BuildPayload("bridge.beacon"));

                foreach (IPAddress target in targets)
                {
                    try
                    {
                        await client.SendAsync(payload, new IPEndPoint(target, DiscoveryPort), cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (SocketException)
                    {
                        // Una interfaz sin ruta al broadcast no es un problema.
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private string BuildPayload(string type)
    {
        var message = new
        {
            protocol = "simtrack.bridge.v1",
            type,
            name = Environment.MachineName,
            trackingPort = IphoneReceiver.TrackingPort,
            controlPort = IphoneReceiver.ControlPort,
            telemetryPort = 4244,
            simhubPort = SimHubPort
        };

        return JsonSerializer.Serialize(message);
    }

    /// <summary>Direcciones IPv4 activas del PC.</summary>
    public static IReadOnlyList<IPAddress> LocalAddresses()
    {
        var addresses = new List<IPAddress>();

        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;

                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                foreach (UnicastIPAddressInformation unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;

                    if (IPAddress.IsLoopback(unicast.Address) || unicast.Address.ToString().StartsWith("169.254."))
                        continue;

                    addresses.Add(unicast.Address);
                }
            }
        }
        catch (NetworkInformationException)
        {
        }

        return addresses;
    }

    /// <summary>Broadcast dirigido de cada subred activa (p. ej. 192.168.1.255).</summary>
    public static IReadOnlyList<IPAddress> BroadcastAddresses()
    {
        var broadcasts = new List<IPAddress>();

        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;

                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                foreach (UnicastIPAddressInformation unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;

                    byte[] bytes = unicast.Address.GetAddressBytes();

                    if (bytes.Length != 4 || (bytes[0] == 169 && bytes[1] == 254))
                        continue;

                    var value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
                    uint mask = unicast.PrefixLength <= 0
                        ? 0u
                        : uint.MaxValue << (32 - unicast.PrefixLength);
                    uint broadcast = value | ~mask;

                    broadcasts.Add(new IPAddress(new[]
                    {
                        (byte)(broadcast >> 24),
                        (byte)(broadcast >> 16),
                        (byte)(broadcast >> 8),
                        (byte)broadcast
                    }));
                }
            }
        }
        catch (NetworkInformationException)
        {
        }

        return broadcasts;
    }

    public void Dispose()
    {
        Stop();
    }
}
