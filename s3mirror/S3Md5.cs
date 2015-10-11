using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace s3mirror
{
    public static class S3Md5
    {
        public static string Calculate(string fn, int chunkSize)
        {
            List<byte[]> chunkHashes = new List<byte[]>();


            using (var s = File.OpenRead(fn))
            {
                var l = s.Length;

                while (l > 0)
                {
                    if (l >= chunkSize)
                    {
                        var hash = Md5Hash.Calculate(s, chunkSize);
                        chunkHashes.Add(hash);
                        l -= chunkSize;
                    }
                    else
                    {
                        var hash = Md5Hash.Calculate(s, (int)l);
                        chunkHashes.Add(hash);
                        l -= l;
                    }
                }
                Trace.Assert(s.Position == s.Length, "File not at end.");
            }

            var hexes = chunkHashes.ToArray();

            return Calculate(hexes);
        }

        public static string Calculate(IList<byte[]> parts)
        {
            var largeHash = Combine(parts);

            using (var e = MD5.Create())
            {
                var hashOfCombinedHashes = e.ComputeHash(largeHash, 0, largeHash.Length);
                var hex = hashOfCombinedHashes.ToHex();
                return hex + "-" + parts.Count;
            }
        }

        static byte[] Combine(IList<byte[]> arrays)
        {
            byte[] ret = new byte[arrays.Sum(x => x.Length)];
            int offset = 0;
            foreach (byte[] data in arrays)
            {
                Buffer.BlockCopy(data, 0, ret, offset, data.Length);
                offset += data.Length;
            }
            return ret;
        }
    }
}