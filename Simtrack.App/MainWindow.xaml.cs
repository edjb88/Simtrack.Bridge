using System.Collections.ObjectModel;
using System.Net;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Simtrack.Core.Tracking;

namespace Simtrack.App;

public partial class MainWindow : Window
{
    private static readonly SolidColorBrush GreenBrush = new(Color.FromRgb(0x39, 0xD9, 0x8A));
    private static readonly SolidColorBrush AmberBrush = new(Color.FromRgb(0xE0, 0xB5, 0x52));
    private static readonly SolidColorBrush RedBrush = new(Color.FromRgb(0xE0, 0x52, 0x52));

    private readonly BridgeService _bridge = new();
    private readonly ObservableCollection<string> _log = new();
    private readonly DispatcherTimer _uiTimer = new();

    private const int MaxLogLines = 400;

    public MainWindow()
    {
        InitializeComponent();

        LogList.ItemsSource = _log;

        _bridge.LogReceived += OnBridgeLog;
        _bridge.CommandReceived += OnBridgeCommand;

        _uiTimer.Interval = TimeSpan.FromMilliseconds(120);
        _uiTimer.Tick += (_, _) => RefreshUi();
        _uiTimer.Start();

        Loaded += OnLoaded;
        Closing += (_, _) =>
        {
            _uiTimer.Stop();
            _bridge.Dispose();
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Arranca solo: si algo falla se queda el botón en INICIAR y el log
        // explica el motivo (puerto ocupado, etc.).
        try
        {
            _bridge.Start();
            ToggleButton.Content = "DETENER";
        }
        catch (Exception ex)
        {
            ToggleButton.Content = "INICIAR";
            Log($"No se pudo arrancar automáticamente: {ex.Message}");
        }
    }

    private void ToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_bridge.IsRunning)
        {
            _bridge.Stop();
            ToggleButton.Content = "INICIAR";
        }
        else
        {
            try
            {
                _bridge.Start();
                ToggleButton.Content = "DETENER";
            }
            catch (Exception ex)
            {
                Log($"No se pudo arrancar: {ex.Message}");
            }
        }
    }

    private void RecenterButton_Click(object sender, RoutedEventArgs e)
    {
        _bridge.Recenter();
    }

    private void OnBridgeLog(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnBridgeLog(message));
            return;
        }

        _log.Add(message);

        while (_log.Count > MaxLogLines)
            _log.RemoveAt(0);

        if (_log.Count > 0)
            LogList.ScrollIntoView(_log[^1]);
    }

    private void OnBridgeCommand(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnBridgeCommand(message));
            return;
        }

        CommandStatus.Text = message;
        CommandStatus.Foreground = GreenBrush;
    }

    private void Log(string message)
    {
        OnBridgeLog($"{DateTime.Now:HH:mm:ss}  {message}");
    }

    private void RefreshUi()
    {
        PoseSample pose = _bridge.CurrentPose;
        bool iphoneFresh = _bridge.IphoneFresh;
        bool kinectFresh = _bridge.KinectFresh;
        bool fresh = iphoneFresh || kinectFresh;
        bool running = _bridge.IsRunning;

        ValueX.Text = $"{pose.X,6:F2} cm";
        ValueY.Text = $"{pose.Y,6:F2} cm";
        ValueZ.Text = $"{pose.Z,6:F2} cm";
        ValueYaw.Text = $"{pose.Yaw * 180 / Math.PI,6:F2}°";
        ValuePitch.Text = $"{pose.Pitch * 180 / Math.PI,6:F2}°";
        ValueRoll.Text = $"{pose.Roll * 180 / Math.PI,6:F2}°";

        if (!running)
        {
            ConnectionIndicator.Fill = RedBrush;
            ConnectionText.Text = "DETENIDO";
        }
        else if (fresh)
        {
            ConnectionIndicator.Fill = GreenBrush;
            if (kinectFresh && iphoneFresh) ConnectionText.Text = "KINECT + IPHONE";
            else if (kinectFresh) ConnectionText.Text = "KINECT CONECTADO";
            else ConnectionText.Text = "IPHONE CONECTADO";
        }
        else
        {
            ConnectionIndicator.Fill = AmberBrush;
            ConnectionText.Text = "ESPERANDO SENSOR";
        }

        IphoneStatus.Text = iphoneFresh ? "Recibiendo pose" : "Sin conexión";
        IphoneAddress.Text = $"IP: {_bridge.RemoteAddress?.ToString() ?? "desconocida"}";

        HeadphonesStatus.Text = "No integrado todavía";
        HeadphonesDetail.Text = "Falta Sony Head Tracker (fase 2)";

        PacketCount.Text = _bridge.PacketsReceived.ToString("N0");

        bool waitingTooLong = running &&
                              _bridge.PacketsReceived == 0 &&
                              _bridge.KinectPackets == 0 &&
                              DateTime.UtcNow - _bridge.StartedAt > TimeSpan.FromSeconds(8);

        if (waitingTooLong)
        {
            PacketDetail.Text = "Sin paquetes: revisa firewall (tools\\Allow-Firewall.ps1) e IP";
            PacketDetail.Foreground = AmberBrush;
        }
        else
        {
            PacketDetail.Text = $"{_bridge.PacketsRejected:N0} descartados · iPhone {_bridge.PacketsReceived:N0} · Kinect {_bridge.KinectPackets:N0}";
            PacketDetail.Foreground = (Brush)FindResource("MutedBrush");
        }

        FusionStatus.Text = _bridge.ActiveSource switch
        {
            "Kinect" => "Kinect v1 (esqueleto) · prioridad",
            "iPhone" => "iPhone TrueDepth",
            _ => "Sin fuente de tracking"
        };
        FusionStatus.Foreground = _bridge.ActiveSource == "ninguna" ? AmberBrush : GreenBrush;

        if (_bridge.FreeTrackOpen)
        {
            OutputStatus.Text = "Escribiendo en FT_SharedMem";
            OutputStatus.Foreground = fresh ? GreenBrush : AmberBrush;
            WriteRateText.Text = $"{_bridge.WriteRateHz:F0} Hz · 108 bytes · DataID 1";
        }
        else
        {
            OutputStatus.Text = "FT_SharedMem cerrada";
            OutputStatus.Foreground = RedBrush;
            WriteRateText.Text = "0 Hz";
        }
    }
}
