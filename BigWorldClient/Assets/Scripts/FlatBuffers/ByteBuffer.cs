
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

#if ENABLE_SPAN_T && UNSAFE_BYTEBUFFER
using System.Buffers.Binary;
#endif

#if ENABLE_SPAN_T && !UNSAFE_BYTEBUFFER
#warning ENABLE_SPAN_T requires UNSAFE_BYTEBUFFER to also be defined
#endif

namespace Google.FlatBuffers
{
    public abstract class ByteBufferAllocator
    {
#if ENABLE_SPAN_T && UNSAFE_BYTEBUFFER
        public abstract Span<byte> Span { get; }
        public abstract ReadOnlySpan<byte> ReadOnlySpan { get; }
        public abstract Memory<byte> Memory { get; }
        public abstract ReadOnlyMemory<byte> ReadOnlyMemory { get; }

#else
        public byte[] Buffer
        {
            get;
            protected set;
        }
#endif

        public int Length
        {
            get;
            protected set;
        }

        public abstract void GrowFront(int newSize);
    }

    public sealed class ByteArrayAllocator : ByteBufferAllocator
    {
        private byte[] _buffer;

        public ByteArrayAllocator(byte[] buffer)
        {
            _buffer = buffer;
            InitBuffer();
        }

        public override void GrowFront(int newSize)
        {
            if ((Length & 0xC0000000) != 0)
                throw new Exception(
                    "ByteBuffer: cannot grow buffer beyond 2 gigabytes.");

            if (newSize < Length)
                throw new Exception("ByteBuffer: cannot truncate buffer.");

            byte[] newBuffer = new byte[newSize];
            System.Buffer.BlockCopy(_buffer, 0, newBuffer, newSize - Length, Length);
            _buffer = newBuffer;
            InitBuffer();
        }

#if ENABLE_SPAN_T && UNSAFE_BYTEBUFFER
        public override Span<byte> Span => _buffer;
        public override ReadOnlySpan<byte> ReadOnlySpan => _buffer;
        public override Memory<byte> Memory => _buffer;
        public override ReadOnlyMemory<byte> ReadOnlyMemory => _buffer;
#endif

        private void InitBuffer()
        {
            Length = _buffer.Length;
#if !ENABLE_SPAN_T
            Buffer = _buffer;
#endif
        }
    }

    public class ByteBuffer
    {
        private ByteBufferAllocator _buffer;
        private int _pos;

        public ByteBuffer(ByteBufferAllocator allocator, int position)
        {
            _buffer = allocator;
            _pos = position;
        }

        public ByteBuffer(int size) : this(new byte[size]) { }

        public ByteBuffer(byte[] buffer) : this(buffer, 0) { }

        public ByteBuffer(byte[] buffer, int pos)
        {
            _buffer = new ByteArrayAllocator(buffer);
            _pos = pos;
        }

        public int Position
        {
            get { return _pos; }
            set { _pos = value; }
        }

        public int Length { get { return _buffer.Length; } }

        public void Reset()
        {
            _pos = 0;
        }

        public ByteBuffer Duplicate()
        {
            return new ByteBuffer(_buffer, Position);
        }

        public void GrowFront(int newSize)
        {
            _buffer.GrowFront(newSize);
        }

        public byte[] ToArray(int pos, int len)
        {
            return ToArray<byte>(pos, len);
        }

        private static Dictionary<Type, int> genericSizes = new Dictionary<Type, int>()
        {
            { typeof(bool),     sizeof(bool) },
            { typeof(float),    sizeof(float) },
            { typeof(double),   sizeof(double) },
            { typeof(sbyte),    sizeof(sbyte) },
            { typeof(byte),     sizeof(byte) },
            { typeof(short),    sizeof(short) },
            { typeof(ushort),   sizeof(ushort) },
            { typeof(int),      sizeof(int) },
            { typeof(uint),     sizeof(uint) },
            { typeof(ulong),    sizeof(ulong) },
            { typeof(long),     sizeof(long) },
        };

        public static int SizeOf<T>()
        {
            return genericSizes[typeof(T)];
        }

        public static bool IsSupportedType<T>()
        {
            return genericSizes.ContainsKey(typeof(T));
        }

        public static int ArraySize<T>(T[] x)
        {
            return SizeOf<T>() * x.Length;
        }

