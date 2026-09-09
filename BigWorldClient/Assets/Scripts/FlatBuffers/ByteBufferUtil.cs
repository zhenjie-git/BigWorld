
using System;

namespace Google.FlatBuffers
{

	public class ByteBufferUtil
	{

		public static int GetSizePrefix(ByteBuffer bb) {
			return bb.GetInt(bb.Position);
		}

		public static ByteBuffer RemoveSizePrefix(ByteBuffer bb) {
			ByteBuffer s = bb.Duplicate();
			s.Position += FlatBufferConstants.SizePrefixLength;
			return s;
		}
	}
}
