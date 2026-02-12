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
using System.IO;
using System.IO.MemoryMappedFiles;

namespace Jellyfin.Xtream.Tests.Service.Streaming.SharedMemory;

/// <summary>
/// Encapsulates cross-platform shared memory creation and cleanup for tests.
/// Provides a <see cref="MemoryMappedFile"/> and <see cref="MemoryMappedViewAccessor"/>
/// backed by named shared memory (Windows) or file-backed memory (Linux/macOS).
/// </summary>
internal sealed class SharedMemoryTestFixture : IDisposable
{
    private readonly string _name;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SharedMemoryTestFixture"/> class.
    /// Creates a cross-platform shared memory region of the specified size.
    /// </summary>
    /// <param name="name">Unique name for the shared memory region.</param>
    /// <param name="totalSize">Total size of the shared memory region in bytes.</param>
    public SharedMemoryTestFixture(string name, long totalSize)
    {
        _name = name;

        if (OperatingSystem.IsWindows())
        {
            // Windows: Use named memory-mapped file
            Mmf = MemoryMappedFile.CreateNew(name, totalSize);
        }
        else
        {
            // Linux/macOS: Use file-backed memory-mapped file
            var shmPath = GetShmPath(name);

            var fileStream = new FileStream(shmPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
            fileStream.SetLength(totalSize);

            // leaveOpen: false transfers ownership of the FileStream to the MMF
            Mmf = MemoryMappedFile.CreateFromFile(
                fileStream,
                mapName: null,
                capacity: 0,
                access: MemoryMappedFileAccess.ReadWrite,
                inheritability: HandleInheritability.None,
                leaveOpen: false
            );
        }

        Accessor = Mmf.CreateViewAccessor(0, totalSize, MemoryMappedFileAccess.ReadWrite);
    }

    /// <summary>
    /// Gets the memory-mapped file backing this shared memory region.
    /// </summary>
    public MemoryMappedFile Mmf { get; }

    /// <summary>
    /// Gets the view accessor for reading and writing the shared memory region.
    /// </summary>
    public MemoryMappedViewAccessor Accessor { get; }

    /// <summary>
    /// Returns the platform-appropriate file path for shared memory.
    /// </summary>
    /// <param name="name">The shared memory region name.</param>
    /// <returns>The absolute path to the shared memory backing file.</returns>
    public static string GetShmPath(string name)
    {
        // Linux: /dev/shm is typically a tmpfs mount
        // macOS: /dev/shm doesn't exist, use /tmp
        var shmDir = Directory.Exists("/dev/shm") ? "/dev/shm" : "/tmp";
        return Path.Combine(shmDir, name);
    }

    /// <summary>
    /// Disposes the accessor, memory-mapped file, and cleans up the backing file on non-Windows platforms.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Accessor.Dispose();
        Mmf.Dispose();

        // Clean up the file-backed shared memory on Linux/macOS
        if (!OperatingSystem.IsWindows())
        {
            var shmPath = GetShmPath(_name);
            if (File.Exists(shmPath))
            {
                try
                {
                    File.Delete(shmPath);
                }
                catch
                {
                    // Ignore cleanup errors in test teardown
                }
            }
        }
    }
}
