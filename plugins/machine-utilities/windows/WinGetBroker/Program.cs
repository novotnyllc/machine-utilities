using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using Microsoft.Management.Deployment;
using Microsoft.Win32.SafeHandles;
using Windows.System;

namespace MachineUtilities.WinGetBroker;

internal static class Program
{
    private const string CallerIdentifier = "machine-utilities-winget-broker-v1";
    private const string ProviderVersion = "1.29.280";
    private const uint GenericRead = 0x80000000;
    private const uint ReadControl = 0x00020000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const int FullControlMask = 0x001f01ff;
    private const int TraverseReadMask = 0x001200a0;
    private const int ErrorAlreadyExists = 183;
    private const uint SddlRevision1 = 1;
    private const string ProtectedDirectorySddl =
        "O:SYG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)";
    private const string ProviderSecureDirectorySddl =
        "O:BAG:SYD:P(A;OICI;FA;;;SY)";

    private static readonly HashSet<string> KnownReasons = new(StringComparer.Ordinal)
    {
        "active_state_drift",
        "ambiguous_package",
        "ambiguous_version",
        "authentication_required",
        "catalog_connect_failed",
        "catalog_refresh_failed",
        "catalog_search_failed",
        "dangerous_option_drift",
        "deployment_identity_drift",
        "inapplicable_installer",
        "installed_state_drift",
        "invalid_constraints",
        "invalid_policy",
        "invalid_precondition",
        "invalid_provider_context",
        "invalid_provider_lock",
        "package_agreement_required",
        "package_not_found",
        "package_object_shape_drift",
        "package_state_drift",
        "provider_failure",
        "provider_settings_rejected",
        "runtime_state_drift",
        "source_agreement_required",
        "source_drift",
        "source_provision_failed",
        "unauthorized_policy",
        "unauthorized_policy_token",
        "unsupported_architecture",
        "unsupported_context",
        "unsupported_installer_type",
        "unsupported_version",
    };

    private enum AclContract
    {
        None,
        Protected,
        ProtectedTraverse,
        ProviderStandard,
        ProviderSecure,
    }

    private readonly record struct FileIdentity(
        uint VolumeSerialNumber,
        ulong FileIndex,
        uint NumberOfLinks,
        long Length,
        FileAttributes Attributes);

    private sealed class HeldDirectory : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private readonly FileIdentity _identity;
        private readonly string _aclSignature;
        private readonly AclContract _aclContract;

        private HeldDirectory(
            string path,
            SafeFileHandle handle,
            FileIdentity identity,
            string aclSignature,
            AclContract aclContract)
        {
            Path = path;
            _handle = handle;
            _identity = identity;
            _aclSignature = aclSignature;
            _aclContract = aclContract;
        }

        internal string Path { get; }
        internal string AclSignature => _aclSignature;

        internal static HeldDirectory Open(
            string path,
            AclContract aclContract,
            string reason,
            bool allowWriteSharing = false,
            bool allowDeleteSharing = false)
        {
            string fullPath = System.IO.Path.GetFullPath(path);
            uint share = FileShareRead |
                (allowWriteSharing ? FileShareWrite : 0) |
                (allowDeleteSharing ? FileShareDelete : 0);
            SafeFileHandle handle = CreateFileW(
                fullPath,
                FileReadAttributes | ReadControl,
                share,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw new InvalidOperationException(reason);
            }

            try
            {
                FileIdentity identity = ReadIdentity(handle, fullPath, expectDirectory: true, requireSingleLink: false, reason);
                string aclSignature = aclContract == AclContract.None
                    ? string.Empty
                    : ValidateAcl(handle, aclContract, reason);
                return new HeldDirectory(fullPath, handle, identity, aclSignature, aclContract);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        internal void AssertStable(string reason)
        {
            FileIdentity current = ReadIdentity(_handle, Path, expectDirectory: true, requireSingleLink: false, reason);
            if (current != _identity ||
                (_aclContract != AclContract.None && ValidateAcl(_handle, _aclContract, reason) != _aclSignature))
            {
                throw new InvalidOperationException(reason);
            }
        }

        public void Dispose() => _handle.Dispose();
    }

    private sealed class HeldFile : IDisposable
    {
        private readonly FileStream _stream;
        private readonly FileIdentity _identity;
        private readonly string _aclSignature;
        private readonly int _maximumBytes;

        private HeldFile(
            string path,
            FileStream stream,
            FileIdentity identity,
            string aclSignature,
            int maximumBytes,
            byte[]? bytes,
            string sha256)
        {
            Path = path;
            _stream = stream;
            _identity = identity;
            _aclSignature = aclSignature;
            _maximumBytes = maximumBytes;
            Bytes = bytes;
            Sha256 = sha256;
        }

        internal string Path { get; }
        internal string Name => System.IO.Path.GetFileName(Path);
        internal byte[]? Bytes { get; }
        internal string Sha256 { get; }
        internal long Length => _identity.Length;
        internal SafeFileHandle Handle => _stream.SafeFileHandle;

        internal static HeldFile Open(string path, int maximumBytes, bool retainBytes, string reason)
        {
            string fullPath = System.IO.Path.GetFullPath(path);
            SafeFileHandle handle = CreateFileW(
                fullPath,
                GenericRead | ReadControl,
                FileShareRead,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOpenReparsePoint | FileFlagSequentialScan,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw new InvalidOperationException(reason);
            }

            FileStream? stream = null;
            try
            {
                FileIdentity identity = ReadIdentity(handle, fullPath, expectDirectory: false, requireSingleLink: true, reason);
                string aclSignature = ValidateAcl(handle, AclContract.Protected, reason);
                stream = new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
                (byte[]? bytes, string sha256, long length) = ReadHeldFile(stream, maximumBytes, retainBytes, reason);
                if (length != identity.Length)
                {
                    throw new InvalidOperationException(reason);
                }

                return new HeldFile(fullPath, stream, identity, aclSignature, maximumBytes, bytes, sha256);
            }
            catch
            {
                if (stream is not null)
                {
                    stream.Dispose();
                }
                else
                {
                    handle.Dispose();
                }

                throw;
            }
        }

        internal void AssertStable(string reason)
        {
            FileIdentity current = ReadIdentity(Handle, Path, expectDirectory: false, requireSingleLink: true, reason);
            (byte[]? bytes, string sha256, long length) = ReadHeldFile(
                _stream,
                _maximumBytes,
                retainBytes: Bytes is not null,
                reason);
            if (current != _identity || length != _identity.Length || sha256 != Sha256 ||
                (Bytes is not null && (bytes is null || !bytes.AsSpan().SequenceEqual(Bytes))) ||
                ValidateAcl(Handle, AclContract.Protected, reason) != _aclSignature)
            {
                throw new InvalidOperationException(reason);
            }
        }

        public void Dispose() => _stream.Dispose();
    }

    private sealed class DeploymentState
    {
        internal DeploymentState(
            string directoryPath,
            IReadOnlyList<HeldFile> files,
            IReadOnlyList<string> names,
            BrokerCore.DeploymentEvidence evidence)
        {
            DirectoryPath = directoryPath;
            Files = files;
            Names = names;
            Evidence = evidence;
        }

        internal string DirectoryPath { get; }
        internal IReadOnlyList<HeldFile> Files { get; }
        internal IReadOnlyList<string> Names { get; }
        internal BrokerCore.DeploymentEvidence Evidence { get; }

        internal void AssertStable()
        {
            string[] names = EnumerateDeploymentNames(DirectoryPath);
            if (!names.SequenceEqual(Names, StringComparer.Ordinal))
            {
                throw new InvalidOperationException("deployment_identity_drift");
            }

            foreach (HeldFile file in Files)
            {
                file.AssertStable("deployment_identity_drift");
            }
        }
    }

    private sealed class ProtectedState : IDisposable
    {
        private readonly IReadOnlyList<HeldDirectory> _directories;
        private readonly IReadOnlyList<HeldFile> _files;

        internal ProtectedState(
            BrokerCore.HelperRequest request,
            BrokerCore.ActiveGenerationPointer pointer,
            HeldFile pointerFile,
            HeldFile policyFile,
            HeldFile constraintsFile,
            HeldFile contextFile,
            HeldFile providerLockFile,
            HeldFile openSshIdentityFile,
            IReadOnlyList<HeldDirectory> directories,
            IReadOnlyList<HeldFile> files,
            BrokerCore.ProviderLock providerLock,
            BrokerCore.PolicyDocument policy,
            BrokerCore.ConstraintsDocument constraints,
            BrokerCore.ProviderContext context,
            DeploymentState deployment)
        {
            Request = request;
            Pointer = pointer;
            PointerFile = pointerFile;
            PolicyFile = policyFile;
            ConstraintsFile = constraintsFile;
            ContextFile = contextFile;
            ProviderLockFile = providerLockFile;
            OpenSshIdentityFile = openSshIdentityFile;
            _directories = directories;
            _files = files;
            ProviderLock = providerLock;
            Policy = policy;
            Constraints = constraints;
            Context = context;
            Deployment = deployment;
        }

        internal BrokerCore.HelperRequest Request { get; }
        internal BrokerCore.ActiveGenerationPointer Pointer { get; }
        internal HeldFile PointerFile { get; }
        internal HeldFile PolicyFile { get; }
        internal HeldFile ConstraintsFile { get; }
        internal HeldFile ContextFile { get; }
        internal HeldFile ProviderLockFile { get; }
        internal HeldFile OpenSshIdentityFile { get; }
        internal BrokerCore.ProviderLock ProviderLock { get; }
        internal BrokerCore.PolicyDocument Policy { get; }
        internal BrokerCore.ConstraintsDocument Constraints { get; }
        internal BrokerCore.ProviderContext Context { get; }
        internal DeploymentState Deployment { get; }

        internal void AssertStable()
        {
            foreach (HeldDirectory directory in _directories)
            {
                directory.AssertStable("active_state_drift");
            }

            PointerFile.AssertStable("active_state_drift");
            PolicyFile.AssertStable("active_state_drift");
            ConstraintsFile.AssertStable("active_state_drift");
            ContextFile.AssertStable("active_state_drift");
            ProviderLockFile.AssertStable("active_state_drift");
            OpenSshIdentityFile.AssertStable("active_state_drift");
            _ = BrokerCore.ValidateGenerationEnvelope(
                Pointer,
                Request,
                PolicyFile.Sha256,
                ConstraintsFile.Sha256,
                ContextFile.Sha256,
                ProviderLockFile.Sha256,
                OpenSshIdentityFile.Sha256);
            Deployment.AssertStable();
        }

        public void Dispose()
        {
            for (int index = _files.Count - 1; index >= 0; index--)
            {
                _files[index].Dispose();
            }

            for (int index = _directories.Count - 1; index >= 0; index--)
            {
                _directories[index].Dispose();
            }
        }
    }

    private sealed class ProviderRuntimeState : IDisposable
    {
        private readonly IReadOnlyList<HeldDirectory> _directories;
        private readonly IReadOnlyList<string> _settingsRoots;

        private ProviderRuntimeState(
            IReadOnlyList<HeldDirectory> directories,
            IReadOnlyList<string> settingsRoots,
            string evidenceSha256)
        {
            _directories = directories;
            _settingsRoots = settingsRoots;
            EvidenceSha256 = evidenceSha256;
        }

        internal string EvidenceSha256 { get; }
        internal IReadOnlyList<string> SettingsRoots => _settingsRoots;

        internal static ProviderRuntimeState Open(string stateIdentifier) =>
            Open(stateIdentifier, provisioning: false);

        internal static ProviderRuntimeState Provision(
            string stateIdentifier,
            Action markMutationStarted) =>
            Open(stateIdentifier, provisioning: true, markMutationStarted);

        private static ProviderRuntimeState Open(
            string stateIdentifier,
            bool provisioning,
            Action? markMutationStarted = null)
        {
            var directories = new Dictionary<string, HeldDirectory>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string systemRoot = RequireFixedEnvironmentPath("SystemRoot");
                string programData = RequireFixedEnvironmentPath("ProgramData");
                string localAppData = RequireFixedEnvironmentPath("LOCALAPPDATA");
                string temp = RequireFixedEnvironmentPath("TEMP");
                string tmp = RequireFixedEnvironmentPath("TMP");
                string expectedLocalAppData = Path.Combine(
                    systemRoot, "System32", "config", "systemprofile", "AppData", "Local");
                string expectedTemp = Path.Combine(
                    programData, "MachineUtilities", "state", "processing", "temp");
                if (!SamePath(localAppData, expectedLocalAppData) || !SamePath(temp, tmp) ||
                    !SamePath(temp, expectedTemp))
                {
                    throw new InvalidOperationException("runtime_state_drift");
                }

                var targets = new[]
                {
                    (Kind: "state-cache", Anchor: localAppData,
                        Path: Path.Combine(localAppData, "Microsoft", "WinGet", "State", stateIdentifier),
                        Contract: AclContract.ProviderStandard),
                    (Kind: "settings", Anchor: localAppData,
                        Path: Path.Combine(localAppData, "Microsoft", "WinGet", "Settings", stateIdentifier),
                        Contract: AclContract.ProviderStandard),
                    (Kind: "secure-settings", Anchor: programData,
                        Path: Path.Combine(programData, "Microsoft", "WinGet", "S-1-5-18", "settings", "win", stateIdentifier),
                        Contract: AclContract.ProviderSecure),
                    (Kind: "temp-cache", Anchor: temp,
                        Path: Path.Combine(temp, "WinGet", stateIdentifier),
                        Contract: AclContract.ProviderStandard),
                };

                var observations = new List<BrokerCore.ProviderRuntimeRootObservation>(targets.Length);
                foreach ((string kind, string anchor, string path, AclContract contract) in targets)
                {
                    AddRuntimeDirectoryChain(
                        anchor,
                        path,
                        contract,
                        provisioning,
                        markMutationStarted,
                        directories);
                    HeldDirectory leaf = directories[Path.GetFullPath(path)];
                    observations.Add(new BrokerCore.ProviderRuntimeRootObservation(
                        kind,
                        BrokerCore.Sha256Utf8(NormalizePath(leaf.Path).ToUpperInvariant()),
                        BrokerCore.Sha256Utf8(leaf.AclSignature)));
                }

                var state = new ProviderRuntimeState(
                    directories.Values.ToArray(),
                    targets.Where(target => target.Kind is "settings" or "secure-settings")
                        .Select(target => Path.GetFullPath(target.Path))
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    BrokerCore.ComputeProviderRuntimeRootsSha256(observations));
                state.AssertStable();
                return state;
            }
            catch
            {
                HeldDirectory[] held = directories.Values.ToArray();
                for (int index = held.Length - 1; index >= 0; index--)
                {
                    held[index].Dispose();
                }

                throw;
            }
        }

        internal void AssertStable()
        {
            foreach (HeldDirectory directory in _directories)
            {
                directory.AssertStable("runtime_state_drift");
            }
        }

        public void Dispose()
        {
            for (int index = _directories.Count - 1; index >= 0; index--)
            {
                _directories[index].Dispose();
            }
        }

        private static string RequireFixedEnvironmentPath(string name)
        {
            string? value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process);
            if (string.IsNullOrEmpty(value) || !Path.IsPathFullyQualified(value) ||
                Path.GetFullPath(value).StartsWith(@"\\", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("runtime_state_drift");
            }

            return Path.GetFullPath(value);
        }

        private static void AddRuntimeDirectoryChain(
            string anchor,
            string target,
            AclContract leafContract,
            bool provisioning,
            Action? markMutationStarted,
            IDictionary<string, HeldDirectory> directories)
        {
            string fullAnchor = Path.GetFullPath(anchor);
            string fullTarget = Path.GetFullPath(target);
            if (!SamePath(fullAnchor, fullTarget) && !IsDescendantPath(fullTarget, fullAnchor))
            {
                throw new InvalidOperationException("runtime_state_drift");
            }

            string? volumeRoot = Path.GetPathRoot(fullAnchor);
            if (string.IsNullOrEmpty(volumeRoot))
            {
                throw new InvalidOperationException("runtime_state_drift");
            }

            string current = volumeRoot;
            Add(current, AclContract.None);
            string anchorRelative = Path.GetRelativePath(volumeRoot, fullAnchor);
            if (anchorRelative != ".")
            {
                foreach (string component in anchorRelative.Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries))
                {
                    current = Path.Combine(current, component);
                    Add(current, AclContract.None);
                }
            }

            string relative = Path.GetRelativePath(fullAnchor, fullTarget);
            if (relative != ".")
            {
                foreach (string component in relative.Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries))
                {
                    current = Path.Combine(current, component);
                    bool isLeaf = SamePath(current, fullTarget);
                    AclContract createContract = isLeaf ? leafContract : AclContract.Protected;
                    bool created = BrokerCore.AttemptProviderRuntimeDirectoryCreation(
                        provisioning,
                        () => CreateProtectedDirectory(current, createContract));
                    if (created)
                    {
                        markMutationStarted?.Invoke();
                    }
                    Add(current, isLeaf ? leafContract : created ? AclContract.Protected : AclContract.None);
                }
            }

