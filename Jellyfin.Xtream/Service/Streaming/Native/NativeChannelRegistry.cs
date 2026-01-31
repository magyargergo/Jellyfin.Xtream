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
    private bool _disposed;
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
        _handle.Dispose();
        _logger?.LogDebugIfEnabled("NativeChannelRegistry disposed");
    }
}
