
using System;
using System.Collections.Generic;
using System.Text;

namespace Google.FlatBuffers
{

    public class FlatBufferBuilder
    {
        private int _space;
        private ByteBuffer _bb;
        private int _minAlign = 1;

        private int[] _vtable = new int[16];

        private int _vtableSize = -1;

        private int _objectStart;

        private int[] _vtables = new int[16];

        private int _numVtables = 0;

        private int _vectorNumElems = 0;

        private Dictionary<string, StringOffset> _sharedStringMap = null;

        public FlatBufferBuilder(int initialSize)
        {
            if (initialSize <= 0)
                throw new ArgumentOutOfRangeException("initialSize",
                    initialSize, "Must be greater than zero");
            _space = initialSize;
            _bb = new ByteBuffer(initialSize);
        }

        public FlatBufferBuilder(ByteBuffer buffer)
        {
            _bb = buffer;
            _space = buffer.Length;
            buffer.Reset();
        }

        public void Clear()
        {
            _space = _bb.Length;
            _bb.Reset();
            _minAlign = 1;
            while (_vtableSize > 0) _vtable[--_vtableSize] = 0;
            _vtableSize = -1;
            _objectStart = 0;
            _numVtables = 0;
            _vectorNumElems = 0;
            if (_sharedStringMap != null)
            {
                _sharedStringMap.Clear();
            }
        }

        public bool ForceDefaults { get; set; }

        public int Offset { get { return _bb.Length - _space; } }

        public void Pad(int size)
        {
             _bb.PutByte(_space -= size, 0, size);
        }

        void GrowBuffer()
        {
            _bb.GrowFront(_bb.Length << 1);
        }

        public void Prep(int size, int additionalBytes)
        {

            if (size > _minAlign)
                _minAlign = size;

            var alignSize =
                ((~((int)_bb.Length - _space + additionalBytes)) + 1) &
                (size - 1);

            while (_space < alignSize + size + additionalBytes)
            {
                var oldBufSize = (int)_bb.Length;
                GrowBuffer();
                _space += (int)_bb.Length - oldBufSize;

            }
            if (alignSize > 0)
                Pad(alignSize);
        }

        public void PutBool(bool x)
        {
          _bb.PutByte(_space -= sizeof(byte), (byte)(x ? 1 : 0));
        }

        public void PutSbyte(sbyte x)
        {
          _bb.PutSbyte(_space -= sizeof(sbyte), x);
        }

        public void PutByte(byte x)
        {
            _bb.PutByte(_space -= sizeof(byte), x);
        }

        public void PutShort(short x)
        {
            _bb.PutShort(_space -= sizeof(short), x);
        }

        public void PutUshort(ushort x)
        {
          _bb.PutUshort(_space -= sizeof(ushort), x);
        }

        public void PutInt(int x)
        {
            _bb.PutInt(_space -= sizeof(int), x);
        }

        public void PutUint(uint x)
        {
          _bb.PutUint(_space -= sizeof(uint), x);
        }

        public void PutLong(long x)
        {
            _bb.PutLong(_space -= sizeof(long), x);
        }

        public void PutUlong(ulong x)
        {
          _bb.PutUlong(_space -= sizeof(ulong), x);
        }

        public void PutFloat(float x)
        {
            _bb.PutFloat(_space -= sizeof(float), x);
        }

        public void Put<T>(T[] x)
            where T : struct
        {
            _space = _bb.Put(_space, x);
        }

        public void Put<T>(ArraySegment<T> x)
            where T : struct
        {
            _space = _bb.Put(_space, x);
        }

        public void Put<T>(IntPtr ptr, int sizeInBytes)
            where T : struct
        {
            _space = _bb.Put<T>(_space, ptr, sizeInBytes);
        }

#if ENABLE_SPAN_T && UNSAFE_BYTEBUFFER

        public void Put<T>(Span<T> x)
            where T : struct
        {
            _space = _bb.Put(_space, x);
        }
#endif

        public void PutDouble(double x)
        {
            _bb.PutDouble(_space -= sizeof(double), x);
        }

        public void AddBool(bool x) { Prep(sizeof(byte), 0); PutBool(x); }

        public void AddSbyte(sbyte x) { Prep(sizeof(sbyte), 0); PutSbyte(x); }

