using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using AssetsTools.NET.Extra;
using DG.Tweening;
using HarmonyLib;
using UnityEngine;
using UnityModManagerNet;
using FMOD;
using Fmod5Sharp;
using Fmod5Sharp.FmodTypes;
using Microsoft.Win32;
using UnityEngine.Experimental.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Debug = System.Diagnostics.Debug;
using Object = UnityEngine.Object;

namespace ADOFAI_fmod;

public class StartAct : MonoBehaviour
{
    public Action act;

    private void Update()
    {
        if (act == null) return;
        act?.Invoke();
        act = null;
    }
}

public class Dummy : MonoBehaviour
{
}

public class Main
{
    private static Harmony harmony;
    private static FMOD.System fmodsys;
    private static UnityModManager.ModEntry entry;
    private static Dictionary<int, Channel> channels = new();
    private static Dictionary<int, AudioSource> idToAudioSource = new();
    private static Dictionary<int, Channel> playOneShotChannels = new();

    private static Dictionary<int, float> volCache = new();

    private static Dictionary<int, float> positionCache = new();
    private static uint bufferSize = 64;
    private static Dictionary<string, Sound> cache = new();
    private static Dictionary<string, Sound> staticCache = new();

    private static ChannelGroup general;
    private static ChannelGroup nonpause;


    [DllImport("kernel32", SetLastError = true)]
    static extern IntPtr LoadLibraryW([MarshalAs(UnmanagedType.LPWStr)] string lpFileName);

    public static ulong GetDspClock()
    {
        ulong dspClock;
        //fmodsys.getMasterChannelGroup(out var master);
        general.getDSPClock(out dspClock, out _);
        return dspClock;
    }

    public static double GetDspTime()
    {
        return GetDspClock() / 48000d;
    }


    public static bool useASIO = false;


    public static int selectedDriver = 0;

    public static bool curUseAsio = false;

    public static Sound MakeSoundFromAudioClip(AudioClip audioclip)
    {
        if (cache.TryGetValue(audioclip.name, out var a))
        {
            return a;
        }

        if (audioclip.loadType != AudioClipLoadType.DecompressOnLoad &&
            staticCache.TryGetValue(audioclip.name, out var b))
        {
            return b;
        }

        float[] samples = new float[audioclip.samples * audioclip.channels];
        audioclip.GetData(samples, 0);

        uint lenbytes = (uint)Buffer.ByteLength(samples);
        entry.Logger.Log("Length: " + lenbytes);


        CREATESOUNDEXINFO soundinfo = new CREATESOUNDEXINFO();
        soundinfo.cbsize = Marshal.SizeOf(typeof(CREATESOUNDEXINFO));
        soundinfo.length = lenbytes;
        soundinfo.format = SOUND_FORMAT.PCMFLOAT;
        soundinfo.defaultfrequency = audioclip.frequency;
        soundinfo.numchannels = audioclip.channels;

        RESULT result;
        Sound sound;
        result = fmodsys.createSound("abc", MODE.OPENUSER, ref soundinfo, out sound);

        IntPtr ptr1, ptr2;
        uint len1, len2;
        result = sound.@lock(0, lenbytes, out ptr1, out ptr2, out len1, out len2);
        Marshal.Copy(samples, 0, ptr1, (int)(len1 / sizeof(float)));
        if (len2 > 0)
        {
            Marshal.Copy(samples, (int)(len1 / sizeof(float)), ptr2, (int)(len2 / sizeof(float)));
        }

        result = sound.unlock(ptr1, ptr2, len1, len2);
        result = sound.setMode(MODE.LOOP_NORMAL);
        //entry.Logger.Log("RESULT => " + result);
        cache.Add(audioclip.name, sound);

        return sound;
    }

    static IEnumerator Updater()
    {
        while (true)
        {
            fmodsys.update();

            yield return null;
        }
    }

    static void Collect()
    {
        var dq = new Queue<int>();
        foreach (var i in idToAudioSource)
        {
            if (i.Value) continue;
            dq.Enqueue(i.Key);
            if (channels.ContainsKey(i.Key))
            {
                channels[i.Key].stop();
                channels.Remove(i.Key);
            }

            if (playOneShotChannels.ContainsKey(i.Key))
            {
                playOneShotChannels[i.Key].stop();
                playOneShotChannels.Remove(i.Key);
            }
        }

        while (dq.Count > 0)
        {
            var e = dq.Dequeue();
            idToAudioSource.Remove(e);
            volCache.Remove(e);
            positionCache.Remove(e);
        }
    }

    static IEnumerator Collector()
    {
        while (true)
        {
            Collect();
            yield return null;
        }
    }


