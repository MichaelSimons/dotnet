﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Editor.Implementation.Suggestions;
using Microsoft.CodeAnalysis.Editor.Shared.Extensions;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Workspaces.ProjectSystem;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Roslyn.Utilities;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.VisualStudio.LanguageServices.Implementation.ProjectSystem;

internal partial class VisualStudioWorkspaceImpl
{
    /// <summary>
    /// Singleton the updates the workspace in response to files being opened or closed.
    /// </summary>
    public sealed partial class OpenFileTracker : IOpenTextBufferEventListener
    {
        private readonly VisualStudioWorkspaceImpl _workspace;
        private readonly ProjectSystemProjectFactory _projectSystemProjectFactory;

        // This is lazily initialized to avoid loading editor components when they're not needed until we actually open a file.
        private readonly Lazy<IEditorOptionsFactoryService> _editorOptionsFactoryService;
        private readonly IAsynchronousOperationListener _asynchronousOperationListener;
        private readonly OpenTextBufferProvider _openTextBufferProvider;

        #region Fields read/and written to only on the UI thread to track active context for files

        private readonly ReferenceCountedDisposableCache<IVsHierarchy, HierarchyEventSink> _hierarchyEventSinkCache = new();

        /// <summary>
        /// The IVsHierarchies we have subscribed to to watch for any changes to this moniker. We track this per moniker, so
        /// when a document is closed we know what we have to incrementally unsubscribe from rather than having to unsubscribe from everything.
        /// </summary>
        private readonly MultiDictionary<string, IReferenceCountedDisposable<ICacheEntry<IVsHierarchy, HierarchyEventSink>>> _watchedHierarchiesForDocumentMoniker = [];

        #endregion

        /// <summary>
        /// Boolean flag to indicate if any <see cref="TextDocument"/> has been opened in the workspace.
        /// </summary>
        /// <remarks>
        /// This is written on the UI thread once it's been enabled, since the enablement can only happen there. It might be read on other threads in deciding to
        /// do the work on the UI thread, but that's not a problem since <see cref="EnsureSuggestedActionsSourceProviderEnabled"/> is idempotent.
        /// </remarks>
        private bool _suggestedActionSourceProviderEnabled;

        public OpenFileTracker(
            VisualStudioWorkspaceImpl workspace,
            ProjectSystemProjectFactory projectSystemProjectFactory,
            Lazy<IEditorOptionsFactoryService> editorOptionsFactoryService,
            IAsynchronousOperationListener workspaceOperationListener,
            OpenTextBufferProvider openTextBufferProvider)
        {
            _workspace = workspace;
            _projectSystemProjectFactory = projectSystemProjectFactory;
            _editorOptionsFactoryService = editorOptionsFactoryService;
            _asynchronousOperationListener = workspaceOperationListener;
            _openTextBufferProvider = openTextBufferProvider;
            _openTextBufferProvider.AddListener(this);
        }

        void IOpenTextBufferEventListener.OnOpenDocument(string moniker, ITextBuffer textBuffer, IVsHierarchy? hierarchy)
            => TryOpeningDocumentsForMonikerAndSetContextOnUIThread(moniker, textBuffer, hierarchy);

        void IOpenTextBufferEventListener.OnDocumentOpenedIntoWindowFrame(string moniker, IVsWindowFrame windowFrame) { }

        void IOpenTextBufferEventListener.OnCloseDocument(string moniker)
            => TryClosingDocumentsForMoniker(moniker);

        void IOpenTextBufferEventListener.OnRefreshDocumentContext(string moniker, IVsHierarchy hierarchy)
            => RefreshContextForMoniker(moniker, hierarchy);

        void IOpenTextBufferEventListener.OnRenameDocument(string newMoniker, string oldMoniker, ITextBuffer buffer)
        {
            TryClosingDocumentsForMoniker(oldMoniker);
            TryOpeningDocumentsForMonikerAndSetContextOnUIThread(newMoniker, buffer, hierarchy: _openTextBufferProvider.GetDocumentHierarchy(newMoniker));
        }

        private void TryOpeningDocumentsForMonikerAndSetContextOnUIThread(string moniker, ITextBuffer textBuffer, IVsHierarchy? hierarchy)
        {
            _workspace._threadingContext.ThrowIfNotOnUIThread();

            _projectSystemProjectFactory.ApplyChangeToWorkspace(w =>
            {
                if (TryOpeningDocumentsForFilePathCore(w, moniker, textBuffer, hierarchy))
                {
                    EnsureSuggestedActionsSourceProviderEnabled();
                }
            });
        }

