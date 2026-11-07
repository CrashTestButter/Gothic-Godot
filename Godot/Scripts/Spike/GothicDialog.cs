using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using GothicGodot.Audio;
using ZenKit;
using ZenKit.Daedalus;

namespace GothicGodot.Spike
{
    /// <summary>
    /// Gothic's dialogs on the AI's Daedalus VM, following Gothic-Unity's DialogService: the NPC's C_INFO entries
    /// (important ones start by themselves, the others are offered when their condition is true and they weren't told
    /// or are permanent), info functions with self = the NPC and other = the hero, AI_Output lines with voice
    /// (speech WAV) and subtitle (OU.BIN), Info_AddChoice sub choices, AI_StopProcessInfos to end.
    /// </summary>
    public class GothicDialog
    {
        public class Option
        {
            public string Text;
            public InfoInstance Info;
            public int Function;
        }

        public class Line
        {
            public GothicNpc Speaker;
            public bool IsHero;
            public string Name;
            public string Text;
            public AudioStreamWav Voice;
        }

        private const float _secondsPerCharacter = 0.07f;
        private const float _processInfosDistance = 5f;

        private readonly DaedalusVm _vm;
        private readonly Vfs _vfs;
        private readonly GothicAi _ai;
        private readonly CutsceneLibrary _subtitles;
        private readonly Dictionary<int, List<InfoInstance>> _infosByNpc = new();
        private readonly HashSet<int> _told = new();
        private readonly List<Option> _choices = new();
        private readonly Queue<Line> _lines = new();
        private readonly List<string> _gestures = new();
        private readonly Random _random = new(7);
        private Line _line;
        private double _lineTime;
        private double _lineDuration;
        private bool _isStopRequested;

        public bool IsActive { get; private set; }
        public GothicNpc Npc { get; private set; }
        public List<Option> Options { get; } = new();
        public bool IsShowingOptions => IsActive && _line == null && _lines.Count == 0 && Options.Count > 0;
        public Line Current => _line;
        public int InfoCount { get; }
        public bool HasSubtitles => _subtitles != null;

        /// <summary>Lines end after at most this many seconds (0: after the voice); the scripted test speeds up.</summary>
        public float MaxLineSeconds { get; set; }

        public event Action<Line> LineStarted;
        public event Action Ended;

        public GothicDialog(DaedalusVm vm, Vfs vfs, GothicAi ai, string dataPath)
        {
            _vm = vm;
            _vfs = vfs;
            _ai = ai;

            foreach (var symbol in vm.GetInstanceSymbols("C_INFO"))
            {
                var info = vm.InitInstance<InfoInstance>(symbol);
                if (info == null)
                    continue;
                InfoCount++;
                if (!_infosByNpc.TryGetValue(info.Npc, out var list))
                    _infosByNpc[info.Npc] = list = new List<InfoInstance>();
                list.Add(info);
            }

            foreach (var list in _infosByNpc.Values)
                list.Sort((a, b) => a.Nr.CompareTo(b.Nr));

            _subtitles = LoadSubtitles(vfs, dataPath);

            for (var i = 1; i < 40; i++)
            {
                var gesture = $"T_DIALOGGESTURE_{i:00}";
                if (ai.HasClip(gesture))
                    _gestures.Add(gesture);
            }

            RegisterExternals();
        }

        public bool HasInfos(GothicNpc npc) => npc?.Instance != null && _infosByNpc.ContainsKey(npc.Instance.Index);

        /// <summary>The infos the NPC would offer or start now (conditions are script calls).</summary>
        public int CountAvailable(GothicNpc npc) => GetAvailable(npc).Count();

        /// <summary>The hero talks to the NPC (or the NPC to the hero with AI_ProcessInfos).</summary>
        public bool Start(GothicNpc npc)
        {
            if (IsActive || !HasInfos(npc))
                return false;

            IsActive = true;
            Npc = npc;
            npc.IsInDialog = true;
            npc.Actions.Clear();
            npc.Current = null;
            _isStopRequested = false;
            _choices.Clear();
            FaceHero(npc);
            GD.Print($"[Dialog] talking to {npc.Name} ({npc.Instance.Index})");

            var important = GetAvailable(npc).FirstOrDefault(i => i.Important == 1);
            if (important != null)
                CallInfo(important);
            else
                ShowOptions();

            return IsActive;
        }

        public void Choose(int index)
        {
            if (!IsShowingOptions || index < 0 || index >= Options.Count)
                return;

            var option = Options[index];
            Options.Clear();
            GD.Print($"[Dialog] hero chooses \"{option.Text}\"");
            if (option.Info != null)
                CallInfo(option.Info);
            else
                CallFunction(Npc, option.Function);
        }

        /// <summary>Skips the current line (voice and subtitle).</summary>
        public void Skip()
        {
            if (_line != null)
                _lineTime = _lineDuration;
        }