    public enum CompressionFormat
    {
        PCM,
        Vorbis,
        ADPCM,
        MP3,
        VAG,
        HEVAG,
        XMA,
        AAC,
        GCADPCM,
        ATRAC9
    }


    private static Dictionary<string, FmodSample> internalAudioFiles = new();

    public static bool InitFmod()
    {
        if (fmodsys.init(4093, FMOD.INITFLAGS.NORMAL, System.IntPtr.Zero) != FMOD.RESULT.OK)
        {
            entry.Logger.Error("Failed to initialize FMOD system");
            return false;
        }

        if (fmodsys.createChannelGroup(null, out general) != RESULT.OK)
        {
            entry.Logger.Error("Failed to make general Channel Group");
            return false;
        }

        if (fmodsys.createChannelGroup(null, out nonpause) != RESULT.OK)
        {
            entry.Logger.Error("Failed to make nonpause Channel Group");
            return false;
        }

        foreach (var i in internalAudioFiles)
        {
            i.Value.RebuildAsStandardFileFormat(out var data, out _);
            CREATESOUNDEXINFO soundinfo = new CREATESOUNDEXINFO();
            soundinfo.cbsize = Marshal.SizeOf(typeof(CREATESOUNDEXINFO));
            soundinfo.length = (uint)data.Length;

            var result = fmodsys.createSound(data, MODE.CREATESAMPLE | MODE.OPENMEMORY, ref soundinfo, out var sound);
            if (result == RESULT.OK)
            {
                entry.Logger.Log("Loaded Internal Sound: " + i.Key);
                staticCache.Add(i.Key, sound);
            }
            else
            {
                entry.Logger.Error("Failed to load internal sound: " + i.Key);
            }
        }

        return true;
    }

    static Texture2D logo;
    private static Texture2D logoHighRes;


    public static void LoadInternalAudioFiles()
    {
        entry.Logger.Log("Loading Internal Audio Files From Assets");
        var manager = new AssetsManager();
        manager.LoadClassPackage(Path.Combine(entry.Path, "classdata.tpk"));
        foreach (var file in new DirectoryInfo(Application.dataPath).GetFiles("*.assets"))
        {
            var fileInst = manager.LoadAssetsFile(file.FullName, false);
            var realfile = fileInst.file;
            manager.LoadClassDatabaseFromPackage(realfile.Metadata.UnityVersion);
            foreach (var clip in realfile.GetAssetsOfType(AssetClassID.AudioClip))
            {
                var clipBase = manager.GetBaseField(fileInst, clip);
                if (clipBase["m_LoadType"].AsInt == 0) continue;
                entry.Logger.Log("file name: " + clipBase["m_Name"].AsString);
                var resource = clipBase["m_Resource"];
                var res = new StreamReader(Path.Combine(Application.dataPath, resource["m_Source"].AsString));

                var buffer = new byte[resource["m_Size"].AsULong];
                entry.Logger.Log("File Offset: " + resource["m_Offset"].AsInt);
                res.BaseStream.Seek(resource["m_Offset"].AsLong, SeekOrigin.Begin);
                res.BaseStream.Read(buffer, 0, (int)resource["m_Size"].AsULong);

                FmodSoundBank bank = FsbLoader.LoadFsbFromByteArray(buffer);


                internalAudioFiles.Add(clipBase["m_Name"].AsString, bank.Samples[0]);

                res.Close();
            }
        }
    }

    public static GameObject bufsizeindicator;

