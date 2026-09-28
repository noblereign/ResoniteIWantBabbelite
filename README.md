# IWantBabbelite

A [ResoniteModLoader](https://github.com/resonite-modding-group/ResoniteModLoader) mod for [Resonite](https://resonite.com/) that implements a (*very* scuffed) [Babbelite](https://github.com/Yellow-Dog-Man/Babbelite) client.

Currently only tested with transcription.

## Screenshots

https://github.com/user-attachments/assets/5eb551f1-85ad-47b8-b719-701a895d0f09

<img width="1550" height="872" alt="Discord_114932" src="https://github.com/user-attachments/assets/8abe90e9-eff5-4e8f-8e1a-65337f835352" />

<img width="979" height="512" alt="LosslessCut_114930" src="https://github.com/user-attachments/assets/6413ef48-1300-4a4e-8924-0bc8859f0e69" />

## Usage
The mod exposes Babbelite's output through a few Dynamic Variables:

(**string**) `Babbelite.Transcription`: The result of the transcription chunk.

(**bool**) `Babbelite.IsCompleted`: Whether the transcription is considered 'complete'. When false, it represents a partial transcription.

(**float**) `Babbelite.Confidence`: How 'confident' the model is that the output is correct, in a range from 0 to 1.

(**string**) `Babbelite.Language`: The predicted language code for the transcription. (e.g. `en` for English)

The variables are attached to each player's User Root, under the `User/` dynamic variable space.

In Userspace, they're attached to the world root, under the `World/` dynamic variable space.

> [!NOTE]
> Other users won't be able to read these variables, they are clientsided. You'll have to write the output somewhere manually if you want it to be networked.
>
> However, users of the mod can enable the "Expose to world" option. This allows the local user's *personal* transcription to be accessed by anyone in the session. Transcriptions of people who don't have the mod will always be local.

## Installing the Babbelite server
1. Clone the [Babbelite source code.](https://github.com/Yellow-Dog-Man/Babbelite).
2. Build the project, usually using some form of [Visual Studio](https://visualstudio.microsoft.com/downloads/) or `dotnet`.
3. After building, you should have the Babbelite server executable (`Babbelite.Server.CLI.exe`) at or near `Babbelite\Babbelite.Server.CLI\bin\Release\net10.0`.
4. Download the necessary models.

> [!IMPORTANT]
> Babbelite requires Silero VAD and OpenAI Whisper for transcription.
> 
> You can get the Silero VAD onnx from [this GitHub repo](https://github.com/snakers4/silero-vad/blob/master/src/silero_vad/data/silero_vad.onnx).
> 
> For Whisper, you specifically need the Whisper.NET flavor of it. There's a friendly interface to browse them [here](https://huggingface.co/sandrohanea/whisper.net/tree/main), but I've personally had more luck downloading them from [the URLs in EchoSharp's source code](https://github.com/Yellow-Dog-Man/echosharp/blob/main/components/EchoSharp.Whisper.net/WhisperNetModels.cs).
5. Place those models near the server executable, e.g. in a folder named `models`.
6. Create a `config.json` next to the server executable.

> [!TIP]
> An example config file:
> ```json
> {
>   "serverName": "My Babbelite Server",
>   "port": 12052,
>   "hostName": "localhost",
>   "transcription": {
>     "$type": "whisper",
>     "whisperModelPath": "models/ggml-base.bin",
>     "sileroVadModelPath": "models/silero_vad.onnx",
>     "sileroVadThreshold": 0.1,
>     "sileroVadThresholdGap": 0.05
>   }
> }
> ```

7. It should be ready by now, go ahead and launch `Babbelite.Server.CLI.exe`! If all goes well, the log output should look like this:
>
> Loading Config.json
>
> WS: [WatsonWsServer] starting on: http://localhost:12052/
>
> Server started on: 12052

## Installing the mod
1. Install [ResoniteModLoader](https://github.com/resonite-modding-group/ResoniteModLoader).
2. Place [IWantBabbelite.dll](https://github.com/noblereign/ResoniteIWantBabbelite/releases/latest/download/IWantBabbelite.dll) into your `rml_mods` folder. This folder should be at `C:\Program Files (x86)\Steam\steamapps\common\Resonite\rml_mods` for a default install. You can create it if it's missing, or if you launch the game once with ResoniteModLoader installed it will create this folder for you.
3. Place [Babbelite.Client.dll](https://github.com/noblereign/ResoniteIWantBabbelite/releases/latest/download/Babbelite.Client.dll) and [Babbelite.Shared.dll](https://github.com/noblereign/ResoniteIWantBabbelite/releases/latest/download/Babbelite.Shared.dll) into your `rml_libs` folder.
4. Start the game. If you want to verify that the mod is working you can check your Resonite logs.