        public void AddByte(byte x) { Prep(sizeof(byte), 0); PutByte(x); }

        public void AddShort(short x) { Prep(sizeof(short), 0); PutShort(x); }

        public void AddUshort(ushort x) { Prep(sizeof(ushort), 0); PutUshort(x); }

        public void AddInt(int x) { Prep(sizeof(int), 0); PutInt(x); }

        public void AddUint(uint x) { Prep(sizeof(uint), 0); PutUint(x); }

        public void AddLong(long x) { Prep(sizeof(long), 0); PutLong(x); }

        public void AddUlong(ulong x) { Prep(sizeof(ulong), 0); PutUlong(x); }

        public void AddFloat(float x) { Prep(sizeof(float), 0); PutFloat(x); }

        public void Add<T>(T[] x)
            where T : struct
        {
            Add(new ArraySegment<T>(x));
        }

        public void Add<T>(ArraySegment<T> x)
            where T : struct
        {
            if (x == null)
            {
                throw new ArgumentNullException("Cannot add a null array");
            }

            if( x.Count == 0)
            {

                return;
            }

            if(!ByteBuffer.IsSupportedType<T>())
            {
                throw new ArgumentException("Cannot add this Type array to the builder");
            }

            int size = ByteBuffer.SizeOf<T>();

            Prep(size, size * (x.Count - 1));
            Put(x);
        }

        public void Add<T>(IntPtr ptr, int sizeInBytes)
            where T : struct
        {
            if(sizeInBytes == 0)
            {

                return;
            }

            if (ptr == IntPtr.Zero)
            {
                throw new ArgumentNullException("Cannot add a null pointer");
            }

            if(sizeInBytes < 0)
            {
                throw new ArgumentOutOfRangeException("sizeInBytes", "sizeInBytes cannot be negative");
            }

            if(!ByteBuffer.IsSupportedType<T>())
            {
                throw new ArgumentException("Cannot add this Type array to the builder");
            }

            int size = ByteBuffer.SizeOf<T>();
            if((sizeInBytes % size) != 0)
            {
                throw new ArgumentException("The given size in bytes " + sizeInBytes + " doesn't match the element size of T ( " + size + ")", "sizeInBytes");
            }

            Prep(size, sizeInBytes - size);
            Put<T>(ptr, sizeInBytes);
        }

#if ENABLE_SPAN_T && UNSAFE_BYTEBUFFER

        public void Add<T>(Span<T> x)
            where T : struct
        {
            if (!ByteBuffer.IsSupportedType<T>())
            {
                throw new ArgumentException("Cannot add this Type array to the builder");
            }

            int size = ByteBuffer.SizeOf<T>();

            Prep(size, size * (x.Length - 1));
            Put(x);
        }
#endif

        public void AddDouble(double x) { Prep(sizeof(double), 0);
                                          PutDouble(x); }

        public void AddOffset(int off)
        {
            Prep(sizeof(int), 0);
            if (off > Offset)
                throw new ArgumentException();

            if (off != 0)
                off = Offset - off + sizeof(int);
            PutInt(off);
        }

        public void StartVector(int elemSize, int count, int alignment)
        {
            NotNested();
            _vectorNumElems = count;
            Prep(sizeof(int), elemSize * count);
            Prep(alignment, elemSize * count);
        }

        public VectorOffset EndVector()
        {
            PutInt(_vectorNumElems);
            return new VectorOffset(Offset);
        }

        public VectorOffset CreateVectorOfTables<T>(Offset<T>[] offsets) where T : struct
        {
            NotNested();
            StartVector(sizeof(int), offsets.Length, sizeof(int));
            for (int i = offsets.Length - 1; i >= 0; i--) AddOffset(offsets[i].Value);
            return EndVector();
        }

        public void Nested(int obj)
        {

            if (obj != Offset)
                throw new Exception(
                    "FlatBuffers: struct must be serialized inline.");
        }

        public void NotNested()
        {

            if (_vtableSize >= 0)
                throw new Exception(
                    "FlatBuffers: object serialization must not be nested.");
        }

        public void StartTable(int numfields)
        {
            if (numfields < 0)
                throw new ArgumentOutOfRangeException("Flatbuffers: invalid numfields");

            NotNested();

            if (_vtable.Length < numfields)
                _vtable = new int[numfields];

            _vtableSize = numfields;
            _objectStart = Offset;
        }

