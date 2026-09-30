using System.Buffers;
using System.Net.WebSockets;
using Babbelite.Client;
using Babbelite.Shared;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.CommonAvatar;
using HarmonyLib;
using ResoniteModLoader;


#if DEBUG
using ResoniteHotReloadLib;
#endif

namespace IWantBabbelite;

public class IWantBabbelite : ResoniteMod {
	internal const string VERSION_CONSTANT = "1.0.0"; //Changing the version here updates it in all locations needed
	public override string Name => "IWantBabbelite";
	public override string Author => "Noble";
	public override string Version => VERSION_CONSTANT;
	public override string Link => "https://github.com/noblereign/ResoniteIWantBabbelite/";

	const string harmonyId = "dog.glacier.IWantBabbelite";

	private static ModConfiguration? Config;

	public enum WhisperBubblePlan {
		PauseAll,
		PauseAllInRemote,
		PauseExcludingSelf,
		DontPause
	}

	[AutoRegisterConfigKey]
	public static readonly ModConfigurationKey<bool> Enabled = new("Enabled", "Enables the mod. If the mod gets stuck (e.g. the Babbelite server crashed), you can try toggling this off and on to reset the client.", () => true);

	[AutoRegisterConfigKey]
	public static readonly ModConfigurationKey<bool> ExposeToWorld = new("Expose to world", "Makes your personal transcription global for anyone to read, even without the mod.", () => false);

	[AutoRegisterConfigKey]
	public static readonly ModConfigurationKey<bool> Filter = new("Filter common hallucinations", "Ignores common erroneous output like '[BLANK_AUDIO]'.", () => true);

	[AutoRegisterConfigKey]
	public static readonly ModConfigurationKey<bool> TranscribeRemoteUsers = new("Transcribe remote users", "Should the mod process and transcribe other users locally? This can incur heavy VRAM costs.", () => false);

	[AutoRegisterConfigKey]
	public static readonly ModConfigurationKey<bool> TranscribeLocalMuted = new("Transcribe locally muted users", "Should users muted through the Interactive Camera still be transcribed?", () => false);

	[AutoRegisterConfigKey]
	public static readonly ModConfigurationKey<WhisperBubblePlan> PauseInWhisperBubbles = new("Pause in whisper bubbles", "When should transcriptions be paused?\n\n<color=hero.red><b>WARNING:</color> Lowering this setting comes with risks to privacy.</b> Consider how each option may affect you, as well as the people around you.\n\n<color=hero.yellow>PauseAll</color>: When inside any whisper bubble.\n\n<color=hero.yellow>PauseAllInRemote</color>: Only inside other users whisper bubbles.\n\n<color=hero.yellow>PauseExcludingSelf</color>: When inside any whisper bubble, but always keep transcribing yourself.\n\n<color=hero.yellow>DontPause</color>: Never pause, just keep transcribing regardless of the context.", () => WhisperBubblePlan.PauseAll);

	[AutoRegisterConfigKey]
	public static readonly ModConfigurationKey<bool> UserspaceIgnoresPauses = new("Ignore pausing in Userspace", "Should your transcription still be passed on to Userspace, regardless of any transcription pause?", () => true);

	private static BabbeliteClient? _babbeliteManager;
	private static readonly Dictionary<(World World, RefID RefID), LiveTranscriptionSession> _userSessions = [];
	private static readonly HashSet<(World World, RefID RefID)> _pendingSessions = [];
	private static readonly HashSet<(World World, RefID RefID)> _transmitting = [];
	private static DateTime _lastErrorNotificationTime = DateTime.MinValue;

	private static readonly Dictionary<(World World, RefID RefID), List<float>> _audioAccumulators = [];
	private static readonly Dictionary<(World World, RefID RefID), DateTime> _lastHeardFrom = [];
	private const int SILERO_CHUNK_SIZE = 512;

	private static readonly Dictionary<(World World, RefID RefID), AvatarAudioOutputManager?> _audioManagers = [];
	private static int _inWhisperBubble = 0;
	private static int _inRemoteWhisperBubble = 0;

	private static Slot? userspaceSlot = null;
	const string SLOT_NAME = "Babbelite";
	private static readonly SearchValues<string> Hallucinations = SearchValues.Create(["(silence)", "*mimics a video*", "[Buzzer]", "[BLANK_AUDIO]", "[silence]", "[no audio]", "[XBOX NOISE]", "[XBOX SOUND]", "(Buzz)", "(Buzzer)", "[Buzz]", "*mimics*", "[mimics]", "(mimics)"], StringComparison.OrdinalIgnoreCase);
	private static float GetDefaultMaxDistance(VoiceMode mode) => mode switch {
		VoiceMode.Broadcast => 100000f,
		VoiceMode.Shout => 100f,
		VoiceMode.Normal => 500f,
		VoiceMode.Whisper => 1.25f,
		_ => 0f
	};

