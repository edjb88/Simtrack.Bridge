using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Simtrack.Core.Tracking;

namespace Simtrack.App;

public partial class CalibrationWindow : Window
{
    private enum Kind
    {
        Baseline,
        Rest,
        YawLeft,
        YawRight,
        PitchUp,
        PitchDown,
        RollLeft,
        RollRight
    }

    private sealed class Step
    {
        public string Instruction = "";
        public string Detail = "";
        public double Seconds;
        public Kind Kind;
    }

    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x39, 0xD9, 0x8A));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x3A, 0x46, 0x58));
    private static readonly Brush Warm = new SolidColorBrush(Color.FromRgb(0xE0, 0xB5, 0x52));

    private readonly BridgeService _bridge;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Step[] _steps;
    private readonly List<Ellipse> _dots = new();
    private readonly List<double> _yaw = new();
    private readonly List<double> _pitch = new();
    private readonly List<double> _roll = new();

    private int _index;
    private double _elapsed;
    private DateTime _lastTick = DateTime.UtcNow;
    private bool _finished;

    private double _yawLeft;
    private double _yawRight;
    private double _pitchUp;
    private double _pitchDown;
    private double _rollLeft;
    private double _rollRight;

    public string? ResultMessage { get; private set; }

    public CalibrationWindow(BridgeService bridge)
    {
        InitializeComponent();

        _bridge = bridge;

        // Entre dirección y dirección hay una pausa corta ("aire") para volver
        // al centro sin inducir a error en el paso siguiente.
        _steps = new[]
        {
            new Step { Instruction = "MIRA AL FRENTE", Detail = "Cabeza recta y quieta. Esto fija el punto cero.", Seconds = 3.0, Kind = Kind.Baseline },

            new Step { Instruction = "VUELVE AL CENTRO", Detail = "Respira. Enseguida empezamos con el primer giro.", Seconds = 1.5, Kind = Kind.Rest },
            new Step { Instruction = "GIRA A LA IZQUIERDA", Detail = "Solo la cabeza, hombros quietos. Mantén hasta el pitido.", Seconds = 5.0, Kind = Kind.YawLeft },

            new Step { Instruction = "VUELVE AL CENTRO", Detail = "Suelta el giro y mira al frente.", Seconds = 1.5, Kind = Kind.Rest },
            new Step { Instruction = "GIRA A LA DERECHA", Detail = "Al otro lado, misma velocidad. Mantén hasta el pitido.", Seconds = 5.0, Kind = Kind.YawRight },

            new Step { Instruction = "VUELVE AL CENTRO", Detail = "Relaja el cuello.", Seconds = 1.5, Kind = Kind.Rest },
            new Step { Instruction = "MIRA ARRIBA", Detail = "Levanta la barbilla sin mover los hombros.", Seconds = 4.0, Kind = Kind.PitchUp },

            new Step { Instruction = "VUELVE AL CENTRO", Detail = "Baja la cabeza al centro.", Seconds = 1.5, Kind = Kind.Rest },
            new Step { Instruction = "MIRA ABAJO", Detail = "Baja la barbilla. Mantén hasta el pitido.", Seconds = 4.0, Kind = Kind.PitchDown },

            new Step { Instruction = "VUELVE AL CENTRO", Detail = "Cabeza recta otra vez.", Seconds = 1.5, Kind = Kind.Rest },
            new Step { Instruction = "INCLINA A LA IZQUIERDA", Detail = "Oreja hacia el hombro izquierdo.", Seconds = 4.0, Kind = Kind.RollLeft },

            new Step { Instruction = "VUELVE AL CENTRO", Detail = "Cabeza recta, último paso.", Seconds = 1.5, Kind = Kind.Rest },
            new Step { Instruction = "INCLINA A LA DERECHA", Detail = "Oreja hacia el hombro derecho. Mantén hasta el pitido.", Seconds = 4.0, Kind = Kind.RollRight }
        };

        CreateDots();

        _timer.Tick += Timer_Tick;
        _timer.Start();

        _lastTick = DateTime.UtcNow;
        BeginStep();
    }

    private void CreateDots()
    {
        for (var i = 0; i < _steps.Length; i++)
        {
            var dot = new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = Muted,
                Margin = new Thickness(0, 0, 7, 0)
            };

            _dots.Add(dot);
            DotsPanel.Children.Add(dot);
        }
    }

    private void BeginStep()
    {
        _elapsed = 0;
        _lastTick = DateTime.UtcNow;
        _yaw.Clear();
        _pitch.Clear();
        _roll.Clear();

        Step step = _steps[_index];
        bool isRest = step.Kind == Kind.Rest;

        InstructionText.Text = step.Instruction;
        InstructionText.Foreground = isRest ? Warm : Brushes.White;
        DetailText.Text = step.Detail;
        CountdownText.Text = Math.Ceiling(step.Seconds).ToString("0");
        StepProgress.Value = 0;
        StepCounterText.Text = $"Paso {_index + 1} de {_steps.Length}";

        for (var i = 0; i < _dots.Count; i++)
        {
            _dots[i].Fill = i < _index ? Accent : (i == _index ? Brushes.White : Muted);
        }

        if (step.Kind == Kind.Baseline)
            _bridge.Recenter("Calibración");

        // Pitido de arranque de paso: grave en las pausas, agudo en los gestos.
        Tone(isRest ? 660u : 880u, isRest ? 70u : 90u);
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (_finished)
            return;

        DateTime now = DateTime.UtcNow;
        double delta = (now - _lastTick).TotalSeconds;
        _lastTick = now;

        Step step = _steps[_index];
        _elapsed += delta;

        double remaining = Math.Max(0, step.Seconds - _elapsed);
        CountdownText.Text = Math.Ceiling(remaining).ToString("0");
        StepProgress.Value = Math.Min(1.0, _elapsed / step.Seconds);

        PoseSample pose = _bridge.CurrentPose;

        if (IsCapture(step.Kind))
        {
            _yaw.Add(pose.Yaw * 180 / Math.PI);
            _pitch.Add(pose.Pitch * 180 / Math.PI);
            _roll.Add(pose.Roll * 180 / Math.PI);
        }

        LiveValueText.Text = string.Format(
            "yaw {0,6:F1}°   pitch {1,6:F1}°   roll {2,6:F1}°",
            pose.Yaw * 180 / Math.PI,
            pose.Pitch * 180 / Math.PI,
            pose.Roll * 180 / Math.PI);

        if (_elapsed >= step.Seconds)
            CompleteStep();
    }

    private static bool IsCapture(Kind kind)
    {
        return kind is Kind.YawLeft or Kind.YawRight or Kind.PitchUp or Kind.PitchDown or Kind.RollLeft or Kind.RollRight;
    }

    private void CompleteStep()
    {
        Step step = _steps[_index];

        if (IsCapture(step.Kind))
            StoreCapture(step.Kind);

        _dots[_index].Fill = Accent;

        // Pitido de paso completado.
        Tone(step.Kind == Kind.Rest ? 800u : 1400u, step.Kind == Kind.Rest ? 70u : 130u);

        _index++;

        if (_index >= _steps.Length)
        {
            Finish();
            return;
        }

        BeginStep();
    }

    private void StoreCapture(Kind kind)
    {
        PoseSettings settings = _bridge.Settings;

        switch (kind)
        {
            case Kind.YawLeft:
                _yawLeft = Extreme(_yaw);
                break;
            case Kind.YawRight:
                _yawRight = Extreme(_yaw);
                break;
            case Kind.PitchUp:
                _pitchUp = Extreme(_pitch);
                break;
            case Kind.PitchDown:
                _pitchDown = Extreme(_pitch);
                break;
            case Kind.RollLeft:
                _rollLeft = Extreme(_roll);
                break;
            case Kind.RollRight:
                _rollRight = Extreme(_roll);
                break;
        }
    }

    private static double Extreme(List<double> values)
    {
        if (values.Count == 0)
            return 0;

        double result = 0;

        foreach (double value in values)
        {
            if (Math.Abs(value) > Math.Abs(result))
                result = value;
        }

        return result;
    }

    private void Finish()
    {
        _finished = true;
        _timer.Stop();

        PoseSettings settings = _bridge.Settings;

        bool invertYaw = Math.Abs(_yawLeft) < Math.Abs(_yawRight) || _yawLeft < 0;
        bool invertPitch = Math.Abs(_pitchUp) < Math.Abs(_pitchDown) || _pitchUp < 0;
        bool invertRoll = Math.Abs(_rollLeft) < Math.Abs(_rollRight) || _rollLeft < 0;

        bool tooLittleMotion =
            Math.Abs(_yawLeft) + Math.Abs(_yawRight) < 6 ||
            Math.Abs(_pitchUp) + Math.Abs(_pitchDown) < 6 ||
            Math.Abs(_rollLeft) + Math.Abs(_rollRight) < 6;

        InstructionText.Foreground = Brushes.White;

        if (tooLittleMotion)
        {
            InstructionText.Text = "CALIBRACIÓN NO VÁLIDA";
            DetailText.Text = "No se detectó movimiento suficiente en algún eje. Comprueba que la fuente de tracking (Kinect o iPhone) está enviando y repite.";
            CountdownText.Text = "!";
            ResultMessage = "Calibración no válida: movimiento insuficiente.";
            Tone(420, 350);
            return;
        }

        settings.InvertYaw = invertYaw;
        settings.InvertPitch = invertPitch;
        settings.InvertRoll = invertRoll;

        _bridge.ApplyCalibration();

        InstructionText.Text = "CALIBRACIÓN COMPLETADA";
        DetailText.Text = string.Format(
            "Izquierda {0:F0}° / derecha {1:F0}° · arriba {2:F0}° / abajo {3:F0}° · inclinación {4:F0}° / {5:F0}°.\nAjustes guardados en simtrack.settings.json.",
            _yawLeft, _yawRight, _pitchUp, _pitchDown, _rollLeft, _rollRight);
        CountdownText.Text = "OK";
        LiveValueText.Text = string.Format(
            "InvertYaw={0}  InvertPitch={1}  InvertRoll={2}",
            settings.InvertYaw, settings.InvertPitch, settings.InvertRoll);
        StepCounterText.Text = "Completado";
        StepProgress.Value = 1;

        ResultMessage = "Calibración completada y guardada.";

        Tone(1600, 150);
        Task.Delay(180).ContinueWith(_ => Tone(1900, 150));
        Task.Delay(380).ContinueWith(_ => Tone(2200, 220));

        Task.Delay(2600).ContinueWith(_ => Dispatcher.Invoke(Close));
    }

    private static void Tone(uint frequency, uint duration)
    {
        Task.Run(() =>
        {
            try
            {
                Beep(frequency, duration);
            }
            catch
            {
            }
        });
    }

    [DllImport("kernel32.dll")]
    private static extern bool Beep(uint frequency, uint duration);

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        ResultMessage = "Calibración cancelada.";
        Close();
    }
}
