﻿/*
    _                _      _  ____   _                           _____
   / \    _ __  ___ | |__  (_)/ ___| | |_  ___   __ _  _ __ ___  |  ___|__ _  _ __  _ __ ___
  / _ \  | '__|/ __|| '_ \ | |\___ \ | __|/ _ \ / _` || '_ ` _ \ | |_  / _` || '__|| '_ ` _ \
 / ___ \ | |  | (__ | | | || | ___) || |_|  __/| (_| || | | | | ||  _|| (_| || |   | | | | | |
/_/   \_\|_|   \___||_| |_||_||____/  \__|\___| \__,_||_| |_| |_||_|   \__,_||_|   |_| |_| |_|

 Copyright 2015-2016 Łukasz "JustArchi" Domeradzki
 Contact: JustArchi@JustArchi.net

 Licensed under the Apache License, Version 2.0 (the "License");
 you may not use this file except in compliance with the License.
 You may obtain a copy of the License at

 http://www.apache.org/licenses/LICENSE-2.0
					
 Unless required by applicable law or agreed to in writing, software
 distributed under the License is distributed on an "AS IS" BASIS,
 WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 See the License for the specific language governing permissions and
 limitations under the License.

*/

using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace ArchiSteamFarm {
	internal static class Program {
		internal enum EUserInputType : byte {
			Login,
			Password,
			PhoneNumber,
			SMS,
			SteamGuard,
			SteamParentalPIN,
			RevocationCode,
			TwoFactorAuthentication,
		}

		internal enum EMode : byte {
			Normal, // Standard most common usage
			Client, // WCF client only
			Server // Normal + WCF server
		}

		private const string GithubReleaseURL = "https://api.github.com/repos/JustArchi/ArchiSteamFarm/releases";

		internal const string ASF = "ASF";
		internal const string ConfigDirectory = "config";
		internal const string DebugDirectory = "debug";
		internal const string LogFile = "log.txt";
		internal const string GlobalConfigFile = ASF + ".json";
		internal const string GlobalDatabaseFile = ASF + ".db";

		private static readonly object ConsoleLock = new object();
		private static readonly SemaphoreSlim SteamSemaphore = new SemaphoreSlim(1);
		private static readonly ManualResetEvent ShutdownResetEvent = new ManualResetEvent(false);
		private static readonly Assembly Assembly = Assembly.GetExecutingAssembly();
		private static readonly string ExecutableFile = Assembly.Location;
		private static readonly string ExecutableName = Path.GetFileName(ExecutableFile);
		private static readonly string ExecutableDirectory = Path.GetDirectoryName(ExecutableFile);
		private static readonly WCF WCF = new WCF();

		internal static readonly string Version = Assembly.GetName().Version.ToString();

		internal static GlobalConfig GlobalConfig { get; private set; }
		internal static GlobalDatabase GlobalDatabase { get; private set; }
		internal static bool ConsoleIsBusy { get; private set; } = false;

		private static Timer AutoUpdatesTimer;
		private static EMode Mode = EMode.Normal;

		private static async Task CheckForUpdate() {
			string oldExeFile = ExecutableFile + ".old";

			// We booted successfully so we can now remove old exe file
			if (File.Exists(oldExeFile)) {
				try {
					File.Delete(oldExeFile);
				} catch (Exception e) {
					Logging.LogGenericException(e);
					return;
				}
			}

			if (GlobalConfig.UpdateChannel == GlobalConfig.EUpdateChannel.Unknown) {
				return;
			}

			string releaseURL = GithubReleaseURL;
			if (GlobalConfig.UpdateChannel == GlobalConfig.EUpdateChannel.Stable) {
				releaseURL += "/latest";
			}

			string response = null;
			Logging.LogGenericInfo("Checking new version...");
			for (byte i = 0; i < WebBrowser.MaxRetries && string.IsNullOrEmpty(response); i++) {
				response = await WebBrowser.UrlGetToContent(releaseURL).ConfigureAwait(false);
			}

			if (string.IsNullOrEmpty(response)) {
				Logging.LogGenericWarning("Could not check latest version!");
				return;
			}

			GitHub.ReleaseResponse releaseResponse;
			if (GlobalConfig.UpdateChannel == GlobalConfig.EUpdateChannel.Stable) {
				try {
					releaseResponse = JsonConvert.DeserializeObject<GitHub.ReleaseResponse>(response);
				} catch (JsonException e) {
					Logging.LogGenericException(e);
					return;
				}
			} else {
				List<GitHub.ReleaseResponse> releases;
				try {
					releases = JsonConvert.DeserializeObject<List<GitHub.ReleaseResponse>>(response);
				} catch (JsonException e) {
					Logging.LogGenericException(e);
					return;
				}

				if (releases == null || releases.Count == 0) {
					Logging.LogGenericWarning("Could not check latest version!");
					return;
				}

				releaseResponse = releases[0];
			}

			if (string.IsNullOrEmpty(releaseResponse.Tag)) {
				Logging.LogGenericWarning("Could not check latest version!");
				return;
			}

			Logging.LogGenericInfo("Local version: " + Version + " | Remote version: " + releaseResponse.Tag);

			if (string.Compare(Version, releaseResponse.Tag, StringComparison.Ordinal) >= 0) { // If local version is the same or newer than remote version
																							   // Set up a timer that will automatically update ASF on as-needed basis
				if (GlobalConfig.AutoUpdates && AutoUpdatesTimer == null) {
					Logging.LogGenericInfo("ASF will automatically check for new versions every 24 hours");
					AutoUpdatesTimer = new Timer(
						async e => await CheckForUpdate().ConfigureAwait(false),
						null,
						TimeSpan.FromDays(1), // Delay
						TimeSpan.FromDays(1) // Period
					);
				}
				return;
			}

			if (!GlobalConfig.AutoUpdates) {
				Logging.LogGenericInfo("New version is available!");
				Logging.LogGenericInfo("Consider updating yourself!");
				await Utilities.SleepAsync(5000).ConfigureAwait(false);
				return;
			}

			// Auto update logic starts here
			if (releaseResponse.Assets == null) {
				Logging.LogGenericWarning("Could not proceed with update because that version doesn't include assets!");
				return;
			}

			GitHub.Asset binaryAsset = null;
			foreach (var asset in releaseResponse.Assets) {
				if (string.IsNullOrEmpty(asset.Name) || !asset.Name.Equals(ExecutableName)) {
					continue;
				}

				binaryAsset = asset;
				break;
			}

			if (binaryAsset == null) {
				Logging.LogGenericWarning("Could not proceed with update because there is no asset that relates to currently running binary!");
				return;
			}

			if (string.IsNullOrEmpty(binaryAsset.DownloadURL)) {
				Logging.LogGenericWarning("Could not proceed with update because download URL is empty!");
				return;
			}

			Logging.LogGenericInfo("Downloading new version...");
			Stream newExe = await WebBrowser.UrlGetToStream(binaryAsset.DownloadURL).ConfigureAwait(false);
			if (newExe == null) {
				Logging.LogGenericWarning("Could not download new version!");
				return;
			}

			// We start deep update logic here
			string newExeFile = ExecutableFile + ".new";

			// Firstly we create new exec
			try {
				using (FileStream fileStream = File.Open(newExeFile, FileMode.Create)) {
					await newExe.CopyToAsync(fileStream).ConfigureAwait(false);
				}
			} catch (Exception e) {
				Logging.LogGenericException(e);
				return;
			}

			// Now we move current -> old
			try {
				File.Move(ExecutableFile, oldExeFile);
			} catch (Exception e) {
				Logging.LogGenericException(e);
				try {
					// Cleanup
					File.Delete(newExeFile);
				} catch { }
				return;
			}

			// Now we move new -> current
			try {
				File.Move(newExeFile, ExecutableFile);
			} catch (Exception e) {
				Logging.LogGenericException(e);
				try {
					// Cleanup
					File.Move(oldExeFile, ExecutableFile);
					File.Delete(newExeFile);
				} catch { }
				return;
			}

			Logging.LogGenericInfo("Update process is finished! ASF will now restart itself...");
			await Utilities.SleepAsync(5000);

			if (!Restart()) {
				// Make sure that we won't try updating again in this case
				if (AutoUpdatesTimer != null) {
					AutoUpdatesTimer.Dispose();
					AutoUpdatesTimer = null;
				}

				// Inform user about failure
				Logging.LogGenericWarning("ASF could not restart itself, you may need to restart it manually!");
				await Utilities.SleepAsync(5000);
			}
		}

		internal static void Exit(int exitCode = 0) {
			Environment.Exit(exitCode);
		}

		internal static bool Restart() {
			try {
				if (Process.Start(ExecutableFile, string.Join(" ", Environment.GetCommandLineArgs().Skip(1))) != null) {
					Exit();
					return true;
				} else {
					return false;
				}
			} catch (Exception e) {
				Logging.LogGenericException(e);
				return false;
			}
		}

		internal static async Task LimitSteamRequestsAsync() {
			await SteamSemaphore.WaitAsync().ConfigureAwait(false);
			Task.Run(async () => {
				await Utilities.SleepAsync(GlobalConfig.LoginLimiterDelay * 1000).ConfigureAwait(false);
				SteamSemaphore.Release();
			}).Forget();
		}

		internal static string GetUserInput(string botLogin, EUserInputType userInputType, string extraInformation = null) {
			string result;
			lock (ConsoleLock) {
				ConsoleIsBusy = true;
				switch (userInputType) {
					case EUserInputType.Login:
						Console.Write("<" + botLogin + "> Please enter your login: ");
						break;
					case EUserInputType.Password:
						Console.Write("<" + botLogin + "> Please enter your password: ");
						break;
					case EUserInputType.PhoneNumber:
						Console.Write("<" + botLogin + "> Please enter your full phone number (e.g. +1234567890): ");
						break;
					case EUserInputType.SMS:
						Console.Write("<" + botLogin + "> Please enter SMS code sent on your mobile: ");
						break;
					case EUserInputType.SteamGuard:
						Console.Write("<" + botLogin + "> Please enter the auth code sent to your email: ");
						break;
					case EUserInputType.SteamParentalPIN:
						Console.Write("<" + botLogin + "> Please enter steam parental PIN: ");
						break;
					case EUserInputType.RevocationCode:
						Console.WriteLine("<" + botLogin + "> PLEASE WRITE DOWN YOUR REVOCATION CODE: " + extraInformation);
						Console.WriteLine("<" + botLogin + "> THIS IS THE ONLY WAY TO NOT GET LOCKED OUT OF YOUR ACCOUNT!");
						Console.Write("<" + botLogin + "> Hit enter once ready...");
						break;
					case EUserInputType.TwoFactorAuthentication:
						Console.Write("<" + botLogin + "> Please enter your 2 factor auth code from your authenticator app: ");
						break;
				}
				result = Console.ReadLine();
				Console.Clear(); // For security purposes
				ConsoleIsBusy = false;
			}

			return string.IsNullOrEmpty(result) ? null : result.Trim();
		}

		internal static void OnBotShutdown() {
			foreach (Bot bot in Bot.Bots.Values) {
				if (bot.KeepRunning) {
					return;
				}
			}

			if (WCF.IsServerRunning()) {
				return;
			}

			Logging.LogGenericInfo("No bots are running, exiting");
			ShutdownResetEvent.Set();
		}

		private static void InitServices() {
			GlobalConfig = GlobalConfig.Load();
			if (GlobalConfig == null) {
				Logging.LogGenericError("Global config could not be loaded, please make sure that ASF.db exists and is valid!");
				Thread.Sleep(5000);
				Exit(1);
			}

			GlobalDatabase = GlobalDatabase.Load();
			if (GlobalDatabase == null) {
				Logging.LogGenericError("Global database could not be loaded!");
				Thread.Sleep(5000);
				Exit(1);
			}

			ArchiWebHandler.Init();
			WebBrowser.Init();
			WCF.Init();
		}

		private static void ParseArgs(string[] args) {
			foreach (string arg in args) {
				switch (arg) {
					case "--client":
						Mode = EMode.Client;
						Logging.LogToFile = false;
						break;
					case "--log":
						Logging.LogToFile = true;
						break;
					case "--no-log":
						Logging.LogToFile = false;
						break;
					case "--server":
						Mode = EMode.Server;
						WCF.StartServer();
						break;
					default:
						if (arg.StartsWith("--")) {
							Logging.LogGenericWarning("Unrecognized parameter: " + arg);
							continue;
						}

						if (Mode != EMode.Client) {
							Logging.LogGenericWarning("Ignoring command because --client wasn't specified: " + arg);
							continue;
						}

						Logging.LogGenericInfo("Command sent: \"" + arg + "\"");

						// We intentionally execute this async block synchronously
						Logging.LogGenericInfo("Response received: \"" + WCF.SendCommand(arg) + "\"");
						/*
						Task.Run(async () => {
							Logging.LogGenericNotice("WCF", "Response received: " + await WCF.SendCommand(arg).ConfigureAwait(false));
						}).Wait();
						*/
						break;
				}
			}
		}

		private static void UnhandledExceptionHandler(object sender, UnhandledExceptionEventArgs args) {
			if (sender == null || args == null) {
				return;
			}

			Logging.LogGenericException((Exception) args.ExceptionObject);
		}

		private static void Main(string[] args) {
			AppDomain.CurrentDomain.UnhandledException += UnhandledExceptionHandler;

			Logging.LogGenericInfo("Archi's Steam Farm, version " + Version);
			Directory.SetCurrentDirectory(ExecutableDirectory);
			InitServices();

			// Allow loading configs from source tree if it's a debug build
			if (Debugging.IsDebugBuild) {

				// Common structure is bin/(x64/)Debug/ArchiSteamFarm.exe, so we allow up to 4 directories up
				for (var i = 0; i < 4; i++) {
					Directory.SetCurrentDirectory("..");
					if (Directory.Exists(ConfigDirectory)) {
						break;
					}
				}

				// If config directory doesn't exist after our adjustment, abort all of that
				if (!Directory.Exists(ConfigDirectory)) {
					Directory.SetCurrentDirectory(ExecutableDirectory);
				}
			}

			// Parse args
			ParseArgs(args);

			// If we ran ASF as a client, we're done by now
			if (Mode == EMode.Client) {
				return;
			}

			// From now on it's server mode
			Logging.Init();

			if (!Directory.Exists(ConfigDirectory)) {
				Logging.LogGenericError("Config directory doesn't exist!");
				Thread.Sleep(5000);
				Exit(1);
			}

			CheckForUpdate().Wait();

			// Before attempting to connect, initialize our list of CMs
			Bot.RefreshCMs(GlobalDatabase.CellID).Wait();

			foreach (var configFile in Directory.EnumerateFiles(ConfigDirectory, "*.json")) {
				string botName = Path.GetFileNameWithoutExtension(configFile);
				if (botName.Equals(ASF)) {
					continue;
				}

				Bot bot = new Bot(botName);
				if (!bot.BotConfig.Enabled) {
					Logging.LogGenericInfo("Not starting this instance because it's disabled in config file", botName);
				}
			}

			// CONVERSION START
			foreach (var configFile in Directory.EnumerateFiles(ConfigDirectory, "*.xml")) {
				string botName = Path.GetFileNameWithoutExtension(configFile);
				Logging.LogGenericWarning("Found legacy " + botName + ".xml config file, it will now be converted to new ASF V2.0 format!");
				Bot bot = new Bot(botName);
				if (!bot.BotConfig.Enabled) {
					Logging.LogGenericInfo("Not starting this instance because it's disabled in config file", botName);
				}
			}
			// CONVERSION END

			// Check if we got any bots running
			OnBotShutdown();

			// Wait for signal to shutdown
			ShutdownResetEvent.WaitOne();

			// We got a signal to shutdown, consider giving user some time to read the message
			Thread.Sleep(5000);

			// This is over, cleanup only now
			WCF.StopServer();
		}
	}
}
