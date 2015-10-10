using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using log4net;

namespace s3mirror
{
    internal class Program
    {

        static readonly RegionEndpoint region;
        static readonly string awsAccessKeyId;
        static readonly string awsSecretAccessKey;

        private static readonly ILog Log = LogManager.GetLogger(typeof(Program));
        private static bool ignoreMultipartMd5 = true;
        //private static int chunkSize = 1024 * 1024 * 512; //512MB
        private static bool verifyDisk = true;
        private static bool updateLastModified = true;
        private static bool preallocate = true;
        private static readonly object writeLock = new object();
        const string md5fileName = @"md5.txt";
        private static readonly bool write = true;
        private static readonly string[] excludes = {
            @"\.md5",
            @"Thumbs\.db",
            @"picasa\.ini",
            @"\.DS_Store",
            @"\@eaDir",
            @"\#recycle",
            @"\.wd_tv",
            @"\@eaDir",
            @"\#recycle",
            @"\.ithmb",
            @"  "
            };

        private static string s3ObjectsCache = ".s3cache";

        private static int maxThreads = 1;

        private static bool autoflush = false;

        private static void Main(string[] args)
        {
            log4net.Config.XmlConfigurator.Configure();

            //CopyFrom(args[0], args[1]);
            CopyTo(args[0], args[1]);
        }


        static Item Map(string root, FileInfo src)
        {
            return new Item
            {
                Type = "FileInfo",
                Path = src.FullName,
                Length = src.Length,
                FileInfo = src,
                Relative = src.FullName.Substring(root.Length).Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                ModifiedUtc = src.LastWriteTimeUtc
            };
        }


        private static void CopyTo(string src, string dst)
        {
            var uri = new Uri(dst);
            var dstBucket = uri.Host;
            var dstPath = uri.PathAndQuery.Substring(1);

            Log.InfoFormat("Bucket: {0}, Path: {1}", dstBucket, dstPath);

            var md5File = Path.Combine(src, md5fileName);
            var md5Dictionary = LoadMd5File(md5File);

            var files = FetchFiles(src)
                .Where(x => x.Length > 0)
                .Where(x => !x.Attributes.HasFlag(FileAttributes.Hidden))
                .Where(x => !x.Name.StartsWith("."))
                .Where(x => x.FullName != md5File)
                .Select(x => Map(src, x));

            using (var md5FileWriter = File.AppendText(md5File))
            {
                files = files.Select(x =>
                {
                    Md5Item item;
                    if (!md5Dictionary.TryGetValue(x.Path, out item)
                        || x.Length != item.Length
                        || x.ModifiedUtc != item.ModifiedUtc
                        )
                    {
                        x.Hash = Md5Hash.Calculate(x.Path).ToHex();
                        item = new Md5Item(x.Hash, x.Path, x.Length, x.ModifiedUtc);
                        md5Dictionary[x.Path] = item;
                        lock (md5FileWriter)
                        {
                            md5FileWriter.WriteLine(item);
                        }
                    }
                    x.Hash = item.Md5;
                    return x;
                });
            }

            WriteMd5Dictionary(md5Dictionary, md5File);

            var objects = FetchObjects(dstBucket, dstPath)
                .Select(x => Map(dstPath, x))
                .ToDictionary(x => x.Relative, x => x);

            var items = files
                .Where(x => !objects.ContainsKey(x.Relative) || x.Hash != objects[x.Relative].Hash)
                .ToList();

            Log.InfoFormat("Items to be mirrored: {0}", items.Count);

            var chunkSize = 1024 * 1024 * 8; //8MB
            using (IAmazonS3 client = new AmazonS3Client(awsAccessKeyId, awsSecretAccessKey, region))
                foreach (var item in items)
                {
                    var key = dstPath + item.Relative;

                    Log.DebugFormat("Uploading {0} => {1}", item.Relative, key);

                    if (item.Length < chunkSize)
                    {
                        client.UploadObjectFromFilePath(dstBucket, key, item.Path, null);
                        var isMatch = client.GetObject(dstBucket, key).ETag.Contains(item.Hash);

                        if(!isMatch) Log.ErrorFormat("Upload failed: {0}",item.Relative);
                    }
                    else
                    {
                        var response = client.InitiateMultipartUpload(dstBucket, key);
                        try
                        {

                            var index = 0;

                            var md5s = new List<PartETag>();

                            for (int part = 1; index < item.Length; part++)
                            {
                                var md5 = Md5Hash.Calculate(item.Path, index, chunkSize).ToHex();

                                client.UploadPart(new UploadPartRequest
                                {
                                    Key = key,
                                    BucketName = dstBucket,
                                    FilePath = item.Path,
                                    FilePosition = 0,
                                    PartNumber = part,
                                    PartSize = chunkSize,
                                    UploadId = response.UploadId,
                                    MD5Digest = md5
                                });

                                md5s.Add(new PartETag(part, md5));

                                Log.DebugFormat("\tPart {0} : {1}", part, md5);
                                index += chunkSize;
                            }

                            client.CompleteMultipartUpload(new CompleteMultipartUploadRequest
                            {
                                Key = key,
                                BucketName = dstBucket,
                                PartETags = md5s,
                                UploadId = response.UploadId,
                                
                            });

                            Log.DebugFormat("\ts3md5: {0}", S3Md5.Calculate(item.Path, chunkSize));
                        }
                        catch (Exception ex)
                        {
                            Log.Error(item.Relative, ex);
                            client.AbortMultipartUpload(dstBucket, key, response.UploadId);
                        }
                    }
                }

        }


