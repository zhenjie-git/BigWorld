
namespace Google.FlatBuffers
{

    public struct Offset<T> where T : struct
    {
        public int Value;
        public Offset(int value)
        {
            Value = value;
        }
    }

    public struct StringOffset
    {
        public int Value;
        public StringOffset(int value)
        {
            Value = value;
        }
    }

    public struct VectorOffset
    {
        public int Value;
        public VectorOffset(int value)
        {
            Value = value;
        }
    }
}
