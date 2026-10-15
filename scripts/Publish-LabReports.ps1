[CmdletBinding()]
param([Parameter(Mandatory)][string]$RunDirectory)
$ErrorActionPreference = 'Stop'
$localRootForNext = Join-Path $env:LOCALAPPDATA 'RadioTEDU\OnAirNext-Dev'
$runsRootForNext = [IO.Path]::GetFullPath((Join-Path $localRootForNext 'runs'))
$resolvedRunForNext = (Resolve-Path -LiteralPath $RunDirectory).ProviderPath
if (-not $resolvedRunForNext.StartsWith($runsRootForNext + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Only isolated synthetic lab runs can be published by this milestone.'
}
$runNameForNext = Split-Path $resolvedRunForNext -Leaf
if ($runNameForNext -notmatch '^\d{8}-\d{6}-[0-9a-f]{8}$') { throw 'Unexpected lab identity.' }
$resultsPathForNext = Join-Path $resolvedRunForNext 'results.json'
$resultsForNext = Get-Content -LiteralPath $resultsPathForNext -Raw | ConvertFrom-Json
if (@($resultsForNext.Results | Where-Object { -not $_.Passed }).Count -gt 0) { throw 'This helper archives successful synthetic report fixtures only. Failed test evidence stays local and in the source evidence archive.' }
$accountForNext = gh api user --jq .login
if ($LASTEXITCODE -ne 0 -or $accountForNext -ne 'akgularda') { throw 'Expected active GitHub account akgularda. No account switching is performed.' }
$repositoryForNext = 'akgularda/radiotedu-broadcast-reports'
$metadataForNext = gh repo view $repositoryForNext --json isPrivate,defaultBranchRef | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or -not $metadataForNext.isPrivate) { throw 'The designated report archive must be private.' }
$checkoutForNext = Join-Path $localRootForNext 'report-archive'
if (-not (Test-Path -LiteralPath $checkoutForNext)) {
    gh repo clone $repositoryForNext $checkoutForNext
    if ($LASTEXITCODE -ne 0) { throw 'Report archive clone failed.' }
}
$remoteForNext = git -C $checkoutForNext remote get-url origin
if ($LASTEXITCODE -ne 0 -or $remoteForNext -notin @('https://github.com/akgularda/radiotedu-broadcast-reports.git','git@github.com:akgularda/radiotedu-broadcast-reports.git')) {
    throw 'Existing local checkout has an unexpected remote; it was not modified.'
}
if (git -C $checkoutForNext status --porcelain) { throw 'Existing report checkout contains changes; preserve them before retrying.' }
$branchForNext = $metadataForNext.defaultBranchRef.name
git -C $checkoutForNext pull --ff-only origin $branchForNext
if ($LASTEXITCODE -ne 0) { throw 'Report archive could not fast-forward; no force operation is permitted.' }
$relativeFolderForNext = 'development-tests/' + $runNameForNext
$destinationForNext = Join-Path $checkoutForNext $relativeFolderForNext
New-Item -ItemType Directory -Path $destinationForNext -Force | Out-Null
Copy-Item -LiteralPath $resultsPathForNext -Destination (Join-Path $destinationForNext 'results.json')
foreach ($caseForNext in $resultsForNext.Results) {
    if ($caseForNext.Name -notmatch '^[a-z]+$') { throw 'Unexpected case name.' }
    $sourceReportsForNext = Join-Path $resolvedRunForNext ($caseForNext.Name + '\reports')
    if (Test-Path -LiteralPath $sourceReportsForNext) {
        $caseFolderForNext = Join-Path $destinationForNext $caseForNext.Name
        New-Item -ItemType Directory -Path $caseFolderForNext -Force | Out-Null
        Get-ChildItem -LiteralPath $sourceReportsForNext -File | Where-Object { $_.Name -match '^\d{4}-\d{2}-\d{2}(-hours\.csv|\.csv|\.manifest\.json)$' } | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $caseFolderForNext $_.Name)
        }
    }
}
$publishedFilesForNext = @(Get-ChildItem -LiteralPath $destinationForNext -Recurse -File | Where-Object Name -ne 'archive-manifest.json' | Sort-Object FullName | ForEach-Object {
    [ordered]@{ Path = [IO.Path]::GetRelativePath($destinationForNext, $_.FullName).Replace('\','/'); Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$archiveManifestForNext = [ordered]@{ SchemaVersion = 1; Environment = 'synthetic-development'; IsSynthetic = $true;
    Run = $runNameForNext; RecordedAtUtc = $resultsForNext.CreatedAtUtc; Files = $publishedFilesForNext;
    Notice = 'Generated tone fixtures and simulated broadcasts. Do not include these in annual broadcast submissions.' }
$archiveManifestForNext | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $destinationForNext 'archive-manifest.json') -Encoding utf8NoBOM
git -C $checkoutForNext add -- $relativeFolderForNext
if ($LASTEXITCODE -ne 0) { throw 'Report staging failed.' }
git -C $checkoutForNext diff --cached --quiet -- $relativeFolderForNext
if ($LASTEXITCODE -eq 1) {
    git -C $checkoutForNext -c user.name=akgularda -c user.email=akgularda@users.noreply.github.com commit --quiet -m "test: archive synthetic engine reports $runNameForNext" -- $relativeFolderForNext
    if ($LASTEXITCODE -ne 0) { throw 'Report commit failed; local data remains available.' }
}
elseif ($LASTEXITCODE -ne 0) { throw 'Could not check report changes.' }
git -C $checkoutForNext push origin ('HEAD:' + $branchForNext)
if ($LASTEXITCODE -ne 0) { throw 'Report push failed; local report and commit remain available for retry.' }
$localCommitForNext = git -C $checkoutForNext rev-parse HEAD
$remoteCommitForNext = gh api ('repos/' + $repositoryForNext + '/commits/' + $branchForNext) --jq .sha
if ($LASTEXITCODE -ne 0 -or $remoteCommitForNext -ne $localCommitForNext) { throw 'Could not verify the archived commit on GitHub.' }
Write-Output "Verified synthetic report archive: https://github.com/$repositoryForNext/tree/$localCommitForNext/$relativeFolderForNext"
