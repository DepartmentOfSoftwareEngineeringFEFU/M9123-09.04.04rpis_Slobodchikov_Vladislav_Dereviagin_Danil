using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Lime;
using Match;
using Match.Extensions;
using QualityAssurance.Tests;
using RemoteScriptingClient;
using Yuzu;
using Yuzu.Json;
using Python.Runtime;

namespace QualityAssurance.Utility
{
	public class TravelMatchReportBot : ReportBot.IImplementation
	{
		private const string ReportConfigurationFileName = "ReportConfiguration.json";

		private static readonly Func<ReportBot.IImplementation> factory = () => new TravelMatchReportBot();

		private readonly object @lock = new ();
		private readonly List<Chat> chats = [];
		private ReportConfiguration configuration;
		private TelegramClient telegram;
		private TelegramEscaper escaper;

		private bool secondSendFlag = false;

		public static Coroutine ScheduleReport(
			TestCoroutine test,
			CoroutineBase parentCoroutine,
			TestParameters[] testParameters = null,
			IEnumerable<ReportBot.IReportContributor> reportContributors = null,
			bool sendMessageOnMessageBox = true
		) {
			var extraReportContributors = new List<ReportBot.IReportContributor> {
				new KeywordsContributor(testParameters),
				new TimeFlowContributor(testParameters),
				new ParametersContributor(testParameters),
				new ConsoleContributor(),
				new BundlesContributor(),
			};
			if (reportContributors != null) {
				extraReportContributors.AddRange(reportContributors);
			}
			return ReportBot.ScheduleReport(
				factory,
				test,
				parentCoroutine,
				sendMessageOnMessageBox,
				extraReportContributors
			);
		}

		public async Coroutine<bool> TryInitialize()
		{
			byte[] file = null;
			Runtime.PythonDLL = @"C:\Users\User\AppData\Local\Programs\Python\Python312\python312.dll";
			PythonEngine.Initialize();

			if (RemoteScriptingClientExtension.IsLaunchedWithLocalScriptData()) {
				try {
					file = File.ReadAllBytes($"../../../RemoteStorage/{ReportConfigurationFileName}");
				} catch {
					// supress
				}
			} else {
				file = await Command.RequestRemoteFile(
					ReportConfigurationFileName,
					new TaskParameters {
						IsStrictly = false,
						Duration = float.MaxValue,
					}
				);
			}
			configuration = ReportConfiguration.Parse(file);
			if (configuration == null) {
				RemoteScriptingManager.Instance.Environment.Log(
					"Report would not be send to messenger due to absence of the configuration file."
				);
				return false;
			}
			if (!configuration.IsValid) {
				configuration = null;
				RemoteScriptingManager.Instance.Environment.Log(
					"Report would not be send to messenger due to errors in the configuration file."
				);
				return false;
			}
			return true;
		}