        private static void WriteMd5Dictionary(Dictionary<string, Md5Item> md5Dictionary, string md5File)
        {
            using (var w = File.CreateText(md5File))
            {
                foreach (var i in md5Dictionary)
                {
                    w.WriteLine(i.Value);
                }
            }
        }

        private static Item Map(string root, S3Object src)
        {
            return new Item
            {
                Type = "S3Object",
                Path = src.Key,
                Length = src.Size,
                S3Object = src,
                Relative = src.Key.Substring(root.Length),
                Hash = src.ETag.Replace("\"", string.Empty)
            };
        }

        private static void CopyFrom(string src, string dst)
        {
            var uri = new Uri(src);
            var bucket = uri.Host;
            var srcPath = uri.PathAndQuery.Substring(1);

            Directory.CreateDirectory(dst);

            var md5file = dst + md5fileName;

            var md5Dictionary = md5(dst, md5file);

            Log.Info("Fetching S3 objects");

            // TODO: Read s3 object cache
            var objects = FetchObjects(bucket, srcPath)
                .OrderBy(x => x.Size)
                .ToList();

            // TODO: Write s3 object cache
            Log.InfoFormat("S3 files: {0:N0}", objects.Count);

            var d = Convert(srcPath, dst, objects);
            d = FilterExcludes(d);
            d = FilterEqualMD5(d, md5Dictionary);


            //foreach (var o in d)
            Parallel.ForEach(d, new ParallelOptions { MaxDegreeOfParallelism = maxThreads }, o =>
            {
                if (o.Item1.Size == 0)
                {
                    Log.WarnFormat("S3 Object '{0}' has 0 length.", o.Item1.Key);
                    return;
                }

                Log.DebugFormat("Downloading: {0} => {1} (size: {2:N0})", o.Item1.Key, o.Item2, o.Item1.Size);

                if (write)
                {
                    var streamHash = DownloadAndHash(bucket, o.Item1.Key, o.Item2, o.Item1.Size).ToHex();

                    if (!o.Item1.ETag.Contains(streamHash))
                    {
                        Log.ErrorFormat("Etag MD5 ({1}) not equal to object stream ({2}) for object '{0}'.", o.Item1.Key,
                            o.Item1.ETag, streamHash);
                    }

                    if (verifyDisk)
                    {
                        var diskHash = Md5Hash.Calculate(o.Item2).ToHex();
                        if (!o.Item1.ETag.Contains(diskHash))
                        {
                            Log.ErrorFormat("Etag MD5 ({1}) not equal to object stream ({2}) for object '{0}'.",
                                o.Item1.Key, o.Item1.ETag, diskHash);
                        }
                    }

                    var fi = new FileInfo(o.Item2);

                    if (updateLastModified)
                    {
                        File.SetLastWriteTimeUtc(o.Item2, o.Item1.LastModified.ToUniversalTime());
                    }

                    lock (writeLock)
                    {
                        File.AppendAllText(md5file,
                            new Md5Item(streamHash, o.Item2, fi.Length, File.GetLastWriteTimeUtc(o.Item2)) +
                            Environment.NewLine);
                    }
                }
            }
            );


            // TODO: Remove destination files that are not in source

            //ReportDuplicates(md5Dictionary);
        }

