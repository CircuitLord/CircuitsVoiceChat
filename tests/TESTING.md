# VOIP replacement integration test

Run from the game directory:

```powershell
powershell -ExecutionPolicy Bypass -File modSrc\CircuitsVoiceChat\tests\run-integration.ps1
```

The runner refuses to start if the game or port 1757 is already in use. It launches
one hidden dedicated server and two hidden flycam clients with distinct offline user
IDs, then stops only those processes.

The test sends fifteen real 48 kHz Opus frames through `MessageType.VoiceChat` and
asserts:

- the server injects authenticated sender ID 2;
- frames at 49.9 m reach client 1;
- frames at 50.1 m never reach client 1;
- relay resumes after returning to 49.9 m;
- the receiver produces nonzero decoded PCM;
- a five-packet gap uses three PLC frames and one in-band FEC frame, then resumes;
- three duplicate sequence violations disconnect the sender;
- Concentus loads and passes its local encode/decode test in all three processes;
- the selected game channel is unreliable with a 30 ms maximum send interval.

All instrumentation is dormant unless `/voip_test_sender`, `/voip_test_receiver`, or
`/voip_test_range` is explicitly present on the process command line.

This test does not prove physical microphone capture or headset left/right output;
those require audio hardware. The codec, transport, relay, range boundary, jitter
recovery, identity, and validation paths do not require user interaction.