        public static int ArraySize<T>(ArraySegment<T> x)
        {
            return SizeOf<T>() * x.Count;
        }

#if ENABLE_SPAN_T && UNSAFE_BYTEBUFFER
        public static int ArraySize<T>(Span<T> x)
        {
            return SizeOf<T>() * x.Length;
        }
#endif

#if ENABLE_SPAN_T && UNSAFE_BYTEBUFFER
        public T[] ToArray<T>(int pos, int len)
            where T : struct
        {
            AssertOffsetAndLength(pos, len);
            return MemoryMarshal.Cast<byte, T>(_buffer.ReadOnlySpan.Slice(pos)).Slice(0, len).ToArray();
        }
#else
        public T[] ToArray<T>(int pos, int len)
            where T : struct
        {
            AssertOffsetAndLength(pos, len);
            T[] arr = new T[len];
            Buffer.BlockCopy(_buffer.Buffer, pos, arr, 0, ArraySize(arr));
            return arr;
        }
#endif

        public byte[] ToSizedArray()
        {
            return ToArray<byte>(Position, Length - Position);
        }

        public byte[] ToFullArray()
        {
            return ToArray<byte>(0, Length);
        }

#if ENABLE_SPAN_T && UNSAFE_BYTEBUFFER
        public ReadOnlyMemory<byte> ToReadOnlyMemory(int pos, int len)
        {
            return _buffer.ReadOnlyMemory.Slice(pos, len);
        }

        public Memory<byte> ToMemory(int pos, int len)
        {
            return _buffer.Memory.Slice(pos, len);
        }

        public Span<byte> ToSpan(int pos, int len)
        {
            return _buffer.Span.Slice(pos, len);
        }
#else
        public ArraySegment<byte> ToArraySegment(int pos, int len)
        {
            return new ArraySegment<byte>(_buffer.Buffer, pos, len);
        }

        public MemoryStream ToMemoryStream(int pos, int len)
        {
            return new MemoryStream(_buffer.Buffer, pos, len);
        }
#endif

#if !UNSAFE_BYTEBUFFER

        [StructLayout(LayoutKind.Explicit)]
        struct ConversionUnion
        {
          [FieldOffset(0)] public int intValue;
          [FieldOffset(0)] public float floatValue;
        }
#endif

        static public ushort ReverseBytes(ushort input)
        {
            return (ushort)(((input & 0x00FFU) << 8) |
                            ((input & 0xFF00U) >> 8));
        }
        static public uint ReverseBytes(uint input)
        {
            return ((input & 0x000000FFU) << 24) |
                   ((input & 0x0000FF00U) <<  8) |
                   ((input & 0x00FF0000U) >>  8) |
                   ((input & 0xFF000000U) >> 24);
        }
        static public ulong ReverseBytes(ulong input)
        {
            return (((input & 0x00000000000000FFUL) << 56) |
                    ((input & 0x000000000000FF00UL) << 40) |
                    ((input & 0x0000000000FF0000UL) << 24) |
                    ((input & 0x00000000FF000000UL) <<  8) |
                    ((input & 0x000000FF00000000UL) >>  8) |
                    ((input & 0x0000FF0000000000UL) >> 24) |
                    ((input & 0x00FF000000000000UL) >> 40) |
                    ((input & 0xFF00000000000000UL) >> 56));
        }

#if !UNSAFE_BYTEBUFFER && !ENABLE_SPAN_T

        protected void WriteLittleEndian(int offset, int count, ulong data)
        {
            if (BitConverter.IsLittleEndian)
            {
                for (int i = 0; i < count; i++)
                {
                    _buffer.Buffer[offset + i] = (byte)(data >> i * 8);
                }
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    _buffer.Buffer[offset + count - 1 - i] = (byte)(data >> i * 8);
                }
            }
        }

        protected ulong ReadLittleEndian(int offset, int count)
        {
            AssertOffsetAndLength(offset, count);
            ulong r = 0;
            if (BitConverter.IsLittleEndian)
            {
                for (int i = 0; i < count; i++)
                {
                    r |= (ulong)_buffer.Buffer[offset + i] << i * 8;
                }
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    r |= (ulong)_buffer.Buffer[offset + count - 1 - i] << i * 8;
                }
            }
            return r;
        }
