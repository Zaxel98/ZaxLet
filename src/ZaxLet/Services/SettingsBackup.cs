using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ZaxLet;

/// <summary>
/// Settings backup to a single JSON file ("ZaxLet-pengaturan.json"). Secrets (phone token, Discord secret, song recognition token) are excluded
/// on export, and on restore the current secrets are preserved, so backups are safe to share and paired phones stay connected.
/// Operates at the JSON level so it can be tested without the Settings class.
/// </summary>
public static class SettingsBackup
{
    public const int Format = 1;
    public static readonly string[] Secrets = { "ShareToken", "DiscordSecret", "DiscordClientId", "SongIdToken" };

    public static string Export(string settingsJson)
    {
        var s = JsonNode.Parse(settingsJson).AsObject();
        foreach (var k in Secrets) if (s.ContainsKey(k)) s[k] = "";
        var o = new JsonObject { ["zaxlet"] = Format, ["exported"] = DateTime.Now.ToString("s"), ["settings"] = s };
        return o.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Restored settings JSON; throws FormatException if the file is not a ZaxLet backup.</summary>
    public static string Import(string backupJson, string currentJson)
    {
        JsonObject b;
        try { b = JsonNode.Parse(backupJson) as JsonObject; } catch (JsonException) { b = null; }
        if (b == null || b["zaxlet"]?.GetValue<int>() != Format || b["settings"] is not JsonObject restored)
            throw new FormatException("Berkas ini bukan cadangan pengaturan ZaxLet.");
        var cur = JsonNode.Parse(currentJson).AsObject();
        foreach (var k in Secrets) if (cur.ContainsKey(k)) restored[k] = cur[k]?.DeepClone();
        return restored.ToJsonString();
    }
}
