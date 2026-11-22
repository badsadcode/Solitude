using System;
using System.Collections.Generic;
using System.Linq;

namespace Solitude.Systems;

/// <summary>What a finished analysis gives the boat (beyond the field note).</summary>
public enum Discovery { None, LureFlares, SiltForecast, FastSonar, RedFloods, RichIntake, NerveMap }

/// <summary>
/// THE SAMPLE LAB (an idea from BALE IX). One sample at a time goes under the scope; the analysis
/// runs on its own (minutes, a trickle of power) while you do other things. Each kind of sample is
/// analysed once: you get a field note in the journal, and some give the boat something to use.
/// Pure data; Main drives the bench, the panel and the effects. Saved.
/// </summary>
public sealed class Lab
{
    public sealed record Study(ItemId Item, float Seconds, string Title, string Note, Discovery Gives = Discovery.None, string GivesText = null);

    // The field notes are written by whoever runs the lab: you. Dry, a little uneasy.
    public static readonly Study[] All =
    {
        new(ItemId.Lumen, 90f, "Lumen pod: light that waits",
            "The glow isn't constant. It brightens when the lab lights go off and dims when they come on, as if it's answering. Under the scope the cells are packed with something like a battery, charged and patient.\n\nIt keeps glowing for hours after it's cut. Bound in sealant it would make a decent flare. Something that hunts by light would find it very interesting.",
            Discovery.LureFlares, "LURE FLARES: prepare them here (lumen pod + bio-sealant). Release one from the helm with B."),
        new(ItemId.Sediment, 75f, "Sediment: layers",
            "Graded bedding: coarse at the bottom, fine at the top, over and over. Every band is one slope letting go upstream. The bands are thin, close together, recent.\n\nThe fine fraction has a signature on the hydrophone before it arrives. I can tune for it.",
            Discovery.SiltForecast, "SILT FORECAST: the hydrophone now hears storms much earlier."),
        new(ItemId.Crystal, 110f, "Lattice crystal: it rings",
            "Tap it and it rings for eleven seconds. Ping it and it rings back at exactly the sonar frequency, a little louder than it was pinged. It stores sound the way the lumen stores light.\n\nA sliver in the transducer housing should let the sonar recharge faster.",
            Discovery.FastSonar, "SONAR: recharges faster."),
        new(ItemId.Plankton, 60f, "Plankton: copper",
            "The copper smell is real: their blood is copper-based, like old Earth crabs. They swarm toward warmth and away from noise.\n\nIf the intake runs a little warmer it will pull in more of them.",
            Discovery.RichIntake, "INTAKE: collects faster."),
        new(ItemId.Nodule, 80f, "Manganese nodule: rings of years",
            "Cut in half, it has growth rings like a tree. Four million years in this one, if the rate is right. In the outermost millimetre there's a thin dark band that doesn't belong: fine metal dust, iron and something else. Station-grade alloy.\n\nThe stations have been shedding into the sea for a long time. Or something has been taking them apart."),
        new(ItemId.Clam, 50f, "Vent clam: tenants",
            "There's something living inside the clam that isn't the clam. A thread-thin worm, coiled around the gut. It doesn't seem to do any harm. It doesn't seem to do anything. It turns to face the lamp.\n\nI cooked the rest of them very thoroughly."),
        new(ItemId.TissueRavager, 100f, "Ravager tissue: wiring",
            "Its nerves are thick and bundled in a single cord down the back, like a cable run. One good jolt to that cord and the whole animal stops.\n\nThe shock prod should hold it much longer now that I know where to put it.",
            Discovery.NerveMap, "SHOCK PROD: stuns ravagers for much longer."),
        new(ItemId.TissueRay, 70f, "Crystal ray: what the light is for",
            "The glow along the wing edges isn't for seeing. It's for being seen. Each ray flashes in a pattern, and the pattern repeats. They count to each other.\n\nI listened to the Sounding again afterwards. Different numbers. Same idea."),
        new(ItemId.ScaleWyrm, 120f, "Fangwyrm scale: old",
            "The scale has the same growth rings as the nodules. Fewer, but the same rate. The animal this came from is older than the stations, much older than the colony.\n\nThere are bite marks on the scale's edge. Something bit the Fangwyrm and the Fangwyrm survived it. I would like to not meet what made them."),
        new(ItemId.PlateGloam, 140f, "Plate fragment: blind to red",
            "Under the plate there are eyes. Hundreds of tiny ones, set into the armour itself, all over the body. Their pigment is tuned to blue-green, the colour light turns down here. They're almost blind to deep red.\n\nThe floods could be refitted to run red: you'd see less, and it would see you hardly at all.",
            Discovery.RedFloods, "FLOODS: L now cycles off, white and red. Red shows you less, and shows you less to whatever is watching."),
    };

    public static Study Of(ItemId id) => All.FirstOrDefault(s => s.Item == id);
    public static bool Analysable(ItemId id) => Of(id) != null;

    public readonly HashSet<ItemId> Done = new();
    public ItemId? Running { get; private set; }
    public float Left { get; private set; }
    public float Progress => Running is ItemId r ? 1f - Left / Of(r).Seconds : 0f;

    public event Action<Study> Finished;

    public bool Has(Discovery d) => d != Discovery.None && Done.Any(i => Of(i)?.Gives == d);

    public void Start(ItemId id)
    {
        if (Running != null || !Analysable(id)) return;
        Running = id;
        Left = Of(id).Seconds;
    }

    /// <summary>Advance the running analysis. powered = false pauses it.</summary>
    public void Update(float dt, bool powered)
    {
        if (Running is not ItemId r || !powered) return;
        Left -= dt;
        if (Left > 0f) return;
        Running = null;
        Left = 0f;
        Done.Add(r);
        Finished?.Invoke(Of(r));
    }

    // ---- save
    public List<string> Save()
    {
        var l = Done.Select(d => d.ToString()).ToList();
        if (Running is ItemId r) l.Add($"RUN|{r}|{Left.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        return l;
    }

    public void Load(List<string> l)
    {
        Done.Clear(); Running = null; Left = 0f;
        if (l == null) return;
        foreach (var s in l)
        {
            if (s.StartsWith("RUN|"))
            {
                var p = s.Split('|');
                if (p.Length == 3 && Enum.TryParse<ItemId>(p[1], out var id) && Analysable(id))
                {
                    Running = id;
                    Left = float.TryParse(p[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : Of(id).Seconds;
                }
            }
            else if (Enum.TryParse<ItemId>(s, out var d)) Done.Add(d);
        }
    }
}
