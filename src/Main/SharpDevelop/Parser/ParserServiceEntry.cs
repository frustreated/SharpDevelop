﻿// Copyright (c) AlphaSierraPapa for the SharpDevelop Team (for details please see \doc\copyright.txt)
// This code is distributed under the GNU LGPL (for details please see \doc\license.txt)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using ICSharpCode.Core;
using ICSharpCode.NRefactory.Editor;
using ICSharpCode.NRefactory.TypeSystem;
using ICSharpCode.NRefactory.TypeSystem.Implementation;
using ICSharpCode.SharpDevelop.Project;

namespace ICSharpCode.SharpDevelop.Parser
{
	sealed class ParserServiceEntry
	{
		struct ProjectEntry
		{
			public readonly IProject Project;
			public readonly IParsedFile ParsedFile;
			public readonly ParseInformation CachedParseInformation;
			
			public ProjectEntry(IProject project, IParsedFile parsedFile, ParseInformation cachedParseInformation)
			{
				this.Project = project;
				this.ParsedFile = parsedFile;
				this.CachedParseInformation = cachedParseInformation;
			}
		}
		
		readonly ParserService parserService;
		internal readonly FileName fileName;
		internal readonly IParser parser;
		List<ProjectEntry> entries = new List<ProjectEntry> { default(ProjectEntry) };
		ITextSourceVersion currentVersion;
		
		public ParserServiceEntry(ParserService parserService, FileName fileName)
		{
			this.parserService = parserService;
			this.fileName = fileName;
			this.parser = parserService.CreateParser(fileName);
		}
		
		#region Owner Projects
		IProject PrimaryProject {
			get { return entries[0].Project; }
		}
		
		int FindIndexForProject(IProject parentProject)
		{
			if (parentProject == null)
				return 0;
			for (int i = 0; i < entries.Count; i++) {
				if (entries[i].Project == parentProject)
					return i;
			}
			// project not found
			return -1;
		}
		
		public void AddOwnerProject(IProject project, bool isLinkedFile)
		{
			Debug.Assert(project != null);
			lock (this) {
				if (FindIndexForProject(project) >= 0)
					throw new InvalidOperationException("The project alreadys owns the file");
				ProjectEntry newEntry = new ProjectEntry(project, null, null);
				if (entries[0].Project == null) {
					entries[0] = newEntry;
				} else if (isLinkedFile) {
					entries.Add(newEntry);
				} else {
					entries.Insert(0, newEntry);
				}
			}
		}
		
		public void RemoveOwnerProject(IProject project)
		{
			Debug.Assert(project != null);
			lock (this) {
				int index = FindIndexForProject(project);
				if (index < 0)
					throw new InvalidOperationException("The project does not own the file");
				if (entries.Count == 1) {
					entries[0] = default(ProjectEntry);
				} else {
					entries.RemoveAt(index);
				}
			}
		}
		#endregion
		
		/// <summary>
		/// Compares currentVersion with version.
		/// -1 = currentVersion is older; 0 = same version; 1 = newVersion is older
		/// </summary>
		int CompareVersions(ITextSourceVersion newVersion)
		{
			if (currentVersion != null && newVersion != null && currentVersion.BelongsToSameDocumentAs(newVersion))
				return currentVersion.CompareAge(newVersion);
			else
				return -1;
		}
		
		#region Expire Cache + GetExistingParsedFile + GetCachedParseInformation
		public void ExpireCache()
		{
			lock (this) {
				if (PrimaryProject == null) {
					parserService.RemoveEntry(this);
				} else {
					for (int i = 0; i < entries.Count; i++) {
						var oldEntry = entries[i];
						entries[i] = new ProjectEntry(oldEntry.Project, oldEntry.ParsedFile, null);
					}
				}
			}
		}
		
		public IParsedFile GetExistingParsedFile(ITextSourceVersion version, IProject parentProject)
		{
			lock (this) {
				if (version != null && CompareVersions(version) != 0) {
					return null;
				}
				int index = FindIndexForProject(parentProject);
				if (index < 0)
					return null;
				return entries[index].ParsedFile;
			}
		}
		
		public ParseInformation GetCachedParseInformation(ITextSourceVersion version, IProject parentProject)
		{
			lock (this) {
				if (version != null && CompareVersions(version) != 0) {
					return null;
				}
				int index = FindIndexForProject(parentProject);
				if (index < 0)
					return null;
				return entries[index].CachedParseInformation;
			}
		}
		#endregion
		
