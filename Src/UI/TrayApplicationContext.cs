using System.Drawing;
using AgenteBiometricoPresencial.Contracts;
using AgenteBiometricoPresencial.Server;

namespace AgenteBiometricoPresencial.UI;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly SynchronizationContext _uiContext;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _brandIcon;
    private readonly ToolStripMenuItem _toggleLogsItem;
    private readonly ToolStripMenuItem _restartItem;
    private readonly ToolStripMenuItem _exitItem;
    private readonly LogWindow _logWindow;
    private BiometricWebSocketServer? _server;
    private bool _exiting;

    public TrayApplicationContext(bool showLogsOnStart = false)
    {
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _brandIcon = AgentBranding.LoadApplicationIcon();
        _logWindow = new LogWindow();

        _toggleLogsItem = new ToolStripMenuItem("Mostrar logs en vivo", null, (_, _) => ToggleLogs());
        _logWindow.HiddenByUser += () => _toggleLogsItem.Text = "Mostrar logs en vivo";
        _logWindow.RestartRequested += () => _ = RestartAsync();
        _restartItem = new ToolStripMenuItem("Reiniciar agente y dispositivos", null, async (_, _) => await RestartAsync());
        _exitItem = new ToolStripMenuItem("Apagar agente", null, async (_, _) => await ExitAsync());

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Agente Biométrico Presencial") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_toggleLogsItem);
        menu.Items.Add(_restartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = _brandIcon,
            Text = "Agente biométrico: iniciando",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => ToggleLogs();

        AgentLog.Append("Agente iniciado en modo bandeja de sistema.");
        _ = RestartAsync(initialStart: true);
        if (showLogsOnStart)
        {
            ShowLogs();
        }
    }

    private async Task RestartAsync(bool initialStart = false)
    {
        if (_exiting || !await _lifecycleLock.WaitAsync(0))
        {
            return;
        }

        SetControlsEnabled(false);
        SetStatus(initialStart ? "Iniciando agente y dispositivos…" : "Reiniciando agente y dispositivos…", false);
        try
        {
            await Task.Run(() =>
            {
                _server?.Dispose();
                _server = null;
                AgentLog.Append(initialStart
                    ? "Inicializando servicio WebSocket y dispositivos Xperix…"
                    : "Servicio detenido. Reinicializando dispositivos Xperix…");

                var replacement = new BiometricWebSocketServer();
                try
                {
                    replacement.HardwareStatusChanged += OnHardwareStatusChanged;
                    replacement.Start();
                    _server = replacement;
                }
                catch
                {
                    replacement.Dispose();
                    throw;
                }
            });

            SetStatus(
                _server?.AllDevicesConnected == true
                    ? "Agente activo · RealScan y RealPass conectados"
                    : "Agente activo · falta hardware biométrico",
                _server?.AllDevicesConnected == true);
            AgentLog.Append("Agente y dispositivos listos.");
            if (!initialStart)
            {
                ShowBalloon("Agente reiniciado", "RealScan, RealPass y el canal local fueron reinicializados.", ToolTipIcon.Info);
            }
        }
        catch (Exception exception)
        {
            AgentLog.Append($"[FATAL] No se pudo iniciar el agente: {exception}");
            SetStatus("Agente con error · abre los logs para revisar", false);
            ShowBalloon("Error del agente biométrico", exception.Message, ToolTipIcon.Error);
            ShowLogs();
        }
        finally
        {
            SetControlsEnabled(true);
            _lifecycleLock.Release();
        }
    }

    private void OnHardwareStatusChanged(DeviceState realScan, DeviceState realPass)
    {
        var healthy = realScan.Connected && realPass.Connected;
        var status = healthy
            ? "Agente activo · RealScan y RealPass conectados"
            : $"Hardware ausente · RealScan {(realScan.Connected ? "✓" : "✗")} · RealPass {(realPass.Connected ? "✓" : "✗")}";
        SetStatus(status, healthy);
        if (!healthy)
        {
            ShowBalloon(
                "Hardware biométrico desconectado",
                $"RealScan {(realScan.Connected ? "conectado" : "desconectado")}; RealPass {(realPass.Connected ? "conectado" : "desconectado")}.",
                ToolTipIcon.Warning);
        }
        else
        {
            ShowBalloon(
                "Hardware biométrico conectado",
                "RealScan y RealPass están disponibles nuevamente.",
                ToolTipIcon.Info);
        }
    }

    private async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        SetControlsEnabled(false);
        SetStatus("Apagando agente…", false);
        AgentLog.Append("Apagado solicitado desde la bandeja del sistema.");

        await _lifecycleLock.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                _server?.Dispose();
                _server = null;
            });
        }
        finally
        {
            _lifecycleLock.Release();
        }

        _notifyIcon.Visible = false;
        _logWindow.ClosePermanently();
        ExitThread();
    }

    private void ToggleLogs()
    {
        if (_logWindow.Visible)
        {
            _logWindow.Hide();
            _toggleLogsItem.Text = "Mostrar logs en vivo";
        }
        else
        {
            ShowLogs();
        }
    }

    private void ShowLogs()
    {
        _logWindow.RefreshFromBuffer();
        _logWindow.Show();
        _logWindow.WindowState = FormWindowState.Normal;
        _logWindow.Activate();
        _toggleLogsItem.Text = "Ocultar logs en vivo";
    }

    private void SetStatus(string status, bool healthy)
    {
        _uiContext.Post(_ =>
        {
            if (_exiting && healthy)
            {
                return;
            }

            _notifyIcon.Text = status.Length <= 63 ? status : status[..63];
            _notifyIcon.Icon = healthy ? _brandIcon : SystemIcons.Warning;
            _logWindow.SetAgentStatus(status, healthy);
        }, null);
    }

    private void SetControlsEnabled(bool enabled)
    {
        _uiContext.Post(_ =>
        {
            _restartItem.Enabled = enabled && !_exiting;
            _exitItem.Enabled = enabled || !_exiting;
            _logWindow.SetActionsEnabled(enabled && !_exiting);
        }, null);
    }

    private void ShowBalloon(string title, string message, ToolTipIcon icon)
    {
        _uiContext.Post(_ =>
        {
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = message;
            _notifyIcon.BalloonTipIcon = icon;
            _notifyIcon.ShowBalloonTip(5000);
        }, null);
    }

    protected override void ExitThreadCore()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _brandIcon.Dispose();
        _lifecycleLock.Dispose();
        base.ExitThreadCore();
    }
}
