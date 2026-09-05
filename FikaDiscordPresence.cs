using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Common.Models.Logging;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Net.Http;
using System.Net.Http.Headers;

namespace _FikaDiscordPresence;

public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.fiodor.fikadiscordpresence";
    public string Name { get; init; } = "Fika Discord Presence";
    public string Author { get; init; } = "Fiodor";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("1.0.3");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.0");
    public bool HasPrepatcher { get; init; } = false;
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
    public string License { get; init; } = "MIT";
}

[Injectable(TypePriority = OnLoadOrder.PostLoad)]
public class ReadJsonConfig(ISptLogger<ReadJsonConfig> logger, ModHelper modHelper) : IOnLoad
{
    private const string StateFileName = "message_Id.json";
    private const bool DiscordTts = false;
    private const bool IgnoreSslErrors = true;
    private const int TimeoutSeconds = 10;
    private DateTime _lastConfigWriteTime = DateTime.MinValue;

    private readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private bool ValidateConfig(ModConfig config, ISptLogger<ReadJsonConfig> logger)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(config.Discord.WebhookUrl))
            errors.Add("Discord.WebhookUrl is missing or empty.");

        if (string.IsNullOrWhiteSpace(config.Fika.ApiKey))
            errors.Add("Fika.ApiKey is missing or empty.");

        if (config.LogMonitor.Enabled)
        {
            var logPath = string.IsNullOrWhiteSpace(config.LogMonitor.LogFolderPath)
                ? GetDefaultLogFolderFromModPath(modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly()))
                : config.LogMonitor.LogFolderPath;

            if (string.IsNullOrWhiteSpace(logPath))
                errors.Add("LogMonitor enabled but could not resolve default log folder path.");
            else if (!Directory.Exists(logPath))
                errors.Add($"LogMonitor enabled but log folder does not exist: {logPath}");
        }

        if (errors.Count > 0)
        {
            logger.Error("Fika Discord Presence configuration error:");
            errors.ForEach(e => logger.Error($"  - {e}"));
            logger.Error("Fix config.json and restart the server.");
            return false;
        }

        return true;
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var pathToMod = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        var config = modHelper.GetJsonDataFromFile<ModConfig>(pathToMod, "config.json");

        if (!ValidateConfig(config, logger))
        {
            logger.Error("Mod will NOT start due to invalid configuration.");
            return Task.CompletedTask;
        }

        _ = Task.Run(async () =>
        {
            try { await RunLoop(logger, pathToMod, config, cancellationToken); }
            catch (Exception e) { logger.Error($"RunLoop error: {e}"); }
        }, cancellationToken);

        return Task.CompletedTask;
    }

    private async Task RunLoop(ISptLogger<ReadJsonConfig> logger, string pathToMod, ModConfig initialConfig, CancellationToken cancellationToken)
    {
        var config = initialConfig;
        var configPath = Path.Combine(pathToMod, "config.json");
        string statePath = Path.Combine(pathToMod, StateFileName);
        BotState state = LoadState(statePath);

        ulong? statusMessageId = config.Discord.StatusMessageId > 0 
            ? (ulong)config.Discord.StatusMessageId 
            : state.StatusMessageId > 0 ? state.StatusMessageId : null;

        HttpClient http = CreateHttpClient();
        LogMonitorLite? logMon = null;
        bool logMonEnabled = config.LogMonitor.Enabled;
        string resolvedLogPath = string.IsNullOrWhiteSpace(config.LogMonitor.LogFolderPath)
            ? GetDefaultLogFolderFromModPath(pathToMod)
            : config.LogMonitor.LogFolderPath;

        if (config.LogMonitor.Enabled && Directory.Exists(resolvedLogPath))
        {
            logMon = new LogMonitorLite(resolvedLogPath, TimeSpan.FromHours(config.LogMonitor.TimezoneOffsetHours));
        }

        try
        {
            string baseUrl = (config.Fika.BaseUrl ?? "").Trim().TrimEnd('/');
            var fikaHeaders = new AuthenticationHeaderValue("Bearer", config.Fika.ApiKey ?? "");
            ModConfig lastConfig = config;

            while (!cancellationToken.IsCancellationRequested)
            {
                config = ReloadConfig(configPath, config, logger);
                if (!ReferenceEquals(config, lastConfig))
                {
                    baseUrl = (config.Fika.BaseUrl ?? "").Trim().TrimEnd('/');
                    fikaHeaders = new AuthenticationHeaderValue("Bearer", config.Fika.ApiKey ?? "");
                    lastConfig = config;
                }

                logMon = UpdateLogMonitor(logMon, config, ref logMonEnabled, resolvedLogPath, logger);

                try
                {
                    logMon?.Poll();

                    var players = await GetOnlinePlayers(http, baseUrl, fikaHeaders, cancellationToken);
                    var presence = await GetPresence(http, baseUrl, fikaHeaders, cancellationToken);
                    var presenceByNick = BuildPresenceDict(presence);
                    var embed = RenderEmbed(config, players, presenceByNick, logMon?.WeeklyBoss, logMon?.WeeklyBossMap);

                    statusMessageId = await UpdateDiscordMessage(http, config, embed, statusMessageId, state, statePath, cancellationToken);
                }
                catch (TaskCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    logger.Warning($"Fika API request timed out. Will retry in {config.Update.IntervalSeconds} seconds.");
                }
                catch (HttpRequestException ex)
                {
                    logger.Warning($"Fika API request failed: {ex.Message}. Will retry in {config.Update.IntervalSeconds} seconds.");
                }
                catch (Exception e)
                {
                    logger.Error($"Update error: {e}");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, config.Update.IntervalSeconds)), cancellationToken);
                }
                catch (TaskCanceledException) { break; }
            }
        }
        finally
        {
            http.Dispose();
            logMon?.Dispose();
        }
    }

    private ModConfig ReloadConfig(string configPath, ModConfig current, ISptLogger<ReadJsonConfig> logger)
    {
        try
        {
            var lastWrite = File.GetLastWriteTimeUtc(configPath);
            if (lastWrite <= _lastConfigWriteTime) return current;

            var json = File.ReadAllText(configPath, Encoding.UTF8);
            var reloaded = JsonSerializer.Deserialize<ModConfig>(json, _jsonOpts);
            if (reloaded != null)
            {
                _lastConfigWriteTime = lastWrite;
                return reloaded;
            }
            return current;
        }
        catch (Exception ex)
        {
            logger.Warning($"Failed to reload config.json — keeping previous config. ({ex.Message})");
            return current;
        }
    }

    private LogMonitorLite? UpdateLogMonitor(LogMonitorLite? current, ModConfig config, 
        ref bool wasEnabled, string logPath, ISptLogger<ReadJsonConfig> logger)
    {
        if (config.LogMonitor.Enabled == wasEnabled) return current;

        current?.Dispose();
        wasEnabled = config.LogMonitor.Enabled;

        if (!config.LogMonitor.Enabled) return null;

        if (Directory.Exists(logPath))
            return new LogMonitorLite(logPath, TimeSpan.FromHours(config.LogMonitor.TimezoneOffsetHours));

        logger.Error($"LogMonitor enabled but log folder not found: {logPath}");
        return null;
    }

    private async Task<ulong?> UpdateDiscordMessage(HttpClient http, ModConfig config, EmbedPayload embed, 
        ulong? currentId, BotState state, string statePath, CancellationToken cancellationToken)
    {
        if (currentId is null || currentId == 0)
        {
            var created = await WebhookCreateMessage(http, config, embed, cancellationToken);
            if (ulong.TryParse(created.Id, out var mid) && mid > 0)
            {
                if (config.Discord.StatusMessageId <= 0)
                {
                    state.StatusMessageId = mid;
                    SaveState(statePath, state);
                }
                return mid;
            }
            return 0;
        }

        try
        {
            await WebhookEditMessage(http, config, currentId.Value, embed, cancellationToken);
            return currentId;
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("404"))
        {
            return 0;
        }
    }

    private static Dictionary<string, PresenceEntry> BuildPresenceDict(List<PresenceEntry> presence)
    {
        var dict = new Dictionary<string, PresenceEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in presence)
        {
            if (!string.IsNullOrWhiteSpace(p.Nickname))
                dict[p.Nickname] = p;
        }
        return dict;
    }

    private static string GetDefaultLogFolderFromModPath(string pathToMod)
    {
        try
        {
            var userDir = new DirectoryInfo(pathToMod).Parent?.Parent;
            return userDir == null ? "" : Path.Combine(userDir.FullName, "logs", "spt");
        }
        catch { return ""; }
    }

    private static HttpClient CreateHttpClient()
    {
        var httpHandler = new HttpClientHandler();
        if (IgnoreSslErrors)
            httpHandler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        return new HttpClient(httpHandler);
    }

    private string GetConfigValue(Dictionary<string, string> dict, string key, string fallback) =>
        dict.TryGetValue(key, out var value) ? value : fallback;

    private string GetBossDisplay(ModConfig config, string boss, string? map)
    {
        var bossName = GetConfigValue(config.BossNames, boss, boss);
        if (string.IsNullOrWhiteSpace(map)) return $"**{bossName}**";
        var mapDisp = GetConfigValue(config.MapNamesLog, map, map);
        return $"**{bossName}** on **{mapDisp}**";
    }

    private (List<OnlinePlayer> inRaid, List<OnlinePlayer> tetris) CategorizePlayers(
        List<OnlinePlayer> players, Dictionary<string, PresenceEntry> presenceByNick)
    {
        var inRaid = new List<OnlinePlayer>();
        var tetris = new List<OnlinePlayer>();

        foreach (var p in players)
        {
            var isInRaid = (presenceByNick.TryGetValue(p.Nickname, out var pres) && pres.Activity == 1) 
                           || p.LocationId is not (0 or 1);
            (isInRaid ? inRaid : tetris).Add(p);
        }

        return (inRaid, tetris);
    }

    private string FormatInRaidPlayer(OnlinePlayer p, ModConfig config, Dictionary<string, PresenceEntry> presenceByNick)
    {
        var mapName = GetConfigValue(config.LocationNames, p.LocationId.ToString(), $"Unknown({p.LocationId})");
        var mapEmoji = GetConfigValue(config.MapEmoji, mapName, config.Icons.DefaultMap);
        
        var extra = "";
        if (presenceByNick.TryGetValue(p.Nickname, out var pres))
        {
            var parts = new List<string>();
            if (pres.Activity == 1 && pres.Side is not null)
                parts.Add(GetConfigValue(config.SideNames, pres.Side.Value.ToString(), "Unknown"));
            
            var since = FmtSince(pres.ActivityStartedTimestamp);
            if (!string.IsNullOrWhiteSpace(since)) parts.Add(since);
            
            if (parts.Count > 0) extra = $" — _{string.Join(" · ", parts)}_";
        }

        return $"• **{p.Nickname}** — {mapEmoji} {mapName}{extra}";
    }

    private string FormatOutOfRaidPlayer(
    OnlinePlayer p,
    ModConfig config,
    Dictionary<string, PresenceEntry> presenceByNick)
    {
        if (presenceByNick.TryGetValue(p.Nickname, out var pres))
        {
            var activityName = GetConfigValue(config.ActivityNames, pres.Activity.ToString(), $"Activity({pres.Activity})");
            var since = FmtSince(pres.ActivityStartedTimestamp);
            var detailText = string.IsNullOrWhiteSpace(since) ? activityName : $"{activityName} · {since}";
            var icon = pres.Activity switch { 3 => "🏠", 0 => "📋", 2 => "🧰", 4 => "🛒", _ => "🧩" };
            return $"• **{p.Nickname}** — {icon} {detailText}";
        }

        var loc = GetConfigValue(config.LocationNames, p.LocationId.ToString(), $"Unknown({p.LocationId})");
        var fallbackText = loc == "Hideout" ? "🏠 Hideout" : "📋 Menu";
        return $"• **{p.Nickname}** — {fallbackText}";
    }


    private EmbedPayload RenderEmbed(ModConfig config, List<OnlinePlayer> players, 
        Dictionary<string, PresenceEntry> presenceByNick, string? weeklyBoss, string? weeklyBossMap)
    {
        var (inRaid, tetris) = CategorizePlayers(players, presenceByNick);

        var embed = new EmbedPayload
        {
            Title = config.Text.Title,
            Color = config.Colors.EmbedColorDecimal,
            Fields = []
        };

        if (players.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(weeklyBoss))
                embed.Fields.Add(new() { Name = config.Text.BossTitle, Value = GetBossDisplay(config, weeklyBoss, weeklyBossMap), Inline = false });

            embed.Fields.Add(new() { Name = "\u200b", Value = config.Text.NoOnlineDescription, Inline = false });
            embed.Footer = new() { Text = $"{config.Text.FooterPrefix} {DateTime.Now:yyyy-MM-dd HH:mm:ss}" };
            return embed;
        }

        if (!string.IsNullOrWhiteSpace(weeklyBoss))
            embed.Fields.Add(new() { Name = config.Text.BossTitle, Value = GetBossDisplay(config, weeklyBoss, weeklyBossMap), Inline = false });

        if (inRaid.Count > 0)
        {
            inRaid.Sort((a, b) => string.Compare(a.Nickname, b.Nickname, StringComparison.OrdinalIgnoreCase));
            var lines = inRaid.Select(p => FormatInRaidPlayer(p, config, presenceByNick)).ToList();
            embed.Fields.Add(new() { Name = config.Text.InRaidTitle, Value = string.Join("\n", lines), Inline = false });
        }
        else
        {
            embed.Fields.Add(new() { Name = config.Text.InRaidTitle, Value = config.Text.InRaidEmpty, Inline = false });
        }

        if (tetris.Count > 0)
        {
            tetris.Sort((a, b) => string.Compare(a.Nickname, b.Nickname, StringComparison.OrdinalIgnoreCase));
            var lines = tetris.Select(p => FormatOutOfRaidPlayer(p, config, presenceByNick)).ToList();
            embed.Fields.Add(new() { Name = config.Text.OutOfRaidTitle, Value = string.Join("\n", lines), Inline = false });
        }
        else
        {
            embed.Fields.Add(new() { Name = config.Text.OutOfRaidTitle, Value = config.Text.OutOfRaidEmpty, Inline = false });
        }

        var countsLine = $"{players.Count} Online | {inRaid.Count} In Raid | {tetris.Count} Playing Tetris";
        embed.Fields.Add(new() { Name = "\u200b", Value = $"**{countsLine}**", Inline = false });
        embed.Footer = new() { Text = $"{config.Text.FooterPrefix} {DateTime.Now:yyyy-MM-dd HH:mm:ss}" };
        
        return embed;
    }

    private static string FmtSince(long startedTs)
    {
        if (startedTs <= 0) return "";
        try
        {
            var delta = DateTime.Now - DateTimeOffset.FromUnixTimeSeconds(startedTs).LocalDateTime;
            if (delta.TotalSeconds < 0) return "";

            var mins = (int)(delta.TotalSeconds / 60);
            if (mins < 1) return "just now";
            if (mins < 60) return $"{mins}m";

            var hrs = mins / 60;
            return $"{hrs}h{mins % 60:00}m";
        }
        catch { return ""; }
    }

    private async Task<T> FikaRequest<T>(HttpClient http, string endpoint, string baseUrl, AuthenticationHeaderValue auth, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}{endpoint}");
        req.Headers.Authorization = auth;
        req.Headers.Add("responsecompressed", "0");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
        using var resp = await http.SendAsync(req, cts.Token);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(cts.Token);
        return JsonSerializer.Deserialize<T>(json, _jsonOpts)!;
    }

    private async Task<List<OnlinePlayer>> GetOnlinePlayers(HttpClient http, string baseUrl, AuthenticationHeaderValue auth, CancellationToken cancellationToken)
    {
        var data = await FikaRequest<PlayersResponse>(http, "/fika/api/players", baseUrl, auth, cancellationToken);
        return data?.Players?.Select(p => new OnlinePlayer
        {
            ProfileId = p.ProfileId ?? "",
            Nickname = p.Nickname ?? "",
            LocationId = (int)p.Location
        }).ToList() ?? [];
    }

    private async Task<List<PresenceEntry>> GetPresence(HttpClient http, string baseUrl, AuthenticationHeaderValue auth, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/fika/presence/get");
        req.Headers.Authorization = auth;
        req.Headers.Add("responsecompressed", "0");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
        using var resp = await http.SendAsync(req, cts.Token);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(cts.Token);
        var outList = new List<PresenceEntry>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return outList;

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                try
                {
                    var nick = el.TryGetProperty("nickname", out var nEl) ? nEl.GetString() ?? "" : "";
                    var level = el.TryGetProperty("level", out var lEl) ? lEl.GetInt32() : 0;
                    var activity = el.TryGetProperty("activity", out var aEl) ? aEl.GetInt32() : 0;
                    var started = el.TryGetProperty("activityStartedTimestamp", out var sEl) ? sEl.GetInt64() : 0;

                    int? side = null;
                    if (el.TryGetProperty("raidInformation", out var rEl) && rEl.ValueKind == JsonValueKind.Object)
                        if (rEl.TryGetProperty("side", out var sideEl) && sideEl.ValueKind is JsonValueKind.Number)
                            side = sideEl.GetInt32();

                    outList.Add(new() { Nickname = nick, Level = level, Activity = activity, ActivityStartedTimestamp = started, Side = side });
                }
                catch { }
            }
        }
        catch { }

        return outList;
    }

    private async Task<WebhookMessage> WebhookCreateMessage(HttpClient http, ModConfig config, EmbedPayload embed, CancellationToken cancellationToken)
    {
        var url = (config.Discord.WebhookUrl ?? "").Trim();
        var postUrl = $"{url}{(url.Contains('?') ? "&" : "?")}wait=true";

        var payload = new WebhookSendPayload
        {
            Content = null,
            Username = config.Discord.Username,
            AvatarUrl = config.Discord.AvatarUrl,
            Tts = DiscordTts,
            Embeds = [embed]
        };

        var body = JsonSerializer.Serialize(payload, _jsonOpts);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
        using var resp = await http.PostAsync(postUrl, new StringContent(body, Encoding.UTF8, "application/json"), cts.Token);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(cts.Token);
        return JsonSerializer.Deserialize<WebhookMessage>(json, _jsonOpts) ?? new();
    }

    private async Task WebhookEditMessage(HttpClient http, ModConfig config, ulong messageId, EmbedPayload embed, CancellationToken cancellationToken)
    {
        var patchUrl = $"{(config.Discord.WebhookUrl ?? "").Trim().TrimEnd('/')}/messages/{messageId}";

        var payload = new WebhookSendPayload
        {
            Content = null,
            Username = config.Discord.Username,
            AvatarUrl = config.Discord.AvatarUrl,
            Tts = DiscordTts,
            Embeds = [embed]
        };

        var body = JsonSerializer.Serialize(payload, _jsonOpts);
        using var req = new HttpRequestMessage(new HttpMethod("PATCH"), patchUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
        using var resp = await http.SendAsync(req, cts.Token);
        resp.EnsureSuccessStatusCode();
    }

    private static BotState LoadState(string path)
    {
        try
        {
            return File.Exists(path) 
                ? JsonSerializer.Deserialize<BotState>(File.ReadAllText(path, Encoding.UTF8)) ?? new()
                : new();
        }
        catch { return new(); }
    }

    private static void SaveState(string path, BotState state)
    {
        try
        {
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json, Encoding.UTF8);
        }
        catch { }
    }
}