        private void EnsureSuggestedActionsSourceProviderEnabled()
        {
            _workspace._threadingContext.ThrowIfNotOnUIThread();

            if (!_suggestedActionSourceProviderEnabled)
            {
                _suggestedActionSourceProviderEnabled = true;

                // First document opened in the workspace.
                // We enable quick actions from SuggestedActionsSourceProvider via an editor option.
                // NOTE: We need to be on the UI thread to enable the editor option.
                SuggestedActionsSourceProvider.Enable(_editorOptionsFactoryService.Value);
            }
        }

        /// <summary>
        /// Implements the core logic of connecting a buffer to the workspace. If a hierarchy is given, this must be on the UI thread and
        /// the hierarchy will be used to determine the correct context. Otherwise, an arbitrary context will be chosen.
        /// </summary>
        /// <returns>True if we actually opened at least one document.</returns>
        private bool TryOpeningDocumentsForFilePathCore(Workspace workspace, string moniker, ITextBuffer textBuffer, IVsHierarchy? hierarchy)
        {
            // If this method is given a hierarchy, we will need to be on the UI thread to use it; in any other case, we can be free-threaded.
            if (hierarchy != null)
                _workspace._threadingContext.ThrowIfNotOnUIThread();

            var documentIds = _projectSystemProjectFactory.Workspace.CurrentSolution.GetDocumentIdsWithFilePath(moniker);
            if (documentIds.IsDefaultOrEmpty)
            {
                return false;
            }

            if (documentIds.All(workspace.IsDocumentOpen))
            {
                return false;
            }

            ProjectId activeContextProjectId;

            if (documentIds.Length == 1 || hierarchy == null)
            {
                activeContextProjectId = documentIds.First().ProjectId;
            }
            else
            {
                activeContextProjectId = GetActiveContextProjectIdAndWatchHierarchies_NoLock(moniker, documentIds.Select(d => d.ProjectId), hierarchy);
            }

            var textContainer = textBuffer.AsTextContainer();

            var documentOpened = false;

            foreach (var documentId in documentIds)
            {
                if (!workspace.IsDocumentOpen(documentId) && !_projectSystemProjectFactory.DocumentsNotFromFiles.Contains(documentId))
                {
                    var isCurrentContext = documentId.ProjectId == activeContextProjectId;
                    if (workspace.CurrentSolution.ContainsDocument(documentId))
                    {
                        workspace.OnDocumentOpened(documentId, textContainer, isCurrentContext);
                    }
                    else if (workspace.CurrentSolution.ContainsAdditionalDocument(documentId))
                    {
                        workspace.OnAdditionalDocumentOpened(documentId, textContainer, isCurrentContext);
                    }
                    else
                    {
                        Debug.Assert(workspace.CurrentSolution.ContainsAnalyzerConfigDocument(documentId));
                        workspace.OnAnalyzerConfigDocumentOpened(documentId, textContainer, isCurrentContext);
                    }

                    documentOpened = true;
                }
            }

            return documentOpened;
        }

        private ProjectId GetActiveContextProjectIdAndWatchHierarchies_NoLock(string moniker, IEnumerable<ProjectId> projectIds, IVsHierarchy? hierarchy)
        {
            _workspace._threadingContext.ThrowIfNotOnUIThread();

            // First clear off any existing IVsHierarchies we are watching. Any ones that still matter we will resubscribe to.
            // We could be fancy and diff, but the cost is probably negligible.
            UnsubscribeFromWatchedHierarchies(moniker);

            if (hierarchy == null)
            {
                // Any item in the RDT should have a hierarchy associated; in this case we don't so there's absolutely nothing
                // we can do at this point.
                return projectIds.First();
            }

            void WatchHierarchy(IVsHierarchy hierarchyToWatch)
            {
                _watchedHierarchiesForDocumentMoniker.Add(moniker, _hierarchyEventSinkCache.GetOrCreate(hierarchyToWatch, static (h, self) => new HierarchyEventSink(h, self), this));
            }

            // Take a snapshot of the immutable data structure here to avoid mutation underneath us
            var projectToHierarchyMap = _workspace._projectToHierarchyMap;
            var solution = _workspace.CurrentSolution;

            // We now must chase to the actual hierarchy that we know about. First, we'll chase through multiple shared asset projects if
            // we need to do so.
            while (true)
            {
                var contextHierarchy = hierarchy.GetActiveProjectContext();

                // The check for if contextHierarchy == hierarchy is working around downstream impacts of https://devdiv.visualstudio.com/DevDiv/_git/CPS/pullrequest/158271
                // Since that bug means shared projects have themselves as their own owner, it sometimes results in us corrupting state where we end up
                // having the context of shared project be itself, it seems.
                if (contextHierarchy == null || contextHierarchy == hierarchy)
                {
                    break;
                }

                WatchHierarchy(hierarchy);
                hierarchy = contextHierarchy;
            }

            // We may have multiple projects with the same hierarchy, but we can use __VSHPROPID8.VSHPROPID_ActiveIntellisenseProjectContext to distinguish
            if (ErrorHandler.Succeeded(hierarchy.GetProperty(VSConstants.VSITEMID_ROOT, (int)__VSHPROPID8.VSHPROPID_ActiveIntellisenseProjectContext, out var contextProjectNameObject)))
            {
                WatchHierarchy(hierarchy);

                if (contextProjectNameObject is string contextProjectName)
                {
                    var project = _workspace.GetProjectWithHierarchyAndName_NoLock(hierarchy, contextProjectName);

                    if (project != null && projectIds.Contains(project.Id))
                    {
                        return project.Id;
                    }
                }
            }

            // At this point, we should hopefully have only one project that matches by hierarchy. If there's multiple, at this point we can't figure anything
            // out better.
            var matchingProjectId = projectIds.FirstOrDefault(id => projectToHierarchyMap.GetValueOrDefault(id, null) == hierarchy);

            if (matchingProjectId != null)
            {
                return matchingProjectId;
            }

            // If we had some trouble finding the project, we'll just pick one arbitrarily
            return projectIds.First();
        }

