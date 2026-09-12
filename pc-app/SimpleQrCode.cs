using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;

namespace TapRelay
{
    /// <summary>
    /// Small QR encoder used only for the short TapRelay endpoint payload.
    /// It deliberately supports byte-mode QR versions 1-5 with low error
    /// correction: the connection payload is ASCII, and keeping the encoder
    /// in-process avoids a runtime dependency on a QR package.
    /// </summary>
    internal static class SimpleQrCode
    {
        private static readonly int[] TotalCodewords = { 26, 44, 70, 100, 134 };
        private static readonly int[] DataCodewords = { 19, 34, 55, 80, 108 };
        private static readonly int[] EccCodewords = { 7, 10, 15, 20, 26 };
        private static readonly int[] ByteCapacities = { 17, 32, 53, 78, 106 };
        private static readonly int[][] AlignmentPositions =
        {
            Array.Empty<int>(),
            new[] { 6, 18 },
            new[] { 6, 22 },
            new[] { 6, 26 },
            new[] { 6, 30 }
        };

        public static Bitmap CreateBitmap(string text, int scale = 8)
        {
            var bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
            var version = SelectVersion(bytes.Length);
            var codewords = CreateCodewords(bytes, version);
            var qr = new Matrix(version);
            qr.DrawFunctionPatterns();
            qr.DrawCodewords(codewords);
            qr.DrawFormatBits(mask: 0);
            return qr.Render(scale, quietZone: 4);
        }

        private static int SelectVersion(int byteCount)
        {
            for (var index = 0; index < ByteCapacities.Length; index++)
            {
                if (byteCount <= ByteCapacities[index])
                {
                    return index + 1;
                }
            }

            throw new ArgumentException("二维码数据过长，请缩短公网域名或地址。", nameof(byteCount));
        }

        private static byte[] CreateCodewords(byte[] data, int version)
        {
            var dataCount = DataCodewords[version - 1];
            var bits = new List<bool>(dataCount * 8);
            AppendBits(bits, 0b0100, 4); // byte mode
            AppendBits(bits, data.Length, 8); // versions 1-9 use an 8-bit count field
            foreach (var value in data)
            {
                AppendBits(bits, value, 8);
            }

            var capacity = dataCount * 8;
            for (var i = 0; i < 4 && bits.Count < capacity; i++) bits.Add(false);
            while ((bits.Count & 7) != 0) bits.Add(false);

            var pad = true;
            while (bits.Count < capacity)
            {
                AppendBits(bits, pad ? 0xEC : 0x11, 8);
                pad = !pad;
            }

            var dataCodewords = ToBytes(bits);
            var divisor = ReedSolomonComputeDivisor(EccCodewords[version - 1]);
            var remainder = ReedSolomonComputeRemainder(dataCodewords, divisor);
            var result = new byte[TotalCodewords[version - 1]];
            Array.Copy(dataCodewords, result, dataCodewords.Length);
            Array.Copy(remainder, 0, result, dataCodewords.Length, remainder.Length);
            return result;
        }

        private static void AppendBits(List<bool> bits, int value, int length)
        {
            for (var i = length - 1; i >= 0; i--)
            {
                bits.Add(((value >> i) & 1) != 0);
            }
        }

        private static byte[] ToBytes(List<bool> bits)
        {
            var result = new byte[bits.Count / 8];
            for (var i = 0; i < bits.Count; i++)
            {
                if (bits[i]) result[i >> 3] |= (byte)(1 << (7 - (i & 7)));
            }
            return result;
        }

