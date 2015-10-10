namespace s3mirror
{
    public static class ByteArrayExtensions
    {
        public static string ToHex(this byte[] bytes, bool upperCase = false)
        {
            char aIndex = upperCase ? 'A' : 'a';
            char[] c = new char[bytes.Length * 2];

            byte b;

            for (int bx = 0, cx = 0; bx < bytes.Length; ++bx, ++cx)
            {
                b = ((byte)(bytes[bx] >> 4));
                c[cx] = (char)(b > 9 ? b - 10 + aIndex : b + '0');

                b = ((byte)(bytes[bx] & 0x0F));
                c[++cx] = (char)(b > 9 ? b - 10 + aIndex : b + '0');
            }

            return new string(c);
        }

    }
}