#elif ENABLE_SPAN_T
        protected void WriteLittleEndian(int offset, int count, ulong data)
        {
            if (BitConverter.IsLittleEndian)
            {
                for (int i = 0; i < count; i++)
                {
                    _buffer.Span[offset + i] = (byte)(data >> i * 8);
                }
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    _buffer.Span[offset + count - 1 - i] = (byte)(data >> i * 8);
                }
            }
        }

        protected ulong ReadLittleEndian(int offset, int count)
        {
            AssertOffsetAndLength(offset, count);
            ulong r = 0;
            if (BitConverter.IsLittleEndian)
            {
                for (int i = 0; i < count; i++)
                {
                    r |= (ulong)_buffer.Span[offset + i] << i * 8;
                }
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    r |= (ulong)_buffer.Span[offset + count - 1 - i] << i * 8;
                }
            }
            return r;
        }
#endif

        private void AssertOffsetAndLength(int offset, int length)
        {
#if !BYTEBUFFER_NO_BOUNDS_CHECK
            if (offset < 0 ||
                offset > _buffer.Length - length)
                throw new ArgumentOutOfRangeException();
#endif
        }

#if ENABLE_SPAN_T && UNSAFE_BYTEBUFFER

        public void PutSbyte(int offset, sbyte value)
        {
            AssertOffsetAndLength(offset, sizeof(sbyte));
            _buffer.Span[offset] = (byte)value;
        }

        public void PutByte(int offset, byte value)
        {
            AssertOffsetAndLength(offset, sizeof(byte));
            _buffer.Span[offset] = value;
        }

        public void PutByte(int offset, byte value, int count)
        {
            AssertOffsetAndLength(offset, sizeof(byte) * count);
            Span<byte> span = _buffer.Span.Slice(offset, count);
            for (var i = 0; i < span.Length; ++i)
                span[i] = value;
        }
#else
        public void PutSbyte(int offset, sbyte value)
        {
            AssertOffsetAndLength(offset, sizeof(sbyte));
            _buffer.Buffer[offset] = (byte)value;
        }

        public void PutByte(int offset, byte value)
        {
            AssertOffsetAndLength(offset, sizeof(byte));
            _buffer.Buffer[offset] = value;
        }

        public void PutByte(int offset, byte value, int count)
        {
            AssertOffsetAndLength(offset, sizeof(byte) * count);
            for (var i = 0; i < count; ++i)
                _buffer.Buffer[offset + i] = value;
        }
#endif

        public void Put(int offset, byte value)
        {
            PutByte(offset, value);
        }

#if ENABLE_SPAN_T && UNSAFE_BYTEBUFFER
        public unsafe void PutStringUTF8(int offset, string value)
        {
            AssertOffsetAndLength(offset, value.Length);
            fixed (char* s = value)
            {
                fixed (byte* buffer = &MemoryMarshal.GetReference(_buffer.Span))
                {
                    Encoding.UTF8.GetBytes(s, value.Length, buffer + offset, Length - offset);
                }
            }
        }
#elif ENABLE_SPAN_T
        public void PutStringUTF8(int offset, string value)
        {
            AssertOffsetAndLength(offset, value.Length);
            Encoding.UTF8.GetBytes(value.AsSpan().Slice(0, value.Length),
                _buffer.Span.Slice(offset));
        }
#else
        public void PutStringUTF8(int offset, string value)
        {
            AssertOffsetAndLength(offset, value.Length);
            Encoding.UTF8.GetBytes(value, 0, value.Length,
                _buffer.Buffer, offset);
        }
#endif