        private static IEnumerable<Tuple<S3Object, string, string>> FilterExcludes(IEnumerable<Tuple<S3Object, string, string>> d)
        {
            foreach (var i in d)
            {
                var hasMatch = excludes.Any(x => Regex.IsMatch(i.Item2, x));
                if (hasMatch)
                {
                    Log.DebugFormat("Excluding: {0}", i.Item2);
                    continue;
                }

                yield return i;
            }
        }

        private static byte[] DownloadAndHash(string bucket, string key, string dst, long size)
        {
            using (IAmazonS3 client = new AmazonS3Client(awsAccessKeyId, awsSecretAccessKey, region))
            {
                GetObjectRequest request = new GetObjectRequest
                {
                    BucketName = bucket,
                    Key = key
                };

                using (var fs = File.OpenWrite(dst))
                {
                    if (preallocate)
                    {
                        fs.SetLength(size);
                    }
                    using (GetObjectResponse response = client.GetObject(request))
                    using (Stream responseStream = response.ResponseStream)
                    {
                        using (var e = MD5.Create())
                        using (CryptoStream cs = new CryptoStream(fs, e, CryptoStreamMode.Write))
                        {
                            responseStream.CopyTo(cs);
                            // Write data here
                            cs.FlushFinalBlock();

                            if (fs.Position != size)
                            {
                                Log.ErrorFormat("Stream position {0} unequal to size {1}.", fs.Position, size);
                            }
                            return e.Hash;
                        }
                    }
                }
            }
        }

        // s3cmd sync --verbose --cache-file s3cmd-md5-cache.txt --exclude '@eaDir/*' --exclude 'Thumbs.db' --exclude '.DS_Store/*'  s3://com.ramonsmits.synology/diskstation/photo/ /Volumes/photo/


        static IEnumerable<S3Object> FetchObjects(string bucketName, string prefix)
        {
            // Set to a bucket you create            
            // Create S3 service client.      

            //var fn = "s3.txt";

            var start = Stopwatch.StartNew();
            using (IAmazonS3 client = new AmazonS3Client(awsAccessKeyId, awsSecretAccessKey, region))
            {
                //using (var f = File.OpenWrite(fn))
                //using (var w = new StreamWriter(f, Encoding.UTF8))
                {
                    ListObjectsRequest request = new ListObjectsRequest();
                    request.BucketName = bucketName;
                    request.Prefix = prefix;
                    do
                    {
                        ListObjectsResponse response = client.ListObjects(request);

                        foreach (var o in response.S3Objects)
                        {
                            //    w.WriteLine("{0};{1};{2:s};{3}", o.Key, o.ETag, o.LastModified, o.Size);
                            yield return o;
                        }

                        //Console.Write(".");

                        // If response is truncated, set the marker to get the next 
                        // set of keys.
                        if (response.IsTruncated)
                        {
                            request.Marker = response.NextMarker;
                        }
                        else
                        {
                            request = null;
                        }
                    } while (request != null);

                    //w.Close();
                    //f.Close();
                }
            }

            Log.DebugFormat("Duration: {0}", start.ElapsedMilliseconds);
        }

        static readonly Encoding encoding = Encoding.UTF8;

        static IEnumerable<FileInfo> FetchFiles(string path)
        {
            foreach (var fn in Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories))
            {
                yield return new FileInfo(fn);
            }
        }

