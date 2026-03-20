using System;
using BepInEx.Logging;

namespace LiftoffPhotonEventLogger.Features.MultiplayerTrackControl;

internal sealed class MultiplayerTrackControlLog
{
    private readonly ManualLogSource _logger;
    private readonly Action<string> _stateLog;

    public MultiplayerTrackControlLog(ManualLogSource logger, Action<string> stateLog)
    {
        _logger = logger;
        _stateLog = stateLog;
    }

    public void Info(string category, string message)
    {
        _stateLog(Format(category, message));
    }

    public void Warn(string category, string message)
    {
        var line = Format(category, message);
        _logger.LogWarning(line);
        _stateLog(line);
    }

    public void Error(string category, string message, Exception? exception = null)
    {
        var line = Format(category, message);
        _logger.LogError(exception == null ? line : $"{line}{Environment.NewLine}{exception}");
        _stateLog(line);
        if (exception != null)
            _stateLog(Format(category, exception.ToString()));
    }

    private static string Format(string category, string message)
    {
        return $"[MTC][{category}] {message}";
    }
}