        private static byte[] ReedSolomonComputeDivisor(int degree)
        {
            var result = new byte[degree];
            result[degree - 1] = 1;
            byte root = 1;
            for (var i = 0; i < degree; i++)
            {
                // Multiply the current product by (x - r^i). The XOR must use
                // result[j + 1] BEFORE it has been multiplied in this iteration,
                // so the multiply and the XOR are interleaved in one pass.
                for (var j = 0; j < result.Length; j++)
                {
                    result[j] = ReedSolomonMultiply(result[j], root);
                    if (j + 1 < result.Length)
                    {
                        result[j] ^= result[j + 1];
                    }
                }
                root = ReedSolomonMultiply(root, 0x02);
            }
            return result;
        }

        private static byte[] ReedSolomonComputeRemainder(byte[] data, byte[] divisor)
        {
            var result = new byte[divisor.Length];
            foreach (var value in data)
            {
                var factor = (byte)(value ^ result[0]);
                Array.Copy(result, 1, result, 0, result.Length - 1);
                result[result.Length - 1] = 0;
                for (var i = 0; i < result.Length; i++)
                {
                    result[i] ^= ReedSolomonMultiply(divisor[i], factor);
                }
            }
            return result;
        }

        private static byte ReedSolomonMultiply(byte x, byte y)
        {
            var product = 0;
            var left = (int)x;
            var right = (int)y;
            while (right != 0)
            {
                if ((right & 1) != 0) product ^= left;
                right >>= 1;
                left <<= 1;
                if ((left & 0x100) != 0) left ^= 0x11D;
            }
            return (byte)product;
        }

        private sealed class Matrix
        {
            private readonly int _version;
            private readonly int _size;
            private readonly bool[,] _modules;
            private readonly bool[,] _isFunction;

            internal Matrix(int version)
            {
                _version = version;
                _size = version * 4 + 17;
                _modules = new bool[_size, _size];
                _isFunction = new bool[_size, _size];
            }

            internal void DrawFunctionPatterns()
            {
                DrawFinderPattern(3, 3);
                DrawFinderPattern(_size - 4, 3);
                DrawFinderPattern(3, _size - 4);

                for (var i = 8; i < _size - 8; i++)
                {
                    if (!_isFunction[i, 6]) SetFunctionModule(i, 6, (i & 1) == 0);
                    if (!_isFunction[6, i]) SetFunctionModule(6, i, (i & 1) == 0);
                }

                var positions = AlignmentPositions[_version - 1];
                for (var i = 0; i < positions.Length; i++)
                {
                    for (var j = 0; j < positions.Length; j++)
                    {
                        if ((i == 0 && j == 0) ||
                            (i == 0 && j == positions.Length - 1) ||
                            (i == positions.Length - 1 && j == 0))
                        {
                            continue;
                        }
                        DrawAlignmentPattern(positions[i], positions[j]);
                    }
                }

                // Reserve both format-information copies and the fixed dark module.
                for (var i = 0; i < 9; i++)
                {
                    if (i != 6)
                    {
                        SetFunctionModule(8, i, false);
                        SetFunctionModule(i, 8, false);
                    }
                }
                for (var i = 0; i < 8; i++)
                {
                    SetFunctionModule(_size - 1 - i, 8, false);
                }
                for (var i = 8; i < 15; i++)
                {
                    SetFunctionModule(8, _size - 15 + i, false);
                }
                SetFunctionModule(8, _size - 8, true);
            }

            internal void DrawCodewords(byte[] data)
            {
                var bitIndex = 0;
                var upward = true;
                for (var right = _size - 1; right >= 1; right -= 2)
                {
                    if (right == 6) right--;
                    for (var vert = 0; vert < _size; vert++)
                    {
                        var y = upward ? _size - 1 - vert : vert;
                        for (var j = 0; j < 2; j++)
                        {
                            var x = right - j;
                            if (_isFunction[x, y]) continue;
                            var bit = bitIndex < data.Length * 8 &&
                                      ((data[bitIndex >> 3] >> (7 - (bitIndex & 7))) & 1) != 0;
                            _modules[x, y] = bit ^ Mask(0, x, y);
                            bitIndex++;
                        }
                    }
                    upward = !upward;
                }
            }