public record ModConfig
{
    public DiscordConfig Discord { get; set; } = new();
    public FikaConfig Fika { get; set; } = new();
    public UpdateConfig Update { get; set; } = new();
    public LogMonitorConfig LogMonitor { get; set; } = new();
    public TextConfig Text { get; set; } = new();
    public ColorConfig Colors { get; set; } = new();
    public IconConfig Icons { get; set; } = new();
    public Dictionary<string, string> MapEmoji { get; set; } = new();
    public Dictionary<string, string> LocationNames { get; set; } = new();
    public Dictionary<string, string> ActivityNames { get; set; } = new();
    public Dictionary<string, string> SideNames { get; set; } = new();
    public Dictionary<string, string> BossNames { get; set; } = new();
    public Dictionary<string, string> MapNamesLog { get; set; } = new();
}

public record DiscordConfig
{
    public string WebhookUrl { get; set; } = "";
    public string Username { get; set; } = "Fika Status";
    public string AvatarUrl { get; set; } = "";
    public long StatusMessageId { get; set; }
}

public record FikaConfig
{
    public string BaseUrl { get; set; } = "https://127.0.0.1:6969";
    public string ApiKey { get; set; } = "";
}

public record UpdateConfig { public int IntervalSeconds { get; set; } = 30; }
public record LogMonitorConfig { public bool Enabled { get; set; }  public int TimezoneOffsetHours { get; set; } public string? LogFolderPath { get; set; } }

