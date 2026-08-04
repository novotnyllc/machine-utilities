using System.Text;
using System.Text.Json;
using MachineUtilities.WinGetBroker;

var tests = new (string Name, Action Body)[]
{
    ("request_accepts_only_mode_bound_fields", RequestAcceptsOnlyModeBoundFields),
    ("request_rejects_carriage_return_and_extra_field", RequestRejectsCarriageReturnAndExtraField),
    ("precondition_probe_request_and_result_are_exact", PreconditionProbeRequestAndResultAreExact),
    ("provision_protocol_is_distinct_and_generation_bound", ProvisionProtocolIsDistinctAndGenerationBound),
    ("active_generation_pointer_and_envelope_are_exact", ActiveGenerationPointerAndEnvelopeAreExact),
    ("context_binds_canonical_protected_source", ContextBindsCanonicalProtectedSource),
    ("canonical_uri_vector_requires_raw_absolute_uri", CanonicalUriVectorRequiresRawAbsoluteUri),
    ("provider_lock_is_exact", ProviderLockIsExact),
    ("policy_constraint_digest_and_token_are_bound", PolicyConstraintDigestAndTokenAreBound),
    ("constraint_source_bindings_are_nonfungible", ConstraintSourceBindingsAreNonfungible),
    ("settings_keep_dependency_and_dangerous_controls_fixed", SettingsKeepDependencyAndDangerousControlsFixed),
    ("persisted_settings_and_locked_restore_hashes_fail_closed", PersistedSettingsAndLockedRestoreHashesFailClosed),
    ("source_inventory_and_exact_search_fail_closed", SourceInventoryAndExactSearchFailClosed),
    ("source_precondition_ignores_refresh_timestamp", SourcePreconditionIgnoresRefreshTimestamp),
    ("provider_runtime_roots_are_created_only_during_provisioning", ProviderRuntimeRootsAreCreatedOnlyDuringProvisioning),
    ("operation_package_shape_allows_only_fixed_upgrade_association", OperationPackageShapeAllowsOnlyFixedUpgradeAssociation),
    ("version_selection_is_unordered_bounded_and_unambiguous", VersionSelectionIsUnorderedBoundedAndUnambiguous),
    ("installer_and_mutation_options_are_fixed", InstallerAndMutationOptionsAreFixed),
    ("provider_mutation_invocation_enforces_real_ordering", ProviderMutationInvocationEnforcesRealOrdering),
    ("deployment_evidence_is_complete_and_observed", DeploymentEvidenceIsCompleteAndObserved),
    ("terminal_mapping_is_conservative_after_launch", TerminalMappingIsConservativeAfterLaunch),
    ("result_is_deterministic_bounded_and_redacted", ResultIsDeterministicBoundedAndRedacted),
};

