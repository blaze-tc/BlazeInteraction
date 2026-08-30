using Yuexin.Radar.Contracts;

namespace Yuexin.Radar.Protocol;

public static class FaseLasePacketParser
{
    public const int PacketLength = 4;

    public static bool HasPacketHighBitPattern(byte a, byte b, byte c, byte d)
    {
        return (a & 0x80) == 0 &&
               (b & 0x80) == 0 &&
               (c & 0x80) == 0 &&
               (d & 0x80) != 0;
    }

    public static bool TryParse(ReadOnlySpan<byte> packet, out RadarPoint point)
    {
        point = default;
        if (packet.Length < PacketLength)
        {
            return false;
        }

        var a = packet[0];
        var b = packet[1];
        var c = packet[2];
        var d = packet[3];

        if (!HasPacketHighBitPattern(a, b, c, d) || !FaseLaseCrc.IsValid(a, b, c, d))
        {
            return false;
        }

        var distanceCentimeters = DecodeDistanceCentimeters(a, b, c);
        var angleRaw = DecodeAngleRaw(c, d);
        var angleDegrees = angleRaw / 16f;
        var distanceMeters = distanceCentimeters / 100f;
        var radians = angleDegrees * MathF.PI / 180f;
        var x = distanceMeters * MathF.Cos(radians);
        var y = distanceMeters * MathF.Sin(radians);

        point = new RadarPoint(distanceCentimeters, angleRaw, angleDegrees, x, y);
        return true;
    }

    public static int DecodeDistanceCentimeters(byte a, byte b, byte c)
    {
        var distance = a & 0x0F;
        distance <<= 7;
        distance += b & 0x7F;
        distance <<= 1;
        if ((c & 0x40) != 0)
        {
            distance++;
        }

        return distance;
    }

    public static int DecodeAngleRaw(byte c, byte d)
    {
        var angle = c & 0x3F;
        angle <<= 7;
        angle += d & 0x7F;
        return angle;
    }
}