        static Dictionary<string, Md5Item> md5(string src, string md5file)
        {
            //var src = @"\\nas.smigo.nl\video\";


            //var files = Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories);
            //var dst = Path.GetTempPath(); // Ends with '\'
            var srcDir = new DirectoryInfo(src);

            Log.Info("Getting file list...");
            var filesUnordered = srcDir
                .GetFiles("*.*", SearchOption.AllDirectories);

            var totalSize = filesUnordered.Sum(x => x.Length);
            var fileCount = filesUnordered.Length;
            Log.InfoFormat("Done {0:N0} files, total size {1:N0}", fileCount, totalSize);

            Log.InfoFormat("Ordering file list size descending...");
            var files = filesUnordered
                //.OrderBy(x => x.Length);
                .OrderBy(x => x.FullName);

            var md5Dictionary = LoadMd5File(md5file);

            Log.InfoFormat("Getting file list...");

            var isRelative = false;

            var i = 0;
            var size = 0L;
            using (var md5sumFile = File.Open(md5file, FileMode.Append, FileAccess.Write, FileShare.Read))
            using (var w = new StreamWriter(md5sumFile, encoding))
            {
                w.AutoFlush = autoflush;

                foreach (var fi in files)
                {
                    ++i;
                    size += fi.Length;

                    if (i % 1000 == 0)
                    {
                        Console.Title = string.Format("{0:N0}/{1:N0} ({2}%) {3}/{4} ({5}%)", i, fileCount, i * 100 / fileCount, Pretty(size), Pretty(totalSize), size * 100 / totalSize);
                    }

                    var f = fi.FullName;

                    var path = isRelative ? f.Substring(src.Length + 1) : f;

                    if (excludes.Any(exclude => Regex.IsMatch(path, exclude)))
                    {
                        continue;
                    }

                    if (path.EndsWith(md5fileName)) continue;

                    if (fi.Length == 0)
                    {
                        Log.WarnFormat("Length is 0: {0}", path);
                        continue;
                    }
                    if (md5Dictionary.ContainsKey(path)) continue;

                    if (path.Contains(Md5Item.Seperator))
                    {
                        Log.WarnFormat("Error seperator: {0}", path);
                        continue;
                    }

                    Log.DebugFormat("Processing '{0}' with size {1:N0}...", path, fi.Length);

                    var s = Stopwatch.StartNew();
                    var md5 = Md5Hash.Calculate(f);

                    var md5hex = md5.ToHex();

                    w.WriteLine(new Md5Item(md5hex, path, fi.Length, fi.LastWriteTimeUtc));

                    Log.DebugFormat("Done, took {0:g}", s.Elapsed);
                }
                w.Close();
                md5sumFile.Close();

            }

            return md5Dictionary;
        }

        private static Dictionary<string, Md5Item> LoadMd5File(string md5file)
        {
            Log.InfoFormat("Reading md5 file '{0}'...", md5file);

            var md5Dictionary = new Dictionary<string, Md5Item>();

            if (File.Exists(md5file))
            {
                var md5lines = File.ReadAllLines(md5file, encoding);

                foreach (var l in md5lines)
                {
                    var item = Md5Item.Parse(l);
                    md5Dictionary[item.Key] = item;
                }
            }

            Log.InfoFormat("Done, {0:N0} items", md5Dictionary.Count);
            return md5Dictionary;
        }

        public static void ReportDuplicates(IDictionary<string, string[]> md5Dictionary)
        {
            var dupes = md5Dictionary
                .Values
                .GroupBy(x => x[0])
                .Where(x => x.Count() > 1)
                .ToList();

            foreach (var dupe in dupes)
            {
                Console.WriteLine("Possible duplicates");
                foreach (var f in dupe)
                {
                    Console.WriteLine("\tsize: {1,6} {0}", f[1], Pretty(int.Parse(f[2])));
                }
            }
        }

        private static string Pretty(long size)
        {
            var s = new StringBuilder(25);
            Win32.StrFormatByteSize(size, s, 25);
            return s.ToString();
        }