	public override void OnEngineInit() {
#if DEBUG
		HotReloader.RegisterForHotReload(this);
#endif

		if (_babbeliteManager == null) {
			Msg("Initializing BabbeliteClient...");
			_babbeliteManager = new BabbeliteClient(false);
			DiscoveryAtHome();
			BubbleTrackingLoop();
		}

		Config = GetConfiguration()!;
		Config!.Save(true);
		Config.OnThisConfigurationChanged += OnConfigurationChanged;
		// Call setup method
		Setup();
	}

	static void Setup() {
		// Patch Harmony
		Harmony harmony = new(harmonyId);
		harmony.PatchAll();
	}

#if DEBUG
	// This is the method that should be used to unload your mod
	static void BeforeHotReload() {
		// Unpatch Harmony
		Harmony harmony = new(harmonyId);
		harmony.UnpatchAll(harmonyId);
		ResetState();
	}

	// This is called in the newly loaded assembly
	static void OnHotReload(ResoniteMod modInstance) {
		// Get the config if needed
		Config = modInstance.GetConfiguration()!;
		Config!.Save(true);

		// Call setup method
		Setup();
	}
#endif

	private static void BubbleTrackingLoop() {
		Task.Run(async () => {
			while (true) {
				await Task.Delay(500);

				try {
					bool currentlyInBubble = false;
					bool currentlyInRemoteBubble = false;
					World world = Engine.Current.WorldManager.FocusedWorld;

					foreach (User targetUser in world.AllUsers) {
						if (targetUser.ActiveVoiceMode == VoiceMode.Whisper) {
							float radius = GetAudioManager(targetUser)?.GetConfig(VoiceMode.Whisper)?.MaxDistance.Value ?? GetDefaultMaxDistance(VoiceMode.Whisper);
							if (targetUser.DistanceToLocalUserHead <= radius) {
								currentlyInBubble = true;
								currentlyInRemoteBubble = currentlyInRemoteBubble || !targetUser.IsLocalUser;
							}
						}
					}

					Interlocked.Exchange(ref _inWhisperBubble, currentlyInBubble ? 1 : 0);
					Interlocked.Exchange(ref _inRemoteWhisperBubble, currentlyInRemoteBubble ? 1 : 0);
				} catch { }
			}
		});
	}

	private static void ResetState() {
		Msg("Resetting babbelite state");
		lock (_userSessions) {
			foreach (LiveTranscriptionSession session in _userSessions.Values) {
				_ = Task.Run(() => {
					try { session?.Dispose(); } catch { }
				});
			}

			_userSessions.Clear(); 
		}
		lock (_pendingSessions) { _pendingSessions.Clear(); }
		lock (_audioAccumulators) { _audioAccumulators.Clear(); }
		lock (_transmitting) { _transmitting.Clear(); }
		lock (_lastHeardFrom) { _lastHeardFrom.Clear(); }
		lock (_audioManagers) { _audioManagers.Clear(); }
		Interlocked.Exchange(ref _inWhisperBubble, 0);
		Interlocked.Exchange(ref _inRemoteWhisperBubble, 0);
		if (_babbeliteManager != null) {
			try {
				var connectionsList = Traverse.Create(_babbeliteManager).Field<List<BabbeliteConnection>>("_connections").Value;
				if (connectionsList != null) {
					lock (connectionsList) {
						foreach (var conn in connectionsList) {
							try { Traverse.Create(conn).Method("Disconnect").GetValue(); } catch { }
						}
						connectionsList.Clear();
					}
				}
			} catch { }
		}
	}

	private static void ResetSession(LiveTranscriptionSession session, World world, RefID refId, bool killConnection = false) {
		lock (_userSessions) { _userSessions.Remove((world, refId)); }
		lock (_pendingSessions) { _pendingSessions.Remove((world, refId)); }
		lock (_audioAccumulators) { _audioAccumulators.Remove((world, refId)); }
		lock (_lastHeardFrom) { _lastHeardFrom.Remove((world, refId)); }
		lock (_transmitting) { _transmitting.Remove((world, refId)); }
		lock (_audioManagers) { _audioManagers.Remove((world, refId)); }

		_ = Task.Run(() => {
			try { session?.Dispose(); } catch { }
		});

		if (killConnection) {
			_ = Task.Run(() => {
				try {
					var conn = Traverse.Create(session).Property("Connection").GetValue() ??
							   Traverse.Create(session).Field("_connection").GetValue();

					if (conn != null) {
						var isConnectedProp = Traverse.Create(conn).Property("IsConnected");
						if (isConnectedProp.PropertyExists()) {
							isConnectedProp.SetValue(false);
						}

						Traverse.Create(conn).Method("Disconnect").GetValue();
					}
				} catch { }
			});
		}
	}