int failures = 0;
foreach ((string name, Action body) in tests)
{
    try
    {
        body();
        Console.WriteLine($"ok {name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"not ok {name}: {exception}");
    }
}

Console.WriteLine($"tests={tests.Length} failures={failures}");
return failures == 0 ? 0 : 1;

static void RequestAcceptsOnlyModeBoundFields()
{
    Equal(BrokerCore.BrokerMode.Inventory, BrokerCore.ParseMode(new[] { "inventory" }));
    Equal(BrokerCore.BrokerMode.Install, BrokerCore.ParseMode(new[] { "install" }));
    Equal(BrokerCore.BrokerMode.Upgrade, BrokerCore.ParseMode(new[] { "upgrade" }));
    Throws<FormatException>(() => BrokerCore.ParseMode(new[] { "install", "caller-option" }));

    BrokerCore.HelperRequest install = BrokerCore.ParseRequest(
        Ascii(Request(BrokerCore.InstallAction, "allow-7")),
        BrokerCore.BrokerMode.Install);
    Equal("allow-7", install.PolicyToken);
    Equal(7, install.EnrollmentEpoch);

    BrokerCore.HelperRequest inventory = BrokerCore.ParseRequest(
        Ascii(Request(BrokerCore.InventoryAction, "-")),
        BrokerCore.BrokerMode.Inventory);
    Equal("-", inventory.PolicyToken);

    Throws<FormatException>(() => BrokerCore.ParseRequest(
        Ascii(Request(BrokerCore.InstallAction, "allow-7")),
        BrokerCore.BrokerMode.Upgrade));
    Throws<FormatException>(() => BrokerCore.ParseRequest(
        Ascii(Request(BrokerCore.InventoryAction, "caller-package")),
        BrokerCore.BrokerMode.Inventory));
}

static void RequestRejectsCarriageReturnAndExtraField()
{
    string valid = Request(BrokerCore.InstallAction, "allow-7");
    Throws<FormatException>(() => BrokerCore.ParseRequest(
        Encoding.ASCII.GetBytes(valid.Replace("\n", "\r\n", StringComparison.Ordinal)),
        BrokerCore.BrokerMode.Install));

    string widened = valid.Replace("end-request|\n", "package-id|caller-controlled\nend-request|\n", StringComparison.Ordinal);
    Throws<FormatException>(() => BrokerCore.ParseRequest(Ascii(widened), BrokerCore.BrokerMode.Install));
    Throws<FormatException>(() => BrokerCore.ParseRequest(
        Enumerable.Repeat((byte)'A', BrokerCore.MaxRequestBytes + 1).ToArray(),
        BrokerCore.BrokerMode.Install));
}

static void PreconditionProbeRequestAndResultAreExact()
{
    Equal(BrokerCore.BrokerMode.Probe, BrokerCore.ParseMode(new[] { "probe" }));
    BrokerCore.HelperRequest inventory = BrokerCore.ParseRequest(
        Ascii(Request(BrokerCore.InventoryAction, "-", preconditionSha256: BrokerCore.EmptySha256)),
        BrokerCore.BrokerMode.Probe);
    BrokerCore.HelperRequest install = BrokerCore.ParseRequest(
        Ascii(Request(BrokerCore.InstallAction, "allow-7", preconditionSha256: BrokerCore.EmptySha256)),
        BrokerCore.BrokerMode.Probe);
    BrokerCore.HelperRequest upgrade = BrokerCore.ParseRequest(
        Ascii(Request(BrokerCore.UpgradeAction, "upgrade-7", preconditionSha256: BrokerCore.EmptySha256)),
        BrokerCore.BrokerMode.Probe);
    Equal(BrokerCore.BrokerMode.Inventory, BrokerCore.ModeForAction(inventory.ActionId));
    Equal(BrokerCore.BrokerMode.Install, BrokerCore.ModeForAction(install.ActionId));
    Equal(BrokerCore.BrokerMode.Upgrade, BrokerCore.ModeForAction(upgrade.ActionId));
    Throws<FormatException>(() => BrokerCore.ParseRequest(
        Ascii(Request(BrokerCore.InstallAction, "allow-7")), BrokerCore.BrokerMode.Probe));
    Throws<FormatException>(() => BrokerCore.ParseRequest(
        Ascii(Request(BrokerCore.InstallAction, "allow-7", preconditionSha256: BrokerCore.EmptySha256)),
        BrokerCore.BrokerMode.Install));
    Throws<FormatException>(() => BrokerCore.ParseRequest(
        Ascii(Request(BrokerCore.InventoryAction, "caller-token", preconditionSha256: BrokerCore.EmptySha256)),
        BrokerCore.BrokerMode.Probe));
    Throws<FormatException>(() => BrokerCore.ParseRequest(
        Ascii(Request("apt.autoremove.v1", "-", preconditionSha256: BrokerCore.EmptySha256)),
        BrokerCore.BrokerMode.Probe));

    string preconditionSha256 = new string('a', 64);
    string expected = string.Join('\n', new[]
    {
        "winget-precondition-probe|1",
        "request-id|request-0123456789abcdef0123456789abcdef",
        $"action-id|{BrokerCore.InstallAction}",
        "policy-token|allow-7",
        $"precondition-sha256|{preconditionSha256}",
        "end-precondition-probe|",
        string.Empty,
    });
    string actual = BrokerCore.RenderPreconditionProbeResult(new BrokerCore.PreconditionProbeResult(
        "request-0123456789abcdef0123456789abcdef",
        BrokerCore.InstallAction,
        "allow-7",
        preconditionSha256));
    Equal(expected, actual);
    True(Encoding.ASCII.GetByteCount(actual) <= BrokerCore.MaxResultBytes);
    False(actual.Contains("state|", StringComparison.Ordinal));
    False(actual.Contains("reason|", StringComparison.Ordinal));
}

static void ProvisionProtocolIsDistinctAndGenerationBound()
{
    Throws<FormatException>(() => BrokerCore.ParseMode(new[] { "provision" }));
    BrokerCore.ProvisionRequest request = BrokerCore.ParseProvisionRequest(Ascii(ProvisionRequest()));
    Equal(7, request.EnrollmentEpoch);
    Equal(new string('0', 64), request.GenerationSha256);
    BrokerCore.ProviderContext context = BrokerCore.ParseProviderContext(Ascii(ProviderContext()));
    string authority = BrokerCore.ComputeStateAuthoritySha256(
        7,
        request.PolicySha256,
        request.ConstraintsSha256,
        request.ProviderLockSha256,
        context);
    Equal("2b048d26707cdbfdfb379b025237d5bfccb259bdc5fc5d621f3138f39dbd6a87", authority);
    BrokerCore.ValidateStateIdentifierForAuthority($"machine-utilities-e7-{authority}", 7, authority);
    Equal(authority, BrokerCore.ComputeStateAuthoritySha256(
        7,
        request.PolicySha256,
        request.ConstraintsSha256,
        request.ProviderLockSha256,
        context with { StateIdentifier = $"machine-utilities-e7-{new string('f', 64)}" }));
    False(authority == BrokerCore.ComputeStateAuthoritySha256(
        7,
        request.PolicySha256,
        request.ConstraintsSha256,
        request.ProviderLockSha256,
        context with { SourceTrust = "none" }));
    False(authority == BrokerCore.ComputeStateAuthoritySha256(
        7,
        request.PolicySha256,
        request.ConstraintsSha256,
        request.ProviderLockSha256,
        context with { AppInstallerIdentitySha256 = new string('d', 64) }));
    ThrowsReason<InvalidOperationException>(
        "runtime_state_drift",
        () => BrokerCore.ValidateStateIdentifierForAuthority($"machine-utilities-e8-{authority}", 7, authority));
    ThrowsReason<InvalidOperationException>(
        "runtime_state_drift",
        () => BrokerCore.ValidateStateIdentifierForAuthority(
            "machine-utilities-e7-0000000000000000", 7, authority));
    ThrowsReason<InvalidOperationException>(
        "runtime_state_drift",
        () => BrokerCore.ValidateStateIdentifierForAuthority(
            $"machine-utilities-e7-{authority[..63]}{(authority[63] == '0' ? '1' : '0')}", 7, authority));
    Throws<FormatException>(() => BrokerCore.ParseProvisionRequest(Ascii(
        ProvisionRequest().Replace("end-provision-request|\n", "mode|inventory\nend-provision-request|\n",
            StringComparison.Ordinal))));
    True(BrokerCore.IsSafeSourceArgument(SourceArgument()));
    False(BrokerCore.IsSafeSourceArgument("http://example.invalid/catalog"));
    False(BrokerCore.IsSafeSourceArgument("https://user@example.invalid/catalog"));
    False(BrokerCore.IsSafeSourceArgument("https://example.invalid/catalog?token=value"));
    False(BrokerCore.IsSafeSourceArgument("https://EXAMPLE.invalid/catalog"));
}

static void ActiveGenerationPointerAndEnvelopeAreExact()
{
    BrokerCore.HelperRequest request = BrokerCore.ParseRequest(
        Ascii(Request(BrokerCore.InstallAction, "allow-7")),
        BrokerCore.BrokerMode.Install);
    string generationSha256 = BrokerCore.ComputeGenerationSha256(
        request.EnrollmentEpoch,
        request.PolicySha256,
        request.ConstraintsSha256,
        request.ContextSha256,
        request.ProviderLockSha256,
        new string('5', 64));
    Equal(
        "8bc9ecd600b256002020a754d3e439e3845882143bf26a516d816039d79960ee",
        generationSha256);
    string pointerText = string.Join('\n', new[]
    {
        "machine-utilities-active-generation|1",
        "epoch|7",
        $"generation-sha256|{generationSha256}",
        "end-generation|",
    }) + "\n";
    BrokerCore.ActiveGenerationPointer pointer = BrokerCore.ParseActiveGenerationPointer(Ascii(pointerText));
    Equal(7, pointer.Epoch);
    Equal(generationSha256, pointer.GenerationSha256);
    Equal(
        generationSha256,
        BrokerCore.ValidateGenerationEnvelope(
            pointer,
            request,
            request.PolicySha256,
            request.ConstraintsSha256,
            request.ContextSha256,
            request.ProviderLockSha256,
            new string('5', 64)));

    Throws<FormatException>(() => BrokerCore.ParseActiveGenerationPointer(
        Ascii(pointerText.Replace("epoch|7", "epoch|07", StringComparison.Ordinal))));
    Throws<FormatException>(() => BrokerCore.ParseActiveGenerationPointer(
        Encoding.ASCII.GetBytes(pointerText.Replace("\n", "\r\n", StringComparison.Ordinal))));
    Throws<FormatException>(() => BrokerCore.ParseActiveGenerationPointer(
        Ascii(pointerText.Replace(generationSha256, generationSha256.ToUpperInvariant(), StringComparison.Ordinal))));
    ThrowsReason<InvalidOperationException>(
        "active_state_drift",
        () => BrokerCore.ValidateGenerationEnvelope(
            pointer,
            request with { EnrollmentEpoch = 8 },
            request.PolicySha256,
            request.ConstraintsSha256,
            request.ContextSha256,
            request.ProviderLockSha256,
            new string('5', 64)));
    ThrowsReason<InvalidOperationException>(
        "active_state_drift",
        () => BrokerCore.ValidateGenerationEnvelope(
            pointer with { GenerationSha256 = new string('f', 64) },
            request,
            request.PolicySha256,
            request.ConstraintsSha256,
            request.ContextSha256,
            request.ProviderLockSha256,
            new string('5', 64)));
    ThrowsReason<InvalidOperationException>(
        "active_state_drift",
        () => BrokerCore.ValidateGenerationEnvelope(
            pointer,
            request,
            request.PolicySha256,
            request.ConstraintsSha256,
            request.ContextSha256,
            request.ProviderLockSha256,
            new string('6', 64)));
}

static void ContextBindsCanonicalProtectedSource()
{
    BrokerCore.ProviderContext context = BrokerCore.ParseProviderContext(Ascii(ProviderContext()));
    Equal($"machine-utilities-e7-{new string('0', 64)}", context.StateIdentifier);
    Equal(SourceArgument(), context.SourceArgument);
    Equal(BrokerCore.Sha256Utf8(SourceArgument()), context.SourceArgumentSha256);
    Equal(1700000000L, context.SourceLastUpdateMinimumUnix);

    string rawArgument = ProviderContext().Replace(
        $"source-argument-sha256|{BrokerCore.Sha256Utf8(SourceArgument())}",
        $"source-argument-sha256|{new string('a', 64)}",
        StringComparison.Ordinal);
    Throws<FormatException>(() => BrokerCore.ParseProviderContext(Ascii(rawArgument)));
    Throws<FormatException>(() => BrokerCore.ParseProviderContext(Ascii(
        ProviderContext().Replace(SourceArgument(), "https://example.invalid/catalog?caller=1",
            StringComparison.Ordinal))));
}

static void CanonicalUriVectorRequiresRawAbsoluteUri()
{
    const string canonical = "https://example.invalid/catalog";
    const string explicitDefaultPort = "https://example.invalid:443/catalog";
    True(BrokerCore.IsSafeSourceArgument(canonical));
    False(BrokerCore.IsSafeSourceArgument(explicitDefaultPort));

    string nonCanonicalContext = ProviderContext()
        .Replace(SourceArgument(), explicitDefaultPort, StringComparison.Ordinal)
        .Replace(
            BrokerCore.Sha256Utf8(SourceArgument()),
            BrokerCore.Sha256Utf8(explicitDefaultPort),
            StringComparison.Ordinal);
    Throws<FormatException>(() => BrokerCore.ParseProviderContext(Ascii(nonCanonicalContext)));
}

static void ProviderLockIsExact()
{
    byte[] providerLockBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "windows-winget-provider.lock"));
    BrokerCore.ProviderLock providerLock = BrokerCore.ParseProviderLock(providerLockBytes);
    byte[] packageLockBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "packages.lock.json"));
    Equal("8.0.29", providerLock.DotNetRuntimeVersion);
    Equal("1.29.280", providerLock.ProviderVersion);
    Equal("IjFc4STI6HVYd7hG2O8LxdMbRsjJb0pbt8oSOV+Mcl+hoIRkuHKfopq6FHYdY5o1tbFprFSZaXtXzOeXnDaFjg==", providerLock.WindowsSdkPackageSha512);
    Equal("5LkAwRSxcbZpBiAdJjRE9w8q6Jh4voRckbtmbCpMW3jMDb0n2pwxlxANyXRcLEoMAxSTSCKcmjcX6waWrPAUuw==", providerLock.CsWinRtPackageSha512);
    Equal("uWSgGMlZEWCkdZ6kxXSI50fqFXaj8LWhFnOiEg0uKhpcSoEh2HCwPXasAiT32aZ3ZF8KGyOSlTtOal/+soZ7jg==", providerLock.ProviderPackageSha512);
    SequenceEqual(new[] { "win-arm64", "win-x64" }, providerLock.RuntimeIdentifiers);
    SequenceEqual(
        new[] { "MachineUtilities.WinGetBroker.exe", "Microsoft.Management.Deployment.dll", "WindowsPackageManager.dll" },
        providerLock.AuthenticodeNames);
    BrokerCore.ValidateProviderRuntime(providerLock, "8.0.29", "win-x64");
    BrokerCore.ValidatePackageLock(packageLockBytes, providerLock);
    BrokerCore.ValidateFrameworkReferencePackageHash(providerLock, providerLock.WindowsSdkPackageSha512);
    ThrowsReason<InvalidOperationException>(
        "deployment_identity_drift",
        () => BrokerCore.ValidateProviderRuntime(providerLock, "8.0.30", "win-x64"));
    ThrowsReason<InvalidOperationException>(
        "deployment_identity_drift",
        () => BrokerCore.ValidateProviderRuntime(providerLock, "8.0.29", "win-x86"));
    string providerLockText = Encoding.ASCII.GetString(providerLockBytes);
    Throws<FormatException>(() => BrokerCore.ParseProviderLock(Ascii(
        providerLockText.Replace("dotnet-runtime|8.0.29", "dotnet-runtime|8.0.30", StringComparison.Ordinal))));
    Throws<FormatException>(() => BrokerCore.ParseProviderLock(Ascii(
        providerLockText.Replace(
            providerLock.WindowsSdkPackageSha512,
            new string('a', providerLock.WindowsSdkPackageSha512.Length),
            StringComparison.Ordinal))));
    Throws<FormatException>(() => BrokerCore.ParseProviderLock(Ascii(
        providerLockText.Replace(
            providerLock.CsWinRtPackageSha512,
            new string('a', providerLock.CsWinRtPackageSha512.Length),
            StringComparison.Ordinal))));
    Throws<FormatException>(() => BrokerCore.ParseProviderLock(Ascii(
        providerLockText.Replace(
            providerLock.ProviderPackageSha512,
            new string('a', providerLock.ProviderPackageSha512.Length),
            StringComparison.Ordinal))));
    ThrowsReason<InvalidOperationException>(
        "provider_lock_drift",
        () => BrokerCore.ValidatePackageLock(
            Ascii(Encoding.UTF8.GetString(packageLockBytes).Replace(
                BrokerCore.CsWinRtNuGetContentHash,
                new string('a', BrokerCore.CsWinRtNuGetContentHash.Length),
                StringComparison.Ordinal)),
            providerLock));
    ThrowsReason<InvalidOperationException>(
        "provider_lock_drift",
        () => BrokerCore.ValidatePackageLock(
            Ascii(Encoding.UTF8.GetString(packageLockBytes).Replace(
                BrokerCore.ProviderNuGetContentHash,
                new string('a', BrokerCore.ProviderNuGetContentHash.Length),
                StringComparison.Ordinal)),
            providerLock));
    ThrowsReason<InvalidOperationException>(
        "provider_lock_drift",
        () => BrokerCore.ValidateFrameworkReferencePackageHash(
            providerLock,
            new string('a', providerLock.WindowsSdkPackageSha512.Length)));
}

