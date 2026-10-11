using System;
using System.Text;
using UnityEngine;

namespace WandWarsMP.Net
{
    /// <summary>Little-endian growable write buffer. Reuse via Reset() to avoid per-message garbage.</summary>
    public sealed class NetWriter
    {
        public byte[] Data = new byte[256];
        public int Length;

        public void Reset() => Length = 0;

        private void Ensure(int extra)
        {
            if (Length + extra <= Data.Length) return;
            Array.Resize(ref Data, Math.Max(Data.Length * 2, Length + extra));
        }

        public void U8(byte v) { Ensure(1); Data[Length++] = v; }
        public void Bool(bool v) => U8(v ? (byte)1 : (byte)0);
        public void I8(sbyte v) => U8((byte)v);

        public void U16(ushort v) { Ensure(2); Data[Length++] = (byte)v; Data[Length++] = (byte)(v >> 8); }
        public void I16(short v) => U16((ushort)v);

        public void U32(uint v)
        {
            Ensure(4);
            Data[Length++] = (byte)v; Data[Length++] = (byte)(v >> 8);
            Data[Length++] = (byte)(v >> 16); Data[Length++] = (byte)(v >> 24);
        }
        public void I32(int v) => U32((uint)v);
        public void U64(ulong v) { U32((uint)v); U32((uint)(v >> 32)); }

        public unsafe void F32(float v) => U32(*(uint*)&v);

        public void Vec2(Vector2 v) { F32(v.x); F32(v.y); }

        /// <summary>Var-length unsigned int (7 bits per byte).</summary>
        public void VarU(uint v)
        {
            while (v >= 0x80) { U8((byte)(v | 0x80)); v >>= 7; }
            U8((byte)v);
        }

        public void Str(string s)
        {
            if (s == null) { VarU(0); return; }
            byte[] b = Encoding.UTF8.GetBytes(s);
            VarU((uint)b.Length + 1);
            Bytes(b, 0, b.Length);
        }

        public void Bytes(byte[] src, int offset, int count)
        {
            Ensure(count);
            Buffer.BlockCopy(src, offset, Data, Length, count);
            Length += count;
        }

        public byte[] ToArray()
        {
            var r = new byte[Length];
            Buffer.BlockCopy(Data, 0, r, 0, Length);
            return r;
        }
    }

    /// <summary>Reader over a byte segment. Throws on overrun (callers drop the malformed message).</summary>
    public struct NetReader
    {
        private readonly byte[] data;
        private readonly int end;
        public int Pos;

        public NetReader(byte[] data, int offset, int count)
        {
            this.data = data;
            Pos = offset;
            end = offset + count;
        }

        public int Remaining => end - Pos;

        private void Need(int n)
        {
            if (Pos + n > end) throw new IndexOutOfRangeException("NetReader overrun");
        }

        public byte U8() { Need(1); return data[Pos++]; }
        public bool Bool() => U8() != 0;
        public sbyte I8() => (sbyte)U8();

        public ushort U16() { Need(2); ushort v = (ushort)(data[Pos] | (data[Pos + 1] << 8)); Pos += 2; return v; }
        public short I16() => (short)U16();

        public uint U32()
        {
            Need(4);
            uint v = (uint)(data[Pos] | (data[Pos + 1] << 8) | (data[Pos + 2] << 16) | (data[Pos + 3] << 24));
            Pos += 4;
            return v;
        }
        public int I32() => (int)U32();
        public ulong U64() { ulong lo = U32(); ulong hi = U32(); return lo | (hi << 32); }

        public unsafe float F32() { uint v = U32(); return *(float*)&v; }

        public Vector2 Vec2() => new Vector2(F32(), F32());

        public uint VarU()
        {
            uint v = 0; int shift = 0;
            while (true)
            {
                byte b = U8();
                v |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return v;
                shift += 7;
                if (shift > 28) throw new FormatException("VarU too long");
            }
        }

        public string Str()
        {
            uint n = VarU();
            if (n == 0) return null;
            int len = (int)n - 1;
            Need(len);
            string s = Encoding.UTF8.GetString(data, Pos, len);
            Pos += len;
            return s;
        }
    }
}
