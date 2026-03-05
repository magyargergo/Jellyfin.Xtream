// Copyright (C) 2025  Gergo Magyar
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Xtream.Service.Streaming.Native;

/// <summary>
/// SafeHandle for native ChannelRegistry.
/// </summary>
internal sealed class ChannelRegistrySafeHandle : SafeHandle
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelRegistrySafeHandle"/> class.
    /// </summary>
    public ChannelRegistrySafeHandle()
        : base(IntPtr.Zero, ownsHandle: true) { }

    /// <inheritdoc/>
    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        NativeRegistryMethods.RegistryDestroy(handle);
        return true;
    }

    /// <summary>
    /// Creates a new registry handle.
    /// </summary>
    public static ChannelRegistrySafeHandle Create()
    {
        var safeHandle = new ChannelRegistrySafeHandle();
        var rawHandle = NativeRegistryMethods.RegistryCreate();
        safeHandle.SetHandle(rawHandle);
        return safeHandle;
    }
}

/// <summary>
/// P/Invoke declarations for the channel registry native API.
/// </summary>
internal static partial class NativeRegistryMethods
{
    private const string LibraryName = "tsduck_interop";

    [LibraryImport(LibraryName, EntryPoint = "tsduck_registry_create")]
    internal static partial nint RegistryCreate();

    [LibraryImport(LibraryName, EntryPoint = "tsduck_registry_destroy")]
    internal static partial void RegistryDestroy(nint registry);

    [LibraryImport(LibraryName, EntryPoint = "tsduck_registry_add_provider")]
    internal static partial int RegistryAddProvider(nint registry, in RegistryProviderInfoNative info);