public record TextConfig
{
    public string Title { get; set; } = "Fika Server Status";
    public string NoOnlineDescription { get; set; } = "🌵💨 _No one is online right now._";
    public string InRaidTitle { get; set; } = "⚔️ In Raid";
    public string InRaidEmpty { get; set; } = "_Nobody currently in raid._";
    public string OutOfRaidTitle { get; set; } = "🧩 Playing Tetris";
    public string OutOfRaidEmpty { get; set; } = "_Everyone online is in raid._";
    public string BossTitle { get; set; } = "👑 Boss of the Week";
    public string FooterPrefix { get; set; } = "Last updated:";
}

public record ColorConfig { public int EmbedColorDecimal { get; set; } = 3447003; }
public record IconConfig { public string DefaultMap { get; set; } = "🗺️"; }

public class PlayersResponse { [JsonPropertyName("players")] public List<PlayerEntry>? Players { get; set; } }
public class PlayerEntry
{
    [JsonPropertyName("profileId")] public string? ProfileId { get; set; }
    [JsonPropertyName("nickname")] public string? Nickname { get; set; }
    [JsonPropertyName("location")] public int Location { get; set; }
}

public class OnlinePlayer
{
    public string ProfileId { get; set; } = "";
    public string Nickname { get; set; } = "";
    public int LocationId { get; set; }
}

