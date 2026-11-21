using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ZaxLet;

/// <summary>
/// Cadangan pengaturan ke satu berkas JSON ("ZaxLet-pengaturan.json"). Rahasia (token HP, rahasia Discord, token pengenal lagu) tidak ikut
/// diekspor, dan saat memulihkan nilai rahasia yang sekarang dipertahankan, jadi cadangan aman dibagikan dan HP yang sudah dipasangkan tidak putus.
/// Bekerja di tingkat JSON supaya bisa diuji tanpa kelas Settings.
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

    /// <summary>JSON pengaturan hasil pemulihan; melempar FormatException bila berkas bukan cadangan ZaxLet.</summary>
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