		#region Parse
		public ParseInformation Parse(ITextSource fileContent, IProject parentProject, CancellationToken cancellationToken)
		{
			if (fileContent == null) {
				fileContent = SD.FileService.GetFileContent(fileName);
			}
			
			return DoParse(fileContent, parentProject, true, cancellationToken).CachedParseInformation;
		}
		
		public IParsedFile ParseFile(ITextSource fileContent, IProject parentProject, CancellationToken cancellationToken)
		{
			if (fileContent == null) {
				fileContent = SD.FileService.GetFileContent(fileName);
			}
			
			return DoParse(fileContent, parentProject, false, cancellationToken).ParsedFile;
		}
		
		ProjectEntry DoParse(ITextSource fileContent, IProject parentProject, bool fullParseInformationRequested,
		                     CancellationToken cancellationToken)
		{
			if (parser == null)
				return default(ProjectEntry);
			
			if (fileContent == null) {
				// No file content was specified. Because the callers of this method already check for currently open files,
				// we can assume that the file isn't open and simply read it from disk.
				try {
					fileContent = SD.FileService.GetFileContentFromDisk(fileName, cancellationToken);
				} catch (IOException) {
					// It is possible that the file gets deleted/becomes inaccessible while a background parse
					// operation is enqueued, so we have to handle IO exceptions.
					return default(ProjectEntry);
				} catch (UnauthorizedAccessException) {
					return default(ProjectEntry);
				}
			}
			
			ProjectEntry result;
			lock (this) {
				int index = FindIndexForProject(parentProject);
				int versionComparison = CompareVersions(fileContent.Version);
				if (versionComparison > 0 || index < 0) {
					// We're going backwards in time, or are requesting a project that is not an owner
					// for this entry.
					var parseInfo = parser.Parse(fileName, fileContent, fullParseInformationRequested, parentProject, cancellationToken);
					FreezableHelper.Freeze(parseInfo.ParsedFile);
					return new ProjectEntry(parentProject, parseInfo.ParsedFile, parseInfo);
				} else {
					if (versionComparison == 0 && index >= 0) {
						// If full parse info is requested, ensure we have full parse info.
						if (!(fullParseInformationRequested && entries[index].CachedParseInformation == null)) {
							// We already have the requested version parsed, just return it:
							return entries[index];
						}
					}
				}
				
				ParseInformationEventArgs[] results = new ParseInformationEventArgs[entries.Count];
				for (int i = 0; i < entries.Count; i++) {
					ParseInformation parseInfo;
					try {
						parseInfo = parser.Parse(fileName, fileContent, fullParseInformationRequested, entries[i].Project, cancellationToken);
					} catch (Exception ex) {
						SD.LoggingService.Error("Got " + ex.GetType().Name + " while parsing " + fileName);
						throw;
					}
					if (parseInfo == null)
						throw new NullReferenceException(parser.GetType().Name + ".Parse() returned null");
					if (fullParseInformationRequested && !parseInfo.IsFullParseInformation)
						throw new InvalidOperationException(parser.GetType().Name + ".Parse() did not return full parse info as requested.");
					OnDiskTextSourceVersion onDiskVersion = fileContent.Version as OnDiskTextSourceVersion;
					if (onDiskVersion != null)
						parseInfo.ParsedFile.LastWriteTime = onDiskVersion.LastWriteTime;
					FreezableHelper.Freeze(parseInfo.ParsedFile);
					results[i] = new ParseInformationEventArgs(entries[i].Project, entries[i].ParsedFile, parseInfo);
				}
				
				// Only if all parse runs succeeded, register the parse information.
				currentVersion = fileContent.Version;
				for (int i = 0; i < entries.Count; i++) {
					if (fullParseInformationRequested || (entries[i].CachedParseInformation != null && results[i].NewParseInformation.IsFullParseInformation))
						entries[i] = new ProjectEntry(entries[i].Project, results[i].NewParsedFile, results[i].NewParseInformation);
					else
						entries[i] = new ProjectEntry(entries[i].Project, results[i].NewParsedFile, null);
					if (entries[i].Project != null)
						entries[i].Project.OnParseInformationUpdated(results[i]);
					parserService.RaiseParseInformationUpdated(results[i]);
				}
				result = entries[index];
			}  // exit lock
			parserService.RegisterForCacheExpiry(this);
			return result;
		}
		#endregion
		
