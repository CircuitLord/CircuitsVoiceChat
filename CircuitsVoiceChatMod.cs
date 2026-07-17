using MelonLoader;

[assembly: MelonInfo(typeof(CircuitsVoiceChat.CircuitsVoiceChatMod), "Circuit's Voice Chat", "1.0.4", "CircuitLord")]
[assembly: MelonGame("Alta", "A Township Tale")]

namespace CircuitsVoiceChat
{
    public sealed class CircuitsVoiceChatMod : MelonMod
    {
        private VoiceSettingsGui settingsGui;

        public override void OnInitializeMelon()
        {
            VoiceRuntime.Initialize(HarmonyInstance);
            if (!ApplicationManager.IsHeadless)
                settingsGui = new VoiceSettingsGui();
            MelonLogger.Msg("Initialized; Vivox is disabled and game-socket VOIP is active");
        }

        public override void OnUpdate() { settingsGui?.Update(); VoiceRuntime.Update(); }
        public override void OnGUI() => settingsGui?.OnGui();
        public override void OnApplicationQuit() => VoiceRuntime.Shutdown();
    }
}