		public async System.Threading.Tasks.Task SendAsync(ReportBot.Report report)
		{
			if (configuration == null) {
				return;
			}
			lock (@lock) {
				if (telegram == null) {
					telegram = new TelegramClient(configuration.AuthToken, GameHttpClient.Instance.Client);
					telegram.OnApiResponseReceived += response =>
						RemoteScriptingManager.Instance.Environment.Log(
							$"{nameof(TelegramClient)}.{nameof(TelegramClient.OnApiResponseReceived)}():\n\t{response}"
						);
					escaper = new TelegramEscaper();
					foreach (var chatId in configuration.EnumerateChatIds()) {
						chats.Add(new Chat { Id = chatId });
					}
				}
			}
			var stringBuilder = new StringBuilder();
			// Device information line
			stringBuilder.Append(configuration.DeviceInformation);
			stringBuilder.Append("\n\n");
			// Test status line
			stringBuilder.Append("\\#");
			stringBuilder.Append(escaper.EscapeMarkdownV2(report.TestName));
			stringBuilder.Append(" \\#");
			stringBuilder.Append(escaper.EscapeMarkdownV2(Hashtag(report.TestStatus)));
			var keywordsContributor = report.GetExtension<KeywordsContributor>();
			if (keywordsContributor != null && keywordsContributor.Keywords.Count > 0) {
				stringBuilder.Append('\n');
				stringBuilder.AppendJoin("; ", keywordsContributor.Keywords.Select(keyword => escaper.EscapeMarkdownV2(keyword)));
			}
			stringBuilder.Append("\n\n");
			// Test time line
			if (report.TestTotalDeltaTime > TimeSpan.Zero) {
				stringBuilder.Append("\U0001F55D Testing Duration: ");
				stringBuilder.Append(escaper.EscapeMarkdownV2(report.GetTestDurationFormatted()));
				stringBuilder.Append("; Total deltaTime during the test: ");
				stringBuilder.Append(escaper.EscapeMarkdownV2(report.GetTestTotalDeltaTimeFormatted()));
				var testPerformanceRatio = report.GetTestPerformanceRatio();
				if (testPerformanceRatio > 0) {
					stringBuilder.Append(" \\(x");
					stringBuilder.Append(escaper.EscapeMarkdownV2(testPerformanceRatio.ToString("F1")));
					stringBuilder.Append("\\)");
				}
				var testTimeFlow = report.GetExtension<TimeFlowContributor>()?.TestTimeFlow;
				if (testTimeFlow.HasValue) {
					stringBuilder.Append("; Time flow: ");
					stringBuilder.Append(testTimeFlow.Value);
				}
				stringBuilder.Append('\n');
			}
			// Test console
			var consoleContributor = report.GetExtension<ConsoleContributor>();
			if (consoleContributor != null && !string.IsNullOrWhiteSpace(consoleContributor.Status)) {
				stringBuilder.Append("\U000026A0 ");
				stringBuilder.Append(escaper.EscapeMarkdownV2(consoleContributor.Status));
				stringBuilder.Append('\n');
			}
			// Build line
			stringBuilder.Append("\U0001F3F7 Built on commit \\#");
			var sha = RemoteScriptingClientExtension.BuildInfoWrapper.Sha;
			stringBuilder.Append(sha[..Math.Min(sha.Length, 8)]);
			stringBuilder.Append(", branch ");
			if (RemoteScriptingClientExtension.BuildInfoWrapper.Branch != "Unknown branch name") {
				stringBuilder.Append("\\#");
				stringBuilder.Append(escaper.EscapeMarkdownV2(Hashtag(RemoteScriptingClientExtension.BuildInfoWrapper.Branch)));
			} else if (!string.IsNullOrWhiteSpace(configuration.BranchName)) {
				stringBuilder.Append("\\#");
				stringBuilder.Append(escaper.EscapeMarkdownV2(Hashtag(configuration.BranchName)));
			} else {
				stringBuilder.Append(RemoteScriptingClientExtension.BuildInfoWrapper.Branch);
			}
			stringBuilder.Append('\n');
			// Device line
			var isMobileDevice = Lime.Application.Platform is PlatformId.Android or PlatformId.iOS;
			stringBuilder.Append(isMobileDevice ? "\U0001F4F1" : "\U0001F4BB");
			stringBuilder.Append(" The test was executed on an \\#");
			stringBuilder.Append(Lime.Application.Platform);
			stringBuilder.Append(" device \\(");
			stringBuilder.Append(Lime.Application.RenderingBackend);
			stringBuilder.Append("\\)");
			if (isMobileDevice) {
				stringBuilder.Append(" \"");
				stringBuilder.Append(escaper.EscapeMarkdownV2(RemoteScriptingBehavior.GetDeviceName()));
				stringBuilder.Append('"');
			}
			// Done
			var text = stringBuilder.ToString();
			stringBuilder.Clear();

			byte[] reportFile = null;
			if (report.Entries.Count > 0) {
				for (var i = 0; i < report.Entries.Count; i++) {
					stringBuilder.Append("# ");
					stringBuilder.Append(report.Entries[i].Header);
					stringBuilder.Append("\n\n");
					stringBuilder.Append(report.Entries[i].Content);
					if (i < report.Entries.Count - 1) {
						stringBuilder.Append("\n\n\n\n");
					}
				}
				var reportText = stringBuilder.ToString();
				stringBuilder.Clear();
				reportFile = Encoding.UTF8.GetBytes(reportText);
			}

			const int RequestDelay = 500;
			const string ParseMode = "MarkdownV2";
			for (var i = 0; i < chats.Count; i++) {
				if (i != 0) {
					await System.Threading.Tasks.Task.Delay(RequestDelay).ConfigureAwait(continueOnCapturedContext: false);
				}
				var chat = chats[i];
				var logFormat = nameof(TelegramClient) + ".{0}(" + chat.Id.ToString() + "{1}) calling...";
				Message message = null;
				if (reportFile != null) {
					RemoteScriptingManager.Instance.Environment.Log(
						string.Format(logFormat, nameof(TelegramClient.SendDocumentAsync), string.Empty)
					);
					var inputFile = new InputFileStream(new MemoryStream(reportFile), "Report.txt");

					if (secondSendFlag) {
						using (Py.GIL()) {
							dynamic scope = Py.CreateScope();

							scope.testName = report.TestName;
							scope.testStatus = report.TestStatus;
							scope.testDuration = escaper.EscapeMarkdownV2(report.GetTestDurationFormatted());
							scope.deltaTime = escaper.EscapeMarkdownV2(report.GetTestTotalDeltaTimeFormatted());
							var testTimeFlow = report.GetExtension<TimeFlowContributor>()?.TestTimeFlow;
							scope.timeFlow = testTimeFlow.Value;
							scope.brunchName = escaper.EscapeMarkdownV2(Hashtag(RemoteScriptingClientExtension.BuildInfoWrapper.Branch));
							scope.commit = sha[..Math.Min(sha.Length, 8)];
							scope.deviceInfo = Lime.Application.Platform;
							scope.otherErrors = escaper.EscapeMarkdownV2(consoleContributor.Status);
							scope.deltaTime = escaper.EscapeMarkdownV2(report.GetTestDurationFormatted());

							string pythonCode = File.ReadAllText(@"..\scripts\test_script.py");
							scope.Exec(pythonCode);
						}
					}
					secondSendFlag = true;

					message = await telegram.SendDocumentAsync(
						chat.Id,
						inputFile,
						caption: text,
						parseMode: ParseMode
					).ConfigureAwait(continueOnCapturedContext: false);
				} else {
					RemoteScriptingManager.Instance.Environment.Log(
						string.Format(logFormat, nameof(TelegramClient.SendTextMessageAsync), string.Empty)
					);
					message = await telegram.SendTextMessageAsync(
						chat.Id,
						text,
						parseMode: ParseMode
					).ConfigureAwait(continueOnCapturedContext: false);
				}
				if (message != null) {
					int? localLastMessageId;
					lock (@lock) {
						(localLastMessageId, chat.LastMessageId) = (chat.LastMessageId, message.MessageId);
					}
					if (localLastMessageId.HasValue) {
						await System.Threading.Tasks.Task.Delay(RequestDelay).ConfigureAwait(continueOnCapturedContext: false);
						var messageIdToDelete = localLastMessageId.Value;
						RemoteScriptingManager.Instance.Environment.Log(
							string.Format(logFormat, nameof(TelegramClient.DeleteMessageAsync), $", {messageIdToDelete}")
						);
						await telegram.DeleteMessageAsync(chat.Id, messageIdToDelete).ConfigureAwait(continueOnCapturedContext: false);
					}
				}
			}

			static string Hashtag(string text)
			{
				if (string.IsNullOrEmpty(text)) {
					return text;
				}
				var stringBuilder = new StringBuilder();
				foreach (var character in text) {
					stringBuilder.Append(char.IsLetterOrDigit(character) ? character : '_');
				}
				return stringBuilder.ToString();
			}
		}

