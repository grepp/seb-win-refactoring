﻿/*
 * Copyright (c) 2018 ETH Zürich, Educational Development and Technology (LET)
 * 
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using SafeExamBrowser.Client.Behaviour.Operations;
using SafeExamBrowser.Contracts.Logging;
using SafeExamBrowser.Contracts.Monitoring;
using SafeExamBrowser.Contracts.UserInterface;
using SafeExamBrowser.Contracts.UserInterface.Taskbar;

namespace SafeExamBrowser.Client.UnitTests.Behaviour.Operations
{
	[TestClass]
	public class DisplayMonitorOperationTests
	{
		private Mock<IDisplayMonitor> displayMonitorMock;
		private Mock<ILogger> loggerMock;
		private Mock<ISplashScreen> splashScreenMock;
		private Mock<ITaskbar> taskbarMock;

		private DisplayMonitorOperation sut;

		[TestInitialize]
		public void Initialize()
		{
			loggerMock = new Mock<ILogger>();
			displayMonitorMock = new Mock<IDisplayMonitor>();
			splashScreenMock = new Mock<ISplashScreen>();
			taskbarMock = new Mock<ITaskbar>();

			sut = new DisplayMonitorOperation(displayMonitorMock.Object, loggerMock.Object, taskbarMock.Object)
			{
				SplashScreen = splashScreenMock.Object
			};
		}

		[TestMethod]
		public void MustPerformCorrectly()
		{
			var order = 0;

			displayMonitorMock.Setup(d => d.PreventSleepMode()).Callback(() => Assert.AreEqual(++order, 1));
			displayMonitorMock.Setup(d => d.InitializePrimaryDisplay(It.IsAny<int>())).Callback(() => Assert.AreEqual(++order, 2));
			displayMonitorMock.Setup(d => d.StartMonitoringDisplayChanges()).Callback(() => Assert.AreEqual(++order, 3));

			sut.Perform();

			displayMonitorMock.Verify(d => d.PreventSleepMode(), Times.Once);
			displayMonitorMock.Verify(d => d.InitializePrimaryDisplay(It.IsAny<int>()), Times.Once);
			displayMonitorMock.Verify(d => d.StartMonitoringDisplayChanges(), Times.Once);
		}

		[TestMethod]
		public void MustRevertCorrectly()
		{
			var order = 0;

			displayMonitorMock.Setup(d => d.StopMonitoringDisplayChanges()).Callback(() => Assert.AreEqual(++order, 1));
			displayMonitorMock.Setup(d => d.ResetPrimaryDisplay()).Callback(() => Assert.AreEqual(++order, 2));

			sut.Revert();

			displayMonitorMock.Verify(d => d.StopMonitoringDisplayChanges(), Times.Once);
			displayMonitorMock.Verify(d => d.ResetPrimaryDisplay(), Times.Once);
		}
	}
}