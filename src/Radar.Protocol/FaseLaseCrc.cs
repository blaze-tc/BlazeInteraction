using System.Numerics;

namespace Yuexin.Radar.Protocol;

public static class FaseLaseCrc
{
    public static int Calculate(byte b, byte c, byte d)
    {
        return (BitOperations.PopCount((uint)b)
              + BitOperations.PopCount((uint)c)
              + BitOperations.PopCount((uint)d)) & 0x07;
    }

    public static int ReadPacketCrc(byte a) => (a >> 4) & 0x07;

    public static bool IsValid(byte a, byte b, byte c, byte d)
    {
        return ReadPacketCrc(a) == Calculate(b, c, d);
    }
}
