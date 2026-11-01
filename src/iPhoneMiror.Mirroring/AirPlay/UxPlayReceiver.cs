using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace iPhoneMiror.Mirroring.AirPlay;

public enum AirPlayReceiverEventKind { Started, ClientConnected, ClientDisconnected, Exited, Log }

public sealed record AirPlayReceiverEvent(AirPlayReceiverEventKind Kind, string Message, string? ClientName = null, string? ClientModel = null);

/// <summary>
/// Runs UxPlay (https://github.com/FDH2/UxPlay, GPL-3.0) as a separate process to receive AirPlay screen mirroring.
/// UxPlay is not bundled or linked: the user installs it and points iPhoneMiror at uxplay.exe. UxPlay renders the
/// video in its own GStreamer window; iPhoneMiror manages its lifecycle and reports connection status from its log.
/// </summary>
public sealed partial class UxPlayReceiver : IAsyncDisposable
{
    private readonly ILogger _logger;
    private Process? _process;

    public UxPlayReceiver(ILogger logger)
    {
        _logger = logger;
    }

    public event EventHandler<AirPlayReceiverEvent>? EventRaised;

    public bool IsRunning => _process is { HasExited: false };

    public static IReadOnlyList<string> BuildArguments(string receiverName) =>
        ["-n", receiverName, "-nh"];

    public static string? FindExecutable(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath)) return configuredPath;
        var candidates = new List<string>();
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(Path.Combine(dir, "uxplay.exe"));
            candidates.Add(Path.Combine(dir, "uxplay"));
        }
        candidates.Add(@"C:\msys64\ucrt64\bin\uxplay.exe");
        candidates.Add(@"C:\msys64\mingw64\bin\uxplay.exe");
        return candidates.FirstOrDefault(File.Exists);
    }

    public Task StartAsync(string executable, string receiverName)
    {
        if (IsRunning) return Task.CompletedTask;
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in BuildArguments(receiverName)) psi.ArgumentList.Add(a);
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) OnLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) OnLine(e.Data); };
        p.Exited += (_, _) => Raise(new AirPlayReceiverEvent(AirPlayReceiverEventKind.Exited, $"UxPlay exited with code {SafeExitCode(p)}."));
        if (!p.Start()) throw new InvalidOperationException("UxPlay could not be started.");
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _process = p;
        Raise(new AirPlayReceiverEvent(AirPlayReceiverEventKind.Started,
            $"AirPlay receiver \"{receiverName}\" is running. On the iPhone open Control Center > Screen Mirroring and choose it."));
        return Task.CompletedTask;
    }

    private static string SafeExitCode(Process p)
    {
        try { return p.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        catch (InvalidOperationException) { return "?"; }
    }

    // UxPlay log line (uxplay.cpp): "connection request from %s (%s) with deviceID = %s"
    [GeneratedRegex(@"connection request from (?<name>.+?) \((?<model>[^)]*)\)", RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionRequestRegex();

    internal void OnLine(string line)
    {
        _logger.LogDebug("uxplay: {Line}", line);
        var m = ConnectionRequestRegex().Match(line);
        if (m.Success)
        {
            Raise(new AirPlayReceiverEvent(AirPlayReceiverEventKind.ClientConnected, line, m.Groups["name"].Value, m.Groups["model"].Value));
            return;
        }
        if (line.Contains("lost connection with client", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Open connections: 0", StringComparison.Ordinal))
        {
            Raise(new AirPlayReceiverEvent(AirPlayReceiverEventKind.ClientDisconnected, line));
            return;
        }
        Raise(new AirPlayReceiverEvent(AirPlayReceiverEventKind.Log, line));
    }

    private void Raise(AirPlayReceiverEvent e)
    {
        try { EventRaised?.Invoke(this, e); }
        catch (Exception ex) { _logger.LogError(ex, "AirPlay event handler threw."); }
    }

    public async Task StopAsync()
    {
        var p = _process;
        _process = null;
        if (p is null) return;
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
                await p.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            _logger.LogDebug(ex, "Stopping UxPlay.");
        }
        finally
        {
            p.Dispose();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
