
using System;
using System.Reflection;using System.Collections.Generic;
using System.IO;

namespace Google.FlatBuffers
{

  public class Options
  {
    public const int DEFAULT_MAX_DEPTH = 64;
    public const int DEFAULT_MAX_TABLES = 1000000;

    private int max_depth = 0;
    private int max_tables = 0;
    private bool string_end_check = false;
    private bool alignment_check = false;

    public Options()
    {
      max_depth = DEFAULT_MAX_DEPTH;
      max_tables = DEFAULT_MAX_TABLES;
      string_end_check = true;
      alignment_check = true;
    }

    public Options(int maxDepth, int maxTables, bool stringEndCheck, bool alignmentCheck)
    {
      max_depth = maxDepth;
      max_tables = maxTables;
      string_end_check = stringEndCheck;
      alignment_check = alignmentCheck;
    }

    public int maxDepth
    {
      get { return max_depth; }
      set { max_depth = value; }
    }

    public int maxTables
    {
      get { return max_tables; }
      set { max_tables = value; }
    }

    public bool stringEndCheck
    {
      get { return string_end_check; }
      set { string_end_check = value; }
    }

    public bool alignmentCheck
    {
      get { return alignment_check; }
      set { alignment_check = value; }
    }
  }

  public struct checkElementStruct
  {
    public bool elementValid;
    public uint elementOffset;
  }

  public delegate bool VerifyTableAction(Verifier verifier, uint tablePos);
  public delegate bool VerifyUnionAction(Verifier verifier, byte typeId, uint tablePos);

  public class Verifier
  {
    private ByteBuffer verifier_buffer = null;
    private Options verifier_options = null;
    private int depth_cnt = 0;
    private int num_tables_cnt = 0;

    public const int SIZE_BYTE = 1;
    public const int SIZE_INT = 4;
    public const int SIZE_U_OFFSET = 4;
    public const int SIZE_S_OFFSET = 4;
    public const int SIZE_V_OFFSET = 2;
    public const int SIZE_PREFIX_LENGTH = FlatBufferConstants.SizePrefixLength;
    public const int FLATBUFFERS_MAX_BUFFER_SIZE = System.Int32.MaxValue;
    public const int FILE_IDENTIFIER_LENGTH = FlatBufferConstants.FileIdentifierLength;

    public Verifier()
    {

      verifier_buffer = null;

      verifier_options = null;

      depth_cnt = 0;

      num_tables_cnt = 0;
    }

    public Verifier(ByteBuffer buf, Options options = null)
    {
      verifier_buffer = buf;
      verifier_options = options ?? new Options();
      depth_cnt = 0;
      num_tables_cnt = 0;
    }

    public ByteBuffer Buf
    {
      get { return verifier_buffer; }
      set { verifier_buffer = value; }
    }

    public Options options
    {
      get { return verifier_options; }
      set { verifier_options = value; }
    }

    public int depth
    {
      get { return depth_cnt; }
      set { depth_cnt = value; }
    }

    public int numTables
    {
      get { return num_tables_cnt; }
      set { num_tables_cnt = value; }
    }

    public Verifier SetMaxDepth(int value)
    {
      verifier_options.maxDepth = value;
      return this;
    }

    public Verifier SetMaxTables(int value)
    {
      verifier_options.maxTables = value;
      return this;
    }

    public Verifier SetAlignmentCheck(bool value)
    {
      verifier_options.alignmentCheck = value;
      return this;
    }

    public Verifier SetStringCheck(bool value)
    {
      verifier_options.stringEndCheck = value;
      return this;
    }

    private bool BufferHasIdentifier(ByteBuffer buf, uint startPos, string identifier)
    {
      if (identifier.Length != FILE_IDENTIFIER_LENGTH)
      {
        throw new ArgumentException("FlatBuffers: file identifier must be length" + Convert.ToString(FILE_IDENTIFIER_LENGTH));
      }
      for (int i = 0; i < FILE_IDENTIFIER_LENGTH; i++)
      {
        if ((sbyte)identifier[i] != verifier_buffer.GetSbyte(Convert.ToInt32(SIZE_S_OFFSET + i + startPos)))
        {
          return false;
        }
      }

      return true;
    }

    private uint ReadUOffsetT(ByteBuffer buf, uint pos)
    {
      return buf.GetUint(Convert.ToInt32(pos));
    }

    private int ReadSOffsetT(ByteBuffer buf, int pos)
    {
      return buf.GetInt(pos);
    }

