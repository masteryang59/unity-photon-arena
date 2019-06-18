using System;
using System.Text;

namespace PhotonArena.Core.Net.Wire
{
    /// <summary>Little-endian binary writer used by the relay protocol and the game messages.</summary>
    public sealed class ByteWriter
    {
        byte[] _buffer;
        int _length;

        public ByteWriter(int capacity = 64)
        {
            _buffer = new byte[Math.Max(16, capacity)];
        }

        public int Length => _length;

        public byte[] ToArray()
        {
            var result = new byte[_length];
            Buffer.BlockCopy(_buffer, 0, result, 0, _length);
            return result;
        }

        void Ensure(int extra)
        {
            if (_length + extra <= _buffer.Length) return;
            int size = _buffer.Length * 2;
            while (size < _length + extra) size *= 2;
            Array.Resize(ref _buffer, size);
        }

        public ByteWriter U8(byte v)
        {
            Ensure(1);
            _buffer[_length++] = v;
            return this;
        }

        public ByteWriter Bool(bool v) => U8(v ? (byte)1 : (byte)0);

        public ByteWriter I16(short v) => U16(unchecked((ushort)v));

        public ByteWriter U16(ushort v)
        {
            Ensure(2);
            _buffer[_length++] = (byte)v;
            _buffer[_length++] = (byte)(v >> 8);
            return this;
        }

        public ByteWriter I32(int v)
        {
            Ensure(4);
            _buffer[_length++] = (byte)v;
            _buffer[_length++] = (byte)(v >> 8);
            _buffer[_length++] = (byte)(v >> 16);
            _buffer[_length++] = (byte)(v >> 24);
            return this;
        }

        public ByteWriter F32(float v) => I32(BitConverter.SingleToInt32Bits(v));

        /// <summary>Unsigned LEB128, for lengths and counts.</summary>
        public ByteWriter VarU(uint v)
        {
            while (v >= 0x80)
            {
                U8((byte)(v | 0x80));
                v >>= 7;
            }
            return U8((byte)v);
        }

        public ByteWriter Str(string s)
        {
            if (s == null)
            {
                VarU(0);
                return this;
            }
            int count = Encoding.UTF8.GetByteCount(s);
            VarU((uint)count + 1);
            Ensure(count);
            Encoding.UTF8.GetBytes(s, 0, s.Length, _buffer, _length);
            _length += count;
            return this;
        }

        public ByteWriter Bytes(byte[] data)
        {
            if (data == null)
            {
                VarU(0);
                return this;
            }
            VarU((uint)data.Length + 1);
            Ensure(data.Length);
            Buffer.BlockCopy(data, 0, _buffer, _length, data.Length);
            _length += data.Length;
            return this;
        }
    }

    /// <summary>Reader for <see cref="ByteWriter"/> output. Malformed input throws <see cref="WireFormatException"/>.</summary>
    public sealed class ByteReader
    {
        readonly byte[] _data;
        readonly int _end;
        int _pos;

        public ByteReader(byte[] data) : this(data, 0, data?.Length ?? 0) { }

        public ByteReader(byte[] data, int offset, int count)
        {
            _data = data ?? Array.Empty<byte>();
            _pos = offset;
            _end = offset + count;
            if (offset < 0 || _end > _data.Length) throw new WireFormatException("Bad buffer range.");
        }

        public int Remaining => _end - _pos;
        public bool AtEnd => _pos >= _end;

        void Need(int n)
        {
            if (n < 0 || _end - _pos < n) throw new WireFormatException("Message truncated.");
        }

        public byte U8()
        {
            Need(1);
            return _data[_pos++];
        }

        public bool Bool()
        {
            byte b = U8();
            if (b > 1) throw new WireFormatException("Bad bool.");
            return b == 1;
        }

        public ushort U16()
        {
            Need(2);
            int v = _data[_pos] | (_data[_pos + 1] << 8);
            _pos += 2;
            return (ushort)v;
        }

        public short I16() => unchecked((short)U16());

        public int I32()
        {
            Need(4);
            int v = _data[_pos] | (_data[_pos + 1] << 8) | (_data[_pos + 2] << 16) | (_data[_pos + 3] << 24);
            _pos += 4;
            return v;
        }

        public float F32() => BitConverter.Int32BitsToSingle(I32());

        public uint VarU()
        {
            uint result = 0;
            for (int shift = 0; shift < 35; shift += 7)
            {
                byte b = U8();
                result |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return result;
            }
            throw new WireFormatException("Varint too long.");
        }

        int Length(int max)
        {
            uint raw = VarU();
            if (raw == 0) return -1;
            uint len = raw - 1;
            if (len > (uint)max) throw new WireFormatException($"Field longer than {max} bytes.");
            Need((int)len);
            return (int)len;
        }

        public string Str(int maxBytes = 4096)
        {
            int len = Length(maxBytes);
            if (len < 0) return null;
            string s;
            try
            {
                s = new UTF8Encoding(false, true).GetString(_data, _pos, len);
            }
            catch (ArgumentException e)
            {
                throw new WireFormatException("Invalid UTF-8: " + e.Message);
            }
            _pos += len;
            return s;
        }

        public byte[] Bytes(int maxBytes = 65536)
        {
            int len = Length(maxBytes);
            if (len < 0) return null;
            var result = new byte[len];
            Buffer.BlockCopy(_data, _pos, result, 0, len);
            _pos += len;
            return result;
        }
    }

    public sealed class WireFormatException : Exception
    {
        public WireFormatException(string message) : base(message) { }
    }
}


    public static object EmitEvent(object data = null)
    {
        if (data == null) return null;
        return data;
    }