static void PolicyConstraintDigestAndTokenAreBound()
{
    BrokerCore.PolicyDocument defaultPolicy = BrokerCore.ParsePolicy(
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "privilege-policy.default")));
    True(defaultPolicy.Actions[BrokerCore.InventoryAction].Enabled);
    False(defaultPolicy.Actions[BrokerCore.InstallAction].Enabled);
    False(defaultPolicy.Actions[BrokerCore.UpgradeAction].Enabled);

    (string policyText, string constraintsText) = PolicyAndConstraints(enabled: true);
    string policySha256 = BrokerCore.Sha256(Ascii(policyText));
    BrokerCore.PolicyDocument policy = BrokerCore.ParsePolicy(Ascii(policyText));
    BrokerCore.ConstraintsDocument constraints = BrokerCore.ParseConstraints(
        Ascii(constraintsText),
        policy,
        policySha256);

    BrokerCore.HelperRequest request = BrokerCore.ParseRequest(
        Ascii(Request(BrokerCore.InstallAction, "allow-7", policySha256, BrokerCore.Sha256(Ascii(constraintsText)))),
        BrokerCore.BrokerMode.Install);
    BrokerCore.ProviderContext context = BrokerCore.ParseProviderContext(Ascii(ProviderContext()));
    BrokerCore.WinGetConstraint selected = BrokerCore.SelectConstraint(
        BrokerCore.BrokerMode.Install,
        request,
        policy,
        constraints,
        context);

    Equal("Contoso.Tool", selected.PackageId);
    SequenceEqual(new[] { "msi", "zip" }, selected.InstallerTypes);
    Throws<InvalidOperationException>(() => BrokerCore.SelectConstraint(
        BrokerCore.BrokerMode.Upgrade,
        request with { ActionId = BrokerCore.UpgradeAction },
        policy,
        constraints,
        context));

    string drifted = constraintsText.Replace("Contoso.Tool", "Contoso.Other", StringComparison.Ordinal);
    Throws<FormatException>(() => BrokerCore.ParseConstraints(Ascii(drifted), policy, policySha256));
    string widened = constraintsText.Replace(
        "|source-delegated-all\n",
        "|source-delegated-all|skip-dependencies\n",
        StringComparison.Ordinal);
    Throws<FormatException>(() => BrokerCore.ParseConstraints(Ascii(widened), policy, policySha256));

    (string disabledPolicyText, string disabledConstraintsText) = PolicyAndConstraints(enabled: false);
    string disabledPolicySha256 = BrokerCore.Sha256(Ascii(disabledPolicyText));
    BrokerCore.PolicyDocument disabledPolicy = BrokerCore.ParsePolicy(Ascii(disabledPolicyText));
    BrokerCore.ConstraintsDocument disabledConstraints = BrokerCore.ParseConstraints(
        Ascii(disabledConstraintsText), disabledPolicy, disabledPolicySha256);
    Throws<InvalidOperationException>(() => BrokerCore.SelectConstraint(
        BrokerCore.BrokerMode.Install,
        request with { PolicySha256 = disabledPolicySha256 },
        disabledPolicy,
        disabledConstraints,
        context));
}

