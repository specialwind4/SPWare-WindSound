using System;
using System.Collections.Generic;

namespace SPWare.VirtualSoundCanvas.Recording
{
    /// <summary>
    /// 반복되는 음악(게임 배경음악)이 "처음으로 돌아가 다시 연주되기 시작한 지점"을 찾는다.
    ///
    /// MIDI에는 "여기서 반복"이라는 표시가 없다. 그래서 노트 온(채널, 음, 세기)의 나열을 보고, 지금 들어오는 음들이
    /// 일정한 간격(P개 전, T밀리초 전)의 음들과 계속 똑같으면 반복으로 본다. 곡 안의 짧은 되풀이(같은 마디를 두 번 치는 것)를
    /// 반복으로 오해하지 않도록 조건을 엄하게 둔다:
    ///  - 주기가 충분히 길어야 한다(<see cref="MinPeriodMs"/>, <see cref="MinPeriodEvents"/>).
    ///  - 두 번째 바퀴가 처음부터 끝까지 똑같아야 하고, 세 번째 바퀴도 얼마간 똑같이 시작해야 한다
    ///    (A-A-B 꼴의 곡은 B가 시작되는 순간 어긋나므로 걸러진다).
    /// 그래서 확정은 두 바퀴가 지나고 조금 뒤에 난다. 찾으면 "첫 바퀴가 끝난 시각"(= 두 번째 바퀴의 첫 음 시각)을 돌려준다.
    /// </summary>
    public sealed class LoopDetector
    {
        public int MinPeriodMs = 15000;
        public int MinPeriodEvents = 32;
        public int ToleranceMs = 60;
        public int MaxHypotheses = 8000;

        public readonly struct Loop
        {
            public Loop(long cycleEndMs, int periodEvents, long periodMs) { CycleEndMs = cycleEndMs; PeriodEvents = periodEvents; PeriodMs = periodMs; }
            /// <summary>한 사이클이 끝나는 시각(두 번째 바퀴의 첫 음이 온 시각).</summary>
            public long CycleEndMs { get; }
            public int PeriodEvents { get; }
            public long PeriodMs { get; }
        }

        private readonly List<long> _ms = new();
        private readonly List<int> _keys = new();
        private readonly Dictionary<int, List<int>> _byKey = new();

        private struct Hyp { public int P; public int Start; public long T; }
        private readonly List<Hyp> _hyps = new();
        private readonly HashSet<int> _activeP = new();

        private int _followLag;   // 반복을 찾은 뒤: 같은 반복이 이어지는지 따라가는 간격(0이면 아직 못 찾음)

        public int Count => _keys.Count;

        public static int KeyOf(int channel, int note, int velocity) => (channel << 14) | (note << 7) | velocity;

        /// <summary>노트 온 하나를 넣는다. 이 음으로 반복이 확정되면 그 정보를 돌려준다(한 번만).</summary>
        public Loop? Add(long ms, int key)
        {
            int n = _keys.Count;
            _ms.Add(ms); _keys.Add(key);
            if (_followLag > 0) return null;

            Loop? found = null;
            for (int h = _hyps.Count - 1; h >= 0; h--)
            {
                var hy = _hyps[h];
                int j = n - hy.P;
                if (_keys[j] != key || Math.Abs((ms - _ms[j]) - hy.T) > ToleranceMs)
                {
                    _activeP.Remove(hy.P);
                    _hyps[h] = _hyps[_hyps.Count - 1];   // 순서는 상관없으므로 맨 뒤 것으로 메운다
                    _hyps.RemoveAt(_hyps.Count - 1);
                    continue;
                }
                int matched = n - hy.Start + 1;
                int need = hy.P + Math.Max(16, hy.P / 8);   // 두 번째 바퀴 전체 + 세 번째 바퀴의 시작
                if (matched >= need && (found is null || hy.P < found.Value.PeriodEvents))
                    found = new Loop(_ms[hy.Start], hy.P, hy.T);
            }

            if (found is null && _byKey.TryGetValue(key, out var same))
            {
                // 같은 음이 예전에 나온 자리마다 "거기서부터 반복이 시작됐다"는 가설을 세운다(대부분 다음 음에서 바로 깨진다)
                foreach (int i in same)
                {
                    long t = ms - _ms[i];
                    if (t < MinPeriodMs) break;            // 목록은 오래된 순서: 이 뒤로는 더 가깝다
                    int p = n - i;
                    if (p < MinPeriodEvents) break;
                    if (_hyps.Count >= MaxHypotheses) break;
                    if (_activeP.Add(p)) _hyps.Add(new Hyp { P = p, Start = n, T = t });
                }
            }

            if (!_byKey.TryGetValue(key, out var list)) _byKey[key] = list = new List<int>();
            list.Add(n);

            if (found is Loop f)
            {
                _followLag = f.PeriodEvents;
                _hyps.Clear(); _activeP.Clear();
            }
            return found;
        }

        /// <summary>
        /// 반복을 찾은 뒤에 부른다: 이 노트 온이 같은 반복의 다음 음이면 true(같은 곡이 계속 돌고 있다),
        /// 아니면 false(다른 곡이 시작됐다).
        /// </summary>
        public bool Follow(long ms, int key)
        {
            if (_followLag <= 0) return false;
            int n = _keys.Count;
            bool same = _keys[n - _followLag] == key;
            if (same) { _ms.Add(ms); _keys.Add(key); }
            return same;
        }

        /// <summary>지금까지 넣은 노트 온 가운데 <paramref name="untilMs"/> 이전 것들의 지문(같은 곡인지 견줄 때 쓴다).</summary>
        public ulong Fingerprint(long untilMs)
        {
            ulong h = 1469598103934665603UL;
            int n = 0;
            for (int i = 0; i < _keys.Count && _ms[i] < untilMs; i++)
            {
                h = (h ^ (uint)_keys[i]) * 1099511628211UL;
                n++;
            }
            return h ^ ((ulong)n << 40);
        }
    }
}