        public void Slot(int voffset)
        {
            if (voffset >= _vtableSize)
                throw new IndexOutOfRangeException("Flatbuffers: invalid voffset");

            _vtable[voffset] = Offset;
        }

        public void AddBool(int o, bool x, bool d) { if (ForceDefaults || x != d) { AddBool(x); Slot(o); } }

        public void AddBool(int o, bool? x) { if (x.HasValue) { AddBool(x.Value); Slot(o); } }

        public void AddSbyte(int o, sbyte x, sbyte d) { if (ForceDefaults || x != d) { AddSbyte(x); Slot(o); } }

        public void AddSbyte(int o, sbyte? x) { if (x.HasValue) { AddSbyte(x.Value); Slot(o); } }

        public void AddByte(int o, byte x, byte d) { if (ForceDefaults || x != d) { AddByte(x); Slot(o); } }

        public void AddByte(int o, byte? x) { if (x.HasValue) { AddByte(x.Value); Slot(o); } }

        public void AddShort(int o, short x, int d) { if (ForceDefaults || x != d) { AddShort(x); Slot(o); } }

        public void AddShort(int o, short? x) { if (x.HasValue) { AddShort(x.Value); Slot(o); } }

        public void AddUshort(int o, ushort x, ushort d) { if (ForceDefaults || x != d) { AddUshort(x); Slot(o); } }

        public void AddUshort(int o, ushort? x) { if (x.HasValue) { AddUshort(x.Value); Slot(o); } }

        public void AddInt(int o, int x, int d) { if (ForceDefaults || x != d) { AddInt(x); Slot(o); } }

        public void AddInt(int o, int? x) { if (x.HasValue) { AddInt(x.Value); Slot(o); } }

        public void AddUint(int o, uint x, uint d) { if (ForceDefaults || x != d) { AddUint(x); Slot(o); } }

        public void AddUint(int o, uint? x) { if (x.HasValue) { AddUint(x.Value); Slot(o); } }

        public void AddLong(int o, long x, long d) { if (ForceDefaults || x != d) { AddLong(x); Slot(o); } }

        public void AddLong(int o, long? x) { if (x.HasValue) { AddLong(x.Value); Slot(o); } }

        public void AddUlong(int o, ulong x, ulong d) { if (ForceDefaults || x != d) { AddUlong(x); Slot(o); } }

        public void AddUlong(int o, ulong? x) { if (x.HasValue) { AddUlong(x.Value); Slot(o); } }

        public void AddFloat(int o, float x, double d) { if (ForceDefaults || x != d) { AddFloat(x); Slot(o); } }

        public void AddFloat(int o, float? x) { if (x.HasValue) { AddFloat(x.Value); Slot(o); } }

        public void AddDouble(int o, double x, double d) { if (ForceDefaults || x != d) { AddDouble(x); Slot(o); } }

        public void AddDouble(int o, double? x) { if (x.HasValue) { AddDouble(x.Value); Slot(o); } }

        public void AddOffset(int o, int x, int d) { if (x != d) { AddOffset(x); Slot(o); } }

        public StringOffset CreateString(string s)
        {
            if (s == null)
            {
                return new StringOffset(0);
            }
            NotNested();
            AddByte(0);
            var utf8StringLen = Encoding.UTF8.GetByteCount(s);
            StartVector(1, utf8StringLen, 1);
            _bb.PutStringUTF8(_space -= utf8StringLen, s);
            return new StringOffset(EndVector().Value);
        }

#if ENABLE_SPAN_T && UNSAFE_BYTEBUFFER

        public StringOffset CreateUTF8String(Span<byte> chars)
        {
            NotNested();
            AddByte(0);
            var utf8StringLen = chars.Length;
            StartVector(1, utf8StringLen, 1);
            _space = _bb.Put(_space, chars);
            return new StringOffset(EndVector().Value);
        }
#endif

        public StringOffset CreateSharedString(string s)
        {
            if (s == null)
            {
              return new StringOffset(0);
            }

            if (_sharedStringMap == null)
            {
                _sharedStringMap = new Dictionary<string, StringOffset>();
            }

            if (_sharedStringMap.ContainsKey(s))
            {
                return _sharedStringMap[s];
            }

            var stringOffset = CreateString(s);
            _sharedStringMap.Add(s, stringOffset);
            return stringOffset;
        }