static void ConstraintSourceBindingsAreNonfungible()
{
    (string policyText, string constraintsText) = PolicyAndConstraints(enabled: true);
    string policySha256 = BrokerCore.Sha256(Ascii(policyText));
    BrokerCore.PolicyDocument policy = BrokerCore.ParsePolicy(Ascii(policyText));
    BrokerCore.ConstraintsDocument constraints = BrokerCore.ParseConstraints(
        Ascii(constraintsText), policy, policySha256);
    BrokerCore.ProviderContext context = BrokerCore.ParseProviderContext(Ascii(ProviderContext()));
    BrokerCore.HelperRequest request = BrokerCore.ParseRequest(
        Ascii(Request(BrokerCore.InstallAction, "allow-7", policySha256, BrokerCore.Sha256(Ascii(constraintsText)))),
        BrokerCore.BrokerMode.Install);

    Equal("Contoso.Tool", BrokerCore.SelectConstraint(
        BrokerCore.BrokerMode.Install, request, policy, constraints, context).PackageId);
    Equal(1, BrokerCore.InventoryConstraints(constraints, context).Count);

    BrokerCore.ProviderContext[] mismatchedContexts =
    [
        context with { SourceName = "other-catalog" },
        context with { SourceType = "Microsoft.Rest" },
        context with { SourceArgumentSha256 = new string('a', 64) },
    ];
    foreach (BrokerCore.ProviderContext mismatchedContext in mismatchedContexts)
    {
        ThrowsReason<InvalidOperationException>(
            "source_drift",
            () => BrokerCore.SelectConstraint(
                BrokerCore.BrokerMode.Install, request, policy, constraints, mismatchedContext));
        ThrowsReason<InvalidOperationException>(
            "source_drift",
            () => BrokerCore.InventoryConstraints(constraints, mismatchedContext));
    }
}

static void SettingsKeepDependencyAndDangerousControlsFixed()
{
    (string policyText, string constraintsText) = PolicyAndConstraints(enabled: true);
    BrokerCore.PolicyDocument policy = BrokerCore.ParsePolicy(Ascii(policyText));
    BrokerCore.ConstraintsDocument constraints = BrokerCore.ParseConstraints(
        Ascii(constraintsText),
        policy,
        BrokerCore.Sha256(Ascii(policyText)));
    BrokerCore.WinGetConstraint constraint = constraints.WinGetConstraints.Single();

    string settings = BrokerCore.BuildUserSettings(constraint);
    using JsonDocument document = JsonDocument.Parse(settings);
    JsonElement installBehavior = document.RootElement.GetProperty("installBehavior");
    False(installBehavior.GetProperty("skipDependencies").GetBoolean());
    Equal("machine", installBehavior.GetProperty("requirements").GetProperty("scope").GetString());
    Equal("x64", installBehavior.GetProperty("requirements").GetProperty("architectures")[0].GetString());
    Equal("en-US", installBehavior.GetProperty("requirements").GetProperty("locale")[0].GetString());
    False(document.RootElement.GetProperty("experimentalFeatures").GetProperty("resume").GetBoolean());
    False(settings.Contains("arguments", StringComparison.OrdinalIgnoreCase));
    False(settings.Contains("proxy", StringComparison.OrdinalIgnoreCase));
    False(settings.Contains("hashMismatch", StringComparison.OrdinalIgnoreCase));
}

static void PersistedSettingsAndLockedRestoreHashesFailClosed()
{
    BrokerCore.ValidatePersistedSettingsLayout(Array.Empty<string>());
    BrokerCore.ValidatePersistedSettingsLayout(new[] { "settings.json", "settings.json.backup" });
    BrokerCore.ValidatePersistedSettingsJson(Ascii("{}"));
    BrokerCore.ValidatePersistedSettingsJson(Ascii("{\"installBehavior\":{\"skipDependencies\":false}}"));

    ThrowsReason<InvalidOperationException>(
        "runtime_state_drift",
        () => BrokerCore.ValidatePersistedSettingsLayout(new[] { "settings.json", "state.db" }));
    ThrowsReason<InvalidOperationException>(
        "runtime_state_drift",
        () => BrokerCore.ValidatePersistedSettingsLayout(new[] { "settings.json", "settings.json" }));
    ThrowsReason<InvalidOperationException>(
        "runtime_state_drift",
        () => BrokerCore.ValidatePersistedSettingsJson(Ascii("{\"installBehavior\":{\"skipDependencies\":true}}")));
    ThrowsReason<InvalidOperationException>(
        "runtime_state_drift",
        () => BrokerCore.ValidatePersistedSettingsJson(Ascii("{\"installBehavior\":{\"skipDependencies\":false,\"skipDependencies\":false}}")));
    ThrowsReason<InvalidOperationException>(
        "runtime_state_drift",
        () => BrokerCore.ValidatePersistedSettingsJson(Ascii("{\"installBehavior\":\"caller-controlled\"}")));
    ThrowsReason<InvalidOperationException>(
        "runtime_state_drift",
        () => BrokerCore.ValidatePersistedSettingsJson(Ascii("{\"installBehavior\":{\"skipDependencies\":false}")));
}