#if UNSAFE_BYTEBUFFER

        public void PutShort(int offset, short value)
        {
            PutUshort(offset, (ushort)value);
        }

        public unsafe void PutUshort(int offset, ushort value)
        {
            AssertOffsetAndLength(offset, sizeof(ushort));
#if ENABLE_SPAN_T
            Span<byte> span = _buffer.Span.Slice(offset);
            BinaryPrimitives.WriteUInt16LittleEndian(span, value);
#else
            fixed (byte* ptr = _buffer.Buffer)
            {
                *(ushort*)(ptr + offset) = BitConverter.IsLittleEndian
                    ? value
                    : ReverseBytes(value);
            }
#endif
        }

        public void PutInt(int offset, int value)
        {
            PutUint(offset, (uint)value);
        }

        public unsafe void PutUint(int offset, uint value)
        {
            AssertOffsetAndLength(offset, sizeof(uint));
#if ENABLE_SPAN_T
            Span<byte> span = _buffer.Span.Slice(offset);
            BinaryPrimitives.WriteUInt32LittleEndian(span, value);
#else
            fixed (byte* ptr = _buffer.Buffer)
            {
                *(uint*)(ptr + offset) = BitConverter.IsLittleEndian
                    ? value
                    : ReverseBytes(value);
            }
#endif
        }

        public unsafe void PutLong(int offset, long value)
        {
            PutUlong(offset, (ulong)value);
        }

        public unsafe void PutUlong(int offset, ulong value)
        {
            AssertOffsetAndLength(offset, sizeof(ulong));
#if ENABLE_SPAN_T
            Span<byte> span = _buffer.Span.Slice(offset);
            BinaryPrimitives.WriteUInt64LittleEndian(span, value);
#else
            fixed (byte* ptr = _buffer.Buffer)
            {
                *(ulong*)(ptr + offset) = BitConverter.IsLittleEndian
                    ? value
                    : ReverseBytes(value);
            }
#endif
        }

        public unsafe void PutFloat(int offset, float value)
        {
            AssertOffsetAndLength(offset, sizeof(float));
#if ENABLE_SPAN_T
            fixed (byte* ptr = &MemoryMarshal.GetReference(_buffer.Span))
#else
            fixed (byte* ptr = _buffer.Buffer)
#endif
            {
                if (BitConverter.IsLittleEndian)
                {
                    *(float*)(ptr + offset) = value;
                }
                else
                {
                    *(uint*)(ptr + offset) = ReverseBytes(*(uint*)(&value));
                }
            }
        }

        public unsafe void PutDouble(int offset, double value)
        {
            AssertOffsetAndLength(offset, sizeof(double));
#if ENABLE_SPAN_T
            fixed (byte* ptr = &MemoryMarshal.GetReference(_buffer.Span))
#else
            fixed (byte* ptr = _buffer.Buffer)
#endif
            {
                if (BitConverter.IsLittleEndian)
                {
                    *(double*)(ptr + offset) = value;
                }
                else
                {
                    *(ulong*)(ptr + offset) = ReverseBytes(*(ulong*)(&value));
                }
            }
        }
#else

        public void PutShort(int offset, short value)
        {
            AssertOffsetAndLength(offset, sizeof(short));
            WriteLittleEndian(offset, sizeof(short), (ulong)value);
        }

        public void PutUshort(int offset, ushort value)
        {
            AssertOffsetAndLength(offset, sizeof(ushort));
            WriteLittleEndian(offset, sizeof(ushort), (ulong)value);
        }

        public void PutInt(int offset, int value)
        {
            AssertOffsetAndLength(offset, sizeof(int));
            WriteLittleEndian(offset, sizeof(int), (ulong)value);
        }

        public void PutUint(int offset, uint value)
        {
            AssertOffsetAndLength(offset, sizeof(uint));
            WriteLittleEndian(offset, sizeof(uint), (ulong)value);
        }

        public void PutLong(int offset, long value)
        {
            AssertOffsetAndLength(offset, sizeof(long));
            WriteLittleEndian(offset, sizeof(long), (ulong)value);
        }

        public void PutUlong(int offset, ulong value)
        {
            AssertOffsetAndLength(offset, sizeof(ulong));
            WriteLittleEndian(offset, sizeof(ulong), value);
        }

        public void PutFloat(int offset, float value)
        {
            AssertOffsetAndLength(offset, sizeof(float));

            ConversionUnion union;
            union.intValue = 0;
            union.floatValue = value;
            WriteLittleEndian(offset, sizeof(float), (ulong)union.intValue);
        }

        public void PutDouble(int offset, double value)
        {
            AssertOffsetAndLength(offset, sizeof(double));
            WriteLittleEndian(offset, sizeof(double), (ulong)BitConverter.DoubleToInt64Bits(value));
        }

#endif

#if ENABLE_SPAN_T && UNSAFE_BYTEBUFFER
        public sbyte GetSbyte(int index)
        {
            AssertOffsetAndLength(index, sizeof(sbyte));
            return (sbyte)_buffer.ReadOnlySpan[index];
        }

        public byte Get(int index)
        {
            AssertOffsetAndLength(index, sizeof(byte));
            return _buffer.ReadOnlySpan[index];
        }
#else
        public sbyte GetSbyte(int index)
        {
            AssertOffsetAndLength(index, sizeof(sbyte));
            return (sbyte)_buffer.Buffer[index];
        }

        public byte Get(int index)
        {
            AssertOffsetAndLength(index, sizeof(byte));
            return _buffer.Buffer[index];
        }