    private short ReadVOffsetT(ByteBuffer buf, int pos)
    {
      return buf.GetShort(pos);
    }

    private short GetVRelOffset(int pos, short vtableOffset)
    {
      short VOffset = 0;

      try
      {

        short vtable = Convert.ToInt16(pos - ReadSOffsetT(verifier_buffer, pos));

        if (vtableOffset < ReadVOffsetT(verifier_buffer, vtable))
        {

          VOffset = ReadVOffsetT(verifier_buffer, vtable + vtableOffset);
        }
        else
        {
          VOffset = 0;
        }
      }
      catch (Exception e)
      {
        Console.WriteLine("Exception: {0}", e);
        return VOffset;
      }
      return VOffset;

    }

    private uint GetVOffset(uint tablePos, short vtableOffset)
    {
      uint UOffset = 0;

      short relPos = GetVRelOffset(Convert.ToInt32(tablePos), vtableOffset);
      if (relPos != 0)
      {

        UOffset = Convert.ToUInt32(tablePos + relPos);
      }
      else
      {
        UOffset = 0;
      }
      return UOffset;
    }

    private bool CheckComplexity()
    {
      return ((depth <= options.maxDepth) && (numTables <= options.maxTables));
    }

    private bool CheckAlignment(uint element, ulong align)
    {
      return (((element & (align - 1)) == 0) || (!options.alignmentCheck));
    }

    private bool CheckElement(uint pos, ulong elementSize)
    {
      return ((elementSize < Convert.ToUInt64(verifier_buffer.Length)) && (pos <= (Convert.ToUInt32(verifier_buffer.Length) - elementSize)));
    }

    private bool CheckScalar(uint pos, ulong elementSize)
    {
      return ((CheckAlignment(pos, elementSize)) && (CheckElement(pos, elementSize)));
    }

    private bool CheckOffset(uint offset)
    {
      return (CheckScalar(offset, SIZE_U_OFFSET));
    }

    private checkElementStruct CheckVectorOrString(uint pos, ulong elementSize)
    {
      var result = new checkElementStruct
      {
        elementValid = false,
        elementOffset = 0
      };

      uint vectorPos = pos;

      if (!CheckScalar(vectorPos, SIZE_U_OFFSET))
      {

        return result;
      }

      uint size = ReadUOffsetT(verifier_buffer, vectorPos);
      ulong max_elements = (FLATBUFFERS_MAX_BUFFER_SIZE / elementSize);
      if (size >= max_elements)
      {

        return result;
      }

      uint bytes_size = SIZE_U_OFFSET + (Convert.ToUInt32(elementSize) * size);
      uint buffer_end_pos = vectorPos + bytes_size;
      result.elementValid = CheckElement(vectorPos, bytes_size);
      result.elementOffset = buffer_end_pos;
      return (result);
    }

    private bool CheckString(uint pos)
    {
      var result = CheckVectorOrString(pos, SIZE_BYTE);
      if (options.stringEndCheck)
      {
        result.elementValid = result.elementValid && CheckScalar(result.elementOffset, 1);
        result.elementValid = result.elementValid && (verifier_buffer.GetSbyte(Convert.ToInt32(result.elementOffset)) == 0);
      }
      return result.elementValid;
    }

    private bool CheckVector(uint pos, ulong elementSize)
    {
      var result = CheckVectorOrString(pos, elementSize);
      return result.elementValid;
    }

    private bool CheckTable(uint tablePos, VerifyTableAction verifyAction)
    {
      return verifyAction(this, tablePos);
    }

    private bool CheckStringFunc(Verifier verifier, uint pos)
    {
      return verifier.CheckString(pos);
    }

    private bool CheckVectorOfObjects(uint pos, VerifyTableAction verifyAction)
    {
      if (!CheckVector(pos, SIZE_U_OFFSET))
      {
        return false;
      }
      uint size = ReadUOffsetT(verifier_buffer, pos);

      uint vecStart = pos + SIZE_U_OFFSET;
      uint vecOff = 0;

      for (uint i = 0; i < size; i++)
      {
        vecOff = vecStart + (i * SIZE_U_OFFSET);
        if (!CheckIndirectOffset(vecOff))
        {
          return false;
        }
        uint objOffset = GetIndirectOffset(vecOff);
        if (!verifyAction(this, objOffset))
        {
          return false;
        }
      }
      return true;
    }

