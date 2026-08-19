#!/usr/bin/env pwsh
<#
.SYNOPSIS
    End-to-end smoke test against a running WorkLedger stack (plans/S5, acceptance criterion 3).

.DESCRIPTION
    Exercises the deployed surface the way two real devices would: register, push a batch, pull it from
    a second device, conflict, and report. This is deliberately not a unit test -- the test suite
    already covers the merge and counting rules against Testcontainers. What only this can catch is the
    deployment itself: image, container config, compose wiring, migrations-on-boot, and the fact that a
    token minted by the container is accepted by the container.

    Every step asserts; the first failure stops the run with a non-zero exit code, because each step
    depends on the one before it.

    The data it writes is real and stays in the database: one project named "Smoke test <timestamp>"
    with one task and two time entries, in the single v1 account. Nothing is cleaned up (a delete would
    only tombstone it anyway), so run it against a deployment you do not mind seeding -- or drop the
    volume afterwards with `docker compose down -v`.

.PARAMETER BaseUrl
    Where the API is. Defaults to http://localhost:<API_PORT from .env, else 8080>.

.PARAMETER BootstrapSecret
    The X-Bootstrap-Secret value. Defaults to Bootstrap__Secret from .env, then from the environment.

.PARAMETER Up
    Run `docker compose up -d --build` first, and wait longer for the API to come up.

.PARAMETER Down
    Run `docker compose down` at the end. The volume, and therefore the data, survives.

.EXAMPLE
    pwsh ./scripts/smoke-test.ps1 -Up

.EXAMPLE
    pwsh ./scripts/smoke-test.ps1 -BaseUrl https://workledger.example.com -BootstrapSecret $secret