        private void UnsubscribeFromWatchedHierarchies(string moniker)
        {
            _workspace._threadingContext.ThrowIfNotOnUIThread();

            foreach (var watchedHierarchy in _watchedHierarchiesForDocumentMoniker[moniker])
            {
                watchedHierarchy.Dispose();
            }

            _watchedHierarchiesForDocumentMoniker.Remove(moniker);
        }

        private void RefreshContextForMoniker(string moniker, IVsHierarchy hierarchy)
        {
            _workspace._threadingContext.ThrowIfNotOnUIThread();

            _projectSystemProjectFactory.ApplyChangeToWorkspace(w =>
            {
                var documentIds = _workspace.CurrentSolution.GetDocumentIdsWithFilePath(moniker);
                if (documentIds.IsDefaultOrEmpty || documentIds.Length == 1)
                {
                    return;
                }

                if (!documentIds.All(w.IsDocumentOpen))
                {
                    return;
                }

                var activeProjectId = GetActiveContextProjectIdAndWatchHierarchies_NoLock(moniker, documentIds.Select(d => d.ProjectId), hierarchy);
                w.OnDocumentContextUpdated(documentIds.First(d => d.ProjectId == activeProjectId));
            });
        }

        private void RefreshContextsForHierarchyPropertyChange(IVsHierarchy hierarchy)
        {
            _workspace._threadingContext.ThrowIfNotOnUIThread();

            // We're going to go through each file that has subscriptions, and update them appropriately.
            // We have to clone this since we will be modifying it under the covers.
            foreach (var moniker in _watchedHierarchiesForDocumentMoniker.Keys.ToList())
            {
                foreach (var subscribedHierarchy in _watchedHierarchiesForDocumentMoniker[moniker])
                {
                    if (subscribedHierarchy.Target.Key == hierarchy)
                    {
                        RefreshContextForMoniker(moniker, hierarchy);
                    }
                }
            }
        }

        private void TryClosingDocumentsForMoniker(string moniker)
        {
            _workspace._threadingContext.ThrowIfNotOnUIThread();

            UnsubscribeFromWatchedHierarchies(moniker);

            _projectSystemProjectFactory.ApplyChangeToWorkspace(w =>
            {
                var documentIds = w.CurrentSolution.GetDocumentIdsWithFilePath(moniker);
                if (documentIds.IsDefaultOrEmpty)
                {
                    return;
                }

                foreach (var documentId in documentIds)
                {
                    if (w.IsDocumentOpen(documentId) && !_projectSystemProjectFactory.DocumentsNotFromFiles.Contains(documentId))
                    {
                        var solution = w.CurrentSolution;

                        if (solution.GetDocument(documentId) is { } document)
                        {
                            w.OnDocumentClosed(documentId, new WorkspaceFileTextLoader(w.Services.SolutionServices, moniker, defaultEncoding: null));
                        }
                        else if (solution.GetAdditionalDocument(documentId) is { } additionalDocument)
                        {
                            w.OnAdditionalDocumentClosed(documentId, new WorkspaceFileTextLoader(w.Services.SolutionServices, moniker, defaultEncoding: null));
                        }
                        else
                        {
                            var analyzerConfigDocument = solution.GetRequiredAnalyzerConfigDocument(documentId);
                            w.OnAnalyzerConfigDocumentClosed(documentId, new WorkspaceFileTextLoader(w.Services.SolutionServices, moniker, defaultEncoding: null));
                        }
                    }
                }
            });
        }