public class PresenceEntry
{
    public string Nickname { get; set; } = "";
    public int Level { get; set; }
    public int Activity { get; set; }
    public long ActivityStartedTimestamp { get; set; }
    public int? Side { get; set; }
}

public class WebhookSendPayload
{
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("avatar_url")] public string? AvatarUrl { get; set; }
    [JsonPropertyName("tts")] public bool Tts { get; set; }
    [JsonPropertyName("embeds")] public List<EmbedPayload>? Embeds { get; set; }
}

public class WebhookMessage { [JsonPropertyName("id")] public string Id { get; set; } = "0"; }

public class EmbedPayload
{
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("color")] public int Color { get; set; }
    [JsonPropertyName("fields")] public List<EmbedFieldPayload>? Fields { get; set; }
    [JsonPropertyName("footer")] public EmbedFooterPayload? Footer { get; set; }
}

public class EmbedFieldPayload
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("value")] public string Value { get; set; } = "";
    [JsonPropertyName("inline")] public bool Inline { get; set; }
}

public class EmbedFooterPayload { [JsonPropertyName("text")] public string Text { get; set; } = ""; }
public class BotState { [JsonPropertyName("status_message_id")] public ulong StatusMessageId { get; set; } }

public class LogMonitorLite : IDisposable
{
    private readonly string _logFolderPath;
    private readonly TimeSpan _tzOffset;
    private string? _logFilePath;
    private long _pos;
    private bool _disposed;

