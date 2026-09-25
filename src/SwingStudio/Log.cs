using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;

namespace SwingStudio;

public static class Log
{
    private const int DaysToKeep = 14;
    private static readonly BlockingCollection<string> Pending = new(4096);
    private static readonly object FileGate = new();
    private static Thread? _writer;
    private static Exception? _lastFatal;

    public static string CurrentFile => Path.Combine(AppPaths.LogDirectory, FileNameFor(DateTime.Now));

    public static void Start()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            DeleteOldFiles();
        }
        catch (Exception)
        {
        }

        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "Log" };
        _writer.Start();
    }

    public static void Stop()
    {
        Pending.CompleteAdding();
        _writer?.Join(TimeSpan.FromSeconds(2));
    }

    public static void Info(string message) => Enqueue(Format("INFO", message, null));

    public static void Warn(string message, Exception? error = null) => Enqueue(Format("WARN", message, error));

    public static void Error(string message, Exception? error = null) => Enqueue(Format("ERROR", message, error));

    public static void Fatal(string message, Exception error)
    {
        if (ReferenceEquals(Interlocked.Exchange(ref _lastFatal, error), error))
        {
            return;
        }

        Stop();
        Append(Format("FATAL", message, error));
    }

    private static void Enqueue(string line)
    {
        try
        {
            if (Pending.TryAdd(line))
            {
                return;
            }
        }
        catch (InvalidOperationException)
        {
        }

        Append(line);
    }

    private static string Format(string level, string message, Exception? error)
    {
        var thread = Thread.CurrentThread;
        var threadName = string.IsNullOrEmpty(thread.Name) ? thread.ManagedThreadId.ToString(CultureInfo.InvariantCulture) : thread.Name;
        var line = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(' ')
            .Append(level.PadRight(5))
            .Append(" [")
            .Append(threadName)
            .Append("] ")
            .Append(message);
        if (error is not null)
        {
            line.AppendLine().Append(error);
        }

        return line.ToString();
    }

    private static void WriteLoop()
    {
        foreach (var line in Pending.GetConsumingEnumerable())
        {
            Append(line);
        }
    }

    private static void Append(string line)
    {
        lock (FileGate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.LogDirectory);
                using var stream = new FileStream(CurrentFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, Encoding.UTF8);
                writer.WriteLine(line);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void DeleteOldFiles()
    {
        var cutoff = DateTime.Now.Date.AddDays(-DaysToKeep);
        foreach (var file in Directory.EnumerateFiles(AppPaths.LogDirectory, "swingstudio-*.log"))
        {
            if (File.GetLastWriteTime(file) < cutoff)
            {
                File.Delete(file);
            }
        }
    }

    private static string FileNameFor(DateTime day) => "swingstudio-" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log";
}
