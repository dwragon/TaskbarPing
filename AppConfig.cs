using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TaskbarPing;

internal sealed class ServiceEntry
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
}

internal sealed class AppConfig
{
    public static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskbarPing");

    public static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    /// <summary>Список сервисов (максимум 6): имя на экране + IP или имя хоста.</summary>
    public List<ServiceEntry> Services { get; set; } = DefaultServices();

    /// <summary>Пауза между пингами, мс.</summary>
    public int IntervalMs { get; set; } = 1000;

    /// <summary>Таймаут одного пинга, мс.</summary>
    public int TimeoutMs { get; set; } = 1000;

    /// <summary>До этого значения (включительно) число зелёное, выше - жёлтое.</summary>
    public int GoodMs { get; set; } = 50;

    /// <summary>Сколько минут истории показывать на графике при наведении (1-120).</summary>
    public int HistoryMinutes { get; set; } = 5;

    /// <summary>Отступ виджета влево от области уведомлений (в пикселях при 100% масштаба).</summary>
    public int OffsetX { get; set; } = 170;

    private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private static List<ServiceEntry> DefaultServices() => new List<ServiceEntry>
    {
        new ServiceEntry { Name = "home", Host = "192.168.0.1" },
        new ServiceEntry { Name = "dns", Host = "1.1.1.1" }
    };

    /// <summary>
    /// Читает конфиг. Если файла нет - создаёт со значениями по умолчанию (created = true).
    /// Возвращает false, если файл повреждён (тогда cfg = значения по умолчанию).
    /// </summary>
    public static bool TryLoad(out AppConfig cfg, out bool created)
    {
        created = false;
        try
        {
            if (!File.Exists(ConfigPath))
            {
                cfg = new AppConfig();
                cfg.Save();
                created = true;
                return true;
            }

            string text = File.ReadAllText(ConfigPath);
            AppConfig? loaded = JsonSerializer.Deserialize<AppConfig>(text, JsonOpts);
            if (loaded == null)
            {
                cfg = new AppConfig();
                return false;
            }

            loaded.Sanitize();
            cfg = loaded;
            return true;
        }
        catch
        {
            cfg = new AppConfig();
            return false;
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            string tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
            File.Move(tmp, ConfigPath, true);
        }
        catch
        {
            // конфиг не критичен - работаем дальше
        }
    }

    private void Sanitize()
    {
        var clean = new List<ServiceEntry>();
        if (Services != null)
        {
            foreach (ServiceEntry s in Services)
            {
                string host = (s?.Host ?? "").Trim();
                string name = (s?.Name ?? "").Trim();
                if (!IsValidHost(host)) continue;
                if (name.Length == 0) name = host;
                if (name.Length > 14) name = name.Substring(0, 14);
                clean.Add(new ServiceEntry { Name = name, Host = host });
                if (clean.Count == 6) break;
            }
        }

        Services = clean.Count > 0 ? clean : DefaultServices();
        IntervalMs = Math.Clamp(IntervalMs, 500, 60000);
        TimeoutMs = Math.Clamp(TimeoutMs, 200, 5000);
        GoodMs = Math.Clamp(GoodMs, 1, 5000);
        HistoryMinutes = Math.Clamp(HistoryMinutes, 1, 120);
        OffsetX = Math.Clamp(OffsetX, -20, 4000);
    }

    private static readonly Regex HostRegex = new Regex(
        @"^(?!-)[A-Za-z0-9-]{1,63}(?<!-)(\.(?!-)[A-Za-z0-9-]{1,63}(?<!-))*$",
        RegexOptions.Compiled);

    /// <summary>Принимаем только IP-адрес или обычное DNS-имя - никаких путей, портов и прочего.</summary>
    public static bool IsValidHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253) return false;
        if (IPAddress.TryParse(host, out _)) return true;
        return HostRegex.IsMatch(host);
    }
}
