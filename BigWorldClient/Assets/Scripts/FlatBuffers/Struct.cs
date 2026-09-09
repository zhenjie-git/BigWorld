
namespace Google.FlatBuffers
{

    public struct Struct
    {
        public int bb_pos { get; private set; }
        public ByteBuffer bb { get; private set; }

        public Struct(int _i, ByteBuffer _bb) : this()
        {
            bb = _bb;
            bb_pos = _i;
        }
    }
}
