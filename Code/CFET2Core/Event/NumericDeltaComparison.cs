using System;
using System.Numerics;

namespace Jtext103.CFET2.Core.Event
{
    internal static class NumericDeltaComparison
    {
        internal static bool MeetsThreshold(object previous, object current, double threshold)
        {
            if (previous == null || current == null || previous.GetType() != current.GetType()) return true;
            if (previous is double left) return Floating(left, (double)current, threshold);
            if (previous is float single) return Floating(single, (float)current, threshold);
            var type = previous.GetType();
            if (type.IsEnum) return true;
            switch (Type.GetTypeCode(type))
            {
                case TypeCode.Byte: case TypeCode.SByte: case TypeCode.Int16: case TypeCode.UInt16:
                case TypeCode.Int32: case TypeCode.UInt32: case TypeCode.Int64: case TypeCode.UInt64:
                case TypeCode.Decimal:
                    DecimalRatio(Convert.ToDecimal(previous), out var a, out var scaleA);
                    DecimalRatio(Convert.ToDecimal(current), out var b, out var scaleB);
                    var scale = Math.Max(scaleA, scaleB);
                    var difference = BigInteger.Abs(a * BigInteger.Pow(10, scale - scaleA) - b * BigInteger.Pow(10, scale - scaleB));
                    DoubleRatio(threshold, out var numerator, out var denominator);
                    return difference * denominator >= numerator * BigInteger.Pow(10, scale);
                default: return true;
            }
        }

        private static bool Floating(double left, double right, double threshold)
        {
            if (double.IsNaN(left) || double.IsNaN(right) || double.IsInfinity(left) || double.IsInfinity(right)) return true;
            return Math.Abs(right - left) >= threshold;
        }

        private static void DecimalRatio(decimal value, out BigInteger coefficient, out int scale)
        {
            var bits = decimal.GetBits(value);
            coefficient = (BigInteger)(uint)bits[0] | ((BigInteger)(uint)bits[1] << 32) | ((BigInteger)(uint)bits[2] << 64);
            if ((bits[3] & int.MinValue) != 0) coefficient = -coefficient;
            scale = (bits[3] >> 16) & 255;
        }

        // Positive finite IEEE-754 value, including subnormals, as an exact integer ratio.
        private static void DoubleRatio(double value, out BigInteger numerator, out BigInteger denominator)
        {
            var bits = (ulong)BitConverter.DoubleToInt64Bits(value);
            var exponent = (int)((bits >> 52) & 2047);
            var mantissa = bits & 0xfffffffffffffUL;
            if (exponent != 0) mantissa |= 1UL << 52;
            var shift = exponent == 0 ? -1074 : exponent - 1023 - 52;
            numerator = new BigInteger(mantissa);
            denominator = BigInteger.One;
            if (shift >= 0) numerator <<= shift;
            else denominator <<= -shift;
        }
    }
}
