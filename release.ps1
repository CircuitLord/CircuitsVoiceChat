# Tag and publish a Circuit's Voice Chat release to GitHub.
# Usage: .\release.ps1
# Version is read from the MelonInfo attribute in CircuitsVoiceChatMod.cs.
# Requires: dotnet, git, gh (authenticated via `gh auth login`).
$ErrorActionPreference = "Stop"

$RepoDir = $PSScriptRoot
$Csproj = Join-Path $RepoDir "CircuitsVoiceChat.csproj"

$Version = Get-ChildItem $RepoDir -Filter *.cs |
    Select-String -Pattern 'MelonInfo' |
    ForEach-Object { [regex]::Match($_.Line, '"(\d+\.\d+\.\d+)"').Groups[1].Value } |
    Where-Object { $_ } |
    Select-Object -First 1

if (-not $Version) {
    Write-Error "error: no MelonInfo version found in $RepoDir"
}

$Tag = "v$Version"
Write-Host "Releasing CircuitsVoiceChat $Tag"

if (git -C $RepoDir tag --list $Tag) {
    Write-Error "error: tag $Tag already exists (bump the MelonInfo version first)"
}

$Dirty = git -C $RepoDir status --porcelain
if ($Dirty) {
    Write-Error "error: uncommitted changes, commit them first"
}

dotnet build $Csproj -c Release
if ($LASTEXITCODE -ne 0) {
    Write-Error "error: build failed"
}

$ModDll = Join-Path $RepoDir "bin\Release\CircuitsVoiceChat.dll"
$ConcentusDll = Join-Path $RepoDir "bin\Release\Concentus.dll"
foreach ($f in @($ModDll, $ConcentusDll)) {
    if (-not (Test-Path $f)) {
        Write-Error "error: build output $f not found"
    }
}

$AssetDir = Join-Path ([IO.Path]::GetTempPath()) "release-CircuitsVoiceChat-$Tag-$(Get-Random)"
New-Item -ItemType Directory -Path $AssetDir | Out-Null
try {
    Copy-Item $ModDll (Join-Path $AssetDir "CircuitsVoiceChat-$Tag.dll")
    Copy-Item $ConcentusDll (Join-Path $AssetDir "Concentus.dll")
    Copy-Item (Join-Path $RepoDir "THIRD-PARTY-LICENSES.txt") $AssetDir

    $Zip = Join-Path $AssetDir "CircuitsVoiceChat-$Tag.zip"
    Compress-Archive -Path (Join-Path $AssetDir "*") -DestinationPath $Zip

    git -C $RepoDir tag $Tag
    if ($LASTEXITCODE -ne 0) { Write-Error "error: git tag failed" }
    git -C $RepoDir push origin $Tag
    if ($LASTEXITCODE -ne 0) { Write-Error "error: git push failed" }

    $RemoteUrl = git -C $RepoDir remote get-url origin
    $RepoSlug = [regex]::Match($RemoteUrl, 'github\.com[:/](.+?)(\.git)?$').Groups[1].Value

    $Notes = gh api "repos/$RepoSlug/releases/generate-notes" -f tag_name=$Tag --jq .body
    if ($LASTEXITCODE -ne 0) { Write-Error "error: generating release notes failed" }
    $Notes = ($Notes -join "`n") -replace '\*\*Full Changelog\*\*', 'Full changelog'

    gh release create $Tag $Zip --repo $RepoSlug --title "Circuit's Voice Chat $Tag" --notes $Notes
    if ($LASTEXITCODE -ne 0) { Write-Error "error: gh release create failed" }
}
finally {
    Remove-Item -Recurse -Force $AssetDir
}

Write-Host "Done: $Tag published"