	private void OnConfigurationChanged(ConfigurationChangedEvent @event) {
		if (@event.Key == Enabled && !Config!.GetValue(Enabled)) {
			Msg("Mod was disabled, shutting down Babbelite client...");
			ResetState();
		} else if (@event.Key == TranscribeRemoteUsers && !Config!.GetValue(TranscribeRemoteUsers)) {
			Msg("Transcription of remote users disabled, cleaning up local slots");

			foreach (World world in Engine.Current.WorldManager.Worlds) {
				if (world == null) continue;

				world.RunSynchronously(() => {
					var remoteSessionsToPurge = new List<(World World, RefID RefID)>();

					foreach (User u in world.AllUsers) {
						if (u != null && !u.IsLocalUser) {
							remoteSessionsToPurge.Add((world, u.ReferenceID));

							Slot? localBabbeliteSlot = u.Root.Slot.FindLocalChild(SLOT_NAME);
							localBabbeliteSlot?.Destroy();
						}
					}

					foreach (var key in remoteSessionsToPurge) {
						lock (_userSessions) {
							if (_userSessions.TryGetValue(key, out var session)) {
								ResetSession(session, key.World, key.RefID, false);
							}
						}
					}
				});
			}
		}
	}


	// patches for babbelite itself
	static void DiscoveryAtHome() {
		try {
			var listenerField = typeof(BabbeliteClient).GetField("_listener", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
			var listener = listenerField?.GetValue(_babbeliteManager);

			if (listener != null) {
				var eventInfo = listener.GetType().GetEvent("ServerDiscovered");

				var handler = new Action<BabbeliteServerInfo>(OnServerDiscovered);
				var typedDelegate = Delegate.CreateDelegate(eventInfo!.EventHandlerType!, handler.Target, handler.Method);

				eventInfo.AddEventHandler(listener, typedDelegate);

				Msg("Now looking for Babbelite servers...");
			} else {
				Error("Couldn't find the BabbeliteClient's _listener!");
			}
		} catch (Exception ex) {
			Error($"Failed to hook discovery: {ex}");
		}
	}

	static void OnServerDiscovered(BabbeliteServerInfo info) {
		Msg($"Babbelite server discovered! Connecting to port {info.Port}! Name: {info.ServerName}");

		// force the connection to use localhost specifically, because otherwise the server refuses (might be something with watsonwsserver??)
		Uri localUri = new($"ws://localhost:{info.Port}");

		Task.Run(async () => {
			await _babbeliteManager!.ConnectTo(localUri, CancellationToken.None);
			NotificationMessage.SpawnTextMessage($"Connected to a Babbelite server!\nServer name: {info.ServerName}", colorX.Green, 0.65f, 5f);
		});
	}

	// return more useful error messages
	[HarmonyPatch(typeof(BabbeliteConnection), nameof(BabbeliteConnection.CreateTranscriptionSession))]
	class BabbeliteConnection_CreateTranscriptionSession_Patch {
		static void Postfix(BabbeliteConnection __instance, string sessionId, ref Task<LiveTranscriptionSession> __result) {
			var originalTask = __result;

			__result = Task.Run(async () => {
				try {
					var session = await originalTask.ConfigureAwait(false) ?? throw new Exception("Received null response from Babbelite server.");
					return session;

				} catch (Exception ex) {
					if (ex.InnerException is System.Net.Sockets.SocketException ||
						ex.InnerException is IOException ||
						ex.InnerException is WebSocketException) {

						Warn($"Lost connection to Babbelite server while creating session {sessionId}");

						World world = Engine.Current.WorldManager.FocusedWorld;
						world?.RunSynchronously(() => {
							NotificationMessage.SpawnTextMessage($"Lost connection to Babbelite server!", colorX.Red, 0.65f, 5f);
						});

						try { Traverse.Create(__instance).Method("Disconnect").GetValue(); } catch { }
					}
					throw;
				}
			});
		}
	}

	[HarmonyPatch(typeof(BabbeliteConnection), nameof(BabbeliteConnection.Connect))]
	class BabbeliteConnection_Connect_Patch {
		static void Postfix(BabbeliteConnection __instance, Uri target) {
			__instance.OnDisconnected += () => {
				Msg($"Disconnected from Babbelite server ({target})");

				World world = Engine.Current.WorldManager.FocusedWorld;
				world?.RunSynchronously(() => {
					NotificationMessage.SpawnTextMessage($"Disconnected from Babbelite server.", colorX.Orange, 0.65f, 5f);
				});

				ResetState();
			};
		}
	}


	// other users (incoming)
	[HarmonyPatch(typeof(OpusStream<MonoSample>), "DecodeSamples")]
	class OpusStream_Decode_Patch {
		public static void Postfix(OpusStream<MonoSample> __instance, ref float[] buffer, int __result) {
			if (__result <= 0 || buffer == null) return;
			if (__instance.Name != "Voice") return;

			User user = __instance.User;
			if (user == null) return;

			ProcessAudio(user, buffer, __result, __instance.EncodedSampleRate);
		}
	}

	// local user (outgoing)
	[HarmonyPatch(typeof(OpusStream<MonoSample>), "EncodeSamples")]
	class OpusStream_Encode_Patch {
		public static void Prefix(OpusStream<MonoSample> __instance, float[] buffer, int count) {
			if (count <= 0 || buffer == null) return;
			if (__instance.Name != "Voice") return;

			User user = __instance.User;
			if (user == null) return;

			ProcessAudio(user, buffer, count, __instance.EncodedSampleRate);
		}
	}

	// clear transcriptions immediately when muting
	[HarmonyPatch(typeof(User), "InternalRunStartup")]
	class User_VoiceMode_Patch {
		public static void Postfix(User __instance) {
			__instance.World.RunSynchronously(() => {
				if (__instance == null) return;

				__instance.isMuted.OnValueChange += (changeable) => {
					if (__instance.isMuted.Value) {
						ClearTranscription(__instance);
					}
				};
			});
		}

		static void ClearTranscription(User user) {
			static void ClearForSlot(Slot slot) {
				if (slot == null) return;

				GetOrAddVar<string>(slot, "User/Babbelite.Transcription");
				GetOrAddVar<bool>(slot, "User/Babbelite.IsCompleted");
				GetOrAddVar<float>(slot, "User/Babbelite.Confidence");
				GetOrAddVar<string>(slot, "User/Babbelite.Language");

				DynamicVariableHelper.WriteDynamicVariable<string>(slot, "User/Babbelite.Transcription", null!);
				DynamicVariableHelper.WriteDynamicVariable(slot, "User/Babbelite.IsCompleted", true);
				DynamicVariableHelper.WriteDynamicVariable(slot, "User/Babbelite.Confidence", 1f);
				DynamicVariableHelper.WriteDynamicVariable(slot, "User/Babbelite.Language", "en");
			}

			
			Slot localBabbeliteSlot = user.Root.Slot.FindLocalChild(SLOT_NAME);
			ClearForSlot(localBabbeliteSlot);
			if (user.IsLocalUser) { // if another users has a global babbelite slot, they're using the mod and would handle it themselves.
				Slot globalBabbeliteSlot = user.Root.Slot.FindChild(SLOT_NAME);
				ClearForSlot(globalBabbeliteSlot);
			};
		}
	}

	[HarmonyPatch(typeof(OpusStream<MonoSample>), "OnDispose")]
	class OpusStream_Dispose_Patch {
		public static void Prefix(OpusStream<MonoSample> __instance) {
			try {
				User user = __instance.User;
				if (user != null) {
					if (_userSessions.TryGetValue((user.World, user.ReferenceID), out var session)) {
						Msg($"Cleaning up {user.UserName}'s Babbelite session");
						session.Dispose();
						lock (_userSessions) {
							_userSessions.Remove((user.World, user.ReferenceID));
						}
					}
					lock (_pendingSessions) {
						_pendingSessions.Remove((user.World, user.ReferenceID));
					}
					lock (_audioAccumulators) {
						_audioAccumulators.Remove((user.World, user.ReferenceID));
					}
					lock (_lastHeardFrom) {
						_lastHeardFrom.Remove((user.World, user.ReferenceID));
					}
					lock (_transmitting) {
						_transmitting.Remove((user.World, user.ReferenceID));
					}
					lock (_audioManagers) {
						_audioManagers.Remove((user.World, user.ReferenceID));
					}
				}
			} catch (Exception ex) {
				Error($"Babbelite cleanup on OpusStream disposal failed: {ex}");
			}
		}
	}

	[HarmonyPatch(typeof(World), "Dispose")]
	class World_Dispose_Patch {
		static void Prefix(World __instance) {
			try {
				lock (_userSessions) {
					var keysToRemove = _userSessions.Keys.Where(k => k.World == __instance).ToList();
					foreach (var key in keysToRemove) {
						_userSessions[key]?.Dispose();
						_userSessions.Remove(key);
					}
				}
				lock (_pendingSessions) {
					_pendingSessions.RemoveWhere(k => k.World == __instance);
				}
				lock (_transmitting) {
					_transmitting.RemoveWhere(k => k.World == __instance);
				}
				lock (_audioAccumulators) {
					var keysToRemove = _audioAccumulators.Keys.Where(k => k.World == __instance).ToList();
					foreach (var key in keysToRemove) _audioAccumulators.Remove(key);
				}
				lock (_lastHeardFrom) {
					var keysToRemove = _lastHeardFrom.Keys.Where(k => k.World == __instance).ToList();
					foreach (var key in keysToRemove) _lastHeardFrom.Remove(key);
				}
				lock (_audioManagers) {
					var keysToRemove = _audioManagers.Keys.Where(k => k.World == __instance).ToList();
					foreach (var key in keysToRemove) _audioManagers.Remove(key);
				}

			} catch (Exception ex) {
				Error($"Babbelite cleanup on world disposal failed: {ex}");
			}
		}
	}

	private static float[] ResampleAudio(float[] input, int count, double sourceRate, double targetRate) {
		if (input == null || count <= 0 || targetRate <= 0 || sourceRate <= 0)
			return [];

		// if it's 48000 source to 16000 target, do quick math instead
		if (Math.Abs(sourceRate - 48000) < 1.0 && Math.Abs(targetRate - 16000) < 1.0) {
			int newLen = count / 3;
			float[] fastOutput = new float[newLen];
			for (int i = 0; i < newLen; i++) {
				fastOutput[i] = input[i * 3];
			}
			return fastOutput;
		}

		// if basically the same, just slice it and be done
		if (Math.Abs(sourceRate - targetRate) < 1.0) {
			float[] exactOutput = new float[count];
			Array.Copy(input, exactOutput, count);
			return exactOutput;
		}

		// linear interpolation </3
		double ratio = sourceRate / targetRate;
		int targetLength = (int)Math.Floor(count / ratio);
		if (targetLength <= 0)
			return [];

		float[] output = new float[targetLength];

		for (int i = 0; i < targetLength; i++) {
			double srcIndex = i * ratio;
			int indexFloor = (int)srcIndex;
			int indexCeil = Math.Min(indexFloor + 1, count - 1);
			float fraction = (float)(srcIndex - indexFloor);

			output[i] = input[indexFloor] * (1.0f - fraction) + input[indexCeil] * fraction;
		}

		return output;
	}

	private static async Task<bool> PushAudioWithTimeout(LiveTranscriptionSession session, float[] chunk) {
		Task pushTask = session.PushAudioData(chunk);
		Task timeoutTask = Task.Delay(5000);

		if (await Task.WhenAny(pushTask, timeoutTask) == timeoutTask) {
			return false;
		}

		await pushTask;
		return true;
	}

	private static AvatarAudioOutputManager? GetAudioManager(User user) {
		if (user == null || user.Root?.Slot == null) return null;
		var key = (user.World, user.ReferenceID);

		if (_audioManagers.TryGetValue(key, out AvatarAudioOutputManager? manager)) {
			if (manager != null && (manager.IsRemoved || manager.IsDisposed)) {
				_audioManagers.Remove(key);
			} else {
				return manager;
			}
		}

		manager = user.Root.Slot.GetComponentInChildren<AvatarAudioOutputManager>();

		lock (_audioManagers) {
			_audioManagers[key] = manager;
		}

		return manager;
	}

	[HarmonyPatch(typeof(AvatarAudioOutputManager), nameof(AvatarAudioOutputManager.OnEquip))]
	class AvatarAudioOutputManager_OnEquip_Patch {
		public static void Postfix(AvatarAudioOutputManager __instance, AvatarObjectSlot slot) {
			User? user = slot.Slot.ActiveUserRoot?.ActiveUser;
			if (user != null) {
				lock (_audioManagers) {
					_audioManagers[(user.World, user.ReferenceID)] = __instance;
				}
			}
		}
	}

	[HarmonyPatch(typeof(AvatarAudioOutputManager), nameof(AvatarAudioOutputManager.OnDequip))]
	class AvatarAudioOutputManager_OnDequip_Patch {
		public static void Postfix(AvatarAudioOutputManager __instance, AvatarObjectSlot slot) {
			User? user = slot.Slot.ActiveUserRoot?.ActiveUser;
			if (user != null) {
				lock (_audioManagers) {
					_audioManagers.Remove((user.World, user.ReferenceID));
				}
			}
		}
	}

	[HarmonyPatch(typeof(AvatarAudioOutputManager), "OnDispose")]
	class AvatarAudioOutputManager_OnDispose_Patch {
		public static void Postfix(AvatarAudioOutputManager __instance) {
			lock (_audioManagers) {
				List<(World, RefID)> keysToRemove = [.. _audioManagers
					.Where(kvp => kvp.Value == __instance)
					.Select(kvp => kvp.Key)];

				foreach (var key in keysToRemove) {
					_audioManagers.Remove(key);
				}
			}
		}
	}

	private static void CleanDeadConnections() {
		if (_babbeliteManager == null) return;
		try {
			var connectionsList = Traverse.Create(_babbeliteManager).Field<List<BabbeliteConnection>>("_connections").Value;
			if (connectionsList != null) {
				lock (connectionsList) {
					connectionsList.RemoveAll(c => !c.IsConnected);
				}
			}
		} catch { }
	}
	
	private static DynamicValueVariable<T> GetOrAddVar<T>(Slot slot, string varName) {
		var dynVar = slot.GetComponent<DynamicValueVariable<T>>(v => v.VariableName.Value == varName);
		if (dynVar == null) {
			dynVar = slot.AttachComponent<DynamicValueVariable<T>>();
			dynVar.VariableName.Value = varName;
		}
		return dynVar;
	}

	private static (bool result, bool transcriptionIsUserspaceOnly, bool userspaceIgnoresPauses) IsAudioProcessingAllowed(User user) {
		bool inWhisperBubble = Volatile.Read(ref _inWhisperBubble) == 1;
		bool inRemoteWhisperBubble = Volatile.Read(ref _inRemoteWhisperBubble) == 1;
		bool allowUserspaceTranscription = Config!.GetValue(UserspaceIgnoresPauses);
		bool transcriptionIsUserspaceOnly = false;

		if (inWhisperBubble) {
			WhisperBubblePlan plan = Config!.GetValue(PauseInWhisperBubbles);

			switch (plan) {
				case WhisperBubblePlan.PauseAll:
					if (allowUserspaceTranscription && user.IsLocalUser) {
						transcriptionIsUserspaceOnly = true;
						break;
					}
					return (false, transcriptionIsUserspaceOnly, allowUserspaceTranscription);
				case WhisperBubblePlan.PauseAllInRemote:
					if (allowUserspaceTranscription && user.IsLocalUser) {
						transcriptionIsUserspaceOnly = true;
						break;
					}
					if (inRemoteWhisperBubble) return (false, transcriptionIsUserspaceOnly, allowUserspaceTranscription);
					break;
				case WhisperBubblePlan.PauseExcludingSelf:
					if (!user.IsLocalUser) return (false, transcriptionIsUserspaceOnly, allowUserspaceTranscription);
					break;
				case WhisperBubblePlan.DontPause:
					break;
			}
		}
		return (true, transcriptionIsUserspaceOnly, allowUserspaceTranscription);
	}

	static void WriteUserspaceTranscription(string text, bool completed, float? confidence, string language) {
		string trimmed = text.Trim();
		if (Config!.GetValue(Filter)) {
			if (Hallucinations.Contains(trimmed)) return;
			if (trimmed.Contains("*mimic") || trimmed.Contains("[mimic") || trimmed.Contains("(mimic")) return;
			if (trimmed.Contains("*imitat") || trimmed.Contains("[imitat") || trimmed.Contains("(imitat")) return;
		}

		Userspace.UserspaceWorld.RunSynchronously(() => {
			userspaceSlot ??= Userspace.UserspaceWorld.RootSlot.FindChildOrAdd(SLOT_NAME);
			DynamicValueVariable<string> textVar = GetOrAddVar<string>(userspaceSlot, "World/Babbelite.Transcription");
			DynamicValueVariable<bool> isCompletedVar = GetOrAddVar<bool>(userspaceSlot, "World/Babbelite.IsCompleted");
			DynamicValueVariable<float> confidenceVar = GetOrAddVar<float>(userspaceSlot, "World/Babbelite.Confidence");
			DynamicValueVariable<string> languageVar = GetOrAddVar<string>(userspaceSlot, "World/Babbelite.Language");

			DynamicVariableHelper.WriteDynamicVariable(userspaceSlot, "World/Babbelite.Transcription", trimmed);
			DynamicVariableHelper.WriteDynamicVariable(userspaceSlot, "World/Babbelite.IsCompleted", completed);
			DynamicVariableHelper.WriteDynamicVariable(userspaceSlot, "World/Babbelite.Confidence", confidence ?? 1f);
			DynamicVariableHelper.WriteDynamicVariable(userspaceSlot, "World/Babbelite.Language", language);
		});
	}

	private static void ProcessAudio(User user, float[] audioData, int count, int sourceSampleRate) {
		if (!Config!.GetValue(Enabled)) return;
		if (!Config!.GetValue(TranscribeRemoteUsers) && !user.IsLocalUser) return;
		if (!IsAudioProcessingAllowed(user).result) return;

		RefID refId = user.ReferenceID;
		World world = user.World;
		string userName = user.UserName;
		string userId = String.IsNullOrEmpty(user.UserID) ? user.MachineID : user.UserID;
		byte allocationId = user.AllocationID;

		if (_babbeliteManager == null || _babbeliteManager.ConnectionCount <= 0) return;

		if (_userSessions.TryGetValue((world, refId), out LiveTranscriptionSession? session)) {
			float[] resampled = ResampleAudio(audioData, count, sourceSampleRate, session.SampleRate);
			if (resampled == null || resampled.Length == 0) return;

			lock (_lastHeardFrom) { _lastHeardFrom[(world, refId)] = DateTime.UtcNow; }

			lock (_audioAccumulators) {
				if (!_audioAccumulators.TryGetValue((world, refId), out List<float>? accumulator)) {
					accumulator = new List<float>(SILERO_CHUNK_SIZE * 4);
					_audioAccumulators[(world, refId)] = accumulator;
				}

				accumulator.AddRange(resampled);

				if (accumulator.Count > session.SampleRate*2) {
					accumulator.RemoveRange(0, accumulator.Count - (SILERO_CHUNK_SIZE * 4));
				}

				if (accumulator.Count >= SILERO_CHUNK_SIZE) {
					bool lockTaken = false;
					lock (_transmitting) {
						if (!_transmitting.Contains((world, refId))) {
							_transmitting.Add((world, refId));
							lockTaken = true;
						}
					}

					if (lockTaken) {
						List<float[]> chunksToPush = [];
						while (accumulator.Count >= SILERO_CHUNK_SIZE) {
							chunksToPush.Add([.. accumulator.GetRange(0, SILERO_CHUNK_SIZE)]);
							accumulator.RemoveRange(0, SILERO_CHUNK_SIZE);
						}

						_ = Task.Run(async () => {
							try {
								foreach (var chunk in chunksToPush) {
									bool success = await PushAudioWithTimeout(session, chunk);
									if (!success) {
										Warn($"Audio push timed out for {userName}!");
										break;
									}
								}
							} catch (Exception ex) {
								string errorMsg = ex.InnerException?.Message ?? ex.Message;
								Warn($"Error while trying to push audio for {userName}: {errorMsg}");
								ResetSession(session, world, refId);
							} finally {
								lock (_transmitting) { _transmitting.Remove((world, refId)); }
							}
						});
					}
				}
			}

			return;
		}

		lock (_pendingSessions) {
			if (_pendingSessions.Contains((world, refId))) return;
			Msg($"Requesting new Babbelite session for {userName}");
			_pendingSessions.Add((world, refId));
		}

		Task.Run(async () => {
			try {
				CleanDeadConnections();
				LiveTranscriptionSession newSession = await _babbeliteManager!.CreateTranscriptionSession($"{userId} @ {world.SessionId}");

				lock (_userSessions) {
					_userSessions[(world, refId)] = newSession;
				}

				newSession.TranscriptionUpdated += (transcription) => {
					try {
						World world = Engine.Current.WorldManager.FocusedWorld;
						if (world == null) return;

						world.RunSynchronously(() => {
							try {
								User targetUser = world.GetUserByAllocationID(allocationId);
								if (targetUser != null) {
									(bool audioProcessingAllowed, bool transcriptionIsUserspaceOnly, bool userspaceIgnoresPauses) = IsAudioProcessingAllowed(user);
									if (!audioProcessingAllowed) return;

									Slot? globalBabbeliteSlot = targetUser.Root.Slot.FindChild(SLOT_NAME);
									Slot? localBabbeliteSlot = targetUser.Root.Slot.FindLocalChild(SLOT_NAME);

									bool exposeToWorld = Config!.GetValue(ExposeToWorld);
									VoiceMode currentVoiceMode = Config!.GetValue(TranscribeLocalMuted) ? (targetUser.isMuted ? VoiceMode.Mute : targetUser.VoiceMode) : targetUser.ActiveVoiceMode;

									if (targetUser.IsLocalUser) {
										if (!transcriptionIsUserspaceOnly) {
											if (exposeToWorld) { // exposing self transcription to world
												if (localBabbeliteSlot != null) {
													localBabbeliteSlot.Destroy();
													localBabbeliteSlot = null;
												}

												globalBabbeliteSlot ??= targetUser.Root.Slot.AddSlot(SLOT_NAME, false);

												if (targetUser.isMuted) {
													WriteTranscription(globalBabbeliteSlot, null!, true, 1f, "en");
												} else {
													WriteTranscription(globalBabbeliteSlot, transcription.Text, transcription.IsCompleted, transcription.ConfidenceLevel, transcription.LanguageCode);
												}
											} else { // keeping self transcription local
												if (globalBabbeliteSlot != null) {
													globalBabbeliteSlot.Destroy();
													globalBabbeliteSlot = null;
												}

												localBabbeliteSlot ??= targetUser.Root.Slot.FindLocalChildOrAdd(SLOT_NAME);
												if (targetUser.isMuted) {
													WriteTranscription(localBabbeliteSlot, null!, true, 1f, "en");
												} else {
													WriteTranscription(localBabbeliteSlot, transcription.Text, transcription.IsCompleted, transcription.ConfidenceLevel, transcription.LanguageCode);
												}
											}
										}
										if (userspaceIgnoresPauses || currentVoiceMode != VoiceMode.Mute) {
											WriteUserspaceTranscription(transcription.Text, transcription.IsCompleted, transcription.ConfidenceLevel, transcription.LanguageCode);
										} else {
											WriteUserspaceTranscription(null!, true, 1f, "en");
										}
									} else { // other players
										if (globalBabbeliteSlot != null) { // this user has the mod and is exposing their babbelite slot
											localBabbeliteSlot?.Destroy();
										} else if (Config!.GetValue(TranscribeRemoteUsers)) { // this user does not have the mod, so transcribe locally
											localBabbeliteSlot ??= targetUser.Root.Slot.FindLocalChildOrAdd(SLOT_NAME);

											if (targetUser.isMuted) {
												WriteTranscription(localBabbeliteSlot, null!, true, 1f, "en");
											} else {
												// check if theyre within hearing range
												ViewReferenceController? viewRefController = targetUser.Root.GetRegisteredComponent<ViewReferenceController>();
												AvatarAudioOutputManager? audioManager = GetAudioManager(targetUser);

												// trying to find the true 'voice' position would probably be too intensive to do so often, so instead we're just using the headslot as a reasonable guesstimate
												// if they're in freecam, it uses either the head or the freecam, whichever is closest (as long as theyre not whispering)
												Slot? targetFreecam = (viewRefController.ObjectSlot.Target.Slot ?? viewRefController.Slot);
												Slot targetUserVoiceSlot = ((viewRefController?.ShouldVoiceBeActive.Value ?? false) && currentVoiceMode != VoiceMode.Whisper) ? (targetFreecam.DistanceFromUserHead() < targetUser.DistanceToLocalUserHead ? targetFreecam : targetUser.Root.HeadSlot) : targetUser.Root.HeadSlot;
												
												Slot localUserListenerSlot = world.LocalUser.Root.PrimaryListener.Target.Slot;
												float maxDistance = audioManager?.GetConfig(currentVoiceMode)?.MaxDistance.Value ?? GetDefaultMaxDistance(currentVoiceMode);

												if ((currentVoiceMode == VoiceMode.Whisper ? targetUser.DistanceToLocalUserHead : MathX.Distance(targetUserVoiceSlot.GlobalPosition, localUserListenerSlot.GlobalPosition)) <= maxDistance) {
													WriteTranscription(localBabbeliteSlot, transcription.Text, transcription.IsCompleted, transcription.ConfidenceLevel, transcription.LanguageCode);
												}
											}
										}
									}

									static void WriteTranscription(Slot slot, string text, bool completed, float? confidence, string language) {
										string trimmed = text.Trim();
										if (Config!.GetValue(Filter)) {
											if (Hallucinations.Contains(trimmed)) return;
											if (trimmed.Contains("*mimic") || trimmed.Contains("[mimic") || trimmed.Contains("(mimic")) return;
											if (trimmed.Contains("*imitat") || trimmed.Contains("[imitat") || trimmed.Contains("(imitat")) return;
										}

										DynamicValueVariable<string> textVar = GetOrAddVar<string>(slot, "User/Babbelite.Transcription");
										DynamicValueVariable<bool> isCompletedVar = GetOrAddVar<bool>(slot, "User/Babbelite.IsCompleted");
										DynamicValueVariable<float> confidenceVar = GetOrAddVar<float>(slot, "User/Babbelite.Confidence");
										DynamicValueVariable<string> languageVar = GetOrAddVar<string>(slot, "User/Babbelite.Language");

										DynamicVariableHelper.WriteDynamicVariable(slot, "User/Babbelite.Transcription", trimmed);
										DynamicVariableHelper.WriteDynamicVariable(slot, "User/Babbelite.IsCompleted", completed);
										DynamicVariableHelper.WriteDynamicVariable(slot, "User/Babbelite.Confidence", confidence ?? 1f);
										DynamicVariableHelper.WriteDynamicVariable(slot, "User/Babbelite.Language", language);
									}
								}
							} catch (Exception innerEx) {
								Error($"RunSynchronously error: {innerEx}");
							}
						});
					} catch (Exception ex) {
						Error($"TranscriptionUpdated error: {ex}");
					}
				};

				_ = Task.Run(async () => {
					float[] silentChunk = new float[SILERO_CHUNK_SIZE];
					while (!newSession.IsDisposed) {
						await Task.Delay(32);

						DateTime lastAudio;
						lock (_lastHeardFrom) {
							if (!_lastHeardFrom.TryGetValue((world, refId), out lastAudio)) {
								lastAudio = DateTime.UtcNow;
								lock (_lastHeardFrom) { _lastHeardFrom[(world, refId)] = DateTime.UtcNow; }
							}
						}

						double timeSinceLastAudio = (DateTime.UtcNow - lastAudio).TotalMilliseconds;

						if (timeSinceLastAudio > 150 && timeSinceLastAudio < 1000) {
							bool lockTaken = false;
							try {
								lock (_transmitting) {
									if (!_transmitting.Contains((world, refId))) {
										_transmitting.Add((world, refId));
										lockTaken = true;
									}
								}

								if (lockTaken) {
									bool success = await PushAudioWithTimeout(newSession, silentChunk);
									if (!success) {
										Warn($"Silence push timed out for {userName}.");
									}
								}
							} catch (Exception ex) {
								string errorMsg = ex.InnerException?.Message ?? ex.Message;
								Warn($"Error while trying to push silence for {userName}: {errorMsg}");
								ResetSession(newSession, world, refId);
							} finally {
								if (lockTaken) {
									lock (_transmitting) { _transmitting.Remove((world, refId)); }
								}
							}
						}
					}
				});

				Msg($"Transcription session established for {userName}. Server is requesting sample rate of {newSession.SampleRate}Hz");
				lock (_pendingSessions) {
					_pendingSessions.Remove((world, refId));
				}
			} catch (Exception ex) {
				Warn($"Failed to create Babbelite session for {userName}: {ex.Message}");

				lock (_pendingSessions)
				{
					if ((DateTime.UtcNow - _lastErrorNotificationTime).TotalSeconds > 60) {
						_lastErrorNotificationTime = DateTime.UtcNow;
						if (ex.Message.Contains("whisper", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("model", StringComparison.OrdinalIgnoreCase)) {
							NotificationMessage.SpawnTextMessage($"The speech-to-text model is missing from your Babbelite server!", colorX.Red, 0.65f, 5f);
						} else if(ex.Message.Contains("Could not find a free Babbelite server connection", StringComparison.OrdinalIgnoreCase)) {
							Warn("Possible zombie connection detected, cleaning");
							CleanDeadConnections();
							NotificationMessage.SpawnTextMessage($"Lost connection to Babbelite server!", colorX.Red, 0.65f, 5f);
						} else {
							NotificationMessage.SpawnTextMessage($"Failed to create a Babbelite session, please check logs", colorX.Red, 0.65f, 5f);
						}
					}
				}

				await Task.Delay(5000);

				lock (_pendingSessions) {
					_pendingSessions.Remove((world, refId));
				}
			}
		});
	}
}
