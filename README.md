# Circuit's Voice Chat

A MelonLoader mod for A Township Tale that replaces the game's Vivox voice chat with a custom Opus-based implementation running over the game's own network socket. No third-party voice service, no extra ports: voice packets travel on the same kcp2k connection the game already uses.

## Features

- Working 3D proximity voice chat on custom servers, replaces the game's Vivox dependency
- Includes Automatic Gain Control to automatically balance player volumes
- Support for some in-game features, like mouth movement when talking
- Supports self-muting, and muting other players (untested) + moderator mute (untested)

## Installation

Requires [MelonLoader](https://melonwiki.xyz/).

1. Download the latest `CircuitsVoiceChat-vX.X.X.zip` from [Releases](../../releases) and extract it.
2. Copy the `CircuitsVoiceChat` dll into the game's `Mods` folder.
3. Copy `Concentus.dll` into the game's `UserLibs` folder.

Both the client and the server need the mod installed. Vivox is disabled entirely on load.

Building the project does both steps automatically (see below).

## Usage

- Voice works like the vanilla game.
- Press **F5** in-game to open the settings window: microphone selection, receive volume, and connection status.
- Selected microphone and output gain persist in `PlayerPrefs`.

## How it works

- **Client (send)**: `VoiceCapture` pulls mic audio through winmm `waveIn` (48 kHz mono, independent of Unity/FMOD and safe under the game's old Mono runtime). `VoiceEncoder` applies an RMS gate with hysteresis and hangover, AGC toward a target speech level, a peak limiter, then Opus-encodes 20 ms frames at 24 kbps. Frames go to the server over a dedicated `MessageType.VoiceChat` channel on the game's kcp2k socket.
- **Server**: `VoiceRelay` stamps the sender id and rebroadcasts each packet to all other connections, with token-bucket rate limiting per sender.
- **Client (receive)**: `RemoteVoice` reorders packets in a jitter buffer with adaptive target latency, decodes with Opus packet loss concealment for missing frames, and feeds PCM into a Wwise Audio Input voice (`Play_ModVoice`) positioned at the speaking player's head.
- `KcpSocketPatch` also fixes the Windows UDP `WSAECONNRESET` spam in kcp2k.

## Building

```
dotnet build
```

The csproj expects the game at `C:\Games\Alta\A Township Tale` (edit `GamePath` if different). A post-build step copies the mod DLL to `Mods` and `Concentus.dll` to `UserLibs`. The Wwise soundbank (`wwise/ModAudio`) is embedded into the mod DLL, so no loose bank files are needed.

## Testing

See `tests/TESTING.md` and `tests/run-integration.ps1` for the integration test harness (`/voip_test_*` command line flags drive a headless sender/receiver loop).

## Credits and third-party libraries

- **[Concentus](https://github.com/lostromb/concentus)** by Logan Stromberg. A pure C# port of the Opus codec, used for all encoding and decoding. Licensed under the Opus BSD 3-clause license. `Concentus.dll` is redistributed with this mod.
- **[Opus](https://opus-codec.org/)** by Xiph.Org Foundation, Skype Limited, Octasic, CSIRO and contributors. The codec Concentus is ported from. BSD 3-clause license.
- **[MelonLoader](https://melonwiki.xyz/)** and **[Harmony](https://github.com/pardeike/Harmony)**. Mod loader and runtime patching, not redistributed by this mod.
- **[Wwise](https://www.audiokinetic.com/)** by Audiokinetic. Playback uses the game's existing Wwise engine and its Audio Input source plugin via a soundbank built with the Wwise authoring tool.

This mod itself is licensed under the [MIT License](LICENSE).

Full license texts for redistributed components are in [`THIRD-PARTY-LICENSES.txt`](THIRD-PARTY-LICENSES.txt).
