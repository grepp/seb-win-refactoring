﻿/*
 * Copyright (c) 2019 ETH Zürich, Educational Development and Technology (LET)
 * 
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using SafeExamBrowser.Logging.Contracts;
using SafeExamBrowser.Monitoring.Contracts.Applications;
using SafeExamBrowser.Monitoring.Contracts.Applications.Events;
using SafeExamBrowser.Settings.Applications;
using SafeExamBrowser.WindowsApi.Contracts;

namespace SafeExamBrowser.Monitoring.Applications
{
	public class ApplicationMonitor : IApplicationMonitor
	{
		private IntPtr activeWindow;
		private IList<BlacklistApplication> blacklist;
		private Guid? captureHookId;
		private Guid? foregroundHookId;
		private ILogger logger;
		private INativeMethods nativeMethods;
		private IList<IProcess> processes;
		private IProcessFactory processFactory;
		private Timer timer;
		private IList<WhitelistApplication> whitelist;

		public event ExplorerStartedEventHandler ExplorerStarted;
		public event TerminationFailedEventHandler TerminationFailed;

		public ApplicationMonitor(int interval_ms, ILogger logger, INativeMethods nativeMethods, IProcessFactory processFactory)
		{
			this.blacklist = new List<BlacklistApplication>();
			this.logger = logger;
			this.nativeMethods = nativeMethods;
			this.processes = new List<IProcess>();
			this.processFactory = processFactory;
			this.timer = new Timer(interval_ms);
			this.whitelist = new List<WhitelistApplication>();
		}

		public InitializationResult Initialize(ApplicationSettings settings)
		{
			var result = new InitializationResult();

			foreach (var application in settings.Blacklist)
			{
				blacklist.Add(application);
			}

			foreach (var application in settings.Whitelist)
			{
				whitelist.Add(application);
			}

			logger.Debug($"Initialized blacklist with {blacklist.Count} applications{(blacklist.Any() ? $": {string.Join(", ", blacklist.Select(a => a.ExecutableName))}" : ".")}");
			logger.Debug($"Initialized whitelist with {whitelist.Count} applications{(whitelist.Any() ? $": {string.Join(", ", whitelist.Select(a => a.ExecutableName))}" : ".")}");

			processes = processFactory.GetAllRunning();

			foreach (var process in processes)
			{
				foreach (var application in blacklist)
				{
					var isBlacklisted = BelongsToApplication(process, application);

					if (isBlacklisted && !application.AutoTerminate)
					{
						AddForTermination(application.ExecutableName, process, result);
					}
					else if (isBlacklisted && application.AutoTerminate && !TryTerminate(process))
					{
						AddFailed(application.ExecutableName, process, result);
					}
				}

				foreach (var application in whitelist)
				{
					// TODO: Check if application is running, auto-terminate or add to result.
				}
			}

			return result;
		}

		public void Start()
		{
			timer.AutoReset = false;
			timer.Elapsed += Timer_Elapsed;
			timer.Start();
			logger.Info("Started monitoring applications.");

			captureHookId = nativeMethods.RegisterSystemCaptureStartEvent(SystemEvent_WindowChanged);
			logger.Info($"Registered system capture start event with ID = {captureHookId}.");

			foregroundHookId = nativeMethods.RegisterSystemForegroundEvent(SystemEvent_WindowChanged);
			logger.Info($"Registered system foreground event with ID = {foregroundHookId}.");
		}

		public void Stop()
		{
			timer.Stop();
			timer.Elapsed -= Timer_Elapsed;
			logger.Info("Stopped monitoring applications.");

			if (captureHookId.HasValue)
			{
				nativeMethods.DeregisterSystemEventHook(captureHookId.Value);
				logger.Info($"Unregistered system capture start event with ID = {captureHookId}.");
			}

			if (foregroundHookId.HasValue)
			{
				nativeMethods.DeregisterSystemEventHook(foregroundHookId.Value);
				logger.Info($"Unregistered system foreground event with ID = {foregroundHookId}.");
			}
		}

		public bool TryTerminate(RunningApplication application)
		{
			var success = true;

			foreach (var process in application.Processes)
			{
				success &= TryTerminate(process);
			}

			return success;
		}

		private void SystemEvent_WindowChanged(IntPtr window)
		{
			if (window != IntPtr.Zero && activeWindow != window)
			{
				logger.Debug($"Window has changed from {activeWindow} to {window}.");
				activeWindow = window;

				Task.Run(() =>
				{
					if (!IsAllowed(window) && !TryHide(window))
					{
						Close(window);
					}
				});
			}
		}

		private void Timer_Elapsed(object sender, ElapsedEventArgs e)
		{
			var failed = new List<RunningApplication>();
			var running = processFactory.GetAllRunning();
			var started = running.Where(r => processes.All(p => p.Id != r.Id)).ToList();
			var terminated = processes.Where(p => running.All(r => r.Id != p.Id)).ToList();

			foreach (var process in started)
			{
				logger.Debug($"Process {process} has been started.");
				processes.Add(process);

				if (process.Name == "explorer")
				{
					HandleExplorerStart(process);
				}
				else if (!IsAllowed(process) && !TryTerminate(process))
				{
					AddFailed(process, failed);
				}
			}

			foreach (var process in terminated)
			{
				logger.Debug($"Process {process} has been terminated.");
				processes.Remove(process);
			}

			if (failed.Any())
			{
				logger.Warn($"Failed to terminate these blacklisted applications: {string.Join(", ", failed.Select(a => a.Name))}.");
				TerminationFailed?.Invoke(failed);
			}

			timer.Start();
		}

		private void AddFailed(IProcess process, List<RunningApplication> failed)
		{
			var name = blacklist.First(a => BelongsToApplication(process, a)).ExecutableName;
			var application = failed.FirstOrDefault(a => a.Name == name);

			if (application == default(RunningApplication))
			{
				application = new RunningApplication(name);
				failed.Add(application);
			}

			application.Processes.Add(process);
		}

		private void AddFailed(string name, IProcess process, InitializationResult result)
		{
			var application = result.FailedAutoTerminations.FirstOrDefault(a => a.Name == name);

			if (application == default(RunningApplication))
			{
				application = new RunningApplication(name);
				result.FailedAutoTerminations.Add(application);
			}

			application.Processes.Add(process);
			logger.Error($"Process {process} belongs to application '{application.Name}' and could not be terminated automatically!");
		}

		private void AddForTermination(string name, IProcess process, InitializationResult result)
		{
			var application = result.RunningApplications.FirstOrDefault(a => a.Name == name);

			if (application == default(RunningApplication))
			{
				application = new RunningApplication(name);
				result.RunningApplications.Add(application);
			}

			application.Processes.Add(process);
			logger.Debug($"Process {process} belongs to application '{application.Name}' and needs to be terminated.");
		}

		private bool BelongsToApplication(IProcess process, BlacklistApplication application)
		{
			var sameName = process.Name.Equals(application.ExecutableName, StringComparison.OrdinalIgnoreCase);
			var sameOriginalName = process.OriginalName?.Equals(application.ExecutableOriginalName, StringComparison.OrdinalIgnoreCase) == true;

			return sameName || sameOriginalName;
		}

		private void Close(IntPtr window)
		{
			var title = nativeMethods.GetWindowTitle(window);

			nativeMethods.SendCloseMessageTo(window);
			logger.Info($"Sent close message to window '{title}' with handle = {window}.");
		}

		private void HandleExplorerStart(IProcess process)
		{
			logger.Warn($"A new instance of Windows Explorer {process} has been started!");

			if (!TryTerminate(process))
			{
				var application = new RunningApplication("Windows Explorer");

				logger.Error("Failed to terminate new Windows Explorer instance!");
				application.Processes.Add(process);

				Task.Run(() => TerminationFailed?.Invoke(new[] { application }));
			}

			Task.Run(() => ExplorerStarted?.Invoke());
		}

		private bool IsAllowed(IntPtr window)
		{
			var processId = nativeMethods.GetProcessIdFor(window);
			// TODO: Allow only if in whitelist!
			//var process = processFactory.GetById(Convert.ToInt32(processId));

			//if (process != null)
			//{
			//	var allowed = process.Name == "SafeExamBrowser" || process.Name == "SafeExamBrowser.Client";

			//	if (!allowed)
			//	{
			//		logger.Warn($"Window with handle = {window} belongs to not allowed process '{process.Name}'!");
			//	}

			//	return allowed;
			//}

			return true;
		}

		private bool IsAllowed(IProcess process)
		{
			foreach (var application in blacklist)
			{
				if (BelongsToApplication(process, application))
				{
					logger.Warn($"Process {process} belongs to blacklisted application '{application.ExecutableName}'!");

					return false;
				}
			}

			return true;
		}

		private bool TryHide(IntPtr window)
		{
			var title = nativeMethods.GetWindowTitle(window);
			var success = nativeMethods.HideWindow(window);

			if (success)
			{
				logger.Info($"Hid window '{title}' with handle = {window}.");
			}
			else
			{
				logger.Warn($"Failed to hide window '{title}' with handle = {window}!");
			}

			return success;
		}

		private bool TryTerminate(IProcess process)
		{
			const int MAX_ATTEMPTS = 5;
			const int TIMEOUT = 500;

			for (var attempt = 0; attempt < MAX_ATTEMPTS; attempt++)
			{
				if (process.TryClose(TIMEOUT))
				{
					break;
				}
			}

			if (!process.HasTerminated)
			{
				for (var attempt = 0; attempt < MAX_ATTEMPTS; attempt++)
				{
					if (process.TryKill(TIMEOUT))
					{
						break;
					}
				}
			}

			if (process.HasTerminated)
			{
				logger.Info($"Successfully terminated process {process}.");
			}
			else
			{
				logger.Warn($"Failed to terminate process {process}!");
			}

			return process.HasTerminated;
		}
	}
}