static void SourceInventoryAndExactSearchFailClosed()
{
    BrokerCore.ProviderContext context = BrokerCore.ParseProviderContext(Ascii(ProviderContext()));
    var evidence = new BrokerCore.SourceEvidence(
        context.SourceId,
        context.SourceName,
        context.SourceType,
        context.SourceArgumentSha256,
        context.SourceOrigin,
        context.SourceTrust,
        context.SourceExplicit,
        context.SourceLastUpdateMinimumUnix + 1);
    var source = new BrokerCore.SourceCatalogObservation(evidence, false, 0, "none");
    Equal(evidence, BrokerCore.ValidateSourceInventory(context, new[] { source }, source));

    var storeEvidence = evidence with
    {
        Id = "StoreEdgeFD",
        Name = "msstore",
        Type = "Microsoft.Rest",
        ArgumentSha256 = new string('b', 64),
    };
    var store = new BrokerCore.SourceCatalogObservation(storeEvidence, false, 0, "none");
    BrokerCore.SourceProvisioningPlan provision = BrokerCore.PlanSourceProvisioning(
        context, new[] { store, source });
    SequenceEqual(new[] { "msstore" }, provision.CatalogNamesToRemove);
    False(provision.AddExpectedCatalog);
    True(BrokerCore.PlanSourceProvisioning(
        context, Array.Empty<BrokerCore.SourceCatalogObservation>()).AddExpectedCatalog);
    ThrowsReason<InvalidOperationException>(
        "source_provision_failed",
        () => BrokerCore.PlanSourceProvisioning(context, new[]
        {
            store,
            store with { Evidence = storeEvidence with { Name = "MSSTORE" } },
        }));

    ThrowsReason<InvalidOperationException>(
        "source_drift",
        () => BrokerCore.ValidateSourceInventory(context, Array.Empty<BrokerCore.SourceCatalogObservation>(), source));
    ThrowsReason<InvalidOperationException>(
        "source_drift",
        () => BrokerCore.ValidateSourceInventory(context, new[] { source, source }, source));
    ThrowsReason<InvalidOperationException>(
        "source_drift",
        () => BrokerCore.ValidateSourceInventory(context, new[] { source with { IsComposite = true } }, source));
    ThrowsReason<InvalidOperationException>(
        "source_agreement_required",
        () => BrokerCore.ValidateSourceInventory(context, new[] { source with { AgreementCount = 1 } }, source with { AgreementCount = 1 }));
    ThrowsReason<InvalidOperationException>(
        "authentication_required",
        () => BrokerCore.ValidateSourceInventory(context, new[] { source with { AuthenticationType = "entra" } }, source with { AuthenticationType = "entra" }));

    string roots = BrokerCore.ComputeProviderRuntimeRootsSha256(new[]
    {
        new BrokerCore.ProviderRuntimeRootObservation("temp-cache", new string('1', 64), new string('2', 64)),
        new BrokerCore.ProviderRuntimeRootObservation("state-cache", new string('3', 64), new string('4', 64)),
        new BrokerCore.ProviderRuntimeRootObservation("secure-settings", new string('5', 64), new string('6', 64)),
        new BrokerCore.ProviderRuntimeRootObservation("settings", new string('7', 64), new string('8', 64)),
    });
    Equal(64, roots.Length);
    ThrowsReason<InvalidOperationException>(
        "runtime_state_drift",
        () => BrokerCore.ComputeProviderRuntimeRootsSha256(new[]
        {
            new BrokerCore.ProviderRuntimeRootObservation("state-cache", new string('3', 64), new string('4', 64)),
        }));

    BrokerCore.ValidateExactPackageMatch("Contoso.Tool", new[] { "Contoso.Tool" }, false);
    ThrowsReason<InvalidOperationException>(
        "package_not_found",
        () => BrokerCore.ValidateExactPackageMatch("Contoso.Tool", Array.Empty<string>(), false));
    ThrowsReason<InvalidOperationException>(
        "ambiguous_package",
        () => BrokerCore.ValidateExactPackageMatch("Contoso.Tool", new[] { "Contoso.Tool", "Contoso.Tool" }, true));
    ThrowsReason<InvalidOperationException>(
        "ambiguous_package",
        () => BrokerCore.ValidateExactPackageMatch("Contoso.Tool", new[] { "Contoso.Other" }, false));
}

static void SourcePreconditionIgnoresRefreshTimestamp()
{
    BrokerCore.ProviderContext context = BrokerCore.ParseProviderContext(Ascii(ProviderContext()));
    var source = new BrokerCore.SourceEvidence(
        context.SourceId,
        context.SourceName,
        context.SourceType,
        context.SourceArgumentSha256,
        context.SourceOrigin,
        context.SourceTrust,
        context.SourceExplicit,
        context.SourceLastUpdateMinimumUnix + 1);

    string precondition = BrokerCore.ComputeSourceStateSha256(source);
    Equal(precondition, BrokerCore.ComputeSourceStateSha256(source with
    {
        LastUpdateUnix = source.LastUpdateUnix + 1,
    }));
    False(precondition == BrokerCore.ComputeSourceStateSha256(source with
    {
        ArgumentSha256 = new string('f', 64),
    }));
}

static void ProviderRuntimeRootsAreCreatedOnlyDuringProvisioning()
{
    int calls = 0;
    bool created = BrokerCore.AttemptProviderRuntimeDirectoryCreation(
        provisioning: true,
        () =>
        {
            calls++;
            return true;
        });
    True(created);
    Equal(1, calls);

    bool routineCreated = BrokerCore.AttemptProviderRuntimeDirectoryCreation(
        provisioning: false,
        () => throw new InvalidOperationException("routine mode attempted creation"));
    False(routineCreated);
    Equal(1, calls);
}

static void OperationPackageShapeAllowsOnlyFixedUpgradeAssociation()
{
    BrokerCore.ValidateOperationPackageShape(
        BrokerCore.BrokerMode.Install,
        new BrokerCore.OperationPackageShape(false, 1, false, "none", true));
    BrokerCore.ValidateOperationPackageShape(
        BrokerCore.BrokerMode.Upgrade,
        new BrokerCore.OperationPackageShape(true, 1, true, "all-catalogs-fixed", true));

    ThrowsReason<InvalidOperationException>(
        "package_object_shape_drift",
        () => BrokerCore.ValidateOperationPackageShape(
            BrokerCore.BrokerMode.Install,
            new BrokerCore.OperationPackageShape(true, 1, false, "all-catalogs-fixed", true)));
    ThrowsReason<InvalidOperationException>(
        "package_object_shape_drift",
        () => BrokerCore.ValidateOperationPackageShape(
            BrokerCore.BrokerMode.Upgrade,
            new BrokerCore.OperationPackageShape(false, 1, false, "none", true)));
    ThrowsReason<InvalidOperationException>(
        "package_object_shape_drift",
        () => BrokerCore.ValidateOperationPackageShape(
            BrokerCore.BrokerMode.Upgrade,
            new BrokerCore.OperationPackageShape(true, 2, true, "all-catalogs-fixed", true)));
    ThrowsReason<InvalidOperationException>(
        "package_object_shape_drift",
        () => BrokerCore.ValidateOperationPackageShape(
            BrokerCore.BrokerMode.Upgrade,
            new BrokerCore.OperationPackageShape(true, 1, true, "caller-selected", true)));
    ThrowsReason<InvalidOperationException>(
        "package_object_shape_drift",
        () => BrokerCore.ValidateOperationPackageShape(
            BrokerCore.BrokerMode.Upgrade,
            new BrokerCore.OperationPackageShape(true, 1, true, "all-catalogs-fixed", false)));
}

