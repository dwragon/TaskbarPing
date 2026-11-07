using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace TaskbarPing;

/// <summary>Один замер: время (Environment.TickCount64, мс) и задержка (-1 = потеря).</summary>
internal readonly struct PingSample
{
    public readonly long T;
    public readonly int Rtt;

    public PingSample(long t, int rtt)
    {
        T = t;
        Rtt = rtt;
    }
}

internal sealed class ServiceState
{
    public const long Unknown = -2;
    public const long Lost = -1;

    private readonly object _lock = new object();
    private readonly PingSample[] _ring;
    private int _head;
    private int _count;
    private string? _address;

    private long _rtt = Unknown;
    private int _fails;

    public string Name { get; }
    public string Host { get; }

    public ServiceState(string name, string host, int capacity)
    {
        Name = name;
        Host = host;
        _ring = new PingSample[Math.Max(16, capacity)];
    }

    /// <summary>-2 - ещё нет данных, -1 - связи нет, иначе задержка в мс.</summary>
    public long Rtt => Interlocked.Read(ref _rtt);

    /// <summary>IP-адрес, с которого пришёл последний успешный ответ.</summary>
    public string? Address
    {
        get { lock (_lock) return _address; }
    }

    public void SetAddress(string address)
    {
        lock (_lock) _address = address;
    }

    public void Report(long? rtt)
    {
        long now = Environment.TickCount64;
        lock (_lock)
        {
            int v = rtt.HasValue ? (int)Math.Min(rtt.Value, int.MaxValue) : -1;
            _ring[_head] = new PingSample(now, v);
            _head = (_head + 1) % _ring.Length;
            if (_count < _ring.Length) _count++;
        }

        if (rtt.HasValue)
        {
            _fails = 0;
            Interlocked.Exchange(ref _rtt, rtt.Value);
            return;
        }

        // две потери подряд = связь пропала (чтобы один дропнутый пакет не мигал красным)
        _fails++;
        if (_fails >= 2 || Interlocked.Read(ref _rtt) == Unknown)
            Interlocked.Exchange(ref _rtt, Lost);
    }

    /// <summary>Копия истории начиная с момента sinceMs (от старых к новым).</summary>
    public List<PingSample> Snapshot(long sinceMs)
    {
        var list = new List<PingSample>();
        lock (_lock)
        {
            int start = (_head - _count + _ring.Length) % _ring.Length;
            for (int i = 0; i < _count; i++)
            {
                PingSample s = _ring[(start + i) % _ring.Length];
                if (s.T >= sinceMs) list.Add(s);
            }
        }
        return list;
    }
}

internal sealed class PingMonitor : IDisposable
{
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();

    public IReadOnlyList<ServiceState> States { get; }

    public PingMonitor(AppConfig cfg)
    {
        int capacity = (int)Math.Min(100_000L, (long)cfg.HistoryMinutes * 60_000L / cfg.IntervalMs + 16);

        var list = new List<ServiceState>();
        foreach (ServiceEntry e in cfg.Services)
            list.Add(new ServiceState(e.Name, e.Host, capacity));
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
                {
                    rtt = reply.RoundtripTime;
                    if (reply.Address != null) state.SetAddress(reply.Address.ToString());
                }
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
