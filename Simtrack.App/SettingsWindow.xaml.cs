using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Simtrack.App;

namespace Simtrack.App;

public partial class SettingsWindow : Window
{
    private readonly BridgeService _bridge;

    public SettingsWindow(BridgeService bridge)
    {
        InitializeComponent();
        _bridge = bridge;

        StatusText.Text = $"Fuente activa: {DescribeSource()}";

        Closed += (_, _) => { };
    }

    private string DescribeSource()
    {
        return _bridge.ActiveSource switch
        {
            "Kinect+iPhone" => "Kinect (posición) + iPhone (rotación)",
            "Kinect" => "Kinect v1 (esqueleto)",
            "iPhone" => "iPhone TrueDepth",
            _ => "sin datos de tracking"
        };
    }

    private void CalibrateButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_bridge.IsRunning)
        {
            StatusText.Text = "El motor está detenido: pulsa INICIAR en la ventana principal.";
            return;
        }

        if (!_bridge.IphoneFresh && !_bridge.KinectFresh)
        {
            StatusText.Text = "No hay datos de tracking. Colócate frente al sensor (o abre la app del iPhone) y vuelve a intentarlo.";
            return;
        }

        var window = new CalibrationWindow(_bridge) { Owner = this };
        window.ShowDialog();

        StatusText.Text = window.ResultMessage ?? "Calibración cerrada.";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
