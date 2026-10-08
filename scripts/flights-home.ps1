# Daily flight search from a home connection, so Wizz Air answers: it refuses the data-centre
# network GitHub Actions runs on. Writes the same flights/{event}_{origins} documents as the cloud
# job, which then keeps these Wizz fares instead of asking Wizz itself (WIZZ=reuse in sync.yml).
#
# Needs: .NET 10 SDK, GitHub CLI signed in (for the mirror), and the Firebase service-account key at
# %USERPROFILE%\.wcs-events\service-account.json. Register the daily task with
# scripts\install-flights-task.ps1. Log: %USERPROFILE%\.wcs-events\flights.log

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$home_ = Join-Path $env:USERPROFILE ".wcs-events"
$key = Join-Path $home_ "service-account.json"
$work = Join-Path $home_ "mirror"
$log = Join-Path $home_ "flights.log"

New-Item -ItemType Directory -Force $work | Out-Null
Start-Transcript -Path $log -Append | Out-Null
try {
    if (-not (Test-Path $key)) { throw "Service-account key missing: $key" }

    # The flight search reads the events from the sync's mirror; the cloud job owns it, this only reads.
    & gh release download mirror --repo Lucroth/wcs-events-planner --pattern "wcs-events.db.gz" --dir $work --clobber
    if ($LASTEXITCODE -ne 0) { throw "Mirror download failed" }
    $gz = Join-Path $work "wcs-events.db.gz"
    $db = Join-Path $work "wcs-events.db"
    $in = [IO.File]::OpenRead($gz)
    $out = [IO.File]::Create($db)
    try { (New-Object IO.Compression.GZipStream($in, [IO.Compression.CompressionMode]::Decompress)).CopyTo($out) }
    finally { $out.Dispose(); $in.Dispose() }

    $env:FIREBASE_PROJECT_ID = "wcs-events-planner"
    $env:GOOGLE_APPLICATION_CREDENTIALS = $key
    $env:ConnectionStrings__Db = "Data Source=$db"
    Remove-Item Env:\WIZZ -ErrorAction SilentlyContinue

    & dotnet run --project (Join-Path $repo "WcsEvents.Sync") -c Release -- flights
    if ($LASTEXITCODE -ne 0) { throw "Flight search failed with exit code $LASTEXITCODE" }
}
finally {
    Stop-Transcript | Out-Null
}
