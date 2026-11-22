using System;
using System.Collections.Generic;
using Godot;
using Solitude.Core;
using Solitude.World;

namespace Solitude.Systems;

public enum WreckKind { SupplySub, CargoString, SurveyDrone, Capsule }

/// <summary>A wreck on the seabed. Everything about it follows from the seed and its cell.</summary>
public sealed class WreckSite
{
    public long Id;
    public WreckKind Kind;
    public string Name;
    public Vector3 Position;     // on the seabed
    public float Yaw;
    public int Seed;
    /// <summary>Its distress pinger still runs (until someone pulls the recorder).</summary>
    public bool Pinger;
    /// <summary>A wrong one. The details are in the recorder.</summary>
    public bool Wrong;
    /// <summary>Which voiced crew this is (supply subs, capsules, drones: see tools/voice/solitude_voice_script.json).</summary>
    public int Variant;
    /// <summary>Voice line id prefix: "sub2", "cap0", "drone1".</summary>
    public string VoiceKey => Kind switch { WreckKind.SupplySub => $"sub{Variant}", WreckKind.Capsule => $"cap{Variant}", WreckKind.SurveyDrone => $"drone{Variant}", _ => null };
    public bool HasRecorder => Kind != WreckKind.CargoString;
    /// <summary>Salvage id space: 1&lt;&lt;62 | wreck id &lt;&lt; 6 | slot (slot 0 = the recorder).</summary>
    public long SalvageId(int slot) => (1L << 62) | (Id << 6) | (long)slot;
}

/// <summary>
/// WRECKS AND DISTRESS CALLS. Wrecks are scattered over the deep seabed on a 1.4 km grid (about half
/// the cells have one), never on top of a station, the Origin, or the start. Deterministic per seed,
/// generated lazily around wherever you are. Also writes the voices on their loops and their
/// recorders' last entries.
/// </summary>
public sealed class WreckDirectory
{
    public const float Cell = 1400f;
    private readonly int _seed;
    private readonly TerrainGenerator _gen;
    private readonly Func<Vector3, bool> _blocked;
    private readonly Dictionary<Vector2I, WreckSite> _cells = new();   // null value = no wreck here

    private static readonly string[] SubNames = { "HALDANE", "MERIDIAN BELL", "OKONKWO", "PATIENCE", "GREY HERON", "TALLY", "ST. ELMO", "KITTIWAKE", "LODESTAR", "BRAVE DORA" };
    /// <summary>Capsule variant -> the number its voice gives ("Capsule two", "Capsule four", ...).</summary>
    private static readonly int[] CapsuleNumbers = { 2, 4, 1, 3 };
    private static readonly string[] Crew = { "Ada", "Marek", "Ilse", "Tomas", "Yuki", "Bram", "Nadia", "Oren", "Sabine", "Kofi", "Lena", "Piotr", "Ines", "Rafe" };

    /// <param name="blocked">true where no wreck may lie (stations, the Origin, the start)</param>
    public WreckDirectory(int seed, TerrainGenerator gen, Func<Vector3, bool> blocked)
    {
        _seed = seed; _gen = gen; _blocked = blocked;
    }

    public IEnumerable<WreckSite> Near(Vector3 p, float radius)
    {
        int r = Mathf.CeilToInt(radius / Cell);
        int cx = Mathf.FloorToInt(p.X / Cell), cz = Mathf.FloorToInt(p.Z / Cell);
        for (int z = -r; z <= r; z++)
            for (int x = -r; x <= r; x++)
            {
                var w = At(new Vector2I(cx + x, cz + z));
                if (w != null && new Vector2(w.Position.X - p.X, w.Position.Z - p.Z).Length() <= radius) yield return w;
            }
    }

    public WreckSite ById(long id)
    {
        var c = new Vector2I((int)(id >> 24) - 0x8000, (int)((id >> 4) & 0xFFFFF) - 0x80000);
        var w = At(c);
        return w != null && w.Id == id ? w : null;
    }

    private WreckSite At(Vector2I c)
    {
        if (_cells.TryGetValue(c, out var w)) return w;
        w = Make(c);
        _cells[c] = w;
        return w;
    }

