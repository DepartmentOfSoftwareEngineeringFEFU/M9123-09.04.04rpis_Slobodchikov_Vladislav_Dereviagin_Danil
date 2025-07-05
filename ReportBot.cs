using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Lime;
using Match;
using Python.Runtime;
using RemoteScriptingClient;

namespace QualityAssurance.Utility
{
	public static class ReportBot
	{
		private static bool isReportScheduled;

		public static async Coroutine ScheduleReport(
			Func<IImplementation> implementationFactory,
			TestCoroutine test,
			CoroutineBase parentCoroutine,
			bool sendMessageOnMessageBox = true,
			IEnumerable<IReportContributor> reportContributors = null
		) {
			if (isReportScheduled) {
				return;
			}
			var implementation = implementationFactory.Invoke();
			if (!await implementation.TryInitialize()) {
				return;
			}

			var stopwatch = new Stopwatch();
			parentCoroutine.CreateFinallyBlock(stopwatch.Stop);
			var gameTime = 0d;
			var semaphore = new SemaphoreSlim(1, 1);
			Task task = null;
			task = RemoteScriptingManager.Instance.Environment.Tasks.Add(
				TestMonitoringTask(),
				RemoteScriptingManager.Instance.Environment.TasksTag
			);
			task.Advance(0);
			stopwatch.Start();

			IEnumerator<object> TestMonitoringTask()
			{
				var window = (Window)RemoteScriptingManager.Instance.Environment.Window;
				try {
					isReportScheduled = true;
					if (sendMessageOnMessageBox) {
						MessageBox.Showing += MessageBoxShowing;
					}
					window.UnhandledExceptionOnUpdate += ExceptionOnUpdateHandler;
					if (reportContributors != null) {
						foreach (var reportContributor in reportContributors) {
							reportContributor.TestStarting();
						}
					}

					SendMessageAsync("Running", string.Empty);
					var wasTestStopped = true;
					var wasBlockingBackgroundWorker = false;
					var lastFrame = -1L;
					try {
						while (!test.IsCompleted) {
							var app = The.Application;
							var existBlockingBackgroundWorker = RemoteScriptingManager.Instance.Environment.ExistBlockingBackgroundWorker;
							if (!wasBlockingBackgroundWorker && existBlockingBackgroundWorker) {
								lastFrame = app.CurrentFrameIndex;
								wasBlockingBackgroundWorker = true;
							} else if (wasBlockingBackgroundWorker && !existBlockingBackgroundWorker) {
								wasBlockingBackgroundWorker = false;
							}
							gameTime += !existBlockingBackgroundWorker
								? (double)Task.Current.Delta
								: (app.CurrentFrameIndex != lastFrame ? app.LastUpdateFrameTime.TotalSeconds : 0d);
							lastFrame = app.CurrentFrameIndex;
							yield return null;
						}
						wasTestStopped = false;
					} finally {
						if (wasTestStopped) {
							RemoteScriptingManager.Instance.Environment.Log(
								"Report would not be send to messenger due to the test being stopped."
							);
						}
					}
				} finally {
					if (sendMessageOnMessageBox) {
						MessageBox.Showing -= MessageBoxShowing;
					}
					window.UnhandledExceptionOnUpdate -= ExceptionOnUpdateHandler;
					if (reportContributors != null) {
						foreach (var reportContributor in reportContributors) {
							reportContributor.TestStopped();
						}
					}
					isReportScheduled = false;
				}
				SendMessageAsync();
			}

			void MessageBoxShowing(MessageBox messageBox)
			{
				RemoteScriptingManager.Instance.Environment.Tasks.Add(
					MessageBoxMonitoringTask(),
					RemoteScriptingManager.Instance.Environment.TasksTag
				);

				IEnumerator<object> MessageBoxMonitoringTask()
				{
					const long MessageDelay = 20000;

					var stopwatch = new Stopwatch();
					try {
						stopwatch.Start();
						var lastMousePosition = RemoteScriptingManager.Instance.Environment.WindowInput.MousePosition;
						while (stopwatch.ElapsedMilliseconds < MessageDelay) {
							if (messageBox.IsClosed) {
								yield break;
							}
							var mousePosition = RemoteScriptingManager.Instance.Environment.WindowInput.MousePosition;
							if (mousePosition != lastMousePosition) {
								lastMousePosition = mousePosition;
								stopwatch.Restart();
							}
							yield return null;
						}
					} finally {
						stopwatch.Stop();
					}
					SendMessageAsync("WaitingForUserAction", string.Empty);
				}
			}

			void ExceptionOnUpdateHandler(Exception exception)
			{
				RemoteScriptingManager.Instance.Environment.Log(
					$"Unhandled exception on update: {exception}\nTrying to send report to messenger and close application."
				);
				RemoteScriptingManager.Instance.Environment.Tasks.StopByTag(RemoteScriptingManager.Instance.Environment.TasksTag);
				TimeFlow.DisposeAll();
				WindowsFormLocker.DisposeAll();
				foreach (var dialog in Dialog.VisibleDialogs) {
					dialog.Root.Unlink();
				}
				SendMessageAsync("CrashedGame", exception.ToString(), requireApplicationShutdown: true);
				RemoteScriptingManager.Instance.Environment.Tasks.Add(DefferedApplicationExitTask());
			}

			async System.Threading.Tasks.Task SendKiwi()
			{
				using (Py.GIL()) {
					dynamic scope = Py.CreateScope();
					string pythonCode = File.ReadAllText(@"E:\KiwiScripts\KiwiScripts\SingleScripts\change_tester.py");
					scope.Exec(pythonCode);
				}
			}

			async void SendMessageAsync(string overrideStatus = null, string overrideLog = null, bool requireApplicationShutdown = false)
			{
				var task = RemoteScriptingManager.Instance.Environment.Tasks.Add(
					Task.Repeat(() => true),
					RemoteScriptingManager.Instance.Environment.TasksTag
				);
				try {
					var report = new Report {
						TestName = test.GetType().Name,
						TestStatus = overrideStatus ?? test.Status.ToString(),
						TestDuration = stopwatch.Elapsed,
						TestTotalDeltaTime = TimeSpan.FromSeconds(gameTime),
					};
					if (reportContributors != null) {
						foreach (var reportContributor in reportContributors) {
							reportContributor.Contribute(report);
						}
					}
					report.AddEntry("Exception", test.Exception?.ToString());
					report.AddEntry("Log", overrideLog);
					if (reportContributors != null) {
						foreach (var reportContributor in reportContributors) {
							reportContributor.ContributeLate(report);
						}
					}
					await semaphore.WaitAsync().ConfigureAwait(continueOnCapturedContext: false);
					try {
						await implementation.SendAsync(report).ConfigureAwait(continueOnCapturedContext: true);
						//Console.WriteLine(report);
						//await SendKiwi();
					} finally {
						semaphore.Release();
					}
					if (requireApplicationShutdown) {
						System.Environment.Exit(-1);
					}
				} catch (Exception exception) {
					RemoteScriptingManager.Instance.Environment.Log(
						$"Report was not send to messenger due to exception: {exception}"
					);
				} finally {
					RemoteScriptingManager.Instance.Environment.Tasks.Stop(t => t == task);
				}
			}

		static IEnumerator<object> DefferedApplicationExitTask()
			{
				yield return 120;
				System.Environment.Exit(-1);
			}
		}

