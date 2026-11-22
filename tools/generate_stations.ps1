# Bakes Solitude's deep-sea station interiors with NoorLevel's CLI.
#
# Solitude never links NoorLevel's code (studio rule: projects connect through files).
# Build the CLI from github.com/badsadcode/BadLevel (NoorLevel/src/NoorLevel.Cli) and pass
# its path. Output, per station: assets/stations/<name>.glb + <name>.recipe.json (the exact
# command), and Scripts/Stations/StationBank.g.cs listing every station the game may use.
#
#   ./tools/generate_stations.ps1 -Cli path\to\noorlevel-cli.exe            # bake missing ones
#   ./tools/generate_stations.ps1 -Cli path\to\noorlevel-cli.exe -Force     # rebake everything
#
# Existing GLBs are NEVER regenerated without -Force: saved games remember what was taken
# where, so a station's geometry must not change under a player's feet.
#
# Station rules for the generator (all families):
#   - underground: entered from the top (the docking airlock), deepest point = the core
#   - no lifts / ladders / spiral stairs yet; stairs and ramps only
#   - no windows, skylights, open yards, hazard pools, chasms or floods (500 m under water;
#     Solitude restyles only nl_floor / nl_wall / nl_ceiling / nl_door / nl_secret)
#   - a keycard door; usually a secret room