static void VersionSelectionIsUnorderedBoundedAndUnambiguous()
{
    BrokerCore.WinGetConstraint upgrade = UpgradeConstraint();
    var candidates = new[]
    {
        new BrokerCore.VersionCandidateObservation(
            7, "2.5.0", "catalog-id", "", BrokerCore.ProviderComparison.Greater,
            BrokerCore.ProviderComparison.Lesser, BrokerCore.ProviderComparison.Greater),
        new BrokerCore.VersionCandidateObservation(
            3, "2.7.0", "catalog-id", "", BrokerCore.ProviderComparison.Greater,
            BrokerCore.ProviderComparison.Lesser, BrokerCore.ProviderComparison.Greater),
        new BrokerCore.VersionCandidateObservation(
            9, "3.0.0", "catalog-id", "", BrokerCore.ProviderComparison.Greater,
            BrokerCore.ProviderComparison.Greater, BrokerCore.ProviderComparison.Greater),
        new BrokerCore.VersionCandidateObservation(
            4, "2.8.0", "catalog-id", "preview", BrokerCore.ProviderComparison.Greater,
            BrokerCore.ProviderComparison.Lesser, BrokerCore.ProviderComparison.Greater),
    };
    var versions = candidates.ToDictionary(candidate => candidate.Index, candidate => Version.Parse(candidate.Version));
    Equal(
        3,
        BrokerCore.SelectVersionCandidate(
            upgrade,
            "catalog-id",
            candidates,
            (left, right) => CompareVersions(versions[left], versions[right])));

    ThrowsReason<InvalidOperationException>(
        "ambiguous_version",
        () => BrokerCore.SelectVersionCandidate(
            upgrade,
            "catalog-id",
            candidates.Take(2).ToArray(),
            (_, _) => BrokerCore.ProviderComparison.Equal));
    ThrowsReason<InvalidOperationException>(
        "source_drift",
        () => BrokerCore.SelectVersionCandidate(
            upgrade,
            "catalog-id",
            new[] { candidates[0] with { SourceId = "other-catalog" } },
            (_, _) => BrokerCore.ProviderComparison.Unknown));

    BrokerCore.WinGetConstraint install = InstallConstraint();
    var exact = new BrokerCore.VersionCandidateObservation(
        0, "1.2.3", "catalog-id", "", BrokerCore.ProviderComparison.Equal,
        BrokerCore.ProviderComparison.Equal, BrokerCore.ProviderComparison.Unknown);
    Equal(0, BrokerCore.SelectVersionCandidate(install, "catalog-id", new[] { exact }, (_, _) => BrokerCore.ProviderComparison.Unknown));
    ThrowsReason<InvalidOperationException>(
        "ambiguous_version",
        () => BrokerCore.SelectVersionCandidate(
            install,
            "catalog-id",
            new[] { exact, exact with { Index = 1 } },
            (_, _) => BrokerCore.ProviderComparison.Equal));
}

static void InstallerAndMutationOptionsAreFixed()
{
    BrokerCore.WinGetConstraint constraint = InstallConstraint();
    var installer = new BrokerCore.InstallerObservation("system", "x64", "en-US", "msi", "-", "none", 0);
    Equal(installer, BrokerCore.ValidateInstallerObservation(constraint, installer));
    Equal(
        "msi",
        BrokerCore.ValidateInstallerObservation(
            constraint,
            new BrokerCore.InstallerObservation("system", "x64", "en-US", "zip", "msi", "none", 0)).NestedInstallerType);
    ThrowsReason<InvalidOperationException>(
        "inapplicable_installer",
        () => BrokerCore.ValidateInstallerObservation(constraint, installer with { Scope = "user" }));
    ThrowsReason<InvalidOperationException>(
        "unsupported_installer_type",
        () => BrokerCore.ValidateInstallerObservation(constraint, installer with { NestedInstallerType = "msi" }));
    ThrowsReason<InvalidOperationException>(
        "authentication_required",
        () => BrokerCore.ValidateInstallerObservation(constraint, installer with { AuthenticationType = "entra" }));
    ThrowsReason<InvalidOperationException>(
        "package_agreement_required",
        () => BrokerCore.ValidateInstallerObservation(constraint, installer with { AgreementCount = 1 }));

    BrokerCore.DangerousOptionsObservation options = SafeOptions();
    BrokerCore.ValidateDangerousOptions(constraint, "catalog-id", "1.2.3", options);
    ThrowsReason<InvalidOperationException>(
        "dangerous_option_drift",
        () => BrokerCore.ValidateDangerousOptions(constraint, "catalog-id", "1.2.3", options with { SkipDependencies = true }));
    ThrowsReason<InvalidOperationException>(
        "dangerous_option_drift",
        () => BrokerCore.ValidateDangerousOptions(constraint, "catalog-id", "1.2.3", options with { AllowHashMismatch = true }));
    ThrowsReason<InvalidOperationException>(
        "dangerous_option_drift",
        () => BrokerCore.ValidateDangerousOptions(constraint, "catalog-id", "1.2.3", options with { AdditionalInstallerArguments = "/quiet" }));
    ThrowsReason<InvalidOperationException>(
        "dangerous_option_drift",
        () => BrokerCore.ValidateDangerousOptions(constraint, "catalog-id", "1.2.3", options with { PackageCatalogId = "other" }));
}

static void ProviderMutationInvocationEnforcesRealOrdering()
{
    BrokerCore.ProviderContext context = BrokerCore.ParseProviderContext(Ascii(ProviderContext()));
    BrokerCore.WinGetConstraint constraint = InstallConstraint();
    var tooEarly = new BrokerCore.ProviderMutationInvocation(
        BrokerCore.BrokerMode.Install, context, constraint);
    ThrowsReason<InvalidOperationException>("package_state_drift", tooEarly.RecordManagerActivated);

    var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    var fake = new FakeProviderMutationBackend(
        new BrokerCore.ProviderMutationInvocation(BrokerCore.BrokerMode.Install, context, constraint),
        completion);
    var source = new BrokerCore.SourceEvidence(
        context.SourceId,
        context.SourceName,
        context.SourceType,
        context.SourceArgumentSha256,
        context.SourceOrigin,
        context.SourceTrust,
        context.SourceExplicit,
        context.SourceLastUpdateMinimumUnix + 1);
    var evidence = new BrokerCore.PackageEvidence(
        0,
        constraint.PolicyToken,
        constraint.PackageId,
        "-",
        constraint.MinimumVersion,
        constraint.Architecture,
        constraint.Locale,
        "msi",
        "-",
        "-");
    var resolution = new BrokerCore.MutationResolutionObservation(
        evidence,
        context.SourceId,
        SafeOptions());

    fake.ApplySettings();
    fake.ActivateManager();
    ThrowsReason<InvalidOperationException>("source_drift", () => fake.OpenSingleSource(source, 2));
    fake.OpenSingleSource(source, 1);
    ThrowsReason<InvalidOperationException>(
        "package_state_drift",
        () => fake.ResolveInitial(resolution with { Evidence = evidence with { PackageId = "Contoso.Other" } }));
    ThrowsReason<InvalidOperationException>(
        "dangerous_option_drift",
        () => fake.ResolveInitial(resolution with { Options = SafeOptions() with { SkipDependencies = true } }));
    fake.ResolveInitial(resolution);
    fake.RevalidateSingleSource(source with { LastUpdateUnix = source.LastUpdateUnix + 1 }, 1);
    fake.ResolveRevalidated(resolution);

    Task<int> terminal = fake.InvokeAsync();
    False(terminal.IsCompleted);
    True(fake.Invocation.InvocationStarted);
    completion.SetResult(17);
    Equal(17, terminal.GetAwaiter().GetResult());
    True(fake.Invocation.TerminalObserved);
    SequenceEqual(
        new[] { "settings", "manager", "source", "initial-resolution", "revalidated-source", "revalidated-resolution", "invoke", "terminal" },
        fake.Events);
}