    private bool CheckIndirectOffset(uint pos)
    {

      if(!CheckScalar(pos, SIZE_U_OFFSET))
      {
        return false;
      }

      uint offset = ReadUOffsetT(verifier_buffer, pos);

      if ((offset == 0) || (offset >= FLATBUFFERS_MAX_BUFFER_SIZE))
      {
        return false;
      }

      return CheckElement(pos + offset, 1);
    }

    private bool CheckBufferFromStart(string identifier, uint startPos, VerifyTableAction verifyAction)
    {
      if ((identifier != null) &&
          (identifier.Length == 0) &&
          ((verifier_buffer.Length < (SIZE_U_OFFSET + FILE_IDENTIFIER_LENGTH)) || (!BufferHasIdentifier(verifier_buffer, startPos, identifier))))
      {
        return false;
      }
      if(!CheckIndirectOffset(startPos))
      {
        return false;
      }
      uint offset = GetIndirectOffset(startPos);
      return CheckTable(offset, verifyAction);
    }

    private uint GetIndirectOffset(uint pos)
    {

      uint offset = pos + ReadUOffsetT(verifier_buffer, pos);
      return offset;
    }

    public bool VerifyTableStart(uint tablePos)
    {

      depth_cnt++;
      num_tables_cnt++;

      if (!CheckScalar(tablePos, SIZE_S_OFFSET))
      {
        return false;
      }
      uint vtable = (uint)(tablePos - ReadSOffsetT(verifier_buffer, Convert.ToInt32(tablePos)));
      return ((CheckComplexity()) && (CheckScalar(vtable, SIZE_V_OFFSET)) && (CheckAlignment(Convert.ToUInt32(ReadVOffsetT(verifier_buffer, Convert.ToInt32(vtable))), SIZE_V_OFFSET)) && (CheckElement(vtable, Convert.ToUInt64(ReadVOffsetT(verifier_buffer, Convert.ToInt32(vtable))))));
    }

    public bool VerifyTableEnd(uint tablePos)
    {
      depth--;
      return true;
    }

    public bool VerifyField(uint tablePos, short offsetId, ulong elementSize, ulong align, bool required)
    {
      uint offset = GetVOffset(tablePos, offsetId);
      if (offset != 0)
      {
        return ((CheckAlignment(offset, align)) && (CheckElement(offset, elementSize)));
      }
      return !required;
    }

    public bool VerifyString(uint tablePos, short vOffset, bool required)
    {
      var offset = GetVOffset(tablePos, vOffset);
      if (offset == 0)
      {
        return !required;
      }
      if (!CheckIndirectOffset(offset))
      {
        return false;
      }
      var strOffset = GetIndirectOffset(offset);
      return CheckString(strOffset);
    }

    public bool VerifyVectorOfData(uint tablePos, short vOffset, ulong elementSize, bool required)
    {
      var offset = GetVOffset(tablePos, vOffset);
      if (offset == 0)
      {
        return !required;
      }
      if (!CheckIndirectOffset(offset))
      {
        return false;
      }
      var vecOffset = GetIndirectOffset(offset);
      return  CheckVector(vecOffset, elementSize);
    }

    public bool VerifyVectorOfStrings(uint tablePos, short offsetId, bool required)
    {
      var offset = GetVOffset(tablePos, offsetId);
      if (offset == 0)
      {
        return !required;
      }
      if (!CheckIndirectOffset(offset))
      {
        return false;
      }
      var vecOffset = GetIndirectOffset(offset);
      return CheckVectorOfObjects(vecOffset, CheckStringFunc);
    }

    public bool VerifyVectorOfTables(uint tablePos, short offsetId, VerifyTableAction verifyAction, bool required)
    {
      var offset = GetVOffset(tablePos, offsetId);
      if (offset == 0)
      {
        return !required;
      }
      if (!CheckIndirectOffset(offset))
      {
        return false;
      }
      var vecOffset = GetIndirectOffset(offset);
      return CheckVectorOfObjects(vecOffset, verifyAction);
    }

    public bool VerifyTable(uint tablePos, short offsetId, VerifyTableAction verifyAction, bool required)
    {
      var offset = GetVOffset(tablePos, offsetId);
      if (offset == 0)
      {
        return !required;
      }
      if (!CheckIndirectOffset(offset))
      {
        return false;
      }
      var tabOffset = GetIndirectOffset(offset);
      return CheckTable(tabOffset, verifyAction);
    }