    public static bool Init(UnityModManager.ModEntry modEntry)
    {
        modEntry.OnSaveGUI = _ =>
        {
            PlayerPrefs.SetInt("useASIO", useASIO ? 1 : 0);
            PlayerPrefs.SetInt("selectedDriver", selectedDriver);
        };
        useASIO = PlayerPrefs.GetInt("useASIO", 0) == 1;
        selectedDriver = PlayerPrefs.GetInt("selectedDriver", 0);
        curUseAsio = useASIO;
        AudioListener.volume = 0;
        AudioListener.pause = true;
        var dllpath = Path.Combine(modEntry.Path, "fmod.dll");
        modEntry.Logger.Log(dllpath);
        var handle = LoadLibraryW(dllpath);
        if (handle == IntPtr.Zero)
        {
            throw new Exception("fmod.dll Handle Is Null!");
        }

        dllpath = Path.Combine(modEntry.Path, "fmodL.dll");
        modEntry.Logger.Log(dllpath);
        handle = LoadLibraryW(dllpath);
        if (handle == IntPtr.Zero)
        {
            throw new Exception("fmodL.dll Handle Is Null!");
        }

        entry = modEntry;


        Factory.System_Create(out fmodsys);
        if (fmodsys.setSoftwareFormat(48000, FMOD.SPEAKERMODE.DEFAULT, 0) != FMOD.RESULT.OK)
        {
            modEntry.Logger.Error("Failed to set FMOD software format");
            return false;
        }

        if (fmodsys.setSoftwareChannels(65536 * 8) != FMOD.RESULT.OK)
        {
            modEntry.Logger.Error("Failed to set FMOD software channels");
            return false;
        }

        if (fmodsys.setDSPBufferSize(bufferSize, 2) != FMOD.RESULT.OK)
        {
            modEntry.Logger.Error("Failed to set FMOD DSP buffer size");
            return false;
        }

        if (useASIO)
        {
            if (fmodsys.setOutput(OUTPUTTYPE.ASIO) != FMOD.RESULT.OK)
            {
                modEntry.Logger.Error("Failed to set output device");
                return false;
            }
        }

        fmodsys.setDriver(selectedDriver);

        LoadInternalAudioFiles();
        if (!InitFmod()) return false;


        harmony = new Harmony(modEntry.Info.Id);
        harmony.PatchAll(Assembly.GetExecutingAssembly());

        logo = new Texture2D(1, 1);
        logo.LoadImage(Convert.FromBase64String(
            "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"));
        logoHighRes = new Texture2D(1, 1);
        logoHighRes.LoadImage(Convert.FromBase64String(
            "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"));


        SceneManager.sceneUnloaded += (a) =>
        {
            if (a.name is "scnGame" or "scnEditor" or "scnLevelSelect")
            {
                foreach (var aa in cache)
                {
                    aa.Value.release();
                }

                cache.Clear();
            }
        };
        SceneManager.sceneLoaded += (_, _) =>
        {
            Collect();
            foreach (var ads in Object.FindObjectsByType<AudioSource>(FindObjectsSortMode.None))
            {
                if (ads.playOnAwake)
                {
                    var sa = new GameObject().AddComponent<StartAct>();
                    sa.act = () => { ads.Play(); };
                }
            }

            var sa1 = new GameObject().AddComponent<StartAct>();
            sa1.act = () =>
            {
                if (scrConductor.instance) scrConductor.instance.song2.volume = 0;
            };
        };

        entry.OnGUI = _ =>
        {
            GUILayout.Label(logo);
            GUILayout.Label("Made using FMOD by Firelight Technologies Pty Ltd.");

            var values = new List<string>();

            fmodsys.getNumDrivers(out var num);
            for (int i = 0; i < num; i++)
            {
                fmodsys.getDriverInfo(i, out var name, 1000, out var _, out var _, out var _, out var _);
                values.Add(name);
            }

            if (UnityModManager.UI.PopupToggleGroup(ref selectedDriver, values.ToArray()))
            {
                if (!scrController.instance.paused)
                    scrController.instance.TogglePauseGame();

                fmodsys.setDriver(selectedDriver);
            }


            useASIO = GUILayout.Toggle(useASIO, "Use Asio");

            if (curUseAsio != useASIO)
            {
                GUILayout.Label("Restart the game to apply the changes.");
            }
        };

        var mgr = new GameObject();
        Object.DontDestroyOnLoad(mgr);
        var dum = mgr.AddComponent<Dummy>();
        dum.StartCoroutine(Updater());
        dum.StartCoroutine(Collector());

        var canvas = new GameObject();
        Object.DontDestroyOnLoad(canvas);
        var cv = canvas.AddComponent<Canvas>();
        cv.sortingOrder = 32767;
        cv.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvas.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;

        var logoObj2 = new GameObject();
        logoObj2.transform.SetParent(canvas.transform);
        logoObj2.transform.localScale = Vector2.one;
        var rt = logoObj2.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0, 1);
        rt.anchorMax = new Vector2(0, 1);
        rt.sizeDelta = new Vector2(logo.width / (float)logo.height * 10, 10);
        rt.pivot = new Vector2(0, 1);
        rt.anchoredPosition = new Vector2(0, 0);
        var logoTex2 = logoObj2.AddComponent<RawImage>();
        logoTex2.texture = logo;
        logoTex2.color = new Color(1, 1, 1, 0.5f);
        var logoOut = logoObj2.AddComponent<Shadow>();
        logoOut.effectColor = Color.black;
        logoOut.effectDistance = new Vector2(1, -1);
        bufsizeindicator = new GameObject();
        bufsizeindicator.transform.SetParent(canvas.transform);
        bufsizeindicator.transform.localScale = Vector2.one;
        var rt2 = bufsizeindicator.AddComponent<RectTransform>();
        rt2.anchorMin = new Vector2(0, 1);
        rt2.anchorMax = new Vector2(0, 1);

        rt2.pivot = new Vector2(0, 1);


        var bufsizeindicatorText = bufsizeindicator.AddComponent<Text>();
        bufsizeindicatorText.text = bufferSize.ToString();


