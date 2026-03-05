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
/// Default implementation of <see cref="IPluginConfigurationProvider"/>
/// that retrieves configuration from the Plugin singleton.
/// </summary>
public sealed class PluginConfigurationProvider : IPluginConfigurationProvider
{
    /// <inheritdoc />
    public PluginConfiguration? GetConfiguration()
    {
        try
        {
            return Plugin.Instance?.Configuration;
        }
        catch
        {
            return null;
        }
    }
}
