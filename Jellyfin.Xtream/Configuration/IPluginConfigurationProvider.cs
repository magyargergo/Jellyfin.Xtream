// Copyright (C) 2025  Gergo Magyar

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

namespace Jellyfin.Xtream.Configuration;

/// <summary>
/// Provides access to the current plugin configuration.
/// </summary>
/// <remarks>
/// This interface allows services to access configuration without
/// depending directly on the Plugin singleton, improving testability
/// and following dependency injection patterns.
/// </remarks>
public interface IPluginConfigurationProvider
{
    /// <summary>
    /// Gets the current plugin configuration.
    /// </summary>
    /// <returns>The current configuration, or null if not available.</returns>
    PluginConfiguration? GetConfiguration();
}
