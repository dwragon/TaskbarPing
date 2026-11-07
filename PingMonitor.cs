using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace TaskbarPing;

internal sealed class ServiceState
{
    public const long Unknown = -2;
    public const long Lost = -1;

    private long _rtt = Unknown;
    private int _fails;

    public string Name { get; }
    public string Host { get; }

    public ServiceState(string name, string host)
    {
        Name = name;
        Host = host;
    }

    /// <summary>-2 - ещё нет данных, -1 - связи нет, иначе задержка в мс.</summary>
    public long Rtt => Interlocked.Read(ref _rtt);

    public void Report(long? rtt)
    {
        if (rtt.HasValue)
        {
            _fails = 0;
            Interlocked.Exchange(ref _rtt, rtt.Value);
            return;
        }

        // два потери подряд = связь пропала (чтобы один дропнутый пакет не мигал красным)
        _fails++;
        if (_fails >= 2 || Interlocked.Read(ref _rtt) == Unknown)
            Interlocked.Exchange(ref _rtt, Lost);
    }
}

internal sealed class PingMonitor : IDisposable
{
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();

    public IReadOnlyList<ServiceState> States { get; }

    public PingMonitor(AppConfig cfg)
    {
        var list = new List<ServiceState>();
        foreach (ServiceEntry e in cfg.Services)
            list.Add(new ServiceState(e.Name, e.Host));
        States = list;

        CancellationToken ct = _cts.Token;
        int interval = cfg.IntervalMs;
        int timeout = cfg.TimeoutMs;
        foreach (ServiceState s in list)
        {
            ServiceState state = s;
            _ = Task.Run(() => LoopAsync(state, interval, timeout, ct));
        }
    }

    private static async Task LoopAsync(ServiceState state, int intervalMs, int timeoutMs, CancellationToken ct)
    {
        using var ping = new Ping();
        byte[] buffer = new byte[32];
        var options = new PingOptions(64, true);

        while (!ct.IsCancellationRequested)
        {
            long? rtt = null;
            try
            {
                PingReply reply = await ping.SendPingAsync(state.Host, timeoutMs, buffer, options).ConfigureAwait(false);
                if (reply.Status == IPStatus.Success)
                    rtt = reply.RoundtripTime;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // DNS не резолвится, сеть отключена и т.п. - считаем потерей
            }

            state.Report(rtt);

            try
            {
                await Task.Delay(intervalMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
