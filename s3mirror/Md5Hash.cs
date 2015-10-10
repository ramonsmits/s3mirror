using System.IO;
using System.Security.Cryptography;

namespace s3mirror
{
    public static class Md5Hash
    {
        public static byte[] Calculate(string f)
        {
            using (var e = System.Security.Cryptography.MD5.Create())
            using (var s = File.OpenRead(f))
            {
                var hash = e.ComputeHash(s);
                return hash;
            }
        }

        public static byte[] Calculate(string fn, long position, int count)
        {
            using (var s = File.OpenRead(fn))
            {
                s.Position = position;
                return Calculate(s, count);
            }
        }

        public static byte[] Calculate(Stream stream, int count)
        {
            int offset = 0;
            int bufferSize = 4096 > count ? count : 4096;
            byte[] buffer = new byte[bufferSize];

            using (var e = MD5.Create())
            {
                while (offset < count)
                {
                    var chunkSize = count >= bufferSize ? bufferSize : count;
                    int read = stream.Read(buffer, 0, chunkSize);
                    if (read == 0) break;
                    offset += read;
                    e.TransformBlock(buffer, 0, read, null, 0);
                }

                e.TransformFinalBlock(buffer, 0, 0);
                return e.Hash;
            }
        }
    }
}