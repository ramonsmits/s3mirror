using System;
using System.IO;
using Amazon.S3.Model;

namespace s3mirror
{
    public class Item
    {
        public string Type { get; set; }
        public string Path { get; set; }
        public string Relative { get; set; }

        public long Length { get; set; }

        public FileInfo FileInfo { get; set; }

        public S3Object S3Object { get; set; }

        public string Hash { get; set; }

        public override string ToString()
        {
            return Path;
        }

        public DateTime ModifiedUtc { get; set; }
    }
}