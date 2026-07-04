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
        try { SeedTemplatesIfEmpty(); } catch { /* ignore - templates are a convenience, never a blocker */ }
    }

    /// <summary>First-run quickstart: when the user has no crafts at all, unpack the embedded starter
    /// templates (Templates\*.json in the repo) into the Crafts folder so a new user starts from working
    /// alt-craft skeletons instead of a blank editor.</summary>
    private void SeedTemplatesIfEmpty()
    {
        if (Directory.EnumerateFiles(_folder, "*.json").Any()) return;

        var asm = typeof(CraftStorage).Assembly;
        foreach (var res in asm.GetManifestResourceNames())
        {
            // Manifest names are "<RootNamespace>.Templates.<original file name>" - the folder part is
            // namespace-mangled but the file name survives verbatim (spaces, '+' and all).
            const string marker = ".Templates.";
            var i = res.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0 || !res.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;

            using var s = asm.GetManifestResourceStream(res);
            if (s == null) continue;
            using var sr = new StreamReader(s, Encoding.UTF8);
            File.WriteAllText(Path.Combine(_folder, res[(i + marker.Length)..]), sr.ReadToEnd());
        }
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

    // Native clipboards can hand back stray NUL terminators/BOMs, and chat apps like to wrap pastes in
    // quotes - none of which plain Trim() removes.
    private static readonly char[] ShareTrimChars =
        { '\0', '\uFEFF', '\u200B', '"', '\'', ' ', '\t', '\r', '\n' };

    /// <summary>Accepts an "MBC2:" share string, or raw JSON (handy for hand-editing). Null if invalid, with
    /// the reason in <paramref name="error"/> so the toolbar can say WHY instead of failing silently.</summary>
    public CraftPlan Import(string text, out string error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(text)) { error = "the clipboard has no text"; return null; }

        text = text.Trim(ShareTrimChars);

        string json;
        if (text.StartsWith(SharePrefixV2, StringComparison.Ordinal))
        {
            try
            {
                var data = Convert.FromBase64String(text[SharePrefixV2.Length..]);
                using var ms = new MemoryStream(data);
                using var ds = new DeflateStream(ms, CompressionMode.Decompress);
                using var sr = new StreamReader(ds, Encoding.UTF8);
                json = sr.ReadToEnd();
            }
            catch (Exception e) { error = "corrupt share string (" + e.Message + ")"; return null; }
        }
        else if (text.StartsWith("{", StringComparison.Ordinal))
        {
            json = text;
        }
        else
        {
            error = $"not a craft share string (clipboard starts with '{Snippet(text)}')";
            return null;
        }

        try
        {
            var plan = JsonConvert.DeserializeObject<CraftPlan>(json, JsonSettings);
            if (plan == null) { error = "the share string contained no craft"; return null; }
            return plan;
        }
        catch (Exception e) { error = "craft JSON didn't parse (" + e.Message + ")"; return null; }
    }

    private static string Snippet(string s) => s.Length <= 24 ? s : s[..24] + "...";

    private static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "craft";
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }
}
