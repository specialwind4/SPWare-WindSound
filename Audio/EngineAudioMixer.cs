using System;
using System.Collections.Generic;
using NAudio.Wave;
using SPWare.VirtualSoundCanvas.Engines;

namespace SPWare.VirtualSoundCanvas.Audio
{
    /// <summary>
    /// 등록된 모든 엔진(MTS3, MTS5 등)의 RenderFloat를 매 오디오 콜백마다 불러서
    /// 하나의 스테레오 스트림으로 합쳐주는 믹서. 이걸 실제 스피커로 내보내는 부분이
    /// 지금까지 빠져 있었던 조각입니다 — 엔진이 MIDI를 받아 내부적으로 소리를 "계산"은
    /// 하고 있었지만, 그 결과를 밖으로 꺼내 재생하는 루프가 없으면 아무 소리도 안 납니다.
    ///
    /// ExternalForwardingEngine처럼 RenderFloat가 no-op인 엔진을 섞어 넣어도 안전합니다
    /// (그냥 무음으로 취급됩니다 — 그 엔진은 자기 프로세스가 알아서 소리를 냅니다).
    /// </summary>
    public sealed class EngineAudioMixer : ISampleProvider
    {
        private readonly List<ISynthEngine> _engines = new();
        private readonly Dictionary<ISynthEngine, float> _gains = new();
        private readonly Dictionary<ISynthEngine, float> _baseGains = new();
        private float[] _scratch = new float[0];

        public WaveFormat WaveFormat { get; }

        /// <summary>마스터 볼륨 (0.0 ~ 1.0). 오디오 스레드에서 읽으므로 volatile 대신 float 필드로 둡니다.</summary>
        public float MasterVolume { get; set; } = 0.8f;

        /// <summary>[진단용] 마스터/클리퍼를 거치기 전 합산 신호의 피크. 1.0을 넘으면 헤드룸 부족.</summary>
        public float PeakBeforeClip;

        /// <summary>[진단용] 마지막으로 ResetPeakHold()를 부른 뒤로 관측된 최대 피크(피크홀드).
        /// 오디오 콜백은 몇 ms 단위로 자주 도는데 UI는 훨씬 뜸하게 폴링하므로, 순간값만
        /// 읽으면 곡의 진짜 최고점(코드가 겹치는 찰나)을 놓치기 쉽다.</summary>
        public float PeakHoldMax;

        /// <summary>[진단용] ResetPeakHold() 이후 tanh 입력이 |x|>1(선형 구간을 벗어난 압축 시작점)
        /// 이었던 샘플 수 / 전체 샘플 수.</summary>
        public float CompressedRatio => _totalSamplesSinceReset > 0
            ? (float)_compressedSamplesSinceReset / _totalSamplesSinceReset
            : 0f;

        private long _compressedSamplesSinceReset;
        private long _totalSamplesSinceReset;

        /// <summary>피크홀드/압축비율 통계를 초기화합니다. UI가 표시 직후 호출하세요.</summary>
        public void ResetPeakHold()
        {
            PeakHoldMax = 0f;
            _compressedSamplesSinceReset = 0;
            _totalSamplesSinceReset = 0;
        }

        /// <summary>[진단용] 엔진별 렌더 출력 피크(게인 적용 전). 인덱스는 AddEngine 순서.
        /// 오디오 스레드가 쓰고 UI 스레드가 읽으므로 Dictionary가 아니라 배열로 둡니다
        /// (float 원소 읽기/쓰기는 원자적이라 진단용으로 충분).</summary>
        public readonly float[] EnginePeaks = new float[8];

        public EngineAudioMixer(int sampleRate = 44100)
        {
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
        }

        /// <summary>
        /// 엔진을 등록한다. <paramref name="baseGain"/>은 엔진마다 소리 크기를 맞추는 고정 보정값(선형 배율, 1.0 = 그대로)이고,
        /// 사용자가 만지는 음량 슬라이더(<see cref="SetEngineVolume"/>, 0~1)와는 별개로 곱해진다.
        /// 엔진마다 기본 출력 크기가 많이 달라서(같은 피아노 코드 기준 MTS3가 MTS5보다 약 6.4 dB 큼) 슬라이더를
        /// 같은 값에 두면 엔진을 바꿀 때 크기가 널뛰던 것을 막기 위한 것. 값은 STATUS.md §2.64 참고.
        /// </summary>
        public void AddEngine(ISynthEngine engine, float baseGain = 1.0f)
        {
            _engines.Add(engine);
            _gains[engine] = 1.0f;
            _baseGains[engine] = baseGain;
        }

