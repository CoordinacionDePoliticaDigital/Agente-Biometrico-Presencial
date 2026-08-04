using System.Text;

namespace AgenteBiometricoPresencial.UI;

public static class AgentLog
{
    private const int MaximumLines = 2500;
    private static readonly object Sync = new();
    private static readonly Queue<string> Lines = new();
    private static StreamWriter? _fileWriter;
    private static DateOnly _fileDate;
    private static bool _initialized;

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GobiernoChihuahua",
        "AgenteBiometrico",
        "logs");

    public static event Action<string>? LineAppended;

    public static void Initialize()
    {
        lock (Sync)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            var writer = new LiveLogTextWriter(Append);
            Console.SetOut(writer);
            Console.SetError(writer);
        }

        Append($"[INFO] Logs persistentes: {LogDirectory}");
    }

    public static IReadOnlyList<string> Snapshot()
    {
        lock (Sync)
        {
            return Lines.ToArray();
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            Lines.Clear();
        }
    }

    public static void Append(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message.TrimEnd()}";
        lock (Sync)
        {
            Lines.Enqueue(line);
            while (Lines.Count > MaximumLines)
            {
                Lines.Dequeue();
            }

            try
            {
                EnsureFileWriter();
                _fileWriter?.WriteLine(line);
            }
            catch
            {
                // El monitoreo en memoria debe continuar aunque falle el disco.
            }
        }

        LineAppended?.Invoke(line);
    }

    private static void EnsureFileWriter()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_fileWriter is not null && _fileDate == today)
        {
            return;
        }

        _fileWriter?.Dispose();
        Directory.CreateDirectory(LogDirectory);
        var path = Path.Combine(LogDirectory, $"agent-{today:yyyyMMdd}.log");
        _fileWriter = new StreamWriter(path, append: true, Encoding.UTF8) { AutoFlush = true };
        _fileDate = today;
    }

    private sealed class LiveLogTextWriter(Action<string> append) : TextWriter
    {
        private readonly object _sync = new();
        private readonly StringBuilder _pending = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_sync)
            {
                if (value == '\n')
                {
                    FlushPending();
                }
                else if (value != '\r')
                {
                    _pending.Append(value);
                }
            }
        }

        public override void Write(string? value)
        {
            if (value is null)
            {
                return;
            }

            foreach (var character in value)
            {
                Write(character);
            }
        }

        public override void WriteLine(string? value)
        {
            lock (_sync)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    _pending.Append(value);
                }

                FlushPending();
            }
        }

        private void FlushPending()
        {
            if (_pending.Length == 0)
            {
                return;
            }

            var line = _pending.ToString();
            _pending.Clear();
            append(line);
        }
    }
}