		public interface IImplementation
		{
			Coroutine<bool> TryInitialize();
			System.Threading.Tasks.Task SendAsync(Report report);
		}

		public interface IReportContributor
		{
			void TestStarting() { }
			void TestStopped() { }
			void Contribute(Report report) { }
			void ContributeLate(Report report) { }
		}

		public class DelegateReportContributor : IReportContributor
		{
			private readonly Action<Report> action;

			public DelegateReportContributor(Action<Report> action)
			{
				this.action = action;
			}

			void IReportContributor.Contribute(Report report) => action?.Invoke(report);
		}

		public class Report
		{
			public string TestName { get; init; }
			public string TestStatus { get; init; }
			public TimeSpan TestDuration { get; init; }
			public TimeSpan TestTotalDeltaTime { get; init; }
			public List<Entry> Entries { get; } = new ();
			public Dictionary<Type, object> Extensions { get; } = new ();

			public void AddEntry(string header, string content) => InsertEntry(Entries.Count, header, content);

			public void PushEntry(string header, string content) => InsertEntry(0, header, content);

			public void InsertEntry(int index, string header, string content)
			{
				if (!string.IsNullOrWhiteSpace(content)) {
					Entries.Insert(index, new Entry { Header = header, Content = content });
				}
			}

			public void SetExtension<T>(T extension) where T : class
			{
				Extensions[typeof(T)] = extension;
			}

			public T GetExtension<T>() where T : class
			{
				Extensions.TryGetValue(typeof(T), out var value);
				return value as T;
			}

			public double GetTestPerformanceRatio()
			{
				var duration = TestDuration.TotalSeconds;
				return duration > Mathf.ZeroTolerance ? TestTotalDeltaTime.TotalSeconds / duration : 0d;
			}

			public string GetTestDurationFormatted() => GetFormattedTime(TestDuration);

			public string GetTestTotalDeltaTimeFormatted() => GetFormattedTime(TestTotalDeltaTime);

			private static string GetFormattedTime(TimeSpan time)
			{
				var sb = new StringBuilder();
				if (time.TotalDays >= 1) {
					sb.Append(time.ToString("%d"));
					sb.Append(" day(s) and ");
				}
				sb.Append(time.ToString("hh\\:mm\\:ss"));
				return sb.ToString();
			}

			public class Entry
			{
				public string Header { get; init; }
				public string Content { get; init; }
			}
		}
	}
}
