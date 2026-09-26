using FrooxEngine;

using HarmonyLib;

using ResoniteModLoader;

using Elements.Assets;
using Elements.Core;

using Babbelite.Client;
using Babbelite.Shared;

using System.Net.WebSockets;




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

	[AutoRegisterConfigKey]
	public static readonly ModConfigurationKey<bool> Enabled = new("Enabled", "Enables the mod.", () => true);

	private static BabbeliteClient? _babbeliteManager;
	private static readonly Dictionary<RefID, LiveTranscriptionSession> _userSessions = [];
	private static readonly HashSet<RefID> _pendingSessions = [];
	private static DateTime _lastErrorNotificationTime = DateTime.MinValue;

	private static readonly Dictionary<RefID, List<float>> _audioAccumulators = [];
	private const int SILERO_CHUNK_SIZE = 512; 

	public override void OnEngineInit() {
#if DEBUG
		HotReloader.RegisterForHotReload(this);
#endif

		if (_babbeliteManager == null) {
			Msg("Initializing BabbeliteClient...");
			_babbeliteManager = new BabbeliteClient(false);
			DiscoveryAtHome();
		}

		Config = GetConfiguration()!;
		Config!.Save(true);
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

	// patching babbelite itself to return more useful error messages
	[HarmonyPatch(typeof(BabbeliteConnection), nameof(BabbeliteConnection.CreateTranscriptionSession))]
	class BabbeliteConnection_CreateTranscriptionSession_Patch {
		static bool Prefix(BabbeliteConnection __instance, string sessionId, ref Task<LiveTranscriptionSession> __result) {
			__result = CreateSessionSafe(__instance, sessionId);
			return false;
		}

		static async Task<LiveTranscriptionSession> CreateSessionSafe(BabbeliteConnection connection, string sessionId) {
			if (string.IsNullOrWhiteSpace(sessionId))
				sessionId = Guid.NewGuid().ToString();

			var createMessage = new CreateLiveTranscribeSession() {
				SessionId = sessionId,
			};

			var method = (typeof(BabbeliteConnection)
				.GetMethod("SendMessage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
				?.MakeGenericMethod(typeof(CreateLiveTranscribeSession), typeof(Response))) ?? throw new InvalidOperationException("Could not find SendMessage on BabbeliteConnection");
			try {
				var task = (Task<Response>)method.Invoke(connection, [createMessage])!;
				var response = await task.ConfigureAwait(false) ?? throw new Exception("Received null response from Babbelite server.");
				if (!response.IsSuccess)
					throw new Exception($"Server Error: {response.ErrorMessage}");

				if (response is TranscribeSessionCreated created) {
					var session = new LiveTranscriptionSession(connection, created.SessionId, created.SampleRate);
					var sessionsDict = Traverse.Create(connection).Field<Dictionary<string, LiveTranscriptionSession>>("_transcriptionSessions").Value;
					lock (sessionsDict) {
						sessionsDict[sessionId] = session;
					}
					return session;
				}

				throw new Exception($"Unexpected response type from server: {response.GetType().Name}");
			} catch (Exception ex) {
				if (ex.InnerException is System.Net.Sockets.SocketException ||
					ex.InnerException is IOException ||
					ex.InnerException is WebSocketException) {
					Warn("Lost connection to Babbelite server!");

					World world = Engine.Current.WorldManager.FocusedWorld;
					world?.RunSynchronously(() => {
							NotificationMessage.SpawnTextMessage($"Lost connection to Babbelite server!", colorX.Red, 0.65f, 5f);
						});

					lock (_userSessions) { _userSessions.Clear(); }
					lock (_pendingSessions) { _pendingSessions.Clear(); }
					lock (_audioAccumulators) { _audioAccumulators.Clear(); }

					// manually remove it from BabbeliteClient ourselves
					if (_babbeliteManager != null) {
						var connectionsList = Traverse.Create(_babbeliteManager).Field<List<BabbeliteConnection>>("_connections").Value;
						if (connectionsList != null) {
							lock (connectionsList) {
								connectionsList.Remove(connection);
							}
						}
					}
				}

				throw;
			}
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

				lock (_userSessions) { _userSessions.Clear(); }
				lock (_pendingSessions) { _pendingSessions.Clear(); }
				lock (_audioAccumulators) { _audioAccumulators.Clear(); }
			};
		}
	}


	// other users (incoming)
	[HarmonyPatch(typeof(OpusStream<MonoSample>), "DecodeSamples")]
	class OpusStream_Decode_Patch {
		public static void Postfix(OpusStream<MonoSample> __instance, ref float[] buffer, int __result) {
			if (__result <= 0 || buffer == null) return;

			User user = __instance.User;
			if (user == null) return;

			float[] audioData = new float[__result];
			Array.Copy(buffer, audioData, __result);

			ProcessAudio(user, audioData, __instance.EncodedSampleRate);
		}
	}

	// local user (outgoing)
	[HarmonyPatch(typeof(OpusStream<MonoSample>), "EncodeSamples")]
	class OpusStream_Encode_Patch {
		public static void Prefix(OpusStream<MonoSample> __instance, float[] buffer, int count) {
			if (count <= 0 || buffer == null) return;

			User user = __instance.User;
			if (user == null) return;

			float[] audioData = new float[count];
			Array.Copy(buffer, audioData, count);

			ProcessAudio(user, audioData, __instance.EncodedSampleRate);
		}
	}

	[HarmonyPatch(typeof(OpusStream<MonoSample>), "OnDispose")]
	class OpusStream_Dispose_Patch {
		public static void Postfix(OpusStream<MonoSample> __instance) {
			User user = __instance.User;
			if (user != null) {
				if (_userSessions.TryGetValue(user.ReferenceID, out var session)) {
					Msg($"Cleaning up {user.UserName}'s Babbelite session");
					session.Dispose();
					lock (_userSessions) {
						_userSessions.Remove(user.ReferenceID);
					}
				}
				lock (_pendingSessions) {
					_pendingSessions.Remove(user.ReferenceID);
				}
				lock (_audioAccumulators) {
					_audioAccumulators.Remove(user.ReferenceID);
				}
			}
		}
	}

	private static float[] ResampleAudio(float[] input, double sourceRate, double targetRate) {
		if (input == null || input.Length == 0 || targetRate <= 0 || sourceRate <= 0)
			return input ?? [];

		// if theyre basically the same, do nothing
		if (Math.Abs(sourceRate - targetRate) < 1.0)
			return input;


		// if it's 48000 source to 16000 target, do quick math instead
		if (Math.Abs(sourceRate - 48000) < 1.0 && Math.Abs(targetRate - 16000) < 1.0) {
			int newLen = input.Length / 3;
			float[] fastOutput = new float[newLen];
			for (int i = 0; i < newLen; i++) {
				fastOutput[i] = input[i * 3];
			}
			return fastOutput;
		}

		// linear interpolation </3
		double ratio = sourceRate / targetRate;
		int targetLength = (int)Math.Floor(input.Length / ratio);
		if (targetLength <= 0)
			return [];

		float[] output = new float[targetLength];

		for (int i = 0; i < targetLength; i++) {
			double srcIndex = i * ratio;
			int indexFloor = (int)srcIndex;
			int indexCeil = Math.Min(indexFloor + 1, input.Length - 1);
			float fraction = (float)(srcIndex - indexFloor);

			output[i] = input[indexFloor] * (1.0f - fraction) + input[indexCeil] * fraction;
		}

		return output;
	}

	private static void ProcessAudio(User user, float[] audioData, int sourceSampleRate) {
		RefID refId = user.ReferenceID;
		string userName = user.UserName;
		string userId = user.UserID;
		byte allocationId = user.AllocationID;

		if (_babbeliteManager == null || _babbeliteManager.ConnectionCount <= 0) {
			return;
		}

		if (_userSessions.TryGetValue(refId, out LiveTranscriptionSession? session)) {
			float[] resampled = ResampleAudio(audioData, sourceSampleRate, session.SampleRate);

			if (resampled == null || resampled.Length == 0)
				return;

			lock (_audioAccumulators) {
				if (!_audioAccumulators.TryGetValue(refId, out List<float>? accumulator)) {
					accumulator = new List<float>(SILERO_CHUNK_SIZE * 4);
					_audioAccumulators[refId] = accumulator;
				}

				accumulator.AddRange(resampled);

				while (accumulator.Count >= SILERO_CHUNK_SIZE) {
					float[] chunkToPush = [.. accumulator.GetRange(0, SILERO_CHUNK_SIZE)];
					accumulator.RemoveRange(0, SILERO_CHUNK_SIZE);

					session.PushAudioData(chunkToPush).ContinueWith(t => {
						if (t.IsFaulted && t.Exception != null) {
							string errorMsg = t.Exception.InnerException?.Message ?? t.Exception.Message;
							if (!errorMsg.Contains("disposed") && !errorMsg.Contains("ClientWebSocket")) {
								Error($"PushAudioData error for {userName}: {errorMsg}");
							}
							if (errorMsg.Contains("disposed") || errorMsg.Contains("aborted") || errorMsg.Contains("ClientWebSocket")) {
								lock (_userSessions) { _userSessions.Clear(); }
								lock (_pendingSessions) { _pendingSessions.Clear(); }
								lock (_audioAccumulators) { _audioAccumulators.Clear(); }

								if (_babbeliteManager != null) {
									var connectionsList = Traverse.Create(_babbeliteManager).Field<List<BabbeliteConnection>>("_connections").Value;
									if (connectionsList != null) {
										lock (connectionsList) {
											connectionsList.Clear();
										}
									}
								}
							}
						}
					}, TaskContinuationOptions.OnlyOnFaulted);
				}
			}

			return;
		}

		if (_pendingSessions.Contains(refId)) {
			return;
		}

		Msg($"Requesting new Babbelite session for {userName}");
		_pendingSessions.Add(refId);

		Task.Run(async () => {
			try {
				LiveTranscriptionSession newSession = await _babbeliteManager!.CreateTranscriptionSession(userId);

				lock (_userSessions) {
					_userSessions[refId] = newSession;
				}

				newSession.TranscriptionUpdated += (transcription) => {
					Msg("Transcription updated.");
					try {
						World world = Engine.Current.WorldManager.FocusedWorld;
						if (world == null) return;

						world.RunSynchronously(() => {
							try {
								User targetUser = world.GetUserByAllocationID(allocationId);
								if (targetUser != null) {
									Slot localBabbeliteSlot = targetUser.Root.Slot.FindChildOrAdd("Babbelite");

									DynamicValueVariable<string>? dynVar = localBabbeliteSlot.GetComponent<DynamicValueVariable<string>>(v => v.VariableName.Value == "User/Babbelite.Transcription");
									if (dynVar == null) {
										dynVar = localBabbeliteSlot.AttachComponent<DynamicValueVariable<string>>();
										dynVar.VariableName.Value = "User/Babbelite.Transcription";
									}

									Msg($"{targetUser.userName}: {transcription.Text}");
									DynamicVariableHelper.WriteDynamicVariable(localBabbeliteSlot, "User/Babbelite.Transcription", transcription.Text);
								}
							} catch (Exception innerEx) {
								Error($"RunSynchronously error: {innerEx}");
							}
						});
					} catch (Exception ex) {
						Error($"TranscriptionUpdated error: {ex}");
					}
				};

				Msg($"Transcription session established for {userName}. Server is requesting sample rate of {newSession.SampleRate}Hz");
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
							if (_babbeliteManager != null) {
								var connectionsList = Traverse.Create(_babbeliteManager).Field<List<BabbeliteConnection>>("_connections").Value;
								if (connectionsList != null) {
									lock (connectionsList) {
										connectionsList.RemoveAll(c => !c.IsConnected);
									}
									NotificationMessage.SpawnTextMessage($"Lost connection to Babbelite server!", colorX.Red, 0.65f, 5f);
								}
							}
							lock (_userSessions) { _userSessions.Clear(); }
							lock (_pendingSessions) { _pendingSessions.Clear(); }
							lock (_audioAccumulators) { _audioAccumulators.Clear(); }
						} else {
							NotificationMessage.SpawnTextMessage($"Failed to create a Babbelite session, please check logs", colorX.Red, 0.65f, 5f);
						}
					}
				}

				await Task.Delay(5000);

				lock (_pendingSessions) {
					_pendingSessions.Remove(refId);
				}
			}
		});
	}
}
