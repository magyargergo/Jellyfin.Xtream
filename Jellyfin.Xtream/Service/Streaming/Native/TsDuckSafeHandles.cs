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
using Microsoft.Win32.SafeHandles;

namespace Jellyfin.Xtream.Service.Streaming.Native;

/// <summary>
/// Safe handle wrapper for TsDuck context handles.
/// Ensures proper cleanup even if Dispose is not called.
/// </summary>
internal sealed class TsDuckContextSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TsDuckContextSafeHandle"/> class.
    /// </summary>
    public TsDuckContextSafeHandle()
        : base(ownsHandle: true) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="TsDuckContextSafeHandle"/> class
    /// with an existing handle value.
    /// </summary>
    /// <param name="existingHandle">The existing handle value.</param>
    /// <param name="ownsHandle">Whether this wrapper owns the handle.</param>
    public TsDuckContextSafeHandle(nint existingHandle, bool ownsHandle)
        : base(ownsHandle)
    {
        SetHandle(existingHandle);
    }

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        TsDuckNativeMethods.ContextDestroy(handle);
        return true;
    }

    /// <summary>
    /// Creates a new TsDuck context.
    /// </summary>
    /// <returns>A safe handle wrapping the context, or an invalid handle on failure.</returns>
    public static TsDuckContextSafeHandle Create()
    {
        var rawHandle = TsDuckNativeMethods.ContextCreate();
        return new TsDuckContextSafeHandle(rawHandle, ownsHandle: true);
    }
}

/// <summary>
/// Safe handle wrapper for TsDuck analyzer handles.
/// Ensures proper cleanup even if Dispose is not called.
/// </summary>
internal sealed class TsDuckAnalyzerSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TsDuckAnalyzerSafeHandle"/> class.
    /// </summary>
    public TsDuckAnalyzerSafeHandle()
        : base(ownsHandle: true) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="TsDuckAnalyzerSafeHandle"/> class
    /// with an existing handle value.
    /// </summary>
    /// <param name="existingHandle">The existing handle value.</param>
    /// <param name="ownsHandle">Whether this wrapper owns the handle.</param>
    public TsDuckAnalyzerSafeHandle(nint existingHandle, bool ownsHandle)
        : base(ownsHandle)
    {
        SetHandle(existingHandle);
    }

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        TsDuckNativeMethods.AnalyzerDestroy(handle);
        return true;
    }

    /// <summary>
    /// Creates a new TsDuck analyzer attached to a context.
    /// </summary>
    /// <param name="context">The context handle.</param>
    /// <param name="config">The configuration.</param>
    /// <returns>A safe handle wrapping the analyzer, or an invalid handle on failure.</returns>
    public static TsDuckAnalyzerSafeHandle Create(TsDuckContextSafeHandle context, TsDuckConfigNative config)
    {
        var rawHandle = TsDuckNativeMethods.AnalyzerCreate(context.DangerousGetHandle(), in config);
        return new TsDuckAnalyzerSafeHandle(rawHandle, ownsHandle: true);
    }
}

/// <summary>
/// Safe handle wrapper for TsDuck streamer handles.
/// Ensures proper cleanup even if Dispose is not called.
/// </summary>
internal sealed class TsDuckStreamerSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TsDuckStreamerSafeHandle"/> class.
    /// </summary>
    public TsDuckStreamerSafeHandle()
        : base(ownsHandle: true) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="TsDuckStreamerSafeHandle"/> class
    /// with an existing handle value.
    /// </summary>
    /// <param name="existingHandle">The existing handle value.</param>
    /// <param name="ownsHandle">Whether this wrapper owns the handle.</param>
    public TsDuckStreamerSafeHandle(nint existingHandle, bool ownsHandle)
        : base(ownsHandle)
    {
        SetHandle(existingHandle);
    }

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        TsDuckNativeMethods.StreamerDestroy(handle);
        return true;
    }

    /// <summary>
    /// Creates a new streamer with configuration.
    /// </summary>
    /// <param name="config">Streamer configuration.</param>
    /// <param name="analyzerConfig">Optional analyzer configuration.</param>
    /// <returns>A safe handle wrapping the streamer, or an invalid handle on failure.</returns>
    public static TsDuckStreamerSafeHandle Create(TsDuckStreamerConfigNative config, TsDuckConfigNative? analyzerConfig)
    {
        nint rawHandle;
        if (analyzerConfig.HasValue)
        {
            var aCfg = analyzerConfig.Value;
            rawHandle = TsDuckNativeMethods.StreamerCreate(in config, in aCfg);
        }
        else
        {
            unsafe
            {
                var pCfg = &config;
                rawHandle = TsDuckNativeMethods.StreamerCreateDefault((nint)pCfg, 0);
            }
        }

        return new TsDuckStreamerSafeHandle(rawHandle, ownsHandle: true);
    }
}

/// <summary>
/// Safe handle wrapper for shared memory producer handles.
/// Ensures proper cleanup even if Dispose is not called.
/// </summary>
internal sealed class SharedMemoryProducerSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SharedMemoryProducerSafeHandle"/> class.
    /// </summary>
    public SharedMemoryProducerSafeHandle()
        : base(ownsHandle: true) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="SharedMemoryProducerSafeHandle"/> class
    /// with an existing handle value.
    /// </summary>
    /// <param name="existingHandle">The existing handle value.</param>
    /// <param name="ownsHandle">Whether this wrapper owns the handle.</param>
    public SharedMemoryProducerSafeHandle(nint existingHandle, bool ownsHandle)
        : base(ownsHandle)
    {
        SetHandle(existingHandle);
    }

    /// <inheritdoc/>
    protected override bool ReleaseHandle()
    {
        TsDuckNativeMethods.ShmProducerDestroy(handle);
        return true;
    }

    /// <summary>
    /// Creates a new shared memory producer.
    /// </summary>
    /// <param name="name">Unique name for the shared memory region.</param>
    /// <param name="slotCount">Number of slots in ring buffer (must be power of 2).</param>
    /// <param name="slotSize">Size of each slot in bytes (must be multiple of 188).</param>
    /// <returns>A safe handle wrapping the producer, or an invalid handle on failure.</returns>
    public static SharedMemoryProducerSafeHandle Create(string name, uint slotCount, uint slotSize)
    {
        var rawHandle = TsDuckNativeMethods.ShmProducerCreate(name, slotCount, slotSize);
        return new SharedMemoryProducerSafeHandle(rawHandle, ownsHandle: true);
    }
}