    public bool VerifyNestedBuffer(uint tablePos, short offsetId, VerifyTableAction verifyAction, bool required)
    {
      var offset = GetVOffset(tablePos, offsetId);
      if (offset == 0)
      {
        return !required;
      }
      uint vecOffset = GetIndirectOffset(offset);
      if (!CheckVector(vecOffset, SIZE_BYTE))
      {
        return false;
      }
      if (verifyAction != null)
      {
        var vecLength = ReadUOffsetT(verifier_buffer, vecOffset);

        var vecStart = vecOffset + SIZE_U_OFFSET;

        var nestedByteBuffer = new ByteBuffer(verifier_buffer.ToArray(Convert.ToInt32(vecStart), Convert.ToInt32(vecLength)));
        var nestedVerifyier = new Verifier(nestedByteBuffer, options);

        if (!nestedVerifyier.CheckBufferFromStart("", 0, verifyAction))
        {
          return false;
        }
      }
      return true;
    }

    public bool VerifyUnionData(uint pos, ulong elementSize, ulong align)
    {
      bool result = ((CheckAlignment(pos, align)) && (CheckElement(pos, elementSize)));
      return result;
    }

    public bool VerifyUnionString(uint pos)
    {
      bool result = CheckString(pos);
      return result;
    }

    public bool VerifyUnion(uint tablePos, short typeIdVOffset, short valueVOffset, VerifyUnionAction verifyAction, bool required)
    {

      var offset = GetVOffset(tablePos, typeIdVOffset);
      if (offset == 0)
      {
        return !required;
      }
      if (!((CheckAlignment(offset, SIZE_BYTE)) && (CheckElement(offset, SIZE_BYTE))))
      {
        return false;
      }

      offset = GetVOffset(tablePos, valueVOffset);

      var typeId = verifier_buffer.Get(Convert.ToInt32(offset));
      if (offset == 0)
      {

        return verifyAction(this, typeId, Convert.ToUInt32(verifier_buffer.Length));
      }
      if (!CheckIndirectOffset(offset))
      {
        return false;
      }

      uint unionOffset = GetIndirectOffset(offset);
      return verifyAction(this, typeId, unionOffset);
    }

    public bool VerifyVectorOfUnion(uint tablePos, short typeOffsetId, short offsetId, VerifyUnionAction verifyAction, bool required)
    {

      var offset = GetVOffset(tablePos, typeOffsetId);
      if (offset == 0)
      {
        return !required;
      }
      if (!CheckIndirectOffset(offset))
      {
        return false;
      }

      var typeIdVectorOffset = GetIndirectOffset(offset);

      offset = GetVOffset(tablePos, offsetId);
      if (!CheckIndirectOffset(offset))
      {
        return false;
      }
      var valueVectorOffset = GetIndirectOffset(offset);

      if(!CheckVector(typeIdVectorOffset, SIZE_BYTE) ||
         !CheckVector(valueVectorOffset, SIZE_U_OFFSET))
      {
        return false;
      }

      var typeIdVectorLength = ReadUOffsetT(verifier_buffer, typeIdVectorOffset);
      var valueVectorLength = ReadUOffsetT(verifier_buffer, valueVectorOffset);
      if (typeIdVectorLength != valueVectorLength)
      {
        return false;
      }

      var typeIdStart = typeIdVectorOffset + SIZE_U_OFFSET;
      var valueStart = valueVectorOffset + SIZE_U_OFFSET;
      for (uint i = 0; i < typeIdVectorLength; i++)
      {

        byte typeId = verifier_buffer.Get(Convert.ToInt32(typeIdStart + i * SIZE_U_OFFSET));

        uint off = valueStart + i * SIZE_U_OFFSET;

        if (!CheckIndirectOffset(off))
        {
          return false;
        }
        uint valueOffset = GetIndirectOffset(off);

        if (!verifyAction(this, typeId, valueOffset))
        {
          return false;
        }
      }
      return true;
    }

    public bool VerifyBuffer(string identifier, bool sizePrefixed, VerifyTableAction verifyAction)
    {

      depth = 0;
      numTables = 0;

      var start = (uint)(verifier_buffer.Position);
      if (sizePrefixed)
      {
        start = (uint)(verifier_buffer.Position) + SIZE_PREFIX_LENGTH;
        if(!CheckScalar((uint)(verifier_buffer.Position), SIZE_PREFIX_LENGTH))
        {
          return false;
        }
        uint size = ReadUOffsetT(verifier_buffer, (uint)(verifier_buffer.Position));
        if (size != ((uint)(verifier_buffer.Length) - start))
        {
          return false;
        }
      }
      return CheckBufferFromStart(identifier, start, verifyAction);
    }
  }

}
