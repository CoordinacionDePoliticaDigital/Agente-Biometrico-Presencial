using System.Drawing;

namespace AgenteBiometricoPresencial.UI;

public sealed class LogWindow : Form
{
    private static readonly Color Surface = Color.FromArgb(11, 18, 32);
    private static readonly Color ConsoleBackground = Color.FromArgb(5, 10, 22);
    private static readonly Color TextPrimary = Color.FromArgb(226, 232, 240);
    private static readonly Color TextMuted = Color.FromArgb(100, 116, 139);
    private static readonly Color InfoColor = Color.FromArgb(96, 165, 250);
    private static readonly Color SuccessColor = Color.FromArgb(52, 211, 153);
    private static readonly Color WarningColor = Color.FromArgb(251, 191, 36);
    private static readonly Color ErrorColor = Color.FromArgb(251, 113, 133);
    private static readonly Color HardwareColor = Color.FromArgb(192, 132, 252);
    private static readonly Color WebSocketColor = Color.FromArgb(34, 211, 238);
    private readonly RichTextBox _logBox;
    private readonly Label _statusLabel;
    private readonly Button _restartButton;
    private readonly Font _regularLogFont = new("Cascadia Mono", 9, FontStyle.Regular);
    private readonly Font _boldLogFont = new("Cascadia Mono", 9, FontStyle.Bold);
    private bool _allowClose;

    public event Action? HiddenByUser;
    public event Action? RestartRequested;

    public LogWindow()
    {
        Text = "Agente Biométrico Presencial — Logs en vivo";
        Icon = AgentBranding.LoadApplicationIcon();
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 420);
        Size = new Size(1050, 650);
        ShowInTaskbar = true;
        BackColor = Surface;