param(
    [Parameter(Mandatory = $true)][string]$Cli,
    [string]$Out = (Join-Path $PSScriptRoot "..\assets\stations"),
    [switch]$Force
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force $Out | Out-Null

$common = @(
    "--dial", "openness=0", "--set", "underground=true",
    "--set", "connector_weights.lift=0", "--set", "connector_weights.ladder=0", "--set", "connector_weights.spiral=0",
    "--set", "ramps=1", "--set", "windows=0", "--set", "skylights=0", "--set", "open_areas=0",
    "--set", "hazards=0", "--set", "chasms=0", "--set", "floods=0", "--set", "keys=1",
    "--off", "windows,landmark",
    "--format", "glb", "--json-result", "--max-ms", "30000"
)

# Families: name prefix, preset, seeds, and per-seed extra settings (cycled).
# research_01..08 and outpost_01..04 are the original bank: same commands as when first baked.
$families = @(
    @{ Name = "research"; Preset = "facility"; Seeds = 1..16; Starter = $true; Variants = @(
        @("--set", "map_size=48", "--set", "storeys=2"),
        @("--set", "map_size=56", "--set", "storeys=2"),
        @("--set", "map_size=56", "--set", "storeys=2"),
        @("--set", "map_size=64", "--set", "storeys=3"),
        @("--set", "map_size=48", "--set", "storeys=2"),
        @("--set", "map_size=64", "--set", "storeys=2"),
        @("--set", "map_size=56", "--set", "storeys=3"),
        @("--set", "map_size=48", "--set", "storeys=2"),
        @("--set", "map_size=52", "--set", "storeys=2", "--set", "composition=hub"),
        @("--set", "map_size=60", "--set", "storeys=3", "--set", "composition=central"),
        @("--set", "map_size=48", "--set", "storeys=2", "--set", "composition=linear"),
        @("--set", "map_size=68", "--set", "storeys=3", "--set", "composition=tree"))
    },
    @{ Name = "outpost"; Preset = "bunker"; Seeds = 1..10; Variants = @(
        @("--set", "map_size=40", "--set", "storeys=2"),
        @("--set", "map_size=40", "--set", "storeys=2"),
        @("--set", "map_size=44", "--set", "storeys=2"),
        @("--set", "map_size=36", "--set", "storeys=2"),
        @("--set", "map_size=48", "--set", "storeys=3"),
        @("--set", "map_size=40", "--set", "storeys=2", "--set", "composition=hub"))
    },
    # Crew habitats: a handful of tight rooms. Quick to search, nowhere to run.
    @{ Name = "habitat"; Preset = "compact"; Seeds = 1..10; Variants = @(
        @("--set", "map_size=36", "--set", "storeys=2"),
        @("--set", "map_size=40", "--set", "storeys=2"),
        @("--set", "map_size=32", "--set", "storeys=1"),
        @("--set", "map_size=40", "--set", "storeys=2", "--set", "arch_style=bunker"))
    },
    # Mining and processing works: big halls, catwalks, heavy industrial walls.
    @{ Name = "works"; Preset = "industrial"; Seeds = 1..10; Variants = @(
        @("--set", "map_size=56", "--set", "storeys=2", "--set", "arch_style=industrial"),
        @("--set", "map_size=64", "--set", "storeys=2", "--set", "arch_style=industrial"),
        @("--set", "map_size=56", "--set", "storeys=3", "--set", "arch_style=industrial", "--set", "composition=linear"))
    },
    # Deep labs: the facility again, but newer and stranger (sci-fi walls, central plans).
    @{ Name = "deeplab"; Preset = "facility"; Seeds = 21..30; Variants = @(
        @("--set", "map_size=56", "--set", "storeys=3", "--set", "arch_style=scifi", "--set", "composition=central"),
        @("--set", "map_size=64", "--set", "storeys=2", "--set", "arch_style=scifi", "--set", "composition=hub"),
        @("--set", "map_size=48", "--set", "storeys=2", "--set", "arch_style=scifi"))
    },
    # Single-deck stations: one level, angled walls, height from steps and platforms.
    @{ Name = "deck"; Preset = "techbase"; Seeds = 1..8; Variants = @(
        @("--set", "map_size=48", "--set", "storeys=1"),
        @("--set", "map_size=56", "--set", "storeys=1"))
    }
)

$allowed = @("nl_floor", "nl_wall", "nl_ceiling", "nl_door", "nl_secret", "nl_trigger", "nl_sky")

function Test-Station([string]$path) {
    # Read the GLB's JSON chunk: markers Solitude needs, and only materials it knows.
    $bytes = [IO.File]::ReadAllBytes($path)
    $len = [BitConverter]::ToInt32($bytes, 12)
    $js = [Text.Encoding]::UTF8.GetString($bytes, 20, $len) | ConvertFrom-Json
    $kinds = @($js.nodes | Where-Object { $_.extras -and $_.extras.kind } | ForEach-Object { $_.extras.kind })
    $mats = @($js.materials | ForEach-Object { $_.name })
    $problems = @()
    foreach ($k in "player_start", "exit") { if ($kinds -notcontains $k) { $problems += "no $k" } }
    foreach ($m in $mats) { if ($allowed -notcontains $m) { $problems += "material $m" } }
    return $problems
}

$bank = New-Object System.Collections.Generic.List[string]
$starters = New-Object System.Collections.Generic.List[string]
$made = 0; $kept = 0; $rejected = 0
foreach ($f in $families) {
    $i = 0
    foreach ($seed in $f.Seeds) {
        $i++
        $name = "{0}_{1:00}" -f $f.Name, $i
        $glb = Join-Path $Out "$name.glb"
        $extra = $f.Variants[($i - 1) % $f.Variants.Count]
        if ((Test-Path $glb) -and -not $Force) {
            $kept++
        } else {
            $cliArgs = @("generate", "--preset", $f.Preset, "--seed", "$seed") + $common + $extra + @("--out", $glb)
            $result = (& $Cli @cliArgs | Select-Object -Last 1) | ConvertFrom-Json
            if (-not $result.ok) { Write-Warning "$name failed: $($result.errors -join '; ')"; $rejected++; continue }
            $recipe = [ordered]@{ tool = "NoorLevel"; preset = $f.Preset; seed = $seed; family = $f.Name; command = "noorlevel-cli " + ($cliArgs -join " ") }
            $recipe | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $Out "$name.recipe.json")
            Write-Host ("{0,-12} {1,5} ms  {2} rooms" -f $name, $result.ms, $result.rooms)
            $made++
        }
        $problems = Test-Station $glb
        if ($problems.Count -gt 0) {
            Write-Warning "$name rejected: $($problems -join ', ')"
            Remove-Item $glb, (Join-Path $Out "$name.recipe.json") -ErrorAction SilentlyContinue
            $rejected++
            continue
        }
        $bank.Add($name)
        # The starter keeps the original eight, so an existing world's first station doesn't move.
        if ($f.Starter -and $i -le 8) { $starters.Add($name) }
    }
}

# The game's list of stations (generated; don't edit by hand).
$cs = @"
// GENERATED by tools/generate_stations.ps1. Do not edit by hand: rerun the script.
namespace Solitude.Stations;

public static class StationBank
{
    /// <summary>Every baked station blockout in assets/stations (name without .glb).</summary>
    public static readonly string[] All =
    {
$(($bank | ForEach-Object { "        `"$_`"," }) -join "`n")
    };

    /// <summary>Layouts the first (starter) station may use: research stations.</summary>
    public static readonly string[] Starter =
    {
$(($starters | ForEach-Object { "        `"$_`"," }) -join "`n")
    };
}
"@
$csPath = Join-Path $PSScriptRoot "..\Scripts\Stations\StationBank.g.cs"
Set-Content -Encoding utf8 $csPath $cs
Write-Host "Done: $($bank.Count) stations ($made baked, $kept kept, $rejected rejected). Wrote $csPath"