        public void Update(double delta)
        {
            if (!IsActive)
                return;

            if (_line != null)
            {
                _lineTime += delta;
                if (_line.Speaker != null && !_line.IsHero)
                    KeepGesturing(_line.Speaker);
                if (_lineTime < _lineDuration)
                    return;
                if (_line.Speaker != null)
                    _ai.PlayClip(_line.Speaker, _line.Speaker.IdleClip, true);
                _line = null;
            }

            if (_lines.Count > 0)
            {
                StartLine(_lines.Dequeue());
                return;
            }

            if (Options.Count > 0)
                return;

            // All lines of the info are spoken: end, the choices of the info, or the NPC's menu again.
            if (_isStopRequested)
                End();
            else
                ShowOptions();
        }

        public void End()
        {
            if (!IsActive)
                return;
            GD.Print($"[Dialog] {Npc?.Name} ends the dialog");
            if (Npc != null)
            {
                Npc.IsInDialog = false;
                _ai.PlayClip(Npc, Npc.IdleClip, true);
            }

            IsActive = false;
            Npc = null;
            Options.Clear();
            _choices.Clear();
            _lines.Clear();
            _line = null;
            Ended?.Invoke();
        }

        private IEnumerable<InfoInstance> GetAvailable(GothicNpc npc)
        {
            if (npc?.Instance == null || !_infosByNpc.TryGetValue(npc.Instance.Index, out var infos))
                yield break;

            foreach (var info in infos)
            {
                if (info.Permanent == 0 && _told.Contains(info.Index))
                    continue;
                if (info.Condition > 0 && CallInt(npc, info.Condition) == 0)
                    continue;
                yield return info;
            }
        }

        private void ShowOptions()
        {
            Options.Clear();
            if (_choices.Count > 0)
            {
                Options.AddRange(_choices);
                return;
            }

            foreach (var info in GetAvailable(Npc))
            {
                // An important info that became true while talking starts by itself, like in the engine.
                if (info.Important == 1)
                {
                    CallInfo(info);
                    return;
                }

                Options.Add(new Option { Text = info.Description, Info = info });
            }

            if (Options.Count == 0)
                End();
        }

        private void CallInfo(InfoInstance info)
        {
            _told.Add(info.Index);
            CallFunction(Npc, info.Information);
        }

        private void CallFunction(GothicNpc npc, int function)
        {
            if (function <= 0)
                return;
            var self = _vm.GlobalSelf;
            var other = _vm.GlobalOther;
            _vm.GlobalSelf = npc.Instance;
            _vm.GlobalOther = _ai.HeroInstance;
            try
            {
                _vm.Call(function);
            }
            catch (Exception e)
            {
                GD.PushWarning($"[Dialog] info function failed: {e.Message}");
            }
            finally
            {
                _vm.GlobalSelf = self;
                _vm.GlobalOther = other;
            }
        }

        /// <summary>A condition: may run inside another script call (Npc_CheckInfo), so self and other are restored.</summary>
        private int CallInt(GothicNpc npc, int function)
        {
            var self = _vm.GlobalSelf;
            var other = _vm.GlobalOther;
            _vm.GlobalSelf = npc.Instance;
            _vm.GlobalOther = _ai.HeroInstance;
            try
            {
                return _vm.Call<int>(function);
            }
            catch (Exception)
            {
                return 0;
            }
            finally
            {
                _vm.GlobalSelf = self;
                _vm.GlobalOther = other;
            }
        }

        private void StartLine(Line line)
        {
            _line = line;
            _lineTime = 0;
            _lineDuration = line.Voice?.GetLength() ?? Math.Max(1.5, (line.Text?.Length ?? 0) * _secondsPerCharacter);
            if (MaxLineSeconds > 0)
                _lineDuration = Math.Min(_lineDuration, MaxLineSeconds);
            if (line.Speaker != null && !line.IsHero)
                PlayGesture(line.Speaker);
            GD.Print($"[Dialog] {(line.IsHero ? "Hero" : line.Speaker?.Name)}: \"{line.Text}\" ({line.Name}, " +
                     (line.Voice != null ? $"voice {line.Voice.GetLength():F1} s)" : "no voice)"));
            LineStarted?.Invoke(line);
        }

        private void PlayGesture(GothicNpc npc)
        {
            if (_gestures.Count > 0)
                _ai.PlayClip(npc, _gestures[_random.Next(_gestures.Count)], false);
        }

        /// <summary>A long line gets the next gesture when the last one is over.</summary>
        private void KeepGesturing(GothicNpc npc)
        {
            var clip = npc.Clip;
            if (clip != null && !npc.IsClipLooping && npc.ClipTime * clip.Fps >= clip.FrameCount - 1)
                PlayGesture(npc);
        }

        private void FaceHero(GothicNpc npc)
        {
            var toHero = _ai.HeroPosition - npc.Position;
            toHero.Y = 0;
            if (toHero.LengthSquared() > 0.01f)
                npc.FaceTowards(toHero);
            _ai.PlayClip(npc, npc.IdleClip, true);
        }

