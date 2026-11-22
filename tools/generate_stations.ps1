# Regenerates Solitude's deep-sea station interiors with NoorLevel's CLI.
#
# Solitude never links NoorLevel's code (studio rule: projects connect through files).
# Build the CLI from github.com/badsadcode/BadLevel (NoorLevel/src/NoorLevel.Cli) and pass
# its path. Output: assets/stations/<name>.glb + <name>.recipe.json (the exact command).
#
#   ./tools/generate_stations.ps1 -Cli path\to\noorlevel-cli.exe
#
# Station rules for the generator:
#   - underground: entered from the top (the docking airlock), deepest point = the core
#   - no lifts / ladders / spiral stairs yet (moving platforms come later); ramps over stairs
#   - no windows, skylights or open yards (we're 500 m under water)
#   - one keycard door and a secret room, for exploration

param(
    [Parameter(Mandatory = $true)][string]$Cli,
    [string]$Out = (Join-Path $PSScriptRoot "..\assets\stations")
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force $Out | Out-Null
$tmp = Join-Path ([IO.Path]::GetTempPath()) "solitude_stations"
New-Item -ItemType Directory -Force $tmp | Out-Null

$common = @(
    "--dial", "openness=0",
    "--set", "connector_weights.lift=0", "--set", "connector_weights.ladder=0", "--set", "connector_weights.spiral=0",
    "--set", "ramps=1", "--set", "windows=0", "--set", "skylights=0", "--set", "keys=1",
    "--off", "windows,landmark",
    "--format", "glb"
)

# name, preset, seed, extra args
$bank = @(
    @("research_01", "facility", 1, @("--set", "map_size=48", "--set", "storeys=2")),
    @("research_02", "facility", 2, @("--set", "map_size=56", "--set", "storeys=2")),
    @("research_03", "facility", 3, @("--set", "map_size=56", "--set", "storeys=2")),
    @("research_04", "facility", 4, @("--set", "map_size=64", "--set", "storeys=3")),
    @("research_05", "facility", 5, @("--set", "map_size=48", "--set", "storeys=2")),
    @("research_06", "facility", 6, @("--set", "map_size=64", "--set", "storeys=2")),
    @("research_07", "facility", 7, @("--set", "map_size=56", "--set", "storeys=3")),
    @("research_08", "facility", 8, @("--set", "map_size=48", "--set", "storeys=2")),
    @("outpost_01", "bunker", 1, @("--set", "map_size=40", "--set", "storeys=2")),
    @("outpost_02", "bunker", 2, @("--set", "map_size=40", "--set", "storeys=2")),
    @("outpost_03", "bunker", 3, @("--set", "map_size=44", "--set", "storeys=2")),
    @("outpost_04", "bunker", 4, @("--set", "map_size=36", "--set", "storeys=2"))
)

foreach ($b in $bank) {
    $name, $preset, $seed, $extra = $b
    $args = @("generate", "--preset", $preset, "--seed", "$seed") + $common + $extra + @("--out", $tmp)
    & $Cli @args | Write-Host
    $glb = Get-ChildItem $tmp -Filter "noorlevel_$seed.glb" | Select-Object -First 1
    Move-Item -Force $glb.FullName (Join-Path $Out "$name.glb")
    $recipe = [ordered]@{
        tool = "NoorLevel"; preset = $preset; seed = $seed; command = "noorlevel-cli " + ($args -join " ")
    }
    $recipe | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $Out "$name.recipe.json")
}
Write-Host "Done: $($bank.Count) stations in $Out"
