using System;
using System.IO;

namespace SPWare.VirtualSoundCanvas.Midi
{
    /// <summary>
    /// 곡의 설정(MidiRouter의 기록)을 파일 하나에 저장/읽는다. 앱을 끄고 다른 앱(또는 같은 앱)을 켜도 재생 중이던 곡/게임의 설정을 이어받게 하려는 것이다.
    /// 파일은 개인 버전과 공개 버전이 같이 쓴다(%AppData%\SPWare\LastMidiState.bin).
    /// 실기 MT-32가 전원을 켜 둔 동안 음색을 기억하는 것과 같은 효과다. 게임은 시작할 때 한 번만 음색을 올리기 때문이다.
    /// </summary>
    public static class MidiStateStore
    {
        public static string FilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SPWare", "LastMidiState.bin");

        public static byte[]? Load()
        {
            try { return File.Exists(FilePath) ? File.ReadAllBytes(FilePath) : null; }
            catch { return null; }
        }

        /// <summary>임시 파일에 쓴 뒤 바꿔치기해서, 쓰다가 꺼져도 반쯤 쓴 파일이 남지 않게 한다.</summary>
        public static void Save(byte[] data)
        {
            try
            {
                string dir = Path.GetDirectoryName(FilePath)!;
                Directory.CreateDirectory(dir);
                string tmp = FilePath + "." + Environment.ProcessId + ".tmp";
                File.WriteAllBytes(tmp, data);
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch { /* 저장 실패는 조용히 넘어간다(다음 기회에 다시 저장) */ }
        }
    }
}