static void DeploymentEvidenceIsCompleteAndObserved()
{
    string exe = new string('1', 64);
    string deployment = new string('2', 64);
    string manifest = new string('3', 64);
    string winmd = new string('4', 64);
    string packageManager = new string('5', 64);
    BrokerCore.DeploymentFileObservation[] files =
    {
        new("WindowsPackageManager.dll", 50, packageManager),
        new("MachineUtilities.WinGetBroker.exe", 10, exe),
        new("Microsoft.Management.Deployment.winmd", 40, winmd),
        new("Microsoft.Management.Deployment.dll", 20, deployment),
        new("Microsoft.Management.Deployment.dll.manifest", 30, manifest),
    };
    string[] payloads =
    {
        "Microsoft.Management.Deployment.dll",
        "Microsoft.Management.Deployment.dll.manifest",
        "Microsoft.Management.Deployment.winmd",
        "WindowsPackageManager.dll",
    };
    string[] signedNames =
    {
        "MachineUtilities.WinGetBroker.exe",
        "Microsoft.Management.Deployment.dll",
        "WindowsPackageManager.dll",
    };
    BrokerCore.AuthenticodeFileObservation[] signed =
    {
        new("WindowsPackageManager.dll", packageManager, new string('c', 64)),
        new("MachineUtilities.WinGetBroker.exe", exe, new string('a', 64)),
        new("Microsoft.Management.Deployment.dll", deployment, new string('b', 64)),
    };

    BrokerCore.DeploymentEvidence first = BrokerCore.ComputeDeploymentEvidence(files, payloads, signedNames, signed);
    BrokerCore.DeploymentEvidence second = BrokerCore.ComputeDeploymentEvidence(
        Enumerable.Reverse(files).ToArray(), payloads, signedNames, Enumerable.Reverse(signed).ToArray());
    Equal(first.FileSetSha256, second.FileSetSha256);
    Equal(first.AuthenticodeIdentitySha256, second.AuthenticodeIdentitySha256);
    string canonicalFileSet = "winget-deployment-file-set|1\n" +
        string.Concat(files.OrderBy(file => file.Name, StringComparer.Ordinal).Select(file =>
            $"file|{file.Name}|{file.Length}|{file.Sha256}\n")) +
        "end-file-set|\n";
    Equal(BrokerCore.Sha256Ascii(canonicalFileSet), first.FileSetSha256);
    string canonicalAuthenticode = "winget-authenticode-identity|1\n" +
        string.Concat(signed.OrderBy(file => file.Name, StringComparer.Ordinal).Select(file =>
            $"file|{file.Name}|{file.FileSha256}|{file.SignerCertificateSha256}\n")) +
        "end-identity|\n";
    Equal(BrokerCore.Sha256Ascii(canonicalAuthenticode), first.AuthenticodeIdentitySha256);

    ThrowsReason<InvalidOperationException>(
        "deployment_identity_drift",
        () => BrokerCore.ComputeDeploymentEvidence(files.Where(file => file.Name != "Microsoft.Management.Deployment.winmd").ToArray(), payloads, signedNames, signed));
    ThrowsReason<InvalidOperationException>(
        "deployment_identity_drift",
        () => BrokerCore.ComputeDeploymentEvidence(files, payloads, signedNames, signed.Take(2).ToArray()));
    ThrowsReason<InvalidOperationException>(
        "deployment_identity_drift",
        () => BrokerCore.ComputeDeploymentEvidence(
            files.Append(new BrokerCore.DeploymentFileObservation(
                "windowsPackageManager.dll", 50, packageManager)).ToArray(),
            payloads,
            signedNames,
            signed));
    ThrowsReason<InvalidOperationException>(
        "deployment_identity_drift",
        () => BrokerCore.ComputeDeploymentEvidence(
            files,
            payloads,
            signedNames,
            signed.Select(item => item.Name == "WindowsPackageManager.dll" ? item with { FileSha256 = new string('f', 64) } : item).ToArray()));

    BrokerCore.DeploymentEvidence changed = BrokerCore.ComputeDeploymentEvidence(
        files.Append(new BrokerCore.DeploymentFileObservation("MachineUtilities.WinGetBroker.deps.json", 6, new string('6', 64))).ToArray(),
        payloads,
        signedNames,
        signed);
    False(first.FileSetSha256 == changed.FileSetSha256);
}

static void TerminalMappingIsConservativeAfterLaunch()
{
    Equal(
        (BrokerCore.TerminalState.Rejected, "source_drift"),
        BrokerCore.ClassifyMutation(false, false, false, false, "source_drift"));
    Equal(
        (BrokerCore.TerminalState.Partial, "provider_failure"),
        BrokerCore.ClassifyMutation(true, false, false, false, "provider_failure"));
    Equal(
        (BrokerCore.TerminalState.Partial, "reboot_required"),
        BrokerCore.ClassifyMutation(true, true, true, true, "ignored"));
    Equal(
        (BrokerCore.TerminalState.Partial, "post_state_unverified"),
        BrokerCore.ClassifyMutation(true, true, false, false, "ignored"));
    Equal(
        (BrokerCore.TerminalState.Completed, "post_state_verified"),
        BrokerCore.ClassifyMutation(true, true, false, true, "ignored"));
}