    [LibraryImport(LibraryName, EntryPoint = "tsduck_registry_add_stream", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int RegistryAddStream(
        nint registry,
        int providerIndex,
        int streamId,
        string name,
        string? iconUrl
    );

    [LibraryImport(LibraryName, EntryPoint = "tsduck_registry_build")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegistryBuild(nint registry);

    [LibraryImport(LibraryName, EntryPoint = "tsduck_registry_get_stats")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegistryGetStats(nint registry, out RegistryStatsNative stats);

    // Rebuild (health-preserving)

    [LibraryImport(LibraryName, EntryPoint = "tsduck_registry_begin_rebuild")]
    internal static partial void RegistryBeginRebuild(nint registry);

    [LibraryImport(LibraryName, EntryPoint = "tsduck_registry_rebuild")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegistryRebuild(nint registry);

    // Channel enumeration

    [LibraryImport(LibraryName, EntryPoint = "tsduck_registry_get_channel_count")]
    internal static partial int RegistryGetChannelCount(nint registry);

    [LibraryImport(LibraryName, EntryPoint = "tsduck_registry_enumerate_channels")]
    internal static unsafe partial int RegistryEnumerateChannels(
        nint registry,
        RegistryChannelListEntryNative* outEntries,
        int maxCount,
        int offset
    );

    [LibraryImport(LibraryName, EntryPoint = "tsduck_registry_get_channel_info")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool RegistryGetChannelInfo(
        nint registry,
        long guidHigh,
        long guidLow,
        RegistryChannelListEntryNative* outEntry
    );

    // GUID alias registration (for C# ToProviderGuid → native channel lookup)

    [LibraryImport(LibraryName, EntryPoint = "tsduck_registry_add_guid_alias")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegistryAddGuidAlias(
        nint registry,
        long aliasHigh,
        long aliasLow,
        int providerIndex,
        int streamId
    );

    // API health reporting

    [LibraryImport(LibraryName, EntryPoint = "tsduck_registry_report_api_success")]
    internal static partial void RegistryReportApiSuccess(nint registry, int providerIndex, int latencyMs);

    [LibraryImport(LibraryName, EntryPoint = "tsduck_registry_report_api_failure")]
    internal static partial void RegistryReportApiFailure(nint registry, int providerIndex, int errorType);

    // Provider status

    [LibraryImport(LibraryName, EntryPoint = "tsduck_registry_get_provider_status")]
    internal static unsafe partial int RegistryGetProviderStatus(
        nint registry,
        RegistryProviderStatusNative* outStatus,
        int maxCount
    );

    // Health event callback

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void HealthCallbackDelegate(int providerIndex, IntPtr eventType, IntPtr details, IntPtr userData);

    [LibraryImport(LibraryName, EntryPoint = "tsduck_registry_set_health_callback")]
    internal static partial void RegistrySetHealthCallback(nint registry, IntPtr callback, IntPtr userData);
}

/// <summary>
/// Native channel-to-provider registry for IPTV streaming.
/// C# side only handles setup; C++ handles all runtime decisions (health, failover, quality).
/// </summary>
/// <remarks>
/// <para>
/// Usage pattern:
/// 1. Create registry
/// 2. Add providers (returns provider index)
/// 3. Add streams for each provider
/// 4. Build (normalizes names, generates GUIDs, sorts by quality)
/// 5. Pass to native streamer for runtime URL selection
/// </para>
/// </remarks>
public sealed class NativeChannelRegistry : IDisposable
{
    private readonly ChannelRegistrySafeHandle _handle;
    private readonly ILogger? _logger;
    private volatile bool _disposed;
    private bool _built;

    /// <summary>
    /// Initializes a new instance of the <see cref="NativeChannelRegistry"/> class.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <exception cref="TsDuckNativeException">Thrown if native registry creation fails.</exception>
    public NativeChannelRegistry(ILogger? logger = null)
    {
        _logger = logger;
        _handle = ChannelRegistrySafeHandle.Create();

        if (_handle.IsInvalid)
        {
            throw new TsDuckNativeException("Failed to create native channel registry");
        }

        _logger?.LogDebugIfEnabled("NativeChannelRegistry created");
    }

    /// <summary>
    /// Gets whether the registry has been built.
    /// </summary>
    public bool IsBuilt => _built;

    /// <summary>
    /// Adds a provider to the registry.
    /// </summary>
    /// <param name="id">Provider ID (max 15 chars).</param>
    /// <param name="name">Display name (max 63 chars).</param>
    /// <param name="baseUrl">Base URL for Xtream API.</param>
    /// <param name="username">Username for auth.</param>
    /// <param name="password">Password for auth.</param>
    /// <param name="priority">Priority (0 = highest).</param>
    /// <param name="idHash">Hash for GUID generation.</param>
    /// <param name="initialHealth">Initial health score (0-100).</param>
    /// <returns>Provider index for use in AddStream.</returns>
    /// <exception cref="InvalidOperationException">Thrown if registry is already built.</exception>
    public int AddProvider(
        string id,
        string name,
        string baseUrl,
        string username,
        string password,
        int priority,
        int idHash,
        double initialHealth = 50.0
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_built)
        {
            throw new InvalidOperationException("Cannot add providers after build");
        }

        var info = RegistryProviderInfoNative.Create(
            id,
            name,
            baseUrl,
            username,
            password,
            priority,
            idHash,
            initialHealth
        );

        var index = NativeRegistryMethods.RegistryAddProvider(_handle.DangerousGetHandle(), in info);

        if (index < 0)
        {
            throw new InvalidOperationException($"Failed to add provider '{name}': error {index}");
        }

        _logger?.LogDebugIfEnabled("Added provider '{Name}' at index {Index}", name, index);
        return index;
    }

    /// <summary>
    /// Adds a stream to the registry.
    /// </summary>
    /// <param name="providerIndex">Index from AddProvider.</param>
    /// <param name="streamId">Provider's stream ID.</param>
    /// <param name="name">Raw channel name (will be normalized).</param>
    /// <param name="iconUrl">Optional icon URL.</param>
    /// <exception cref="InvalidOperationException">Thrown if registry is built or parameters invalid.</exception>
    public void AddStream(int providerIndex, int streamId, string name, string? iconUrl = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_built)
        {
            throw new InvalidOperationException("Cannot add streams after build");
        }

        var result = NativeRegistryMethods.RegistryAddStream(
            _handle.DangerousGetHandle(),
            providerIndex,
            streamId,
            name,
            iconUrl
        );

        if (result < 0)
        {
            throw new InvalidOperationException($"Failed to add stream '{name}': error {result}");
        }
    }

    /// <summary>
    /// Builds the registry. After this, no more add calls allowed.
    /// Performs channel name normalization, quality scoring, and GUID generation.
    /// </summary>
    /// <returns>Build statistics.</returns>
    /// <exception cref="InvalidOperationException">Thrown if already built.</exception>
    public RegistryStats Build()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_built)
        {
            throw new InvalidOperationException("Registry already built");
        }

        if (!NativeRegistryMethods.RegistryBuild(_handle.DangerousGetHandle()))
        {
            throw new InvalidOperationException("Failed to build registry");
        }

        _built = true;

        var stats = GetStats();
        _logger?.PluginLogInformation(
            "Registry built: {Providers} providers, {Channels} channels, {Streams} streams (ratio: {Ratio:P0})",
            stats.ProviderCount,
            stats.ChannelCount,
            stats.StreamCount,
            stats.DeduplicationRatio
        );

        return stats;
    }

    /// <summary>
    /// Gets registry statistics.
    /// </summary>
    /// <returns>Statistics snapshot.</returns>
    public RegistryStats GetStats()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (NativeRegistryMethods.RegistryGetStats(_handle.DangerousGetHandle(), out var native))
        {
            return native.ToManaged();
        }

        return default;
    }

    /// <summary>
    /// Begins a health-preserving rebuild. Clears channels and GUIDs but keeps
    /// the health manager and provider list. After calling, add new streams
    /// with <see cref="AddStream"/>, then call <see cref="Rebuild"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if registry is not built.</exception>
    public void BeginRebuild()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_built)
        {
            throw new InvalidOperationException("Cannot rebuild a registry that has not been built");
        }

        NativeRegistryMethods.RegistryBeginRebuild(_handle.DangerousGetHandle());
        _built = false;
        _logger?.LogDebugIfEnabled("Registry rebuild started (health preserved)");
    }