#endif

#if ENABLE_SPAN_T && UNSAFE_BYTEBUFFER
        public unsafe string GetStringUTF8(int startPos, int len)
        {
            fixed (byte* buffer = &MemoryMarshal.GetReference(_buffer.ReadOnlySpan.Slice(startPos)))
            {
                return Encoding.UTF8.GetString(buffer, len);
            }
        }
#elif ENABLE_SPAN_T
        public string GetStringUTF8(int startPos, int len)
        {
            return Encoding.UTF8.GetString(_buffer.Span.Slice(startPos, len));
        }
#else
        public string GetStringUTF8(int startPos, int len)
        {
            return Encoding.UTF8.GetString(_buffer.Buffer, startPos, len);
        }
#endif

#if UNSAFE_BYTEBUFFER

        public short GetShort(int offset)
        {
            return (short)GetUshort(offset);
        }

        public unsafe ushort GetUshort(int offset)
        {
            AssertOffsetAndLength(offset, sizeof(ushort));
#if ENABLE_SPAN_T
            ReadOnlySpan<byte> span = _buffer.ReadOnlySpan.Slice(offset);
            return BinaryPrimitives.ReadUInt16LittleEndian(span);
#else
            fixed (byte* ptr = _buffer.Buffer)
            {
                return BitConverter.IsLittleEndian
                    ? *(ushort*)(ptr + offset)
                    : ReverseBytes(*(ushort*)(ptr + offset));
            }
#endif
        }

        public int GetInt(int offset)
        {
            return (int)GetUint(offset);
        }

        public unsafe uint GetUint(int offset)
        {
            AssertOffsetAndLength(offset, sizeof(uint));
#if ENABLE_SPAN_T
            ReadOnlySpan<byte> span = _buffer.ReadOnlySpan.Slice(offset);
            return BinaryPrimitives.ReadUInt32LittleEndian(span);
#else
            fixed (byte* ptr = _buffer.Buffer)
            {
                return BitConverter.IsLittleEndian
                    ? *(uint*)(ptr + offset)
                    : ReverseBytes(*(uint*)(ptr + offset));
            }
#endif
        }

        public long GetLong(int offset)
        {
            return (long)GetUlong(offset);
        }

        public unsafe ulong GetUlong(int offset)
        {
            AssertOffsetAndLength(offset, sizeof(ulong));
#if ENABLE_SPAN_T
            ReadOnlySpan<byte> span = _buffer.ReadOnlySpan.Slice(offset);
            return BinaryPrimitives.ReadUInt64LittleEndian(span);
#else
            fixed (byte* ptr = _buffer.Buffer)
            {
                return BitConverter.IsLittleEndian
                    ? *(ulong*)(ptr + offset)
                    : ReverseBytes(*(ulong*)(ptr + offset));
            }
#endif
        }

        public unsafe float GetFloat(int offset)
        {
            AssertOffsetAndLength(offset, sizeof(float));
#if ENABLE_SPAN_T
            fixed (byte* ptr = &MemoryMarshal.GetReference(_buffer.ReadOnlySpan))
#else
            fixed (byte* ptr = _buffer.Buffer)
#endif
            {
                if (BitConverter.IsLittleEndian)
                {
                    return *(float*)(ptr + offset);
                }
                else
                {
                    uint uvalue = ReverseBytes(*(uint*)(ptr + offset));
                    return *(float*)(&uvalue);
                }
            }
        }

        public unsafe double GetDouble(int offset)
        {
            AssertOffsetAndLength(offset, sizeof(double));
#if ENABLE_SPAN_T
            fixed (byte* ptr = &MemoryMarshal.GetReference(_buffer.ReadOnlySpan))
#else
            fixed (byte* ptr = _buffer.Buffer)
#endif
            {
                if (BitConverter.IsLittleEndian)
                {
                    return *(double*)(ptr + offset);
                }
                else
                {
                    ulong uvalue = ReverseBytes(*(ulong*)(ptr + offset));
                    return *(double*)(&uvalue);
                }
            }
        }