		private class KeywordsContributor : ReportBot.IReportContributor
		{
			public List<string> Keywords { get; } = new ();

			public KeywordsContributor(TestParameters[] testParameters)
			{
				if (testParameters == null || testParameters.Length == 0) {
					return;
				}
				foreach (var parameters in testParameters) {
					switch (parameters) {
						case EnvironmentTestParameters environmentTestParameters:
							if (!environmentTestParameters.AlertOnMissingTexture) {
								AddKeyword($"!{nameof(environmentTestParameters.AlertOnMissingTexture)}");
							}
							if (!environmentTestParameters.AlertOnMissingAudio) {
								AddKeyword($"!{nameof(environmentTestParameters.AlertOnMissingAudio)}");
							}
							if (environmentTestParameters.EmulateLowMemoryDevice) {
								AddKeyword(nameof(environmentTestParameters.EmulateLowMemoryDevice));
							}
							if (!environmentTestParameters.NewGameScreenUI) {
								AddKeyword($"!{nameof(environmentTestParameters.NewGameScreenUI)}");
							}
							if (!environmentTestParameters.PuzzleCollections) {
								AddKeyword($"!{nameof(environmentTestParameters.PuzzleCollections)}");
							}
							break;
						case CityTraversalTestParameters cityTraversalTestParameters:
							AddKeyword(cityTraversalTestParameters.CityTraversalMode.ToString());
							if (
								cityTraversalTestParameters.CityTraversalMode == CityTraversalMode.AllCities
								&& !string.IsNullOrWhiteSpace(cityTraversalTestParameters.StartingCity)
							) {
								AddKeyword($"Starting: {cityTraversalTestParameters.StartingCity}");
							}
							break;
						case TargetCityParameters targetCityParameters:
							AddKeyword(targetCityParameters.TargetCity.ToString());
							break;
						case EventTestParameters eventTestParameters:
							if (eventTestParameters.UseCustomEventName) {
								AddKeyword(eventTestParameters.CustomEventName);
							}
							break;
						case BulletTestParameters bulletTestParameters:
							AddKeyword($"{nameof(bulletTestParameters.TiersToComplete)}: {bulletTestParameters.TiersToComplete}");
							break;
						case CommonFunctionalityBenchmarkParameters commonFunctionalityBenchmarkParameters:
							AddKeyword(commonFunctionalityBenchmarkParameters.TestFlow.ToString());
							break;
						case CrashTestParameters crashTestParameters:
							if (crashTestParameters.Base) {
								AddKeyword(nameof(crashTestParameters.Base));
							}
							if (crashTestParameters.Additional) {
								AddKeyword(nameof(crashTestParameters.Additional));
							}
							if (crashTestParameters.Extended) {
								AddKeyword(nameof(crashTestParameters.Extended));
							}
							break;
					}
				}

				void AddKeyword(string keyword)
				{
					if (!string.IsNullOrWhiteSpace(keyword)) {
						Keywords.Add(keyword);
					}
				}
			}