            void Add(string path, AclContract contract)
            {
                string fullPath = Path.GetFullPath(path);
                if (directories.TryGetValue(fullPath, out HeldDirectory? existing))
                {
                    if (contract != AclContract.None && string.IsNullOrEmpty(existing.AclSignature))
                    {
                        throw new InvalidOperationException("runtime_state_drift");
                    }

                    return;
                }

                directories.Add(fullPath, HeldDirectory.Open(
                    fullPath,
                    contract,
                    "runtime_state_drift",
                    allowWriteSharing: true,
                    allowDeleteSharing: false));
            }
        }

        private static bool CreateProtectedDirectory(string path, AclContract contract)
        {
            string sddl = contract == AclContract.ProviderSecure
                ? ProviderSecureDirectorySddl
                : ProtectedDirectorySddl;
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                    sddl,
                    SddlRevision1,
                    out IntPtr descriptor,
                    out _) || descriptor == IntPtr.Zero)
            {
                throw new InvalidOperationException("runtime_state_drift");
            }

            try
            {
                var attributes = new SecurityAttributes
                {
                    Length = (uint)Marshal.SizeOf<SecurityAttributes>(),
                    SecurityDescriptor = descriptor,
                    InheritHandle = 0,
                };
                if (CreateDirectoryW(path, ref attributes))
                {
                    return true;
                }

                if (Marshal.GetLastWin32Error() != ErrorAlreadyExists)
                {
                    throw new InvalidOperationException("runtime_state_drift");
                }

                return false;
            }
            finally
            {
                _ = LocalFree(descriptor);
            }
        }
    }

    private sealed class ProviderSettingsState : IDisposable
    {
        private readonly ProviderRuntimeState _runtimeState;
        private IReadOnlyList<HeldFile> _files = Array.Empty<HeldFile>();
        private string[] _layout = Array.Empty<string>();

        private ProviderSettingsState(ProviderRuntimeState runtimeState)
        {
            _runtimeState = runtimeState;
            Reattest();
        }

        internal static void Inspect(ProviderRuntimeState runtimeState)
        {
            using var settings = new ProviderSettingsState(runtimeState);
        }

        internal static ProviderSettingsState Open(ProviderRuntimeState runtimeState) =>
            new(runtimeState);

        internal void Reattest()
        {
            DisposeFiles();
            try
            {
                _runtimeState.AssertStable();
                (string[] layout, IReadOnlyList<HeldFile> files) = ReadSettingsFiles();
                _layout = layout;
                _files = files;
                AssertStable();
            }
            catch
            {
                DisposeFiles();
                throw;
            }
        }

        internal void AssertStable()
        {
            _runtimeState.AssertStable();
            if (!ReadSettingsLayout().SequenceEqual(_layout, StringComparer.Ordinal))
            {
                throw new InvalidOperationException("runtime_state_drift");
            }

            foreach (HeldFile file in _files)
            {
                file.AssertStable("runtime_state_drift");
                BrokerCore.ValidatePersistedSettingsJson(file.Bytes!);
            }

            _runtimeState.AssertStable();
            if (!ReadSettingsLayout().SequenceEqual(_layout, StringComparer.Ordinal))
            {
                throw new InvalidOperationException("runtime_state_drift");
            }
        }

        public void Dispose() => DisposeFiles();

        private (string[] Layout, IReadOnlyList<HeldFile> Files) ReadSettingsFiles()
        {
            string[] layout = ReadSettingsLayout();
            var files = new List<HeldFile>(layout.Length);
            try
            {
                foreach (string entry in layout)
                {
                    int separator = entry.IndexOf('|');
                    string root = _runtimeState.SettingsRoots[int.Parse(
                        entry[..separator], CultureInfo.InvariantCulture)];
                    string name = entry[(separator + 1)..];
                    HeldFile file = HeldFile.Open(
                        Path.Combine(root, name),
                        BrokerCore.MaxSettingsBytes,
                        retainBytes: true,
                        "runtime_state_drift");
                    BrokerCore.ValidatePersistedSettingsJson(file.Bytes!);
                    files.Add(file);
                }

                return (layout, files);
            }
            catch
            {
                for (int index = files.Count - 1; index >= 0; index--)
                {
                    files[index].Dispose();
                }

                throw;
            }
        }

        private string[] ReadSettingsLayout()
        {
            var layout = new List<string>(4);
            for (int rootIndex = 0; rootIndex < _runtimeState.SettingsRoots.Count; rootIndex++)
            {
                string[] entries;
                try
                {
                    entries = Directory.EnumerateFileSystemEntries(_runtimeState.SettingsRoots[rootIndex])
                        .Take(3)
                        .Select(Path.GetFileName)
                        .OfType<string>()
                        .OrderBy(name => name, StringComparer.Ordinal)
                        .ToArray();
                }
                catch
                {
                    throw new InvalidOperationException("runtime_state_drift");
                }

                BrokerCore.ValidatePersistedSettingsLayout(entries);
                layout.AddRange(entries.Select(name =>
                    rootIndex.ToString(CultureInfo.InvariantCulture) + "|" + name));
            }

            return layout.ToArray();
        }

        private void DisposeFiles()
        {
            for (int index = _files.Count - 1; index >= 0; index--)
            {
                _files[index].Dispose();
            }

            _files = Array.Empty<HeldFile>();
            _layout = Array.Empty<string>();
        }
    }

    private sealed record CatalogSession(
        PackageCatalogReference Reference,
        PackageCatalog Catalog,
        BrokerCore.SourceEvidence Source,
        int SourceCount);

    private sealed record ResolvedMutation(
        CatalogPackage OperationPackage,
        PackageVersionId VersionId,
        InstallOptions Options,
        BrokerCore.PackageEvidence Evidence,
        string PreStateSha256);

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "provision")
        {
            return await RunProvisionAsync().ConfigureAwait(false);
        }

        BrokerCore.BrokerMode mode;
        BrokerCore.BrokerMode operationMode;
        BrokerCore.HelperRequest request;
        try
        {
            mode = BrokerCore.ParseMode(args);
            request = BrokerCore.ParseRequest(ReadStandardInput(), mode);
            operationMode = mode == BrokerCore.BrokerMode.Probe
                ? BrokerCore.ModeForAction(request.ActionId)
                : mode;
        }
        catch
        {
            return 64;
        }

        ProtectedState? state = null;
        ProviderRuntimeState? runtimeState = null;
        ProviderSettingsState? settingsState = null;
        BrokerCore.SourceEvidence? source = null;
        IReadOnlyList<BrokerCore.PackageEvidence> packages = Array.Empty<BrokerCore.PackageEvidence>();
        string settingsSha256 = "-";
        string providerVersion = "-";
        string providerStatus = "not-applicable";
        string providerExtendedError = "-";
        string providerInstallerError = "-";
        string preStateSha256 = "-";
        string postStateSha256 = "-";
        bool rebootRequired = false;
        bool callStarted = false;

        try
        {
            AssertSystemContext();
            SanitizeProcessEnvironment();
            state = LoadProtectedState(request);
            runtimeState = ProviderRuntimeState.Open(state.Context.StateIdentifier);

            BrokerCore.PolicyAction action = state.Policy.Actions[request.ActionId];
            if (!action.Enabled || action.Context != BrokerCore.ContextName ||
                state.Constraints.Generation != request.EnrollmentEpoch)
            {
                throw new InvalidOperationException("unauthorized_policy");
            }

            BrokerCore.WinGetConstraint? constraint = operationMode == BrokerCore.BrokerMode.Inventory
                ? null
                : BrokerCore.SelectConstraint(operationMode, request, state.Policy, state.Constraints, state.Context);
            BrokerCore.ProviderMutationInvocation? mutationInvocation = mode == BrokerCore.BrokerMode.Probe || constraint is null
                ? null
                : new BrokerCore.ProviderMutationInvocation(operationMode, state.Context, constraint);
            string settingsJson = BrokerCore.BuildUserSettings(constraint);
            settingsSha256 = BrokerCore.Sha256Utf8(settingsJson);

            ProviderSettingsState.Inspect(runtimeState);
            var managerSettings = new PackageManagerSettings();
            if (!managerSettings.SetCallerIdentifier(CallerIdentifier) ||
                !managerSettings.SetStateIdentifier(state.Context.StateIdentifier) ||
                !managerSettings.SetUserSettings(settingsJson))
            {
                throw new InvalidOperationException("provider_settings_rejected");
            }

            mutationInvocation?.RecordSettingsApplied();
            ProviderSettingsState.Inspect(runtimeState);
            runtimeState.AssertStable();

            var manager = new PackageManager();
            mutationInvocation?.RecordManagerActivated();
            providerVersion = manager.Version;
            if (providerVersion != state.ProviderLock.ProviderVersion || providerVersion != ProviderVersion)
            {
                throw new InvalidOperationException("deployment_identity_drift");
            }

            settingsState = ProviderSettingsState.Open(runtimeState);
            CatalogSession catalog = await OpenProductionCatalogAsync(manager, state.Context, refresh: true).ConfigureAwait(false);
            settingsState.Reattest();
            source = catalog.Source;
            mutationInvocation?.RecordInitialSource(catalog.Source, catalog.SourceCount);

            if (mode == BrokerCore.BrokerMode.Probe)
            {
                async Task<(BrokerCore.SourceEvidence Source, IReadOnlyList<BrokerCore.PackageEvidence> Packages,
                    string PreStateSha256)> ResolveProbeStateAsync(CatalogSession session)
                {
                    if (operationMode == BrokerCore.BrokerMode.Inventory)
                    {
                        PackageCatalog installedCatalog = await OpenInstalledCatalogAsync(manager).ConfigureAwait(false);
                        IReadOnlyList<BrokerCore.PackageEvidence> inventory = await ResolveInventoryAsync(
                            session.Catalog,
                            installedCatalog,
                            state.Context,
                            BrokerCore.InventoryConstraints(state.Constraints, state.Context)).ConfigureAwait(false);
                        return (session.Source, inventory, BrokerCore.ComputeSourceStateSha256(session.Source));
                    }

                    PackageCatalog installedCatalog = await OpenInstalledCatalogAsync(manager).ConfigureAwait(false);
                    PackageCatalog operationCatalog = operationMode == BrokerCore.BrokerMode.Upgrade
                        ? await OpenUpgradeCatalogAsync(manager, session).ConfigureAwait(false)
                        : session.Catalog;
                    ResolvedMutation resolvedProbe = await ResolveMutationAsync(
                        operationMode,
                        session.Catalog,
                        operationCatalog,
                        installedCatalog,
                        state.Context,
                        session.Source,
                        constraint!).ConfigureAwait(false);
                    _ = AssertDangerousOptionsFixed(
                        resolvedProbe.Options,
                        resolvedProbe.VersionId,
                        constraint!);
                    return (session.Source, new[] { resolvedProbe.Evidence }, resolvedProbe.PreStateSha256);
                }

                var initialProbe = await ResolveProbeStateAsync(catalog).ConfigureAwait(false);
                source = initialProbe.Source;
                packages = initialProbe.Packages;
                preStateSha256 = initialProbe.PreStateSha256;
                AssertProtectedStateStable(state, runtimeState, settingsState);

                CatalogSession revalidatedCatalog = await OpenProductionCatalogAsync(
                    manager,
                    state.Context,
                    refresh: true).ConfigureAwait(false);
                settingsState.Reattest();
                var revalidatedProbe = await ResolveProbeStateAsync(revalidatedCatalog).ConfigureAwait(false);
                if (revalidatedProbe.PreStateSha256 != initialProbe.PreStateSha256 ||
                    !revalidatedProbe.Packages.SequenceEqual(initialProbe.Packages))
                {
                    throw new InvalidOperationException("package_state_drift");
                }

                source = revalidatedProbe.Source;
                packages = revalidatedProbe.Packages;
                preStateSha256 = revalidatedProbe.PreStateSha256;
                AssertProtectedStateStable(state, runtimeState, settingsState);
                WritePreconditionProbeResult(new BrokerCore.PreconditionProbeResult(
                    request.RequestId,
                    request.ActionId,
                    request.PolicyToken,
                    preStateSha256));
                return 0;
            }

            if (mode == BrokerCore.BrokerMode.Inventory)
            {
                preStateSha256 = BrokerCore.ComputeSourceStateSha256(source);
                if (request.PreconditionSha256 != preStateSha256)
                {
                    throw new InvalidOperationException("invalid_precondition");
                }

                PackageCatalog installedCatalog = await OpenInstalledCatalogAsync(manager).ConfigureAwait(false);
                packages = await ResolveInventoryAsync(
                    catalog.Catalog,
                    installedCatalog,
                    state.Context,
                    BrokerCore.InventoryConstraints(state.Constraints, state.Context)).ConfigureAwait(false);
                AssertProtectedStateStable(state, runtimeState, settingsState);

                var inventoryResult = BuildResult(
                    request,
                    state,
                    BrokerCore.TerminalState.Completed,
                    "inventory_verified",
                    source,
                    packages,
                    runtimeState,
                    settingsSha256,
                    providerVersion,
                    "not-applicable",
                    "-",
                    "-",
                    false,
                    preStateSha256,
                    preStateSha256);
                WriteResult(inventoryResult);
                return 0;
            }

            PackageCatalog installed = await OpenInstalledCatalogAsync(manager).ConfigureAwait(false);
            PackageCatalog operationCatalog = mode == BrokerCore.BrokerMode.Upgrade
                ? await OpenUpgradeCatalogAsync(manager, catalog).ConfigureAwait(false)
                : catalog.Catalog;
            ResolvedMutation resolved = await ResolveMutationAsync(
                mode,
                catalog.Catalog,
                operationCatalog,
                installed,
                state.Context,
                catalog.Source,
                constraint!).ConfigureAwait(false);
            BrokerCore.DangerousOptionsObservation resolvedOptions = AssertDangerousOptionsFixed(
                resolved.Options,
                resolved.VersionId,
                constraint!);
            mutationInvocation!.RecordInitialResolution(new BrokerCore.MutationResolutionObservation(
                resolved.Evidence,
                resolved.VersionId.PackageCatalogId,
                resolvedOptions));
            packages = new[] { resolved.Evidence };
            preStateSha256 = resolved.PreStateSha256;
            if (request.PreconditionSha256 != preStateSha256)
            {
                throw new InvalidOperationException("invalid_precondition");
            }

            AssertProtectedStateStable(state, runtimeState, settingsState);
            CatalogSession revalidatedCatalog = await OpenProductionCatalogAsync(manager, state.Context, refresh: true).ConfigureAwait(false);
            settingsState.Reattest();
            mutationInvocation!.RecordRevalidatedSource(
                revalidatedCatalog.Source,
                revalidatedCatalog.SourceCount);
            PackageCatalog revalidatedInstalled = await OpenInstalledCatalogAsync(manager).ConfigureAwait(false);
            PackageCatalog revalidatedOperationCatalog = mode == BrokerCore.BrokerMode.Upgrade
                ? await OpenUpgradeCatalogAsync(manager, revalidatedCatalog).ConfigureAwait(false)
                : revalidatedCatalog.Catalog;
            ResolvedMutation revalidated = await ResolveMutationAsync(
                mode,
                revalidatedCatalog.Catalog,
                revalidatedOperationCatalog,
                revalidatedInstalled,
                state.Context,
                revalidatedCatalog.Source,
                constraint!).ConfigureAwait(false);
            if (revalidated.PreStateSha256 != preStateSha256 ||
                revalidated.Evidence.CandidateVersion != resolved.Evidence.CandidateVersion ||
                revalidated.Evidence.InstallerType != resolved.Evidence.InstallerType ||
                revalidated.Evidence.NestedInstallerType != resolved.Evidence.NestedInstallerType)
            {
                throw new InvalidOperationException("package_state_drift");
            }

            source = revalidatedCatalog.Source;
            AssertProtectedStateStable(state, runtimeState, settingsState);
            BrokerCore.DangerousOptionsObservation revalidatedOptions = AssertDangerousOptionsFixed(
                revalidated.Options,
                revalidated.VersionId,
                constraint!);
            mutationInvocation!.RecordRevalidatedResolution(new BrokerCore.MutationResolutionObservation(
                revalidated.Evidence,
                revalidated.VersionId.PackageCatalogId,
                revalidatedOptions));

            InstallResult providerResult = await mutationInvocation!.InvokeAsync(async () =>
            {
                callStarted = true;
                return mode == BrokerCore.BrokerMode.Install
                    ? await manager.InstallPackageAsync(revalidated.OperationPackage, revalidated.Options)
                    : await manager.UpgradePackageAsync(revalidated.OperationPackage, revalidated.Options);
            }).ConfigureAwait(false);

            providerStatus = providerResult.Status.ToString();
            BrokerCore.EnsureResultAtom(providerStatus);
            providerExtendedError = FormatHResult(providerResult.ExtendedErrorCode);
            providerInstallerError = providerResult.InstallerErrorCode.ToString(CultureInfo.InvariantCulture);
            rebootRequired = providerResult.RebootRequired;

            bool postMatches = false;
            string postVersion = "-";
            try
            {
                PackageCatalog postCatalog = await OpenInstalledCatalogAsync(manager).ConfigureAwait(false);
                CatalogPackage? postPackage = await FindExactAsync(postCatalog, constraint!.PackageId, true).ConfigureAwait(false);
                if (postPackage?.InstalledVersion is not null)
                {
                    ValidateInstalledVersion(postPackage.InstalledVersion, constraint.PackageId);
                    postVersion = ValidateVersionForResult(postPackage.InstalledVersion.Version);
                    postMatches = postVersion == revalidated.Evidence.CandidateVersion;
                    postStateSha256 = BrokerCore.ComputePackageStateSha256(
                        constraint.PackageId,
                        postVersion,
                        revalidated.Evidence.CandidateVersion,
                        revalidated.Evidence.Architecture,
                        revalidated.Evidence.Locale,
                        revalidated.Evidence.InstallerType,
                        BrokerCore.ComputeSourceStateSha256(source));
                }
            }
            catch
            {
                postMatches = false;
                postStateSha256 = "-";
            }

            AssertProtectedStateStable(state, runtimeState, settingsState);
            packages = new[] { revalidated.Evidence with { PostVersion = postVersion } };
            bool providerSucceeded = providerResult.Status == InstallResultStatus.Ok;
            (BrokerCore.TerminalState terminalState, string reason) = BrokerCore.ClassifyMutation(
                true,
                providerSucceeded,
                rebootRequired,
                postMatches,
                "provider_failure");
            var mutationResult = BuildResult(
                request,
                state,
                terminalState,
                reason,
                source,
                packages,
                runtimeState,
                settingsSha256,
                providerVersion,
                providerStatus,
                providerExtendedError,
                providerInstallerError,
                rebootRequired,
                preStateSha256,
                postStateSha256);
            WriteResult(mutationResult);
            return terminalState switch
            {
                BrokerCore.TerminalState.Completed => 0,
                BrokerCore.TerminalState.Partial => 2,
                _ => 3,
            };
        }
        catch (Exception exception)
        {
            BrokerCore.TerminalState terminalState = callStarted
                ? BrokerCore.TerminalState.Partial
                : BrokerCore.TerminalState.Rejected;
            string reason = MapReason(exception);
            var failure = BuildResult(
                request,
                state,
                terminalState,
                reason,
                source,
                packages,
                runtimeState,
                settingsSha256,
                providerVersion,
                callStarted ? "exception" : providerStatus,
                providerExtendedError,
                providerInstallerError,
                rebootRequired,
                preStateSha256,
                postStateSha256);

            try
            {
                WriteResult(failure);
            }
            catch
            {
                return 70;
            }

            return callStarted ? 2 : 3;
        }
        finally
        {
            settingsState?.Dispose();
            runtimeState?.Dispose();
            state?.Dispose();
        }
    }

    private static async Task<int> RunProvisionAsync()
    {
        BrokerCore.ProvisionRequest provision;
        try
        {
            provision = BrokerCore.ParseProvisionRequest(
                ReadBounded(Console.OpenStandardInput(), BrokerCore.MaxProvisionRequestBytes));
        }
        catch
        {
            return 64;
        }

        var helperRequest = new BrokerCore.HelperRequest(
            "request-00000000000000000000000000000000",
            BrokerCore.InventoryAction,
            "-",
            provision.EnrollmentEpoch,
            provision.PolicySha256,
            provision.ConstraintsSha256,
            provision.ContextSha256,
            provision.ProviderLockSha256,
            new string('0', 64));
        ProtectedState? state = null;
        ProviderRuntimeState? runtimeState = null;
        ProviderSettingsState? settingsState = null;
        BrokerCore.SourceEvidence? source = null;
        string settingsSha256 = "-";
        string providerVersion = "-";
        bool mutationStarted = false;
        try
        {
            AssertSystemContext();
            SanitizeProcessEnvironment();
            state = LoadProtectedState(helperRequest);
            if (state.Pointer.GenerationSha256 != provision.GenerationSha256)
            {
                throw new InvalidOperationException("active_state_drift");
            }

            BrokerCore.PolicyAction inventory = state.Policy.Actions[BrokerCore.InventoryAction];
            if (!inventory.Enabled || inventory.Context != BrokerCore.ContextName ||
                state.Constraints.Generation != provision.EnrollmentEpoch)
            {
                throw new InvalidOperationException("unauthorized_policy");
            }

            runtimeState = ProviderRuntimeState.Provision(
                state.Context.StateIdentifier,
                () => mutationStarted = true);
            string settingsJson = BrokerCore.BuildUserSettings(null);
            settingsSha256 = BrokerCore.Sha256Utf8(settingsJson);
            ProviderSettingsState.Inspect(runtimeState);
            var managerSettings = new PackageManagerSettings();
            if (!managerSettings.SetCallerIdentifier(CallerIdentifier) ||
                !managerSettings.SetStateIdentifier(state.Context.StateIdentifier) ||
                !managerSettings.SetUserSettings(settingsJson))
            {
                throw new InvalidOperationException("provider_settings_rejected");
            }

            ProviderSettingsState.Inspect(runtimeState);
            AssertProtectedStateStable(state, runtimeState);
            mutationStarted = true;
            var manager = new PackageManager();
            providerVersion = manager.Version;
            if (providerVersion != state.ProviderLock.ProviderVersion || providerVersion != ProviderVersion)
            {
                throw new InvalidOperationException("deployment_identity_drift");
            }

            settingsState = ProviderSettingsState.Open(runtimeState);
            source = await ProvisionProductionCatalogAsync(
                manager,
                state.Context,
                () => mutationStarted = true).ConfigureAwait(false);
            settingsState.Reattest();
            AssertProtectedStateStable(state, runtimeState, settingsState);
            WriteProvisionResult(BuildProvisionResult(
                provision,
                state,
                runtimeState,
                BrokerCore.TerminalState.Completed,
                "provider_state_provisioned",
                providerVersion,
                settingsSha256,
                source));
            return 0;
        }
        catch (Exception exception)
        {
            BrokerCore.TerminalState terminalState = mutationStarted
                ? BrokerCore.TerminalState.Partial
                : BrokerCore.TerminalState.Rejected;
            try
            {
                WriteProvisionResult(BuildProvisionResult(
                    provision,
                    state,
                    runtimeState,
                    terminalState,
                    MapReason(exception),
                    providerVersion,
                    settingsSha256,
                    source));
            }
            catch
            {
                return 70;
            }

            return terminalState == BrokerCore.TerminalState.Partial ? 2 : 3;
        }
        finally
        {
            settingsState?.Dispose();
            runtimeState?.Dispose();
            state?.Dispose();
        }
    }

    private static ProtectedState LoadProtectedState(BrokerCore.HelperRequest request)
    {
        var directories = new Dictionary<string, HeldDirectory>(StringComparer.OrdinalIgnoreCase);
        var files = new List<HeldFile>();
        try
        {
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrEmpty(programData) || !Path.IsPathFullyQualified(programData))
            {
                throw new InvalidOperationException("unsupported_context");
            }

            string fullProgramData = Path.GetFullPath(programData);
            if (fullProgramData.StartsWith(@"\\", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("unsupported_context");
            }

            string root = Path.GetFullPath(Path.Combine(fullProgramData, "MachineUtilities"));
            AddDirectoryChain(root, root, directories, "active_state_drift");

            HeldFile pointerFile = AddHeldFile(
                Path.Combine(root, "active.generation"), 160, true, "active_state_drift", files);
            BrokerCore.ActiveGenerationPointer pointer;
            try
            {
                pointer = BrokerCore.ParseActiveGenerationPointer(pointerFile.Bytes!);
            }
            catch (FormatException)
            {
                throw new InvalidOperationException("active_state_drift");
            }
            if (pointer.Epoch != request.EnrollmentEpoch)
            {
                throw new InvalidOperationException("active_state_drift");
            }

            string generationsRoot = Path.Combine(root, "generations");
            AddDirectoryChain(generationsRoot, root, directories, "active_state_drift");
            string generationRoot = Path.Combine(
                generationsRoot,
                pointer.Epoch.ToString(CultureInfo.InvariantCulture));
            AddDirectoryChain(generationRoot, root, directories, "active_state_drift");

            HeldFile policyFile = AddHeldFile(
                Path.Combine(generationRoot, "policy.actions"), BrokerCore.MaxPolicyBytes, true,
                "active_state_drift", files);
            HeldFile constraintsFile = AddHeldFile(
                Path.Combine(generationRoot, "policy.constraints"), BrokerCore.MaxConstraintsBytes, true,
                "active_state_drift", files);
            HeldFile contextFile = AddHeldFile(
                Path.Combine(generationRoot, "winget.context"), BrokerCore.MaxContextBytes, true,
                "active_state_drift", files);
            HeldFile providerLockFile = AddHeldFile(
                Path.Combine(generationRoot, "windows-winget-provider.lock"), BrokerCore.MaxProviderLockBytes, true,
                "active_state_drift", files);
            HeldFile openSshIdentityFile = AddHeldFile(
                Path.Combine(generationRoot, "openssh.identity"), BrokerCore.MaxOpenSshIdentityBytes, true,
                "active_state_drift", files);

            _ = BrokerCore.ValidateGenerationEnvelope(
                pointer,
                request,
                policyFile.Sha256,
                constraintsFile.Sha256,
                contextFile.Sha256,
                providerLockFile.Sha256,
                openSshIdentityFile.Sha256);

            BrokerCore.ProviderLock providerLock = BrokerCore.ParseProviderLock(providerLockFile.Bytes!);
            BrokerCore.PolicyDocument policy = BrokerCore.ParsePolicy(policyFile.Bytes!);
            BrokerCore.ConstraintsDocument constraints = BrokerCore.ParseConstraints(
                constraintsFile.Bytes!,
                policy,
                policyFile.Sha256);
            BrokerCore.ProviderContext context = BrokerCore.ParseProviderContext(contextFile.Bytes!);
            string stateAuthoritySha256 = BrokerCore.ComputeStateAuthoritySha256(
                request.EnrollmentEpoch,
                policyFile.Sha256,
                constraintsFile.Sha256,
                providerLockFile.Sha256,
                context);
            BrokerCore.ValidateStateIdentifierForAuthority(
                context.StateIdentifier,
                request.EnrollmentEpoch,
                stateAuthoritySha256);

            string runtimeIdentifier = GetRuntimeIdentifier();
            BrokerCore.ValidateProviderRuntime(providerLock, Environment.Version.ToString(3), runtimeIdentifier);

            string deploymentRoot = Path.Combine(generationRoot, "winget", runtimeIdentifier);
            AddDirectoryChain(deploymentRoot, root, directories, "deployment_identity_drift");
            string expectedExecutable = Path.Combine(deploymentRoot, "MachineUtilities.WinGetBroker.exe");
            if (!SamePath(AppContext.BaseDirectory, deploymentRoot) || Environment.ProcessPath is null ||
                !SamePath(Environment.ProcessPath, expectedExecutable))
            {
                throw new InvalidOperationException("deployment_identity_drift");
            }

            DeploymentState deployment = LoadDeploymentState(deploymentRoot, providerLock, files);
            if (deployment.Evidence.FileSetSha256 != context.DeploymentFileSetSha256 ||
                deployment.Evidence.AuthenticodeIdentitySha256 != context.AppInstallerIdentitySha256)
            {
                throw new InvalidOperationException("deployment_identity_drift");
            }

            var state = new ProtectedState(
                request,
                pointer,
                pointerFile,
                policyFile,
                constraintsFile,
                contextFile,
                providerLockFile,
                openSshIdentityFile,
                directories.Values.ToArray(),
                files.ToArray(),
                providerLock,
                policy,
                constraints,
                context,
                deployment);
            state.AssertStable();
            return state;
        }
        catch
        {
            for (int index = files.Count - 1; index >= 0; index--)
            {
                files[index].Dispose();
            }

            HeldDirectory[] heldDirectories = directories.Values.ToArray();
            for (int index = heldDirectories.Length - 1; index >= 0; index--)
            {
                heldDirectories[index].Dispose();
            }

            throw;
        }
    }

    private static HeldFile AddHeldFile(
        string path,
        int maximumBytes,
        bool retainBytes,
        string reason,
        ICollection<HeldFile> files)
    {
        HeldFile file = HeldFile.Open(path, maximumBytes, retainBytes, reason);
        files.Add(file);
        return file;
    }

    private static void AddDirectoryChain(
        string target,
        string protectedRoot,
        IDictionary<string, HeldDirectory> directories,
        string reason)
    {
        string fullTarget = Path.GetFullPath(target);
        string fullProtectedRoot = Path.GetFullPath(protectedRoot);
        string? volumeRoot = Path.GetPathRoot(fullTarget);
        if (string.IsNullOrEmpty(volumeRoot))
        {
            throw new InvalidOperationException(reason);
        }

        string current = volumeRoot;
        AddDirectory(current);
        string relative = Path.GetRelativePath(volumeRoot, fullTarget);
        if (relative != ".")
        {
            foreach (string component in relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, component);
                AddDirectory(current);
            }
        }

        void AddDirectory(string path)
        {
            string fullPath = Path.GetFullPath(path);
            if (directories.ContainsKey(fullPath))
            {
                return;
            }

            AclContract aclContract = SamePath(fullPath, fullProtectedRoot)
                ? AclContract.ProtectedTraverse
                : IsDescendantPath(fullPath, fullProtectedRoot)
                    ? AclContract.Protected
                    : AclContract.None;
            bool outsideProtectedRoot = aclContract == AclContract.None;
            directories.Add(fullPath, HeldDirectory.Open(
                fullPath,
                aclContract,
                reason,
                allowWriteSharing: outsideProtectedRoot,
                allowDeleteSharing: outsideProtectedRoot));
        }
    }

    private static DeploymentState LoadDeploymentState(
        string deploymentRoot,
        BrokerCore.ProviderLock providerLock,
        ICollection<HeldFile> ownedFiles)
    {
        string[] names = EnumerateDeploymentNames(deploymentRoot);
        var deploymentFiles = new List<HeldFile>(names.Length);
        var observations = new List<BrokerCore.DeploymentFileObservation>(names.Length);
        foreach (string name in names)
        {
            HeldFile file = AddHeldFile(
                Path.Combine(deploymentRoot, name), 268435456, false,
                "deployment_identity_drift", ownedFiles);
            deploymentFiles.Add(file);
            observations.Add(new BrokerCore.DeploymentFileObservation(file.Name, file.Length, file.Sha256));
        }

        var authenticode = new List<BrokerCore.AuthenticodeFileObservation>(providerLock.AuthenticodeNames.Count);
        foreach (string name in providerLock.AuthenticodeNames)
        {
            HeldFile? file = deploymentFiles.SingleOrDefault(
                candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
            if (file is null)
            {
                throw new InvalidOperationException("deployment_identity_drift");
            }

            authenticode.Add(VerifyAuthenticode(file));
        }

        BrokerCore.DeploymentEvidence evidence = BrokerCore.ComputeDeploymentEvidence(
            observations,
            providerLock.PayloadNames,
            providerLock.AuthenticodeNames,
            authenticode);
        var deployment = new DeploymentState(deploymentRoot, deploymentFiles, names, evidence);
        deployment.AssertStable();
        return deployment;
    }

    private static string[] EnumerateDeploymentNames(string deploymentRoot)
    {
        try
        {
            string[] entries = Directory.EnumerateFileSystemEntries(deploymentRoot)
                .Take(513)
                .Select(Path.GetFileName)
                .OfType<string>()
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            if (entries.Length == 0 || entries.Length > 512)
            {
                throw new InvalidOperationException("deployment_identity_drift");
            }

            return entries;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch
        {
            throw new InvalidOperationException("deployment_identity_drift");
        }
    }

    private static string GetRuntimeIdentifier() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "win-x64",
        Architecture.Arm64 => "win-arm64",
        _ => throw new InvalidOperationException("unsupported_context"),
    };

    private static bool SamePath(string left, string right) =>
        string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);

    private static bool IsDescendantPath(string path, string parent)
    {
        string normalizedParent = NormalizePath(parent) + Path.DirectorySeparatorChar;
        return NormalizePath(path).StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static async Task<CatalogSession> OpenProductionCatalogAsync(
        PackageManager manager,
        BrokerCore.ProviderContext expected,
        bool refresh = false)
    {
        IReadOnlyList<PackageCatalogReference> catalogs = manager.GetPackageCatalogs();
        var observedCatalogs = new List<BrokerCore.SourceCatalogObservation>(catalogs.Count);
        foreach (PackageCatalogReference candidate in catalogs)
        {
            ConfigureCatalogReference(candidate);
            observedCatalogs.Add(ObserveSourceReference(candidate));
        }

        PackageCatalogReference named = manager.GetPackageCatalogByName(expected.SourceName);
        if (named is null)
        {
            throw new InvalidOperationException("source_drift");
        }

        ConfigureCatalogReference(named);
        BrokerCore.SourceCatalogObservation namedSource = ObserveSourceReference(named);
        BrokerCore.SourceEvidence source = BrokerCore.ValidateSourceInventory(expected, observedCatalogs, namedSource);
        PackageCatalogReference reference = catalogs.Single();

        if (refresh)
        {
            RefreshPackageCatalogResult refreshed = await reference.RefreshPackageCatalogAsync();
            if (refreshed.Status != RefreshPackageCatalogStatus.Ok)
            {
                throw new InvalidOperationException("catalog_refresh_failed");
            }

            return await OpenProductionCatalogAsync(manager, expected, refresh: false).ConfigureAwait(false);
        }

        ConnectResult connected = await reference.ConnectAsync();
        if (connected.Status != ConnectResultStatus.Ok || connected.PackageCatalog is null ||
            connected.PackageCatalog.IsComposite)
        {
            throw new InvalidOperationException("catalog_connect_failed");
        }

        BrokerCore.SourceEvidence connectedSource = ObserveCatalog(connected.PackageCatalog);
        BrokerCore.ValidateSource(expected, connectedSource);
        return new CatalogSession(reference, connected.PackageCatalog, connectedSource, observedCatalogs.Count);
    }

    private static async Task<BrokerCore.SourceEvidence> ProvisionProductionCatalogAsync(
        PackageManager manager,
        BrokerCore.ProviderContext expected,
        Action markMutationStarted)
    {
        IReadOnlyList<PackageCatalogReference> catalogs = manager.GetPackageCatalogs();
        var observed = new List<BrokerCore.SourceCatalogObservation>(catalogs.Count);
        foreach (PackageCatalogReference catalog in catalogs)
        {
            observed.Add(ObserveSourceReference(catalog));
        }

        BrokerCore.SourceProvisioningPlan plan = BrokerCore.PlanSourceProvisioning(expected, observed);
        foreach (string name in plan.CatalogNamesToRemove)
        {
            var options = new RemovePackageCatalogOptions
            {
                Name = name,
                PreserveData = false,
            };
            markMutationStarted();
            RemovePackageCatalogResult result = await manager.RemovePackageCatalogAsync(options);
            if (result.Status != RemovePackageCatalogStatus.Ok)
            {
                throw new InvalidOperationException("source_provision_failed");
            }
        }

        if (plan.AddExpectedCatalog)
        {
            var options = new AddPackageCatalogOptions
            {
                Name = expected.SourceName,
                SourceUri = expected.SourceArgument,
                Type = expected.SourceType,
                TrustLevel = expected.SourceTrust == "trusted"
                    ? PackageCatalogTrustLevel.Trusted
                    : PackageCatalogTrustLevel.None,
                CustomHeader = string.Empty,
                Explicit = expected.SourceExplicit,
                Priority = 0,
            };
            markMutationStarted();
            AddPackageCatalogResult result = await manager.AddPackageCatalogAsync(options);
            if (result.Status != AddPackageCatalogStatus.Ok)
            {
                throw new InvalidOperationException("source_provision_failed");
            }
        }

        CatalogSession provisioned = await OpenProductionCatalogAsync(manager, expected, refresh: true).ConfigureAwait(false);
        return provisioned.Source;
    }

    private static async Task<PackageCatalog> OpenUpgradeCatalogAsync(
        PackageManager manager,
        CatalogSession protectedRemote)
    {
        // This is the sole composite use in the broker: one already-attested remote reference plus
        // the System installed catalog, constructed internally to attach InstalledVersion for MMD's
        // UpgradePackageAsync contract. No request or policy field can select catalogs or behavior.
        var options = new CreateCompositePackageCatalogOptions
        {
            CompositeSearchBehavior = CompositeSearchBehavior.AllCatalogs,
            InstalledScope = PackageInstallScope.System,
        };
        options.Catalogs.Add(protectedRemote.Reference);

        PackageCatalogReference reference = manager.CreateCompositePackageCatalog(options);
        if (reference is null || !reference.IsComposite)
        {
            throw new InvalidOperationException("package_object_shape_drift");
        }

        ConnectResult connected = await reference.ConnectAsync();
        if (connected.Status != ConnectResultStatus.Ok || connected.PackageCatalog is null ||
            !connected.PackageCatalog.IsComposite)
        {
            throw new InvalidOperationException("catalog_connect_failed");
        }

        return connected.PackageCatalog;
    }

    private static async Task<PackageCatalog> OpenInstalledCatalogAsync(PackageManager manager)
    {
        PackageCatalogReference reference = manager.GetLocalPackageCatalog(LocalPackageCatalog.InstalledPackages);
        if (reference is null || reference.IsComposite)
        {
            throw new InvalidOperationException("installed_state_drift");
        }

        reference.AdditionalPackageCatalogArguments = string.Empty;
        reference.AcceptSourceAgreements = false;
        reference.PackageCatalogBackgroundUpdateInterval = TimeSpan.Zero;
        reference.InstalledPackageInformationOnly = true;
        reference.AuthenticationArguments = null!;

        ConnectResult connected = await reference.ConnectAsync();
        if (connected.Status != ConnectResultStatus.Ok || connected.PackageCatalog is null ||
            connected.PackageCatalog.IsComposite)
        {
            throw new InvalidOperationException("installed_state_drift");
        }

        return connected.PackageCatalog;
    }

    private static void ConfigureCatalogReference(PackageCatalogReference reference)
    {
        if (reference is null || reference.IsComposite)
        {
            throw new InvalidOperationException("source_drift");
        }

        reference.AdditionalPackageCatalogArguments = string.Empty;
        reference.AcceptSourceAgreements = false;
        reference.PackageCatalogBackgroundUpdateInterval = TimeSpan.Zero;
        reference.InstalledPackageInformationOnly = false;
        reference.AuthenticationArguments = null!;

        if (reference.SourceAgreements.Count != 0)
        {
            throw new InvalidOperationException("source_agreement_required");
        }

        if (reference.AuthenticationInfo is null ||
            reference.AuthenticationInfo.AuthenticationType != AuthenticationType.None)
        {
            throw new InvalidOperationException("authentication_required");
        }
    }

    private static BrokerCore.SourceCatalogObservation ObserveSourceReference(PackageCatalogReference reference) =>
        new(
            ObserveInfo(reference.Info, reference.IsComposite),
            reference.IsComposite,
            reference.SourceAgreements.Count,
            reference.AuthenticationInfo?.AuthenticationType == AuthenticationType.None ? "none" : "required");

    private static BrokerCore.SourceEvidence ObserveCatalog(PackageCatalog catalog) =>
        ObserveInfo(catalog.Info, catalog.IsComposite);

    private static BrokerCore.SourceEvidence ObserveInfo(PackageCatalogInfo info, bool composite)
    {
        if (composite || info is null || string.IsNullOrEmpty(info.Argument))
        {
            throw new InvalidOperationException("source_drift");
        }

        string origin = info.Origin switch
        {
            PackageCatalogOrigin.Predefined => "predefined",
            PackageCatalogOrigin.User => "user",
            _ => throw new InvalidOperationException("source_drift"),
        };
        string trust = info.TrustLevel switch
        {
            PackageCatalogTrustLevel.None => "none",
            PackageCatalogTrustLevel.Trusted => "trusted",
            _ => throw new InvalidOperationException("source_drift"),
        };

        BrokerCore.EnsureResultAtom(info.Id);
        BrokerCore.EnsureResultAtom(info.Name);
        BrokerCore.EnsureResultAtom(info.Type);
        return new BrokerCore.SourceEvidence(
            info.Id,
            info.Name,
            info.Type,
            BrokerCore.Sha256Utf8(info.Argument),
            origin,
            trust,
            info.Explicit,
            info.LastUpdateTime.ToUnixTimeSeconds());
    }

    private static async Task<IReadOnlyList<BrokerCore.PackageEvidence>> ResolveInventoryAsync(
        PackageCatalog remoteCatalog,
        PackageCatalog installedCatalog,
        BrokerCore.ProviderContext context,
        IReadOnlyList<BrokerCore.WinGetConstraint> constraints)
    {
        var results = new List<BrokerCore.PackageEvidence>(constraints.Count);
        for (int index = 0; index < constraints.Count; index++)
        {
            BrokerCore.WinGetConstraint constraint = constraints[index];
            CatalogPackage remote = await FindExactAsync(remoteCatalog, constraint.PackageId, false).ConfigureAwait(false)
                ?? throw new InvalidOperationException("package_not_found");
            CatalogPackage? local = await FindExactAsync(installedCatalog, constraint.PackageId, true).ConfigureAwait(false);
            string installedVersion = "-";
            if (local?.InstalledVersion is not null)
            {
                ValidateInstalledVersion(local.InstalledVersion, constraint.PackageId);
                installedVersion = ValidateVersionForResult(local.InstalledVersion.Version);
            }

            if (constraint.Mode == BrokerCore.BrokerMode.Upgrade && local?.InstalledVersion is null)
            {
                results.Add(new BrokerCore.PackageEvidence(
                    index,
                    constraint.PolicyToken,
                    constraint.PackageId,
                    "-",
                    "-",
                    constraint.Architecture,
                    constraint.Locale,
                    "-",
                    "-",
                    "-"));
                continue;
            }

            (PackageVersionId versionId, PackageVersionInfo versionInfo) = SelectVersion(
                remote,
                constraint,
                local?.InstalledVersion,
                context.SourceId);
            (InstallOptions _, PackageInstallerInfo installer, string installerType, string nestedType) =
                ResolveInstaller(remote, versionId, versionInfo, context, constraint);
            results.Add(new BrokerCore.PackageEvidence(
                index,
                constraint.PolicyToken,
                constraint.PackageId,
                installedVersion,
                ValidateVersionForResult(versionId.Version),
                BrokerCore.NormalizeArchitecture(installer.Architecture.ToString()),
                installer.Locale,
                installerType,
                nestedType,
                "-"));
        }

        return results;
    }

    private static async Task<ResolvedMutation> ResolveMutationAsync(
        BrokerCore.BrokerMode mode,
        PackageCatalog protectedRemoteCatalog,
        PackageCatalog operationCatalog,
        PackageCatalog installedCatalog,
        BrokerCore.ProviderContext context,
        BrokerCore.SourceEvidence protectedSource,
        BrokerCore.WinGetConstraint constraint)
    {
        CatalogPackage protectedRemotePackage = await FindExactAsync(
            protectedRemoteCatalog,
            constraint.PackageId,
            false).ConfigureAwait(false)
            ?? throw new InvalidOperationException("package_not_found");
        CatalogPackage operationPackage = mode == BrokerCore.BrokerMode.Upgrade
            ? await FindExactAsync(operationCatalog, constraint.PackageId, false).ConfigureAwait(false)
                ?? throw new InvalidOperationException("package_not_found")
            : protectedRemotePackage;
        CatalogPackage? local = await FindExactAsync(installedCatalog, constraint.PackageId, true).ConfigureAwait(false);

        PackageVersionInfo? independentInstalled = local?.InstalledVersion;
        if (independentInstalled is not null)
        {
            ValidateInstalledVersion(independentInstalled, constraint.PackageId);
        }

        PackageVersionInfo? attachedInstalled = operationPackage.InstalledVersion;
        BrokerCore.ValidateOperationPackageShape(
            mode,
            new BrokerCore.OperationPackageShape(
                operationCatalog.IsComposite,
                1,
                attachedInstalled is not null,
                mode == BrokerCore.BrokerMode.Upgrade ? "all-catalogs-fixed" : "none",
                true));

        if ((mode == BrokerCore.BrokerMode.Install && independentInstalled is not null) ||
            (mode == BrokerCore.BrokerMode.Upgrade && independentInstalled is null))
        {
            throw new InvalidOperationException("installed_state_drift");
        }

        if (mode == BrokerCore.BrokerMode.Upgrade)
        {
            ValidateInstalledVersion(attachedInstalled!, constraint.PackageId);
            if (attachedInstalled!.Id != independentInstalled!.Id ||
                attachedInstalled.Version != independentInstalled.Version)
            {
                throw new InvalidOperationException("installed_state_drift");
            }
        }

        (PackageVersionId remoteVersionId, PackageVersionInfo remoteVersionInfo) = SelectVersion(
            protectedRemotePackage,
            constraint,
            independentInstalled,
            context.SourceId);
        (InstallOptions remoteOptions, PackageInstallerInfo remoteInstaller, string remoteInstallerType, string remoteNestedType) =
            ResolveInstaller(protectedRemotePackage, remoteVersionId, remoteVersionInfo, context, constraint);

        PackageVersionId versionId = remoteVersionId;
        InstallOptions options = remoteOptions;
        PackageInstallerInfo installer = remoteInstaller;
        string installerType = remoteInstallerType;
        string nestedType = remoteNestedType;
        if (mode == BrokerCore.BrokerMode.Upgrade)
        {
            (PackageVersionId associatedVersionId, PackageVersionInfo associatedVersionInfo) = SelectVersion(
                operationPackage,
                constraint,
                independentInstalled,
                context.SourceId);
            (InstallOptions associatedOptions, PackageInstallerInfo associatedInstaller,
                string associatedInstallerType, string associatedNestedType) = ResolveInstaller(
                    operationPackage,
                    associatedVersionId,
                    associatedVersionInfo,
                    context,
                    constraint);
            if (associatedVersionId.PackageCatalogId != remoteVersionId.PackageCatalogId ||
                associatedVersionId.Version != remoteVersionId.Version ||
                associatedVersionId.Channel != remoteVersionId.Channel ||
                associatedInstallerType != remoteInstallerType || associatedNestedType != remoteNestedType ||
                associatedInstaller.Scope != remoteInstaller.Scope ||
                associatedInstaller.Architecture != remoteInstaller.Architecture ||
                associatedInstaller.Locale != remoteInstaller.Locale)
            {
                throw new InvalidOperationException("package_state_drift");
            }

            versionId = associatedVersionId;
            options = associatedOptions;
            installer = associatedInstaller;
            installerType = associatedInstallerType;
            nestedType = associatedNestedType;
        }

        string installedVersion = independentInstalled is null
            ? "-"
            : ValidateVersionForResult(independentInstalled.Version);
        string candidateVersion = ValidateVersionForResult(versionId.Version);
        string architecture = BrokerCore.NormalizeArchitecture(installer.Architecture.ToString());
        var evidence = new BrokerCore.PackageEvidence(
            0,
            constraint.PolicyToken,
            constraint.PackageId,
            installedVersion,
            candidateVersion,
            architecture,
            installer.Locale,
            installerType,
            nestedType,
            "-");
        string preState = BrokerCore.ComputePackageStateSha256(
            constraint.PackageId,
            installedVersion,
            candidateVersion,
            architecture,
            installer.Locale,
            installerType,
            BrokerCore.ComputeSourceStateSha256(protectedSource));
        return new ResolvedMutation(operationPackage, versionId, options, evidence, preState);
    }

    private static async Task<CatalogPackage?> FindExactAsync(
        PackageCatalog catalog,
        string packageId,
        bool allowAbsent)
    {
        var options = new FindPackagesOptions { ResultLimit = 2 };
        options.Selectors.Add(new PackageMatchFilter
        {
            Field = PackageMatchField.Id,
            Option = PackageFieldMatchOption.Equals,
            Value = packageId,
        });
        FindPackagesResult result = await catalog.FindPackagesAsync(options);
        if (result.Status != FindPackagesResultStatus.Ok)
        {
            throw new InvalidOperationException("catalog_search_failed");
        }

        if (result.Matches.Count == 0 && !result.WasLimitExceeded)
        {
            if (allowAbsent)
            {
                return null;
            }

            throw new InvalidOperationException("package_not_found");
        }

        if (result.Matches.Any(match => match.CatalogPackage is null))
        {
            throw new InvalidOperationException("ambiguous_package");
        }

        BrokerCore.ValidateExactPackageMatch(
            packageId,
            result.Matches.Select(match => match.CatalogPackage!.Id).ToArray(),
            result.WasLimitExceeded);

        MatchResult match = result.Matches[0];
        if (match.MatchCriteria.Field != PackageMatchField.Id ||
            match.MatchCriteria.Option != PackageFieldMatchOption.Equals ||
            match.MatchCriteria.Value != packageId)
        {
            throw new InvalidOperationException("ambiguous_package");
        }

        return match.CatalogPackage;
    }

    private static (PackageVersionId Id, PackageVersionInfo Info) SelectVersion(
        CatalogPackage package,
        BrokerCore.WinGetConstraint constraint,
        PackageVersionInfo? installed,
        string sourceId)
    {
        var versions = new Dictionary<int, (PackageVersionId Id, PackageVersionInfo Info)>();
        var observations = new List<BrokerCore.VersionCandidateObservation>(package.AvailableVersions.Count);
        for (int index = 0; index < package.AvailableVersions.Count; index++)
        {
            PackageVersionId id = package.AvailableVersions[index];
            PackageVersionInfo info = package.GetPackageVersionInfo(id);
            if (info.Id != constraint.PackageId || info.Version != id.Version ||
                info.Channel != id.Channel)
            {
                throw new InvalidOperationException("package_state_drift");
            }

            versions.Add(index, (id, info));
            observations.Add(new BrokerCore.VersionCandidateObservation(
                index,
                id.Version,
                id.PackageCatalogId,
                id.Channel,
                MapComparison(info.CompareToVersion(constraint.MinimumVersion)),
                MapComparison(info.CompareToVersion(constraint.MaximumVersion)),
                installed is null
                    ? BrokerCore.ProviderComparison.Unknown
                    : MapComparison(info.CompareToVersion(installed.Version))));
        }

        int selected = BrokerCore.SelectVersionCandidate(
            constraint,
            sourceId,
            observations,
            (left, right) => MapComparison(versions[left].Info.CompareToVersion(versions[right].Id.Version)));
        return versions[selected];
    }

    private static BrokerCore.ProviderComparison MapComparison(CompareResult result) => result switch
    {
        CompareResult.Lesser => BrokerCore.ProviderComparison.Lesser,
        CompareResult.Equal => BrokerCore.ProviderComparison.Equal,
        CompareResult.Greater => BrokerCore.ProviderComparison.Greater,
        _ => BrokerCore.ProviderComparison.Unknown,
    };

    private static (InstallOptions Options, PackageInstallerInfo Installer, string Type, string NestedType) ResolveInstaller(
        CatalogPackage package,
        PackageVersionId versionId,
        PackageVersionInfo versionInfo,
        BrokerCore.ProviderContext context,
        BrokerCore.WinGetConstraint constraint)
    {
        BrokerCore.SourceEvidence versionSource = ObserveCatalog(versionInfo.PackageCatalog);
        BrokerCore.ValidateSource(context, versionSource);
        if (versionId.PackageCatalogId != context.SourceId)
        {
            throw new InvalidOperationException("source_drift");
        }

        var options = new InstallOptions
        {
            PackageVersionId = versionId,
            PreferredInstallLocation = string.Empty,
            PackageInstallScope = PackageInstallScope.System,
            PackageInstallMode = PackageInstallMode.Silent,
            LogOutputPath = string.Empty,
            AllowHashMismatch = false,
            ReplacementInstallerArguments = string.Empty,
            CorrelationData = string.Empty,
            AdditionalPackageCatalogArguments = string.Empty,
            AllowUpgradeToUnknownVersion = false,
            Force = false,
            AdditionalInstallerArguments = string.Empty,
            AcceptPackageAgreements = false,
            BypassIsStoreClientBlockedPolicyCheck = false,
            SkipDependencies = false,
            InstallerType = PackageInstallerType.Unknown,
            AuthenticationArguments = null!,
        };
        options.AllowedArchitectures.Clear();
        options.AllowedArchitectures.Add(ToProcessorArchitecture(constraint.Architecture));

        PackageInstallerInfo installer = versionInfo.GetApplicableInstaller(options)
            ?? throw new InvalidOperationException("inapplicable_installer");
        (string installerType, string nestedType) = ValidateInstaller(installer, versionInfo, constraint);
        options.InstallerType = installer.InstallerType;
        PackageInstallerInfo pinnedInstaller = versionInfo.GetApplicableInstaller(options)
            ?? throw new InvalidOperationException("inapplicable_installer");
        (string pinnedType, string pinnedNestedType) = ValidateInstaller(pinnedInstaller, versionInfo, constraint);
        if (pinnedType != installerType || pinnedNestedType != nestedType ||
            pinnedInstaller.Architecture != installer.Architecture ||
            pinnedInstaller.Locale != installer.Locale || pinnedInstaller.Scope != installer.Scope)
        {
            throw new InvalidOperationException("inapplicable_installer");
        }

        return (options, pinnedInstaller, pinnedType, pinnedNestedType);
    }

    private static (string Type, string NestedType) ValidateInstaller(
        PackageInstallerInfo installer,
        PackageVersionInfo version,
        BrokerCore.WinGetConstraint constraint)
    {
        string architecture = BrokerCore.NormalizeArchitecture(installer.Architecture.ToString());
        string installerType = BrokerCore.NormalizeInstallerType(installer.InstallerType.ToString());
        string nestedType = installer.NestedInstallerType == PackageInstallerType.Unknown
            ? "-"
            : BrokerCore.NormalizeInstallerType(installer.NestedInstallerType.ToString());
        CatalogPackageMetadata metadata = version.GetCatalogPackageMetadata(constraint.Locale);
        if (metadata is null || metadata.Locale != constraint.Locale)
        {
            throw new InvalidOperationException("inapplicable_installer");
        }

        BrokerCore.InstallerObservation observed = BrokerCore.ValidateInstallerObservation(
            constraint,
            new BrokerCore.InstallerObservation(
                installer.Scope == PackageInstallerScope.System ? "system" : "other",
                architecture,
                installer.Locale,
                installerType,
                nestedType,
                installer.AuthenticationInfo?.AuthenticationType == AuthenticationType.None ? "none" : "required",
                metadata.Agreements.Count));
        return (observed.InstallerType, observed.NestedInstallerType);
    }

    private static BrokerCore.DangerousOptionsObservation AssertDangerousOptionsFixed(
        InstallOptions options,
        PackageVersionId expectedVersion,
        BrokerCore.WinGetConstraint constraint)
    {
        if (options.PackageVersionId.PackageCatalogId != expectedVersion.PackageCatalogId ||
            options.PackageVersionId.Version != expectedVersion.Version ||
            options.PackageVersionId.Channel != expectedVersion.Channel)
        {
            throw new InvalidOperationException("dangerous_option_drift");
        }

        var observation = new BrokerCore.DangerousOptionsObservation(
            options.PackageVersionId.PackageCatalogId,
            options.PackageVersionId.Version,
            options.PackageVersionId.Channel,
            options.PackageInstallScope == PackageInstallScope.System ? "system" : "other",
            options.PackageInstallMode == PackageInstallMode.Silent ? "silent" : "other",
            options.AllowHashMismatch,
            options.AllowUpgradeToUnknownVersion,
            options.Force,
            options.AcceptPackageAgreements,
            options.BypassIsStoreClientBlockedPolicyCheck,
            options.SkipDependencies,
            options.AuthenticationArguments is not null,
            options.PreferredInstallLocation,
            options.LogOutputPath,
            options.ReplacementInstallerArguments,
            options.AdditionalInstallerArguments,
            options.AdditionalPackageCatalogArguments,
            options.CorrelationData,
            options.AllowedArchitectures
                .Select(architecture => BrokerCore.NormalizeArchitecture(architecture.ToString()))
                .ToArray(),
            options.InstallerType == PackageInstallerType.Unknown
                ? "unknown"
                : BrokerCore.NormalizeInstallerType(options.InstallerType.ToString()));
        BrokerCore.ValidateDangerousOptions(
            constraint,
            expectedVersion.PackageCatalogId,
            expectedVersion.Version,
            observation);
        return observation;
    }

    private static void ValidateInstalledVersion(PackageVersionInfo version, string expectedPackageId)
    {
        string scope = version.GetMetadata(PackageVersionMetadataField.InstalledScope);
        if (version.Id != expectedPackageId ||
            (!string.Equals(scope, "Machine", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(scope, "System", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("installed_state_drift");
        }

        _ = ValidateVersionForResult(version.Version);
    }

    private static string ValidateVersionForResult(string version)
    {
        if (!BrokerCore.IsVersion(version))
        {
            throw new InvalidOperationException("unsupported_version");
        }

        return version;
    }

    private static ProcessorArchitecture ToProcessorArchitecture(string architecture) => architecture switch
    {
        "x86" => ProcessorArchitecture.X86,
        "x64" => ProcessorArchitecture.X64,
        "arm" => ProcessorArchitecture.Arm,
        "arm64" => ProcessorArchitecture.Arm64,
        "neutral" => ProcessorArchitecture.Neutral,
        _ => throw new InvalidOperationException("unsupported_architecture"),
    };

    private static void AssertProtectedStateStable(
        ProtectedState state,
        ProviderRuntimeState runtimeState,
        ProviderSettingsState? settingsState = null)
    {
        state.AssertStable();
        runtimeState.AssertStable();
        settingsState?.AssertStable();
    }

    private static (byte[]? Bytes, string Sha256, long Length) ReadHeldFile(
        FileStream stream,
        int maximumBytes,
        bool retainBytes,
        string reason)
    {
        try
        {
            stream.Position = 0;
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using MemoryStream? retained = retainBytes ? new MemoryStream(Math.Min(maximumBytes, 16384)) : null;
            byte[] block = new byte[65536];
            long length = 0;
            while (true)
            {
                int read = stream.Read(block, 0, block.Length);
                if (read == 0)
                {
                    break;
                }

                length = checked(length + read);
                if (length > maximumBytes)
                {
                    throw new InvalidOperationException(reason);
                }

                hash.AppendData(block, 0, read);
                retained?.Write(block, 0, read);
            }

            stream.Position = 0;
            return (
                retained?.ToArray(),
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                length);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch
        {
            throw new InvalidOperationException(reason);
        }
    }

    private static FileIdentity ReadIdentity(
        SafeFileHandle handle,
        string expectedPath,
        bool expectDirectory,
        bool requireSingleLink,
        string reason)
    {
        try
        {
            if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
            {
                throw new InvalidOperationException(reason);
            }

            FileAttributes attributes = (FileAttributes)information.FileAttributes;
            bool isDirectory = (attributes & FileAttributes.Directory) != 0;
            if ((attributes & FileAttributes.ReparsePoint) != 0 || isDirectory != expectDirectory ||
                (requireSingleLink && information.NumberOfLinks != 1) ||
                !SamePath(ReadFinalPath(handle, reason), expectedPath))
            {
                throw new InvalidOperationException(reason);
            }

            long length = ((long)information.FileSizeHigh << 32) | information.FileSizeLow;
            ulong fileIndex = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
            return new FileIdentity(
                information.VolumeSerialNumber,
                fileIndex,
                information.NumberOfLinks,
                length,
                attributes);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch
        {
            throw new InvalidOperationException(reason);
        }
    }

    private static string ReadFinalPath(SafeFileHandle handle, string reason)
    {
        var builder = new StringBuilder(512);
        uint length = GetFinalPathNameByHandleW(handle, builder, (uint)builder.Capacity, 0);
        if (length == 0)
        {
            throw new InvalidOperationException(reason);
        }

        if (length >= builder.Capacity)
        {
            builder = new StringBuilder(checked((int)length + 1));
            length = GetFinalPathNameByHandleW(handle, builder, (uint)builder.Capacity, 0);
            if (length == 0 || length >= builder.Capacity)
            {
                throw new InvalidOperationException(reason);
            }
        }

        string finalPath = builder.ToString();
        if (finalPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + finalPath[8..];
        }

        return finalPath.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
            ? finalPath[4..]
            : finalPath;
    }

    private static string ValidateAcl(SafeFileHandle handle, AclContract contract, string reason)
    {
        IntPtr descriptor = IntPtr.Zero;
        try
        {
            uint status = GetSecurityInfo(
                handle,
                SeObjectType.File,
                SecurityInformation.Owner | SecurityInformation.Group | SecurityInformation.Dacl,
                out _,
                out _,
                out _,
                out _,
                out descriptor);
            if (status != 0 || descriptor == IntPtr.Zero)
            {
                throw new InvalidOperationException(reason);
            }

            uint descriptorLength = GetSecurityDescriptorLength(descriptor);
            if (descriptorLength == 0 || descriptorLength > 65536)
            {
                throw new InvalidOperationException(reason);
            }

            byte[] bytes = new byte[descriptorLength];
            Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            var security = new RawSecurityDescriptor(bytes, 0);
            string systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value;
            string administratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value;
            string owner = security.Owner?.Value ?? string.Empty;
            string group = security.Group?.Value ?? string.Empty;
            bool providerSecure = contract == AclContract.ProviderSecure;
            bool providerStandard = contract == AclContract.ProviderStandard;
            if ((providerSecure
                    ? owner != administratorsSid
                    : providerStandard ? owner != systemSid : owner != systemSid && owner != administratorsSid) ||
                (group != systemSid && group != administratorsSid) ||
                security.DiscretionaryAcl is null ||
                (security.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0 ||
                (security.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0)
            {
                throw new InvalidOperationException(reason);
            }

            int systemCount = 0;
            int administratorsCount = 0;
            int traverseCount = 0;
            AceFlags providerAceFlags = AceFlags.ObjectInherit | AceFlags.ContainerInherit;
            var canonicalAces = new List<string>(security.DiscretionaryAcl.Count);
            foreach (GenericAce genericAce in security.DiscretionaryAcl)
            {
                if (genericAce is not CommonAce ace || ace.AceQualifier != AceQualifier.AccessAllowed ||
                    (ace.AceFlags & (AceFlags.InheritOnly | AceFlags.Inherited)) != 0 ||
                    ((providerStandard || providerSecure) && ace.AceFlags != providerAceFlags))
                {
                    throw new InvalidOperationException(reason);
                }

                string sid = ace.SecurityIdentifier.Value;
                if (sid == systemSid && ace.AccessMask == FullControlMask)
                {
                    systemCount++;
                }
                else if (sid == administratorsSid && ace.AccessMask == FullControlMask)
                {
                    administratorsCount++;
                }
                else if (contract == AclContract.ProtectedTraverse && ace.AccessMask == TraverseReadMask &&
                    sid != systemSid && sid != administratorsSid)
                {
                    traverseCount++;
                }
                else
                {
                    throw new InvalidOperationException(reason);
                }

                canonicalAces.Add($"{sid}|{ace.AccessMask:x8}|{(int)ace.AceFlags:x4}");
            }

            if (systemCount != 1 ||
                (providerSecure ? administratorsCount != 0 : administratorsCount != 1) ||
                (contract == AclContract.ProtectedTraverse ? traverseCount != 1 : traverseCount != 0))
            {
                throw new InvalidOperationException(reason);
            }

            canonicalAces.Sort(StringComparer.Ordinal);
            return $"{owner}|{group}|{(int)security.ControlFlags:x8}|{string.Join(';', canonicalAces)}";
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch
        {
            throw new InvalidOperationException(reason);
        }
        finally
        {
            if (descriptor != IntPtr.Zero)
            {
                _ = LocalFree(descriptor);
            }
        }
    }

    private static BrokerCore.AuthenticodeFileObservation VerifyAuthenticode(HeldFile file)
    {
        IntPtr fileInfoPointer = IntPtr.Zero;
        try
        {
            var fileInfo = new WinTrustFileInfo
            {
                CbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = file.Path,
                FileHandle = file.Handle.DangerousGetHandle(),
                KnownSubject = IntPtr.Zero,
            };
            fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);

            var trustData = new WinTrustData
            {
                CbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = 2,
                RevocationChecks = 0,
                UnionChoice = 1,
                FileInfo = fileInfoPointer,
                StateAction = 0,
                ProviderFlags = 0x00001010,
                UiContext = 0,
            };
            Guid action = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            if (WinVerifyTrust(new IntPtr(-1), ref action, ref trustData) != 0)
            {
                throw new InvalidOperationException("deployment_identity_drift");
            }

#pragma warning disable SYSLIB0057
            using X509Certificate certificate = X509Certificate.CreateFromSignedFile(file.Path);
#pragma warning restore SYSLIB0057
            string certificateSha256 = BrokerCore.Sha256(certificate.GetRawCertData());
            file.AssertStable("deployment_identity_drift");
            return new BrokerCore.AuthenticodeFileObservation(file.Name, file.Sha256, certificateSha256);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch
        {
            throw new InvalidOperationException("deployment_identity_drift");
        }
        finally
        {
            if (fileInfoPointer != IntPtr.Zero)
            {
                Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
                Marshal.FreeHGlobal(fileInfoPointer);
            }
        }
    }

    private static byte[] ReadStandardInput() => ReadBounded(Console.OpenStandardInput(), BrokerCore.MaxRequestBytes);

    private static byte[] ReadBounded(Stream stream, int maximumBytes)
    {
        using var buffer = new MemoryStream(Math.Min(maximumBytes, 16384));
        byte[] block = new byte[4096];
        while (true)
        {
            int read = stream.Read(block, 0, block.Length);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maximumBytes)
            {
                throw new FormatException("bounded_input_exceeded");
            }

            buffer.Write(block, 0, read);
        }

        return buffer.ToArray();
    }

    private static void AssertSystemContext()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("unsupported_context");
        }

        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem)
        {
            throw new InvalidOperationException("unsupported_context");
        }
    }

    private static void SanitizeProcessEnvironment()
    {
        foreach (string name in new[]
        {
            "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY",
            "http_proxy", "https_proxy", "all_proxy", "no_proxy",
            "WINGET_DISABLE_INTERACTIVITY", "WINGET_CONFIG_HOME",
        })
        {
            Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.Process);
        }
    }

    private static string FormatHResult(Exception? exception) =>
        $"0x{unchecked((uint)(exception?.HResult ?? 0)):x8}";

    private static string MapReason(Exception exception)
    {
        string message = exception.Message;
        return KnownReasons.Contains(message) ? message : "provider_failure";
    }

    private static BrokerCore.HelperResult BuildResult(
        BrokerCore.HelperRequest request,
        ProtectedState? state,
        BrokerCore.TerminalState terminalState,
        string reason,
        BrokerCore.SourceEvidence? source,
        IReadOnlyList<BrokerCore.PackageEvidence> packages,
        ProviderRuntimeState? runtimeState,
        string settingsSha256,
        string providerVersion,
        string providerStatus,
        string providerExtendedError,
        string providerInstallerError,
        bool rebootRequired,
        string preStateSha256,
        string postStateSha256) =>
        new(
            request.RequestId,
            request.ActionId,
            terminalState,
            BrokerCore.NormalizeReason(reason),
            state?.ProviderLockFile.Sha256 ?? "-",
            state?.Deployment.Evidence.FileSetSha256 ?? "-",
            state?.Deployment.Evidence.AuthenticodeIdentitySha256 ?? "-",
            providerVersion,
            state is null ? "-" : BrokerCore.Sha256Utf8(state.Context.StateIdentifier),
            runtimeState?.EvidenceSha256 ?? "-",
            settingsSha256,
            source,
            packages,
            providerStatus,
            providerExtendedError,
            providerInstallerError,
            rebootRequired,
            preStateSha256,
            postStateSha256);

    private static BrokerCore.ProviderProvisionResult BuildProvisionResult(
        BrokerCore.ProvisionRequest request,
        ProtectedState? state,
        ProviderRuntimeState? runtimeState,
        BrokerCore.TerminalState terminalState,
        string reason,
        string providerVersion,
        string settingsSha256,
        BrokerCore.SourceEvidence? source) =>
        new(
            terminalState,
            BrokerCore.NormalizeReason(reason),
            request.EnrollmentEpoch,
            state?.Pointer.GenerationSha256 ?? "-",
            state?.ProviderLockFile.Sha256 ?? "-",
            state?.Deployment.Evidence.FileSetSha256 ?? "-",
            state?.Deployment.Evidence.AuthenticodeIdentitySha256 ?? "-",
            providerVersion,
            state is null ? "-" : BrokerCore.Sha256Utf8(state.Context.StateIdentifier),
            runtimeState?.EvidenceSha256 ?? "-",
            settingsSha256,
            source);

    private static void WriteResult(BrokerCore.HelperResult result)
    {
        string text = BrokerCore.RenderResult(result);
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(text);
        using Stream output = Console.OpenStandardOutput();
        output.Write(bytes, 0, bytes.Length);
        output.Flush();
    }

    private static void WritePreconditionProbeResult(BrokerCore.PreconditionProbeResult result)
    {
        string text = BrokerCore.RenderPreconditionProbeResult(result);
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        using Stream output = Console.OpenStandardOutput();
        output.Write(bytes, 0, bytes.Length);
        output.Flush();
    }

    private static void WriteProvisionResult(BrokerCore.ProviderProvisionResult result)
    {
        string text = BrokerCore.RenderProvisionResult(result);
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        using Stream output = Console.OpenStandardOutput();
        output.Write(bytes, 0, bytes.Length);
        output.Flush();
    }

    private enum SeObjectType : uint
    {
        File = 1,
    }

    [Flags]
    private enum SecurityInformation : uint
    {
        Owner = 0x00000001,
        Group = 0x00000002,
        Dacl = 0x00000004,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal uint Length;
        internal IntPtr SecurityDescriptor;
        internal int InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        internal uint CbStruct;

        [MarshalAs(UnmanagedType.LPWStr)]
        internal string FilePath;

        internal IntPtr FileHandle;
        internal IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        internal uint CbStruct;
        internal IntPtr PolicyCallbackData;
        internal IntPtr SipClientData;
        internal uint UiChoice;
        internal uint RevocationChecks;
        internal uint UnionChoice;
        internal IntPtr FileInfo;
        internal uint StateAction;
        internal IntPtr StateData;
        internal IntPtr UrlReference;
        internal uint ProviderFlags;
        internal uint UiContext;
        internal IntPtr SignatureSettings;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(
        string path,
        ref SecurityAttributes securityAttributes);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string stringSecurityDescriptor,
        uint stringSdRevision,
        out IntPtr securityDescriptor,
        out uint securityDescriptorSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(
        SafeFileHandle handle,
        SeObjectType objectType,
        SecurityInformation securityInformation,
        out IntPtr owner,
        out IntPtr group,
        out IntPtr dacl,
        out IntPtr sacl,
        out IntPtr securityDescriptor);

    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityDescriptorLength(IntPtr securityDescriptor);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int WinVerifyTrust(
        IntPtr window,
        ref Guid action,
        ref WinTrustData trustData);
}
