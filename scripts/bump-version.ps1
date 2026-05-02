param(
    [string]$ManifestPath = "manifest.json",
    [string]$PluginPath = "ShoutRunner\\ShoutRunner.json",
    [string]$ProjectPath = "ShoutRunner\\ShoutRunner.csproj"
)

$manifestContent = Get-Content $ManifestPath -Raw | ConvertFrom-Json
if ($manifestContent -isnot [System.Array]) {
    $manifest = @($manifestContent)
} else {
    $manifest = $manifestContent
}

if ($manifest.Count -lt 1) {
    throw "Manifest is empty."
}

$version = [Version]$manifest[0].AssemblyVersion
$newVersion = "{0}.{1}.{2}.{3}" -f $version.Major, $version.Minor, $version.Build, ($version.Revision + 1)
$epoch = [int][DateTimeOffset]::UtcNow.ToUnixTimeSeconds()

$manifest[0].AssemblyVersion = $newVersion
$manifest[0].LastUpdate = $epoch
$manifest | ConvertTo-Json -Depth 10 | Set-Content $ManifestPath

$plugin = Get-Content $PluginPath -Raw | ConvertFrom-Json
$plugin.AssemblyVersion = $newVersion
$plugin | ConvertTo-Json -Depth 10 | Set-Content $PluginPath

$project = Get-Content $ProjectPath -Raw
$project = [System.Text.RegularExpressions.Regex]::Replace($project, "<Version>.*?</Version>", "<Version>$newVersion</Version>")
$project = [System.Text.RegularExpressions.Regex]::Replace($project, "<AssemblyVersion>.*?</AssemblyVersion>", "<AssemblyVersion>$newVersion</AssemblyVersion>")
$project = [System.Text.RegularExpressions.Regex]::Replace($project, "<FileVersion>.*?</FileVersion>", "<FileVersion>$newVersion</FileVersion>")
Set-Content $ProjectPath $project

Write-Output "$newVersion $epoch"