        _statusLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 42,
            Padding = new Padding(12, 12, 12, 8),
            ForeColor = Color.FromArgb(110, 231, 183),
            BackColor = Surface,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            Text = "Iniciando agente…"
        };

        _logBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = ConsoleBackground,
            ForeColor = TextPrimary,
            BorderStyle = BorderStyle.None,
            Font = _regularLogFont,
            WordWrap = false,
            DetectUrls = false,
            HideSelection = false
        };

        var legendPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 30,
            Padding = new Padding(10, 4, 10, 2),
            BackColor = Color.FromArgb(15, 25, 44),
            WrapContents = false
        };
        legendPanel.Controls.Add(CreateLegend("INFO", InfoColor));
        legendPanel.Controls.Add(CreateLegend("ÉXITO", SuccessColor));
        legendPanel.Controls.Add(CreateLegend("ADVERTENCIA / TIMEOUT", WarningColor));
        legendPanel.Controls.Add(CreateLegend("ERROR", ErrorColor));
        legendPanel.Controls.Add(CreateLegend("HARDWARE", HardwareColor));
        legendPanel.Controls.Add(CreateLegend("WEBSOCKET", WebSocketColor));

        var clearButton = new Button
        {
            Text = "Limpiar vista",
            Dock = DockStyle.Right,
            Width = 130,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(30, 41, 59)
        };
        clearButton.FlatAppearance.BorderColor = Color.FromArgb(71, 85, 105);
        clearButton.Click += (_, _) =>
        {
            AgentLog.Clear();
            _logBox.Clear();
        };

        var hideButton = new Button
        {
            Text = "Ocultar",
            Dock = DockStyle.Right,
            Width = 110,
            Margin = new Padding(0, 0, 8, 0),
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(30, 41, 59)
        };
        hideButton.FlatAppearance.BorderColor = Color.FromArgb(71, 85, 105);
        hideButton.Click += (_, _) => HideFromUser();

        _restartButton = new Button
        {
            Text = "Reiniciar agente",
            Dock = DockStyle.Left,
            Width = 150,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(37, 99, 235)
        };
        _restartButton.FlatAppearance.BorderColor = Color.FromArgb(59, 130, 246);
        _restartButton.Click += (_, _) => RestartRequested?.Invoke();

        var bottomPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            Padding = new Padding(8, 6, 8, 6),
            BackColor = Surface
        };
        bottomPanel.Controls.Add(clearButton);
        bottomPanel.Controls.Add(hideButton);
        bottomPanel.Controls.Add(_restartButton);

        Controls.Add(_logBox);
        Controls.Add(bottomPanel);
        Controls.Add(legendPanel);
        Controls.Add(_statusLabel);

        foreach (var line in AgentLog.Snapshot())
        {
            AppendLine(line);
        }

        AgentLog.LineAppended += OnLineAppended;
        FormClosing += OnFormClosing;
    }

    public void SetAgentStatus(string status, bool healthy)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => SetAgentStatus(status, healthy));
            return;
        }

        _statusLabel.Text = status;
        _statusLabel.ForeColor = healthy
            ? Color.FromArgb(110, 231, 183)
            : Color.FromArgb(251, 191, 36);
    }

    public void RefreshFromBuffer()
    {
        if (InvokeRequired)
        {
            BeginInvoke(RefreshFromBuffer);
            return;
        }

        _logBox.Clear();
        foreach (var line in AgentLog.Snapshot())
        {
            AppendLine(line);
        }
    }

    public void SetActionsEnabled(bool enabled)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => SetActionsEnabled(enabled));
            return;
        }

        _restartButton.Enabled = enabled;
    }

    public void ClosePermanently()
    {
        _allowClose = true;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            AgentLog.LineAppended -= OnLineAppended;
        }

        base.Dispose(disposing);
        if (disposing)
        {
            _regularLogFont.Dispose();
            _boldLogFont.Dispose();
        }
    }

    private void OnLineAppended(string line)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        BeginInvoke(() => AppendLine(line));
    }

    private void AppendLine(string line)
    {
        var timestampLength = line.Length >= 25 && line[4] == '-' ? 25 : 0;
        if (timestampLength > 0)
        {
            AppendSegment(line[..timestampLength], TextMuted, FontStyle.Regular);
        }

        var body = line[timestampLength..];
        var color = ResolveLineColor(body);
        var tagEnd = body.StartsWith('[') ? body.IndexOf(']') : -1;
        if (tagEnd >= 0)
        {
            AppendSegment(body[..(tagEnd + 1)], ResolveTagColor(body, color), FontStyle.Bold);
            AppendSegment(body[(tagEnd + 1)..], color, FontStyle.Regular);
        }
        else
        {
            AppendSegment(body, color, FontStyle.Regular);
        }

        AppendSegment(Environment.NewLine, color, FontStyle.Regular);
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.ScrollToCaret();
    }

    private void AppendSegment(string text, Color color, FontStyle style)
    {
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.SelectionLength = 0;
        _logBox.SelectionColor = color;
        _logBox.SelectionFont = style == FontStyle.Bold ? _boldLogFont : _regularLogFont;
        _logBox.AppendText(text);
    }

    private static Color ResolveLineColor(string body)
    {
        var normalized = body.ToUpperInvariant();
        if (normalized.Contains("FATAL") || normalized.Contains("ERROR") ||
            normalized.Contains("FAILED") || normalized.Contains("DISCONNECTED") ||
            normalized.Contains("DESCONECT"))
        {
            return ErrorColor;
        }

        if (normalized.Contains("WARNING") || normalized.Contains("WARN") ||
            normalized.Contains("TIMEOUT") || normalized.Contains("EXPIR") ||
            normalized.Contains("CANCEL") || normalized.Contains("ABORT"))
        {
            return WarningColor;
        }

        if (normalized.Contains("SUCCESS") || normalized.Contains("READY") ||
            normalized.Contains("COMPLETE") || normalized.Contains("COMPLET") ||
            normalized.Contains("CONNECTED") || normalized.Contains("CONECTADO") ||
            normalized.Contains("LISTO"))
        {
            return SuccessColor;
        }

        if (normalized.Contains("[WS") || normalized.Contains("WEBSOCKET"))
        {
            return WebSocketColor;
        }

        if (normalized.Contains("REALSCAN") || normalized.Contains("REALPASS") ||
            normalized.Contains("[HW"))
        {
            return HardwareColor;
        }

        return TextPrimary;
    }

    private static Color ResolveTagColor(string body, Color fallback)
    {
        var normalized = body.ToUpperInvariant();
        if (normalized.StartsWith("[WS")) return WebSocketColor;
        if (normalized.StartsWith("[HW") || normalized.StartsWith("[REAL")) return HardwareColor;
        if (normalized.Contains("ERROR") || normalized.Contains("FATAL")) return ErrorColor;
        if (normalized.Contains("WARN") || normalized.Contains("TIMEOUT")) return WarningColor;
        if (normalized.Contains("SUCCESS") || normalized.Contains("READY")) return SuccessColor;
        if (normalized.Contains("INFO")) return InfoColor;
        return fallback;
    }

    private static Label CreateLegend(string text, Color color) => new()
    {
        AutoSize = true,
        Text = $"● {text}",
        ForeColor = color,
        Font = new Font("Segoe UI", 8, FontStyle.Bold),
        Margin = new Padding(4, 2, 14, 0)
    };

    private void OnFormClosing(object? sender, FormClosingEventArgs eventArgs)
    {
        if (_allowClose)
        {
            return;
        }

        eventArgs.Cancel = true;
        HideFromUser();
    }

    private void HideFromUser()
    {
        Hide();
        HiddenByUser?.Invoke();
    }
}
