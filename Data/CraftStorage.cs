using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using MyBigCrafter.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace MyBigCrafter.Data;

/// <summary>Saves/loads craft plans as JSON under ConfigDirectory/Crafts, plus clipboard share strings.</summary>
public sealed class CraftStorage
{
    private const string SharePrefixV2 = "MBC2:";   // base64(deflate(compact JSON))

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Ignore,
        Converters = { new StringEnumConverter() },
    };

    // Share strings don't need pretty-printing - the whitespace just bloats the base64 (~40% bigger).
    private static readonly JsonSerializerSettings CompactJson = new()
    {
        Formatting = Formatting.None,
        NullValueHandling = NullValueHandling.Ignore,
        Converters = { new StringEnumConverter() },
    };

    private readonly string _folder;
    private readonly string _configDir;

    public CraftStorage(string configDirectory)
    {
        _configDir = configDirectory ?? ".";
        _folder = Path.Combine(_configDir, "Crafts");
        try { Directory.CreateDirectory(_folder); } catch { /* ignore */ }
    }

    public string FolderPath => _folder;

    public void SaveRunConfig(RunConfig config)
    {
        try { File.WriteAllText(Path.Combine(_configDir, "runconfig.json"), JsonConvert.SerializeObject(config, JsonSettings)); }
        catch { /* ignore */ }
    }

    public RunConfig LoadRunConfig()
    {
        try
        {
            var file = Path.Combine(_configDir, "runconfig.json");
            return File.Exists(file)
                ? JsonConvert.DeserializeObject<RunConfig>(File.ReadAllText(file), JsonSettings) ?? new RunConfig()
                : new RunConfig();
        }
        catch { return new RunConfig(); }
    }

    public List<string> List()
    {
        try
        {
            return Directory.GetFiles(_folder, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return new List<string>(); }
    }

    public bool Save(CraftPlan plan, out string error)
    {
        error = null;
        try
        {
            var file = Path.Combine(_folder, Sanitize(plan.Name) + ".json");
            File.WriteAllText(file, JsonConvert.SerializeObject(plan, JsonSettings));
            return true;
        }
        catch (Exception e) { error = e.Message; return false; }
    }

    public CraftPlan Load(string name)
    {
        try
        {
            var file = Path.Combine(_folder, Sanitize(name) + ".json");
            return File.Exists(file)
                ? JsonConvert.DeserializeObject<CraftPlan>(File.ReadAllText(file), JsonSettings)
                : null;
        }
        catch { return null; }
    }

    public void Delete(string name)
    {
        try
        {
            var file = Path.Combine(_folder, Sanitize(name) + ".json");
            if (File.Exists(file)) File.Delete(file);
        }
        catch { /* ignore */ }
    }

    /// <summary>Compact, paste-safe share string ("MBC2:" + base64 of deflate-compressed compact JSON).</summary>
    public string Export(CraftPlan plan)
    {
        var json = JsonConvert.SerializeObject(plan, CompactJson);
        var bytes = Encoding.UTF8.GetBytes(json);
        using var ms = new MemoryStream();
        using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            ds.Write(bytes, 0, bytes.Length);
        return SharePrefixV2 + Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>Accepts an "MBC2:" share string, or raw JSON (handy for hand-editing). Null if invalid.</summary>
    public CraftPlan Import(string text)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();

            string json;
            if (text.StartsWith(SharePrefixV2, StringComparison.Ordinal))
            {
                var data = Convert.FromBase64String(text[SharePrefixV2.Length..]);
                using var ms = new MemoryStream(data);
                using var ds = new DeflateStream(ms, CompressionMode.Decompress);
                using var sr = new StreamReader(ds, Encoding.UTF8);
                json = sr.ReadToEnd();
            }
            else if (text.StartsWith("{", StringComparison.Ordinal))
            {
                json = text;
            }
            else return null;

            return JsonConvert.DeserializeObject<CraftPlan>(json, JsonSettings);
        }
        catch { return null; }
    }

    private static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "craft";
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }
}
