﻿using SteamKit2;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace ArchiSteamFarm {
	internal class Bot {
		private const byte CallbackSleep = 100; // In miliseconds

		private readonly Dictionary<string, string> Config = new Dictionary<string, string>();

		internal readonly string BotName;
		private readonly string ConfigFile;
		private readonly string SentryFile;

		private readonly CardsFarmer CardsFarmer;

		internal ulong BotID { get; private set; }
		private string AuthCode, TwoFactorAuth;

		internal ArchiHandler ArchiHandler { get; private set; }
		internal ArchiWebHandler ArchiWebHandler { get; private set; }
		internal CallbackManager CallbackManager { get; private set; }
		internal SteamClient SteamClient { get; private set; }
		internal SteamFriends SteamFriends { get; private set; }
		internal SteamUser SteamUser { get; private set; }
		internal Trading Trading { get; private set; }

		// Config variables
		private bool Enabled { get { return bool.Parse(Config["Enabled"]); } }
		private string SteamLogin { get { return Config["SteamLogin"]; } }
		private string SteamPassword { get { return Config["SteamPassword"]; } }
		private string SteamNickname { get { return Config["SteamNickname"]; } }
		private string SteamApiKey { get { return Config["SteamApiKey"]; } }
		internal ulong SteamMasterID { get { return ulong.Parse(Config["SteamMasterID"]); } }
		internal ulong SteamMasterClanID { get { return ulong.Parse(Config["SteamMasterClanID"]); } }

		internal Bot (string botName) {
			BotName = botName;
			CardsFarmer = new CardsFarmer(this);

			ConfigFile = Path.Combine(Program.ConfigDirectoryPath, BotName + ".xml");
			SentryFile = Path.Combine(Program.ConfigDirectoryPath, BotName + ".bin");

			ReadConfig();

			if (!Enabled) {
				return;
			}
            Start();
		}

		private void ReadConfig() {
			using (XmlReader reader = XmlReader.Create(ConfigFile)) {
				while (reader.Read()) {
					if (reader.NodeType != XmlNodeType.Element) {
						continue;
					}

					string key = reader.Name;
					if (string.IsNullOrEmpty(key)) {
						continue;
					}

					string value = reader.GetAttribute("value");
					if (string.IsNullOrEmpty(value)) {
						continue;
					}

					Config.Add(key, value);
				}
			}
		}

		internal void Start() {
			if (SteamClient != null) {
				return;
			}

			SteamClient = new SteamClient();

			ArchiHandler = new ArchiHandler();
            SteamClient.AddHandler(ArchiHandler);

			CallbackManager = new CallbackManager(SteamClient);
			CallbackManager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
			CallbackManager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);

			SteamFriends = SteamClient.GetHandler<SteamFriends>();
			CallbackManager.Subscribe<SteamFriends.FriendsListCallback>(OnFriendsList);
			CallbackManager.Subscribe<SteamFriends.FriendMsgCallback>(OnFriendMsg);

			SteamUser = SteamClient.GetHandler<SteamUser>();
			CallbackManager.Subscribe<SteamUser.AccountInfoCallback>(OnAccountInfo);
			CallbackManager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
			CallbackManager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
			CallbackManager.Subscribe<SteamUser.UpdateMachineAuthCallback>(OnMachineAuth);

			CallbackManager.Subscribe<ArchiHandler.NotificationCallback>(OnNotification);
			CallbackManager.Subscribe<ArchiHandler.PurchaseResponseCallback>(OnPurchaseResponse);

			ArchiWebHandler = new ArchiWebHandler(this, SteamApiKey);
			Trading = new Trading(this);

			SteamClient.Connect();
			Task.Run(() => HandleCallbacks());
		}

		internal void Stop() {
			if (SteamClient == null) {
				return;
			}

			SteamClient.Disconnect();
			SteamClient = null;
			CallbackManager = null;
		}

		internal void PlayGame(params ulong[] gameIDs) {
			ArchiHandler.PlayGames(gameIDs);
		}

		private void HandleCallbacks() {
			TimeSpan timeSpan = TimeSpan.FromMilliseconds(CallbackSleep);
			while (CallbackManager != null) {
				CallbackManager.RunWaitCallbacks(timeSpan);
			}
		}

		private void OnConnected(SteamClient.ConnectedCallback callback) {
			if (callback == null) {
				return;
			}

			if (callback.Result != EResult.OK) {
				Logging.LogGenericError(BotName, "Unable to connect to Steam: " + callback.Result);
				return;
			}

			Logging.LogGenericInfo(BotName, "Connected to Steam!");

			byte[] sentryHash = null;
			if (File.Exists(SentryFile)) {
				byte[] sentryFileContent = File.ReadAllBytes(SentryFile);
				sentryHash = CryptoHelper.SHAHash(sentryFileContent);
			}

			SteamUser.LogOn(new SteamUser.LogOnDetails {
				Username = SteamLogin,
				Password = SteamPassword,
				AuthCode = AuthCode,
				TwoFactorCode = TwoFactorAuth,
				SentryFileHash = sentryHash
			});
		}

		private void OnDisconnected(SteamClient.DisconnectedCallback callback) {
			if (callback == null) {
				return;
			}

			Logging.LogGenericWarning(BotName, "Disconnected from Steam, reconnecting...");
			Thread.Sleep(TimeSpan.FromMilliseconds(CallbackSleep));
			SteamClient.Connect();
		}

		private void OnFriendsList(SteamFriends.FriendsListCallback callback) {
			if (callback == null) {
				return;
			}

			foreach (var friend in callback.FriendList) {
				if (friend.Relationship != EFriendRelationship.RequestRecipient) {
					continue;
				}

				SteamID steamID = friend.SteamID;
				switch (steamID.AccountType) {
					case EAccountType.Clan:
						//ArchiHandler.AcceptClanInvite(steamID);
						break;
					default:
						if (steamID == SteamMasterID) {
							SteamFriends.AddFriend(steamID);
						} else {
							SteamFriends.RemoveFriend(steamID);
						}
						break;
				}
			}
		}

		private void OnFriendMsg(SteamFriends.FriendMsgCallback callback) {
			if (callback == null) {
				return;
			}

			if (callback.EntryType != EChatEntryType.ChatMsg) {
				return;
			}

			ulong steamID = callback.Sender;
			if (steamID != SteamMasterID) {
				return;
			}

			string message = callback.Message;
			if (string.IsNullOrEmpty(message)) {
				return;
			}

			if (message.Length == 17 && message[5] == '-' && message[11] == '-') {
				ArchiHandler.RedeemKey(message);
			}
		}

		private void OnAccountInfo(SteamUser.AccountInfoCallback callback) {
			if (callback == null) {
				return;
			}

			SteamFriends.SetPersonaState(EPersonaState.Online);
		}

		private void OnLoggedOff(SteamUser.LoggedOffCallback callback) {
			if (callback == null) {
				return;
			}

			Logging.LogGenericInfo(BotName, "Logged off of Steam: " + callback.Result);
		}

		private async void OnLoggedOn(SteamUser.LoggedOnCallback callback) {
			if (callback == null) {
				return;
			}

			if (callback.ClientSteamID != 0) {
				BotID = callback.ClientSteamID;
			}

			EResult result = callback.Result;
			switch (result) {
				case EResult.AccountLogonDenied:
					AuthCode = Program.GetSteamGuardCode(SteamLogin, false);
					break;
				case EResult.AccountLoginDeniedNeedTwoFactor:
					TwoFactorAuth = Program.GetSteamGuardCode(SteamLogin, true);
					break;
				case EResult.OK:
					Logging.LogGenericInfo(BotName, "Successfully logged on!");
					SteamFriends.SetPersonaName(SteamNickname);
					ArchiWebHandler.Init(SteamClient, callback.WebAPIUserNonce, callback.VanityURL);

					ulong clanID = SteamMasterClanID;
					if (clanID != 0) {
						SteamFriends.JoinChat(clanID);
                    }

					await CardsFarmer.StartFarming().ConfigureAwait(false);
					break;
				default:
					Logging.LogGenericWarning(BotName, "Unable to login to Steam: " + callback.Result + " / " + callback.ExtendedResult + ", retrying...");
					Stop();
					Thread.Sleep(5000);
					Start();
					break;
			}
		}

		private void OnMachineAuth(SteamUser.UpdateMachineAuthCallback callback) {
			if (callback == null) {
				return;
			}

			Logging.LogGenericInfo(BotName, "Updating sentryfile...");

			int fileSize;
			byte[] sentryHash;

			using (FileStream fileStream = File.Open(SentryFile, FileMode.OpenOrCreate, FileAccess.ReadWrite)) {
				fileStream.Seek(callback.Offset, SeekOrigin.Begin);
				fileStream.Write(callback.Data, 0, callback.BytesToWrite);
				fileSize = (int) fileStream.Length;

				fileStream.Seek(0, SeekOrigin.Begin);
				using (SHA1CryptoServiceProvider sha = new SHA1CryptoServiceProvider()) {
					sentryHash = sha.ComputeHash(fileStream);
				}
			}


			// Inform the steam servers that we're accepting this sentry file
			SteamUser.SendMachineAuthResponse(new SteamUser.MachineAuthDetails {
				JobID = callback.JobID,
				FileName = callback.FileName,
				BytesWritten = callback.BytesToWrite,
				FileSize = fileSize,
				Offset = callback.Offset,
				Result = EResult.OK,
				LastError = 0,
				OneTimePassword = callback.OneTimePassword,
				SentryFileHash = sentryHash,
			});
		}

		private void OnNotification(ArchiHandler.NotificationCallback callback) {
			if (callback == null) {
				return;
			}

			switch (callback.NotificationType) {
				case ArchiHandler.NotificationCallback.ENotificationType.Trading:
					Trading.CheckTrades();
					break;
			}
		}

		private async void OnPurchaseResponse(ArchiHandler.PurchaseResponseCallback callback) {
			if (callback == null) {
				return;
			}

			var purchaseResult = callback.PurchaseResult;
			SteamFriends.SendChatMessage(SteamMasterID, EChatEntryType.ChatMsg, "Status: " + purchaseResult);

			if (purchaseResult == ArchiHandler.PurchaseResponseCallback.EPurchaseResult.OK) {
				await CardsFarmer.StartFarming().ConfigureAwait(false);
			}
		}
	}
}