    public string? WeeklyBoss { get; private set; }
    public string? WeeklyBossMap { get; private set; }

    private static readonly Regex _weeklyBossRegex = new(@"Weekly Boss:\s+(boss\w+)\s+\|\s+\d+%\s+Chance\s+on\s+(\w+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex _bossOfTheWeekRegex = new(@"\b(boss\w+)\b\s+is\s+boss\s+of\s+the\s+week\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Dictionary<string, string> BossToMapKey = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bossBully"] = "bigmap",
        ["bossGluhar"] = "rezervbase",
        ["bossKilla"] = "interchange",
        ["bossKojaniy"] = "woods",
        ["bossSanitar"] = "shoreline",
        ["bossKolontay"] = "tarkovstreets",
        ["bossKnight"] = "lighthouse",
        ["bossTagilla"] = "factory4_day",
    };

    public LogMonitorLite(string logFolderPath, TimeSpan tzOffset)
    {
        _logFolderPath = logFolderPath;
        _tzOffset = tzOffset;
        InitFile();
    }

    private void InitFile()
    {
        _logFilePath = FindLatestLog();
        if (_logFilePath == null) return;

        try
        {
            using var fs = new FileStream(_logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            string? line;
            while ((line = sr.ReadLine()) != null)
                ProcessLine(line, initialLoad: true);

            _pos = fs.Position;
        }
        catch { _logFilePath = null; }
    }

    private string? FindLatestLog()
    {
        if (string.IsNullOrWhiteSpace(_logFolderPath) || !Directory.Exists(_logFolderPath)) return null;

        var files = Directory.GetFiles(_logFolderPath, "*.log");
        if (files.Length == 0) return null;

        return files.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    public void Poll()
    {
        if (_disposed || _logFilePath == null || !File.Exists(_logFilePath))
        {
            if (!_disposed) InitFile();
            return;
        }

        try
        {
            using var fs = new FileStream(_logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(_pos, SeekOrigin.Begin);
            using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
            
            string? line;
            while ((line = sr.ReadLine()) != null)
                if (!string.IsNullOrWhiteSpace(line))
                    ProcessLine(line, initialLoad: false);

            _pos = fs.Position;
        }
        catch { }
    }

    private void ProcessLine(string line, bool initialLoad)
    {
        if (line.Contains("Weekly Boss:", StringComparison.OrdinalIgnoreCase))
        {
            var m = _weeklyBossRegex.Match(line);
            if (m.Success)
            {
                WeeklyBoss = m.Groups[1].Value;
                WeeklyBossMap = m.Groups[2].Value;
                return;
            }
        }

        if (line.Contains(" is boss of the week", StringComparison.OrdinalIgnoreCase))
        {
            var m2 = _bossOfTheWeekRegex.Match(line);
            if (m2.Success && (string.IsNullOrWhiteSpace(WeeklyBoss) || string.IsNullOrWhiteSpace(WeeklyBossMap)))
            {
                WeeklyBoss = m2.Groups[1].Value;
                WeeklyBossMap = BossToMapKey.TryGetValue(WeeklyBoss, out var mapKey) ? mapKey : null;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _logFilePath = null;
        WeeklyBoss = null;
        WeeklyBossMap = null;
    }
}
