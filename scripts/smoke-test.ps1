#!/usr/bin/env pwsh
<#
.SYNOPSIS
    End-to-end smoke test against a running WorkLedger stack (plans/S5, reworked for user accounts in
    plans/S9).

.DESCRIPTION
    Exercises the deployed surface the way a real person with two devices would: read the server's
    capabilities, register an account, sign in from two devices, push a batch, pull it on the second
    device, conflict, report, list the devices, and delete the account again. This is deliberately not
    a unit test -- the test suite already covers the merge rules, the counting rules and the auth rules
    against Testcontainers. What only this can catch is the deployment itself: image, container config,
    compose wiring, migrations-on-boot, and the fact that a token minted by the container is accepted
    by the container.

    If the server advertises a web portal (S11: capabilities' portalUrl, set when Web__Enabled is on),
    it checks the image serves the sign-in page with its Content-Security-Policy and its stylesheet;
    with the portal off it checks /portal is not a route at all. The portal's flows are the test
    suite's job.

    It then does the S10 multi-user leg IF IT CAN ADMINISTER THE TARGET: issue an invite, register a
    second account with that token and no shared secret, and assert the two accounts cannot see each
    other's rows. "If it can" is load-bearing -- on a fresh volume the throwaway account is the first
    user and so an admin; on a deployment that already has one, this script is a stranger and skips
    the leg rather than promoting itself into somebody else's server. It never demotes or deletes an
    account it did not create.

    Every step asserts; the first failure stops the run with a non-zero exit code, because each step
    depends on the one before it.

    IT CLEANS UP AFTER ITSELF, with one deliberate exception. The account it creates is a throwaway
    (smoke+<timestamp>@example.invalid) and the last step is DELETE /auth/account, which removes that
    user and everything pushed under it in one transaction; the invited account goes first, via
    DELETE /admin/users/{id}. It never touches any account it did not create.

    THE EXCEPTION IS ONE ROW IN "Invites" PER RUN, and it is the feature working rather than a leak:
    both user references on an invite are ON DELETE SET NULL, because an invite is the record of how
    an account came to exist and has to outlive both the admin who issued it and the user who spent
    it. A redeemed invite also cannot be revoked (409, on purpose). So each run leaves one redeemed
    invite with two null user ids. Drop the volume if that matters to you. Pass -KeepAccount to skip the deletion and
    leave the account and its rows in place -- useful when you want to poke at the deployment
    afterwards, and it prints the credentials so you can sign in.

    Registration must be open on the target (Auth__RegistrationEnabled / REGISTRATIONS_ALLOWED), and
    the run spends seven requests against the per-IP credential rate limit
    (Auth__LoginRateLimitPerMinute, 10 by default), so two runs inside one minute can trip a 429.
    Auth__SignupSecretEnabled may be either way round: the first account registers with the secret if
    that is accepted and otherwise cannot be created at all, which the script says plainly.

.PARAMETER BaseUrl
    Where the API is. Defaults to http://localhost:<API_PORT from .env, else 8080>.

.PARAMETER BootstrapSecret
    The X-Bootstrap-Secret value, which now gates registration rather than token issuance. Defaults to
    Bootstrap__Secret from .env, then from the environment.

.PARAMETER KeepAccount
    Skip the closing DELETE /auth/account, leaving the throwaway account and its rows behind -- and
    the invited second account, if the multi-user leg ran.

.PARAMETER Up
    Run `docker compose up -d --build` first, and wait longer for the API to come up.

.PARAMETER Down
    Run `docker compose down` at the end. The volume, and therefore the data, survives.

.EXAMPLE
    pwsh ./scripts/smoke-test.ps1 -Up

.EXAMPLE
    pwsh ./scripts/smoke-test.ps1 -BaseUrl https://workledger.example.com -BootstrapSecret $secret

.EXAMPLE
    pwsh ./scripts/smoke-test.ps1 -KeepAccount
#>
[CmdletBinding()]
param(
    [string]$BaseUrl,
    [string]$BootstrapSecret,
    [switch]$KeepAccount,
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
        Status  = [int]$response.StatusCode
        Headers = $response.Headers
        Text    = $text
        Body    = if (-not $RawText -and $isJson -and $text) { $text | ConvertFrom-Json } else { $null }
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
# Declared before the try so the catch block can mention them under Set-StrictMode even when the run
# failed before registration got as far as choosing one. $secondEmail stays null unless the
# multi-user leg both ran and got as far as creating the account.
$email = $null
$secondEmail = $null
$secondUserId = 0
$ranInviteLeg = $false

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
    Confirm-That '/auth/devices without a token is 401' ((Invoke-Api -Path '/auth/devices').Status -eq 401)

    # --- what this server says about itself ------------------------------------------------------
    Start-Step 'GET /auth/capabilities (anonymous: it is configuration, not secrets)'
    $capabilities = Invoke-Api -Path '/auth/capabilities'
    Confirm-That 'capabilities returns 200' ($capabilities.Status -eq 200) $capabilities.Text
    $minPasswordLength = [int]$capabilities.Body.minPasswordLength
    Confirm-That 'it reports a minimum password length' ($minPasswordLength -gt 0) $capabilities.Text
    Confirm-That 'it reports the lockout policy (WL-147: six fields, not four)' `
        (($null -ne $capabilities.Body.maxFailedLogins) -and ($null -ne $capabilities.Body.lockoutMinutes)) $capabilities.Text
    if (-not $capabilities.Body.registrationEnabled) {
        throw "Registration is disabled on $BaseUrl, so this script cannot create its throwaway account. Set REGISTRATIONS_ALLOWED=true (or point -BaseUrl at a deployment where it is on)."
    }
    Write-Host "      policy: minPasswordLength $minPasswordLength, lockout $($capabilities.Body.maxFailedLogins) failures / $($capabilities.Body.lockoutMinutes) min" -ForegroundColor DarkGray

    # --- the web portal (S11), when the deployment has it switched on ----------------------------
    # Only whether the image serves pages and their static assets: the portal's flows are covered by
    # the test suite. Requested from $BaseUrl rather than the advertised address, because the two
    # differ behind a TLS proxy and this script may be running on the box itself.
    # A pre-S11 server omits the field entirely, and StrictMode throws on a missing property.
    $portalProperty = $capabilities.Body.PSObject.Properties['portalUrl']
    $portalUrl = if ($portalProperty) { $portalProperty.Value } else { $null }
    if ($portalUrl) {
        Start-Step "the web portal is on ($portalUrl) and serves its pages"
        Confirm-That 'the advertised address ends in /portal' ($portalUrl -match '^https?://[^/]+(/.*)?/portal$') $portalUrl
        $login = Invoke-Api -Path '/portal/login' -RawText
        Confirm-That 'GET /portal/login is 200' ($login.Status -eq 200) "got $($login.Status)"
        $csp = $login.Headers['Content-Security-Policy'] -join ''
        Confirm-That 'it carries the portal Content-Security-Policy' `
            (($csp -match "default-src 'self'") -and ($csp -match "frame-ancestors 'none'")) $csp
        $css = Invoke-Api -Path '/portal/assets/portal.css' -RawText
        Confirm-That 'its stylesheet is served anonymously' `
            (($css.Status -eq 200) -and (($css.Headers['Content-Type'] -join '') -match 'text/css')) "got $($css.Status)"
        $api = Invoke-Api -Path '/health'
        Confirm-That 'JSON responses do not carry the portal headers' (-not $api.Headers.ContainsKey('Content-Security-Policy'))
    } else {
        Start-Step 'the web portal is off (Web__Enabled unset), so /portal is not a route'
        Confirm-That 'GET /portal/login is not a page' ((Invoke-Api -Path '/portal/login' -RawText).Status -ne 200)
    }

    # --- register a throwaway account ------------------------------------------------------------
    Start-Step 'registering a throwaway account'
    $stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
    # example.invalid is reserved by RFC 2606 and can never be a real address, which matters because
    # this server does not verify email and a typo'd real one would be someone else's.
    $email = "smoke+$stamp@example.invalid"
    $password = "smoke-$([guid]::NewGuid().ToString('N'))"
    if ($password.Length -lt $minPasswordLength) { $password = $password.PadRight($minPasswordLength, 'x') }

    # Only this script can see this in a real container: the secret is checked before the body is.
    $badSecret = Invoke-Api -Method POST -Path '/auth/register' -Headers @{ 'X-Bootstrap-Secret' = 'wrong-secret' } -Body @{
        Email = "smoke-nope+$stamp@example.invalid"; Password = $password
    }
    Confirm-That 'registration with a wrong bootstrap secret is 401' ($badSecret.Status -eq 401) "status $($badSecret.Status): $($badSecret.Text)"

    $registered = Invoke-Api -Method POST -Path '/auth/register' -Headers @{ 'X-Bootstrap-Secret' = $BootstrapSecret } -Body @{
        Email = $email; Password = $password; DisplayName = "Smoke test $stamp"
    }
    Confirm-That 'registration returns 201 and the new user' (($registered.Status -eq 201) -and ($registered.Body.email -eq $email)) "status $($registered.Status): $($registered.Text)"
    Confirm-That 'registration hands back no token (tokens are device-scoped)' (-not ($registered.Body.PSObject.Properties.Name -contains 'token')) $registered.Text
    $userId = [int]$registered.Body.id

    # --- two devices sign in ---------------------------------------------------------------------
    Start-Step 'signing in from two devices'
    $deviceA = "smoke-a-$stamp"
    $deviceB = "smoke-b-$stamp"

    # One failure only: MaxFailedLogins is 5 by default, and the successful login below resets the
    # counter, so this cannot lock the account it is about to use.
    $wrongPassword = Invoke-Api -Method POST -Path '/auth/login' -Body @{
        Email = $email; Password = "$password-not"; DeviceId = $deviceA
    }
    Confirm-That 'login with a wrong password is 401' ($wrongPassword.Status -eq 401) "status $($wrongPassword.Status): $($wrongPassword.Text)"

    $tokens = @{}
    foreach ($device in @($deviceA, $deviceB)) {
        $login = Invoke-Api -Method POST -Path '/auth/login' -Body @{
            Email = $email; Password = $password; DeviceId = $device; DeviceLabel = "smoke test $device"
        }
        Confirm-That "$device signed in and got a token" (($login.Status -eq 200) -and [bool]$login.Body.token) "status $($login.Status): $($login.Text)"
        Confirm-That "$device's token identifies the signed-in user" ($login.Body.user.id -eq $userId) $login.Text
        $tokens[$device] = $login.Body.token
    }

    $whoami = Invoke-Api -Path '/auth/whoami' -Token $tokens[$deviceA]
    Confirm-That '/auth/whoami echoes the calling device' (($whoami.Status -eq 200) -and ($whoami.Body.deviceId -eq $deviceA)) $whoami.Text
    Confirm-That '/auth/whoami echoes the user, not an account id' (($whoami.Body.userId -eq $userId) -and ($whoami.Body.email -eq $email)) $whoami.Text

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
    # A brand-new account has nothing else in it, which the pre-S9 shared-account version could not assert.
    Confirm-That 'a fresh account totals exactly what it pushed' ($weekly.Body.totalSeconds -eq 3600) "got $($weekly.Body.totalSeconds)"

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

    # --- the account knows about both devices ----------------------------------------------------
    Start-Step 'GET /auth/devices lists both sign-ins'
    $devices = Invoke-Api -Path '/auth/devices' -Token $tokens[$deviceA]
    Confirm-That 'devices returns 200' ($devices.Status -eq 200) $devices.Text
    $deviceRows = @($devices.Body)
    Confirm-That 'exactly the two devices that signed in' ($deviceRows.Count -eq 2) $devices.Text
    Confirm-That 'both are addressed by the client-side deviceId, never a server row id' `
        ((@($deviceRows | Where-Object { $_.deviceId -eq $deviceA }).Count -eq 1) -and
         (@($deviceRows | Where-Object { $_.deviceId -eq $deviceB }).Count -eq 1)) $devices.Text
    $current = @($deviceRows | Where-Object { $_.isCurrent })
    Confirm-That 'isCurrent marks the calling device, and only it' `
        (($current.Count -eq 1) -and ($current[0].deviceId -eq $deviceA)) $devices.Text
    Confirm-That 'the label sent at login came back' (@($deviceRows | Where-Object { $_.label -eq "smoke test $deviceA" }).Count -eq 1) $devices.Text

    # --- invites and tenant isolation, if this script is allowed to administer the target ---------
    Start-Step 'can this run administer the deployment?'
    $canAdminister = (Invoke-Api -Path '/admin/users' -Token $tokens[$deviceA]).Status -eq 200
    if (-not $canAdminister) {
        # An empty database makes its first user an admin, so this is the "fresh volume" path only
        # incidentally; what it really asks is whether ANYBODY administers this deployment yet. A 409
        # means somebody does, and it is not us.
        $bootstrap = Invoke-Api -Method POST -Path '/admin/bootstrap' -Token $tokens[$deviceA] `
            -Headers @{ 'X-Bootstrap-Secret' = $BootstrapSecret }
        Confirm-That 'POST /admin/bootstrap answers 204 (promoted) or 409 (already administered)' `
            (($bootstrap.Status -eq 204) -or ($bootstrap.Status -eq 409)) "status $($bootstrap.Status): $($bootstrap.Text)"
        $canAdminister = $bootstrap.Status -eq 204
        if ($canAdminister) {
            Confirm-That 'the promotion is visible on the very next request, with the same token' `
                ((Invoke-Api -Path '/admin/users' -Token $tokens[$deviceA]).Status -eq 200)
        }
    }

    if (-not $canAdminister) {
        Start-Step 'skipping the invite leg -- this deployment already has an administrator'
        Write-Host "      not promoting a throwaway account into somebody else's server" -ForegroundColor Yellow
    }
    else {
        $ranInviteLeg = $true
        Start-Step 'issuing an invite and registering a second account with it'
        $secondEmail = "smoke2+$stamp@example.invalid"
        $invite = Invoke-Api -Method POST -Path '/admin/invites' -Token $tokens[$deviceA] -Body @{
            Email = $secondEmail; Note = "smoke test $stamp"
        }
        Confirm-That 'POST /admin/invites returns 201' ($invite.Status -eq 201) "status $($invite.Status): $($invite.Text)"
        $inviteToken = [string]$invite.Body.token
        Confirm-That 'the token comes back exactly once, here' ([bool]$inviteToken) $invite.Text
        $inviteId = [int]$invite.Body.invite.id

        # The point of the whole feature, and only provable against a real deployment: no secret, no
        # header, and the account is created anyway.
        $secondRegistered = Invoke-Api -Method POST -Path '/auth/register' -Body @{
            Email = $secondEmail; Password = $password; DisplayName = "Smoke invitee $stamp"; InviteToken = $inviteToken
        }
        Confirm-That 'registering with the invite and NO bootstrap secret returns 201' `
            (($secondRegistered.Status -eq 201) -and ($secondRegistered.Body.email -eq $secondEmail)) `
            "status $($secondRegistered.Status): $($secondRegistered.Text)"
        $secondUserId = [int]$secondRegistered.Body.id

        $listed = Invoke-Api -Path '/admin/invites' -Token $tokens[$deviceA]
        $thisInvite = @($listed.Body | Where-Object { $_.id -eq $inviteId })
        Confirm-That 'the invite is listed as redeemed by the account it created' `
            (($thisInvite.Count -eq 1) -and ($null -ne $thisInvite[0].redeemedAtUtc) -and ($thisInvite[0].redeemedByUserId -eq $secondUserId)) $listed.Text
        Confirm-That 'listing invites never returns the token' `
            (-not ($thisInvite[0].PSObject.Properties.Name -contains 'token')) $listed.Text
        Confirm-That 'the same invite cannot be spent twice' `
            ((Invoke-Api -Method POST -Path '/auth/register' -Body @{
                Email = "smoke3+$stamp@example.invalid"; Password = $password; InviteToken = $inviteToken
            }).Status -eq 409)

        Start-Step 'the two accounts cannot see each other'
        $secondDevice = "smoke-c-$stamp"
        $secondLogin = Invoke-Api -Method POST -Path '/auth/login' -Body @{
            Email = $secondEmail; Password = $password; DeviceId = $secondDevice; DeviceLabel = 'smoke invitee'
        }
        Confirm-That 'the invited account signs in' (($secondLogin.Status -eq 200) -and [bool]$secondLogin.Body.token) `
            "status $($secondLogin.Status): $($secondLogin.Text)"
        $secondToken = $secondLogin.Body.token

        # The tenancy invariant, end to end through the container: a device that has never synced
        # pulls EVERYTHING its user owns, which for this account is nothing at all.
        $secondPull = Invoke-Api -Path '/sync/pull' -Token $secondToken
        Confirm-That 'the invited account pulls nothing belonging to the first' `
            ((@($secondPull.Body.changes).Count -eq 0)) $secondPull.Text
        Confirm-That 'nor can it address them by SyncId' `
            ((Invoke-Api -Path "/reports/projects/$projectId" -Token $secondToken).Status -eq 404)
        Confirm-That 'while the first account still reports its own project' `
            ((Invoke-Api -Path "/reports/projects/$projectId" -Token $tokens[$deviceA]).Status -eq 200)
        Confirm-That 'and sees only its own two devices' `
            (@((Invoke-Api -Path '/auth/devices' -Token $tokens[$deviceA]).Body).Count -eq 2)
        # The isolation that matters most, and the direction the README's security posture claims:
        # an admin can act on ACCOUNTS and cannot read their rows. Proving it needs the invited
        # account to own something, so give it one.
        $inviteeProjectId = [guid]::NewGuid().ToString()
        $inviteePush = Invoke-Api -Method POST -Path '/sync/push' -Token $secondToken -Body @{
            Changes = @(
                (New-Change -EntityType 'project' -EntityId $inviteeProjectId -Operation 'create' -Version 1 -OriginDeviceId $secondDevice -Payload @{
                    SyncId = $inviteeProjectId; Name = "Invitee project $stamp"; ProjectCode = 'SMOKE2'
                    Description = 'Belongs to the invited account'; CreatedAt = $createdAt; UpdatedAt = $null
                    Version = 1; OriginDeviceId = $secondDevice
                })
            )
        }
        Confirm-That 'the invited account can push its own row' `
            (($inviteePush.Status -eq 200) -and ($inviteePush.Body.results[0].accepted)) "status $($inviteePush.Status): $($inviteePush.Text)"

        $adminPull = Invoke-Api -Path '/sync/pull' -Token $tokens[$deviceA]
        Confirm-That 'the admin does NOT receive the invited account row' `
            (@($adminPull.Body.changes | Where-Object { $_.entityId -eq $inviteeProjectId }).Count -eq 0) $adminPull.Text
        Confirm-That 'nor can the admin report on it by SyncId' `
            ((Invoke-Api -Path "/reports/projects/$inviteeProjectId" -Token $tokens[$deviceA]).Status -eq 404)
        Confirm-That 'while the invited account reports its own project fine' `
            ((Invoke-Api -Path "/reports/projects/$inviteeProjectId" -Token $secondToken).Status -eq 200)

        # An admin CAN see that the account exists -- that is the whole surface, and it is counts and
        # state, never rows.
        $users = Invoke-Api -Path '/admin/users' -Token $tokens[$deviceA]
        $invitee = @($users.Body | Where-Object { $_.id -eq $secondUserId })
        Confirm-That 'GET /admin/users reports the invited account and its device count' `
            (($invitee.Count -eq 1) -and ($invitee[0].deviceCount -eq 1) -and (-not $invitee[0].isAdmin)) $users.Text
    }

    # --- clean up after ourselves ----------------------------------------------------------------
    if ($KeepAccount) {
        Start-Step 'skipping DELETE /auth/account (-KeepAccount)'
        Write-Host "      account $email left in place; password: $password" -ForegroundColor Yellow
        if ($secondEmail) {
            Write-Host "      invited account $secondEmail too, same password" -ForegroundColor Yellow
        }
        Write-Host "      project $projectId stays with it; docker compose down -v drops everything" -ForegroundColor DarkGray
    }
    else {
        if ($secondEmail) {
            # The invited account first, and through the admin route -- this is the only place the
            # script exercises DELETE /admin/users/{id}, and it only ever names an account it created.
            Start-Step 'DELETE /admin/users/{id} removes the invited account'

            $wrongTargetEmail = Invoke-Api -Method DELETE -Path "/admin/users/$secondUserId" -Token $tokens[$deviceA] -Body @{
                ConfirmEmail = $email
            }
            Confirm-That 'a ConfirmEmail belonging to a different account is 400, even though it is a real address' `
                ($wrongTargetEmail.Status -eq 400) "status $($wrongTargetEmail.Status): $($wrongTargetEmail.Text)"
            Confirm-That 'and the invited account is still there' `
                ((Invoke-Api -Path '/auth/whoami' -Token $secondToken).Status -eq 200)

            $secondDeleted = Invoke-Api -Method DELETE -Path "/admin/users/$secondUserId" -Token $tokens[$deviceA] -Body @{
                ConfirmEmail = $secondEmail
            }
            Confirm-That 'deletion returns 204' ($secondDeleted.Status -eq 204) "status $($secondDeleted.Status): $($secondDeleted.Text)"
            Confirm-That "the invited account's token is dead immediately" `
                ((Invoke-Api -Path '/auth/whoami' -Token $secondToken).Status -eq 401)
            $secondEmail = $null
        }

        Start-Step 'DELETE /auth/account removes the throwaway account and everything under it'

        $wrongConfirm = Invoke-Api -Method DELETE -Path '/auth/account' -Token $tokens[$deviceA] -Body @{
            Password = $password; ConfirmEmail = 'someone-else@example.invalid'
        }
        Confirm-That 'deletion with the wrong confirmation email is 400' ($wrongConfirm.Status -eq 400) "status $($wrongConfirm.Status): $($wrongConfirm.Text)"

        $wrongPasswordDelete = Invoke-Api -Method DELETE -Path '/auth/account' -Token $tokens[$deviceA] -Body @{
            Password = "$password-not"; ConfirmEmail = $email
        }
        Confirm-That 'deletion with the wrong password is 401' ($wrongPasswordDelete.Status -eq 401) "status $($wrongPasswordDelete.Status): $($wrongPasswordDelete.Text)"

        $deleted = Invoke-Api -Method DELETE -Path '/auth/account' -Token $tokens[$deviceA] -Body @{
            Password = $password; ConfirmEmail = $email
        }
        Confirm-That 'deletion returns 204' ($deleted.Status -eq 204) "status $($deleted.Status): $($deleted.Text)"

        # Revocation, in a real container, with the cache at whatever the deployment configured: the
        # endpoint evicts its own keys after the write commits, so this is immediate rather than lagging
        # Auth__StampCacheSeconds. If these three ever start needing a sleep, that eviction is broken.
        Confirm-That "the deleting device's token is dead" ((Invoke-Api -Path '/auth/whoami' -Token $tokens[$deviceA]).Status -eq 401)
        Confirm-That "the other device's token is dead too" ((Invoke-Api -Path '/sync/pull' -Token $tokens[$deviceB]).Status -eq 401)
        $goneLogin = Invoke-Api -Method POST -Path '/auth/login' -Body @{ Email = $email; Password = $password; DeviceId = $deviceA }
        Confirm-That 'the credentials no longer sign in' ($goneLogin.Status -eq 401) "status $($goneLogin.Status): $($goneLogin.Text)"
    }

    Write-Host ''
    Write-Host "SMOKE TEST PASSED against $BaseUrl" -ForegroundColor Green
    if (-not $KeepAccount) {
        Write-Host "      both accounts it created are deleted" -ForegroundColor DarkGray
        if ($ranInviteLeg) {
            Write-Host "      one redeemed row stays in Invites, with null user ids -- by design; see the header" -ForegroundColor DarkGray
        }
    }
} catch {
    Write-Host ''
    Write-Host "SMOKE TEST FAILED: $($_.Exception.Message)" -ForegroundColor Red
    if ($email) {
        Write-Host "      the throwaway account $email may still exist -- delete it with DELETE /auth/account, or drop the volume" -ForegroundColor DarkGray
    }
    if ($secondEmail) {
        Write-Host "      so may the invited account $secondEmail -- DELETE /admin/users/$secondUserId, or drop the volume" -ForegroundColor DarkGray
    }
    $exitCode = 1
} finally {
    if ($Down) {
        Start-Step 'docker compose down'
        docker compose --file $composeFile down
    }
}

exit $exitCode
