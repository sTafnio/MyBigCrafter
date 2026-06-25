using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace MyBigCrafter.Data;

/// <summary>One currency's tally for a craft: how many were used, and the chaos they cost AT THE TIME of each
/// run (the cost basis - frozen, immune to later price moves). Current value is computed live from Count.</summary>
public sealed class CurrencyTally
{
    public long Count { get; set; }
    public double ChaosSpent { get; set; }
}

/// <summary>Lifetime tallies for one craft (keyed by craft name): items finished and currency consumed.</summary>
public sealed class CraftStat
{
    public string Name { get; set; } = "";
    public long Finished { get; set; }
    public Dictionary<string, CurrencyTally> Currency { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Persistent per-craft statistics (<c>ConfigDirectory/stats.json</c>): how many items each craft has
/// finished, how much of each currency it used, and the chaos that currency cost at the time of each run
/// (the cost basis), accumulated across runs. Current value can be recomputed live from the counts; the stored
/// ChaosSpent gives the historical "what it actually cost" view. Best-effort: IO errors are swallowed.
/// </summary>
public sealed class StatsStore
{
    private static readonly JsonSerializerSettings Json = new() { Formatting = Formatting.Indented };

    private readonly string _file;
    private Dictionary<string, CraftStat> _byName = new(StringComparer.OrdinalIgnoreCase);

    public StatsStore(string configDir)
    {
        _file = Path.Combine(configDir, "stats.json");
        Load();
    }

    /// <summary>Crafts that have run at least once, most-finished first.</summary>
    public IReadOnlyList<CraftStat> All => _byName.Values.OrderByDescending(s => s.Finished).ThenBy(s => s.Name).ToList();

    /// <summary>Fold one run's result into the craft's lifetime totals: finished items, currency counts, and
    /// the chaos those currencies cost now (the cost basis). <paramref name="chaosNow"/> holds
    /// count x current-price for each PRICED currency this run (absent = unpriced, so no basis added).</summary>
    public void Record(string craftName, long finished, IReadOnlyDictionary<string, int> currencyUsed,
        IReadOnlyDictionary<string, double> chaosNow)
    {
        if (string.IsNullOrWhiteSpace(craftName)) craftName = "(unnamed)";
        var hasCurrency = currencyUsed != null && currencyUsed.Count > 0;
        if (finished <= 0 && !hasCurrency) return;   // nothing happened - don't create an empty row

        if (!_byName.TryGetValue(craftName, out var s))
            _byName[craftName] = s = new CraftStat { Name = craftName };

        s.Finished += finished;
        if (hasCurrency)
            foreach (var kv in currencyUsed)
            {
                if (!s.Currency.TryGetValue(kv.Key, out var t)) s.Currency[kv.Key] = t = new CurrencyTally();
                t.Count += kv.Value;
                if (chaosNow != null && chaosNow.TryGetValue(kv.Key, out var c)) t.ChaosSpent += c;
            }

        Save();
    }

    public void Reset(string craftName) { if (_byName.Remove(craftName)) Save(); }
    public void ResetAll() { if (_byName.Count > 0) { _byName.Clear(); Save(); } }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            var raw = JsonConvert.DeserializeObject<Dictionary<string, CraftStat>>(File.ReadAllText(_file), Json)
                      ?? new Dictionary<string, CraftStat>();
            // Deserialized dictionaries use the default (case-sensitive) comparer - rebuild with our comparer.
            _byName = new Dictionary<string, CraftStat>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in raw)
            {
                var s = kv.Value ?? new CraftStat();
                if (string.IsNullOrEmpty(s.Name)) s.Name = kv.Key;
                s.Currency = new Dictionary<string, CurrencyTally>(s.Currency ?? new(), StringComparer.OrdinalIgnoreCase);
                _byName[kv.Key] = s;
            }
        }
        catch { _byName = new Dictionary<string, CraftStat>(StringComparer.OrdinalIgnoreCase); }
    }

    private void Save()
    {
        try { File.WriteAllText(_file, JsonConvert.SerializeObject(_byName, Json)); }
        catch { /* best-effort */ }
    }
}
