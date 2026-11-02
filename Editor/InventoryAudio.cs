using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace ProjectInventory.Editor
{
    /// <summary>Thin reflection wrapper around Unity's internal AudioUtil preview player.</summary>
    internal static class InventoryAudio
    {
        static readonly Type audioUtil = typeof(AudioImporter).Assembly.GetType("UnityEditor.AudioUtil");

        static MethodInfo Find(string name, params Type[] args) =>
            audioUtil?.GetMethod(name, BindingFlags.Static | BindingFlags.Public, null, args, null);

        public static void Play(AudioClip clip)
        {
            if (clip == null) return;
            Stop();
            var m = Find("PlayPreviewClip", typeof(AudioClip), typeof(int), typeof(bool));
            if (m != null) { m.Invoke(null, new object[] { clip, 0, false }); return; }
            m = Find("PlayClip", typeof(AudioClip), typeof(int), typeof(bool));
            m?.Invoke(null, new object[] { clip, 0, false });
        }

        public static void Stop()
        {
            var m = Find("StopAllPreviewClips") ?? Find("StopAllClips");
            m?.Invoke(null, null);
        }

        public static bool IsPlaying()
        {
            var m = Find("IsPreviewClipPlaying");
            return m != null && (bool)m.Invoke(null, null);
        }
    }
}
