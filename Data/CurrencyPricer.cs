using System;
using System.Collections.Generic;
using ExileCore;
using ExileCore.PoEMemory.Models;

namespace MyBigCrafter.Data;

/// <summary>
/// Per-unit chaos prices for currency, via the Ninja Price plugin's bridge method
/// <c>NinjaPrice.GetBaseItemTypeValue</c> (<see cref="BaseItemType"/> -> chaos). The method is looked up
/// lazily through <c>GameController.PluginBridge</c> and may be absent (Ninja Price not loaded) - then every
/// price is null and the Stats page shows costs as unavailable. Looked-up prices are cached per currency name
/// for the session (each Ninja Price call reprices a CustomItem, so caching matters); <see cref="Refresh"/>
/// clears the cache to re-query after prices update or the plugin loads.
/// </summary>
public sealed class CurrencyPricer
{
    private const string BridgeMethod = "NinjaPrice.GetBaseItemTypeValue";

    private readonly GameController _gc;
    private Func<BaseItemType, double> _getValue;
    private Dictionary<string, BaseItemType> _baseByName;
    private readonly Dictionary<string, double?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public CurrencyPricer(GameController gc) => _gc = gc;

    /// <summary>True once the Ninja Price bridge method is reachable.</summary>
    public bool Available => Resolve() != null;

    /// <summary>Per-unit chaos value for a currency base name (e.g. "Orb of Alteration"), or null when the
    /// price / plugin / base type is unknown. Served from the session cache (for the display).</summary>
    public double? PerUnit(string currencyName)
    {
        if (string.IsNullOrEmpty(currencyName)) return null;
        return _cache.TryGetValue(currencyName, out var cached) ? cached : Query(currencyName);
    }

    /// <summary>Like <see cref="PerUnit"/> but ALWAYS re-queries the bridge (refreshing the cache) - used to
    /// capture an accurate cost basis at the moment a craft finishes, not a stale session-cached price.</summary>
    public double? PerUnitFresh(string currencyName)
        => string.IsNullOrEmpty(currencyName) ? null : Query(currencyName);

    /// <summary>Drop cached prices so the next lookup re-queries (after Ninja Price updates or loads).</summary>
    public void Refresh() => _cache.Clear();

    // Query the bridge for a currency's per-unit chaos and (re)cache it. null = plugin/base/price unknown.
    private double? Query(string currencyName)
    {
        double? value = null;
        var fn = Resolve();
        var bit = fn != null ? BaseFor(currencyName) : null;
        if (bit != null)
        {
            try { var v = fn(bit); if (v > 0) value = v; }
            catch { /* bridge threw - treat as unknown */ }
        }
        _cache[currencyName] = value;
        return value;
    }

    // Re-query the bridge each call until it resolves, then keep it (handles Ninja Price loading after us).
    private Func<BaseItemType, double> Resolve()
    {
        if (_getValue != null) return _getValue;
        try { _getValue = _gc?.PluginBridge?.GetMethod<Func<BaseItemType, double>>(BridgeMethod); }
        catch { _getValue = null; }
        return _getValue;
    }

    private BaseItemType BaseFor(string currencyName)
    {
        if (_baseByName == null)
        {
            _baseByName = new Dictionary<string, BaseItemType>(StringComparer.OrdinalIgnoreCase);
            var contents = _gc?.Files?.BaseItemTypes?.Contents;
            if (contents != null)
                foreach (var kv in contents)
                    if (kv.Value?.BaseName is { Length: > 0 } n) _baseByName.TryAdd(n, kv.Value);
        }
        return _baseByName.GetValueOrDefault(currencyName);
    }
}