#>
[CmdletBinding()]
param(
    [string]$BaseUrl,
    [string]$BootstrapSecret,
    [switch]$Up,
    [switch]$Down
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$composeFile = Join-Path $repoRoot 'docker-compose.yml'

function Read-DotEnv {
    param([string]$Path)

    $values = @{}
    if (-not (Test-Path $Path)) { return $values }

    foreach ($line in Get-Content -LiteralPath $Path) {
        $trimmed = $line.Trim()
        if ($trimmed -eq '' -or $trimmed.StartsWith('#')) { continue }
        $split = $trimmed.IndexOf('=')
        if ($split -lt 1) { continue }
        $values[$trimmed.Substring(0, $split).Trim()] = $trimmed.Substring($split + 1).Trim()
    }
    return $values
}

$dotEnv = Read-DotEnv (Join-Path $repoRoot '.env')

if (-not $BaseUrl) {
    $port = if ($dotEnv.ContainsKey('API_PORT')) { $dotEnv['API_PORT'] } else { '8080' }
    $BaseUrl = "http://localhost:$port"
}
$BaseUrl = $BaseUrl.TrimEnd('/')

if (-not $BootstrapSecret) {
    $BootstrapSecret = if ($dotEnv.ContainsKey('Bootstrap__Secret')) { $dotEnv['Bootstrap__Secret'] } else { $env:Bootstrap__Secret }
}
if (-not $BootstrapSecret) {
    throw 'No bootstrap secret. Pass -BootstrapSecret, or set Bootstrap__Secret in .env (copy .env.example).'
}

# --- plumbing ------------------------------------------------------------------------------------

$script:step = 0
function Start-Step {
    param([string]$Name)
    $script:step++
    Write-Host ("[{0}] {1}" -f $script:step, $Name) -ForegroundColor Cyan
}

function Confirm-That {
    param([string]$What, [bool]$Condition, [string]$Detail = '')
    if (-not $Condition) {
        throw "$What$(if ($Detail) { " -- $Detail" })"
    }
    Write-Host "      ok  $What" -ForegroundColor DarkGray
}

function Invoke-Api {
    param(
        [string]$Method = 'GET',
        [Parameter(Mandatory)][string]$Path,
        $Body,
        [string]$Token,
        [hashtable]$Headers = @{},
        [switch]$RawText
    )

    $request = @{
        Method             = $Method
        Uri                = "$BaseUrl$Path"
        Headers            = $Headers.Clone()
        SkipHttpErrorCheck = $true
        # Nothing here should ever hang; a stuck request is a failure, not something to wait out.
        TimeoutSec         = 30
    }
    if ($Token) { $request.Headers['Authorization'] = "Bearer $Token" }
    if ($null -ne $Body) {
        $request.Body = ($Body | ConvertTo-Json -Depth 12)
        $request.ContentType = 'application/json'
    }

    $response = Invoke-WebRequest @request
    $text = [string]$response.Content
    $isJson = $response.Headers.ContainsKey('Content-Type') -and ($response.Headers['Content-Type'] -match 'application/json')
    return @{
        Status = [int]$response.StatusCode
        Text   = $text
        Body   = if (-not $RawText -and $isJson -and $text) { $text | ConvertFrom-Json } else { $null }
    }
}

# PayloadJson is PascalCase on purpose. The contract reads it case-insensitively, but the real client
# writes PascalCase (see SyncJson), and a smoke test that sends what the client would not send is
# testing the wrong thing.
function New-Change {
    param(
        [Parameter(Mandatory)][string]$EntityType,
        [Parameter(Mandatory)][string]$EntityId,
        [Parameter(Mandatory)][string]$Operation,
        [Parameter(Mandatory)][hashtable]$Payload,
        [Parameter(Mandatory)][int]$Version,
        [Parameter(Mandatory)][string]$OriginDeviceId
    )

    return @{
        EntityType     = $EntityType
        EntityId       = $EntityId
        Operation      = $Operation
        PayloadJson    = ($Payload | ConvertTo-Json -Depth 8 -Compress)
        Version        = $Version
        OriginDeviceId = $OriginDeviceId
        CreatedAt      = (Get-Date).ToUniversalTime().ToString('o')
    }
}

function Utc {
    param([datetime]$Value)
    return $Value.ToString('yyyy-MM-ddTHH:mm:ssZ')
}

if ($Up) {
    Start-Step 'docker compose up --detach --build'
    docker compose --file $composeFile up --detach --build
    if ($LASTEXITCODE -ne 0) { throw 'docker compose up failed.' }
}

$exitCode = 0

try {
    # --- the deployment is serving ---------------------------------------------------------------
    Start-Step "waiting for $BaseUrl/health"
    $timeout = if ($Up) { 180 } else { 60 }
    $deadline = (Get-Date).AddSeconds($timeout)
    $healthy = $false
    while (-not $healthy -and (Get-Date) -lt $deadline) {
        try {
            $health = Invoke-Api -Path '/health'
            $healthy = ($health.Status -eq 200) -and ($health.Body.status -eq 'ok')
        } catch {
            $healthy = $false
        }
        if (-not $healthy) { Start-Sleep -Seconds 2 }
    }
    Confirm-That '/health returns 200 {"status":"ok"}' $healthy "no healthy response within ${timeout}s -- check: docker compose logs api"

    $root = Invoke-Api -Path '/'
    Confirm-That 'GET / identifies the build' (($root.Status -eq 200) -and ($root.Body.name -eq 'WorkLedger.Server'))
    Write-Host "      running version $($root.Body.version)" -ForegroundColor DarkGray

    # --- everything else is closed by default ----------------------------------------------------
    Start-Step 'unauthenticated requests are rejected'
    Confirm-That '/sync/pull without a token is 401' ((Invoke-Api -Path '/sync/pull').Status -eq 401)
    Confirm-That '/reports/weekly without a token is 401' ((Invoke-Api -Path '/reports/weekly').Status -eq 401)
    $badSecret = Invoke-Api -Method POST -Path '/auth/register-device' -Body @{ DeviceId = 'smoke-nope' } -Headers @{ 'X-Bootstrap-Secret' = 'wrong-secret' }
    Confirm-That 'register-device with a wrong bootstrap secret is 401' ($badSecret.Status -eq 401)

    # --- two devices enrol -----------------------------------------------------------------------
    Start-Step 'registering two devices'
    $stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
    $deviceA = "smoke-a-$stamp"
    $deviceB = "smoke-b-$stamp"

    $tokens = @{}
    foreach ($device in @($deviceA, $deviceB)) {
        $registered = Invoke-Api -Method POST -Path '/auth/register-device' -Headers @{ 'X-Bootstrap-Secret' = $BootstrapSecret } -Body @{ DeviceId = $device; Label = "smoke test $device" }
        Confirm-That "$device got a token" (($registered.Status -eq 200) -and [bool]$registered.Body.token) "status $($registered.Status): $($registered.Text)"
        $tokens[$device] = $registered.Body.token
    }

    $whoami = Invoke-Api -Path '/auth/whoami' -Token $tokens[$deviceA]
    Confirm-That '/auth/whoami echoes the calling device' (($whoami.Status -eq 200) -and ($whoami.Body.deviceId -eq $deviceA))

    # --- device A pushes project -> task -> two entries -------------------------------------------
    Start-Step 'device A pushes a project, a task and two time entries'
    $projectId = [guid]::NewGuid().ToString()
    $taskId = [guid]::NewGuid().ToString()
    $entryId = [guid]::NewGuid().ToString()
    $runningId = [guid]::NewGuid().ToString()
    $projectName = "Smoke test $stamp"

    # An hour ending now, so the entry is finished and lands inside one UTC day even at 00:30.
    $entryEnd = (Get-Date).ToUniversalTime()
    $entryStart = $entryEnd.AddSeconds(-3600)
    $createdAt = Utc $entryStart

    $changes = @(
        (New-Change -EntityType 'project' -EntityId $projectId -Operation 'create' -Version 1 -OriginDeviceId $deviceA -Payload @{
            SyncId = $projectId; Name = $projectName; ProjectCode = 'SMOKE'; Description = 'Written by scripts/smoke-test.ps1'
            CreatedAt = $createdAt; UpdatedAt = $null; Version = 1; OriginDeviceId = $deviceA
        }),
        (New-Change -EntityType 'task' -EntityId $taskId -Operation 'create' -Version 1 -OriginDeviceId $deviceA -Payload @{
            SyncId = $taskId; ProjectSyncId = $projectId; Name = 'Smoke task'; Notes = $null; IsCompleted = $false; IsArchived = $false
            CreatedAt = $createdAt; UpdatedAt = $null; Version = 1; OriginDeviceId = $deviceA
        }),
        (New-Change -EntityType 'timeentry' -EntityId $entryId -Operation 'create' -Version 1 -OriginDeviceId $deviceA -Payload @{
            SyncId = $entryId; TaskSyncId = $taskId; StartTime = (Utc $entryStart); EndTime = (Utc $entryEnd); DurationSeconds = 3600
            CreatedAt = $createdAt; UpdatedAt = $null; Version = 1; OriginDeviceId = $deviceA
        }),
        # Deliberately still running: excluded from every total, counted separately.
        (New-Change -EntityType 'timeentry' -EntityId $runningId -Operation 'create' -Version 1 -OriginDeviceId $deviceA -Payload @{
            SyncId = $runningId; TaskSyncId = $taskId; StartTime = (Utc $entryEnd); EndTime = $null; DurationSeconds = $null
            CreatedAt = $createdAt; UpdatedAt = $null; Version = 1; OriginDeviceId = $deviceA
        })
    )

    $push = Invoke-Api -Method POST -Path '/sync/push' -Token $tokens[$deviceA] -Body @{ Changes = $changes }
    Confirm-That 'push returns 200' ($push.Status -eq 200) "status $($push.Status): $($push.Text)"
    Confirm-That 'one result per change, in request order' (($push.Body.results.Count -eq 4) -and ($push.Body.results[0].entityId -eq $projectId))
    $refused = @($push.Body.results | Where-Object { -not $_.accepted })
    Confirm-That 'every change was accepted' ($refused.Count -eq 0) ($refused | ConvertTo-Json -Depth 5 -Compress)
    $cursorAfterPush = $push.Body.serverTime

    # --- device B pulls what A pushed ------------------------------------------------------------
    Start-Step 'device B pulls (no cursor -- a device that has never synced)'
    $pull = Invoke-Api -Path '/sync/pull' -Token $tokens[$deviceB]
    Confirm-That 'pull returns 200' ($pull.Status -eq 200) "status $($pull.Status): $($pull.Text)"

    $pulledProject = $pull.Body.changes | Where-Object { $_.entityId -eq $projectId }
    $pulledTask = $pull.Body.changes | Where-Object { $_.entityId -eq $taskId }
    $pulledEntry = $pull.Body.changes | Where-Object { $_.entityId -eq $entryId }
    Confirm-That 'B receives the project A pushed' (($null -ne $pulledProject) -and ($pulledProject.version -eq 1))
    Confirm-That 'B receives the task' ($null -ne $pulledTask)
    Confirm-That 'B receives the finished time entry' ($null -ne $pulledEntry)
    Confirm-That 'the payload survived the round trip' ((($pulledProject.payloadJson | ConvertFrom-Json).Name) -eq $projectName) $pulledProject.payloadJson

    $echo = Invoke-Api -Path '/sync/pull' -Token $tokens[$deviceA]
    Confirm-That 'A is not sent its own writes back' (@($echo.Body.changes | Where-Object { $_.entityId -eq $projectId }).Count -eq 0)

    # --- B edits it, A picks the edit up ---------------------------------------------------------
    Start-Step 'device B renames the project; device A pulls the change'
    $renamed = "$projectName (renamed by B)"
    $bPush = Invoke-Api -Method POST -Path '/sync/push' -Token $tokens[$deviceB] -Body @{
        Changes = @(
            (New-Change -EntityType 'project' -EntityId $projectId -Operation 'update' -Version 2 -OriginDeviceId $deviceB -Payload @{
                SyncId = $projectId; Name = $renamed; ProjectCode = 'SMOKE'; Description = 'Edited on the second device'
                CreatedAt = $createdAt; UpdatedAt = (Utc (Get-Date).ToUniversalTime()); Version = 2; OriginDeviceId = $deviceB
            })
        )
    }
    Confirm-That "B's higher version wins" ($bPush.Body.results[0].accepted -and ($bPush.Body.results[0].authoritativeVersion -eq 2)) $bPush.Text

    $aPull = Invoke-Api -Path "/sync/pull?since=$([uri]::EscapeDataString($cursorAfterPush))" -Token $tokens[$deviceA]
    $aProject = $aPull.Body.changes | Where-Object { $_.entityId -eq $projectId }
    Confirm-That "A's incremental pull sees version 2" (($null -ne $aProject) -and ($aProject.version -eq 2))
    Confirm-That "A's pull carries B's name" ((($aProject.payloadJson | ConvertFrom-Json).Name) -eq $renamed)

    # --- the conflict path (rule B, and WL-98's AuthoritativeOperation) --------------------------
    Start-Step 'device A pushes a stale edit and is handed the winner'
    $stale = Invoke-Api -Method POST -Path '/sync/push' -Token $tokens[$deviceA] -Body @{
        Changes = @(
            (New-Change -EntityType 'project' -EntityId $projectId -Operation 'update' -Version 2 -OriginDeviceId $deviceA -Payload @{
                SyncId = $projectId; Name = 'Written by a device that had not pulled yet'; ProjectCode = 'SMOKE'; Description = $null
                CreatedAt = $createdAt; UpdatedAt = (Utc (Get-Date).ToUniversalTime()); Version = 2; OriginDeviceId = $deviceA
            })
        )
    }
    $conflict = $stale.Body.results[0]
    Confirm-That 'the stale change is rejected' (-not $conflict.accepted) $stale.Text
    Confirm-That 'the server hands back its own version' ($conflict.authoritativeVersion -eq 2)
    Confirm-That 'the winner is self-describing (authoritativeOperation)' ($conflict.authoritativeOperation -eq 'update')
    Confirm-That 'the winning payload is the row B wrote' ((($conflict.authoritativePayloadJson | ConvertFrom-Json).Name) -eq $renamed)

    # --- reports over the same data --------------------------------------------------------------
    Start-Step 'reports agree with what was pushed'
    $projectReport = Invoke-Api -Path "/reports/projects/$projectId" -Token $tokens[$deviceB]
    Confirm-That 'project report returns 200' ($projectReport.Status -eq 200) $projectReport.Text
    Confirm-That 'project total is the one finished hour' ($projectReport.Body.totalSeconds -eq 3600) "got $($projectReport.Body.totalSeconds)"
    Confirm-That 'the running entry is counted, not totalled' ($projectReport.Body.runningEntries -eq 1)
    Confirm-That 'the report shows the renamed project' ($projectReport.Body.projectName -eq $renamed)
    Confirm-That 'the task appears with its hour' (@($projectReport.Body.tasks | Where-Object { ($_.taskSyncId -eq $taskId) -and ($_.seconds -eq 3600) }).Count -eq 1)

    $weekOf = $entryStart.ToString('yyyy-MM-dd')
    $weekly = Invoke-Api -Path "/reports/weekly?weekOf=$weekOf" -Token $tokens[$deviceA]
    Confirm-That 'weekly report returns 200' ($weekly.Status -eq 200) $weekly.Text
    Confirm-That 'weekly report has seven zero-filled days' ($weekly.Body.days.Count -eq 7)
    Confirm-That 'weekly report includes this project' (@($weekly.Body.projects | Where-Object { $_.projectSyncId -eq $projectId }).Count -eq 1)
    Confirm-That 'weekly total covers the pushed hour' ($weekly.Body.totalSeconds -ge 3600)

    $taskReport = Invoke-Api -Path "/reports/tasks/$taskId" -Token $tokens[$deviceA]
    Confirm-That 'task report totals the hour' (($taskReport.Status -eq 200) -and ($taskReport.Body.totalSeconds -eq 3600)) $taskReport.Text

    $csv = Invoke-Api -Path "/reports/weekly?weekOf=$weekOf&format=csv" -Token $tokens[$deviceA] -RawText
    $csvLines = @(($csv.Text -split "`r?`n") | Where-Object { $_ -ne '' })
    Confirm-That 'CSV export has the documented header' ($csvLines[0] -eq 'date,project,task,seconds') $csvLines[0]
    Confirm-That 'CSV export contains the pushed row' (@($csvLines | Where-Object { $_ -like '*renamed by B*' }).Count -ge 1)

    Start-Step 'reports reject what they should'
    Confirm-That 'unknown project SyncId is 404' ((Invoke-Api -Path "/reports/projects/$([guid]::NewGuid())" -Token $tokens[$deviceA]).Status -eq 404)
    Confirm-That 'unknown format is 400' ((Invoke-Api -Path '/reports/weekly?format=xlsx' -Token $tokens[$deviceA]).Status -eq 400)
    Confirm-That 'from after to is 400' ((Invoke-Api -Path "/reports/projects/$projectId`?from=2026-03-05&to=2026-03-01" -Token $tokens[$deviceA]).Status -eq 400)

    Write-Host ''
    Write-Host "SMOKE TEST PASSED against $BaseUrl" -ForegroundColor Green
    Write-Host "      project $projectId is now in the account; docker compose down -v drops it" -ForegroundColor DarkGray
} catch {
    Write-Host ''
    Write-Host "SMOKE TEST FAILED: $($_.Exception.Message)" -ForegroundColor Red
    $exitCode = 1
} finally {
    if ($Down) {
        Start-Step 'docker compose down'
        docker compose --file $composeFile down
    }
}

exit $exitCode
