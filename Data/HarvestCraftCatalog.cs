using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace MyBigCrafter.Data;

/// <summary>One Horticrafting craft option: its in-game display name plus lifeforce info (for hints/stats).</summary>
public sealed class HarvestCraftInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";   // canonical display name == the live HarvestCraftElement.CraftDisplayName
    public int Tier { get; init; }
    public int LifeforceType { get; init; }    // 1/2/3 = the three lifeforce colours
    public int LifeforceCost { get; init; }
}

/// <summary>
/// The full Horticrafting craft list, loaded from the embedded <c>harvestcraftoptions.json</c> dump so the
/// harvest-node picker works offline (there is no RePoE/PoeModDataLib source for harvest crafts). Each craft's
/// <see cref="HarvestCraftInfo.Name"/> is the dump's <c>Text</c> with colour tags stripped - this matches what
/// the game displays and what its search box filters on (the <c>Description</c> column differs for the
/// stack-exchange crafts), so it is also the string the executor searches for and matches against the live
/// window.
/// </summary>
public sealed class HarvestCraftCatalog
{
    private static readonly Regex Tags = new("<[^>]*>", RegexOptions.Compiled);

    public List<HarvestCraftInfo> All { get; } = new();
    public bool IsBuilt { get; private set; }

    public void EnsureBuilt()
    {
        if (IsBuilt) return;
        try
        {
            var json = LoadEmbedded();
            if (string.IsNullOrEmpty(json)) return;

            var rows = JsonConvert.DeserializeObject<List<Row>>(json);
            if (rows == null) return;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in rows)
            {
                var name = Clean(r.Text);
                if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;
                All.Add(new HarvestCraftInfo
                {
                    Id = r.Id ?? "",
                    Name = name,
                    Tier = r.Tier,
                    LifeforceType = r.LifeforceType,
                    LifeforceCost = r.LifeforceCost,
                });
            }
            IsBuilt = All.Count > 0;
        }
        catch { /* leave empty; the picker shows a "couldn't load" note */ }
    }

    private static string LoadEmbedded()
    {
        var asm = typeof(HarvestCraftCatalog).Assembly;
        var res = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("harvestcraftoptions.json", StringComparison.OrdinalIgnoreCase));
        if (res == null) return null;
        using var s = asm.GetManifestResourceStream(res);
        if (s == null) return null;
        using var sr = new StreamReader(s);
        return sr.ReadToEnd();
    }

    // "<white>{Reforge} a Rare item, including a <craftingfire>{Fire} modifier" -> "Reforge a Rare item, including a Fire modifier"
    private static string Clean(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = Tags.Replace(text, "");
        s = s.Replace("{", "").Replace("}", "");
        return s.Trim();
    }

    private sealed class Row
    {
        public string Id { get; set; }
        public string Text { get; set; }
        public int Tier { get; set; }
        public int LifeforceType { get; set; }
        public int LifeforceCost { get; set; }
    }
}