        /// <summary>
        /// Creates a relative path from one file or folder to another.
        /// </summary>
        /// <param name="fromPath">Contains the directory that defines the start of the relative path.</param>
        /// <param name="toPath">Contains the path that defines the endpoint of the relative path.</param>
        /// <returns>The relative path from the start directory to the end path or <c>toPath</c> if the paths are not related.</returns>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="UriFormatException"></exception>
        /// <exception cref="InvalidOperationException"></exception>
        public static String MakeRelativePath(String fromPath, String toPath)
        {
            if (String.IsNullOrEmpty(fromPath)) throw new ArgumentNullException("fromPath");
            if (String.IsNullOrEmpty(toPath)) throw new ArgumentNullException("toPath");

            Uri fromUri = new Uri(fromPath);
            Uri toUri = new Uri(toPath);

            if (fromUri.Scheme != toUri.Scheme) { return toPath; } // path can't be made relative.

            Uri relativeUri = fromUri.MakeRelativeUri(toUri);
            String relativePath = Uri.UnescapeDataString(relativeUri.ToString());

            if (toUri.Scheme.ToUpperInvariant() == "FILE")
            {
                relativePath = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            }

            return relativePath;
        }

        private static IEnumerable<Tuple<S3Object, string, string>> Convert(
            string srcPath, // photo/
            string dstPath, // \\nas.smigo.nl\video\
            IEnumerable<S3Object> objects
            )
        {
            foreach (var o in objects)
            {
                // photo/s3/img.jpg => s3/img.jpg
                var src = o.Key.Substring(srcPath.Length);

                // s3/img.jpg => s3\img.jpg
                var relative = src.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

                // \\nas.smigo.nl\photo\ + s3\img.pjg => \\nas.smigo.nl\photo\s3\img.jpg
                var dst = dstPath + relative;

                yield return Tuple.Create(o, dst, relative);
            }
        }

        static int GuessChunkSize(long size, int parts)
        {
            var partSize = size / parts;

            for (int chunkSize = 1024 * 1024; ; chunkSize *= 2)
            {
                // 100 = 100, 200, 300, 400, 410 = 82
                // 50
                // 200 

                if (chunkSize > partSize)

                    return chunkSize;
            }
        }

        static IEnumerable<Tuple<S3Object, string, string>> FilterEqualMD5(
            IEnumerable<Tuple<S3Object, string, string>> objects,
            IDictionary<string, Md5Item> hashes
            )
        {
            foreach (var o in objects)
            {
                Md5Item item;

                var hashExists = hashes.TryGetValue(o.Item2, out item);
                if (hashExists)
                {
                    if (o.Item1.Size != item.Length)
                    {
                        yield return o;
                        continue;
                    }
                    var isMultipart = o.Item1.ETag.Length > 34;// md5 hex length = 32 + quotes

                    if (isMultipart)
                    {

                        var etag = o.Item1.ETag;

                        etag = etag.Substring(1, etag.Length - 2);
                        etag = etag.Substring(33);
                        var chunks = int.Parse(etag);

                        var chunkSize = GuessChunkSize(o.Item1.Size, chunks);

                        Log.InfoFormat("Calculating s3md5 checksum with chunk size {0} : {1}", Pretty(chunkSize), o.Item3);

                        var s3md5hash = S3Md5.Calculate(o.Item2, chunkSize);

                        if (o.Item1.ETag.Contains(s3md5hash))
                        {
                            Log.DebugFormat("Multipart object '{0}' is equal.", o.Item3);
                            continue;
                        }
                        else
                        {
                            Log.WarnFormat("Multipart object '{0}' is NOT equal.", o.Item3);

                            if (!ignoreMultipartMd5)
                                yield return o;
                            continue;
                        }
                    }

                    var sameHash = o.Item1.ETag.Replace("\"", "") == item.Md5;
                    if (sameHash)
                    {
                        continue;
                        // download
                    }

                    Log.ErrorFormat("Hashes unequal for '{0}' (src:{1}, dst:{2})", o.Item3, o.Item1.ETag, item.Md5);
                }

                if (File.Exists(o.Item2))
                {
                    var fi = new FileInfo(o.Item2);
                    if (o.Item1.Size != fi.Length)
                    {
                        Log.ErrorFormat("File size unequal for '{0}' (src:{1}, dst:{2}, diff: {3})", o.Item3, o.Item1.Size,
                            fi.Length, o.Item1.Size - fi.Length);
                    }
                }
                else
                {
                    Log.DebugFormat("Destination does not exists: {0}", o.Item2);
                }

                yield return o;
            }
        }

    }
}
