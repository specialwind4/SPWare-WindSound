using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SPWare.VirtualSoundCanvas.Recording
{
    /// <summary>
    /// 표준 MIDI 파일(형식 0, 트랙 하나)을 쓴다. 시간 단위를 1틱 = 1밀리초로 맞춘다(4분음표 = 500틱, 템포 = 500,000마이크로초).
    /// 그래서 받은 시각을 그대로 틱으로 쓰면 원래 빠르기대로 재생된다.
    /// </summary>
    public static class SmfWriter
    {
        /// <param name="events">(밀리초, MIDI 바이트열) - 짧은 메시지는 상태 바이트 포함 2~3바이트, SysEx는 F0부터 F7까지 통째로. 시간 순서여야 한다.</param>
        /// <param name="endMs">트랙이 끝나는 시각(마지막 이벤트보다 앞이면 마지막 이벤트 시각으로 맞춘다).</param>
        public static void Write(string path, IReadOnlyList<(long ms, byte[] data)> events, long endMs, string trackName)
        {
            using var track = new MemoryStream();
            long last = 0;

            void Delta(long ms)
            {
                if (ms < last) ms = last;
                WriteVarLen(track, (uint)Math.Min(ms - last, 0x0FFFFFFF));
                last = ms;
            }

            // 트랙 이름
            var name = Encoding.ASCII.GetBytes(trackName);
            Delta(0); track.WriteByte(0xFF); track.WriteByte(0x03); WriteVarLen(track, (uint)name.Length); track.Write(name, 0, name.Length);
            // 템포: 4분음표 = 500,000마이크로초 (120 BPM)
            Delta(0); track.Write(new byte[] { 0xFF, 0x51, 0x03, 0x07, 0xA1, 0x20 }, 0, 6);

            foreach (var (ms, d) in events)
            {
                if (d.Length == 0) continue;
                byte status = d[0];
                if (status == 0xF0)
                {
                    // SysEx: F0 <길이> <F0 다음부터 F7까지>
                    Delta(ms);
                    track.WriteByte(0xF0);
                    WriteVarLen(track, (uint)(d.Length - 1));
                    track.Write(d, 1, d.Length - 1);
                }
                else if (status >= 0x80 && status < 0xF0)
                {
                    int need = (status & 0xF0) is 0xC0 or 0xD0 ? 2 : 3;
                    if (d.Length < need) continue;
                    Delta(ms);
                    track.Write(d, 0, need);   // 러닝 스테이터스는 쓰지 않는다(어느 재생기에서나 안전)
                }
                // 그 밖(실시간 메시지 등)은 파일에 넣지 않는다
            }

            Delta(Math.Max(endMs, last));
            track.Write(new byte[] { 0xFF, 0x2F, 0x00 }, 0, 3);

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var w = new BinaryWriter(fs);
            w.Write(Encoding.ASCII.GetBytes("MThd"));
            WriteBE32(w, 6);
            WriteBE16(w, 0);     // 형식 0
            WriteBE16(w, 1);     // 트랙 1개
            WriteBE16(w, 500);   // 4분음표당 500틱
            w.Write(Encoding.ASCII.GetBytes("MTrk"));
            WriteBE32(w, (uint)track.Length);
            w.Write(track.GetBuffer(), 0, (int)track.Length);
        }

        private static void WriteVarLen(Stream s, uint value)
        {
            Span<byte> buf = stackalloc byte[5];
            int n = 0;
            buf[n++] = (byte)(value & 0x7F);
            while ((value >>= 7) != 0) buf[n++] = (byte)((value & 0x7F) | 0x80);
            for (int i = n - 1; i >= 0; i--) s.WriteByte(buf[i]);
        }

        private static void WriteBE32(BinaryWriter w, uint v) { w.Write((byte)(v >> 24)); w.Write((byte)(v >> 16)); w.Write((byte)(v >> 8)); w.Write((byte)v); }
        private static void WriteBE16(BinaryWriter w, ushort v) { w.Write((byte)(v >> 8)); w.Write((byte)v); }
    }
}
