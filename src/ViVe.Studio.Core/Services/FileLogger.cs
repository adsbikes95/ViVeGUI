// SPDX-License-Identifier: GPL-3.0-or-later
namespace ViVe.Studio.Services;

/// <summary>Appends log lines to a daily file when logging is enabled in settings. Never throws.</summary>
public sealed class FileLogger : IAppLogger
{
    private readonly IAppPaths _paths;
    private readonly Func<bool> _enabled;
    private readonly object _lock = new();

    public FileLogger(IAppPaths paths, Func<bool> enabled)
    {
        _paths = paths;
        _enabled = enabled;
    }

    public void Log(LogLevel level, string message)
    {
        if (!_enabled()) return;
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(_paths.LogDirectory);
                var file = Path.Combine(_paths.LogDirectory, $"studio-{DateTime.UtcNow:yyyyMMdd}.log");
                File.AppendAllText(file, $"{DateTimeOffset.Now:O} [{level}] {message.Replace(Environment.NewLine, " ")}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
