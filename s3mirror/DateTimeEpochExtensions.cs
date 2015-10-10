using System;

namespace s3mirror
{
    public static class DateTimeEpochExtensions
    {
        static DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static long ToEpoch(this DateTime dateTime)
        {
            var utc = dateTime.ToUniversalTime();
            var diff = (utc - epoch).TotalSeconds;

            return (long)diff;
        }

        public static DateTime FromEpoch(this long timestamp)
        {
            return epoch.AddSeconds(timestamp);
        }
    }
}