        /// <summary>OU.BIN (or OU.CSL) from _work/DATA/scripts/content/CUTSCENE in any case, else from the VFS.</summary>
        private static CutsceneLibrary LoadSubtitles(Vfs vfs, string dataPath)
        {
            foreach (var name in new[] { "OU.BIN", "OU.CSL" })
            {
                try
                {
                    // From disk first: the native library of binary-dependencies has no VFS loader for it.
                    var path = FindIgnoringCase(dataPath, "_work", "DATA", "scripts", "content", "CUTSCENE", name);
                    if (path != null)
                        return new CutsceneLibrary(path);
                    if (vfs.Find(name) != null)
                        return new CutsceneLibrary(vfs, name);
                }
                catch (Exception e)
                {
                    GD.PushWarning($"[Dialog] {name} can't be read: {e.Message}");
                }
            }

            GD.PushWarning("[Dialog] OU.BIN/CSL not found: no subtitles");
            return null;
        }

        private static string FindIgnoringCase(string root, params string[] parts)
        {
            var path = root;
            foreach (var part in parts)
            {
                if (!System.IO.Directory.Exists(path))
                    return null;
                path = System.IO.Directory.EnumerateFileSystemEntries(path)
                    .FirstOrDefault(e => string.Equals(System.IO.Path.GetFileName(e), part, StringComparison.OrdinalIgnoreCase));
                if (path == null)
                    return null;
            }

            return path;
        }

        private string GetText(string name)
        {
            if (_subtitles == null)
                return name;
            try
            {
                return _subtitles.GetBlock(name)?.Message?.Text ?? name;
            }
            catch (Exception)
            {
                return name;
            }
        }

        private void RegisterExternals()
        {
            _vm.RegisterExternal("AI_Output", (DaedalusVm.ExternalFuncV<NpcInstance, NpcInstance, string>)((self, target, name) =>
            {
                // Only the dialog's two speakers: other NPCs' lines (Vatras preaching, ambient talk) belong to their
                // own states, not to this dialog.
                var isHero = self != null && self.Index == _ai.HeroInstance?.Index;
                if (!IsActive || !isHero && self?.Index != Npc?.Instance.Index)
                    return;
                _lines.Enqueue(new Line
                {
                    Speaker = isHero ? Npc : self?.UserData as GothicNpc,
                    IsHero = isHero,
                    Name = name,
                    Text = GetText(name),
                    Voice = GothicSound.Load(_vfs, name)
                });
            }));
            // Like the engine: the newest choice is listed first (scripts add "back" before the others).
            _vm.RegisterExternal("Info_AddChoice", (DaedalusVm.ExternalFuncV<int, string, int>)((info, text, function) =>
                _choices.Insert(0, new Option { Text = text, Function = function })));
            _vm.RegisterExternal("Info_ClearChoices", (DaedalusVm.ExternalFuncV<int>)(_ => _choices.Clear()));
            _vm.RegisterExternal("AI_StopProcessInfos", (DaedalusVm.ExternalFuncV<NpcInstance>)(_ =>
            {
                if (IsActive)
                    _isStopRequested = true;
            }));
            _vm.RegisterExternal("AI_ProcessInfos", (DaedalusVm.ExternalFuncV<NpcInstance>)(npc =>
            {
                // The NPC wants to talk (important info): start it when the hero is near.
                if (npc?.UserData is GothicNpc target && target.IsActive &&
                    target.Position.DistanceTo(_ai.HeroPosition) < _processInfosDistance)
                    Callable.From(() => Start(target)).CallDeferred();
            }));
            _vm.RegisterExternal("Npc_KnowsInfo", (DaedalusVm.ExternalFunc<int, NpcInstance, int>)((npc, info) =>
                _told.Contains(info) ? 1 : 0));
            _vm.RegisterExternal("Npc_CheckInfo", (DaedalusVm.ExternalFunc<int, NpcInstance, int>)((npc, important) =>
                npc?.UserData is GothicNpc target && GetAvailable(target).Any(i => i.Important == important) ? 1 : 0));
            _vm.RegisterExternal("InfoManager_HasFinished", (DaedalusVm.ExternalFunc<int>)(() => IsActive ? 0 : 1));
            _vm.RegisterExternal("Hlp_GetInstanceID", (DaedalusVm.ExternalFunc<int, DaedalusInstance>)(instance =>
                instance?.Index ?? -1));
            _vm.RegisterExternal("Npc_IsPlayer", (DaedalusVm.ExternalFunc<int, NpcInstance>)(npc =>
                npc != null && npc.Index == _ai.HeroInstance?.Index ? 1 : 0));
            _vm.RegisterExternal("Hlp_StrCmp", (DaedalusVm.ExternalFunc<int, string, string>)((a, b) =>
                string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ? 1 : 0));
            _vm.RegisterExternal("Npc_GetTrueGuild", (DaedalusVm.ExternalFunc<int, NpcInstance>)(npc => npc?.Guild ?? 0));
        }
    }
}