#else

        public short GetShort(int index)
        {
            return (short)ReadLittleEndian(index, sizeof(short));
        }

        public ushort GetUshort(int index)
        {
            return (ushort)ReadLittleEndian(index, sizeof(ushort));
        }

        public int GetInt(int index)
        {
            return (int)ReadLittleEndian(index, sizeof(int));
        }

        public uint GetUint(int index)
        {
            return (uint)ReadLittleEndian(index, sizeof(uint));
        }

        public long GetLong(int index)
        {
            return (long)ReadLittleEndian(index, sizeof(long));
        }

        public ulong GetUlong(int index)
        {
            return ReadLittleEndian(index, sizeof(ulong));
        }

        public float GetFloat(int index)
        {

            ConversionUnion union;
            union.floatValue = 0;
            union.intValue = (int)ReadLittleEndian(index, sizeof(float));
            return union.floatValue;
        }

        public double GetDouble(int index)
        {
            return BitConverter.Int64BitsToDouble((long)ReadLittleEndian(index, sizeof(double)));
        }
#endif

        public int Put<T>(int offset, T[] x)
            where T : struct
        {
            if (x == null)
            {
                throw new ArgumentNullException("Cannot put a null array");
            }

            return Put(offset, new ArraySegment<T>(x));
        }

        public int Put<T>(int offset, ArraySegment<T> x)
            where T : struct
        {
            if (x.Equals(default(ArraySegment<T>)))
            {
                throw new ArgumentNullException("Cannot put a uninitialized array segment");
            }

            if (x.Count == 0)
            {
                throw new ArgumentException("Cannot put an empty array");
            }

            if (!IsSupportedType<T>())
            {
                throw new ArgumentException("Cannot put an array of type "
                    + typeof(T) + " into this buffer");
            }

            if (BitConverter.IsLittleEndian)
            {
                int numBytes = ByteBuffer.ArraySize(x);
                offset -= numBytes;
                AssertOffsetAndLength(offset, numBytes);

#if ENABLE_SPAN_T && UNSAFE_BYTEBUFFER
                MemoryMarshal.Cast<T, byte>(x).CopyTo(_buffer.Span.Slice(offset, numBytes));
#else
                var srcOffset = ByteBuffer.SizeOf<T>() * x.Offset;
                Buffer.BlockCopy(x.Array, srcOffset, _buffer.Buffer, offset, numBytes);
#endif
            }
            else
            {
                throw new NotImplementedException("Big Endian Support not implemented yet " +
                    "for putting typed arrays");

            }
            return offset;
        }

        public int Put<T>(int offset, IntPtr ptr, int sizeInBytes)
            where T : struct
        {
            if (ptr == IntPtr.Zero)
            {
                throw new ArgumentNullException("Cannot add a null pointer");
            }

            if(sizeInBytes <= 0)
            {
                throw new ArgumentException("Cannot put an empty array");
            }

            if (!IsSupportedType<T>())
            {
                throw new ArgumentException("Cannot put an array of type "
                    + typeof(T) + " into this buffer");
            }

            if (BitConverter.IsLittleEndian)
            {
                offset -= sizeInBytes;
                AssertOffsetAndLength(offset, sizeInBytes);

#if ENABLE_SPAN_T && UNSAFE_BYTEBUFFER
                unsafe
                {
                    var span = new Span<byte>(ptr.ToPointer(), sizeInBytes);
                    span.CopyTo(_buffer.Span.Slice(offset, sizeInBytes));
                }
#else
                Marshal.Copy(ptr, _buffer.Buffer, offset, sizeInBytes);
#endif
            }
            else
            {
                throw new NotImplementedException("Big Endian Support not implemented yet " +
                    "for putting typed arrays");

            }
            return offset;
        }

#if ENABLE_SPAN_T && UNSAFE_BYTEBUFFER
        public int Put<T>(int offset, Span<T> x)
            where T : struct
        {
            if (x.Length == 0)
            {
                throw new ArgumentException("Cannot put an empty array");
            }

            if (!IsSupportedType<T>())
            {
                throw new ArgumentException("Cannot put an array of type "
                    + typeof(T) + " into this buffer");
            }

            if (BitConverter.IsLittleEndian)
            {
                int numBytes = ByteBuffer.ArraySize(x);
                offset -= numBytes;
                AssertOffsetAndLength(offset, numBytes);

                MemoryMarshal.Cast<T, byte>(x).CopyTo(_buffer.Span.Slice(offset, numBytes));
            }
            else
            {
                throw new NotImplementedException("Big Endian Support not implemented yet " +
                    "for putting typed arrays");

            }
            return offset;
        }
#endif
    }
}
