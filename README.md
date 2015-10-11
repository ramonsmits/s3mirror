# s3mirror
Tool to sync to/from Amazon S3 including verification (Work in progress)

# Background

I started this as tool to restore my Synology Amazon S3 back-up which failed on my new Synology with the helpful message *Restored failed*. I then found the **s3cmd** tool and quickly was trying to restore my S3 files but it was *slow*. The file indexing and md5 hash calculations to prevent downloading of files that are already available on the destination disk. Primarily because s3cmd does not immediately flushes the hash to disk and indexing 45.000 files with s3cmd took ages. Then it also seems to not really support multi-part hash comparison which was needed as I had lots of large files that were download again and again because the md5 hash did not match. Then the last reason was that Synology did not always use the same *chunk size*. I added a chunk size guess routine based powers of two and combined with the s3 md5 hash multi-part this resulted in being able to correctly compare files and preventing excessive AWS ingress/exgress traffic.
I now had a 'tool' to restore my back-up and it works beautifull and fast!


# Goal

However, I lost faith in the Synology S3 backup and restore engine so I now set my mind on implementing fast and efficient upload mirroring too.

The whole source code is just a large sum of quick hacks just to get it working. I'm now busy refactoring the code and the target of version 1.0 will be to support mirroring to/from AWS S3 via the commandline where a lot of code for both operations will be shared. With shared I mean the indexing of files on disk and that in S3 and the routine to skip files already in the destination and last the excluding of files and folders.
