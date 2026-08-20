using System.Globalization;
using System.Text;

namespace BetterTranslator.Updates.Logging;

public interface IUpdateLog
{
    void Write(string message);

    void Write(string message, Exception error);
}

public sealed class NullUpdateLog : IUpdateLog
{
    public static NullUpdateLog Instance { get; } = new();

    public void Write(string message)
    {
    }

    public void Write(string message, Exception error)
    {
    }
}

public sealed class RollingFileLog : IUpdateLog
{
    private const long MaxBytes = 1024 * 1024;
    private const int Generations = 5;

    private readonly object _gate = new();
    private readonly string _folder;
    private readonly string _file;

    public RollingFileLog(string folder, string name = "updater.log")
    {
        _folder = folder;
        _file = Path.Combine(folder, name);
    }

    public string File => _file;

    public void Write(string message) => Append(message);

    public void Write(string message, Exception error) =>
        Append($"{message}: {error.GetType().Name}: {error.Message}");

    private void Append(string message)
    {
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z {message}{Environment.NewLine}");

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_folder);
                Roll();
                System.IO.File.AppendAllText(_file, line, Encoding.UTF8);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void Roll()
    {
        var current = new FileInfo(_file);

        if (!current.Exists || current.Length < MaxBytes)
        {
            return;
        }

        var oldest = _file + "." + Generations.ToString(CultureInfo.InvariantCulture);

        if (System.IO.File.Exists(oldest))
        {
            System.IO.File.Delete(oldest);
        }

        for (var generation = Generations - 1; generation >= 1; generation--)
        {
            var from = _file + "." + generation.ToString(CultureInfo.InvariantCulture);
            var to = _file + "." + (generation + 1).ToString(CultureInfo.InvariantCulture);

            if (System.IO.File.Exists(from))
            {
                System.IO.File.Move(from, to, overwrite: true);
            }
        }

        System.IO.File.Move(_file, _file + ".1", overwrite: true);
    }
}