			void ReportBot.IReportContributor.Contribute(ReportBot.Report report) => report.SetExtension(this);
		}

		private class TimeFlowContributor : ReportBot.IReportContributor
		{
			public TimeFlowType? TestTimeFlow { get; init; }

			public TimeFlowContributor(IEnumerable<TestParameters> testParameters)
			{
				TestTimeFlow = testParameters?.OfType<TimeFlowTestParameters>().FirstOrDefault()?.TimeFlow;
			}

			void ReportBot.IReportContributor.Contribute(ReportBot.Report report) => report.SetExtension(this);
		}

		private class ParametersContributor : ReportBot.IReportContributor
		{
			private readonly TestParameters[] testParameters;

			public ParametersContributor(TestParameters[] testParameters)
			{
				this.testParameters = testParameters;
			}

			void ReportBot.IReportContributor.Contribute(ReportBot.Report report)
			{
				if (testParameters != null && testParameters.Length != 0) {
					var serializer = new JsonSerializer {
						Options = new CommonOptions {
							TagMode = TagMode.Aliases,
							AllowEmptyTypes = true,
							CheckForEmptyCollections = true,
						},
						JsonOptions = new JsonSerializeOptions {
							ArrayLengthPrefix = false,
							SaveClass = JsonSaveClass.None,
							Unordered = true,
							MaxOnelineFields = 8,
							BOM = true,
							EnumAsString = true,
						},
					};
					var messageBuilder = new StringBuilder();
					foreach (var parameters in testParameters) {
						if (messageBuilder.Length > 0) {
							messageBuilder.Append("\n\n");
						}
						var parametersToSerialize = parameters;
						if (parameters is EnvironmentTestParameters etp) {
							var clone = Cloner.Clone(etp);
							clone.GitRepositoryStatus = null;
							parametersToSerialize = clone;
						}
						using var stream = new MemoryStream();
						serializer.ToStream(parametersToSerialize, stream);
						var parametersString = Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
						messageBuilder.Append(parameters.GetType().Name);
						messageBuilder.Append(":\n");
						messageBuilder.Append(parametersString);
					}
					report.AddEntry("Test Parameters", messageBuilder.ToString());
				}
				var environmentParameters = testParameters?.OfType<EnvironmentTestParameters>().FirstOrDefault();
				report.AddEntry("Git Status", environmentParameters?.GitRepositoryStatus?.Text);
			}
		}

		private class ConsoleContributor : ReportBot.IReportContributor
		{
			private const int ConsoleErrorLimit = 256;
			private readonly Dictionary<string, (int Count, LogLevel LogLevel)> consoleErrors = new (ConsoleErrorLimit);
			private int consoleErrorCount;

			public string Status { get; private set; } = string.Empty;

			void ReportBot.IReportContributor.TestStarting()
			{
				Lime.Logger.OnWrite += OnLimeLogWrite;
				Match.Logger.OnWrite += OnGameLogWrite;
			}