        bufsizeindicatorText.color = new Color(1, 1, 1, 0.5f);
        bufsizeindicatorText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        bufsizeindicatorText.fontSize = 10;
        var io = bufsizeindicator.AddComponent<Outline>();
        io.effectColor = Color.black;
        io.effectDistance = new Vector2(1, -1);

        rt2.anchoredPosition = new Vector2(0, -rt.sizeDelta.y);

        return true;
    }


    public static void SetVolume(float volume)
    {
        fmodsys.getMasterChannelGroup(out var mastergroup);
        mastergroup.setVolume(volume);
    }

    public static float GetVolume()
    {
        fmodsys.getMasterChannelGroup(out var mastergroup);
        mastergroup.getVolume(out var volume);
        return volume;
    }

    [HarmonyPatch(typeof(AudioListener), "volume", MethodType.Getter)]
    public static class AudioListener_Volume_Getter
    {
        // Patch With Transpiler
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>();
            codes.Add(new CodeInstruction(OpCodes.Call, typeof(Main).GetMethod("GetVolume")));
            codes.Add(new CodeInstruction(OpCodes.Ret));
            return codes;
        }
    }

    [HarmonyPatch(typeof(AudioListener), "volume", MethodType.Setter)]
    public static class AudioListener_Volume_Setter
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>();
            codes.Add(new CodeInstruction(OpCodes.Ldarg_0));
            codes.Add(new CodeInstruction(OpCodes.Call, typeof(Main).GetMethod("SetVolume")));
            codes.Add(new CodeInstruction(OpCodes.Ret));
            return codes;
        }
    }

    public static void pa(AudioSource __instance, AudioClip value)
    {
        if (value == null)
        {
            if (channels.ContainsKey(__instance.GetInstanceID()))
            {
                channels[__instance.GetInstanceID()].stop();
                channels.Remove(__instance.GetInstanceID());
            }

            return;
        }

        idToAudioSource[__instance.GetInstanceID()] = __instance;

        if (channels.ContainsKey(__instance.GetInstanceID()))
        {
            channels[__instance.GetInstanceID()].stop();
            channels.Remove(__instance.GetInstanceID());
        }

        Sound sound = MakeSoundFromAudioClip(value);
        Channel channel;

        fmodsys.playSound(sound, __instance.ignoreListenerPause ? nonpause : general, true, out channel);
        channels.Add(__instance.GetInstanceID(), channel);
    }


    [HarmonyPatch(typeof(AudioSource), "Play", new Type[] { })]
    public static class AudioSource_Play
    {
        public static bool Prefix(AudioSource __instance)
        {
            pa(__instance, __instance.clip);
            if (channels.ContainsKey(__instance.GetInstanceID()))
            {
                var chnl = channels[__instance.GetInstanceID()];
                ulong dspClock;
                chnl.getDSPClock(out _, out dspClock);
                Sound snd;
                chnl.getCurrentSound(out snd);
                uint length;
                snd.getLength(out length, TIMEUNIT.PCM);

                channels[__instance.GetInstanceID()].getCurrentSound(out var sound);
                sound.getDefaults(out var freq, out _);
                chnl.setPosition(
                    positionCache.ContainsKey(__instance.GetInstanceID())
                        ? (uint)(positionCache[__instance.GetInstanceID()] * freq)
                        : 0, TIMEUNIT.PCM);
                chnl.setDelay(dspClock, dspClock + (uint)(length / __instance.pitch / (double)freq * 48000));
                chnl.setLoopCount(__instance.loop ? -1 : 0);
                chnl.setPitch(__instance.pitch);

                float vole = 1;

                if (AudioManager.Instance.Mixer.GetFloat(
                        __instance.outputAudioMixerGroup?.name.Replace("Conductor", "").Replace("Parent", "") +
                        "Volume",
                        out var vole_))
                {
                    vole = InverseFunction(vole_);
                }


                chnl.setVolume(__instance.volume * vole);
                chnl.setPriority(__instance.priority);


                chnl.setPaused(false);
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(AudioSource), "pitch", MethodType.Setter)]
    public static class AudioSource_pitch_setter
    {
        public static void Prefix(AudioSource __instance, float value)
        {
            if (channels.ContainsKey(__instance.GetInstanceID()))
            {
                channels[__instance.GetInstanceID()].setPitch(value);
            }
        }
    }

    public static void SetAudioSourceVolume(AudioSource __instance, float vol)
    {
        volCache[__instance.GetInstanceID()] = vol;
        if (channels.ContainsKey(__instance.GetInstanceID()))
        {
            float vole = 1;

            if (AudioManager.Instance.Mixer.GetFloat(
                    __instance.outputAudioMixerGroup?.name.Replace("Conductor", "").Replace("Parent", "") +
                    "Volume",
                    out var vole_))
            {
                vole = InverseFunction(vole_);
            }


            channels[__instance.GetInstanceID()].setVolume(vol * vole);
        }
    }

    public static float GetAudioSourceVolume(AudioSource __instance)
    {
        return volCache.ContainsKey(__instance.GetInstanceID()) ? volCache[__instance.GetInstanceID()] : 1;
    }

    [HarmonyPatch(typeof(AudioSource), "volume", MethodType.Setter)]
    public static class AudioSource_volume_setter
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>();
            codes.Add(new CodeInstruction(OpCodes.Ldarg_0));
            codes.Add(new CodeInstruction(OpCodes.Ldarg_1));
            codes.Add(new CodeInstruction(OpCodes.Call, typeof(Main).GetMethod("SetAudioSourceVolume")));
            codes.Add(new CodeInstruction(OpCodes.Ret));
            return codes;
        }
    }

    [HarmonyPatch(typeof(AudioSource), "volume", MethodType.Getter)]
    public static class AudioSource_volume_getter
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>();
            codes.Add(new CodeInstruction(OpCodes.Ldarg_0));
            codes.Add(new CodeInstruction(OpCodes.Call, typeof(Main).GetMethod("GetAudioSourceVolume")));
            codes.Add(new CodeInstruction(OpCodes.Ret));
            return codes;
        }
    }


    [HarmonyPatch(typeof(AudioSource), "PlayOneShot", typeof(AudioClip))]
    public static class AudioSource_PlayOneShot
    {
        public static bool Prefix(AudioSource __instance, AudioClip clip)
        {
            Sound sound = MakeSoundFromAudioClip(clip);
            Channel chnl;
            fmodsys.playSound(sound, __instance.ignoreListenerPause ? nonpause : general, true, out chnl);
            playOneShotChannels.Add(__instance.GetInstanceID(), chnl);
            ulong dspClock;
            chnl.getDSPClock(out _, out dspClock);
            Sound snd;
            chnl.getCurrentSound(out snd);
            uint length;
            snd.getLength(out length, TIMEUNIT.PCM);
            sound.getDefaults(out var freq, out _);
            chnl.setPosition(
                positionCache.ContainsKey(__instance.GetInstanceID())
                    ? (uint)(positionCache[__instance.GetInstanceID()] * freq)
                    : 0, TIMEUNIT.PCM);
            chnl.setDelay(dspClock, dspClock + (uint)(length / __instance.pitch / (double)freq * 48000));
            chnl.setLoopCount(0);
            chnl.setPitch(__instance.pitch);
            float vole = 1;


            if (AudioManager.Instance.Mixer.GetFloat(
                    __instance.outputAudioMixerGroup?.name.Replace("Conductor", "").Replace("Parent", "") +
                    "Volume",
                    out var vole_))
            {
                vole = InverseFunction(vole_);
            }


            chnl.setVolume(__instance.volume * vole);

            chnl.setPriority(__instance.priority);


            chnl.setPaused(false);

            return false;
        }
    }

    public static float InverseFunction(float num)
    {
        if (Mathf.Approximately(num, -80f))
        {
            return 0f; // num이 -80일 경우 value는 0입니다.
        }

        if (num > 0f)
        {
            return (num / 10f) + 1f;
        }

        return (num / 20f) + 1f; // 역함수: value = (num / 20) + 1
    }

    [HarmonyPatch(typeof(AudioSource), "PlayScheduled")]
    public static class AudioSource_PlayScheduled
    {
        public static bool Prefix(AudioSource __instance, double time)
        {
            pa(__instance, __instance.clip);
            if (channels.ContainsKey(__instance.GetInstanceID()))
            {
                try
                {
                    var chnl = channels[__instance.GetInstanceID()];
                    Sound snd;
                    chnl.getCurrentSound(out snd);
                    uint length;
                    snd.getLength(out length, TIMEUNIT.PCM);
                    channels[__instance.GetInstanceID()].getCurrentSound(out var sound);
                    sound.getDefaults(out var freq, out _);
                    chnl.setPosition(
                        positionCache.ContainsKey(__instance.GetInstanceID())
                            ? (uint)(positionCache[__instance.GetInstanceID()] * freq)
                            : 0, TIMEUNIT.PCM);

                    var t = (ulong)(time * 48000);


                    chnl.setDelay(t,
                        t + (uint)(length / __instance.pitch / (double)freq * 48000));
                    chnl.setLoopCount(__instance.loop ? -1 : 0);
                    chnl.setPitch(__instance.pitch);
                    float vole = 1;


                    if (AudioManager.Instance.Mixer.GetFloat(
                            __instance.outputAudioMixerGroup?.name.Replace("Conductor", "").Replace("Parent", "") +
                            "Volume",
                            out var vole_))
                    {
                        vole = InverseFunction(vole_);
                    }


                    chnl.setVolume(__instance.volume * vole);
                    chnl.setPriority(__instance.priority);

                    chnl.setPaused(false);
                }
                catch (Exception ex)
                {
                    entry.Logger.LogException(ex);
                }
            }

            return false;
        }
    }

    public static void SetScheduledEndTime(AudioSource __instance, double time)
    {
        if (channels.ContainsKey(__instance.GetInstanceID()))
        {
            var chnl = channels[__instance.GetInstanceID()];
            ulong start;
            chnl.getDelay(out start, out _);
            chnl.setDelay(start, (ulong)(time * 48000));
        }
    }

    public static void SetScheduledStartTime(AudioSource __instance, double time)
    {
        if (channels.ContainsKey(__instance.GetInstanceID()))
        {
            var chnl = channels[__instance.GetInstanceID()];
            ulong end;
            chnl.getDelay(out _, out end);
            chnl.setDelay((ulong)(time * 48000), end);
        }
    }

    [HarmonyPatch(typeof(AudioSource), "SetScheduledEndTime")]
    public static class AudioSource_SetScheduledEndTime
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>();
            codes.Add(new CodeInstruction(OpCodes.Ldarg_0));
            codes.Add(new CodeInstruction(OpCodes.Ldarg_1));
            codes.Add(new CodeInstruction(OpCodes.Call, typeof(Main).GetMethod("SetScheduledEndTime")));
            codes.Add(new CodeInstruction(OpCodes.Ret));
            return codes;
        }
    }

    [HarmonyPatch(typeof(AudioSource), "SetScheduledStartTime")]
    public static class AudioSource_SetScheduledStartTime
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>();
            codes.Add(new CodeInstruction(OpCodes.Ldarg_0));
            codes.Add(new CodeInstruction(OpCodes.Ldarg_1));
            codes.Add(new CodeInstruction(OpCodes.Call, typeof(Main).GetMethod("SetScheduledStartTime")));
            codes.Add(new CodeInstruction(OpCodes.Ret));
            return codes;
        }
    }

    [HarmonyPatch(typeof(AudioSource), "Stop", new Type[] { })]
    public static class AudioSource_Stop
    {
        public static bool Prefix(AudioSource __instance)
        {
            if (channels.ContainsKey(__instance.GetInstanceID()))
            {
                channels[__instance.GetInstanceID()].stop();
                channels.Remove(__instance.GetInstanceID());
            }

            return false;
        }
    }

    public static void Pause(AudioSource __instance)
    {
        if (channels.ContainsKey(__instance.GetInstanceID()))
        {
            channels[__instance.GetInstanceID()].setPaused(true);
        }
    }

    public static void UnPause(AudioSource __instance)
    {
        if (channels.ContainsKey(__instance.GetInstanceID()))
        {
            channels[__instance.GetInstanceID()].setPaused(false);
        }
    }

    [HarmonyPatch(typeof(AudioSource), "Pause", new Type[] { })]
    public static class AudioSource_Pause
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>();
            codes.Add(new CodeInstruction(OpCodes.Ldarg_0));
            codes.Add(new CodeInstruction(OpCodes.Call, typeof(Main).GetMethod("Pause")));
            codes.Add(new CodeInstruction(OpCodes.Ret));
            return codes;
        }
    }

    [HarmonyPatch(typeof(AudioSource), "UnPause", new Type[] { })]
    public static class AudioSource_UnPause
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>();
            codes.Add(new CodeInstruction(OpCodes.Ldarg_0));
            codes.Add(new CodeInstruction(OpCodes.Call, typeof(Main).GetMethod("UnPause")));
            codes.Add(new CodeInstruction(OpCodes.Ret));
            return codes;
        }
    }

    [HarmonyPatch(typeof(AudioSettings), "dspTime", MethodType.Getter)]
    public static class AudioSettings_dspTime
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>();
            codes.Add(new CodeInstruction(OpCodes.Call, typeof(Main).GetMethod("GetDspTime")));
            codes.Add(new CodeInstruction(OpCodes.Ret));
            return codes;
        }
    }

    public static float GetTime(AudioSource __instance)
    {
        if (channels.ContainsKey(__instance.GetInstanceID()))
        {
            channels[__instance.GetInstanceID()].getPosition(out var pos, TIMEUNIT.PCM);
            var result = channels[__instance.GetInstanceID()].getCurrentSound(out var sound);
            sound.getDefaults(out var freq, out _);
            return pos / freq;
        }

        return positionCache.ContainsKey(__instance.GetInstanceID()) ? positionCache[__instance.GetInstanceID()] : 0;
    }

    public static void SetTime(AudioSource __instance, float time)
    {
        if (channels.ContainsKey(__instance.GetInstanceID()))
        {
            channels[__instance.GetInstanceID()].getCurrentSound(out var sound);
            sound.getDefaults(out var freq, out _);
            channels[__instance.GetInstanceID()].setPosition((uint)(time * freq), TIMEUNIT.PCM);
        }

        positionCache[__instance.GetInstanceID()] = time;
    }


    [HarmonyPatch(typeof(AudioSource), "time", MethodType.Getter)]
    public static class AudioSource_time_getter
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>();
            codes.Add(new CodeInstruction(OpCodes.Ldarg_0));
            codes.Add(new CodeInstruction(OpCodes.Call, typeof(Main).GetMethod("GetTime")));
            codes.Add(new CodeInstruction(OpCodes.Ret));
            return codes;
        }
    }

    [HarmonyPatch(typeof(AudioSource), "time", MethodType.Setter)]
    public static class AudioSource_time_setter
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>();
            codes.Add(new CodeInstruction(OpCodes.Ldarg_0));
            codes.Add(new CodeInstruction(OpCodes.Ldarg_1));
            codes.Add(new CodeInstruction(OpCodes.Call, typeof(Main).GetMethod("SetTime")));
            codes.Add(new CodeInstruction(OpCodes.Ret));
            return codes;
        }
    }

    public static bool IsPlaying(AudioSource __instance)
    {
        if (channels.ContainsKey(__instance.GetInstanceID()))
        {
            bool isPlaying;
            channels[__instance.GetInstanceID()].isPlaying(out isPlaying);
            return isPlaying;
        }

        return false;
    }

    [HarmonyPatch(typeof(AudioSource), "isPlaying", MethodType.Getter)]
    public static class AudioSource_isPlaying
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>();
            codes.Add(new CodeInstruction(OpCodes.Ldarg_0));
            codes.Add(new CodeInstruction(OpCodes.Call, typeof(Main).GetMethod("IsPlaying")));
            codes.Add(new CodeInstruction(OpCodes.Ret));
            return codes;
        }
    }


    //private static bool pausedAll;

    public static void SetPausedAll(bool paused)
    {
        //pausedAll = paused;
        /*foreach (var i in channels)
        {
            i.Value.setPaused(paused || i.Value.paused);
        }*/
        /*fmodsys.getMasterChannelGroup(out var master);
        master.setPaused(paused);*/
        general.setPaused(paused);
    }

    public static bool GetPausedAll()
    {
        general.getPaused(out var paused);
        return paused;
    }

    [HarmonyPatch(typeof(AudioListener), "pause", MethodType.Getter)]
    public static class AudioListener_pause_getter
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>();
            codes.Add(new CodeInstruction(OpCodes.Call, typeof(Main).GetMethod("GetPausedAll")));
            codes.Add(new CodeInstruction(OpCodes.Ret));
            return codes;
        }
    }

    [HarmonyPatch(typeof(AudioListener), "pause", MethodType.Setter)]
    public static class AudioListener_pause_setter
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>();
            codes.Add(new CodeInstruction(OpCodes.Ldarg_0));
            codes.Add(new CodeInstruction(OpCodes.Call, typeof(Main).GetMethod("SetPausedAll")));
            codes.Add(new CodeInstruction(OpCodes.Ret));
            return codes;
        }
    }


    [HarmonyPatch(typeof(AudioSettings), "GetConfiguration")]
    public static class AudioSettings_GetConfiguration
    {
        public static bool Prefix(ref AudioConfiguration __result)
        {
            var n = new AudioConfiguration();
            n.sampleRate = 48000;
            n.dspBufferSize = (int)bufferSize;
            n.speakerMode = AudioSettings.speakerMode;
            n.numRealVoices = 4093;
            n.numVirtualVoices = 65536 * 8;
            __result = n;
            return false;
        }
    }

    [HarmonyPatch(typeof(AudioSettings), "Reset")]
    public static class AudioSettings_Reset
    {
        public static bool Prefix(AudioConfiguration config, ref bool __result)
        {
            if (config.dspBufferSize == 0)
            {
                config.dspBufferSize = 64;
            }

            bufferSize = (uint)config.dspBufferSize;
            bufsizeindicator.GetComponent<Text>().text = bufferSize.ToString();

            fmodsys.close();

            cache.Clear();
            staticCache.Clear();
            channels.Clear();
            idToAudioSource.Clear();
            volCache.Clear();

            if (fmodsys.setDSPBufferSize(bufferSize, 2) != FMOD.RESULT.OK)
            {
                entry.Logger.Error("Failed to set FMOD DSP buffer size");
                return false;
            }

            if (!InitFmod()) return false;

            __result = true;
            return false;
        }
    }

    private static AudioClip CreateFakeAudioClip(string name, float frequency, float duration)
    {
        int sampleRate = 44100; // 표준 샘플링 주파수
        int sampleCount = (int)(sampleRate * duration);
        AudioClip clip = AudioClip.Create(name, sampleCount, 1, sampleRate, false);

        float[] samples = new float[sampleCount];


        clip.SetData(samples, 0);
        return clip;
    }


    [HarmonyPatch(typeof(AudioManager), "FindOrLoadAudioClipExternal")]
    public static class AudioManager_FindOrLoadAudioClipExternal
    {
        public static void Prefix(string path)
        {
            entry.Logger.Log("Loading FMOD Sound: " + path);
            var cn = Path.GetFileName(path) + "*external";
            if (cache.ContainsKey(cn)) return;

            if (fmodsys.createSound(path, path.EndsWith(".mp3") ? MODE.CREATESAMPLE : MODE.CREATESTREAM,
                    out var sound) ==
                FMOD.RESULT.OK)
            {
                sound.getLength(out var length, TIMEUNIT.MS);
                AudioManager.Instance.audioLib.Add(cn, CreateFakeAudioClip(cn, 440, length / 1000f));
                cache.Add(cn, sound);
            }
            else
            {
                entry.Logger.Error("Failed to load external sound: " + path);
            }
        }
    }

    [HarmonyPatch(typeof(scrConductor), "GetCurrentAudioOutputType")]
    public static class scrConductor_GetCurrentAudioOutputType
    {
        public static bool Prefix(ref AudioOutputType __result)
        {
            __result = AudioOutputType.Other;
            return false;
        }
    }

    [HarmonyPatch(typeof(scrConductor), "GetCurrentAudioOutputName")]
    public static class scrConductor_GetCurrentAudioOutputName
    {
        public static bool Prefix(ref string __result)
        {
            fmodsys.getDriverInfo(selectedDriver, out var drivername, 1000, out _, out _, out _, out _);
            __result = drivername + " with FMod";
            return false;
        }
    }

    [HarmonyPatch(typeof(AudioManager), "FlushData")]
    public static class AudioManager_FlushData
    {
        public static void Prefix()
        {
            foreach (var aa in cache)
            {
                aa.Value.release();
            }

            cache.Clear();
        }
    }

    [HarmonyPatch(typeof(RDUtils), "SetMixerParameter")]
    public static class RDUtils_SetMixerParameter
    {
        public static void Postfix()
        {
            foreach (var ads in idToAudioSource.Values)
            {
                ads.volume = ads.volume;
            }
        }
    }

    private static void RealGoToMenu()
    {
        UnityEngine.Debug.Log("Go to Menu");
        var splash = Object.FindObjectOfType<scnSplash>();
        splash.fade.DOFade(1f, splash.fadeDuration).SetUpdate(true).SetEase(splash.fadeEase)
            .OnComplete(delegate { ADOBase.GoToLevelSelect(); });
    }

    private static IEnumerator FmodWarn()
    {
        var splash = Object.FindObjectOfType<scnSplash>();

        var logoObj = new GameObject();
        logoObj.transform.SetParent(splash.alphaWarning.transform.parent);
        logoObj.transform.localPosition = Vector2.zero;
        logoObj.transform.localScale = new Vector2(logo.width / (float)logo.height * 1, 1);
        var logoTex = logoObj.AddComponent<RawImage>();
        logoTex.texture = logoHighRes;
        logoTex.color = Color.clear;

        splash.alphaWarning.enabled = true;
        splash.alphaWarning.text =
            "\n\n\n\nSound Engine: FMOD by Firelight Technologies Pty Ltd.\n(Added by ADOFAI Fmod)";
        splash.alphaWarning.color = Color.clear;
        splash.alphaWarning.DOColor(Color.white, 0.5f);
        logoTex.DOColor(Color.white, 0.5f);
        yield return new WaitForSeconds(0.5f);
        float startTime = Time.unscaledTime;
        while (Time.unscaledTime < startTime + 3)
        {
            yield return null;
        }

        splash.alphaWarning.DOColor(Color.clear, 0.5f);
        logoTex.DOColor(Color.clear, 0.5f);
        yield return new WaitForSeconds(0.5f);


        RealGoToMenu();
        Object.Destroy(logoHighRes);
    }

    [HarmonyPatch(typeof(scnSplash), "GoToMenu")]
    public static class scnSplash_GoToMenu
    {
        public static bool Prefix(scnSplash __instance)
        {
            __instance.StartCoroutine(FmodWarn());
            return false;
        }
    }
}