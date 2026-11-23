using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Solitude.Core;
using Solitude.Systems;
using Solitude.UI;

namespace Solitude;

/// <summary>
/// THE HOLD (I): the boat's cargo manifest on a green screen (aboard) or as notes in the journal's
/// paper style (inside a station). Grouped the way a crew would sort it; each entry says what it is,
/// what it weighs and what it's for. Two things can be done here and nothing else (Tom: the work stays
/// hands-on in the world): eat, and throw something overboard to make room.
/// </summary>
public partial class Main
{
    private SubViewport _holdVp;
    private TerminalScreen _holdScreen;
    private bool _holdOpen;
    private ItemId? _jettisonArmed;
    private float _jettisonT;

    private static readonly ItemId[] Samples =
    {
        ItemId.Nodule, ItemId.Crystal, ItemId.Lumen, ItemId.Clam, ItemId.Plankton, ItemId.Sediment,
        ItemId.TissueRavager, ItemId.TissueRay, ItemId.ScaleWyrm, ItemId.PlateGloam,
    };
    private static readonly ItemId[] Salvage =
        { ItemId.StationAlloy, ItemId.Servo, ItemId.Valve, ItemId.Coil, ItemId.Optics, ItemId.LogicBoard, ItemId.Impeller };

    private enum HoldGroup { Samples, Salvage, Made, Food, Modules }

    private static HoldGroup GroupOf(ItemId id)
    {
        if (Samples.Contains(id)) return HoldGroup.Samples;
        if (Salvage.Contains(id)) return HoldGroup.Salvage;
        if (Modules.ByItem(id) != null) return HoldGroup.Modules;
        if (Items.Info[id].Food > 0f) return HoldGroup.Food;
        return HoldGroup.Made;
    }

    private void SetupHold()
    {
        _holdVp = MakeTerminalViewport("HoldScreen");
        _holdVp.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        TerminalScreen.Tab Page(string name, HoldGroup g) => new() { Name = name, Notes = true, List = () => HoldList(g) };
        _holdScreen = new TerminalScreen
        {
            Title = "HOLD · MANIFEST",
            Clock = HoldHeader,
            EmptyText = "Nothing of this kind aboard.",
            Tabs = new List<TerminalScreen.Tab>
            {
                Page("SAMPLES", HoldGroup.Samples), Page("SALVAGE", HoldGroup.Salvage), Page("MADE", HoldGroup.Made),
                Page("FOOD", HoldGroup.Food), Page("MODULES", HoldGroup.Modules),
            },
        };
        _holdVp.AddChild(_holdScreen);
    }

    /// <summary>The items of one group in the hold, in a stable order.</summary>
    private List<ItemId> HoldItems(HoldGroup g) =>
        _systems.Hold.All().Where(x => x.count > 0 && GroupOf(x.id) == g).Select(x => x.id).OrderBy(i => (int)i).ToList();

    private string HoldHeader()
    {
        var h = _systems.Hold;
        float f = h.Weight / (float)Mathf.Max(1, h.Capacity);
        int n = Mathf.Clamp(Mathf.RoundToInt(f * 12f), 0, 12);
        string sits = f > 0.9f ? "riding low" : f > 0.6f ? "heavy" : f > 0.2f ? "trim" : "light";
        return $"{h.Weight}/{h.Capacity} [{new string('#', n)}{new string('.', 12 - n)}] {sits}";
    }

    private List<(string, string, string)> HoldList(HoldGroup g)
    {
        var list = new List<(string, string, string)>();
        foreach (var id in HoldItems(g))
        {
            var info = Items.Info[id];
            int count = _systems.Hold.Count(id);
            string where = $"x{count}  ·  weight {info.Weight} each, {info.Weight * count} in all";
            list.Add(($"{info.Name} x{count}", where, info.Description + "\n\n" + UsesOf(id)));
        }
        return list;
    }

