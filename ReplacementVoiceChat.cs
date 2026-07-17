using System;
using System.Collections.Generic;
using Alta.Voice;
using UnityEngine;

namespace CircuitsVoiceChat
{
    internal sealed class ReplacementVoiceChat : IVoiceChat
    {
        private const string MutedPlayersKey = "MutedPlayersKey";
        private readonly HashSet<int> muted = new HashSet<int>();

        internal ReplacementVoiceChat()
        {
            string json = PlayerPrefs.GetString(MutedPlayersKey, "");
            try
            {
                List<int> saved = JsonUtility.FromJson<List<int>>(json);
                if (saved != null)
                    foreach (int id in saved) muted.Add(id);
            }
            catch (Exception exception)
            {
                MelonLoader.MelonLogger.Error(exception.ToString());
            }
        }

        public VoiceChatScope AudibleScope { get; private set; } = VoiceChatScope.Everyone;

        public void CycleAudibleScope()
        {
            VoiceChatScope next = AudibleScope - 1;
            AudibleScope = next < VoiceChatScope.Noone ? VoiceChatScope.Everyone : next;
            VoiceRuntime.ClearMutedAudio();
        }

        public void SetPlayerMuted(int player, bool isMuted)
        {
            if (isMuted) muted.Add(player); else muted.Remove(player);
            PlayerPrefs.SetString(MutedPlayersKey, JsonUtility.ToJson(new List<int>(muted)));
            VoiceRuntime.ClearMutedAudio();
        }

        public bool GetPlayerMuted(int player) => muted.Contains(player);

        internal bool IsAudible(int userId)
        {
            if (AudibleScope == VoiceChatScope.Noone || muted.Contains(userId))
                return false;
            if (AudibleScope == VoiceChatScope.Friends)
                return Player.Current != null && Player.Current.FriendshipManager.IsFriendsWith(userId);
            return true;
        }
    }
}
