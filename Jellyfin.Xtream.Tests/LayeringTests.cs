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

using System.Reflection;
using NetArchTest.Rules;
using Xunit;

namespace Jellyfin.Xtream.Tests;

/// <summary>
/// Architecture tests to enforce Clean Architecture layering rules in the MPEG-TS module.
/// These tests ensure the dependency rule is followed: inner layers cannot reference outer layers.
/// </summary>
/// <remarks>
/// Layer hierarchy (inner to outer):
/// 1. Core - Domain entities, value types, enums (no dependencies)
/// 2. UseCases - Interfaces, events, application contracts (depends on Core)
/// 3. Parsing - Pure parsing utilities (Core-adjacent, bidirectional)
/// 4. Adapters - External library wrappers like Cinegy (depends on Core, UseCases)
/// 5. Infrastructure - Service implementations (depends on all layers)
/// </remarks>
public sealed class LayeringTests
{
    private const string MpegTsNamespace = "Jellyfin.Xtream.Service.MpegTs";
    private const string CoreNamespace = MpegTsNamespace + ".Core";
    private const string UseCasesNamespace = MpegTsNamespace + ".UseCases";
    private const string ParsingNamespace = MpegTsNamespace + ".Parsing";
    private const string AdaptersNamespace = MpegTsNamespace + ".Adapters";
    private const string InfrastructureNamespace = MpegTsNamespace + ".Infrastructure";
    private const string CinegyNamespace = "Cinegy";

    // Use a type from the MpegTs namespace that doesn't trigger Jellyfin dependencies
    private static readonly Assembly MpegTsAssembly = typeof(Jellyfin.Xtream.Service.MpegTs.Core.NalUnitType).Assembly;

    #region Core Layer Rules

    [Fact]
    public void Core_ShouldNotDependOn_UseCases()
    {
        var result = Types
            .InAssembly(MpegTsAssembly)
            .That()
            .ResideInNamespace(CoreNamespace)
            .ShouldNot()
            .HaveDependencyOn(UseCasesNamespace)
            .GetResult();

        AssertArchitectureRule(result, "Core should not depend on UseCases");
    }

    [Fact]
    public void Core_ShouldNotDependOn_Adapters()
    {
        var result = Types
            .InAssembly(MpegTsAssembly)
            .That()
            .ResideInNamespace(CoreNamespace)
            .ShouldNot()
            .HaveDependencyOn(AdaptersNamespace)
            .GetResult();

        AssertArchitectureRule(result, "Core should not depend on Adapters");
    }

    [Fact]
    public void Core_ShouldNotDependOn_Infrastructure()
    {
        var result = Types
            .InAssembly(MpegTsAssembly)
            .That()
            .ResideInNamespace(CoreNamespace)
            .ShouldNot()
            .HaveDependencyOn(InfrastructureNamespace)
            .GetResult();

        AssertArchitectureRule(result, "Core should not depend on Infrastructure");
    }

    [Fact]
    public void Core_ShouldNotDependOn_Cinegy()
    {
        var result = Types
            .InAssembly(MpegTsAssembly)
            .That()
            .ResideInNamespace(CoreNamespace)
            .ShouldNot()
            .HaveDependencyOn(CinegyNamespace)
            .GetResult();

        AssertArchitectureRule(result, "Core should not depend on Cinegy");
    }

    #endregion

    #region UseCases Layer Rules

    [Fact]
    public void UseCases_ShouldNotDependOn_Adapters()
    {
        var result = Types
            .InAssembly(MpegTsAssembly)
            .That()
            .ResideInNamespace(UseCasesNamespace)
            .ShouldNot()
            .HaveDependencyOn(AdaptersNamespace)
            .GetResult();

        AssertArchitectureRule(result, "UseCases should not depend on Adapters");
    }

    [Fact]
    public void UseCases_ShouldNotDependOn_Infrastructure()
    {
        var result = Types
            .InAssembly(MpegTsAssembly)
            .That()
            .ResideInNamespace(UseCasesNamespace)
            .ShouldNot()
            .HaveDependencyOn(InfrastructureNamespace)
            .GetResult();

        AssertArchitectureRule(result, "UseCases should not depend on Infrastructure");
    }

    [Fact]
    public void UseCases_ShouldNotDependOn_Cinegy()
    {
        var result = Types
            .InAssembly(MpegTsAssembly)
            .That()
            .ResideInNamespace(UseCasesNamespace)
            .ShouldNot()
            .HaveDependencyOn(CinegyNamespace)
            .GetResult();

        AssertArchitectureRule(result, "UseCases should not depend on Cinegy");
    }