            internal void DrawFormatBits(int mask)
            {
                // Error correction level L has format value 01.
                var data = (1 << 3) | mask;
                var remainder = data << 10;
                for (var i = 14; i >= 10; i--)
                {
                    if (((remainder >> i) & 1) != 0)
                    {
                        remainder ^= 0x537 << (i - 10);
                    }
                }
                var bits = ((data << 10) | remainder) ^ 0x5412;

                for (var i = 0; i <= 5; i++) SetFunctionModule(8, i, GetBit(bits, i));
                SetFunctionModule(8, 7, GetBit(bits, 6));
                SetFunctionModule(8, 8, GetBit(bits, 7));
                SetFunctionModule(7, 8, GetBit(bits, 8));
                for (var i = 9; i < 15; i++) SetFunctionModule(14 - i, 8, GetBit(bits, i));
                for (var i = 0; i < 8; i++) SetFunctionModule(_size - 1 - i, 8, GetBit(bits, i));
                for (var i = 8; i < 15; i++) SetFunctionModule(8, _size - 15 + i, GetBit(bits, i));
                SetFunctionModule(8, _size - 8, true);
            }

            internal Bitmap Render(int scale, int quietZone)
            {
                scale = Math.Max(2, scale);
                var modules = _size + quietZone * 2;
                var bitmap = new Bitmap(modules * scale, modules * scale);
                using (var graphics = Graphics.FromImage(bitmap))
                using (var white = new SolidBrush(Color.White))
                using (var black = new SolidBrush(Color.Black))
                {
                    graphics.SmoothingMode = SmoothingMode.None;
                    graphics.Clear(Color.White);
                    for (var y = 0; y < _size; y++)
                    {
                        for (var x = 0; x < _size; x++)
                        {
                            if (_modules[x, y])
                            {
                                graphics.FillRectangle(
                                    black,
                                    (x + quietZone) * scale,
                                    (y + quietZone) * scale,
                                    scale,
                                    scale);
                            }
                        }
                    }
                }
                return bitmap;
            }

            private void DrawFinderPattern(int centerX, int centerY)
            {
                // Standard QR finder pattern (7x7) plus its 1-module white
                // separator. Drawn as a 9x9 region per the reference algorithm:
                // cells at Chebyshev distance 2 or 4 become the white rings
                // (including the separator), distance 0/1/3 form the 7x7 finder
                // (outer black ring, white ring, 3x3 black center). Every cell
                // is marked as a function module so data placement skips it.
                for (var dy = -4; dy <= 4; dy++)
                {
                    for (var dx = -4; dx <= 4; dx++)
                    {
                        var distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                        SetFunctionModule(centerX + dx, centerY + dy, distance != 2 && distance != 4);
                    }
                }
            }

            private void DrawAlignmentPattern(int centerX, int centerY)
            {
                for (var dy = -2; dy <= 2; dy++)
                {
                    for (var dx = -2; dx <= 2; dx++)
                    {
                        SetFunctionModule(centerX + dx, centerY + dy,
                            Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
                    }
                }
            }

            private void SetFunctionModule(int x, int y, bool dark)
            {
                if (x < 0 || x >= _size || y < 0 || y >= _size) return;
                _modules[x, y] = dark;
                _isFunction[x, y] = true;
            }

            private static bool GetBit(int value, int index)
            {
                return ((value >> index) & 1) != 0;
            }

            private static bool Mask(int mask, int x, int y)
            {
                return mask switch
                {
                    0 => ((x + y) & 1) == 0,
                    1 => (y & 1) == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => ((x / 3) + (y / 2)) % 2 == 0,
                    5 => (x * y) % 2 + (x * y) % 3 == 0,
                    6 => ((x * y) % 2 + (x * y) % 3) % 2 == 0,
                    _ => ((x * y) % 3 + (x + y) % 2) % 2 == 0
                };
            }
        }
    }
}