        public Task CheckForAddedFileBeingOpenMaybeAsync(bool useAsync, ImmutableArray<string> newFileNames)
        {
            // ThisCanBeCalledOnAnyThread();

            return _projectSystemProjectFactory.ApplyChangeToWorkspaceMaybeAsync(useAsync, w =>
            {
                foreach (var newFileName in newFileNames)
                {
                    if (_openTextBufferProvider.TryGetBufferFromFilePath(newFileName, out var textBuffer))
                    {
                        // If we are on the UI thread, we can just grab the hierarchy and properly wire up to the correct context; if we're off the UI thread we'll instead wire up to some
                        // document, and then asynchronously jump to the UI thread to pick the correct context. This ensures the workspace has the correct content,
                        // even if we don't immediately know the right context.
                        if (_workspace._threadingContext.JoinableTaskContext.IsOnMainThread)
                        {
                            var hierarchy = _openTextBufferProvider.GetDocumentHierarchy(newFileName);
                            if (TryOpeningDocumentsForFilePathCore(w, newFileName, textBuffer, hierarchy))
                                EnsureSuggestedActionsSourceProviderEnabled();
                        }
                        else
                        {
                            // Since we're not on the UI thread, we can't grab a hierarchy to wire up the correct context. We'll try wire up without a context
                            // and if it was actually open, we'll schedule an update asynchronously.
                            if (TryOpeningDocumentsForFilePathCore(w, newFileName, textBuffer, hierarchy: null))
                            {
                                // We've opened the document, but we only have to go to the UI thread if either:
                                //
                                // 1. We have multiple documents with the same file path, so we need to ensure the context is actually correct.
                                // 2. We haven't enabled the suggested actions source provider, which needs to be done once.
                                var needsContextUpdate = w.CurrentSolution.GetDocumentIdsWithFilePath(newFileName).Length >= 2;
                                if (needsContextUpdate || !_suggestedActionSourceProviderEnabled)
                                {
                                    var token = _asynchronousOperationListener.BeginAsyncOperation(nameof(UpdateContextAfterOpenAndEnableSuggestedActionsSourceProviderAsync));

                                    // Update the context on the UI thread if necessary, and enable the suggested actions source provider no matter what.
                                    UpdateContextAfterOpenAndEnableSuggestedActionsSourceProviderAsync(needsContextUpdate ? newFileName : null).CompletesAsyncOperation(token);
                                }
                            }
                        }
                    }
                }
            }).AsTask();
        }

        /// <param name="filePath">The file path to update the context on, or null if we're just calling this to ensure suggested actions are enabled.</param>
        private async Task UpdateContextAfterOpenAndEnableSuggestedActionsSourceProviderAsync(string? filePath)
        {
            await _workspace._threadingContext.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (filePath != null)
            {
                var hierarchy = _openTextBufferProvider.GetDocumentHierarchy(filePath);
                if (hierarchy != null)
                    RefreshContextForMoniker(filePath, hierarchy);
            }

            EnsureSuggestedActionsSourceProviderEnabled();
        }

        private sealed class HierarchyEventSink : IVsHierarchyEvents, IDisposable
        {
            private readonly IVsHierarchy _hierarchy;
            private readonly uint _cookie;
            private readonly OpenFileTracker _openFileTracker;

            public HierarchyEventSink(IVsHierarchy hierarchy, OpenFileTracker openFileTracker)
            {
                _hierarchy = hierarchy;
                _openFileTracker = openFileTracker;
                ErrorHandler.ThrowOnFailure(_hierarchy.AdviseHierarchyEvents(this, out _cookie));
            }

            void IDisposable.Dispose()
                => _hierarchy.UnadviseHierarchyEvents(_cookie);

            int IVsHierarchyEvents.OnItemAdded(uint itemidParent, uint itemidSiblingPrev, uint itemidAdded)
                => VSConstants.E_NOTIMPL;

            int IVsHierarchyEvents.OnItemsAppended(uint itemidParent)
                => VSConstants.E_NOTIMPL;

            int IVsHierarchyEvents.OnItemDeleted(uint itemid)
                => VSConstants.E_NOTIMPL;

            int IVsHierarchyEvents.OnPropertyChanged(uint itemid, int propid, uint flags)
            {
                if (propid is ((int)__VSHPROPID7.VSHPROPID_SharedItemContextHierarchy) or
                    ((int)__VSHPROPID8.VSHPROPID_ActiveIntellisenseProjectContext))
                {
                    _openFileTracker.RefreshContextsForHierarchyPropertyChange(_hierarchy);
                }

                return VSConstants.S_OK;
            }

            int IVsHierarchyEvents.OnInvalidateItems(uint itemidParent)
                => VSConstants.E_NOTIMPL;

            int IVsHierarchyEvents.OnInvalidateIcon(IntPtr hicon)
                => VSConstants.E_NOTIMPL;
        }
    }
}
