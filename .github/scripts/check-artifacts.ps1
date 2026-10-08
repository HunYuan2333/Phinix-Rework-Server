param([switch]$IncludeClient)

$ErrorActionPreference = 'Stop'
$server = 'Server/bin/Release/net10.0'
$required = @(
    "$server/PhinixServer.dll",
    "$server/PhinixServer.deps.json",
    "$server/PhinixServer.runtimeconfig.json",
    "$server/Extensions/ChatExtension.dll",
    "$server/Extensions/ChatExtension.Server.dll",
    "$server/Extensions/TradeExtension.dll",
    "$server/Extensions/TradeExtension.Server.dll"
)
$artifactRoots = @($server)
$compositionRuntimeNames = @(
    "Phinix.ClientComposition", "Autofac", "Microsoft.Bcl.AsyncInterfaces",
    "System.Diagnostics.DiagnosticSource", "System.Memory", "System.Runtime.CompilerServices.Unsafe",
    "System.Buffers", "System.Numerics.Vectors", "System.Threading.Tasks.Extensions"
)
if ($IncludeClient) {
    $client = 'Output/phinix-rework'
    $artifactRoots += $client
    $required += @($compositionRuntimeNames | ForEach-Object { "$client/Common/Assemblies/$_.dll" })
    $required += @(
        "$client/Common/Assemblies/Stateless.dll",
        "$client/About/About.xml",
        "$client/LoadFolders.xml",
        "$client/1.6/Assemblies/13-PhinixClient.dll",
        "$client/Common/Assemblies/10-ClientExtensionAbstractions.dll",
        "$client/Common/Extensions/08-ChatExtension.dll",
        "$client/Common/Extensions/09-TradeExtension.dll",
        "$client/Common/Extensions/10-LegacyAdapter.Client.dll",
        "$client/Common/Extensions/10-InventoryExtension.dll",
        "$client/Common/Extensions/11-ChatExtension.Client.dll",
        "$client/Common/Extensions/11-InventoryExtension.Client.dll",
        "$client/Common/Extensions/12-TradeExtension.Client.dll",
        "$client/Common/Extensions/17-PluginStore.Client.dll",
        "$client/Languages/English/Keyed/PluginStore.xml",
        "$client/Languages/ChineseSimplified (简体中文)/Keyed/PluginStore.xml"
    )
}
foreach ($path in $required) {
    if (!(Test-Path $path -PathType Leaf)) { throw "Missing build artifact: $path" }
}
if ($IncludeClient) {
    # Composition owns one copy of each runtime asset. Compare actual build bytes;
    # filenames and a successful host compile alone do not prove coordinated upgrades.
    foreach ($name in $compositionRuntimeNames) {
        $copies = @(Get-ChildItem $client -Recurse -File -Filter "$name.dll")
        if ($copies.Count -ne 1) { throw "Expected one host-owned runtime asset: $name" }
        $source = "Client/Composition/bin/Release/net472/$name.dll"
        if (!(Test-Path $source -PathType Leaf)) { throw "Missing composition build asset: $source" }
        if ((Get-FileHash $source -Algorithm SHA256).Hash -ne (Get-FileHash $copies[0].FullName -Algorithm SHA256).Hash) {
            throw "Packaged composition asset differs from build: $name"
        }
    }
    $stateCopies = @(Get-ChildItem $client -Recurse -File -Filter "Stateless.dll")
    if ($stateCopies.Count -ne 1) { throw 'Expected one Stateless runtime asset' }
    $stateSource = 'Extensions/PluginStore/Client/bin/Release/net472/Stateless.dll'
    if ((Get-FileHash $stateSource -Algorithm SHA256).Hash -ne (Get-FileHash $stateCopies[0].FullName -Algorithm SHA256).Hash) {
        throw 'Packaged Stateless asset differs from Store build'
    }
    # Optional business plugins must never reappear in a main distribution.
    $retiredFiles = Get-ChildItem $client -Recurse -File | Where-Object {
        $_.Name -match '^(?:[0-9]+-)?Legacy(?:RedPacket|TalentTrade)Extension(?:\.Client)?(?:\.dll(?:\.localization\.json)?|\.pdb|\.xml)$' -or
        $_.FullName -match '[\\/]Resources[\\/]Legacy(?:RedPacket|TalentTrade)[\\/]'
    }
    if ($retiredFiles) { throw "Optional plugins found in main client artifacts: $($retiredFiles.FullName -join ', ')" }
    [xml]$loadFolders = Get-Content "$client/LoadFolders.xml" -Raw
    $folders = @($loadFolders.loadFolders.'v1.6'.li)
    if (($folders -join ',') -ne '/,Common,1.6') { throw 'Unexpected RimWorld 1.6 load folders' }
}
# Game reference assemblies are compiler inputs, never part of a distributable mod/server.
$gameDlls = Get-ChildItem $artifactRoots -Recurse -File | Where-Object {
    $_.Name -like 'Assembly-CSharp*.dll' -or $_.Name -like 'Unity*.dll' -or $_.Name -eq 'mscorlib.dll' -or $_.Name -eq 'com.rlabrecque.steamworks.net.dll'
}
if ($gameDlls) { throw "Game reference assemblies found in artifacts: $($gameDlls.FullName -join ', ')" }
Write-Host 'Required host and extension artifacts are present; no game reference assemblies are packaged.'