    /// <summary>
    /// Finalizes a rebuild started with <see cref="BeginRebuild"/>.
    /// Re-processes streams and regenerates GUIDs. Health scores are preserved.
    /// </summary>
    /// <returns>Build statistics.</returns>
    /// <exception cref="InvalidOperationException">Thrown if rebuild fails.</exception>
    public RegistryStats Rebuild()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!NativeRegistryMethods.RegistryRebuild(_handle.DangerousGetHandle()))
        {
            throw new InvalidOperationException("Failed to rebuild registry");
        }

        _built = true;

        var stats = GetStats();
        _logger?.PluginLogInformation(
            "Registry rebuilt: {Providers} providers, {Channels} channels, {Streams} streams (ratio: {Ratio:P0})",
            stats.ProviderCount,
            stats.ChannelCount,
            stats.StreamCount,
            stats.DeduplicationRatio
        );

        return stats;
    }

    /// <summary>
    /// Gets the number of deduplicated channels.
    /// </summary>
    /// <returns>Channel count, or 0 if not built.</returns>
    public int GetChannelCount()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return NativeRegistryMethods.RegistryGetChannelCount(_handle.DangerousGetHandle());
    }

    /// <summary>
    /// Registers an external GUID alias so that a C# GUID (from ToProviderGuid)
    /// can be looked up to find the corresponding channel in the registry.
    /// Must be called after Build().
    /// </summary>
    /// <param name="aliasGuid">The C# GUID to register as an alias.</param>
    /// <param name="providerIndex">The provider index from AddProvider.</param>
    /// <param name="streamId">The stream ID that identifies the stream entry.</param>
    /// <returns>True if the alias was registered.</returns>
    public bool AddGuidAlias(Guid aliasGuid, int providerIndex, int streamId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Span<byte> bytes = stackalloc byte[16];
        aliasGuid.TryWriteBytes(bytes);
        var aliasHigh = BitConverter.ToInt64(bytes);
        var aliasLow = BitConverter.ToInt64(bytes[8..]);

        return NativeRegistryMethods.RegistryAddGuidAlias(
            _handle.DangerousGetHandle(),
            aliasHigh,
            aliasLow,
            providerIndex,
            streamId
        );
    }

    /// <summary>
    /// Enumerates channels with pagination.
    /// </summary>
    /// <param name="offset">Number of channels to skip.</param>
    /// <param name="limit">Maximum channels to return.</param>
    /// <returns>List of channel entries.</returns>
    public unsafe IReadOnlyList<ChannelListEntry> EnumerateChannels(int offset, int limit)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var buffer = new RegistryChannelListEntryNative[limit];
        int written;
        fixed (RegistryChannelListEntryNative* ptr = buffer)
        {
            written = NativeRegistryMethods.RegistryEnumerateChannels(_handle.DangerousGetHandle(), ptr, limit, offset);
        }

        var result = new List<ChannelListEntry>(written);
        for (int i = 0; i < written; i++)
        {
            result.Add(buffer[i].ToManaged());
        }

        return result;
    }

    /// <summary>
    /// Gets channel info by GUID.
    /// </summary>
    /// <param name="guid">Channel GUID.</param>
    /// <returns>Channel info if found, null otherwise.</returns>
    public unsafe ChannelListEntry? GetChannelInfo(Guid guid)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Span<byte> bytes = stackalloc byte[16];
        guid.TryWriteBytes(bytes);
        var high = BitConverter.ToInt64(bytes);
        var low = BitConverter.ToInt64(bytes[8..]);

        RegistryChannelListEntryNative entry;
        if (NativeRegistryMethods.RegistryGetChannelInfo(_handle.DangerousGetHandle(), high, low, &entry))
        {
            return entry.ToManaged();
        }

        return null;
    }

    /// <summary>
    /// Reports a successful API call for provider health tracking.
    /// </summary>
    /// <param name="providerIndex">Provider index from AddProvider.</param>
    /// <param name="latencyMs">API call latency in milliseconds.</param>
    public void ReportApiSuccess(int providerIndex, int latencyMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativeRegistryMethods.RegistryReportApiSuccess(_handle.DangerousGetHandle(), providerIndex, latencyMs);
    }

    /// <summary>
    /// Reports a failed API call for provider health tracking.
    /// DNS failures trigger the three-tier DNS policy (switch/eject).
    /// </summary>
    /// <param name="providerIndex">Provider index from AddProvider.</param>
    /// <param name="errorType">Type of API error.</param>
    public void ReportApiFailure(int providerIndex, ApiErrorType errorType)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativeRegistryMethods.RegistryReportApiFailure(_handle.DangerousGetHandle(), providerIndex, (int)errorType);
    }

    /// <summary>
    /// Gets status for all providers in the registry.
    /// </summary>
    /// <param name="maxProviders">Maximum number of providers to return.</param>
    /// <returns>List of provider status entries.</returns>
    public unsafe IReadOnlyList<ProviderStatus> GetProviderStatus(int maxProviders = 64)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        maxProviders = Math.Clamp(maxProviders, 1, 256);
        var buffer = new RegistryProviderStatusNative[maxProviders];
        int written;
        fixed (RegistryProviderStatusNative* ptr = buffer)
        {
            written = NativeRegistryMethods.RegistryGetProviderStatus(_handle.DangerousGetHandle(), ptr, maxProviders);
        }

        var result = new List<ProviderStatus>(written);
        for (int i = 0; i < written; i++)
        {
            result.Add(buffer[i].ToManaged());
        }

        return result;
    }

    /// <summary>
    /// Callback invoked when a provider's health state changes.
    /// </summary>
    /// <param name="providerIndex">Index of the affected provider.</param>
    /// <param name="eventType">Event type (e.g. "ejected", "recovered", "probation").</param>
    /// <param name="details">Additional details.</param>
    public delegate void HealthEventCallback(int providerIndex, string eventType, string details);

    private GCHandle _callbackHandle;
    private NativeRegistryMethods.HealthCallbackDelegate? _nativeCallback;

    /// <summary>
    /// Sets a callback for health state change events.
    /// Pass null to disable.
    /// </summary>
    /// <param name="handler">Event handler, or null to disable.</param>
    public void SetHealthCallback(HealthEventCallback? handler)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Disarm native callback FIRST to prevent calls through stale function pointer.
        // Between Free() and this call, native C++ could invoke the old callback.
        NativeRegistryMethods.RegistrySetHealthCallback(_handle.DangerousGetHandle(), IntPtr.Zero, IntPtr.Zero);

        // Now safe to clean up previous state
        if (_callbackHandle.IsAllocated)
        {
            _callbackHandle.Free();
        }

        _nativeCallback = null;

        if (handler is null)
        {
            return;
        }

        // Pin the handler to prevent GC collection
        _callbackHandle = GCHandle.Alloc(handler);

        _nativeCallback = (providerIndex, eventTypePtr, detailsPtr, _) =>
        {
            var eventType = Marshal.PtrToStringUTF8(eventTypePtr) ?? string.Empty;
            var details = Marshal.PtrToStringUTF8(detailsPtr) ?? string.Empty;
            handler(providerIndex, eventType, details);
        };

        var fnPtr = Marshal.GetFunctionPointerForDelegate(_nativeCallback);
        NativeRegistryMethods.RegistrySetHealthCallback(_handle.DangerousGetHandle(), fnPtr, IntPtr.Zero);
    }

    /// <summary>
    /// Gets the native handle for passing to streamer.
    /// </summary>
    internal nint DangerousGetHandle() => _handle.DangerousGetHandle();

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Clear callback before disposing handle
        if (_callbackHandle.IsAllocated)
        {
            NativeRegistryMethods.RegistrySetHealthCallback(_handle.DangerousGetHandle(), IntPtr.Zero, IntPtr.Zero);
            _callbackHandle.Free();
        }

        _nativeCallback = null;
        _handle.Dispose();
        _logger?.LogDebugIfEnabled("NativeChannelRegistry disposed");
    }
}
