using System;

namespace BigWorldClient
{
    public readonly struct SceneID : IEquatable<SceneID>
    {
        public const int TemplateShift = 48;
        public const int ZoneXShift = 40;
        public const int ZoneZShift = 32;
        public const uint TemplateMask = 0xFFFF;
        public const uint ZoneMask = 0xFF;
        public const uint InstanceMask = 0xFFFFFFFF;

        public readonly long Value;

        public SceneID(long value) { Value = value; }

        public static SceneID Make(int template, int zoneX, int zoneZ, uint instance = 0)
        {
            return new SceneID((long)((ulong)(template & (int)TemplateMask) << TemplateShift |
                (ulong)(zoneX & (int)ZoneMask) << ZoneXShift |
                (ulong)(zoneZ & (int)ZoneMask) << ZoneZShift |
                instance & InstanceMask));
        }

        public int Template() => (int)((ulong)Value >> TemplateShift & TemplateMask);
        public int ZoneX() => (int)((ulong)Value >> ZoneXShift & ZoneMask);
        public int ZoneZ() => (int)((ulong)Value >> ZoneZShift & ZoneMask);
        public uint Instance() => (uint)((ulong)Value & InstanceMask);
        public int Main() => (int)((ulong)Value >> ZoneZShift);
        public bool Valid() => Value != 0 && Template() != 0;

        public bool Equals(SceneID other) => Value == other.Value;
        public override bool Equals(object obj) => obj is SceneID other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => $"0x{Value:X16}";

        public static bool operator ==(SceneID left, SceneID right) => left.Value == right.Value;
        public static bool operator !=(SceneID left, SceneID right) => left.Value != right.Value;
    }
}