			void ReportBot.IReportContributor.TestStopped()
			{
				Lime.Logger.OnWrite -= OnLimeLogWrite;
				Match.Logger.OnWrite -= OnGameLogWrite;
			}

			private void OnLimeLogWrite(string message) => OnGameLogWrite(LogLevel.Engine, message, Array.Empty<object[]>());

			private void OnGameLogWrite(LogLevel level, string format, object[] args)
			{
				if (IsImportantLog(level, format)) {
					consoleErrorCount++;
					var str = $"[{level}] {(args.Length != 0 ? string.Format(format, args) : format)}";
					if (consoleErrors.TryGetValue(str, out var consoleError)) {
						consoleErrors[str] = (consoleError.Count + 1, consoleError.LogLevel);
					} else if (consoleErrors.Count < ConsoleErrorLimit) {
						consoleErrors.Add(str, (1, level));
					}
				}

				static bool IsImportantLog(LogLevel level, string format)
				{
					return (level & LogLevel.Warning) != 0
						|| (level & LogLevel.Error) != 0
						|| (level & LogLevel.Fatal) != 0
						|| System.Text.RegularExpressions.Regex.IsMatch(
							format,
							@"\W*(warning|error|fatal|exception)\W*",
							System.Text.RegularExpressions.RegexOptions.IgnoreCase
						)
						&& !System.Text.RegularExpressions.Regex.IsMatch(
							format,
							@"\W*(TelegramClient\.)\W*",
							System.Text.RegularExpressions.RegexOptions.IgnoreCase
						);
				}
			}

			void ReportBot.IReportContributor.ContributeLate(ReportBot.Report report)
			{
				if (consoleErrorCount == 0) {
					report.AddEntry("Important console messages (0)", null);
				}
				StringBuilder stringBuilder = null;
				if (consoleErrors.Count > 0) {
					var errorList = consoleErrors
						.OrderByDescending(kv => kv.Value.LogLevel)
						.ThenByDescending(kv => kv.Value.Count)
						.Select(kv => (Text: $"[x{kv.Value.Count}] {kv.Key}", Level: kv.Value.LogLevel, Count: kv.Value.Count))
						.ToList();
					var initialCapacity = errorList.Sum(e => e.Text.Length) + errorList.Count * 2;
					stringBuilder = new StringBuilder(initialCapacity);
					var statusParts = new List<string>();
					LogLevel? lastLogLevel = null;
					var logLevelCount = 0;
					foreach (var (text, level, count) in errorList) {
						if (lastLogLevel.HasValue && lastLogLevel != level) {
							stringBuilder.Append('\n');
							PushStatus();
						}
						stringBuilder.Append(text);
						stringBuilder.Append('\n');
						logLevelCount += count;
						lastLogLevel = level;
					}
					stringBuilder.Length--;
					PushStatus();
					if (statusParts.Count > 0) {
						Status = string.Join("; ", statusParts);
						report.SetExtension(this);
					}

					void PushStatus()
					{
						if (logLevelCount > 0) {
							statusParts.Add($"{logLevelCount} {lastLogLevel.Value}");
						}
						logLevelCount = 0;
					}
				}
				report.AddEntry(
					$"Important console messages (x{consoleErrorCount})",
					stringBuilder?.ToString()
				);
			}
		}

		private class BundlesContributor : ReportBot.IReportContributor
		{
			private readonly Dictionary<string, List<string>> attachedBundleStackTraces = [];
			private List<string> attachedBundlesOnStart;

			void ReportBot.IReportContributor.TestStarting()
			{
				attachedBundlesOnStart = GetAttachedBundles().ToList();
				DLCBundle.BundleAttached += BundleAttached;
			}

			void ReportBot.IReportContributor.TestStopped()
			{
				DLCBundle.BundleAttached -= BundleAttached;
			}

			private void BundleAttached(DLCBundle bundle)
			{
				var stackTrace = new StackTrace(skipFrames: 2, fNeedFileInfo: true).ToString();
				if (!attachedBundleStackTraces.TryGetValue(bundle.Name, out var stackTraces)) {
					stackTraces = [ stackTrace ];
					attachedBundleStackTraces.Add(bundle.Name, stackTraces);
				} else {
					if (!stackTraces.Contains(stackTrace)) {
						stackTraces.Add(stackTrace);
					}
				}
			}

