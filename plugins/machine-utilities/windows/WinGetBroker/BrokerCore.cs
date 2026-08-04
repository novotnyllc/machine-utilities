using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MachineUtilities.WinGetBroker;

internal static class BrokerCore
{
    internal const int MaxRequestBytes = 4096;
    internal const int MaxProvisionRequestBytes = 2048;
    internal const int MaxContextBytes = 16384;
    internal const int MaxPolicyBytes = 65536;
    internal const int MaxConstraintsBytes = 4 * 1024 * 1024;
    internal const int MaxProviderLockBytes = 16384;
    internal const int MaxSettingsBytes = 65536;
    internal const int MaxResultBytes = 1024 * 1024;
    internal const int MaxOpenSshIdentityBytes = 4096;
    internal const string EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    // NuGet's lock-file content hash is distinct from the raw signed .nupkg SHA-512
    // attested by windows-winget-provider.lock and the build target.
    internal const string CsWinRtNuGetContentHash = "ojoYrj8m8mAx6LVxofQXgfvsReDTv2LQFeDgXt5Lw99r5fv7dCR19ybd1JN0OueaWvL+BNqv0E7zZCgqptP9zA==";
    internal const string ProviderNuGetContentHash = "kw89KoPoV+7RgXbgmp6Jp61ZhDTYAlAFo5H5+/gud01YZ22ULaP8gN3PVEtVmSXVkXswZyPFl0CIcT25QlSQIg==";

    internal const string InstallAction = "winget.install-machine-package.v1";
    internal const string InventoryAction = "winget.inventory-machine.v1";
    internal const string UpgradeAction = "winget.upgrade-machine-package.v1";
    internal const string ContextName = "windows-system-v1";

