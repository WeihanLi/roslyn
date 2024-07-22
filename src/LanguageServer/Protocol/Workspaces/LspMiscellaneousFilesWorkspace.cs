﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Features.Workspaces;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.MetadataAsSource;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CommonLanguageServerProtocol.Framework;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.LanguageServer
{
    /// <summary>
    /// Defines a default workspace for opened LSP files that are not found in any
    /// workspace registered by the <see cref="LspWorkspaceRegistrationService"/>.
    /// If a document added here is subsequently found in a registered workspace, 
    /// the document is removed from this workspace.
    /// 
    /// Future work for this workspace includes supporting basic metadata references (mscorlib, System dlls, etc),
    /// but that is dependent on having a x-plat mechanism for retrieving those references from the framework / sdk.
    /// </summary>
    internal sealed class LspMiscellaneousFilesWorkspace(ILspServices lspServices, IMetadataAsSourceFileService metadataAsSourceFileService, HostServices hostServices)
        : Workspace(hostServices, WorkspaceKind.MiscellaneousFiles), ILspService, ILspWorkspace
    {
        public bool SupportsMutation => true;

        /// <summary>
        /// Takes in a file URI and text and creates a misc project and document for the file.
        /// 
        /// Calls to this method and <see cref="TryRemoveMiscellaneousDocument(Uri, bool)"/> are made
        /// from LSP text sync request handling which do not run concurrently.
        /// </summary>
        public Document? AddMiscellaneousDocument(Uri uri, SourceText documentText, string languageId, ILspLogger logger)
        {
            var documentFilePath = ProtocolConversions.GetDocumentFilePathFromUri(uri);

            var container = new StaticSourceTextContainer(documentText);
            if (metadataAsSourceFileService.TryAddDocumentToWorkspace(documentFilePath, container, out var documentId))
            {
                var metadataWorkspace = metadataAsSourceFileService.TryGetWorkspace();
                Contract.ThrowIfNull(metadataWorkspace);
                var document = metadataWorkspace.CurrentSolution.GetRequiredDocument(documentId);
                return document;
            }

            var languageInfoProvider = lspServices.GetRequiredService<ILanguageInfoProvider>();
            var languageInformation = languageInfoProvider.GetLanguageInformation(documentFilePath, languageId);
            if (languageInformation == null)
            {
                // Only log here since throwing here could take down the LSP server.
                logger.LogError($"Could not find language information for {uri} with absolute path {documentFilePath}");
                return null;
            }

            var sourceTextLoader = new SourceTextLoader(documentText, documentFilePath);

            var projectInfo = MiscellaneousFileUtilities.CreateMiscellaneousProjectInfoForDocument(
                this, documentFilePath, sourceTextLoader, languageInformation, documentText.ChecksumAlgorithm, Services.SolutionServices, []);
            OnProjectAdded(projectInfo);

            var id = projectInfo.Documents.Single().Id;
            return CurrentSolution.GetRequiredDocument(id);
        }

        /// <summary>
        /// Removes a document with the matching file path from this workspace.
        /// 
        /// Calls to this method and <see cref="AddMiscellaneousDocument(Uri, SourceText, string, ILspLogger)"/> are made
        /// from LSP text sync request handling which do not run concurrently.
        /// </summary>
        public void TryRemoveMiscellaneousDocument(Uri uri, bool removeFromMetadata)
        {
            var documentFilePath = ProtocolConversions.GetDocumentFilePathFromUri(uri);
            if (removeFromMetadata && metadataAsSourceFileService.TryRemoveDocumentFromWorkspace(documentFilePath))
            {
                return;
            }

            // We'll only ever have a single document matching this URI in the misc solution.
            var matchingDocument = CurrentSolution.GetDocumentIds(uri).SingleOrDefault();
            if (matchingDocument != null)
            {
                if (CurrentSolution.ContainsDocument(matchingDocument))
                {
                    OnDocumentRemoved(matchingDocument);
                }
                else if (CurrentSolution.ContainsAdditionalDocument(matchingDocument))
                {
                    OnAdditionalDocumentRemoved(matchingDocument);
                }

                // Also remove the project - we always create a new project for each misc file we add
                // so it should never have other documents in it.
                var project = CurrentSolution.GetRequiredProject(matchingDocument.ProjectId);
                OnProjectRemoved(project.Id);
            }
        }

        public ValueTask UpdateTextIfPresentAsync(DocumentId documentId, SourceText sourceText, CancellationToken cancellationToken)
        {
            this.OnDocumentTextChanged(documentId, sourceText, PreservationMode.PreserveIdentity, requireDocumentPresent: false);
            return ValueTaskFactory.CompletedTask;
        }

        private class StaticSourceTextContainer(SourceText text) : SourceTextContainer
        {
            public override SourceText CurrentText => text;

            /// <summary>
            /// Text changes are handled by LSP forking the document, we don't need to actually update anything here.
            /// </summary>
            public override event EventHandler<TextChangeEventArgs> TextChanged
            {
                add { }
                remove { }
            }
        }
    }
}