			void ReportBot.IReportContributor.ContributeLate(ReportBot.Report report)
			{
				var stringBuilder = new StringBuilder();
				stringBuilder.Append("\tAttached on Start (");
				stringBuilder.Append(attachedBundlesOnStart.Count);
				if (attachedBundlesOnStart.Count > 0) {
					stringBuilder.AppendLine("):");
					foreach (var bundleName in attachedBundlesOnStart) {
						stringBuilder.Append("\t\t");
						stringBuilder.AppendLine(bundleName);
					}
				} else {
					stringBuilder.AppendLine(").");
				}

				var attachedBundlesOnStartHashSet = attachedBundlesOnStart.ToHashSet();
				var newAttachedBundles = GetAttachedBundles()
					.Union(attachedBundleStackTraces.Keys)
					.Where(b => !attachedBundlesOnStartHashSet.Contains(b)).ToList();
				stringBuilder.Append("\tAttached during Runtime (");
				stringBuilder.Append(newAttachedBundles.Count);
				if (newAttachedBundles.Count > 0) {
					stringBuilder.AppendLine("):");
					foreach (var bundleName in newAttachedBundles) {
						stringBuilder.Append("\t\t");
						stringBuilder.AppendLine(bundleName);
					}
				} else {
					stringBuilder.AppendLine(").");
				}

				if (attachedBundleStackTraces.Count > 0) {
					stringBuilder.AppendLine();
					foreach (var (bundleName, stackTraces) in attachedBundleStackTraces) {
						foreach (var stackTrace in stackTraces) {
							stringBuilder.Append('\t');
							stringBuilder.AppendLine(bundleName);
							stringBuilder.AppendLine(stackTrace);
						}
					}
				}
				report.AddEntry(
					"DLC Bundles",
					stringBuilder.ToString()
				);
			}

			private static IEnumerable<string> GetAttachedBundles() => DLCBundles.All.Where(b => b.IsAttached()).Select(b => b.Name);
		}

#pragma warning disable CA1812 // Internal class is never instantiated
		private class ReportConfiguration
		{
			[YuzuMember]
			public string AuthToken { get; set; }

			[YuzuMember]
			public string ChatId { get; set; }

			[YuzuMember]
			public string DeviceInformation { get; set; }

			[YuzuOptional]
			public string BranchName { get; set; }

			public bool IsValid =>
				!string.IsNullOrEmpty(DeviceInformation)
				&& !string.IsNullOrEmpty(AuthToken)
				&& !string.IsNullOrEmpty(ChatId);

			public string[] EnumerateChatIds()
			{
				return ChatId.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			}

			public static ReportConfiguration Parse(byte[] data)
			{
				if (data == null) {
					return null;
				}
				var yuzuCommonOptions = new CommonOptions {
					TagMode = TagMode.Aliases,
					AllowEmptyTypes = true,
					CheckForEmptyCollections = true,
				};
				var yuzuJsonOptions = new JsonSerializeOptions {
					ArrayLengthPrefix = false,
					Indent = string.Empty,
					FieldSeparator = string.Empty,
					SaveClass = JsonSaveClass.None,
					Unordered = true,
					MaxOnelineFields = 8,
					BOM = true,
					EnumAsString = true,
				};
				var persistence = new Persistence(yuzuCommonOptions, yuzuJsonOptions);
				try {
					using var stream = new MemoryStream(data);
					return persistence.ReadFromStream<ReportConfiguration>(stream);
				} catch (Exception exception) {
					RemoteScriptingManager.Instance.Environment.Log(
						$"Exception while parsing {nameof(ReportConfiguration)}: {exception}"
					);
				}
				return null;
			}
		}
#pragma warning restore CA1812 // Internal class is never instantiated

		private class TelegramEscaper
		{
			private readonly char[] specialCharacters;

			public TelegramEscaper()
			{
				var specialCharactersList = new List<char> {
					'_', '*', '[', ']', '(', ')', '~', '`', '>', '#', '+', '-', '=', '|', '{', '}', '.', '!',
				};
				specialCharactersList.Sort();
				specialCharacters = [.. specialCharactersList];
			}

			public string EscapeMarkdownV2(string text)
			{
				if (string.IsNullOrEmpty(text)) {
					return text;
				}
				var stringBuilder = new StringBuilder();
				foreach (var character in text) {
					if (Array.BinarySearch(specialCharacters, character) >= 0) {
						stringBuilder.Append('\\');
					}
					stringBuilder.Append(character);
				}
				return stringBuilder.ToString();
			}
		}

		private class Chat
		{
			public ChatId Id { get; init; }
			public int? LastMessageId { get; set; }
		}
	}
}
