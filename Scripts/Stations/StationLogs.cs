using System;
using System.Collections.Generic;

namespace Solitude.Stations;

/// <summary>
/// Crew logs found aboard stations. Each station gets a small cast and a timeline; logs are
/// ordered so that the more you find, the worse it gets. Deterministic per station seed.
/// </summary>
public static class StationLogs
{
    private static readonly string[] First = { "Ada", "Marek", "Ilse", "Tomas", "Yuki", "Bram", "Nadia", "Oren", "Sabine", "Kofi", "Lena", "Piotr", "Ines", "Rafe" };
    private static readonly string[] Last = { "Varga", "Okafor", "Lindqvist", "Moreau", "Hale", "Sato", "Brandt", "Kowal", "Duarte", "Reyes", "Ivers", "Nwosu" };
    private static readonly string[] Roles = { "Station chief", "Hydrologist", "Systems tech", "Medic", "Geochemist", "Diver", "Cook", "Sonar operator" };

    private static readonly string[][] Arc =
    {
        new[]
        {
            "Day {d}. Pressure seals holding. {b} says the vents are richer than the survey promised. Celebrated with the last of the real coffee.",
            "Day {d}. Resupply sub is late. Not worried yet. {b} keeps tapping the hull and listening. Says it's habit.",
            "Day {d}. Lattice crystals from the east ridge are warm to the touch. Warmer at night. We don't have a night down here.",
        },
        new[]
        {
            "Day {d}. Sonar shows a return at 400 m that wasn't there yesterday. Large. {b} logged it as a fault. I didn't argue.",
            "Day {d}. Something has been at the intake grilles. Scrape marks on the inside of the filter housing. Inside.",
            "Day {d}. {b} went out to clear the grilles and came back very quiet. Won't say what the keel camera showed. I deleted nothing. The tape is just gone.",
        },
        new[]
        {
            "Day {d}. We hear them in the ducts now. Clicking. Like someone counting under their breath. {b} sealed the lower corridor with the red card.",
            "Day {d}. Lights failing deck by deck. They don't like the light, or they like the dark. I can't tell which anymore.",
            "Day {d}. {b} is gone. The door was locked from the inside. There is a smell like copper and wet stone.",
        },
        new[]
        {
            "Day {d}. If you are reading this you docked. Don't stay. Take what you need from the core and go back to your boat. Keep your floods on.",
            "Day {d}. I am in the core with the map. It's warm here. Something is breathing on the other side of the bulkhead and it is very patient.",
            "Day {d}. Whoever finds this: the thing at 400 m is not the problem. The problem is that it is not alone.",
        },
    };

    public sealed record Log(string Title, string Body);

    /// <summary>Build the station's logs, ordered early → late. count = how many are placed.</summary>
    public static List<Log> Build(string station, int seed, int count)
    {
        var rng = new Random(seed ^ 0x5A17);
        var crew = new List<(string name, string role)>();
        for (int i = 0; i < 4; i++)
            crew.Add(($"{First[rng.Next(First.Length)]} {Last[rng.Next(Last.Length)]}", Roles[rng.Next(Roles.Length)]));
        int day = 40 + rng.Next(200);
        var logs = new List<Log>();
        for (int i = 0; i < count; i++)
        {
            int stage = Math.Min(Arc.Length - 1, i * Arc.Length / Math.Max(1, count));
            if (i == count - 1) stage = Arc.Length - 1;
            var pool = Arc[stage];
            var (author, role) = crew[rng.Next(crew.Count)];
            var other = crew[rng.Next(crew.Count)].name.Split(' ')[0];
            day += 2 + rng.Next(9);
            string body = pool[rng.Next(pool.Length)].Replace("{d}", day.ToString()).Replace("{b}", other);
            logs.Add(new Log($"{station} — {role} {author}", body));
        }
        return logs;
    }
}