    /// <summary>What an item is for: where to take it. Lab findings that are still a surprise stay quiet.</summary>
    private string UsesOf(ItemId id)
    {
        var uses = new List<string>();
        var info = Items.Info[id];
        if (info.Food > 0f) uses.Add($"Food, +{info.Food:0}. Eat it here (SPACE) or at the galley.");
        foreach (var r in Recipes.All)
            if (r.Cost.Any(c => c.id == id))
                uses.Add($"{(r.Station == Station.Galley ? "Galley" : "Workbench")}: {r.Verb.ToLowerInvariant()} {Items.Name(r.Output).ToLowerInvariant()} ({Recipes.CostText(r)}).");
        foreach (var m in Modules.All)
            if (_modules.Known.Contains(m.Id) && m.Cost.Any(c => c.id == id))
                uses.Add($"Workbench: fabricate the {m.Name} module.");
        foreach (var w in Wear.All)
            if (w.Fix.Any(f => f.id == id && f.n > 0))
                uses.Add($"Upkeep: {w.FixVerb.ToLowerInvariant()} ({w.Room.ToLowerInvariant()}).");
        if (Lab.Analysable(id))
            uses.Add(_lab.Done.Contains(id) ? "Lab: studied. The notes are in your journal." : "Lab: not studied yet. Put one under the scope (engineering).");
        if (Modules.ByItem(id) != null) uses.Add("Engineering: fit it at the module rack.");
        switch (id)
        {
            case ItemId.PowerCell: uses.Add("Engineering panel: install it for +40 power."); break;
            case ItemId.PatchKit: uses.Add("Engineering panel: weld a hull patch (+30 hull)."); break;
            case ItemId.Medkit: uses.Add("Press H to use one, wherever you are."); break;
            case ItemId.LureFlare: uses.Add("At the helm, press B to release one astern."); break;
            case ItemId.MultiTool: uses.Add("Needed to weld breaches."); break;
        }
        if (uses.Count == 0) uses.Add("Nothing on the boat takes it. Somebody might want it.");
        return "WHAT IT'S FOR\n" + string.Join("\n", uses.Select(u => "· " + u));
    }

    // ------------------------------------------------------------------ open, use, close

    private void OpenHold()
    {
        _mode = Mode.Terminal;
        _holdOpen = true;
        _jettisonArmed = null;
        _player.InputEnabled = false;
        _hud.SetCrosshair(false);
        _hud.SetPrompt("");
        bool paper = _player.InStation;   // away from the boat it's your notes about what's aboard
        _holdScreen.SetPaper(paper);
        _holdScreen.Title = paper ? "HOLD · WHAT'S ABOARD" : "HOLD · MANIFEST";
        _holdScreen.Hint = paper
            ? "A/D page   W/S item   SPACE eat   I / ESC close"
            : "A/D page   W/S item   SPACE eat   X overboard   I / ESC close";
        _holdScreen.SetTab(0);
        _audio.PlayAt("pickup", 0f, 0.3f);
        Input.MouseMode = Input.MouseModeEnum.Visible;
        _holdVp.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        _hud.ShowTerminal(_holdVp.GetTexture(), crt: !paper);
    }

    /// <summary>Hold keys (from UpdateTerminal). Returns true if it closed.</summary>
    private bool UpdateHold(float dt)
    {
        _jettisonT -= dt;
        if (_jettisonT <= 0f) _jettisonArmed = null;
        if (Input.IsActionJustPressed(GameInput.Inventory)) { CloseTerminal(); return true; }
        var items = HoldItems((HoldGroup)_holdScreen.Current);
        ItemId? sel = items.Count > 0 ? items[Mathf.Clamp(_holdScreen.Selected, 0, items.Count - 1)] : null;
        if (sel is not ItemId id) return false;
        if (Input.IsActionJustPressed(GameInput.Jump))   // SPACE
        {
            if (Items.Info[id].Food > 0f) { _systems.Eat(id); _audio.PlayBlip(220f, 0.2f, 0.3f); }
            else _hud.Message($"You can't eat the {Items.Name(id).ToLowerInvariant()}.", 2f);
        }
        if (Input.IsActionJustPressed(GameInput.CenterControls) && !_player.InStation)   // X (the trash lock is on the boat)
        {
            if (_jettisonArmed == id)
            {
                _systems.Hold.Remove(id);
                _jettisonArmed = null;
                _audio.PlayAt("airlock", 0f, 0.5f);
                _hud.Message($"One {Items.Name(id).ToLowerInvariant()} goes out through the trash lock. The sea has it now.", 4f);
                Log($"Jettisoned: {Items.Name(id).ToLowerInvariant()}.");
            }
            else
            {
                _jettisonArmed = id; _jettisonT = 2.5f;
                _hud.Message($"Throw one {Items.Name(id).ToLowerInvariant()} overboard? Press X again.", 2.5f);
                _audio.PlayBlip(500f, 0.05f, 0.3f);
            }
        }
        return false;
    }
}