    private static readonly Regex TokenPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex AtomPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._:+@,-]{0,255}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex VersionPattern = new(
        "^[0-9A-Za-z][0-9A-Za-z.+:~_-]{0,127}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex DigestPattern = new(
        "^[0-9a-f]{64}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex RequestIdPattern = new(
        "^request-[0-9a-f]{32}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex SidPattern = new(
        "^S-[0-9]+(?:-[0-9]+){1,14}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex DeploymentFileNamePattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,255}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex StateIdentifierPattern = new(
        "^machine-utilities-e(?<epoch>[1-9][0-9]{0,9})-(?<authority>[0-9a-f]{64})$",
        RegexOptions.CultureInvariant);

    private static readonly string[] ActionIds =
    [
        "apt.autoremove.v1",
        "apt.install-package-version.v1",
        "apt.update-metadata.v1",
        "apt.upgrade-package.v1",
        "macos.apply-system-setting.v1",
        "macos.install-signed-pkg.v1",
        "profile.apply-managed-bundle.v1",
        "profile.inventory-managed-state.v1",
        InstallAction,
        InventoryAction,
        UpgradeAction,
    ];

    private static readonly IReadOnlyDictionary<string, (string Context, string Kind)> ActionSchema =
        new Dictionary<string, (string Context, string Kind)>(StringComparer.Ordinal)
        {
            ["apt.autoremove.v1"] = ("posix-root-v1", "none"),
            ["apt.install-package-version.v1"] = ("posix-root-v1", "package-source-version-closure-set-sha256"),
            ["apt.update-metadata.v1"] = ("posix-root-v1", "none"),
            ["apt.upgrade-package.v1"] = ("posix-root-v1", "package-source-channel-set-sha256"),
            ["macos.apply-system-setting.v1"] = ("macos-root-v1", "macos-system-setting-sha256"),
            ["macos.install-signed-pkg.v1"] = ("macos-root-v1", "macos-signed-pkg-sha256"),
            ["profile.apply-managed-bundle.v1"] = ("windows-user-s4u-v1", "profile-bundle-set-sha256"),
            ["profile.inventory-managed-state.v1"] = ("windows-user-s4u-v1", "profile-bundle-set-sha256"),
            [InstallAction] = (ContextName, "winget-package-version-set-sha256"),
            [InventoryAction] = (ContextName, "none"),
            [UpgradeAction] = (ContextName, "winget-package-channel-set-sha256"),
        };

    private static readonly HashSet<string> Architectures = new(StringComparer.Ordinal)
    {
        "x86", "x64", "arm", "arm64", "neutral",
    };

    private static readonly HashSet<string> InstallerTypes = new(StringComparer.Ordinal)
    {
        "burn", "exe", "inno", "msi", "msix", "nullsoft", "portable", "wix", "zip",
    };

    internal enum BrokerMode
    {
        Inventory,
        Install,
        Upgrade,
        Probe,
    }

    internal enum TerminalState
    {
        Completed,
        Partial,
        Rejected,
    }

    internal enum ProviderComparison
    {
        Unknown,
        Lesser,
        Equal,
        Greater,
    }

    internal sealed record ActiveGenerationPointer(int Epoch, string GenerationSha256);

    internal sealed record ProvisionRequest(
        int EnrollmentEpoch,
        string GenerationSha256,
        string PolicySha256,
        string ConstraintsSha256,
        string ContextSha256,
        string ProviderLockSha256);

    internal sealed record HelperRequest(
        string RequestId,
        string ActionId,
        string PolicyToken,
        int EnrollmentEpoch,
        string PolicySha256,
        string ConstraintsSha256,
        string ContextSha256,
        string ProviderLockSha256,
        string PreconditionSha256);

    internal sealed record ProviderContext(
        string StateIdentifier,
        string SourceId,
        string SourceName,
        string SourceType,
        string SourceArgument,
        string SourceArgumentSha256,
        string SourceOrigin,
        string SourceTrust,
        bool SourceExplicit,
        long SourceLastUpdateMinimumUnix,
        string DeploymentFileSetSha256,
        string AppInstallerIdentitySha256);

    internal sealed record ProviderLock(
        string DotNetSdkVersion,
        string DotNetRuntimeVersion,
        string TargetFramework,
        string TargetPlatformMinVersion,
        string WindowsSdkVersion,
        string WindowsSdkPackageSha512,
        string CsWinRtVersion,
        string CsWinRtPackageSha512,
        string ProviderVersion,
        string ProviderPackageSha512,
        IReadOnlyList<string> RuntimeIdentifiers,
        IReadOnlyList<string> PayloadNames,
        IReadOnlyList<string> AuthenticodeNames);

    internal sealed record PolicyAction(
        string ActionId,
        string Context,
        bool Enabled,
        string ConstraintKind,
        string ConstraintSha256);

    internal sealed record PolicyDocument(IReadOnlyDictionary<string, PolicyAction> Actions);

    internal sealed record WinGetConstraint(
        BrokerMode Mode,
        string ActionId,
        string PolicyToken,
        string PackageId,
        string SourceName,
        string SourceType,
        string SourceArgumentSha256,
        string Architecture,
        string Locale,
        IReadOnlyList<string> InstallerTypes,
        string MinimumVersion,
        string MaximumVersion,
        int? MajorCeiling);

    internal sealed record ConstraintsDocument(
        int Generation,
        string PolicySha256,
        IReadOnlyList<WinGetConstraint> WinGetConstraints);

    internal sealed record SourceEvidence(
        string Id,
        string Name,
        string Type,
        string ArgumentSha256,
        string Origin,
        string Trust,
        bool Explicit,
        long LastUpdateUnix);

    internal sealed record SourceCatalogObservation(
        SourceEvidence Evidence,
        bool IsComposite,
        int AgreementCount,
        string AuthenticationType);

    internal sealed record SourceProvisioningPlan(
        IReadOnlyList<string> CatalogNamesToRemove,
        bool AddExpectedCatalog);

    internal sealed record OperationPackageShape(
        bool IsComposite,
        int ProtectedRemoteCatalogCount,
        bool InstalledVersionAttached,
        string CompositeSearchBehavior,
        bool RootResolvedFromProtectedRemote);

    internal sealed record VersionCandidateObservation(
        int Index,
        string Version,
        string SourceId,
        string Channel,
        ProviderComparison ToMinimum,
        ProviderComparison ToMaximum,
        ProviderComparison ToInstalled);

    internal sealed record InstallerObservation(
        string Scope,
        string Architecture,
        string Locale,
        string InstallerType,
        string NestedInstallerType,
        string AuthenticationType,
        int AgreementCount);

    internal sealed record DangerousOptionsObservation(
        string PackageCatalogId,
        string PackageVersion,
        string PackageChannel,
        string Scope,
        string Mode,
        bool AllowHashMismatch,
        bool AllowUpgradeToUnknownVersion,
        bool Force,
        bool AcceptPackageAgreements,
        bool BypassStorePolicy,
        bool SkipDependencies,
        bool HasAuthenticationArguments,
        string PreferredInstallLocation,
        string LogOutputPath,
        string ReplacementInstallerArguments,
        string AdditionalInstallerArguments,
        string AdditionalCatalogArguments,
        string CorrelationData,
        IReadOnlyList<string> AllowedArchitectures,
        string InstallerType);

    internal sealed record MutationResolutionObservation(
        PackageEvidence Evidence,
        string PackageCatalogId,
        DangerousOptionsObservation Options);

    // This deliberately small seam models the security-sensitive ordering around a native
    // PackageManager operation. Program drives it with real MMD objects; tests drive it with
    // a fake asynchronous terminal so no test can accidentally bypass the production ordering.
    internal sealed class ProviderMutationInvocation
    {
        private enum Phase
        {
            Created,
            SettingsApplied,
            ManagerActivated,
            InitialSourceOpened,
            InitialResolved,
            RevalidatedSourceOpened,
            RevalidatedResolved,
            InvocationStarted,
            TerminalObserved,
        }

        private readonly BrokerMode _mode;
        private readonly ProviderContext _context;
        private readonly WinGetConstraint _constraint;
        private Phase _phase;
        private SourceEvidence? _initialSource;
        private MutationResolutionObservation? _initialResolution;

        internal ProviderMutationInvocation(
            BrokerMode mode,
            ProviderContext context,
            WinGetConstraint constraint)
        {
            if (mode is not (BrokerMode.Install or BrokerMode.Upgrade))
            {
                throw new InvalidOperationException("inventory_has_no_mutation_constraint");
            }

            ValidateConstraintSource(constraint, context);
            _mode = mode;
            _context = context;
            _constraint = constraint;
        }

        internal bool InvocationStarted => _phase == Phase.InvocationStarted;
        internal bool TerminalObserved => _phase == Phase.TerminalObserved;

        internal void RecordSettingsApplied()
        {
            RequirePhase(Phase.Created);
            _phase = Phase.SettingsApplied;
        }

        internal void RecordManagerActivated()
        {
            RequirePhase(Phase.SettingsApplied);
            _phase = Phase.ManagerActivated;
        }

        internal void RecordInitialSource(SourceEvidence source, int observedCatalogCount)
        {
            RequirePhase(Phase.ManagerActivated);
            ValidateSingleSource(source, observedCatalogCount);
            _initialSource = source;
            _phase = Phase.InitialSourceOpened;
        }

        internal void RecordInitialResolution(MutationResolutionObservation resolution)
        {
            RequirePhase(Phase.InitialSourceOpened);
            ValidateResolution(resolution);
            _initialResolution = resolution;
            _phase = Phase.InitialResolved;
        }

        internal void RecordRevalidatedSource(SourceEvidence source, int observedCatalogCount)
        {
            RequirePhase(Phase.InitialResolved);
            ValidateSingleSource(source, observedCatalogCount);
            if (_initialSource is null || !SameSourceIdentity(_initialSource, source))
            {
                throw new InvalidOperationException("source_drift");
            }

            _phase = Phase.RevalidatedSourceOpened;
        }

        internal void RecordRevalidatedResolution(MutationResolutionObservation resolution)
        {
            RequirePhase(Phase.RevalidatedSourceOpened);
            ValidateResolution(resolution);
            if (_initialResolution is null || _initialResolution.Evidence != resolution.Evidence ||
                _initialResolution.PackageCatalogId != resolution.PackageCatalogId ||
                !SameOptions(_initialResolution.Options, resolution.Options))
            {
                throw new InvalidOperationException("package_state_drift");
            }

            _phase = Phase.RevalidatedResolved;
        }

        internal Task<T> InvokeAsync<T>(Func<Task<T>> invoke)
        {
            RequirePhase(Phase.RevalidatedResolved);
            if (invoke is null)
            {
                throw new ArgumentNullException(nameof(invoke));
            }

            _phase = Phase.InvocationStarted;
            return AwaitTerminalAsync(invoke);
        }

        private async Task<T> AwaitTerminalAsync<T>(Func<Task<T>> invoke)
        {
            try
            {
                Task<T>? terminal = invoke();
                if (terminal is null)
                {
                    throw new InvalidOperationException("provider_failure");
                }

                return await terminal.ConfigureAwait(false);
            }
            finally
            {
                _phase = Phase.TerminalObserved;
            }
        }

        private void ValidateSingleSource(SourceEvidence source, int observedCatalogCount)
        {
            if (observedCatalogCount != 1)
            {
                throw new InvalidOperationException("source_drift");
            }

            ValidateSource(_context, source);
        }

        private void ValidateResolution(MutationResolutionObservation resolution)
        {
            if (resolution.Evidence.PackageId != _constraint.PackageId ||
                resolution.PackageCatalogId != _context.SourceId ||
                !IsVersion(resolution.Evidence.CandidateVersion) ||
                (_mode == BrokerMode.Install &&
                    (resolution.Evidence.CandidateVersion != _constraint.MinimumVersion ||
                     resolution.Evidence.CandidateVersion != _constraint.MaximumVersion)))
            {
                throw new InvalidOperationException("package_state_drift");
            }

            ValidateDangerousOptions(
                _constraint,
                resolution.PackageCatalogId,
                resolution.Evidence.CandidateVersion,
                resolution.Options);
        }

        private void RequirePhase(Phase expected)
        {
            if (_phase != expected)
            {
                throw new InvalidOperationException("package_state_drift");
            }
        }

        private static bool SameSourceIdentity(SourceEvidence left, SourceEvidence right) =>
            left.Id == right.Id &&
            left.Name == right.Name &&
            left.Type == right.Type &&
            left.ArgumentSha256 == right.ArgumentSha256 &&
            left.Origin == right.Origin &&
            left.Trust == right.Trust &&
            left.Explicit == right.Explicit;

        private static bool SameOptions(DangerousOptionsObservation left, DangerousOptionsObservation right) =>
            left.PackageCatalogId == right.PackageCatalogId &&
            left.PackageVersion == right.PackageVersion &&
            left.PackageChannel == right.PackageChannel &&
            left.Scope == right.Scope &&
            left.Mode == right.Mode &&
            left.AllowHashMismatch == right.AllowHashMismatch &&
            left.AllowUpgradeToUnknownVersion == right.AllowUpgradeToUnknownVersion &&
            left.Force == right.Force &&
            left.AcceptPackageAgreements == right.AcceptPackageAgreements &&
            left.BypassStorePolicy == right.BypassStorePolicy &&
            left.SkipDependencies == right.SkipDependencies &&
            left.HasAuthenticationArguments == right.HasAuthenticationArguments &&
            left.PreferredInstallLocation == right.PreferredInstallLocation &&
            left.LogOutputPath == right.LogOutputPath &&
            left.ReplacementInstallerArguments == right.ReplacementInstallerArguments &&
            left.AdditionalInstallerArguments == right.AdditionalInstallerArguments &&
            left.AdditionalCatalogArguments == right.AdditionalCatalogArguments &&
            left.CorrelationData == right.CorrelationData &&
            left.AllowedArchitectures.SequenceEqual(right.AllowedArchitectures, StringComparer.Ordinal) &&
            left.InstallerType == right.InstallerType;
    }

    internal sealed record DeploymentFileObservation(string Name, long Length, string Sha256);

    internal sealed record AuthenticodeFileObservation(
        string Name,
        string FileSha256,
        string SignerCertificateSha256);

    internal sealed record DeploymentEvidence(
        string FileSetSha256,
        string AuthenticodeIdentitySha256,
        IReadOnlyList<DeploymentFileObservation> Files);

    internal sealed record ProviderRuntimeRootObservation(
        string Kind,
        string PathSha256,
        string AclSha256);

    internal sealed record PackageEvidence(
        int Index,
        string PolicyToken,
        string PackageId,
        string InstalledVersion,
        string CandidateVersion,
        string Architecture,
        string Locale,
        string InstallerType,
        string NestedInstallerType,
        string PostVersion);

    internal sealed record HelperResult(
        string RequestId,
        string ActionId,
        TerminalState State,
        string Reason,
        string ProviderLockSha256,
        string DeploymentFileSetSha256,
        string AppInstallerIdentitySha256,
        string ProviderVersion,
        string StateIdentifierSha256,
        string ProviderRuntimeRootsSha256,
        string SettingsSha256,
        SourceEvidence? Source,
        IReadOnlyList<PackageEvidence> Packages,
        string ProviderStatus,
        string ProviderExtendedError,
        string ProviderInstallerError,
        bool RebootRequired,
        string PreStateSha256,
        string PostStateSha256);

    internal sealed record ProviderProvisionResult(
        TerminalState State,
        string Reason,
        int EnrollmentEpoch,
        string GenerationSha256,
        string ProviderLockSha256,
        string DeploymentFileSetSha256,
        string AppInstallerIdentitySha256,
        string ProviderVersion,
        string StateIdentifierSha256,
        string ProviderRuntimeRootsSha256,
        string SettingsSha256,
        SourceEvidence? Source);

    internal sealed record PreconditionProbeResult(
        string RequestId,
        string ActionId,
        string PolicyToken,
        string PreconditionSha256);

    internal static BrokerMode ParseMode(string[] args)
    {
        if (args.Length != 1)
        {
            throw new FormatException("invalid_mode");
        }

        return args[0] switch
        {
            "inventory" => BrokerMode.Inventory,
            "install" => BrokerMode.Install,
            "upgrade" => BrokerMode.Upgrade,
            "probe" => BrokerMode.Probe,
            _ => throw new FormatException("invalid_mode"),
        };
    }

    internal static string ActionForMode(BrokerMode mode) => mode switch
    {
        BrokerMode.Inventory => InventoryAction,
        BrokerMode.Install => InstallAction,
        BrokerMode.Upgrade => UpgradeAction,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    internal static BrokerMode ModeForAction(string actionId) => actionId switch
    {
        InventoryAction => BrokerMode.Inventory,
        InstallAction => BrokerMode.Install,
        UpgradeAction => BrokerMode.Upgrade,
        _ => throw new FormatException("invalid_request"),
    };

    internal static ProvisionRequest ParseProvisionRequest(ReadOnlySpan<byte> bytes)
    {
        string[] lines = ParseCanonicalLines(bytes, MaxProvisionRequestBytes, "provision_request");
        if (lines.Length != 8 || lines[0] != "winget-provider-provision-request|1" ||
            lines[7] != "end-provision-request|")
        {
            throw new FormatException("invalid_provision_request");
        }

        return new ProvisionRequest(
            ParsePositiveInt(ReadField(lines[1], "enrollment-epoch"), int.MaxValue),
            ReadDigestField(lines[2], "generation-sha256"),
            ReadDigestField(lines[3], "policy-sha256"),
            ReadDigestField(lines[4], "constraints-sha256"),
            ReadDigestField(lines[5], "winget-context-sha256"),
            ReadDigestField(lines[6], "provider-lock-sha256"));
    }

    internal static ActiveGenerationPointer ParseActiveGenerationPointer(ReadOnlySpan<byte> bytes)
    {
        string[] lines = ParseCanonicalLines(bytes, 160, "active_generation");
        if (lines.Length != 4 || lines[0] != "machine-utilities-active-generation|1" ||
            lines[3] != "end-generation|")
        {
            throw new FormatException("invalid_active_generation");
        }

        int epoch = ParsePositiveInt(ReadField(lines[1], "epoch"), int.MaxValue);
        string generationSha256 = ReadDigestField(lines[2], "generation-sha256");
        return new ActiveGenerationPointer(epoch, generationSha256);
    }

    internal static string ComputeGenerationSha256(
        int epoch,
        string policySha256,
        string constraintsSha256,
        string contextSha256,
        string providerLockSha256,
        string openSshIdentitySha256)
    {
        if (epoch < 1)
        {
            throw new FormatException("invalid_generation");
        }

        EnsureDigest(policySha256);
        EnsureDigest(constraintsSha256);
        EnsureDigest(contextSha256);
        EnsureDigest(providerLockSha256);
        EnsureDigest(openSshIdentitySha256);
        return Sha256Ascii(
            "machine-utilities-generation|1\n" +
            $"epoch|{epoch.ToString(CultureInfo.InvariantCulture)}\n" +
            $"policy-sha256|{policySha256}\n" +
            $"constraints-sha256|{constraintsSha256}\n" +
            $"winget-context-sha256|{contextSha256}\n" +
            $"provider-lock-sha256|{providerLockSha256}\n" +
            $"openssh-identity-sha256|{openSshIdentitySha256}\n" +
            "end-generation|\n");
    }

    internal static string ValidateGenerationEnvelope(
        ActiveGenerationPointer pointer,
        HelperRequest request,
        string observedPolicySha256,
        string observedConstraintsSha256,
        string observedContextSha256,
        string observedProviderLockSha256,
        string observedOpenSshIdentitySha256)
    {
        if (pointer.Epoch != request.EnrollmentEpoch ||
            observedPolicySha256 != request.PolicySha256 ||
            observedConstraintsSha256 != request.ConstraintsSha256 ||
            observedContextSha256 != request.ContextSha256 ||
            observedProviderLockSha256 != request.ProviderLockSha256)
        {
            throw new InvalidOperationException("active_state_drift");
        }

        string observedGeneration = ComputeGenerationSha256(
            pointer.Epoch,
            observedPolicySha256,
            observedConstraintsSha256,
            observedContextSha256,
            observedProviderLockSha256,
            observedOpenSshIdentitySha256);
        if (observedGeneration != pointer.GenerationSha256)
        {
            throw new InvalidOperationException("active_state_drift");
        }

        return observedGeneration;
    }

    internal static HelperRequest ParseRequest(ReadOnlySpan<byte> bytes, BrokerMode mode)
    {
        string[] lines = ParseCanonicalLines(bytes, MaxRequestBytes, "request");
        if (lines.Length != 11 || lines[0] != "winget-helper-request|1" || lines[10] != "end-request|")
        {
            throw new FormatException("invalid_request");
        }

        string requestId = ReadField(lines[1], "request-id");
        string actionId = ReadField(lines[2], "action-id");
        string token = ReadField(lines[3], "policy-token");
        int epoch = ParsePositiveInt(ReadField(lines[4], "enrollment-epoch"), int.MaxValue);
        string policy = ReadDigestField(lines[5], "policy-sha256");
        string constraints = ReadDigestField(lines[6], "constraints-sha256");
        string context = ReadDigestField(lines[7], "winget-context-sha256");
        string providerLock = ReadDigestField(lines[8], "provider-lock-sha256");
        string precondition = ReadDigestField(lines[9], "precondition-sha256");

        BrokerMode actionMode = mode == BrokerMode.Probe ? ModeForAction(actionId) : mode;
        if (!RequestIdPattern.IsMatch(requestId) ||
            (mode != BrokerMode.Probe && actionId != ActionForMode(mode)))
        {
            throw new FormatException("invalid_request");
        }

        if ((actionMode == BrokerMode.Inventory && token != "-") ||
            (actionMode != BrokerMode.Inventory && !TokenPattern.IsMatch(token)) ||
            ((mode == BrokerMode.Probe) != (precondition == EmptySha256)))
        {
            throw new FormatException("invalid_request");
        }

        return new HelperRequest(
            requestId,
            actionId,
            token,
            epoch,
            policy,
            constraints,
            context,
            providerLock,
            precondition);
    }

    internal static ProviderContext ParseProviderContext(ReadOnlySpan<byte> bytes)
    {
        string[] lines = ParseCanonicalLines(bytes, MaxContextBytes, "provider_context");
        if (lines.Length != 14 || lines[0] != "winget-provider-context|1" || lines[13] != "end-context|")
        {
            throw new FormatException("invalid_provider_context");
        }

        string state = ReadAtomField(lines[1], "state-identifier");
        string sourceId = ReadAtomField(lines[2], "source-id");
        string sourceName = ReadAtomField(lines[3], "source-name");
        string sourceType = ReadAtomField(lines[4], "source-type");
        string sourceArgument = ReadField(lines[5], "source-argument");
        string sourceArgumentSha256 = ReadDigestField(lines[6], "source-argument-sha256");
        string sourceOrigin = ReadField(lines[7], "source-origin");
        string sourceTrust = ReadField(lines[8], "source-trust");
        string sourceExplicitText = ReadField(lines[9], "source-explicit");
        long sourceLastUpdateMinimumUnix = ParseNonNegativeLong(
            ReadField(lines[10], "source-last-update-min-unix"), long.MaxValue);
        string deploymentFiles = ReadDigestField(lines[11], "deployment-file-set-sha256");
        string appInstaller = ReadDigestField(lines[12], "app-installer-identity-sha256");

        if (sourceType is not ("Microsoft.Rest" or "Microsoft.PreIndexed.Package") ||
            sourceOrigin is not ("predefined" or "user") ||
            sourceTrust is not ("none" or "trusted") ||
            sourceExplicitText is not ("true" or "false") ||
            !StateIdentifierPattern.IsMatch(state) ||
            !IsSafeSourceArgument(sourceArgument) ||
            Sha256Utf8(sourceArgument) != sourceArgumentSha256)
        {
            throw new FormatException("invalid_provider_context");
        }

        return new ProviderContext(
            state,
            sourceId,
            sourceName,
            sourceType,
            sourceArgument,
            sourceArgumentSha256,
            sourceOrigin,
            sourceTrust,
            sourceExplicitText == "true",
            sourceLastUpdateMinimumUnix,
            deploymentFiles,
            appInstaller);
    }

    internal static ProviderLock ParseProviderLock(ReadOnlySpan<byte> bytes)
    {
        string[] expected =
        [
            "winget-provider-lock|1",
            "dotnet-sdk|8.0.423",
            "dotnet-runtime|8.0.29",
            "target-framework|net8.0-windows10.0.26100.0",
            "target-platform-min-version|10.0.17763.0",
            "windows-sdk-net-ref|10.0.26100.56|SHA512|IjFc4STI6HVYd7hG2O8LxdMbRsjJb0pbt8oSOV+Mcl+hoIRkuHKfopq6FHYdY5o1tbFprFSZaXtXzOeXnDaFjg==",
            "windows-cswinrt|2.3.1|SHA512|5LkAwRSxcbZpBiAdJjRE9w8q6Jh4voRckbtmbCpMW3jMDb0n2pwxlxANyXRcLEoMAxSTSCKcmjcX6waWrPAUuw==",
            "windows-package-manager-inproccom|1.29.280|SHA512|uWSgGMlZEWCkdZ6kxXSI50fqFXaj8LWhFnOiEg0uKhpcSoEh2HCwPXasAiT32aZ3ZF8KGyOSlTtOal/+soZ7jg==",
            "cswinrt-windows-metadata|10.0.26100.0",
            "cswinrt-includes|Microsoft.Management.Deployment",
            "rid|win-arm64",
            "rid|win-x64",
            "payload|Microsoft.Management.Deployment.dll",
            "payload|Microsoft.Management.Deployment.dll.manifest",
            "payload|Microsoft.Management.Deployment.winmd",
            "payload|WindowsPackageManager.dll",
            "authenticode|MachineUtilities.WinGetBroker.exe",
            "authenticode|Microsoft.Management.Deployment.dll",
            "authenticode|WindowsPackageManager.dll",
            "dependency-authority|source-delegated-all",
        ];

        string[] actual = ParseCanonicalLines(bytes, MaxProviderLockBytes, "provider_lock");
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new FormatException("invalid_provider_lock");
        }

        return new ProviderLock(
            "8.0.423",
            "8.0.29",
            "net8.0-windows10.0.26100.0",
            "10.0.17763.0",
            "10.0.26100.56",
            "IjFc4STI6HVYd7hG2O8LxdMbRsjJb0pbt8oSOV+Mcl+hoIRkuHKfopq6FHYdY5o1tbFprFSZaXtXzOeXnDaFjg==",
            "2.3.1",
            "5LkAwRSxcbZpBiAdJjRE9w8q6Jh4voRckbtmbCpMW3jMDb0n2pwxlxANyXRcLEoMAxSTSCKcmjcX6waWrPAUuw==",
            "1.29.280",
            "uWSgGMlZEWCkdZ6kxXSI50fqFXaj8LWhFnOiEg0uKhpcSoEh2HCwPXasAiT32aZ3ZF8KGyOSlTtOal/+soZ7jg==",
            new[] { "win-arm64", "win-x64" },
            new[]
            {
                "Microsoft.Management.Deployment.dll",
                "Microsoft.Management.Deployment.dll.manifest",
                "Microsoft.Management.Deployment.winmd",
                "WindowsPackageManager.dll",
            },
            new[]
            {
                "MachineUtilities.WinGetBroker.exe",
                "Microsoft.Management.Deployment.dll",
                "WindowsPackageManager.dll",
            });
    }

    internal static void ValidateProviderRuntime(ProviderLock providerLock, string runtimeVersion, string runtimeIdentifier)
    {
        if (runtimeVersion != providerLock.DotNetRuntimeVersion ||
            !providerLock.RuntimeIdentifiers.Contains(runtimeIdentifier, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("deployment_identity_drift");
        }
    }

    internal static PolicyDocument ParsePolicy(ReadOnlySpan<byte> bytes)
    {
        string[] lines = ParseCanonicalLines(bytes, MaxPolicyBytes, "policy");
        if (lines.Length != ActionIds.Length + 1 || lines[0] != "policy|1|catalog=1")
        {
            throw new FormatException("invalid_policy");
        }

        var actions = new Dictionary<string, PolicyAction>(StringComparer.Ordinal);
        string previous = string.Empty;
        for (int index = 1; index < lines.Length; index++)
        {
            string line = lines[index];
            if (line.Length > 2048 || (previous.Length != 0 && string.CompareOrdinal(previous, line) >= 0))
            {
                throw new FormatException("invalid_policy");
            }

            previous = line;
            string[] fields = line.Split('|');
            if (fields.Length != 6 || fields[0] != "action" || !ActionSchema.TryGetValue(fields[1], out var schema) ||
                fields[2] != schema.Context || fields[4] != schema.Kind ||
                fields[3] is not ("enabled" or "disabled") ||
                (fields[4] == "none" ? fields[5] != "-" : fields[5] != "-" && !DigestPattern.IsMatch(fields[5])) ||
                !actions.TryAdd(fields[1], new PolicyAction(fields[1], fields[2], fields[3] == "enabled", fields[4], fields[5])))
            {
                throw new FormatException("invalid_policy");
            }
        }

        if (!ActionIds.All(actions.ContainsKey))
        {
            throw new FormatException("invalid_policy");
        }

        return new PolicyDocument(actions);
    }

    internal static ConstraintsDocument ParseConstraints(
        ReadOnlySpan<byte> bytes,
        PolicyDocument policy,
        string policySha256)
    {
        string[] lines = ParseCanonicalLines(bytes, MaxConstraintsBytes, "constraints");
        if (lines.Length < 1)
        {
            throw new FormatException("invalid_constraints");
        }

        string[] header = lines[0].Split('|');
        if (header.Length != 4 || header[0] != "constraints" || header[1] != "1" ||
            !header[2].StartsWith("generation=", StringComparison.Ordinal) ||
            !header[3].StartsWith("policy-sha256=", StringComparison.Ordinal))
        {
            throw new FormatException("invalid_constraints");
        }

        int generation = ParsePositiveInt(header[2]["generation=".Length..], int.MaxValue);
        string boundPolicy = header[3]["policy-sha256=".Length..];
        if (boundPolicy != policySha256 || !DigestPattern.IsMatch(boundPolicy))
        {
            throw new FormatException("invalid_constraints");
        }

        var recordsByAction = ActionIds.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        var memberships = new HashSet<string>(StringComparer.Ordinal);
        var winget = new List<WinGetConstraint>();
        string previous = string.Empty;

        for (int index = 1; index < lines.Length; index++)
        {
            string line = lines[index];
            if (line.Length > 2048 || (previous.Length != 0 && string.CompareOrdinal(previous, line) >= 0))
            {
                throw new FormatException("invalid_constraints");
            }

            previous = line;
            string[] fields = line.Split('|');
            if (fields.Length < 3)
            {
                throw new FormatException("invalid_constraints");
            }

            string membershipAction = fields[0] == "profile" ? "profile" : fields[1];
            string membershipToken = fields[0] == "profile" ? fields[1] : fields[2];
            if (!memberships.Add(membershipAction + "\0" + membershipToken))
            {
                throw new FormatException("invalid_constraints");
            }

            switch (fields[0])
            {
                case "apt-install":
                    ValidateAptInstall(fields);
                    recordsByAction[fields[1]].Add(line);
                    break;
                case "apt-upgrade":
                    ValidateAptUpgrade(fields);
                    recordsByAction[fields[1]].Add(line);
                    break;
                case "profile":
                    ValidateProfile(fields);
                    recordsByAction["profile.apply-managed-bundle.v1"].Add(line);
                    recordsByAction["profile.inventory-managed-state.v1"].Add(line);
                    break;
                case "winget-install":
                    {
                        WinGetConstraint record = ParseWinGetInstall(fields);
                        recordsByAction[record.ActionId].Add(line);
                        winget.Add(record);
                        break;
                    }
                case "winget-upgrade":
                    {
                        WinGetConstraint record = ParseWinGetUpgrade(fields);
                        recordsByAction[record.ActionId].Add(line);
                        winget.Add(record);
                        break;
                    }
                default:
                    throw new FormatException("invalid_constraints");
            }
        }

        foreach (string actionId in ActionIds)
        {
            PolicyAction action = policy.Actions[actionId];
            List<string> group = recordsByAction[actionId];
            if (action.ConstraintSha256 == "-")
            {
                if (group.Count != 0 && !actionId.StartsWith("profile.", StringComparison.Ordinal))
                {
                    throw new FormatException("invalid_constraints");
                }

                if (group.Count != 0 && actionId.StartsWith("profile.", StringComparison.Ordinal) &&
                    !policy.Actions.Where(pair => pair.Key.StartsWith("profile.", StringComparison.Ordinal))
                        .Any(pair => pair.Value.ConstraintSha256 != "-"))
                {
                    throw new FormatException("invalid_constraints");
                }
            }
            else if (group.Count == 0 || Sha256Ascii(string.Join('\n', group) + "\n") != action.ConstraintSha256)
            {
                throw new FormatException("invalid_constraints");
            }
        }

        return new ConstraintsDocument(generation, boundPolicy, winget);
    }

    internal static WinGetConstraint SelectConstraint(
        BrokerMode mode,
        HelperRequest request,
        PolicyDocument policy,
        ConstraintsDocument constraints,
        ProviderContext context)
    {
        PolicyAction action = policy.Actions[request.ActionId];
        if (!action.Enabled || action.Context != ContextName || constraints.Generation != request.EnrollmentEpoch)
        {
            throw new InvalidOperationException("unauthorized_policy");
        }

        if (mode == BrokerMode.Inventory)
        {
            throw new InvalidOperationException("inventory_has_no_mutation_constraint");
        }

        WinGetConstraint[] matches = constraints.WinGetConstraints
            .Where(item => item.Mode == mode && item.ActionId == request.ActionId && item.PolicyToken == request.PolicyToken)
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException("unauthorized_policy_token");
        }

        WinGetConstraint constraint = matches[0];
        ValidateConstraintSource(constraint, context);
        return constraint;
    }

    internal static IReadOnlyList<WinGetConstraint> InventoryConstraints(
        ConstraintsDocument constraints,
        ProviderContext context)
    {
        WinGetConstraint[] selected = constraints.WinGetConstraints
            .OrderBy(item => item.ActionId, StringComparer.Ordinal)
            .ThenBy(item => item.PolicyToken, StringComparer.Ordinal)
            .ToArray();
        foreach (WinGetConstraint constraint in selected)
        {
            ValidateConstraintSource(constraint, context);
        }

        return selected;
    }

    internal static void ValidateConstraintSource(WinGetConstraint constraint, ProviderContext context)
    {
        if (constraint.SourceName != context.SourceName ||
            constraint.SourceType != context.SourceType ||
            constraint.SourceArgumentSha256 != context.SourceArgumentSha256)
        {
            throw new InvalidOperationException("source_drift");
        }
    }

    internal static string BuildUserSettings(WinGetConstraint? constraint)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("$schema");
            writer.WriteStringValue("https://aka.ms/winget-settings.schema.json");
            writer.WritePropertyName("source");
            writer.WriteStartObject();
            writer.WriteNumber("autoUpdateIntervalInMinutes", 0);
            writer.WriteEndObject();
            writer.WritePropertyName("interactivity");
            writer.WriteStartObject();
            writer.WriteBoolean("disable", true);
            writer.WriteEndObject();
            writer.WritePropertyName("installBehavior");
            writer.WriteStartObject();
            writer.WriteBoolean("skipDependencies", false);
            writer.WritePropertyName("requirements");
            writer.WriteStartObject();
            writer.WriteString("scope", "machine");
            if (constraint is not null)
            {
                writer.WritePropertyName("architectures");
                writer.WriteStartArray();
                writer.WriteStringValue(constraint.Architecture);
                writer.WriteEndArray();
                writer.WritePropertyName("locale");
                writer.WriteStartArray();
                writer.WriteStringValue(constraint.Locale);
                writer.WriteEndArray();
                writer.WritePropertyName("installerTypes");
                writer.WriteStartArray();
                foreach (string installerType in constraint.InstallerTypes)
                {
                    writer.WriteStringValue(installerType);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WritePropertyName("experimentalFeatures");
            writer.WriteStartObject();
            writer.WriteBoolean("resume", false);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static void ValidatePersistedSettingsLayout(IReadOnlyList<string> entries)
    {
        if (entries.Count > 2 || entries.Distinct(StringComparer.Ordinal).Count() != entries.Count ||
            entries.Any(entry => entry is not ("settings.json" or "settings.json.backup")))
        {
            throw new InvalidOperationException("runtime_state_drift");
        }
    }

    internal static void ValidatePersistedSettingsJson(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaxSettingsBytes)
        {
            throw new InvalidOperationException("runtime_state_drift");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("runtime_state_drift");
            }

            ValidateUniqueJsonProperties(root);
            if (!root.TryGetProperty("installBehavior", out JsonElement installBehavior))
            {
                return;
            }

            if (installBehavior.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("runtime_state_drift");
            }

            if (installBehavior.TryGetProperty("skipDependencies", out JsonElement skipDependencies) &&
                skipDependencies.ValueKind != JsonValueKind.False)
            {
                throw new InvalidOperationException("runtime_state_drift");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch
        {
            throw new InvalidOperationException("runtime_state_drift");
        }
    }

    internal static void ValidatePackageLock(ReadOnlySpan<byte> bytes, ProviderLock providerLock)
    {
        if (bytes.Length == 0 || bytes.Length > MaxProviderLockBytes)
        {
            throw new InvalidOperationException("provider_lock_drift");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("version", out JsonElement version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out int lockVersion) || lockVersion != 1 ||
                !root.TryGetProperty("dependencies", out JsonElement dependencies) ||
                dependencies.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("provider_lock_drift");
            }

            ValidateUniqueJsonProperties(root, "provider_lock_drift");
            ValidatePackageLockTarget(dependencies, "net8.0", Array.Empty<(string Name, string Version, string ContentHash)>());
            foreach (string runtimeIdentifier in providerLock.RuntimeIdentifiers)
            {
                ValidatePackageLockTarget(
                    dependencies,
                    $"net8.0/{runtimeIdentifier}",
                    Array.Empty<(string Name, string Version, string ContentHash)>());
            }
            ValidatePackageLockTarget(
                dependencies,
                "net8.0-windows10.0.26100",
                new[]
                {
                    (Name: "Microsoft.Windows.CsWinRT", Version: providerLock.CsWinRtVersion,
                        ContentHash: CsWinRtNuGetContentHash),
                    (Name: "Microsoft.WindowsPackageManager.InProcCom", Version: providerLock.ProviderVersion,
                        ContentHash: ProviderNuGetContentHash),
                });
            foreach (string runtimeIdentifier in providerLock.RuntimeIdentifiers)
            {
                ValidatePackageLockTarget(
                    dependencies,
                    $"net8.0-windows10.0.26100/{runtimeIdentifier}",
                    new[]
                    {
                        (Name: "Microsoft.WindowsPackageManager.InProcCom", Version: providerLock.ProviderVersion,
                            ContentHash: ProviderNuGetContentHash),
                    });
            }

            string[] expectedTargets = providerLock.RuntimeIdentifiers
                .Select(runtimeIdentifier => $"net8.0-windows10.0.26100/{runtimeIdentifier}")
                .Concat(providerLock.RuntimeIdentifiers.Select(runtimeIdentifier => $"net8.0/{runtimeIdentifier}"))
                .Append("net8.0-windows10.0.26100")
                .Append("net8.0")
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            string[] actualTargets = dependencies.EnumerateObject()
                .Select(property => property.Name)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            if (!actualTargets.SequenceEqual(expectedTargets, StringComparer.Ordinal))
            {
                throw new InvalidOperationException("provider_lock_drift");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch
        {
            throw new InvalidOperationException("provider_lock_drift");
        }
    }

    internal static void ValidateFrameworkReferencePackageHash(ProviderLock providerLock, string observedSha512)
    {
        if (observedSha512 != providerLock.WindowsSdkPackageSha512)
        {
            throw new InvalidOperationException("provider_lock_drift");
        }
    }

    internal static SourceEvidence ValidateSource(ProviderContext expected, SourceEvidence observed)
    {
        if (observed.Id != expected.SourceId || observed.Name != expected.SourceName ||
            observed.Type != expected.SourceType || observed.ArgumentSha256 != expected.SourceArgumentSha256 ||
            observed.Origin != expected.SourceOrigin || observed.Trust != expected.SourceTrust ||
            observed.Explicit != expected.SourceExplicit ||
            observed.LastUpdateUnix < expected.SourceLastUpdateMinimumUnix)
        {
            throw new InvalidOperationException("source_drift");
        }

        return observed;
    }

    internal static SourceEvidence ValidateSourceInventory(
        ProviderContext expected,
        IReadOnlyList<SourceCatalogObservation> observedSources,
        SourceCatalogObservation namedSource)
    {
        if (observedSources.Count != 1 || observedSources[0] != namedSource)
        {
            throw new InvalidOperationException("source_drift");
        }

        SourceCatalogObservation observed = observedSources[0];
        if (observed.IsComposite || observed.AgreementCount != 0 ||
            observed.AuthenticationType != "none")
        {
            throw new InvalidOperationException(observed.AgreementCount != 0
                ? "source_agreement_required"
                : observed.AuthenticationType != "none"
                    ? "authentication_required"
                    : "source_drift");
        }

        return ValidateSource(expected, observed.Evidence);
    }

    internal static SourceProvisioningPlan PlanSourceProvisioning(
        ProviderContext expected,
        IReadOnlyList<SourceCatalogObservation> observedSources)
    {
        if (observedSources.Count > 32)
        {
            throw new InvalidOperationException("source_provision_failed");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var remove = new List<string>(observedSources.Count);
        bool foundExpected = false;
        foreach (SourceCatalogObservation observed in observedSources)
        {
            SourceEvidence evidence = observed.Evidence;
            if (observed.IsComposite || !TokenPattern.IsMatch(evidence.Name) ||
                !names.Add(evidence.Name))
            {
                throw new InvalidOperationException("source_provision_failed");
            }

            bool matchesExpected;
            try
            {
                _ = ValidateSource(expected, evidence);
                matchesExpected = observed.AgreementCount == 0 && observed.AuthenticationType == "none";
            }
            catch (InvalidOperationException)
            {
                matchesExpected = false;
            }

            if (matchesExpected)
            {
                if (foundExpected)
                {
                    throw new InvalidOperationException("source_provision_failed");
                }

                foundExpected = true;
            }
            else
            {
                remove.Add(evidence.Name);
            }
        }

        remove.Sort(StringComparer.Ordinal);
        return new SourceProvisioningPlan(remove, !foundExpected);
    }

    internal static string ComputeStateAuthoritySha256(
        int enrollmentEpoch,
        string policySha256,
        string constraintsSha256,
        string providerLockSha256,
        ProviderContext context)
    {
        if (enrollmentEpoch < 1)
        {
            throw new InvalidOperationException("runtime_state_drift");
        }

        EnsureDigest(policySha256);
        EnsureDigest(constraintsSha256);
        EnsureDigest(providerLockSha256);
        EnsureDigest(context.SourceArgumentSha256);
        EnsureDigest(context.DeploymentFileSetSha256);
        EnsureDigest(context.AppInstallerIdentitySha256);
        return Sha256Ascii(
            "machine-utilities-winget-state-authority|1\n" +
            $"epoch|{enrollmentEpoch.ToString(CultureInfo.InvariantCulture)}\n" +
            $"policy-sha256|{policySha256}\n" +
            $"constraints-sha256|{constraintsSha256}\n" +
            $"provider-lock-sha256|{providerLockSha256}\n" +
            $"source-id|{context.SourceId}\n" +
            $"source-name|{context.SourceName}\n" +
            $"source-type|{context.SourceType}\n" +
            $"source-argument-sha256|{context.SourceArgumentSha256}\n" +
            $"source-origin|{context.SourceOrigin}\n" +
            $"source-trust|{context.SourceTrust}\n" +
            $"source-explicit|{context.SourceExplicit.ToString().ToLowerInvariant()}\n" +
            $"source-last-update-min-unix|{context.SourceLastUpdateMinimumUnix.ToString(CultureInfo.InvariantCulture)}\n" +
            $"deployment-file-set-sha256|{context.DeploymentFileSetSha256}\n" +
            $"app-installer-identity-sha256|{context.AppInstallerIdentitySha256}\n" +
            "end-state-authority|\n");
    }

    internal static void ValidateStateIdentifierForAuthority(
        string stateIdentifier,
        int enrollmentEpoch,
        string stateAuthoritySha256)
    {
        Match match = StateIdentifierPattern.Match(stateIdentifier);
        if (!match.Success || enrollmentEpoch < 1 ||
            !DigestPattern.IsMatch(stateAuthoritySha256) ||
            !int.TryParse(match.Groups["epoch"].Value, NumberStyles.None, CultureInfo.InvariantCulture,
                out int embeddedEpoch) || embeddedEpoch != enrollmentEpoch ||
            match.Groups["authority"].Value != stateAuthoritySha256)
        {
            throw new InvalidOperationException("runtime_state_drift");
        }
    }

    internal static void ValidateExactPackageMatch(
        string expectedPackageId,
        IReadOnlyList<string> observedPackageIds,
        bool wasLimitExceeded)
    {
        if (wasLimitExceeded || observedPackageIds.Count != 1 || observedPackageIds[0] != expectedPackageId)
        {
            throw new InvalidOperationException(observedPackageIds.Count == 0
                ? "package_not_found"
                : "ambiguous_package");
        }
    }

    internal static void ValidateOperationPackageShape(BrokerMode mode, OperationPackageShape shape)
    {
        bool valid = mode switch
        {
            BrokerMode.Install => shape.RootResolvedFromProtectedRemote && !shape.IsComposite &&
                shape.ProtectedRemoteCatalogCount == 1 &&
                !shape.InstalledVersionAttached && shape.CompositeSearchBehavior == "none",
            BrokerMode.Upgrade => shape.RootResolvedFromProtectedRemote && shape.IsComposite &&
                shape.ProtectedRemoteCatalogCount == 1 &&
                shape.InstalledVersionAttached && shape.CompositeSearchBehavior == "all-catalogs-fixed",
            _ => false,
        };
        if (!valid)
        {
            throw new InvalidOperationException("package_object_shape_drift");
        }
    }

    internal static int SelectVersionCandidate(
        WinGetConstraint constraint,
        string expectedSourceId,
        IReadOnlyList<VersionCandidateObservation> candidates,
        Func<int, int, ProviderComparison> compareCandidates)
    {
        if (candidates.Count == 0 || candidates.Count > 4096 ||
            candidates.Select(candidate => candidate.Index).Distinct().Count() != candidates.Count)
        {
            throw new InvalidOperationException("package_not_found");
        }

        if (constraint.Mode == BrokerMode.Upgrade && constraint.MajorCeiling is null)
        {
            throw new InvalidOperationException("unsupported_version");
        }

        var eligible = new List<VersionCandidateObservation>();
        foreach (VersionCandidateObservation candidate in candidates)
        {
            if (candidate.Index < 0 || !VersionPattern.IsMatch(candidate.Version) ||
                !AtomPattern.IsMatch(candidate.SourceId))
            {
                throw new InvalidOperationException("unsupported_version");
            }

            if (candidate.SourceId != expectedSourceId)
            {
                throw new InvalidOperationException("source_drift");
            }

            if (candidate.Channel.Length != 0)
            {
                continue;
            }

            if (constraint.Mode == BrokerMode.Install)
            {
                if (candidate.Version == constraint.MinimumVersion)
                {
                    eligible.Add(candidate);
                }

                continue;
            }

            if (candidate.ToMinimum is ProviderComparison.Unknown or ProviderComparison.Lesser ||
                candidate.ToMaximum is ProviderComparison.Unknown or ProviderComparison.Greater ||
                candidate.ToInstalled != ProviderComparison.Greater ||
                !TryReadVersionMajor(candidate.Version, out int major) || major > constraint.MajorCeiling!.Value)
            {
                continue;
            }

            eligible.Add(candidate);
        }

        if (constraint.Mode == BrokerMode.Install)
        {
            if (eligible.Count != 1)
            {
                throw new InvalidOperationException(eligible.Count == 0 ? "package_not_found" : "ambiguous_version");
            }

            return eligible[0].Index;
        }

        if (constraint.Mode != BrokerMode.Upgrade || eligible.Count == 0)
        {
            throw new InvalidOperationException("package_not_found");
        }

        VersionCandidateObservation selected = eligible[0];
        for (int index = 1; index < eligible.Count; index++)
        {
            ProviderComparison comparison = compareCandidates(eligible[index].Index, selected.Index);
            if (comparison is ProviderComparison.Unknown or ProviderComparison.Equal)
            {
                throw new InvalidOperationException("ambiguous_version");
            }

            if (comparison == ProviderComparison.Greater)
            {
                selected = eligible[index];
            }
        }

        return selected.Index;
    }

    internal static InstallerObservation ValidateInstallerObservation(
        WinGetConstraint constraint,
        InstallerObservation observation)
    {
        if (observation.Scope != "system" || observation.Architecture != constraint.Architecture ||
            observation.Locale != constraint.Locale ||
            !constraint.InstallerTypes.Contains(observation.InstallerType, StringComparer.Ordinal) ||
            observation.AuthenticationType != "none" || observation.AgreementCount != 0)
        {
            throw new InvalidOperationException(observation.AuthenticationType != "none"
                ? "authentication_required"
                : observation.AgreementCount != 0
                    ? "package_agreement_required"
                    : "inapplicable_installer");
        }

        if ((observation.InstallerType == "zip" &&
                (observation.NestedInstallerType == "-" ||
                    !constraint.InstallerTypes.Contains(observation.NestedInstallerType, StringComparer.Ordinal))) ||
            (observation.InstallerType != "zip" && observation.NestedInstallerType != "-"))
        {
            throw new InvalidOperationException("unsupported_installer_type");
        }

        return observation;
    }

    internal static void ValidateDangerousOptions(
        WinGetConstraint constraint,
        string expectedSourceId,
        string expectedVersion,
        DangerousOptionsObservation observation)
    {
        if (observation.PackageCatalogId != expectedSourceId || observation.PackageVersion != expectedVersion ||
            observation.PackageChannel.Length != 0 || observation.Scope != "system" || observation.Mode != "silent" ||
            observation.AllowHashMismatch || observation.AllowUpgradeToUnknownVersion || observation.Force ||
            observation.AcceptPackageAgreements || observation.BypassStorePolicy || observation.SkipDependencies ||
            observation.HasAuthenticationArguments || observation.PreferredInstallLocation.Length != 0 ||
            observation.LogOutputPath.Length != 0 || observation.ReplacementInstallerArguments.Length != 0 ||
            observation.AdditionalInstallerArguments.Length != 0 || observation.AdditionalCatalogArguments.Length != 0 ||
            observation.CorrelationData.Length != 0 || observation.AllowedArchitectures.Count != 1 ||
            observation.AllowedArchitectures[0] != constraint.Architecture || observation.InstallerType == "unknown" ||
            !constraint.InstallerTypes.Contains(observation.InstallerType, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("dangerous_option_drift");
        }
    }

    internal static DeploymentEvidence ComputeDeploymentEvidence(
        IReadOnlyList<DeploymentFileObservation> files,
        IReadOnlyList<string> requiredPayloadNames,
        IReadOnlyList<string> requiredAuthenticodeNames,
        IReadOnlyList<AuthenticodeFileObservation> authenticodeFiles)
    {
        if (files.Count == 0 || files.Count > 512)
        {
            throw new InvalidOperationException("deployment_identity_drift");
        }

        DeploymentFileObservation[] orderedFiles = files.OrderBy(file => file.Name, StringComparer.Ordinal).ToArray();
        var exactNames = new HashSet<string>(StringComparer.Ordinal);
        var caseInsensitiveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalLength = 0;
        foreach (DeploymentFileObservation file in orderedFiles)
        {
            if (!DeploymentFileNamePattern.IsMatch(file.Name) || !exactNames.Add(file.Name) ||
                !caseInsensitiveNames.Add(file.Name) ||
                file.Length < 0 || file.Length > 268435456 || !DigestPattern.IsMatch(file.Sha256))
            {
                throw new InvalidOperationException("deployment_identity_drift");
            }

            totalLength = checked(totalLength + file.Length);
            if (totalLength > 1073741824)
            {
                throw new InvalidOperationException("deployment_identity_drift");
            }
        }

        string[] requiredFiles = requiredPayloadNames.Append("MachineUtilities.WinGetBroker.exe").ToArray();
        if (requiredFiles.Any(name => !DeploymentFileNamePattern.IsMatch(name)) ||
            requiredFiles.Distinct(StringComparer.Ordinal).Count() != requiredFiles.Length ||
            requiredFiles.Distinct(StringComparer.OrdinalIgnoreCase).Count() != requiredFiles.Length)
        {
            throw new InvalidOperationException("deployment_identity_drift");
        }

        foreach (string required in requiredFiles)
        {
            if (!exactNames.Contains(required))
            {
                throw new InvalidOperationException("deployment_identity_drift");
            }
        }

        string fileSetText = "winget-deployment-file-set|1\n" +
            string.Concat(orderedFiles.Select(file =>
                $"file|{file.Name}|{file.Length.ToString(CultureInfo.InvariantCulture)}|{file.Sha256}\n")) +
            "end-file-set|\n";
        string fileSetSha256 = Sha256Ascii(fileSetText);

        AuthenticodeFileObservation[] orderedAuthenticode = authenticodeFiles
            .OrderBy(file => file.Name, StringComparer.Ordinal)
            .ToArray();
        if (requiredAuthenticodeNames.Any(name => !DeploymentFileNamePattern.IsMatch(name)) ||
            requiredAuthenticodeNames.Distinct(StringComparer.Ordinal).Count() != requiredAuthenticodeNames.Count ||
            requiredAuthenticodeNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != requiredAuthenticodeNames.Count ||
            orderedAuthenticode.Length != requiredAuthenticodeNames.Count ||
            orderedAuthenticode.Select(file => file.Name).Distinct(StringComparer.Ordinal).Count() != orderedAuthenticode.Length ||
            orderedAuthenticode.Select(file => file.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != orderedAuthenticode.Length ||
            !orderedAuthenticode.Select(file => file.Name)
                .SequenceEqual(requiredAuthenticodeNames.OrderBy(name => name, StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new InvalidOperationException("deployment_identity_drift");
        }

        foreach (AuthenticodeFileObservation signed in orderedAuthenticode)
        {
            DeploymentFileObservation? matching = orderedFiles.SingleOrDefault(file => file.Name == signed.Name);
            if (matching is null || signed.FileSha256 != matching.Sha256 ||
                !DigestPattern.IsMatch(signed.SignerCertificateSha256))
            {
                throw new InvalidOperationException("deployment_identity_drift");
            }
        }

        string authenticodeText = "winget-authenticode-identity|1\n" +
            string.Concat(orderedAuthenticode.Select(file =>
                $"file|{file.Name}|{file.FileSha256}|{file.SignerCertificateSha256}\n")) +
            "end-identity|\n";
        return new DeploymentEvidence(
            fileSetSha256,
            Sha256Ascii(authenticodeText),
            orderedFiles);
    }

    internal static string ComputeProviderRuntimeRootsSha256(
        IReadOnlyList<ProviderRuntimeRootObservation> observations)
    {
        string[] expectedKinds = ["secure-settings", "settings", "state-cache", "temp-cache"];
        ProviderRuntimeRootObservation[] ordered = observations
            .OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ToArray();
        if (ordered.Length != expectedKinds.Length ||
            !ordered.Select(item => item.Kind).SequenceEqual(expectedKinds, StringComparer.Ordinal) ||
            ordered.Any(item => !DigestPattern.IsMatch(item.PathSha256) ||
                !DigestPattern.IsMatch(item.AclSha256)))
        {
            throw new InvalidOperationException("runtime_state_drift");
        }

        return Sha256Ascii(
            "winget-provider-runtime-roots|1\n" +
            string.Concat(ordered.Select(item =>
                $"root|{item.Kind}|{item.PathSha256}|{item.AclSha256}\n")) +
            "end-runtime-roots|\n");
    }

    internal static bool AttemptProviderRuntimeDirectoryCreation(
        bool provisioning,
        Func<bool> createProtectedDirectory)
    {
        ArgumentNullException.ThrowIfNull(createProtectedDirectory);
        return provisioning && createProtectedDirectory();
    }

    internal static (TerminalState State, string Reason) ClassifyMutation(
        bool callStarted,
        bool providerSucceeded,
        bool rebootRequired,
        bool postStateMatches,
        string failureReason)
    {
        if (!callStarted)
        {
            return (TerminalState.Rejected, NormalizeReason(failureReason));
        }

        if (rebootRequired)
        {
            return (TerminalState.Partial, "reboot_required");
        }

        if (!providerSucceeded)
        {
            return (TerminalState.Partial, NormalizeReason(failureReason));
        }

        if (!postStateMatches)
        {
            return (TerminalState.Partial, "post_state_unverified");
        }

        return (TerminalState.Completed, "post_state_verified");
    }

    internal static string ComputeSourceStateSha256(SourceEvidence source) => Sha256Ascii(
        "source-state|2\n" +
        $"source|{source.Id}|{source.Name}|{source.Type}|{source.ArgumentSha256}|{source.Origin}|{source.Trust}|{Bool(source.Explicit)}\n" +
        "end-source-state|\n");

    internal static string ComputePackageStateSha256(
        string packageId,
        string installedVersion,
        string candidateVersion,
        string architecture,
        string locale,
        string installerType,
        string sourceStateSha256)
    {
        foreach (string value in new[] { packageId, installedVersion, candidateVersion, architecture, locale, installerType })
        {
            EnsureResultAtomOrDash(value);
        }

        EnsureDigest(sourceStateSha256);
        return Sha256Ascii(
            "winget-package-state|1\n" +
            $"package|{packageId}|{installedVersion}|{candidateVersion}|machine|{architecture}|{locale}|{installerType}\n" +
            $"source-state-sha256|{sourceStateSha256}\n" +
            "end-package-state|\n");
    }

    internal static string RenderResult(HelperResult result)
    {
        EnsureRequestId(result.RequestId);
        EnsureKnownAction(result.ActionId);
        EnsureResultAtom(result.Reason);
        EnsureDigestOrDash(result.ProviderLockSha256);
        EnsureDigestOrDash(result.DeploymentFileSetSha256);
        EnsureDigestOrDash(result.AppInstallerIdentitySha256);
        EnsureResultAtomOrDash(result.ProviderVersion);
        EnsureDigestOrDash(result.StateIdentifierSha256);
        EnsureDigestOrDash(result.ProviderRuntimeRootsSha256);
        EnsureDigestOrDash(result.SettingsSha256);
        EnsureResultAtomOrDash(result.ProviderStatus);
        EnsureHexOrDash(result.ProviderExtendedError);
        EnsureUIntOrDash(result.ProviderInstallerError);
        EnsureDigestOrDash(result.PreStateSha256);
        EnsureDigestOrDash(result.PostStateSha256);

        var builder = new StringBuilder(2048);
        builder.AppendLine("winget-helper-result|1");
        builder.Append("request-id|").AppendLine(result.RequestId);
        builder.Append("action-id|").AppendLine(result.ActionId);
        builder.Append("state|").AppendLine(result.State.ToString().ToLowerInvariant());
        builder.Append("reason|").AppendLine(result.Reason);
        builder.Append("provider-lock-sha256|").AppendLine(result.ProviderLockSha256);
        builder.Append("deployment-file-set-sha256|").AppendLine(result.DeploymentFileSetSha256);
        builder.Append("app-installer-identity-sha256|").AppendLine(result.AppInstallerIdentitySha256);
        builder.Append("provider-version|").AppendLine(result.ProviderVersion);
        builder.Append("state-identifier-sha256|").AppendLine(result.StateIdentifierSha256);
        builder.Append("provider-runtime-roots-sha256|").AppendLine(result.ProviderRuntimeRootsSha256);
        builder.Append("settings-sha256|").AppendLine(result.SettingsSha256);

        if (result.Source is null)
        {
            builder.AppendLine("source|-|-|-|-|-|-|-|-");
        }
        else
        {
            SourceEvidence source = result.Source;
            EnsureResultAtom(source.Id);
            EnsureResultAtom(source.Name);
            EnsureResultAtom(source.Type);
            EnsureDigest(source.ArgumentSha256);
            EnsureResultAtom(source.Origin);
            EnsureResultAtom(source.Trust);
            if (source.LastUpdateUnix < 0)
            {
                throw new FormatException("invalid_result");
            }

            builder.Append("source|").Append(source.Id).Append('|').Append(source.Name).Append('|')
                .Append(source.Type).Append('|').Append(source.ArgumentSha256).Append('|').Append(source.Origin)
                .Append('|').Append(source.Trust).Append('|').Append(Bool(source.Explicit)).Append('|')
                .AppendLine(source.LastUpdateUnix.ToString(CultureInfo.InvariantCulture));
        }

        builder.AppendLine("dependency-authority|source-delegated-all");
        builder.AppendLine("installer-hash-authority|provider-enforced-manifest-hash");
        builder.AppendLine("dependency-closure|not-exposed-by-provider");
        builder.AppendLine("dependency-provenance|not-exposed-by-provider");
        builder.AppendLine("windows-features|root-installer-provider-managed");
        builder.Append("package-count|").AppendLine(result.Packages.Count.ToString(CultureInfo.InvariantCulture));

        PackageEvidence[] packages = result.Packages.OrderBy(item => item.Index).ToArray();
        for (int expectedIndex = 0; expectedIndex < packages.Length; expectedIndex++)
        {
            PackageEvidence package = packages[expectedIndex];
            if (package.Index != expectedIndex)
            {
                throw new FormatException("invalid_result");
            }

            foreach (string value in new[]
            {
                package.PolicyToken, package.PackageId, package.InstalledVersion, package.CandidateVersion,
                package.Architecture, package.Locale, package.InstallerType, package.NestedInstallerType,
                package.PostVersion,
            })
            {
                EnsureResultAtomOrDash(value);
            }

            builder.Append("package|").Append(package.Index.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(package.PolicyToken).Append('|').Append(package.PackageId).Append('|')
                .Append(package.InstalledVersion).Append('|').Append(package.CandidateVersion).Append("|machine|")
                .Append(package.Architecture).Append('|').Append(package.Locale).Append('|')
                .Append(package.InstallerType).Append('|').AppendLine(package.NestedInstallerType);
            builder.Append("post-package|").Append(package.Index.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(package.PostVersion).AppendLine("|machine");
        }

        builder.Append("provider-status|").AppendLine(result.ProviderStatus);
        builder.Append("provider-extended-error|").AppendLine(result.ProviderExtendedError);
        builder.Append("provider-installer-error|").AppendLine(result.ProviderInstallerError);
        builder.Append("reboot-required|").AppendLine(Bool(result.RebootRequired));
        builder.Append("pre-state-sha256|").AppendLine(result.PreStateSha256);
        builder.Append("post-state-sha256|").AppendLine(result.PostStateSha256);
        builder.AppendLine("end-result|");

        string text = builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        if (bytes.Length > MaxResultBytes || Encoding.ASCII.GetString(bytes) != text)
        {
            throw new FormatException("invalid_result");
        }

        return text;
    }

    internal static string RenderPreconditionProbeResult(PreconditionProbeResult result)
    {
        EnsureRequestId(result.RequestId);
        BrokerMode actionMode = ModeForAction(result.ActionId);
        if ((actionMode == BrokerMode.Inventory && result.PolicyToken != "-") ||
            (actionMode != BrokerMode.Inventory && !TokenPattern.IsMatch(result.PolicyToken)))
        {
            throw new FormatException("invalid_precondition_probe");
        }

        EnsureDigest(result.PreconditionSha256);
        string text = string.Join('\n', new[]
        {
            "winget-precondition-probe|1",
            $"request-id|{result.RequestId}",
            $"action-id|{result.ActionId}",
            $"policy-token|{result.PolicyToken}",
            $"precondition-sha256|{result.PreconditionSha256}",
            "end-precondition-probe|",
            string.Empty,
        });
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        if (bytes.Length > MaxResultBytes || Encoding.ASCII.GetString(bytes) != text)
        {
            throw new FormatException("invalid_precondition_probe");
        }

        return text;
    }

    internal static string RenderProvisionResult(ProviderProvisionResult result)
    {
        EnsureResultAtom(result.Reason);
        if (result.EnrollmentEpoch < 1)
        {
            throw new FormatException("invalid_provision_result");
        }

        foreach (string digest in new[]
        {
            result.GenerationSha256,
            result.ProviderLockSha256,
            result.DeploymentFileSetSha256,
            result.AppInstallerIdentitySha256,
            result.StateIdentifierSha256,
            result.ProviderRuntimeRootsSha256,
            result.SettingsSha256,
        })
        {
            EnsureDigestOrDash(digest);
        }

        EnsureResultAtomOrDash(result.ProviderVersion);
        var builder = new StringBuilder(1024);
        builder.AppendLine("winget-provider-provision-result|1");
        builder.Append("state|").AppendLine(result.State.ToString().ToLowerInvariant());
        builder.Append("reason|").AppendLine(result.Reason);
        builder.Append("enrollment-epoch|").AppendLine(
            result.EnrollmentEpoch.ToString(CultureInfo.InvariantCulture));
        builder.Append("generation-sha256|").AppendLine(result.GenerationSha256);
        builder.Append("provider-lock-sha256|").AppendLine(result.ProviderLockSha256);
        builder.Append("deployment-file-set-sha256|").AppendLine(result.DeploymentFileSetSha256);
        builder.Append("app-installer-identity-sha256|").AppendLine(result.AppInstallerIdentitySha256);
        builder.Append("provider-version|").AppendLine(result.ProviderVersion);
        builder.Append("state-identifier-sha256|").AppendLine(result.StateIdentifierSha256);
        builder.Append("provider-runtime-roots-sha256|").AppendLine(result.ProviderRuntimeRootsSha256);
        builder.Append("settings-sha256|").AppendLine(result.SettingsSha256);
        AppendSource(builder, result.Source, "invalid_provision_result");
        builder.AppendLine("end-provision-result|");

        string text = builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        if (bytes.Length > MaxResultBytes || Encoding.ASCII.GetString(bytes) != text)
        {
            throw new FormatException("invalid_provision_result");
        }

        return text;
    }

    internal static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static string Sha256Ascii(string text)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        if (Encoding.ASCII.GetString(bytes) != text)
        {
            throw new FormatException("non_ascii_value");
        }

        return Sha256(bytes);
    }

    internal static string Sha256Utf8(string text) => Sha256(Encoding.UTF8.GetBytes(text));

    internal static string NormalizeReason(string reason) => AtomPattern.IsMatch(reason) ? reason : "provider_failure";

    internal static void EnsureResultAtom(string value)
    {
        if (!AtomPattern.IsMatch(value))
        {
            throw new FormatException("invalid_result");
        }
    }

    internal static void EnsureResultAtomOrDash(string value)
    {
        if (value != "-" && !AtomPattern.IsMatch(value) && !VersionPattern.IsMatch(value))
        {
            throw new FormatException("invalid_result");
        }
    }

    internal static bool IsSafeSourceArgument(string value)
    {
        if (value.Length is < 1 or > 2048 || value.Any(character => character is < '!' or > '~') ||
            !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || uri.Port != 443 ||
            uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6))
        {
            return false;
        }

        return string.Equals(uri.AbsoluteUri, value, StringComparison.Ordinal);
    }

    internal static bool IsAllowedArchitecture(string value) => Architectures.Contains(value);

    internal static bool IsAllowedInstallerType(string value) => InstallerTypes.Contains(value);

    internal static bool IsVersion(string value) => VersionPattern.IsMatch(value);

    internal static string NormalizeArchitecture(string value) => value switch
    {
        "X86" => "x86",
        "X64" => "x64",
        "Arm" => "arm",
        "Arm64" => "arm64",
        "Neutral" => "neutral",
        _ => throw new InvalidOperationException("unsupported_architecture"),
    };

    internal static string NormalizeInstallerType(string value) => value switch
    {
        "Burn" => "burn",
        "Exe" => "exe",
        "Inno" => "inno",
        "Msi" => "msi",
        "Msix" => "msix",
        "Nullsoft" => "nullsoft",
        "Portable" => "portable",
        "Wix" => "wix",
        "Zip" => "zip",
        _ => throw new InvalidOperationException("unsupported_installer_type"),
    };

    private static void ValidatePackageLockTarget(
        JsonElement dependencies,
        string targetName,
        IReadOnlyList<(string Name, string Version, string ContentHash)> expected)
    {
        if (!dependencies.TryGetProperty(targetName, out JsonElement target) || target.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("provider_lock_drift");
        }

        ValidateUniqueJsonProperties(target, "provider_lock_drift");
        string[] actualNames = target.EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        string[] expectedNames = expected.Select(item => item.Name)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (!actualNames.SequenceEqual(expectedNames, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("provider_lock_drift");
        }

        foreach ((string name, string packageVersion, string contentHash) in expected)
        {
            JsonElement package = target.GetProperty(name);
            if (package.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("provider_lock_drift");
            }

            ValidateUniqueJsonProperties(package, "provider_lock_drift");
            string[] expectedFields = ["contentHash", "requested", "resolved", "type"];
            string[] actualFields = package.EnumerateObject()
                .Select(property => property.Name)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            if (!actualFields.SequenceEqual(expectedFields, StringComparer.Ordinal) ||
                package.GetProperty("type").GetString() != "Direct" ||
                package.GetProperty("requested").GetString() != $"[{packageVersion}, {packageVersion}]" ||
                package.GetProperty("resolved").GetString() != packageVersion ||
                package.GetProperty("contentHash").GetString() != contentHash)
            {
                throw new InvalidOperationException("provider_lock_drift");
            }
        }
    }

    private static void ValidateUniqueJsonProperties(JsonElement element, string reason = "runtime_state_drift")
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (JsonProperty property in element.EnumerateObject())
                    {
                        if (!names.Add(property.Name))
                        {
                            throw new InvalidOperationException(reason);
                        }

                        ValidateUniqueJsonProperties(property.Value, reason);
                    }

                    break;
                }
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    ValidateUniqueJsonProperties(item, reason);
                }

                break;
        }
    }

    private static string[] ParseCanonicalLines(ReadOnlySpan<byte> bytes, int maximumBytes, string reason)
    {
        if (bytes.Length == 0 || bytes.Length > maximumBytes || bytes[^1] != (byte)'\n')
        {
            throw new FormatException("invalid_" + reason);
        }

        for (int index = 0; index < bytes.Length; index++)
        {
            byte value = bytes[index];
            if (value != (byte)'\n' && (value < 0x20 || value > 0x7e))
            {
                throw new FormatException("invalid_" + reason);
            }
        }

        string text = Encoding.ASCII.GetString(bytes[..^1]);
        string[] lines = text.Split('\n');
        if (lines.Any(line => line.Length == 0))
        {
            throw new FormatException("invalid_" + reason);
        }

        return lines;
    }

    private static void AppendSource(StringBuilder builder, SourceEvidence? source, string reason)
    {
        if (source is null)
        {
            builder.AppendLine("source|-|-|-|-|-|-|-|-");
            return;
        }

        EnsureResultAtom(source.Id);
        EnsureResultAtom(source.Name);
        EnsureResultAtom(source.Type);
        EnsureDigest(source.ArgumentSha256);
        EnsureResultAtom(source.Origin);
        EnsureResultAtom(source.Trust);
        if (source.LastUpdateUnix < 0)
        {
            throw new FormatException(reason);
        }

        builder.Append("source|").Append(source.Id).Append('|').Append(source.Name).Append('|')
            .Append(source.Type).Append('|').Append(source.ArgumentSha256).Append('|').Append(source.Origin)
            .Append('|').Append(source.Trust).Append('|').Append(Bool(source.Explicit)).Append('|')
            .AppendLine(source.LastUpdateUnix.ToString(CultureInfo.InvariantCulture));
    }

    private static string ReadField(string line, string name)
    {
        string prefix = name + "|";
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new FormatException("invalid_field");
        }

        string value = line[prefix.Length..];
        if (value.Length == 0 || value.Contains('|'))
        {
            throw new FormatException("invalid_field");
        }

        return value;
    }

    private static string ReadAtomField(string line, string name)
    {
        string value = ReadField(line, name);
        if (!AtomPattern.IsMatch(value))
        {
            throw new FormatException("invalid_field");
        }

        return value;
    }

    private static string ReadDigestField(string line, string name)
    {
        string value = ReadField(line, name);
        EnsureDigest(value);
        return value;
    }

    private static int ParsePositiveInt(string value, int maximum)
    {
        if (value.Length > 10 || value.Length == 0 || value[0] == '0' ||
            !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) ||
            parsed < 1 || parsed > maximum)
        {
            throw new FormatException("invalid_integer");
        }

        return parsed;
    }

    private static long ParseNonNegativeLong(string value, long maximum)
    {
        if (value.Length == 0 || (value.Length > 1 && value[0] == '0') ||
            !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) ||
            parsed < 0 || parsed > maximum)
        {
            throw new FormatException("invalid_integer");
        }

        return parsed;
    }

    private static void ValidateAptInstall(string[] fields)
    {
        if (fields.Length != 8 || fields[1] != "apt.install-package-version.v1" ||
            !TokenPattern.IsMatch(fields[2]) || !AtomPattern.IsMatch(fields[3]) ||
            !AtomPattern.IsMatch(fields[4]) || !VersionPattern.IsMatch(fields[5]) ||
            !DigestPattern.IsMatch(fields[6]) || !DigestPattern.IsMatch(fields[7]))
        {
            throw new FormatException("invalid_constraints");
        }
    }

    private static void ValidateAptUpgrade(string[] fields)
    {
        if (fields.Length != 9 || fields[1] != "apt.upgrade-package.v1" ||
            !TokenPattern.IsMatch(fields[2]) || !AtomPattern.IsMatch(fields[3]) ||
            !AtomPattern.IsMatch(fields[4]) || !VersionPattern.IsMatch(fields[5]) ||
            !VersionPattern.IsMatch(fields[6]) || !IsCanonicalNonNegativeInt(fields[7], int.MaxValue) ||
            !DigestPattern.IsMatch(fields[8]))
        {
            throw new FormatException("invalid_constraints");
        }
    }

    private static void ValidateProfile(string[] fields)
    {
        if (fields.Length != 9 || !TokenPattern.IsMatch(fields[1]) || !SidPattern.IsMatch(fields[2]) ||
            !AtomPattern.IsMatch(fields[3]) || !DigestPattern.IsMatch(fields[4]) ||
            !DigestPattern.IsMatch(fields[5]) || fields[6] is not ("managed-only" or "managed-and-prune") ||
            !IsCanonicalPositiveInt(fields[7], 100000) || !IsCanonicalPositiveInt(fields[8], 1073741824))
        {
            throw new FormatException("invalid_constraints");
        }
    }

    private static WinGetConstraint ParseWinGetInstall(string[] fields)
    {
        if (fields.Length != 14 || fields[1] != InstallAction || !TokenPattern.IsMatch(fields[2]) ||
            !AtomPattern.IsMatch(fields[3]) || !AtomPattern.IsMatch(fields[4]) || !AtomPattern.IsMatch(fields[5]) ||
            !DigestPattern.IsMatch(fields[6]) || fields[7] != "machine" || !Architectures.Contains(fields[8]) ||
            !IsLocale(fields[9]) || !TryParseInstallerTypes(fields[10], out string[] installerTypes) ||
            !VersionPattern.IsMatch(fields[11]) || fields[12] != "provider-enforced-manifest-hash" ||
            fields[13] != "source-delegated-all")
        {
            throw new FormatException("invalid_constraints");
        }

        return new WinGetConstraint(
            BrokerMode.Install,
            fields[1],
            fields[2],
            fields[3],
            fields[4],
            fields[5],
            fields[6],
            fields[8],
            fields[9],
            installerTypes,
            fields[11],
            fields[11],
            null);
    }

    private static WinGetConstraint ParseWinGetUpgrade(string[] fields)
    {
        if (fields.Length != 16 || fields[1] != UpgradeAction || !TokenPattern.IsMatch(fields[2]) ||
            !AtomPattern.IsMatch(fields[3]) || !AtomPattern.IsMatch(fields[4]) || !AtomPattern.IsMatch(fields[5]) ||
            !DigestPattern.IsMatch(fields[6]) || fields[7] != "machine" || !Architectures.Contains(fields[8]) ||
            !IsLocale(fields[9]) || !TryParseInstallerTypes(fields[10], out string[] installerTypes) ||
            !VersionPattern.IsMatch(fields[11]) || !VersionPattern.IsMatch(fields[12]) ||
            !IsCanonicalNonNegativeInt(fields[13], int.MaxValue) ||
            fields[14] != "provider-enforced-manifest-hash" || fields[15] != "source-delegated-all")
        {
            throw new FormatException("invalid_constraints");
        }

        return new WinGetConstraint(
            BrokerMode.Upgrade,
            fields[1],
            fields[2],
            fields[3],
            fields[4],
            fields[5],
            fields[6],
            fields[8],
            fields[9],
            installerTypes,
            fields[11],
            fields[12],
            int.Parse(fields[13], NumberStyles.None, CultureInfo.InvariantCulture));
    }

    private static bool TryParseInstallerTypes(string value, out string[] installerTypes)
    {
        installerTypes = value.Split(',');
        if (installerTypes.Length == 0 || installerTypes.Any(type => !InstallerTypes.Contains(type)))
        {
            return false;
        }

        string[] canonical = installerTypes.OrderBy(type => type, StringComparer.Ordinal).Distinct(StringComparer.Ordinal).ToArray();
        return canonical.Length == installerTypes.Length && canonical.SequenceEqual(installerTypes, StringComparer.Ordinal);
    }

    private static bool TryReadVersionMajor(string version, out int major)
    {
        major = 0;
        int separator = version.IndexOfAny(['.', '-', '+', ':', '~', '_']);
        ReadOnlySpan<char> first = separator < 0 ? version.AsSpan() : version.AsSpan(0, separator);
        return first.Length != 0 && first.Length <= 10 &&
            int.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out major) && major >= 0;
    }

    private static bool IsLocale(string value)
    {
        if (value.Length is < 2 or > 20 || !char.IsAsciiLetter(value[0]))
        {
            return false;
        }

        return value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-');
    }

    private static bool IsCanonicalNonNegativeInt(string value, int maximum) =>
        value.Length != 0 && (value.Length == 1 || value[0] != '0') &&
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed is >= 0 && parsed <= maximum;

    private static bool IsCanonicalPositiveInt(string value, int maximum) =>
        value.Length != 0 && value[0] != '0' &&
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed is >= 1 && parsed <= maximum;

    private static string Bool(bool value) => value ? "true" : "false";

    private static void EnsureDigest(string value)
    {
        if (!DigestPattern.IsMatch(value))
        {
            throw new FormatException("invalid_digest");
        }
    }

    private static void EnsureDigestOrDash(string value)
    {
        if (value != "-")
        {
            EnsureDigest(value);
        }
    }

    private static void EnsureHexOrDash(string value)
    {
        if (value != "-" && !Regex.IsMatch(value, "^0x[0-9a-f]{8}$", RegexOptions.CultureInvariant))
        {
            throw new FormatException("invalid_result");
        }
    }

    private static void EnsureUIntOrDash(string value)
    {
        if (value != "-" && (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _) ||
            (value.Length > 1 && value[0] == '0')))
        {
            throw new FormatException("invalid_result");
        }
    }

    private static void EnsureRequestId(string value)
    {
        if (!RequestIdPattern.IsMatch(value))
        {
            throw new FormatException("invalid_result");
        }
    }

    private static void EnsureKnownAction(string value)
    {
        if (!ActionSchema.ContainsKey(value))
        {
            throw new FormatException("invalid_result");
        }
    }
}