    #endregion

    #region Parsing Layer Rules

    [Fact]
    public void Parsing_ShouldNotDependOn_Adapters()
    {
        var result = Types
            .InAssembly(MpegTsAssembly)
            .That()
            .ResideInNamespace(ParsingNamespace)
            .ShouldNot()
            .HaveDependencyOn(AdaptersNamespace)
            .GetResult();

        AssertArchitectureRule(result, "Parsing should not depend on Adapters");
    }

    [Fact]
    public void Parsing_ShouldNotDependOn_Infrastructure()
    {
        var result = Types
            .InAssembly(MpegTsAssembly)
            .That()
            .ResideInNamespace(ParsingNamespace)
            .ShouldNot()
            .HaveDependencyOn(InfrastructureNamespace)
            .GetResult();

        AssertArchitectureRule(result, "Parsing should not depend on Infrastructure");
    }

    [Fact]
    public void Parsing_ShouldNotDependOn_Cinegy()
    {
        var result = Types
            .InAssembly(MpegTsAssembly)
            .That()
            .ResideInNamespace(ParsingNamespace)
            .ShouldNot()
            .HaveDependencyOn(CinegyNamespace)
            .GetResult();

        AssertArchitectureRule(result, "Parsing should not depend on Cinegy");
    }

    #endregion

    #region Adapters Layer Rules

    [Fact]
    public void Adapters_ShouldNotDependOn_Infrastructure()
    {
        var result = Types
            .InAssembly(MpegTsAssembly)
            .That()
            .ResideInNamespace(AdaptersNamespace)
            .ShouldNot()
            .HaveDependencyOn(InfrastructureNamespace)
            .GetResult();

        AssertArchitectureRule(result, "Adapters should not depend on Infrastructure");
    }

    #endregion

    #region Cinegy Isolation Rules

    [Fact]
    public void OnlyAdaptersAndInfrastructure_ShouldDependOn_Cinegy()
    {
        // Verify that Cinegy references are isolated to Adapters and Infrastructure layers.
        // Infrastructure (and legacy Services) is the outermost layer and can depend on Adapters
        // which expose Cinegy types through TsPacketEventArgs.
        var result = Types
            .InAssembly(MpegTsAssembly)
            .That()
            .ResideInNamespaceStartingWith(MpegTsNamespace)
            .And()
            .DoNotResideInNamespace(AdaptersNamespace)
            .And()
            .DoNotResideInNamespace(InfrastructureNamespace)
            .And()
            .DoNotResideInNamespace(MpegTsNamespace + ".Services") // Legacy: will become Infrastructure
            .ShouldNot()
            .HaveDependencyOn(CinegyNamespace)
            .GetResult();

        AssertArchitectureRule(result, "Only Adapters and Infrastructure layers should depend on Cinegy");
    }

    #endregion

    #region Legacy Layer Rules (for gradual migration)

    // These tests check the OLD namespaces during migration
    // They should be removed once migration is complete

    [Fact]
    public void LegacyModels_ShouldNotDependOn_Cinegy()
    {
        var result = Types
            .InAssembly(MpegTsAssembly)
            .That()
            .ResideInNamespace(MpegTsNamespace + ".Models")
            .ShouldNot()
            .HaveDependencyOn(CinegyNamespace)
            .GetResult();

        AssertArchitectureRule(result, "Models should not depend on Cinegy");
    }

    [Fact]
    public void LegacyEvents_ShouldOnlyDependOn_ModelsAndCoreTypes()
    {
        // Events can depend on Models (which will become Core)
        // but should not depend on Services or Parsing
        var result = Types
            .InAssembly(MpegTsAssembly)
            .That()
            .ResideInNamespace(MpegTsNamespace + ".Events")
            .ShouldNot()
            .HaveDependencyOn(MpegTsNamespace + ".Services")
            .GetResult();

        AssertArchitectureRule(result, "Events should not depend on Services");
    }

    #endregion

    #region Helper Methods

    private static void AssertArchitectureRule(TestResult result, string ruleName)
    {
        if (!result.IsSuccessful)
        {
            var violatingTypes =
                result.FailingTypes != null
                    ? string.Join(", ", result.FailingTypes.Select(t => t.FullName))
                    : "Unknown types";

            Assert.Fail($"Architecture rule '{ruleName}' violated by: {violatingTypes}");
        }
    }

    #endregion
}
