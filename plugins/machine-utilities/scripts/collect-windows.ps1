param(
    [Parameter(Mandatory = $true)][string]$ConfigPath,
    [Parameter(Mandatory = $true)][string]$HostId,
    [Parameter(Mandatory = $true)][ValidatePattern("^[0-9A-Fa-f]{64}$")][string]$ControllerConfigDigest,
    [string]$SnapshotId = ([DateTime]::UtcNow.ToString("yyyyMMddTHHmmssZ") + "-" + $PID),
    [string[]]$Sections = @("all"),
    [switch]$AllowAuthVerify
)

$ErrorActionPreference = "Stop"
$OutputEncoding = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = $OutputEncoding
$script:Records = New-Object System.Collections.Generic.List[object]
$script:HasProblems = $false
$ObservedAt = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")

function Test-Section([string]$Name) {
    return $Sections -contains "all" -or $Sections -contains $Name
}

function Limit-Text([object]$Value) {
    if ($null -eq $Value) { return $null }
    if ($Value -is [DateTime]) { return $Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ") }
    $text = [string]$Value
    if ($text.Length -gt 8192) { return $text.Substring(0, 8192) }
    return $text
}

function Add-Record {
    param(
        [string]$Kind,
        [string]$Id,
        [ValidateSet("present", "absent", "partial", "unavailable", "error")][string]$Status,
        [ValidateSet("high", "medium", "low", "unknown")][string]$Confidence,
        [hashtable]$Data = @{},
        [object[]]$Evidence = @(),
        [object[]]$Errors = @()
    )
    [void]$script:Records.Add([ordered]@{
        schema         = "machine-utilities.inventory"
        schema_version = 1
        snapshot_id    = $SnapshotId
        host_id        = $HostId
        kind           = $Kind
        id             = Limit-Text $Id
        observed_at    = $ObservedAt
        status         = $Status
        confidence     = $Confidence
        data           = $Data
        evidence       = $Evidence
        errors         = $Errors
    })
    if ($Status -in @("partial", "unavailable", "error")) {
        $script:HasProblems = $true
    }
}

function Resolve-UserPath([string]$Path) {
    if ($Path -eq "~") { return $HOME }
    if ($Path.StartsWith("~/") -or $Path.StartsWith("~\")) {
        return Join-Path $HOME $Path.Substring(2)
    }
    return $Path
}

function Get-SafeRemote([string]$Remote) {
    if ([string]::IsNullOrWhiteSpace($Remote)) { return $null }
    $value = $Remote -replace '^([A-Za-z][A-Za-z0-9+.-]*://)[^/@]*@', '$1'
    $value = $value -replace '^[^/@]+@([^:]+:.*)$', '$1'
    return Limit-Text (($value -split '\?')[0])
}

function Get-CanonicalGitSource([string]$Remote) {
    $Value = Get-SafeRemote $Remote
    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
    $Value = $Value.ToLowerInvariant()
    $Value = $Value -replace '^[a-z][a-z0-9+.-]*://', ''
    $Value = $Value -replace '^git@', ''
    $Value = $Value -replace '^([^/]+):', '$1/'
    $Value = $Value.TrimEnd("/")
    $Value = $Value -replace '\.git$', ''
    if (($Value -split '/').Count -eq 2) { return "github.com/$Value" }
    return $Value
}

function Get-TextSha256([string]$Text) {
    $Hasher = [Security.Cryptography.SHA256]::Create()
    try {
        $Bytes = [Text.Encoding]::UTF8.GetBytes($Text)
        return (($Hasher.ComputeHash($Bytes) | ForEach-Object { $_.ToString("x2") }) -join "")
    } finally {
        $Hasher.Dispose()
    }
}

function Get-NullableBoolean([object]$Value, [object]$Default = $null) {
    if ($Value -is [bool]) { return $Value }
    return $Default
}

function Test-JsmVersion([object]$Value) {
    return $null -eq $Value -or $Value -is [string] -or
        $Value -is [byte] -or $Value -is [sbyte] -or
        $Value -is [int16] -or $Value -is [uint16] -or
        $Value -is [int32] -or $Value -is [uint32] -or
        $Value -is [int64] -or $Value -is [uint64] -or
        $Value -is [single] -or $Value -is [double] -or $Value -is [decimal]
}

function Test-ManagerEntityName([object]$Value, [string]$Manager) {
    if ($Value -isnot [string] -or [string]::IsNullOrWhiteSpace([string]$Value)) { return $false }
    $Pattern = if ($Manager -eq "skills-cli") {
        "^[A-Za-z0-9@._/][A-Za-z0-9@._/-]*$"
    } else {
        "^[A-Za-z0-9._/][A-Za-z0-9._/-]*$"
    }
    return [string]$Value -match $Pattern
}

function Test-JsmSkill([object]$Skill) {
    if ($null -eq $Skill -or -not (Test-ManagerEntityName $Skill.name "jsm")) { return $false }
    if (-not (Test-JsmVersion $Skill.version) -or -not (Test-JsmVersion $Skill.latest_version)) { return $false }
    foreach ($Field in @("installed_at")) {
        if ($null -ne $Skill.$Field -and $Skill.$Field -isnot [string]) { return $false }
    }
    foreach ($Field in @("pinned", "update_available", "is_saved", "is_jeffreys")) {
        if ($null -ne $Skill.$Field -and $Skill.$Field -isnot [bool]) { return $false }
    }
    if ($null -ne $Skill.tags -and ($Skill.tags -is [string] -or $Skill.tags -isnot [Collections.IEnumerable])) { return $false }
    if (@($Skill.tags).Count -gt 128) { return $false }
    foreach ($Tag in @($Skill.tags)) {
        if ($Tag -isnot [string] -and $Tag -isnot [ValueType]) { return $false }
    }
    return $true
}

function Get-DirectoryDigest([string]$Path) {
    [string[]]$Lines = foreach ($File in @(Get-ChildItem -LiteralPath $Path -File -Recurse -Force -ErrorAction Stop |
        Where-Object { $_.FullName -notmatch '[\\/]\.git[\\/]' })) {
        $Relative = $File.FullName.Substring($Path.TrimEnd("\", "/").Length).TrimStart("\", "/").Replace("\", "/")
        $Hash = (Get-FileHash -LiteralPath $File.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$Relative`t$Hash"
    }
    if ($null -eq $Lines) { $Lines = @() }
    if ($Lines.Count -gt 1) { [Array]::Sort($Lines, [StringComparer]::Ordinal) }
    return Get-TextSha256 (($Lines -join "`n") + "`n")
}

function Get-AuthHealth([object]$Definition) {
    $Verify = @($Definition.verify)
    if ($Verify.Count -eq 0) { return @{ health = "not-configured"; verify_exit_code = $null } }
    if (-not $AllowAuthVerify) { return @{ health = "not-authorized"; verify_exit_code = $null } }
    if ($Verify.Count -gt 32 -or [string]$Verify[0] -notmatch '^[A-Za-z0-9._+-]+$') {
        return @{ health = "invalid-config"; verify_exit_code = $null }
    }
    if ($null -eq (Get-Command ([string]$Verify[0]) -ErrorAction SilentlyContinue)) {
        return @{ health = "unavailable"; verify_exit_code = $null }
    }
    $Job = Start-Job -ScriptBlock {
        param([string[]]$Argv)
        & $Argv[0] @($Argv | Select-Object -Skip 1) *> $null
        $Succeeded = $?
        if ($null -ne $LASTEXITCODE) { return [int]$LASTEXITCODE }
        if ($Succeeded) { return 0 }
        return 1
    } -ArgumentList (, [string[]]$Verify)
    try {
        if ($null -eq (Wait-Job -Job $Job -Timeout 10)) {
            Stop-Job -Job $Job -ErrorAction SilentlyContinue
            return @{ health = "timeout"; verify_exit_code = 124 }
        }
        if ($Job.State -ne "Completed" -or @($Job.ChildJobs | ForEach-Object { $_.Error }).Count -gt 0) {
            return @{ health = "error"; verify_exit_code = $null }
        }
        $JobOutput = @(Receive-Job -Job $Job -ErrorAction SilentlyContinue)
        [int]$ExitCode = 0
        if ($JobOutput.Count -ne 1 -or
            -not [int]::TryParse([string]$JobOutput[0], [ref]$ExitCode)) {
            return @{ health = "error"; verify_exit_code = $null }
        }
        return @{ health = $(if ($ExitCode -eq 0) { "healthy" } else { "unhealthy" }); verify_exit_code = $ExitCode }
    } finally {
        Remove-Job -Job $Job -Force -ErrorAction SilentlyContinue
    }
}

function Test-AgentSettingValue([string]$Key, [object]$Value) {
    if ($null -eq $Value) { return $false }
    if ($Key -cin @("remoteControlAtStartup", "switchModelsOnFlag", "agentPushNotifEnabled",
            "check_for_update_on_startup")) { return $Value -is [bool] }
    if ($Key -ceq "availableModels") {
        return $Value -isnot [string] -and $Value -is [Collections.IEnumerable] -and
            @($Value | Where-Object { $_ -isnot [string] -or [string]::IsNullOrWhiteSpace($_) }).Count -eq 0
    }
    if ($Key -ceq "autoUpdatesChannel") { return [string]$Value -cin @("latest", "stable") }
    if ($Key -ceq "cli_auth_credentials_store") { return [string]$Value -cin @("file", "keyring", "auto") }
    return $Value -is [string] -and -not [string]::IsNullOrWhiteSpace([string]$Value)
}

function Add-AgentSettings(
    [object]$Definition,
    [string]$ArtifactId,
    [string]$ArtifactPath,
    [switch]$UnknownPath,
    [switch]$LinkedPath
) {
    $Format = [string]$Definition.format
    $ArtifactExists = -not $UnknownPath -and (Test-Path -LiteralPath $ArtifactPath)
    $ArtifactIsFile = -not $UnknownPath -and -not $LinkedPath -and
        (Test-Path -LiteralPath $ArtifactPath -PathType Leaf)
    $Parsed = $null
    if ($Format -eq "json" -and $ArtifactIsFile) {
        try { $Parsed = Get-Content -LiteralPath $ArtifactPath -Raw | ConvertFrom-Json -AsHashtable -NoEnumerate } catch { $Parsed = $null }
        if ($null -eq $Parsed -or $Parsed -isnot [Collections.IDictionary]) {
            $Parsed = $null
        }
    }
    foreach ($Setting in $Definition.settings.PSObject.Properties) {
        $Key = [string]$Setting.Name
        $Desired = $Setting.Value
        $Present = $false
        $Observed = $null
        $ParseFailed = $UnknownPath -or $LinkedPath -or ($ArtifactExists -and -not $ArtifactIsFile)
        if ($Format -eq "json") {
            if (-not $ArtifactExists) {
                # An absent artifact means every configured setting is absent, not unparseable.
            } elseif ($null -eq $Parsed) {
                $ParseFailed = $true
            } else {
                if ($Parsed.Contains($Key)) {
                    $Present = $true
                    $Observed = $Parsed[$Key]
                }
            }
        } elseif ($Format -eq "toml") {
            $Escaped = [Regex]::Escape($Key)
            $KeyPattern = '(?:{0}|"{0}"|''{0}'')' -f $Escaped
            $Line = $null
            foreach ($ConfigLine in $(if ($ArtifactIsFile) { @(Get-Content -LiteralPath $ArtifactPath) } else { @() })) {
                if ($ConfigLine -match '^\s*\[') { break }
                if ($ConfigLine -cmatch "^\s*$KeyPattern\s*=") { $Line = $ConfigLine; break }
            }
            if ($null -ne $Line) {
                $Raw = $Line -creplace "^\s*$KeyPattern\s*=\s*", ""
                if ($Raw -match "^\s*'([^']*)'\s*(?:#.*)?$") {
                    $Observed = $Matches[1]
                    $Present = $true
                } elseif ($Raw -match '^\s*(?<value>"(?:[^"\\]|\\.)*")\s*(?:#.*)?$') {
                    try { $Observed = $Matches.value | ConvertFrom-Json; $Present = $true } catch { $ParseFailed = $true }
                } elseif ($Raw -cmatch '^\s*(?<value>true|false)\s*(?:#.*)?$') {
                    $Observed = $Matches.value -ceq "true"
                    $Present = $true
                } else {
                    $ParseFailed = $true
                }
            }
        }
        $SettingValueValid = $null -eq $Observed -and $null -eq $Desired
        if ($null -ne $Observed) { $SettingValueValid = Test-AgentSettingValue $Key $Observed }
        if ($Present -and
            (-not $SettingValueValid -or -not (Test-BoundedSemanticValue $Observed))) {
            $ParseFailed = $true
            $Observed = $null
        }
        if ($ParseFailed) {
            $SettingPath = if ($UnknownPath) { $null } else { Limit-Text $ArtifactPath }
            $Error = if ($UnknownPath) {
                @{ code = "artifact_path_missing"; severity = "warning"; retryable = $false; message = "agent setting artifact has no path for this host" }
            } elseif ($LinkedPath) {
                @{ code = "symlink_not_followed"; severity = "warning"; retryable = $false; message = "agent setting artifact path is a link" }
            } else {
                @{ code = "setting_parse_failed"; severity = "warning"; retryable = $false; message = "allowlisted agent setting could not be parsed" }
            }
            Add-Record -Kind "agent_setting" -Id "$ArtifactId`:$Key" -Status "unavailable" -Confidence "medium" -Data @{
                artifact = $ArtifactId; path = $SettingPath; format = $Format; key = $Key
                desired = $Desired; agent_exposure = @($Definition.agents)
            } -Errors @($Error)
            continue
        }
        $ObservedJson = ConvertTo-Json $Observed -Compress -Depth 20
        $DesiredJson = ConvertTo-Json $Desired -Compress -Depth 20
        $InSync = if ($null -eq $Desired) { -not $Present } else { $Present -and $ObservedJson -ceq $DesiredJson }
        Add-Record -Kind "agent_setting" -Id "$ArtifactId`:$Key" -Status "present" -Confidence "high" -Data @{
            artifact = $ArtifactId; path = Limit-Text $ArtifactPath; format = $Format; key = $Key
            observed_present = $Present; observed = $Observed; desired = $Desired; in_sync = $InSync
            agent_exposure = @($Definition.agents)
        } -Evidence @(@{ source = "filesystem"; method = "allowlisted-semantic-setting" })
    }
}

function Test-BoundedStrings([object]$Value) {
    if ($null -eq $Value) { return $true }
    if ($Value -is [string]) {
        return $Value.Length -le 8192 -and $Value -notmatch "[\x00-\x1f\x7f-\x9f]"
    }
    if ($Value -is [ValueType]) { return $true }
    if ($Value -is [Collections.IDictionary]) {
        foreach ($Key in $Value.Keys) {
            if (-not (Test-BoundedStrings $Key) -or -not (Test-BoundedStrings $Value[$Key])) { return $false }
        }
        return $true
    }
    if ($Value -is [Collections.IEnumerable] -and $Value -isnot [string]) {
        foreach ($Item in $Value) {
            if (-not (Test-BoundedStrings $Item)) { return $false }
        }
        return $true
    }
    if ($Value.PSObject -and $Value.PSObject.Properties) {
        foreach ($Property in $Value.PSObject.Properties) {
            if (-not (Test-BoundedStrings $Property.Name) -or -not (Test-BoundedStrings $Property.Value)) { return $false }
        }
    }
    return $true
}

function Test-BoundedSemanticValue([object]$Value) {
    if (-not (Test-BoundedStrings $Value)) { return $false }
    try {
        $Json = ConvertTo-Json $Value -Compress -Depth 20
        return [Text.Encoding]::UTF8.GetByteCount($Json) -le 8192
    } catch {
        return $false
    }
}

function Get-ExactPropertyValue([object]$Value, [string]$Name) {
    if ($null -eq $Value) { return $null }
    $Property = $Value.PSObject.Properties | Where-Object { $_.Name -ceq $Name } | Select-Object -First 1
    if ($null -eq $Property) { return $null }
    return $Property.Value
}

function Test-ExactMember([object[]]$Values, [object]$Value) {
    return @($Values | Where-Object { [string]$_ -ceq [string]$Value }).Count -gt 0
}

function Get-ConfiguredPath([object]$Definition, [string]$ConfiguredHostId) {
    $HostPath = Get-ExactPropertyValue $Definition.paths $ConfiguredHostId
    if ($null -ne $HostPath) { return [string]$HostPath }
    return [string]$Definition.path
}

function Assert-WorkerConfig([object]$Value) {
    $ConfiguredMachine = Get-ExactPropertyValue $Value.machines $HostId
    if ($HostId -notmatch '^[A-Za-z0-9._-]+$' -or $Value.version -ne 1 -or $null -eq $ConfiguredMachine) {
        throw "Invalid version 1 configuration or unknown host"
    }
    if (-not (Test-BoundedStrings $Value)) { throw "Configuration contains an oversized or control string" }
    if ($null -eq $Value.worker -or
        [string]$Value.worker.target -cne $HostId -or
        [string]$Value.worker.controller_configuration_digest -ne $ControllerConfigDigest.ToLowerInvariant()) {
        throw "Worker configuration is not bound to this controller and target"
    }
    if ($ConfiguredMachine.platform -ne "windows" -or
        $ConfiguredMachine.transport -ne "codex-remote-control" -or
        [string]::IsNullOrWhiteSpace([string]$ConfiguredMachine.codex_host)) {
        throw "Windows worker requires direct codex-remote-control transport"
    }
    if ([string]$ConfiguredMachine.expected_hostname -notmatch "^[A-Za-z0-9._-]+$" -or
        [string]$ConfiguredMachine.expected_user -notmatch "^[A-Za-z0-9._@-]+$") {
        throw "Windows worker requires expected_hostname and expected_user"
    }
    if ([string]$env:COMPUTERNAME -ine [string]$ConfiguredMachine.expected_hostname -or
        [string][Environment]::UserName -ine [string]$ConfiguredMachine.expected_user) {
        throw "Windows target hostname or user does not match configuration"
    }
    if (@($ConfiguredMachine.groups | Where-Object { $_ -notmatch '^[A-Za-z0-9._-]+$' }).Count -gt 0) {
        throw "Invalid machine group"
    }
    if (@($ConfiguredMachine.package_managers | Where-Object { $_ -ne "winget" }).Count -gt 0) {
        throw "Invalid Windows package manager"
    }
    if ($null -ne $Value.projects) {
        foreach ($Property in $Value.projects.PSObject.Properties) {
            $Definition = $Property.Value
            $Path = [string]$Definition.path
            if ($Property.Name -notmatch '^[A-Za-z0-9._-]+$' -or
                [string]::IsNullOrWhiteSpace([string]$Definition.source) -or
                [string]$Definition.source -match '\?' -or
                [string]$Definition.source -match '^[A-Za-z][A-Za-z0-9+.-]*://(?!git@)[^/@]+@' -or
                [string]::IsNullOrWhiteSpace($Path) -or
                [IO.Path]::IsPathRooted($Path) -or $Path.Contains("\") -or
                $Path -match '(^|/)\.\.(/|$)') {
                throw "Invalid project configuration"
            }
        }
    }
    if ($null -ne $Value.capabilities) {
        foreach ($Property in $Value.capabilities.PSObject.Properties) {
            if ($Property.Name -notmatch '^[A-Za-z0-9._][A-Za-z0-9._-]*$') { throw "Invalid capability configuration" }
            $ProviderCount = 0
            foreach ($Agent in @("codex", "claude")) {
                $Definition = Get-ExactPropertyValue $Property.Value $Agent
                if ($null -eq $Definition) { continue }
                $ProviderCount++
                if (@("plugin", "skills-cli", "jsm", "manual", "plugin-source") -notcontains [string]$Definition.provider -or
                    [string]::IsNullOrWhiteSpace([string]$Definition.source) -or
                    [string]$Definition.source -match '\?' -or
                    [string]$Definition.source -match '^[A-Za-z][A-Za-z0-9+.-]*://(?!git@)[^/@]+@' -or
                    ($null -ne $Definition.skill -and [string]$Definition.skill -notmatch '^[A-Za-z0-9._][A-Za-z0-9._-]*$') -or
                    ($null -ne $Definition.name -and [string]$Definition.name -notmatch '^[A-Za-z0-9._][A-Za-z0-9._-]*$')) {
                    throw "Invalid capability provider configuration"
                }
                if ([string]$Definition.provider -eq "plugin" -and
                    ([string]$Definition.source -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$' -or
                     [string]$Definition.source -in @(".", ".."))) {
                    throw "Invalid plugin capability source"
                }
            }
            if ($ProviderCount -eq 0) { throw "Capability has no provider" }
        }
    }
    if ($null -ne $Value.skill_roots) {
        if (@($Value.skill_roots | ForEach-Object { $_.id } | Sort-Object -Unique).Count -ne @($Value.skill_roots).Count) {
            throw "Duplicate skill root ID"
        }
        foreach ($Definition in @($Value.skill_roots)) {
            $Manager = if ($null -eq $Definition.manager) { "manual" } else { [string]$Definition.manager }
            if ([string]$Definition.id -notmatch '^[A-Za-z0-9._-]+$' -or
                [string]::IsNullOrWhiteSpace([string]$Definition.path) -or
                @("manual", "mixed", "skills-cli", "jsm", "plugin-source") -notcontains $Manager -or
                @($Definition.agents | Where-Object { $_ -notin @("codex", "claude") }).Count -gt 0) {
                throw "Invalid skill root configuration"
            }
        }
    }
    if ($null -ne $Value.agent_artifacts) {
        if (@($Value.agent_artifacts | ForEach-Object { $_.id } | Sort-Object -Unique).Count -ne @($Value.agent_artifacts).Count) {
            throw "Duplicate agent artifact ID"
        }
        foreach ($Definition in @($Value.agent_artifacts)) {
            $SettingKeys = if ($null -eq $Definition.settings) { @() } else {
                @($Definition.settings.PSObject.Properties | ForEach-Object { $_.Name })
            }
            $AllowedJsonSettings = @("remoteControlAtStartup", "switchModelsOnFlag", "model", "effortLevel",
                "availableModels", "fallbackModel", "autoUpdatesChannel", "agentPushNotifEnabled")
            $AllowedTomlSettings = @("model", "model_reasoning_effort", "service_tier",
                "check_for_update_on_startup", "cli_auth_credentials_store")
            $InvalidSettingValue = $false
            $SettingProperties = if ($null -eq $Definition.settings) { @() } else {
                @($Definition.settings.PSObject.Properties)
            }
            foreach ($Setting in $SettingProperties) {
                if ($null -eq $Setting.Value) { continue }
                if ($Setting.Name -cin @("remoteControlAtStartup", "switchModelsOnFlag",
                        "agentPushNotifEnabled", "check_for_update_on_startup")) {
                    $InvalidSettingValue = $Setting.Value -isnot [bool]
                } elseif ($Setting.Name -ceq "availableModels") {
                    $InvalidSettingValue = $Setting.Value -is [string] -or
                        $Setting.Value -isnot [Collections.IEnumerable] -or
                        @($Setting.Value | Where-Object { $_ -isnot [string] -or [string]::IsNullOrWhiteSpace($_) }).Count -gt 0
                } elseif ($Setting.Name -ceq "autoUpdatesChannel") {
                    $InvalidSettingValue = [string]$Setting.Value -cnotin @("latest", "stable")
                } elseif ($Setting.Name -ceq "cli_auth_credentials_store") {
                    $InvalidSettingValue = [string]$Setting.Value -cnotin @("file", "keyring", "auto")
                } else {
                    $InvalidSettingValue = $Setting.Value -isnot [string] -or [string]::IsNullOrWhiteSpace([string]$Setting.Value)
                }
                if (-not $InvalidSettingValue -and -not (Test-BoundedSemanticValue $Setting.Value)) {
                    $InvalidSettingValue = $true
                }
                if ($InvalidSettingValue) { break }
            }
            if ([string]$Definition.id -notmatch '^[A-Za-z0-9._-]+$' -or
                @("agent-definition", "instruction", "config") -notcontains [string]$Definition.kind -or
                @($Definition.agents | Where-Object { $_ -notin @("codex", "claude") }).Count -gt 0 -or
                ($SettingKeys.Count -gt 0 -and ([string]$Definition.kind -ne "config" -or
                    [string]$Definition.format -notin @("json", "toml"))) -or
                ($SettingKeys.Count -gt 0 -and
                    (([string]$Definition.format -eq "json" -and
                      (@($Definition.agents).Count -ne 1 -or ([string](@($Definition.agents)[0])) -cne "claude")) -or
                     ([string]$Definition.format -eq "toml" -and
                      (@($Definition.agents).Count -ne 1 -or ([string](@($Definition.agents)[0])) -cne "codex")))) -or
                ([string]$Definition.format -eq "json" -and
                    @($SettingKeys | Where-Object { $_ -cnotin $AllowedJsonSettings }).Count -gt 0) -or
                ([string]$Definition.format -eq "toml" -and
                    @($SettingKeys | Where-Object { $_ -cnotin $AllowedTomlSettings }).Count -gt 0) -or
                $InvalidSettingValue) {
                throw "Invalid agent artifact configuration"
            }
        }
    }
    if ($null -ne $Value.auth_artifacts) {
        foreach ($Property in $Value.auth_artifacts.PSObject.Properties) {
            $Verify = @($Property.Value.verify)
            $ReauthValue = $Property.Value.reauth
            $Reauth = if ($null -eq $ReauthValue) { @() } else { @($ReauthValue) }
            $Strategy = if ($null -eq $Property.Value.strategy) { "ignore" } else { [string]$Property.Value.strategy }
            $Portability = if ($null -eq $Property.Value.portability) { "per-machine" } else { [string]$Property.Value.portability }
            $HasPath = -not [string]::IsNullOrWhiteSpace([string]$Property.Value.path) -or
                ($null -ne $Property.Value.paths -and $Property.Value.paths.PSObject.Properties.Count -gt 0) -or
                ($Portability -in @("native-store", "per-machine") -and $Verify.Count -gt 0)
            $PathlessAuthStatus = $Portability -in @("native-store", "per-machine") -and
                [string]::IsNullOrWhiteSpace([string]$Property.Value.path) -and
                ($null -eq $Property.Value.paths -or $Property.Value.paths.PSObject.Properties.Count -eq 0)
            if ($Property.Name -notmatch '^[A-Za-z0-9._-]+$' -or
                -not $HasPath -or
                @("chezmoi", "encrypted-install", "reauth", "ignore") -notcontains $Strategy -or
                @("declarative", "secret-reference", "portable-session", "native-store", "per-machine", "regenerable-cache") -notcontains $Portability -or
                $Verify.Count -gt 32 -or
                ($null -ne $ReauthValue -and $ReauthValue -isnot [Collections.IList]) -or
                $Reauth.Count -gt 32 -or
                ($Strategy -eq "reauth" -and $Reauth.Count -eq 0) -or
                ($Reauth.Count -gt 0 -and
                    ([string]$Reauth[0] -notmatch '^[A-Za-z0-9._+-]+$' -or
                     @($Reauth | Where-Object { $_ -isnot [string] -or [string]::IsNullOrEmpty($_) }).Count -gt 0)) -or
                ($PathlessAuthStatus -and $Strategy -notin @("reauth", "ignore")) -or
                ($PathlessAuthStatus -and $Verify.Count -eq 0) -or
                ($Verify.Count -gt 0 -and [string]$Verify[0] -notmatch '^[A-Za-z0-9._+-]+$')) {
                throw "Invalid auth artifact configuration"
            }
        }
    }
}

if (-not (Test-Path -LiteralPath $ConfigPath -PathType Leaf)) {
    throw "Configuration file not found: $ConfigPath"
}
$ConfigItem = Get-Item -LiteralPath $ConfigPath -Force
if (($ConfigItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Configuration file must not be a link"
}
$Config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
Assert-WorkerConfig $Config
$Machine = Get-ExactPropertyValue $Config.machines $HostId
$HostGroups = @($Machine.groups)
$AllowedSections = @("all", "host", "packages", "agents", "auth", "projects", "startup", "chezmoi")
if (@($Sections | Where-Object { $AllowedSections -notcontains $_ }).Count -gt 0) {
    throw "Unsupported inventory section"
}

$WorkerConfigHash = (Get-FileHash -LiteralPath $ConfigPath -Algorithm SHA256).Hash.ToLowerInvariant()
Add-Record -Kind "snapshot" -Id "snapshot" -Status "present" -Confidence "high" -Data @{
    configuration_digest = @{ algorithm = "sha256"; value = $ControllerConfigDigest.ToLowerInvariant(); scope = "controller-raw-bytes" }
    worker_configuration_digest = @{ algorithm = "sha256"; value = $WorkerConfigHash; scope = "bounded-worker-raw-bytes" }
    sections = $Sections
}

if (Test-Section "host") {
    $Os = Get-CimInstance Win32_OperatingSystem
    Add-Record -Kind "host" -Id $HostId -Status "present" -Confidence "high" -Data @{
        configured_name = $HostId
        hostname = Limit-Text $env:COMPUTERNAME
        user = Limit-Text ([Environment]::UserName)
        home = Limit-Text $HOME
        os = Limit-Text $Os.Caption
        version = Limit-Text $Os.Version
        build = Limit-Text $Os.BuildNumber
        architecture = Limit-Text $env:PROCESSOR_ARCHITECTURE
    } -Evidence @(@{ source = "system"; method = "Win32_OperatingSystem" })
}

if (Test-Section "packages") {
    $Managers = @($Machine.package_managers)
    if ($Managers -contains "winget") {
        $Winget = Get-Command winget -ErrorAction SilentlyContinue
        if ($null -eq $Winget) {
            Add-Record -Kind "error" -Id "packages:winget" -Status "unavailable" -Confidence "high" -Errors @(
                @{ code = "manager_missing"; severity = "warning"; retryable = $false; message = "winget is not installed" }
            )
        } else {
            $Temp = Join-Path ([IO.Path]::GetTempPath()) ("machine-utilities-winget-" + [Guid]::NewGuid().ToString("N") + ".json")
            $Candidates = @{}
            $CandidateQueryAuthoritative = $false
            try {
                $UpgradeLines = @(& winget upgrade --accept-source-agreements --disable-interactivity 2>$null)
                $UpgradeSucceeded = $?
                $UpgradeExitCode = $LASTEXITCODE
                if ($UpgradeSucceeded -and ($null -eq $UpgradeExitCode -or $UpgradeExitCode -eq 0)) {
                    $HeaderIndex = -1
                    for ($Index = 0; $Index -lt $UpgradeLines.Count; $Index++) {
                        if ([string]$UpgradeLines[$Index] -match '^Name\s{2,}Id\s{2,}Version\s{2,}Available\s{2,}Source\s*$') {
                            $HeaderIndex = $Index
                            break
                        }
                    }
                    if ($HeaderIndex -ge 0 -and $HeaderIndex + 1 -lt $UpgradeLines.Count -and
                        [string]$UpgradeLines[$HeaderIndex + 1] -match '^-{3,}(\s+-{2,}){4}\s*$') {
                        $CandidateQueryAuthoritative = $true
                        foreach ($Line in @($UpgradeLines | Select-Object -Skip ($HeaderIndex + 2))) {
                            if ([string]::IsNullOrWhiteSpace([string]$Line)) { continue }
                            if ([string]$Line -notmatch '^(?<name>.*?)\s{2,}(?<id>\S+)\s+(?<installed>\S+)\s+(?<available>\S+)\s+(?<source>\S+)\s*$' -or
                                [string]$Matches.available -match '^-+$' -or
                                $Candidates.ContainsKey([string]$Matches.id)) {
                                $CandidateQueryAuthoritative = $false
                                $Candidates.Clear()
                                break
                            }
                            $Candidates[[string]$Matches.id] = [string]$Matches.available
                        }
                    } elseif (($UpgradeLines -join "`n") -match
                        '(?m)^(No installed package found matching input criteria|No applicable upgrade found)\.?$') {
                        $CandidateQueryAuthoritative = $true
                    }
                    if (-not $CandidateQueryAuthoritative) {
                        Add-Record -Kind "error" -Id "packages:winget-updates" -Status "partial" -Confidence "high" -Errors @(
                            @{ code = "candidate_query_unverified"; severity = "warning"; retryable = $true; message = "winget upgrade output was not an authoritative package table" }
                        )
                    }
                } else {
                    Add-Record -Kind "error" -Id "packages:winget-updates" -Status "unavailable" -Confidence "medium" -Errors @(
                        @{ code = "candidate_query_failed"; severity = "warning"; retryable = $true; message = "winget upgrade inventory failed" }
                    )
                }
                $null = & winget export --output $Temp --include-versions --accept-source-agreements --disable-interactivity
                $WingetSucceeded = $?
                $WingetExitCode = $LASTEXITCODE
                if (-not $WingetSucceeded -or ($null -ne $WingetExitCode -and $WingetExitCode -ne 0)) {
                    throw "winget export failed"
                }
                if (Test-Path -LiteralPath $Temp) {
                    $Export = Get-Content -LiteralPath $Temp -Raw | ConvertFrom-Json
                    foreach ($Source in @($Export.Sources)) {
                        foreach ($Package in @($Source.Packages)) {
                            $Name = Limit-Text $Package.PackageIdentifier
                            $Candidate = if ($Candidates.ContainsKey([string]$Package.PackageIdentifier)) {
                                Limit-Text $Candidates[[string]$Package.PackageIdentifier]
                            } else {
                                $null
                            }
                            Add-Record -Kind "package" -Id ("winget:" + $Name) -Status "present" `
                                -Confidence $(if ($CandidateQueryAuthoritative) { "high" } else { "medium" }) -Data @{
                                manager = "winget"
                                name = $Name
                                installed_version = Limit-Text $Package.Version
                                candidate_version = $Candidate
                                update_available = if ($CandidateQueryAuthoritative) {
                                    $null -ne $Candidate -and $Candidate -ne [string]$Package.Version
                                } else { $null }
                                source = Limit-Text $Source.SourceDetails.Name
                            } -Evidence @(@{ source = "package-manager"; method = "winget-export+upgrade-list" })
                        }
                    }
                }
            } catch {
                Add-Record -Kind "error" -Id "packages:winget" -Status "error" -Confidence "high" -Errors @(
                    @{ code = "manager_query_failed"; severity = "error"; retryable = $true; message = "winget export failed" }
                )
            } finally {
                Remove-Item -LiteralPath $Temp -Force -ErrorAction SilentlyContinue
            }
        }
    }
}

if (Test-Section "agents") {
    $SkillLockEntries = @()
    $JsmInventory = $null
    $ActivePluginsByAgent = @{ codex = @(); claude = @() }
    $PluginManagerStates = @{ codex = "absent"; claude = "absent" }
    foreach ($Runtime in @("codex", "claude")) {
        $Command = Get-Command $Runtime -ErrorAction SilentlyContinue
        if ($null -eq $Command) {
            Add-Record -Kind "agent_runtime" -Id $Runtime -Status "absent" -Confidence "high" -Data @{ runtime = $Runtime }
        } else {
            $Version = try { (& $Runtime --version 2>$null | Select-Object -First 1) } catch { "unknown" }
            Add-Record -Kind "agent_runtime" -Id $Runtime -Status "present" -Confidence "high" -Data @{
                runtime = $Runtime
                executable = Limit-Text $Command.Source
                version = Limit-Text $Version
            } -Evidence @(@{ source = "runtime"; method = "--version" })
        }
    }

    foreach ($Agent in @("codex", "claude")) {
        if ($null -eq (Get-Command $Agent -ErrorAction SilentlyContinue)) {
            Add-Record -Kind "plugin_manager" -Id $Agent -Status "absent" -Confidence "high" -Data @{
                agent = $Agent; authoritative = $false
            }
            continue
        }
        try {
            $PluginLines = @(& $Agent plugin list --json 2>$null)
            $PluginSucceeded = $?
            $PluginExitCode = $LASTEXITCODE
            if (-not $PluginSucceeded -or ($null -ne $PluginExitCode -and $PluginExitCode -ne 0)) {
                throw "plugin list failed"
            }
            $PluginOutput = ($PluginLines -join "`n").Trim()
            if ([string]::IsNullOrWhiteSpace($PluginOutput)) { throw "plugin list was empty" }
            $ParsedPlugins = $PluginOutput | ConvertFrom-Json
            $Entries = if ($Agent -eq "codex") {
                if (-not $PluginOutput.TrimStart().StartsWith("{") -or $null -eq $ParsedPlugins.installed) {
                    throw "invalid codex plugin list"
                }
                @($ParsedPlugins.installed)
            } else {
                if (-not $PluginOutput.TrimStart().StartsWith("[")) { throw "invalid claude plugin list" }
                @($ParsedPlugins)
            }
            $Normalized = @()
            foreach ($PluginEntry in $Entries) {
                if ($null -eq $PluginEntry) { throw "plugin entry is missing" }
                if ($Agent -eq "codex") {
                    if (
                        $PluginEntry.pluginId -isnot [string] -or [string]::IsNullOrWhiteSpace($PluginEntry.pluginId) -or
                        $PluginEntry.name -isnot [string] -or [string]::IsNullOrWhiteSpace($PluginEntry.name) -or
                        $PluginEntry.marketplaceName -isnot [string] -or [string]::IsNullOrWhiteSpace($PluginEntry.marketplaceName) -or
                        $PluginEntry.version -isnot [string] -or [string]::IsNullOrWhiteSpace($PluginEntry.version) -or
                        $PluginEntry.installed -isnot [bool] -or
                        $PluginEntry.enabled -isnot [bool]
                    ) {
                        throw "invalid codex plugin entry"
                    }
                    if (
                        $null -ne $PluginEntry.source -and
                        $null -ne $PluginEntry.source.path -and
                        $PluginEntry.source.path -isnot [string]
                    ) {
                        throw "invalid codex plugin path"
                    }
                    if (-not $PluginEntry.installed) { continue }
                } else {
                    if (
                        $PluginEntry.id -isnot [string] -or [string]::IsNullOrWhiteSpace($PluginEntry.id) -or
                        $PluginEntry.version -isnot [string] -or [string]::IsNullOrWhiteSpace($PluginEntry.version) -or
                        $PluginEntry.enabled -isnot [bool] -or
                        ($null -ne $PluginEntry.installPath -and $PluginEntry.installPath -isnot [string]) -or
                        (
                            $null -ne $PluginEntry.installedAt -and
                            $PluginEntry.installedAt -isnot [string] -and
                            $PluginEntry.installedAt -isnot [DateTime]
                        ) -or
                        (
                            $null -ne $PluginEntry.lastUpdated -and
                            $PluginEntry.lastUpdated -isnot [string] -and
                            $PluginEntry.lastUpdated -isnot [DateTime]
                        )
                    ) {
                        throw "invalid claude plugin entry"
                    }
                }
                $ManagerId = Limit-Text $(if ($Agent -eq "codex") { $PluginEntry.pluginId } else { $PluginEntry.id })
                $IdParts = $ManagerId -split "@", 2
                if (
                    $Agent -eq "claude" -and
                    ($IdParts.Count -ne 2 -or
                        [string]::IsNullOrWhiteSpace($IdParts[0]) -or
                        [string]::IsNullOrWhiteSpace($IdParts[1]))
                ) {
                    throw "invalid claude plugin id"
                }
                $Name = Limit-Text $(if ($Agent -eq "codex") {
                    $PluginEntry.name
                } else {
                    $IdParts[0]
                })
                $Marketplace = Limit-Text $(if ($Agent -eq "codex") {
                    $PluginEntry.marketplaceName
                } else {
                    $IdParts[1]
                })
                $Normalized += [ordered]@{
                    agent = $Agent
                    manager_id = $ManagerId
                    marketplace = $Marketplace
                    name = $Name
                    installed_version = Limit-Text $PluginEntry.version
                    enabled = $PluginEntry.enabled
                    path = Limit-Text $(if ($Agent -eq "codex") { $PluginEntry.source.path } else { $PluginEntry.installPath })
                    installed_at = Limit-Text $(if ($Agent -eq "claude") { $PluginEntry.installedAt } else { $null })
                    last_updated = Limit-Text $(if ($Agent -eq "claude") { $PluginEntry.lastUpdated } else { $null })
                }
            }
            $ActivePluginsByAgent[$Agent] = @($Normalized)
            $PluginManagerStates[$Agent] = "present"
            Add-Record -Kind "plugin_manager" -Id $Agent -Status "present" -Confidence "high" -Data @{
                agent = $Agent; authoritative = $true; installed_count = @($Normalized).Count
            } -Evidence @(@{ source = "manager-cli"; method = "plugin-list-json" })
        } catch {
            $PluginManagerStates[$Agent] = "unavailable"
            Add-Record -Kind "plugin_manager" -Id $Agent -Status "unavailable" -Confidence "high" -Data @{
                agent = $Agent; authoritative = $false
            } -Errors @(@{ code = "manager_query_failed"; severity = "warning"; retryable = $true; message = "plugin manager inventory failed" })
        }
    }

    $LockPath = Join-Path $HOME ".agents/.skill-lock.json"
    if (Test-Path -LiteralPath $LockPath -PathType Leaf) {
        try {
            $Lock = Get-Content -LiteralPath $LockPath -Raw | ConvertFrom-Json
            $Skills = if ($null -ne $Lock.skills) { $Lock.skills } else { $Lock }
            $SkillLockEntries = @($Skills.PSObject.Properties)
            foreach ($Property in $Skills.PSObject.Properties) {
                if (-not (Test-ManagerEntityName $Property.Name "skills-cli")) {
                    throw "skills-cli lock contains an option-shaped or invalid skill name"
                }
                $Value = $Property.Value
                Add-Record -Kind "skill" -Id ("skills-cli:" + (Limit-Text $Property.Name)) -Status "present" -Confidence "high" -Data @{
                    manager = "skills-cli"
                    name = Limit-Text $Property.Name
                    source = Get-SafeRemote ([string]$Value.source)
                    source_type = Limit-Text $Value.sourceType
                    source_url = Get-SafeRemote ([string]$Value.sourceUrl)
                    skill_path = Limit-Text $Value.skillPath
                    folder_hash = Limit-Text $Value.skillFolderHash
                    installed_at = Limit-Text $Value.installedAt
                    updated_at = Limit-Text $Value.updatedAt
                } -Evidence @(@{ source = "manager-lock"; method = "skills-v3-lock" })
            }
        } catch {
            Add-Record -Kind "error" -Id "agents:skills-cli-lock" -Status "unavailable" -Confidence "high" -Errors @(
                @{ code = "manager_lock_invalid"; severity = "warning"; retryable = $false; message = "skills-cli lock file is invalid" }
            )
        }
    }

    $Jsm = Get-Command jsm -ErrorAction SilentlyContinue
    if ($null -ne $Jsm) {
        try {
            $JsmLines = @(& jsm --json --offline list 2>$null)
            $JsmSucceeded = $?
            $JsmExitCode = $LASTEXITCODE
            if (-not $JsmSucceeded -or ($null -ne $JsmExitCode -and $JsmExitCode -ne 0)) {
                throw "jsm list failed"
            }
            $JsmOutput = ($JsmLines | Out-String)
            $JsmInventory = $JsmOutput | ConvertFrom-Json
            if ($null -eq $JsmInventory.skills -or $JsmInventory.skills -isnot [Array]) {
                throw "jsm skills payload is not an array"
            }
            foreach ($Skill in @($JsmInventory.skills)) {
                if (-not (Test-JsmSkill $Skill)) { throw "jsm skill payload is invalid" }
                $Name = Limit-Text $Skill.name
                Add-Record -Kind "skill" -Id ("jsm:" + $Name) -Status "present" -Confidence "high" -Data @{
                    manager = "jsm"
                    name = $Name
                    version = Limit-Text $Skill.version
                    installed_at = Limit-Text $Skill.installed_at
                    pinned = $(Get-NullableBoolean $Skill.pinned $false)
                    update_available = $(Get-NullableBoolean $Skill.update_available)
                    latest_version = Limit-Text $Skill.latest_version
                    is_saved = $(Get-NullableBoolean $Skill.is_saved)
                    is_jeffreys = $(Get-NullableBoolean $Skill.is_jeffreys)
                    tags = @($Skill.tags | Select-Object -First 128 | ForEach-Object {
                        $Tag = Limit-Text $_
                        if ($Tag.Length -gt 256) { $Tag.Substring(0, 256) } else { $Tag }
                    })
                } -Evidence @(@{ source = "manager-cli"; method = "jsm-offline-list" })
            }
        } catch {
            Add-Record -Kind "error" -Id "agents:jsm" -Status "unavailable" -Confidence "medium" -Errors @(
                @{ code = "manager_query_failed"; severity = "warning"; retryable = $true; message = "jsm offline inventory failed" }
            )
        }
    }

    if ($null -ne $Config.capabilities) {
        foreach ($Property in $Config.capabilities.PSObject.Properties) {
            $Groups = @($Property.Value.groups)
            if ($Groups.Count -gt 0 -and @($Groups | Where-Object { Test-ExactMember $HostGroups $_ }).Count -eq 0) { continue }
            $Name = Limit-Text $Property.Name
            $Providers = @()
            foreach ($Agent in @("codex", "claude")) {
                $Definition = Get-ExactPropertyValue $Property.Value $Agent
                if ($null -eq $Definition) { continue }
                $Provider = Limit-Text $Definition.provider
                $Source = Get-SafeRemote ([string]$Definition.source)
                $ExpectedName = Limit-Text $(if ($null -ne $Definition.skill) {
                    $Definition.skill
                } elseif ($null -ne $Definition.name) {
                    $Definition.name
                } else {
                    $Name
                })
                $Matches = @()
                switch ($Provider) {
                    "plugin" {
                        $PluginName = Split-Path ([string]$Definition.source) -Leaf
                        foreach ($ActivePlugin in @($ActivePluginsByAgent[$Agent])) {
                            if ([bool]$ActivePlugin.enabled -and
                                ($ActivePlugin.name -ceq $PluginName -or
                                $ActivePlugin.manager_id -ceq [string]$Definition.source)) {
                                $Matches += "plugin:$Agent`:$($ActivePlugin.marketplace):$($ActivePlugin.name):$($ActivePlugin.installed_version)"
                            }
                        }
                    }
                    "skills-cli" {
                        foreach ($Entry in $SkillLockEntries) {
                            if ($Entry.Name -ceq $ExpectedName -or
                                [string]$Entry.Value.source -ceq [string]$Definition.source -or
                                [string]$Entry.Value.sourceUrl -ceq [string]$Definition.source) {
                                $Matches += "skills-cli:$($Entry.Name)"
                            }
                        }
                    }
                    "jsm" {
                        foreach ($Skill in @($JsmInventory.skills)) {
                            if ($Skill.name -ceq $ExpectedName -or $Skill.name -ceq [string]$Definition.source) {
                                $Matches += "jsm:$($Skill.name)"
                            }
                        }
                    }
                    { $_ -in @("manual", "plugin-source") } {
                        foreach ($Root in @($Config.skill_roots)) {
                            if (-not (Test-ExactMember @($Root.agents) $Agent)) { continue }
                            $RootGroups = @($Root.groups)
                            if ($RootGroups.Count -gt 0 -and @($RootGroups | Where-Object { Test-ExactMember $HostGroups $_ }).Count -eq 0) { continue }
                            $SkillPath = Join-Path (Resolve-UserPath ([string]$Root.path)) $ExpectedName
                            if (Test-Path -LiteralPath (Join-Path $SkillPath "SKILL.md") -PathType Leaf) {
                                $Matches += "standalone:$($Root.id):$ExpectedName"
                            }
                        }
                    }
                }
                $Matches = @($Matches | Sort-Object -Unique)
                $Providers += @{
                    agent = $Agent
                    provider = $Provider
                    source = $Source
                    expected_name = $ExpectedName
                    observed = $Matches.Count -gt 0
                    matches = $Matches
                    duplicate = $Matches.Count -gt 1
                }
            }
            $ObservedCount = @($Providers | Where-Object { $_.observed }).Count
            $DuplicateCount = @($Providers | Where-Object { $_.duplicate }).Count
            $AuthDependencies = @()
            $ArtifactDependencies = @()
            $DependenciesReady = $true
            foreach ($RequiredAuth in @($Property.Value.requires_auth)) {
                $DependencyStatus = "unconfigured"
                $DependencyReady = $false
                $AuthDefinition = if ($null -ne $Config.auth_artifacts) {
                    Get-ExactPropertyValue $Config.auth_artifacts $RequiredAuth
                } else {
                    $null
                }
                if ($null -ne $AuthDefinition) {
                    $ConfiguredPath = Get-ConfiguredPath $AuthDefinition $HostId
                    $DependencyPath = Resolve-UserPath $ConfiguredPath
                    $Portability = if ($null -eq $AuthDefinition.portability) { "per-machine" } else { [string]$AuthDefinition.portability }
                    if ([string]::IsNullOrWhiteSpace($DependencyPath) -and $Portability -in @("native-store", "per-machine")) {
                        $Health = Get-AuthHealth $AuthDefinition
                        $DependencyStatus = [string]$Health.health
                        $DependencyReady = $DependencyStatus -eq "healthy"
                    } elseif (Test-Path -LiteralPath $DependencyPath) {
                        $Item = Get-Item -LiteralPath $DependencyPath -Force
                        if (($Item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $Item.PSIsContainer) {
                            $DependencyStatus = "partial"
                        } else {
                            $Health = Get-AuthHealth $AuthDefinition
                            $DependencyStatus = [string]$Health.health
                            $DependencyReady = $DependencyStatus -in @("healthy", "not-configured")
                        }
                    } else {
                        $DependencyStatus = "absent"
                    }
                }
                if (-not $DependencyReady) { $DependenciesReady = $false }
                $AuthDependencies += @{
                    id = Limit-Text $RequiredAuth
                    status = Limit-Text $DependencyStatus
                    ready = $DependencyReady
                }
            }
            foreach ($RequiredArtifact in @($Property.Value.requires_artifacts)) {
                $DependencyStatus = "unconfigured"
                $DependencyReady = $false
                $ArtifactDefinition = @($Config.agent_artifacts | Where-Object {
                    $_.id -ceq $RequiredArtifact -and
                    (@($_.groups).Count -eq 0 -or @($_.groups | Where-Object { Test-ExactMember $HostGroups $_ }).Count -gt 0)
                } | Select-Object -First 1)
                if ($ArtifactDefinition.Count -gt 0) {
                    $ConfiguredArtifactPath = Get-ConfiguredPath $ArtifactDefinition[0] $HostId
                    $ArtifactPath = Resolve-UserPath $ConfiguredArtifactPath
                    if ([string]::IsNullOrWhiteSpace($ArtifactPath)) {
                        $DependencyStatus = "unavailable"
                    } elseif (Test-Path -LiteralPath $ArtifactPath) {
                        $Item = Get-Item -LiteralPath $ArtifactPath -Force
                        if (($Item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                            $DependencyStatus = "partial"
                        } else {
                            $DependencyStatus = "present"
                            $DependencyReady = $true
                        }
                    } else {
                        $DependencyStatus = "absent"
                    }
                }
                if (-not $DependencyReady) { $DependenciesReady = $false }
                $ArtifactDependencies += @{
                    id = Limit-Text $RequiredArtifact
                    status = Limit-Text $DependencyStatus
                    ready = $DependencyReady
                }
            }
            $ProviderAvailable = $Providers.Count -gt 0 -and $ObservedCount -eq $Providers.Count
            $ProviderConsistent = $ProviderAvailable -and $DuplicateCount -eq 0
            $CapabilityReady = $ProviderConsistent -and $DependenciesReady
            $CapabilityStatus = if ($CapabilityReady) {
                "present"
            } elseif ($ObservedCount -gt 0 -or $AuthDependencies.Count -gt 0 -or $ArtifactDependencies.Count -gt 0) {
                "partial"
            } else {
                "absent"
            }
            Add-Record -Kind "capability" -Id $Name -Status $CapabilityStatus -Confidence "high" -Data @{
                name = $Name
                available = $ProviderAvailable -and $DependenciesReady
                ready = $CapabilityReady
                consistent = $ProviderConsistent
                providers = $Providers
                dependencies = @{
                    ready = $DependenciesReady
                    auth = $AuthDependencies
                    artifacts = $ArtifactDependencies
                }
            } -Evidence @(@{ source = "configuration+manager-state+dependency-state"; method = "logical-provider-reconciliation" })
        }
    }

    if ($null -ne $Config.skill_roots) {
        foreach ($Definition in @($Config.skill_roots)) {
            $Groups = @($Definition.groups)
            if ($Groups.Count -gt 0 -and @($Groups | Where-Object { Test-ExactMember $HostGroups $_ }).Count -eq 0) { continue }
            $RootId = Limit-Text $Definition.id
            $RootPath = Resolve-UserPath ([string]$Definition.path)
            if (-not (Test-Path -LiteralPath $RootPath -PathType Container)) {
                Add-Record -Kind "skill_root" -Id $RootId -Status "absent" -Confidence "high" -Data @{
                    id = $RootId; path = Limit-Text $RootPath
                }
                continue
            }
            $Manager = Limit-Text $(if (Test-Path -LiteralPath (Join-Path $RootPath ".SKILLS_MANAGED_BY_JSM") -PathType Leaf) {
                "jsm"
            } elseif ($null -ne $Definition.manager) {
                $Definition.manager
            } else {
                "manual"
            })
            foreach ($SkillDirectory in @(Get-ChildItem -LiteralPath $RootPath -Directory -Force -ErrorAction SilentlyContinue |
                Sort-Object Name)) {
                $SkillFile = Join-Path $SkillDirectory.FullName "SKILL.md"
                if (-not (Test-Path -LiteralPath $SkillFile -PathType Leaf)) { continue }
                try {
                    $Origin = (& git -C $SkillDirectory.FullName remote get-url origin 2>$null | Select-Object -First 1)
                    $Digest = Get-DirectoryDigest $SkillDirectory.FullName
                    Add-Record -Kind "skill" -Id ("standalone:" + $RootId + ":" + (Limit-Text $SkillDirectory.Name)) -Status "present" -Confidence "medium" -Data @{
                        name = Limit-Text $SkillDirectory.Name
                        root = $RootId
                        path = Limit-Text $SkillDirectory.FullName
                        manager = $Manager
                        agent_exposure = @($Definition.agents)
                        origin = Get-SafeRemote $Origin
                        updated_at = (Get-Item -LiteralPath $SkillFile).LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ")
                        digest = @{ algorithm = "sha256"; value = $Digest; scope = "directory-files" }
                    } -Evidence @(@{ source = "filesystem"; method = "configured-skill-root+directory-sha256" })
                } catch {
                    Add-Record -Kind "skill" -Id ("standalone:" + $RootId + ":" + (Limit-Text $SkillDirectory.Name)) -Status "partial" -Confidence "medium" -Data @{
                        name = Limit-Text $SkillDirectory.Name; root = $RootId; path = Limit-Text $SkillDirectory.FullName; manager = $Manager
                    } -Errors @(@{ code = "skill_hash_failed"; severity = "warning"; retryable = $true; message = "standalone skill inventory failed" })
                }
            }
        }
    }

    if ($null -ne $Config.agent_artifacts) {
        foreach ($Definition in @($Config.agent_artifacts)) {
            $Groups = @($Definition.groups)
            if ($Groups.Count -gt 0 -and @($Groups | Where-Object { Test-ExactMember $HostGroups $_ }).Count -eq 0) { continue }
            $ArtifactId = Limit-Text $Definition.id
            $ConfiguredArtifactPath = Get-ConfiguredPath $Definition $HostId
            $ArtifactPath = Resolve-UserPath $ConfiguredArtifactPath
            if ([string]::IsNullOrWhiteSpace($ArtifactPath)) {
                Add-Record -Kind "agent_artifact" -Id $ArtifactId -Status "unavailable" -Confidence "high" -Data @{
                    id = $ArtifactId; path = $null; artifact_kind = Limit-Text $Definition.kind
                    agent_exposure = @($Definition.agents)
                } -Errors @(@{ code = "artifact_path_missing"; severity = "warning"; retryable = $false; message = "agent artifact has no path for this host" })
                if ($null -ne $Definition.settings -and $Definition.settings.PSObject.Properties.Count -gt 0) {
                    Add-AgentSettings $Definition $ArtifactId "" -UnknownPath
                }
            } elseif (Test-Path -LiteralPath $ArtifactPath) {
                $Item = Get-Item -LiteralPath $ArtifactPath -Force
                if (($Item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    Add-Record -Kind "agent_artifact" -Id $ArtifactId -Status "partial" -Confidence "medium" -Data @{
                        id = $ArtifactId; path = Limit-Text $ArtifactPath; artifact_kind = Limit-Text $Definition.kind
                        agent_exposure = @($Definition.agents)
                    } -Errors @(@{ code = "symlink_not_followed"; severity = "warning"; retryable = $false; message = "agent artifact path is a link" })
                    if ($null -ne $Definition.settings -and
                        $Definition.settings.PSObject.Properties.Count -gt 0) {
                        Add-AgentSettings $Definition $ArtifactId $ArtifactPath -LinkedPath
                    }
                } else {
                    $Digest = if ($Item.PSIsContainer) {
                        Get-DirectoryDigest $ArtifactPath
                    } else {
                        (Get-FileHash -LiteralPath $ArtifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
                    }
                    Add-Record -Kind "agent_artifact" -Id $ArtifactId -Status "present" -Confidence $(if ($Item.PSIsContainer) { "medium" } else { "high" }) -Data @{
                        id = $ArtifactId
                        path = Limit-Text $ArtifactPath
                        artifact_kind = Limit-Text $Definition.kind
                        agent_exposure = @($Definition.agents)
                        updated_at = $Item.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ")
                        digest = @{ algorithm = "sha256"; value = $Digest; scope = $(if ($Item.PSIsContainer) { "directory-files" } else { "raw-bytes" }) }
                    } -Evidence @(@{ source = "filesystem"; method = "configured-agent-artifact+sha256" })
                    if ($null -ne $Definition.settings -and
                        $Definition.settings.PSObject.Properties.Count -gt 0) {
                        Add-AgentSettings $Definition $ArtifactId $ArtifactPath
                    }
                }
            } else {
                Add-Record -Kind "agent_artifact" -Id $ArtifactId -Status "absent" -Confidence "high" -Data @{
                    id = $ArtifactId; path = Limit-Text $ArtifactPath; artifact_kind = Limit-Text $Definition.kind
                    agent_exposure = @($Definition.agents)
                }
                if ($null -ne $Definition.settings -and $Definition.settings.PSObject.Properties.Count -gt 0) {
                    Add-AgentSettings $Definition $ArtifactId $ArtifactPath
                }
            }
        }
    }

    $SeenPlugins = @{}
    foreach ($Agent in @("codex", "claude")) {
        $Cache = Join-Path $HOME $(if ($Agent -eq "codex") { ".codex/plugins/cache" } else { ".claude/plugins/cache" })
        foreach ($ActivePlugin in @($ActivePluginsByAgent[$Agent])) {
            $CachePath = Join-Path $Cache ([IO.Path]::Combine(
                [string]$ActivePlugin.marketplace,
                [string]$ActivePlugin.name,
                [string]$ActivePlugin.installed_version
            ))
            $CacheItem = if (Test-Path -LiteralPath $CachePath -PathType Container) {
                Get-Item -LiteralPath $CachePath -Force
            } else {
                $null
            }
            $InferredInstalledAt = if ($null -ne $ActivePlugin.installed_at -or $null -eq $CacheItem) {
                $null
            } else {
                $CacheItem.CreationTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ")
            }
            $Key = "$Agent`:$($ActivePlugin.marketplace):$($ActivePlugin.name):$($ActivePlugin.installed_version)"
            $SeenPlugins[$Key] = $true
            Add-Record -Kind "plugin" -Id $Key -Status "present" -Confidence "high" -Data @{
                agent = $Agent
                manager_id = Limit-Text $ActivePlugin.manager_id
                marketplace = Limit-Text $ActivePlugin.marketplace
                name = Limit-Text $ActivePlugin.name
                installed_version = Limit-Text $ActivePlugin.installed_version
                enabled = [bool]$ActivePlugin.enabled
                path = Limit-Text $ActivePlugin.path
                installed_at = Limit-Text $ActivePlugin.installed_at
                last_updated = Limit-Text $ActivePlugin.last_updated
                active = $true
                install_state = "installed"
                inventory_source = "manager"
                cache_path = Limit-Text $(if ($null -ne $CacheItem) { $CachePath } else { $null })
                inferred_installed_at = $InferredInstalledAt
                inferred_installed_at_evidence = $(if ($null -ne $InferredInstalledAt) { "filesystem_creation_time" } else { $null })
                inferred_installed_at_confidence = $(if ($null -ne $InferredInstalledAt) { "low" } else { "unknown" })
            } -Evidence @(@{ source = "manager-cli"; method = "plugin-list-json" })
        }

        if (-not (Test-Path -LiteralPath $Cache -PathType Container)) { continue }
        $CacheMarketplaces = @(Get-ChildItem -LiteralPath $Cache -Directory -Force -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -ne "plugin-eval" })
        foreach ($MarketplaceDirectory in $CacheMarketplaces) {
            foreach ($PluginDirectory in @(Get-ChildItem -LiteralPath $MarketplaceDirectory.FullName -Directory -Force -ErrorAction SilentlyContinue)) {
                foreach ($VersionDirectory in @(Get-ChildItem -LiteralPath $PluginDirectory.FullName -Directory -Force -ErrorAction SilentlyContinue)) {
                    $HasManifest = $false
                    foreach ($ManifestDirectory in @(".codex-plugin", ".claude-plugin")) {
                        if (Test-Path -LiteralPath (Join-Path $VersionDirectory.FullName "$ManifestDirectory/plugin.json") -PathType Leaf) {
                            $HasManifest = $true
                            break
                        }
                    }
                    if (-not $HasManifest) { continue }
                    $Plugin = $PluginDirectory.Name
                    $Marketplace = $MarketplaceDirectory.Name
                    $Version = $VersionDirectory.Name
                    $Key = "$Agent`:$Marketplace`:$Plugin`:$Version"
                    if ($SeenPlugins.ContainsKey($Key)) { continue }
                    $SeenPlugins[$Key] = $true
                    $ManagerUnverified = $PluginManagerStates[$Agent] -eq "unavailable"
                    Add-Record -Kind "plugin_cache" -Id $Key -Status "present" -Confidence "low" -Data @{
                        agent = $Agent
                        marketplace = Limit-Text $Marketplace
                        name = Limit-Text $Plugin
                        cached_version = Limit-Text $Version
                        path = Limit-Text $VersionDirectory.FullName
                        active = $(if ($ManagerUnverified) { $null } else { $false })
                        install_state = $(if ($ManagerUnverified) { "manager-unverified" } else { "cache-only" })
                        inventory_source = "cache"
                        inferred_cached_at = $VersionDirectory.CreationTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ")
                        inferred_cached_at_evidence = "filesystem_creation_time"
                        inferred_cached_at_confidence = "low"
                    } -Evidence @(@{ source = "filesystem"; method = "cache-directory-observation+creation-time-inference" })
                }
            }
        }
    }
}

if (Test-Section "auth") {
    if ($null -ne $Config.auth_artifacts) {
        foreach ($Property in $Config.auth_artifacts.PSObject.Properties) {
            $Name = Limit-Text $Property.Name
            $Definition = $Property.Value
            $Strategy = if ($null -eq $Definition.strategy) { "ignore" } else { Limit-Text $Definition.strategy }
            $Portability = if ($null -eq $Definition.portability) { "per-machine" } else { Limit-Text $Definition.portability }
            $ConfiguredPath = Get-ConfiguredPath $Definition $HostId
            $Path = Resolve-UserPath $ConfiguredPath
            if ([string]::IsNullOrWhiteSpace($Path) -and $Portability -in @("native-store", "per-machine")) {
                $Health = Get-AuthHealth $Definition
                $ReauthRequired = $Strategy -ceq "reauth" -and $Health.health -eq "unhealthy"
                Add-Record -Kind "auth_artifact" -Id $Name -Status $(if ($Health.health -eq "healthy") { "present" } elseif ($ReauthRequired) { "absent" } else { "partial" }) -Confidence "high" -Data @{
                    tool = $Name; path = $null; strategy = $Strategy; portability = $Portability
                    type = "native-status"; health = $Health.health; verify_exit_code = $Health.verify_exit_code
                    reauth_required = $ReauthRequired
                    manual_action = $(if ($ReauthRequired) { "run the configured native login on this host" } else { $null })
                } -Evidence @(@{ source = "native-cli"; method = "configured-auth-status" })
            } elseif (Test-Path -LiteralPath $Path -PathType Leaf) {
                $Item = Get-Item -LiteralPath $Path -Force
                if (($Item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    Add-Record -Kind "auth_artifact" -Id $Name -Status "partial" -Confidence "medium" -Data @{
                        tool = $Name; path = Limit-Text $Path; strategy = $Strategy
                        portability = $Portability
                        type = "symlink"; health = "not-run"; verify_exit_code = $null
                    } -Errors @(@{ code = "symlink_not_followed"; severity = "warning"; retryable = $false; message = "credential path is a link" })
                } else {
                    $Hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
                    $Health = Get-AuthHealth $Definition
                    $Acl = try { Get-Acl -LiteralPath $Path -ErrorAction Stop } catch { $null }
                    $AclErrors = @()
                    $AclAccess = "unavailable"
                    $AclFingerprint = $null
                    if ($null -eq $Acl) {
                        $AclErrors += @{ code = "acl_unavailable"; severity = "warning"; retryable = $true; message = "credential ACL could not be read" }
                    } else {
                        $AclUnresolved = $false
                        $BroadAccess = $false
                        [string[]]$RuleLines = @($Acl.Access | ForEach-Object {
                            $Rule = $_
                            $Sid = try {
                                $Rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
                            } catch {
                                $AclUnresolved = $true
                                "unresolved:" + [string]$Rule.IdentityReference
                            }
                            if ($Sid -in @("S-1-1-0", "S-1-5-11", "S-1-5-32-545") -and
                                $Rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow) {
                                $UnsafeMask = [Security.AccessControl.FileSystemRights]::Read -bor
                                    [Security.AccessControl.FileSystemRights]::Write -bor
                                    [Security.AccessControl.FileSystemRights]::Modify -bor
                                    [Security.AccessControl.FileSystemRights]::FullControl
                                if (($Rule.FileSystemRights -band $UnsafeMask) -ne 0) { $BroadAccess = $true }
                            }
                            "$Sid|$([int]$Rule.AccessControlType)|$([int64]$Rule.FileSystemRights)|$([int]$Rule.InheritanceFlags)|$([int]$Rule.PropagationFlags)|$($Rule.IsInherited)"
                        })
                        if ($RuleLines.Count -gt 1) { [Array]::Sort($RuleLines, [StringComparer]::Ordinal) }
                        $AclFingerprint = Get-TextSha256 (($RuleLines -join "`n") + "`n")
                        $AclAccess = if ($BroadAccess) { "broad-access" } elseif ($AclUnresolved) { "unknown" } else { "restricted" }
                        if ($BroadAccess) {
                            $AclErrors += @{ code = "acl_broad_access"; severity = "warning"; retryable = $false; message = "credential ACL grants broad read or write access" }
                        }
                        if ($AclUnresolved) {
                            $AclErrors += @{ code = "acl_identity_unresolved"; severity = "warning"; retryable = $false; message = "credential ACL contains an identity that could not be resolved to a SID" }
                        }
                    }
                    Add-Record -Kind "auth_artifact" -Id $Name -Status $(if ($AclErrors.Count -eq 0) { "present" } else { "partial" }) -Confidence "medium" -Data @{
                        tool = $Name
                        path = Limit-Text $Path
                        strategy = $Strategy
                        portability = $Portability
                        type = "file"
                        owner = Limit-Text $Acl.Owner
                        acl_inheritance_protected = $Acl.AreAccessRulesProtected
                        acl_access = $AclAccess
                        acl_fingerprint = if ($null -eq $AclFingerprint) { $null } else {
                            @{ algorithm = "sha256"; value = $AclFingerprint; scope = $(if ($AclUnresolved) { "normalized-access-rules" } else { "canonical-sid-access-rules" }) }
                        }
                        size = $Item.Length
                        mtime = $Item.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ")
                        digest = @{ algorithm = "sha256"; value = $Hash; scope = "raw-bytes" }
                        health = $Health.health
                        verify_exit_code = $Health.verify_exit_code
                    } -Evidence @(@{ source = "filesystem"; method = "metadata+sha256+acl" }) -Errors $AclErrors
                }
            } else {
                Add-Record -Kind "auth_artifact" -Id $Name -Status "absent" -Confidence "medium" -Data @{
                    tool = $Name; path = Limit-Text $Path; strategy = $Strategy
                    portability = $Portability
                    health = "not-run"; verify_exit_code = $null
                }
            }
        }
    }
}

if (Test-Section "projects") {
    $DevRoot = Resolve-UserPath ([string]$Machine.dev_root)
    if ([string]::IsNullOrWhiteSpace($DevRoot)) {
        Add-Record -Kind "error" -Id "projects:dev-root" -Status "unavailable" -Confidence "high" -Errors @(
            @{ code = "dev_root_missing"; severity = "error"; retryable = $false; message = "project inventory requires a configured dev_root" }
        )
    } elseif ($null -ne $Config.projects) {
        foreach ($Property in $Config.projects.PSObject.Properties) {
            $Definition = $Property.Value
            $Groups = @($Definition.groups)
            if ($Groups.Count -gt 0 -and @($Groups | Where-Object { Test-ExactMember $HostGroups $_ }).Count -eq 0) { continue }
            $Name = Limit-Text $Property.Name
            $Path = Join-Path $DevRoot ([string]$Definition.path)
            if (Test-Path -LiteralPath $Path -PathType Container) {
                try {
                    if ($null -eq (Get-Command git -ErrorAction SilentlyContinue)) { throw "git is not installed" }
                    $HeadLines = @(& git -C $Path rev-parse HEAD 2>$null)
                    $GitSucceeded = $?
                    $GitExitCode = $LASTEXITCODE
                    $Head = ($HeadLines | Select-Object -First 1)
                    if (-not $GitSucceeded -or
                        ($null -ne $GitExitCode -and $GitExitCode -ne 0) -or
                        [string]::IsNullOrWhiteSpace($Head)) {
                        throw "not a Git checkout"
                    }
                    $Tree = (& git -C $Path rev-parse "HEAD^{tree}" 2>$null | Select-Object -First 1)
                    $Branch = (& git -C $Path symbolic-ref --short -q HEAD 2>$null | Select-Object -First 1)
                    if ([string]::IsNullOrWhiteSpace($Branch)) { $Branch = "detached" }
                    $Origin = (& git -C $Path remote get-url origin 2>$null | Select-Object -First 1)
                    $Dirty = @(& git -C $Path status --porcelain 2>$null).Count
                    $ExpectedSource = Get-SafeRemote ([string]$Definition.source)
                    $SafeOrigin = Get-SafeRemote $Origin
                    $OriginMatches = (Get-CanonicalGitSource $ExpectedSource) -eq (Get-CanonicalGitSource $SafeOrigin)
                    $Upstream = (& git -C $Path rev-parse --abbrev-ref --symbolic-full-name "@{upstream}" 2>$null | Select-Object -First 1)
                    $Ahead = 0
                    $Behind = 0
                    $SyncState = "local-no-upstream"
                    if (-not [string]::IsNullOrWhiteSpace($Upstream)) {
                        $Counts = ((& git -C $Path rev-list --left-right --count ("HEAD..." + $Upstream) 2>$null | Select-Object -First 1) -split '\s+')
                        if ($Counts.Count -ge 2) {
                            $Ahead = [int]$Counts[0]
                            $Behind = [int]$Counts[1]
                        }
                        $SyncState = if ($Ahead -gt 0 -and $Behind -gt 0) {
                            "local-tracking-diverged"
                        } elseif ($Ahead -gt 0) {
                            "local-tracking-ahead"
                        } elseif ($Behind -gt 0) {
                            "local-tracking-behind"
                        } else {
                            "local-tracking-up-to-date"
                        }
                    }
                    $Readiness = if ($SyncState -eq "local-tracking-diverged") {
                        "diverged"
                    } elseif ($Branch -eq "detached") {
                        "detached"
                    } elseif (-not $OriginMatches) {
                        "wrong-origin"
                    } elseif ($Dirty -gt 0) {
                        "dirty"
                    } else {
                        "ready"
                    }
                    $CodexRequired = [bool]$Definition.codex
                    Add-Record -Kind "project" -Id $Name -Status $(if ($Readiness -eq "ready") { "present" } else { "partial" }) -Confidence "high" -Data @{
                        name = $Name
                        path = Limit-Text $Path
                        expected_source = $ExpectedSource
                        origin = $SafeOrigin
                        origin_matches = $OriginMatches
                        head = Limit-Text $Head
                        tree = Limit-Text $Tree
                        branch = Limit-Text $Branch
                        upstream = Limit-Text $Upstream
                        ahead = $Ahead
                        behind = $Behind
                        sync_state = $SyncState
                        tracking_freshness = "unknown"
                        dirty_count = $Dirty
                        repository_readiness = $Readiness
                        codex_required = $CodexRequired
                        codex_saved_project_status = $(if ($CodexRequired) { "requires-controller-check" } else { "not-required" })
                    } -Evidence @(@{ source = "git"; method = "rev-parse+status" })
                } catch {
                    Add-Record -Kind "project" -Id $Name -Status "partial" -Confidence "high" -Data @{
                        name = $Name; path = Limit-Text $Path; expected_source = Get-SafeRemote ([string]$Definition.source)
                    } -Errors @(@{ code = "not_git_repository"; severity = "error"; retryable = $false; message = "configured path is not a readable Git checkout" })
                }
            } else {
                Add-Record -Kind "project" -Id $Name -Status "absent" -Confidence "high" -Data @{
                    name = $Name; path = Limit-Text $Path; expected_source = Get-SafeRemote ([string]$Definition.source)
                }
            }
        }
    }
}

if (Test-Section "startup") {
    try {
        foreach ($Task in @(Get-ScheduledTask -ErrorAction Stop)) {
            $Triggers = @($Task.Triggers | ForEach-Object {
                $ClassName = $_.CimClass.CimClassName
                if ($ClassName -match 'BootTrigger$') { "boot" }
                elseif ($ClassName -match 'LogonTrigger$') { "logon" }
            } | Where-Object { $_ } | Sort-Object -Unique)
            if ($Triggers.Count -eq 0) { continue }
            $TaskId = (Limit-Text $Task.TaskPath) + (Limit-Text $Task.TaskName)
            $Info = try { Get-ScheduledTaskInfo -InputObject $Task -ErrorAction Stop } catch { $null }
            $DefinitionXml = try { Export-ScheduledTask -TaskName $Task.TaskName -TaskPath $Task.TaskPath -ErrorAction Stop } catch { "" }
            $Actions = @($Task.Actions | ForEach-Object {
                @{
                    execute = Limit-Text $_.Execute
                    arguments_digest = if ([string]::IsNullOrEmpty([string]$_.Arguments)) { $null } else { Get-TextSha256 ([string]$_.Arguments) }
                    working_directory = Limit-Text $_.WorkingDirectory
                }
            })
            Add-Record -Kind "startup_task" -Id $TaskId -Status "present" -Confidence "high" -Data @{
                scheduler = "windows-scheduled-task"
                scope = "system-or-user"
                path = Limit-Text $Task.TaskPath
                label = Limit-Text $Task.TaskName
                enabled = $Task.Settings.Enabled
                state = Limit-Text $Task.State
                triggers = $Triggers
                actions = $Actions
                next_run = Limit-Text $Info.NextRunTime
                last_run = Limit-Text $Info.LastRunTime
                last_result = $Info.LastTaskResult
                definition_digest = if ([string]::IsNullOrEmpty($DefinitionXml)) {
                    $null
                } else {
                    @{ algorithm = "sha256"; value = Get-TextSha256 $DefinitionXml; scope = "task-xml" }
                }
            } -Evidence @(@{ source = "scheduler"; method = "Get-ScheduledTask" })
        }
    } catch {
        Add-Record -Kind "error" -Id "startup:scheduled-tasks" -Status "unavailable" -Confidence "high" -Errors @(
            @{ code = "scheduler_query_failed"; severity = "warning"; retryable = $true; message = "scheduled task inventory failed" }
        )
    }
    foreach ($StartupDefinition in @(
        @{ scope = "user"; path = [Environment]::GetFolderPath("Startup") },
        @{ scope = "common"; path = [Environment]::GetFolderPath("CommonStartup") }
    )) {
        if ([string]::IsNullOrWhiteSpace($StartupDefinition.path) -or
            -not (Test-Path -LiteralPath $StartupDefinition.path -PathType Container)) { continue }
        foreach ($Item in @(Get-ChildItem -LiteralPath $StartupDefinition.path -File -Force -ErrorAction SilentlyContinue)) {
            Add-Record -Kind "startup_task" -Id ("startup-folder:" + $StartupDefinition.scope + ":" + (Limit-Text $Item.Name)) -Status "present" -Confidence "high" -Data @{
                scheduler = "windows-startup-folder"
                scope = $StartupDefinition.scope
                label = Limit-Text $Item.Name
                source_definition_path = Limit-Text $Item.FullName
                definition_mtime = $Item.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ")
                definition_digest = @{
                    algorithm = "sha256"
                    value = (Get-FileHash -LiteralPath $Item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                    scope = "raw-bytes"
                }
            } -Evidence @(@{ source = "filesystem"; method = "startup-folder-scan" })
        }
    }
    foreach ($RunKey in @(
        @{ scope = "user"; path = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" },
        @{ scope = "user-once"; path = "HKCU:\Software\Microsoft\Windows\CurrentVersion\RunOnce" },
        @{ scope = "machine"; path = "HKLM:\Software\Microsoft\Windows\CurrentVersion\Run" },
        @{ scope = "machine-once"; path = "HKLM:\Software\Microsoft\Windows\CurrentVersion\RunOnce" }
    )) {
        if (-not (Test-Path -LiteralPath $RunKey.path)) { continue }
        $Values = Get-ItemProperty -LiteralPath $RunKey.path -ErrorAction SilentlyContinue
        foreach ($Property in @($Values.PSObject.Properties | Where-Object { $_.Name -notmatch '^PS(Path|ParentPath|ChildName|Drive|Provider)$' })) {
            $CommandValue = [string]$Property.Value
            Add-Record -Kind "startup_task" -Id ("run-key:" + $RunKey.scope + ":" + (Limit-Text $Property.Name)) -Status "present" -Confidence "high" -Data @{
                scheduler = "windows-run-key"
                scope = $RunKey.scope
                label = Limit-Text $Property.Name
                source_definition_path = $RunKey.path
                definition_digest = @{
                    algorithm = "sha256"
                    value = Get-TextSha256 $CommandValue
                    scope = "registry-value"
                }
            } -Evidence @(@{ source = "registry"; method = "run-key-name+value-hash" })
        }
    }
}

if (Test-Section "chezmoi") {
    $Chezmoi = Get-Command chezmoi -ErrorAction SilentlyContinue
    $SourcePath = if (-not [string]::IsNullOrWhiteSpace($env:CHEZMOI_SOURCE_DIR)) {
        Resolve-UserPath $env:CHEZMOI_SOURCE_DIR
    } elseif ($null -ne $Chezmoi) {
        $ResolvedSource = (& chezmoi source-path 2>$null | Select-Object -First 1)
        if ([string]::IsNullOrWhiteSpace($ResolvedSource)) { Join-Path $HOME ".local/share/chezmoi" } else { $ResolvedSource }
    } else {
        Join-Path $HOME ".local/share/chezmoi"
    }
    if (Test-Path -LiteralPath $SourcePath -PathType Container) {
        try {
            $HeadLines = @(& git -C $SourcePath rev-parse HEAD 2>$null)
            $GitSucceeded = $?
            $GitExitCode = $LASTEXITCODE
            $Head = ($HeadLines | Select-Object -First 1)
            if (-not $GitSucceeded -or
                ($null -ne $GitExitCode -and $GitExitCode -ne 0) -or
                [string]::IsNullOrWhiteSpace($Head)) {
                throw "chezmoi source is not a Git repository"
            }
            $Dirty = @(& git -C $SourcePath status --porcelain 2>$null).Count
            Add-Record -Kind "file" -Id "chezmoi:source" -Status $(if ($Dirty -eq 0) { "present" } else { "partial" }) -Confidence "medium" -Data @{
                role = "chezmoi-source"
                path = Limit-Text $SourcePath
                head = Limit-Text $Head
                dirty_count = $Dirty
            } -Evidence @(@{ source = "git"; method = "rev-parse+status" })
        } catch {
            Add-Record -Kind "file" -Id "chezmoi:source" -Status "partial" -Confidence "medium" -Data @{
                role = "chezmoi-source"; path = Limit-Text $SourcePath
            } -Errors @(@{ code = "not_git_repository"; severity = "warning"; retryable = $false; message = "chezmoi source is not a readable Git checkout" })
        }
    } else {
        Add-Record -Kind "file" -Id "chezmoi:source" -Status "absent" -Confidence "medium" -Data @{
            role = "chezmoi-source"; path = Limit-Text $SourcePath
        }
    }
    if ($null -eq $Chezmoi) {
        Add-Record -Kind "chezmoi_state" -Id "live" -Status "absent" -Confidence "high" -Data @{ tool_available = $false }
    } else {
        $StatusFile = Join-Path ([IO.Path]::GetTempPath()) ("machine-utilities-chezmoi-" + [Guid]::NewGuid().ToString("N"))
        try {
            $StatusOutput = @(& chezmoi status 2>$null)
            $ChezmoiSucceeded = $?
            $ChezmoiExitCode = $LASTEXITCODE
            if (-not $ChezmoiSucceeded -or ($null -ne $ChezmoiExitCode -and $ChezmoiExitCode -ne 0)) {
                throw "chezmoi status failed"
            }
            $StatusText = if ($StatusOutput.Count -gt 0) { ($StatusOutput -join "`n") + "`n" } else { "" }
            [IO.File]::WriteAllText($StatusFile, $StatusText, [Text.UTF8Encoding]::new($false))
            $StatusLines = @(Get-Content -LiteralPath $StatusFile | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
            $Codes = @($StatusLines | Group-Object { if ($_.Length -ge 2) { $_.Substring(0, 2) } else { $_ } } |
                Sort-Object Name | ForEach-Object { @{ code = Limit-Text $_.Name; count = $_.Count } })
            Add-Record -Kind "chezmoi_state" -Id "live" -Status "present" -Confidence "high" -Data @{
                source_path = Limit-Text $SourcePath
                drift_count = $StatusLines.Count
                status_codes = $Codes
                status_digest = @{
                    algorithm = "sha256"
                    value = (Get-FileHash -LiteralPath $StatusFile -Algorithm SHA256).Hash.ToLowerInvariant()
                    scope = "chezmoi-status-output"
                }
            } -Evidence @(@{ source = "chezmoi"; method = "source-path+status" })
        } catch {
            Add-Record -Kind "chezmoi_state" -Id "live" -Status "unavailable" -Confidence "high" -Data @{
                source_path = Limit-Text $SourcePath
            } -Errors @(@{ code = "chezmoi_status_failed"; severity = "warning"; retryable = $true; message = "chezmoi status failed" })
        } finally {
            Remove-Item -LiteralPath $StatusFile -Force -ErrorAction SilentlyContinue
        }
    }
}

$HasProblems = $script:HasProblems
Add-Record -Kind "operation" -Id "collect" -Status $(if ($HasProblems) { "partial" } else { "present" }) -Confidence "high" -Data @{
    run_id = $SnapshotId
    host_id = $HostId
    scope = $Sections
    phase = "collect"
    operation_status = $(if ($HasProblems) { "partial" } else { "completed" })
    transport = Limit-Text $Machine.transport
    task_id = $null
    correlation_id = $null
}

$script:Records |
    Sort-Object host_id, kind, id |
    ForEach-Object {
        if (-not (Test-BoundedStrings $_)) { throw "Inventory record contains an oversized string" }
        $Json = $_ | ConvertTo-Json -Compress -Depth 10
        if ([Text.Encoding]::UTF8.GetByteCount($Json) -gt 65536) { throw "Inventory record exceeds 65536 bytes" }
        $Json
    }
