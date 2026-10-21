using rMic.Core;
using Microsoft.Extensions.Hosting;

namespace rMic.Service;

public sealed class rMicWorker : BackgroundService
{
    private readonly Logger _log;
    private readonly ServiceConfig _config;
    private readonly MicSwitch _switch;

    public rMicWorker(Logger log, ServiceConfig config, MicSwitch micSwitch)
    {
        _log = log;
        _config = config;
        _switch = micSwitch;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.Info("rMic service started.");

        try
        {
            // A reboot must not quietly hand the microphone back.
            await _switch.ReassertAsync("service start", stoppingToken).ConfigureAwait(false);

            var server = new ControlServer(_log, _switch.HandleAsync);
            var serverTask = server.RunAsync(stoppingToken);

            var interval = TimeSpan.FromSeconds(_config.EnforceIntervalSeconds);
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);

                try
                {
                    await _switch.EnforceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.Error("Enforcement pass failed", ex);
                }
            }

            await serverTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _log.Error("Worker stopped unexpectedly", ex);
            throw;
        }
        finally
        {
            // Devices are deliberately left as they are: the kill switch is meant to survive
            // a service restart or an upgrade. Uninstall re-enables them explicitly.
            _log.Info("rMic service stopping.");
        }
    }
}
