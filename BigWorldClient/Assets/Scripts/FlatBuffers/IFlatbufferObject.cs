
namespace Google.FlatBuffers
{

    public interface IFlatbufferObject
    {
        void __init(int _i, ByteBuffer _bb);

        ByteBuffer ByteBuffer { get; }
    }
}