        /// <summary>엔진별 개별 볼륨 (0.0 ~ 1.0). 두 음원의 음량 밸런스를 맞출 때 씁니다.</summary>
        public void SetEngineVolume(ISynthEngine engine, float gain)
        {
            if (_gains.ContainsKey(engine)) _gains[engine] = Math.Clamp(gain, 0f, 1f);
        }

        /// <summary>NAudio가 오디오 디바이스 버퍼를 채울 때마다 호출합니다.</summary>
        public int Read(float[] buffer, int offset, int count)
        {
            // 주의: NAudio의 SampleToWaveProvider는 byte[]를 float[]처럼 위장한 버퍼(WaveBuffer)를 넘겨줍니다.
            // 이런 버퍼에는 Array.Clear/Array.Copy를 쓰면 실제 원소 크기(1바이트)로 동작해서
            // 버퍼의 1/4만 지워지고 나머지엔 직전 콜백의 소리가 남아 다시 섞입니다(10ms마다 나는 글리치의 원인).
            // 그래서 반드시 요소 단위 루프로 지워야 합니다.
            for (int i = 0; i < count; i++) buffer[offset + i] = 0f;
            if (_engines.Count == 0) return count;

            if (_scratch.Length < count) _scratch = new float[count];
            int frames = count / 2; // 스테레오이므로 샘플 수 / 2 = 프레임 수

            for (int e = 0; e < _engines.Count; e++)
            {
                var engine = _engines[e];
                float gain = _gains.TryGetValue(engine, out var g) ? g : 1.0f;
                if (gain <= 0f) continue; // 볼륨 0이면 렌더링 자체를 건너뜁니다
                gain *= _baseGains.TryGetValue(engine, out var bg) ? bg : 1.0f;

                // 엔진마다 새로 비워줍니다. RenderFloat가 버퍼를 안 채우는 엔진
                // (ExternalForwardingEngine처럼 자기 소리를 내지 않는 엔진)이 있으면
                // 직전 엔진의 소리가 _scratch에 남아 두 번 합산되고, 그만큼 진폭이
                // 커져 SoftClip에 걸려 찌그러집니다.
                Array.Clear(_scratch, 0, count);

                try
                {
                    engine.RenderFloat(_scratch, frames);
                }
                catch
                {
                    continue; // 한 엔진이 죽어도 나머지 소리는 계속 나오게
                }
                float enginePeak = 0f;
                for (int i = 0; i < count; i++)
                {
                    float v = _scratch[i];
                    float a = v < 0 ? -v : v;
                    if (a > enginePeak) enginePeak = a;
                    buffer[offset + i] += v * gain;
                }
                if (e < EnginePeaks.Length) EnginePeaks[e] = enginePeak;
            }

            float sumPeak = 0f;
            for (int i = 0; i < count; i++)
            {
                float a = buffer[offset + i];
                if (a < 0) a = -a;
                if (a > sumPeak) sumPeak = a;
            }
            PeakBeforeClip = sumPeak;
            if (sumPeak > PeakHoldMax) PeakHoldMax = sumPeak;

            // 마스터 볼륨을 적용하고, 여러 엔진 소리가 겹쳐 0dB를 넘으면 부드럽게 눌러줍니다.
            float master = Math.Clamp(MasterVolume, 0f, 1f);
            long compressed = 0;
            for (int i = 0; i < count; i++)
            {
                float pre = buffer[offset + i] * master;
                if (pre > 1f || pre < -1f) compressed++;
                buffer[offset + i] = SoftClip(pre);
            }
            _compressedSamplesSinceReset += compressed;
            _totalSamplesSinceReset += count;

            return count;
        }

        /// <summary>
        /// 0dB를 넘는 신호를 부드럽게 눌러주는 소프트 클리퍼.
        ///
        /// 예전 구현은 |x|가 1을 넘는 순간 출력이 1.0에서 0.5로 뚝 떨어지는
        /// **불연속** 함수였다(1f - 1f/(x+1f)는 x=1에서 0.5다). 그래서 신호가
        /// ±1.0을 넘나들 때마다 파형에 0.5짜리 계단이 생겼고, 그게 음이 빽빽한
        /// 곡에서 "지직거리는" 잡음으로 들렸다. 조용한 구간은 1.0을 안 넘어서
        /// 멀쩡했기 때문에 단음 테스트로는 재현되지 않았다.
        ///
        /// tanh는 연속이고 기울기도 매끄러우며 출력이 ±1을 넘지 않는다.
        /// </summary>
        private static float SoftClip(float x) => MathF.Tanh(x);
    }
}