static void ResultIsDeterministicBoundedAndRedacted()
{
    var source = new BrokerCore.SourceEvidence(
        "catalog-id",
        "catalog-name",
        "Microsoft.PreIndexed.Package",
        new string('a', 64),
        "predefined",
        "trusted",
        true,
        1700000000);
    var package = new BrokerCore.PackageEvidence(
        0,
        "allow-7",
        "Contoso.Tool",
        "-",
        "1.2.3",
        "x64",
        "en-US",
        "msi",
        "-",
        "1.2.3");
    var result = new BrokerCore.HelperResult(
        "request-0123456789abcdef0123456789abcdef",
        BrokerCore.InstallAction,
        BrokerCore.TerminalState.Completed,
        "post_state_verified",
        new string('b', 64),
        new string('c', 64),
        new string('a', 64),
        "1.29.280",
        new string('d', 64),
        new string('9', 64),
        new string('e', 64),
        source,
        new[] { package },
        "Ok",
        "0x00000000",
        "0",
        false,
        new string('f', 64),
        new string('0', 64));

    string first = BrokerCore.RenderResult(result);
    string second = BrokerCore.RenderResult(result);
    Equal(first, second);
    True(first.EndsWith("end-result|\n", StringComparison.Ordinal));
    True(Encoding.ASCII.GetByteCount(first) <= BrokerCore.MaxResultBytes);
    True(first.Contains($"app-installer-identity-sha256|{new string('a', 64)}", StringComparison.Ordinal));
    True(first.Contains($"provider-runtime-roots-sha256|{new string('9', 64)}", StringComparison.Ordinal));
    True(first.Contains("dependency-authority|source-delegated-all", StringComparison.Ordinal));
    Equal(1, first.Split('\n').Count(line => line.StartsWith("pre-state-sha256|", StringComparison.Ordinal)));
    False(first.Contains("https://", StringComparison.Ordinal));
    False(first.Contains("--", StringComparison.Ordinal));
    False(first.Contains("dependency-package", StringComparison.Ordinal));
    Equal("provider_failure", BrokerCore.NormalizeReason("https://secret.invalid bearer-token"));

    string provision = BrokerCore.RenderProvisionResult(new BrokerCore.ProviderProvisionResult(
        BrokerCore.TerminalState.Completed,
        "provider_state_provisioned",
        7,
        new string('0', 64),
        new string('1', 64),
        new string('2', 64),
        new string('3', 64),
        "1.29.280",
        new string('4', 64),
        new string('5', 64),
        new string('6', 64),
        source));
    True(provision.EndsWith("end-provision-result|\n", StringComparison.Ordinal));
    Equal(14, provision.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
}

static BrokerCore.WinGetConstraint InstallConstraint() => new(
    BrokerCore.BrokerMode.Install,
    BrokerCore.InstallAction,
    "allow-7",
    "Contoso.Tool",
    "catalog-name",
    "Microsoft.PreIndexed.Package",
    BrokerCore.Sha256Utf8(SourceArgument()),
    "x64",
    "en-US",
    new[] { "msi", "zip" },
    "1.2.3",
    "1.2.3",
    null);

static BrokerCore.WinGetConstraint UpgradeConstraint() => new(
    BrokerCore.BrokerMode.Upgrade,
    BrokerCore.UpgradeAction,
    "upgrade-7",
    "Contoso.Tool",
    "catalog-name",
    "Microsoft.PreIndexed.Package",
    BrokerCore.Sha256Utf8(SourceArgument()),
    "x64",
    "en-US",
    new[] { "msi", "zip" },
    "2.0.0",
    "2.9.9",
    2);

static BrokerCore.DangerousOptionsObservation SafeOptions() => new(
    "catalog-id",
    "1.2.3",
    "",
    "system",
    "silent",
    false,
    false,
    false,
    false,
    false,
    false,
    false,
    "",
    "",
    "",
    "",
    "",
    "",
    new[] { "x64" },
    "msi");

static BrokerCore.ProviderComparison CompareVersions(Version left, Version right)
{
    int comparison = left.CompareTo(right);
    return comparison < 0
        ? BrokerCore.ProviderComparison.Lesser
        : comparison > 0
            ? BrokerCore.ProviderComparison.Greater
            : BrokerCore.ProviderComparison.Equal;
}

static (string Policy, string Constraints) PolicyAndConstraints(bool enabled)
{
    string record = $"winget-install|{BrokerCore.InstallAction}|allow-7|Contoso.Tool|catalog-name|Microsoft.PreIndexed.Package|{BrokerCore.Sha256Utf8(SourceArgument())}|machine|x64|en-US|msi,zip|1.2.3|provider-enforced-manifest-hash|source-delegated-all";
    string recordDigest = BrokerCore.Sha256Ascii(record + "\n");
    string policy = string.Join('\n', new[]
    {
        "policy|1|catalog=1",
        "action|apt.autoremove.v1|posix-root-v1|disabled|none|-",
        "action|apt.install-package-version.v1|posix-root-v1|disabled|package-source-version-closure-set-sha256|-",
        "action|apt.update-metadata.v1|posix-root-v1|enabled|none|-",
        "action|apt.upgrade-package.v1|posix-root-v1|disabled|package-source-channel-set-sha256|-",
        "action|macos.apply-system-setting.v1|macos-root-v1|disabled|macos-system-setting-sha256|-",
        "action|macos.install-signed-pkg.v1|macos-root-v1|disabled|macos-signed-pkg-sha256|-",
        "action|profile.apply-managed-bundle.v1|windows-user-s4u-v1|disabled|profile-bundle-set-sha256|-",
        "action|profile.inventory-managed-state.v1|windows-user-s4u-v1|disabled|profile-bundle-set-sha256|-",
        $"action|{BrokerCore.InstallAction}|windows-system-v1|{(enabled ? "enabled" : "disabled")}|winget-package-version-set-sha256|{recordDigest}",
        $"action|{BrokerCore.InventoryAction}|windows-system-v1|enabled|none|-",
        $"action|{BrokerCore.UpgradeAction}|windows-system-v1|disabled|winget-package-channel-set-sha256|-",
    }) + "\n";
    string constraints = $"constraints|1|generation=7|policy-sha256={BrokerCore.Sha256(Ascii(policy))}\n{record}\n";
    return (policy, constraints);
}

static string Request(
    string action,
    string token,
    string? policySha256 = null,
    string? constraintsSha256 = null,
    string? preconditionSha256 = null) =>
    string.Join('\n', new[]
    {
        "winget-helper-request|1",
        "request-id|request-0123456789abcdef0123456789abcdef",
        $"action-id|{action}",
        $"policy-token|{token}",
        "enrollment-epoch|7",
        $"policy-sha256|{policySha256 ?? new string('1', 64)}",
        $"constraints-sha256|{constraintsSha256 ?? new string('2', 64)}",
        $"winget-context-sha256|{new string('3', 64)}",
        $"provider-lock-sha256|{new string('4', 64)}",
        $"precondition-sha256|{preconditionSha256 ?? new string('5', 64)}",
        "end-request|",
    }) + "\n";

static string ProviderContext() =>
    string.Join('\n', new[]
    {
        "winget-provider-context|1",
        $"state-identifier|machine-utilities-e7-{new string('0', 64)}",
        "source-id|catalog-id",
        "source-name|catalog-name",
        "source-type|Microsoft.PreIndexed.Package",
        $"source-argument|{SourceArgument()}",
        $"source-argument-sha256|{BrokerCore.Sha256Utf8(SourceArgument())}",
        "source-origin|predefined",
        "source-trust|trusted",
        "source-explicit|true",
        "source-last-update-min-unix|1700000000",
        $"deployment-file-set-sha256|{new string('b', 64)}",
        $"app-installer-identity-sha256|{new string('c', 64)}",
        "end-context|",
    }) + "\n";

static string SourceArgument() => "https://example.invalid/catalog";

static string ProvisionRequest() =>
    string.Join('\n', new[]
    {
        "winget-provider-provision-request|1",
        "enrollment-epoch|7",
        $"generation-sha256|{new string('0', 64)}",
        $"policy-sha256|{new string('1', 64)}",
        $"constraints-sha256|{new string('2', 64)}",
        $"winget-context-sha256|{new string('3', 64)}",
        $"provider-lock-sha256|{new string('4', 64)}",
        "end-provision-request|",
    }) + "\n";

static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

static void Throws<T>(Action action)
    where T : Exception
{
    try
    {
        action();
    }
    catch (T)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

static void ThrowsReason<T>(string expectedReason, Action action)
    where T : Exception
{
    try
    {
        action();
    }
    catch (T exception)
    {
        Equal(expectedReason, exception.Message);
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(T).Name} with reason '{expectedReason}'.");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected '{expected}', observed '{actual}'.");
    }
}

static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
{
    if (!expected.SequenceEqual(actual))
    {
        throw new InvalidOperationException("Sequences differ.");
    }
}

static void True(bool value)
{
    if (!value)
    {
        throw new InvalidOperationException("Expected true.");
    }
}

static void False(bool value) => True(!value);

sealed class FakeProviderMutationBackend
{
    private readonly TaskCompletionSource<int> _terminal;

    internal FakeProviderMutationBackend(
        BrokerCore.ProviderMutationInvocation invocation,
        TaskCompletionSource<int> terminal)
    {
        Invocation = invocation;
        _terminal = terminal;
    }

    internal BrokerCore.ProviderMutationInvocation Invocation { get; }
    internal List<string> Events { get; } = new();

    internal void ApplySettings()
    {
        Invocation.RecordSettingsApplied();
        Events.Add("settings");
    }

    internal void ActivateManager()
    {
        Invocation.RecordManagerActivated();
        Events.Add("manager");
    }

    internal void OpenSingleSource(BrokerCore.SourceEvidence source, int count)
    {
        Invocation.RecordInitialSource(source, count);
        Events.Add("source");
    }

    internal void ResolveInitial(BrokerCore.MutationResolutionObservation resolution)
    {
        Invocation.RecordInitialResolution(resolution);
        Events.Add("initial-resolution");
    }

    internal void RevalidateSingleSource(BrokerCore.SourceEvidence source, int count)
    {
        Invocation.RecordRevalidatedSource(source, count);
        Events.Add("revalidated-source");
    }

    internal void ResolveRevalidated(BrokerCore.MutationResolutionObservation resolution)
    {
        Invocation.RecordRevalidatedResolution(resolution);
        Events.Add("revalidated-resolution");
    }

    internal Task<int> InvokeAsync()
    {
        Events.Add("invoke");
        return Invocation.InvokeAsync(() =>
        {
            Events.Add("terminal");
            return _terminal.Task;
        });
    }
}