    private WreckSite Make(Vector2I c)
    {
        var rng = new Random(HashCode.Combine(_seed, c.X * 7919, c.Y * 104729, 0x5EA));
        if (rng.NextDouble() > 0.5) return null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            float x = (c.X + 0.15f + 0.7f * (float)rng.NextDouble()) * Cell;
            float z = (c.Y + 0.15f + 0.7f * (float)rng.NextDouble()) * Cell;
            float y = _gen.Height(x, z);
            var pos = new Vector3(x, y, z);
            if (y > -150f || _blocked(pos)) continue;
            var kind = (WreckKind)(rng.Next(100) switch { < 30 => 0, < 55 => 1, < 80 => 2, _ => 3 });
            var w = new WreckSite
            {
                Id = ((long)(c.X + 0x8000) << 24) | ((long)(c.Y + 0x80000) << 4),
                Kind = kind, Position = pos, Yaw = (float)rng.NextDouble() * Mathf.Tau, Seed = rng.Next(),
                Pinger = kind != WreckKind.CargoString && rng.NextDouble() < 0.7,
            };
            w.Wrong = w.Pinger && rng.NextDouble() < 0.09;
            // The voiced crews: the name has to match what the voice on the loop says.
            var subs = VoiceLines.SubNames.Count > 0 ? (IReadOnlyList<string>)VoiceLines.SubNames : SubNames;
            w.Variant = kind switch
            {
                WreckKind.SupplySub => rng.Next(subs.Count),
                WreckKind.Capsule => rng.Next(Math.Max(1, VoiceLines.Capsules)),
                WreckKind.SurveyDrone => rng.Next(Math.Max(1, VoiceLines.Drones)),
                _ => 0,
            };
            w.Name = kind switch
            {
                WreckKind.SupplySub => $"SUPPLY SUB {subs[w.Variant]}",
                WreckKind.CargoString => $"CARGO STRING {rng.Next(2, 40)}",
                WreckKind.SurveyDrone => $"SURVEY DRONE SD-{rng.Next(10, 99)}",
                _ => $"ESCAPE CAPSULE {CapsuleNumbers[w.Variant % CapsuleNumbers.Length]}",
            };
            return w;
        }
        return null;
    }

    // ------------------------------------------------------------------ voices and recorders

    /// <summary>What the comms set picks up when the pinger is first heard (the loop the crew left running).</summary>
    public static string Loop(WreckSite w)
    {
        if (!w.Wrong && w.VoiceKey != null && VoiceLines.Get(w.VoiceKey + "_loop") is { } vl)
            return w.Kind == WreckKind.SurveyDrone ? $"COMMS: a synthetic voice, repeating: \"{vl.Caption}\"" : $"COMMS: a voice loop, very faint: \"{vl.Caption}\"";
        var r = new Random(w.Seed);
        string who = Crew[r.Next(Crew.Length)];
        if (w.Wrong) return "COMMS: a voice loop, very faint. It's a man's voice, tired, calm. It's giving your boat's heading. Then your depth. Then it starts again.";
        return w.Kind switch
        {
            WreckKind.SupplySub => $"COMMS: a voice loop, very faint: \"...{w.Name.Replace("SUPPLY SUB ", "")}, holed aft, settling. Three aboard. {who} is hurt. If anyone hears this—\" It starts again.",
            WreckKind.Capsule => $"COMMS: a voice loop, very faint: \"Capsule {w.Name[^1]}, two aboard, air for— ...if you're the relief sub, we can hear you. We can hear you.\" It starts again.",
            WreckKind.SurveyDrone => "COMMS: a data burst, repeating: SURVEY DRONE / FAULT / AWAITING RECOVERY. Its clock says it has been waiting for a long time.",
            _ => null,
        };
    }

    /// <summary>The recorder's last entries (copied into the journal when the diver pulls it).</summary>
    /// <summary>The voiced recorder entries for this wreck (empty if none / the wrong ones).</summary>
    public static List<VoiceLines.Line> RecorderLines(WreckSite w)
    {
        var l = new List<VoiceLines.Line>();
        if (w.Wrong || w.VoiceKey == null) return l;
        for (int i = 1; i <= 6; i++) if (VoiceLines.Get($"{w.VoiceKey}_rec{i}") is { } line) l.Add(line);
        return l;
    }

    public static (string title, string body) Recorder(WreckSite w)
    {
        var voiced = RecorderLines(w);
        if (voiced.Count > 0) return ($"{w.Name}: recorder", string.Join("\n\n", voiced.ConvertAll(v => v.Caption)));
        var r = new Random(w.Seed + 1);
        string a = Crew[r.Next(Crew.Length)], b = Crew[(r.Next(Crew.Length - 1) + 1 + Array.IndexOf(Crew, a)) % Crew.Length];
        int depth = Mathf.RoundToInt(-w.Position.Y);
        if (w.Wrong)
            return ($"{w.Name}: recorder",
                "The casing is furred with growth, older than anything else on this seabed. The recorder still plays.\n\n" +
                "It's your boat's log. Your voice. The same flat lines the ship's log terminal writes, in the same order, up to today.\n\n" +
                "Then it keeps going.\n\nThe last entry is short. The date on it is a few days from now.");
        return w.Kind switch
        {
            WreckKind.SupplySub => ($"{w.Name}: recorder",
                $"Day 1. Loaded at the shelf: stores for three stations, mail, a crate of real coffee somebody bribed us for.\n\n" +
                $"Day 3. {a} says the current's taking us east of the lane. Correcting.\n\n" +
                $"Day 3. Something hit the stern. Not a rock. Rocks don't come back.\n\n" +
                $"Day 3. Holed aft. Settling at {depth} m. {b} has the pinger running. We're fine. We're fine. Someone will come down the lane."),
            WreckKind.Capsule => ($"{w.Name}: recorder",
                $"We left the station when the lights went the wrong colour. {a} says we did the right thing.\n\n" +
                $"Hour 9. Air is fine. Pinger is fine. Something circled us twice and left.\n\n" +
                $"Hour 31. {a} is asleep. I keep hearing counting through the hull. I've started counting back.\n\n" +
                "Hour 40. I've stopped."),
            WreckKind.SurveyDrone => ($"{w.Name}: recorder",
                "SURVEY LOG (automatic). Transect complete. Bathymetry uploaded. Anomalous return at the end of the line: large, warm, moving against the current.\n\n" +
                "Collision. Pressure housing compromised. Entering low-power mode. Pinger active. Awaiting recovery.\n\n" +
                "Awaiting recovery.\n\nAwaiting recovery."),
            _ => ($"{w.Name}", ""),
        };
    }

    /// <summary>What lies in and around it for the diver to take (slot 1..n; slot 0 is the recorder).</summary>
    public static List<ItemId> Salvage(WreckSite w)
    {
        var r = new Random(w.Seed + 2);
        var l = new List<ItemId>();
        ItemId[] table = w.Kind switch
        {
            WreckKind.SupplySub => new[] { ItemId.CannedFood, ItemId.CannedFood, ItemId.Medkit, ItemId.PowerCell, ItemId.Valve, ItemId.Servo, ItemId.MetalPlate, ItemId.StationAlloy },
            WreckKind.CargoString => new[] { ItemId.CannedFood, ItemId.CannedFood, ItemId.CannedFood, ItemId.MetalPlate, ItemId.PatchKit, ItemId.Coil, ItemId.Impeller, ItemId.PowerCell },
            WreckKind.SurveyDrone => new[] { ItemId.Optics, ItemId.LogicBoard, ItemId.Coil, ItemId.PowerCell },
            _ => new[] { ItemId.Medkit, ItemId.CannedFood, ItemId.PatchKit },
        };
        int n = w.Kind switch { WreckKind.CargoString => 4 + r.Next(3), WreckKind.SupplySub => 3 + r.Next(3), _ => 2 + r.Next(2) };
        for (int i = 0; i < n; i++) l.Add(table[r.Next(table.Length)]);
        return l;
    }
}
