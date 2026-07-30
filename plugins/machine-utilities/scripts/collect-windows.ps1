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

function Test-JsmSkill([object]$Skill) {
    if ($null -eq $Skill -or [string]::IsNullOrWhiteSpace([string]$Skill.name)) { return $false }
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
        $ExitCode = [int](Receive-Job -Job $Job | Select-Object -Last 1)
        return @{ health = $(if ($ExitCode -eq 0) { "healthy" } else { "unhealthy" }); verify_exit_code = $ExitCode }
    } finally {
        Remove-Job -Job $Job -Force -ErrorAction SilentlyContinue
    }
}

function Test-BoundedStrings([object]$Value) {
    if ($null -eq $Value) { return $true }
    if ($Value -is [string]) { return $Value.Length -le 8192 }
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

function Assert-WorkerConfig([object]$Value) {
    if ($HostId -notmatch '^[A-Za-z0-9._-]+$' -or $Value.version -ne 1 -or $null -eq $Value.machines.$HostId) {
        throw "Invalid version 1 configuration or unknown host"
    }
    if (-not (Test-BoundedStrings $Value)) { throw "Configuration contains an oversized string" }
    $ConfiguredMachine = $Value.machines.$HostId
    if ($ConfiguredMachine.platform -ne "windows" -or
        $ConfiguredMachine.transport -ne "codex-remote-control" -or
        [string]::IsNullOrWhiteSpace([string]$ConfiguredMachine.codex_host)) {
        throw "Windows worker requires direct codex-remote-control transport"
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
            if ($Property.Name -notmatch '^[A-Za-z0-9._-]+$') { throw "Invalid capability configuration" }
            $ProviderCount = 0
            foreach ($Agent in @("codex", "claude")) {
                $Definition = $Property.Value.$Agent
                if ($null -eq $Definition) { continue }
                $ProviderCount++
                if (@("plugin", "skills-cli", "jsm", "manual", "plugin-source") -notcontains [string]$Definition.provider -or
                    [string]::IsNullOrWhiteSpace([string]$Definition.source) -or
                    [string]$Definition.source -match '\?' -or
                    [string]$Definition.source -match '^[A-Za-z][A-Za-z0-9+.-]*://(?!git@)[^/@]+@' -or
                    ($null -ne $Definition.skill -and [string]$Definition.skill -notmatch '^[A-Za-z0-9._-]+$') -or
                    ($null -ne $Definition.name -and [string]$Definition.name -notmatch '^[A-Za-z0-9._-]+$')) {
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
            if ([string]$Definition.id -notmatch '^[A-Za-z0-9._-]+$' -or
                [string]::IsNullOrWhiteSpace([string]$Definition.path) -or
                @("agent-definition", "instruction", "config") -notcontains [string]$Definition.kind -or
                @($Definition.agents | Where-Object { $_ -notin @("codex", "claude") }).Count -gt 0) {
                throw "Invalid agent artifact configuration"
            }
        }
    }
    if ($null -ne $Value.auth_artifacts) {
        foreach ($Property in $Value.auth_artifacts.PSObject.Properties) {
            $Verify = @($Property.Value.verify)
            $Strategy = if ($null -eq $Property.Value.strategy) { "ignore" } else { [string]$Property.Value.strategy }
            $Portability = if ($null -eq $Property.Value.portability) { "per-machine" } else { [string]$Property.Value.portability }
            if ($Property.Name -notmatch '^[A-Za-z0-9._-]+$' -or
                [string]::IsNullOrWhiteSpace([string]$Property.Value.path) -or
                @("chezmoi", "encrypted-install", "reauth", "ignore") -notcontains $Strategy -or
                @("declarative", "secret-reference", "portable-session", "native-store", "per-machine", "regenerable-cache") -notcontains $Portability -or
                $Verify.Count -gt 32 -or
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
$Machine = $Config.machines.$HostId
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
    $Managers = @($Config.machines.$HostId.package_managers)
    if ($Managers -contains "winget") {
        $Winget = Get-Command winget -ErrorAction SilentlyContinue
        if ($null -eq $Winget) {
            Add-Record -Kind "error" -Id "packages:winget" -Status "unavailable" -Confidence "high" -Errors @(
                @{ code = "manager_missing"; severity = "warning"; retryable = $false; message = "winget is not installed" }
            )
        } else {
            $Temp = Join-Path ([IO.Path]::GetTempPath()) ("machine-utilities-winget-" + [Guid]::NewGuid().ToString("N") + ".json")
            try {
                & winget export --output $Temp --include-versions --accept-source-agreements --disable-interactivity | Out-Null
                if ($LASTEXITCODE -ne 0) { throw "winget export exited $LASTEXITCODE" }
                if (Test-Path -LiteralPath $Temp) {
                    $Export = Get-Content -LiteralPath $Temp -Raw | ConvertFrom-Json
                    foreach ($Source in @($Export.Sources)) {
                        foreach ($Package in @($Source.Packages)) {
                            $Name = Limit-Text $Package.PackageIdentifier
                            Add-Record -Kind "package" -Id ("winget:" + $Name) -Status "present" -Confidence "high" -Data @{
                                manager = "winget"
                                name = $Name
                                installed_version = Limit-Text $Package.Version
                                source = Limit-Text $Source.SourceDetails.Name
                            } -Evidence @(@{ source = "package-manager"; method = "winget-export" })
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

    $LockPath = Join-Path $HOME ".agents/.skill-lock.json"
    if (Test-Path -LiteralPath $LockPath -PathType Leaf) {
        try {
            $Lock = Get-Content -LiteralPath $LockPath -Raw | ConvertFrom-Json
            $Skills = if ($null -ne $Lock.skills) { $Lock.skills } else { $Lock }
            $SkillLockEntries = @($Skills.PSObject.Properties)
            foreach ($Property in $Skills.PSObject.Properties) {
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
            $JsmOutput = (& jsm --json --offline list 2>$null | Out-String)
            if ($LASTEXITCODE -ne 0) { throw "jsm list exited $LASTEXITCODE" }
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
            if ($Groups.Count -gt 0 -and @($Groups | Where-Object { $HostGroups -contains $_ }).Count -eq 0) { continue }
            $Name = Limit-Text $Property.Name
            $Providers = @()
            foreach ($Agent in @("codex", "claude")) {
                $Definition = $Property.Value.$Agent
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
                        $Cache = Join-Path $HOME $(if ($Agent -eq "codex") { ".codex/plugins/cache" } else { ".claude/plugins/cache" })
                        $PluginName = Split-Path ([string]$Definition.source) -Leaf
                        if (Test-Path -LiteralPath $Cache -PathType Container) {
                            foreach ($Marketplace in @(Get-ChildItem -LiteralPath $Cache -Directory -Force -ErrorAction SilentlyContinue)) {
                                $PluginDirectory = Join-Path $Marketplace.FullName $PluginName
                                if (Test-Path -LiteralPath $PluginDirectory -PathType Container) {
                                    foreach ($VersionDirectory in @(Get-ChildItem -LiteralPath $PluginDirectory -Directory -Force -ErrorAction SilentlyContinue)) {
                                        $Matches += "plugin:$Agent`:$($Marketplace.Name):$PluginName`:$($VersionDirectory.Name)"
                                    }
                                }
                            }
                        }
                    }
                    "skills-cli" {
                        foreach ($Entry in $SkillLockEntries) {
                            if ($Entry.Name -eq $ExpectedName -or
                                [string]$Entry.Value.source -eq [string]$Definition.source -or
                                [string]$Entry.Value.sourceUrl -eq [string]$Definition.source) {
                                $Matches += "skills-cli:$($Entry.Name)"
                            }
                        }
                    }
                    "jsm" {
                        foreach ($Skill in @($JsmInventory.skills)) {
                            if ($Skill.name -eq $ExpectedName -or $Skill.name -eq [string]$Definition.source) {
                                $Matches += "jsm:$($Skill.name)"
                            }
                        }
                    }
                    { $_ -in @("manual", "plugin-source") } {
                        foreach ($Root in @($Config.skill_roots)) {
                            if (@($Root.agents) -notcontains $Agent) { continue }
                            $RootGroups = @($Root.groups)
                            if ($RootGroups.Count -gt 0 -and @($RootGroups | Where-Object { $HostGroups -contains $_ }).Count -eq 0) { continue }
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
            $CapabilityStatus = if ($Providers.Count -gt 0 -and $ObservedCount -eq $Providers.Count -and $DuplicateCount -eq 0) {
                "present"
            } elseif ($ObservedCount -gt 0) {
                "partial"
            } else {
                "absent"
            }
            Add-Record -Kind "capability" -Id $Name -Status $CapabilityStatus -Confidence "high" -Data @{
                name = $Name
                available = $Providers.Count -gt 0 -and $ObservedCount -eq $Providers.Count
                consistent = $Providers.Count -gt 0 -and $ObservedCount -eq $Providers.Count -and $DuplicateCount -eq 0
                providers = $Providers
            } -Evidence @(@{ source = "configuration+filesystem"; method = "logical-provider-reconciliation" })
        }
    }

    if ($null -ne $Config.skill_roots) {
        foreach ($Definition in @($Config.skill_roots)) {
            $Groups = @($Definition.groups)
            if ($Groups.Count -gt 0 -and @($Groups | Where-Object { $HostGroups -contains $_ }).Count -eq 0) { continue }
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
            if ($Groups.Count -gt 0 -and @($Groups | Where-Object { $HostGroups -contains $_ }).Count -eq 0) { continue }
            $ArtifactId = Limit-Text $Definition.id
            $ArtifactPath = Resolve-UserPath ([string]$Definition.path)
            if (Test-Path -LiteralPath $ArtifactPath) {
                $Item = Get-Item -LiteralPath $ArtifactPath -Force
                if (($Item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    Add-Record -Kind "agent_artifact" -Id $ArtifactId -Status "partial" -Confidence "medium" -Data @{
                        id = $ArtifactId; path = Limit-Text $ArtifactPath; artifact_kind = Limit-Text $Definition.kind
                        agent_exposure = @($Definition.agents)
                    } -Errors @(@{ code = "symlink_not_followed"; severity = "warning"; retryable = $false; message = "agent artifact path is a link" })
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
                }
            } else {
                Add-Record -Kind "agent_artifact" -Id $ArtifactId -Status "absent" -Confidence "high" -Data @{
                    id = $ArtifactId; path = Limit-Text $ArtifactPath; artifact_kind = Limit-Text $Definition.kind
                    agent_exposure = @($Definition.agents)
                }
            }
        }
    }

    $SeenPlugins = @{}
    foreach ($Agent in @("codex", "claude")) {
        $Cache = Join-Path $HOME $(if ($Agent -eq "codex") { ".codex/plugins/cache" } else { ".claude/plugins/cache" })
        if (-not (Test-Path -LiteralPath $Cache -PathType Container)) { continue }
        foreach ($Manifest in @(Get-ChildItem -LiteralPath $Cache -Filter "plugin.json" -File -Recurse -Force -ErrorAction SilentlyContinue |
            Where-Object { $_.Directory.Name -in @(".codex-plugin", ".claude-plugin") })) {
            $VersionDirectory = $Manifest.Directory.Parent
            $PluginDirectory = $VersionDirectory.Parent
            $Plugin = $PluginDirectory.Name
            $Marketplace = $PluginDirectory.Parent.Name
            $Version = $VersionDirectory.Name
            $Key = "$Agent`:$Marketplace`:$Plugin`:$Version"
            if ($SeenPlugins.ContainsKey($Key)) { continue }
            $SeenPlugins[$Key] = $true
            Add-Record -Kind "plugin" -Id $Key -Status "present" -Confidence "medium" -Data @{
                agent = $Agent
                marketplace = Limit-Text $Marketplace
                name = Limit-Text $Plugin
                installed_version = Limit-Text $Version
                path = Limit-Text $VersionDirectory.FullName
                installed_at = $null
                inferred_installed_at = $VersionDirectory.CreationTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ")
                inferred_installed_at_evidence = "filesystem_creation_time"
                inferred_installed_at_confidence = "low"
            } -Evidence @(@{ source = "filesystem"; method = "directory-observation+creation-time-inference" })
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
            $ConfiguredPath = if ($null -ne $Definition.paths -and $null -ne $Definition.paths.$HostId) {
                [string]$Definition.paths.$HostId
            } else {
                [string]$Definition.path
            }
            $Path = Resolve-UserPath $ConfiguredPath
            if (Test-Path -LiteralPath $Path -PathType Leaf) {
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
    $DevRoot = Resolve-UserPath ([string]$Config.machines.$HostId.dev_root)
    if ([string]::IsNullOrWhiteSpace($DevRoot)) {
        Add-Record -Kind "error" -Id "projects:dev-root" -Status "unavailable" -Confidence "high" -Errors @(
            @{ code = "dev_root_missing"; severity = "error"; retryable = $false; message = "project inventory requires a configured dev_root" }
        )
    } elseif ($null -ne $Config.projects) {
        foreach ($Property in $Config.projects.PSObject.Properties) {
            $Definition = $Property.Value
            $Groups = @($Definition.groups)
            if ($Groups.Count -gt 0 -and @($Groups | Where-Object { $HostGroups -contains $_ }).Count -eq 0) { continue }
            $Name = Limit-Text $Property.Name
            $Path = Join-Path $DevRoot ([string]$Definition.path)
            if (Test-Path -LiteralPath $Path -PathType Container) {
                try {
                    if ($null -eq (Get-Command git -ErrorAction SilentlyContinue)) { throw "git is not installed" }
                    $Head = (& git -C $Path rev-parse HEAD 2>$null | Select-Object -First 1)
                    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($Head)) { throw "not a Git checkout" }
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
                    $SyncState = "no-upstream"
                    if (-not [string]::IsNullOrWhiteSpace($Upstream)) {
                        $Counts = ((& git -C $Path rev-list --left-right --count ("HEAD..." + $Upstream) 2>$null | Select-Object -First 1) -split '\s+')
                        if ($Counts.Count -ge 2) {
                            $Ahead = [int]$Counts[0]
                            $Behind = [int]$Counts[1]
                        }
                        $SyncState = if ($Ahead -gt 0 -and $Behind -gt 0) {
                            "diverged"
                        } elseif ($Ahead -gt 0) {
                            "ahead"
                        } elseif ($Behind -gt 0) {
                            "behind"
                        } else {
                            "up-to-date"
                        }
                    }
                    $Readiness = if ($SyncState -eq "diverged") {
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
            $Head = (& git -C $SourcePath rev-parse HEAD 2>$null | Select-Object -First 1)
            if ($LASTEXITCODE -ne 0) { throw "chezmoi source is not a Git repository" }
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
            & chezmoi status 2>$null | Set-Content -LiteralPath $StatusFile -Encoding utf8NoBOM
            if ($LASTEXITCODE -ne 0) { throw "chezmoi status exited $LASTEXITCODE" }
            $StatusLines = @(Get-Content -LiteralPath $StatusFile | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
            $Codes = @($StatusLines | Group-Object { if ($_.Length -ge 2) { $_.Substring(0, 2) } else { $_ } } |
                Sort-Object Name | ForEach-Object { @{ code = Limit-Text $_.Name; count = $_.Count } })
            Add-Record -Kind "chezmoi_state" -Id "live" -Status $(if ($StatusLines.Count -eq 0) { "present" } else { "partial" }) -Confidence "high" -Data @{
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
