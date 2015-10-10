using System;
using System.Globalization;

namespace s3mirror
{
    public class Md5Item
    {
        public const string Seperator = "  ";

        public Md5Item(string md5hex, string path, long length, DateTime modifiedUtc)
        {
            Md5 = md5hex;
            Key = path;
            Length = length;
            ModifiedUtc = modifiedUtc;
        }
        public string Md5 { get; set; }
        public string Key { get; set; }
        public long Length { get; set; }
        public DateTime ModifiedUtc { get; set; }

        public override string ToString()
        {
            return Md5Line(this);
        }

        public static string Md5Line(Md5Item obj)
        {
            return string.Format(
                "{1}{0}{2}{0}{3}{0}{4}",
                Seperator,
                obj.Md5,
                obj.Key,
                obj.Length,
                obj.ModifiedUtc.ToEpoch()
                );
        }


        internal static Md5Item Parse(string l)
        {
            var items = l.Split(new[] { Seperator }, StringSplitOptions.None);
            return new Md5Item(
                items[0],
                items[1],
                Int64.Parse(items[2], CultureInfo.InvariantCulture),
                long.Parse(items[3], CultureInfo.InvariantCulture).FromEpoch()
                );
        }
    }
}