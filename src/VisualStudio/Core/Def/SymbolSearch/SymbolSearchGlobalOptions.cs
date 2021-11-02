﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Options.Providers;

namespace Microsoft.CodeAnalysis.SymbolSearch
{
    [ExportGlobalOptionProvider, Shared]
    internal sealed class SymbolSearchGlobalOptions : IOptionProvider
    {
        [ImportingConstructor]
        [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
        public SymbolSearchGlobalOptions()
        {
        }

        ImmutableArray<IOption> IOptionProvider.Options => ImmutableArray.Create<IOption>(
            Enabled);

        private const string LocalRegistryPath = @"Roslyn\Features\SymbolSearch\";
        private const string FeatureName = "SymbolSearchOptions";

        public static readonly Option2<bool> Enabled = new(
            FeatureName, "Enabled", defaultValue: true,
            storageLocation: new LocalUserProfileStorageLocation(LocalRegistryPath + "Enabled"));
    }
}