		#region ParseAsync
		Task<ProjectEntry> runningAsyncParseTask;
		ITextSourceVersion runningAsyncParseFileContentVersion;
		bool runningAsyncParseFullInfoRequested;
		
		public async Task<ParseInformation> ParseAsync(ITextSource fileContent, IProject parentProject, CancellationToken cancellationToken)
		{
			return (await DoParseAsync(fileContent, parentProject, true, cancellationToken)).CachedParseInformation;
		}
		
		public async Task<IParsedFile> ParseFileAsync(ITextSource fileContent, IProject parentProject, CancellationToken cancellationToken)
		{
			return (await DoParseAsync(fileContent, parentProject, false, cancellationToken)).ParsedFile;
		}
		
		Task<ProjectEntry> DoParseAsync(ITextSource fileContent, IProject parentProject, bool requestFullParseInformation, CancellationToken cancellationToken)
		{
			// Create snapshot of file content, if required
			bool lookupOpenFileOnTargetThread;
			if (fileContent != null) {
				lookupOpenFileOnTargetThread = false;
				// File content was explicitly specified:
				// Let's make a snapshot in case the text source is mutable.
				fileContent = fileContent.CreateSnapshot();
			} else if (SD.MainThread.InvokeRequired) {
				// fileContent == null && not on the main thread:
				// Don't fetch the file content right now; if we need to SafeThreadCall() anyways,
				// it's better to do so from the background task.
				lookupOpenFileOnTargetThread = true;
			} else {
				// fileContent == null && we are on the main thread:
				// Let's look up the file in the list of open files right now
				// so that we don't need to SafeThreadCall() later on.
				lookupOpenFileOnTargetThread = false;
				fileContent = SD.FileService.GetFileContentForOpenFile(fileName);
			}
			Task<ProjectEntry> task;
			lock (this) {
				if (fileContent != null) {
					// Optimization:
					// don't start a background task if fileContent was specified and up-to-date parse info is available
					int index = FindIndexForProject(parentProject);
					int versionComparison = CompareVersions(fileContent.Version);
					if (versionComparison == 0 && index >= 0) {
						// If full parse info is requested, ensure we have full parse info.
						if (!(requestFullParseInformation && entries[index].CachedParseInformation == null)) {
							// We already have the requested version parsed, just return it:
							return Task.FromResult(entries[index]);
						}
					}
					// Optimization:
					// if an equivalent task is already running, return that one instead
					if (runningAsyncParseTask != null && (!requestFullParseInformation || runningAsyncParseFullInfoRequested)
					    && runningAsyncParseFileContentVersion.BelongsToSameDocumentAs(fileContent.Version)
					    && runningAsyncParseFileContentVersion.CompareAge(fileContent.Version) == 0)
					{
						return runningAsyncParseTask;
					}
				}
				task = new Task<ProjectEntry>(
					delegate {
						try {
							if (lookupOpenFileOnTargetThread) {
								fileContent = SD.FileService.GetFileContentForOpenFile(fileName);
							}
							return DoParse(fileContent, parentProject, requestFullParseInformation, cancellationToken);
						} finally {
							lock (this) {
								runningAsyncParseTask = null;
								runningAsyncParseFileContentVersion = null;
							}
						}
					}, cancellationToken);
				if (fileContent != null && fileContent.Version != null && !cancellationToken.CanBeCanceled) {
					runningAsyncParseTask = task;
					runningAsyncParseFileContentVersion = fileContent.Version;
					runningAsyncParseFullInfoRequested = requestFullParseInformation;
				}
			}
			task.Start();
			return task;
		}
		#endregion
		
		public void RegisterParsedFile(IProject project, IParsedFile parsedFile)
		{
			if (project == null)
				throw new ArgumentNullException("project");
			if (parsedFile == null)
				throw new ArgumentNullException("parsedFile");
			FreezableHelper.Freeze(parsedFile);
			var newParseInfo = new ParseInformation(parsedFile, false);
			lock (this) {
				int index = FindIndexForProject(project);
				if (index >= 0) {
					currentVersion = null;
					var args = new ParseInformationEventArgs(project, entries[index].ParsedFile, newParseInfo);
					entries[index] = new ProjectEntry(project, parsedFile, null);
					project.OnParseInformationUpdated(args);
					parserService.RaiseParseInformationUpdated(args);
				}
			}
		}
	}
}