        public void AddStruct(int voffset, int x, int d)
        {
            if (x != d)
            {
                Nested(x);
                Slot(voffset);
            }
        }

        public int EndTable()
        {
            if (_vtableSize < 0)
                throw new InvalidOperationException(
                  "Flatbuffers: calling EndTable without a StartTable");

            AddInt((int)0);
            var vtableloc = Offset;

            int i = _vtableSize - 1;

            for (; i >= 0 && _vtable[i] == 0; i--) {}
            int trimmedSize = i + 1;
            for (; i >= 0 ; i--) {

                short off = (short)(_vtable[i] != 0
                                        ? vtableloc - _vtable[i]
                                        : 0);
                AddShort(off);

                _vtable[i] = 0;
            }

            const int standardFields = 2;
            AddShort((short)(vtableloc - _objectStart));
            AddShort((short)((trimmedSize + standardFields) *
                             sizeof(short)));

            int existingVtable = 0;
            for (i = 0; i < _numVtables; i++) {
                int vt1 = _bb.Length - _vtables[i];
                int vt2 = _space;
                short len = _bb.GetShort(vt1);
                if (len == _bb.GetShort(vt2)) {
                    for (int j = sizeof(short); j < len; j += sizeof(short)) {
                        if (_bb.GetShort(vt1 + j) != _bb.GetShort(vt2 + j)) {
                            goto endLoop;
                        }
                    }
                    existingVtable = _vtables[i];
                    break;
                }

                endLoop: { }
            }

            if (existingVtable != 0) {

                _space = _bb.Length - vtableloc;

                _bb.PutInt(_space, existingVtable - vtableloc);
            } else {

                if (_numVtables == _vtables.Length)
                {

                    var newvtables = new int[ _numVtables * 2];
                    Array.Copy(_vtables, newvtables, _vtables.Length);

                    _vtables = newvtables;
                };
                _vtables[_numVtables++] = Offset;

                _bb.PutInt(_bb.Length - vtableloc, Offset - vtableloc);
            }

            _vtableSize = -1;
            return vtableloc;
        }

        public void Required(int table, int field)
        {
          int table_start = _bb.Length - table;
          int vtable_start = table_start - _bb.GetInt(table_start);
          bool ok = _bb.GetShort(vtable_start + field) != 0;

          if (!ok)
            throw new InvalidOperationException("FlatBuffers: field " + field +
                                                " must be set");
        }

        protected void Finish(int rootTable, bool sizePrefix)
        {
            Prep(_minAlign, sizeof(int) + (sizePrefix ? sizeof(int) : 0));
            AddOffset(rootTable);
            if (sizePrefix) {
                AddInt(_bb.Length - _space);
            }
            _bb.Position = _space;
        }

        public void Finish(int rootTable)
        {
            Finish(rootTable, false);
        }

        public void FinishSizePrefixed(int rootTable)
        {
            Finish(rootTable, true);
        }

        public ByteBuffer DataBuffer { get { return _bb; } }

        public byte[] SizedByteArray()
        {
            return _bb.ToSizedArray();
        }

        protected void Finish(int rootTable, string fileIdentifier, bool sizePrefix)
        {
            Prep(_minAlign, sizeof(int) + (sizePrefix ? sizeof(int) : 0) +
                            FlatBufferConstants.FileIdentifierLength);
            if (fileIdentifier.Length !=
                FlatBufferConstants.FileIdentifierLength)
                throw new ArgumentException(
                    "FlatBuffers: file identifier must be length " +
                    FlatBufferConstants.FileIdentifierLength,
                    "fileIdentifier");
            for (int i = FlatBufferConstants.FileIdentifierLength - 1; i >= 0;
                 i--)
            {
               AddByte((byte)fileIdentifier[i]);
            }
            Finish(rootTable, sizePrefix);
        }

        public void Finish(int rootTable, string fileIdentifier)
        {
            Finish(rootTable, fileIdentifier, false);
        }

        public void FinishSizePrefixed(int rootTable, string fileIdentifier)
        {
            Finish(rootTable, fileIdentifier, true);
        }
    }
}
