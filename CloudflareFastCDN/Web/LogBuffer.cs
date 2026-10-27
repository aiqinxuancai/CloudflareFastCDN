namespace CloudflareFastCDN.Web
{
    internal static class LogBuffer
    {
        private const int Capacity = 3000;
        private const string TimeFormat = "yyyy-MM-dd HH:mm:ss.fff";

        private static readonly object SyncRoot = new();
        private static readonly LogLine[] Lines = new LogLine[Capacity];
        private static long _lastSeq;

        public static void Append(DateTimeOffset time, string text)
        {
            lock (SyncRoot)
            {
                _lastSeq++;
                Lines[_lastSeq % Capacity] = new LogLine(_lastSeq, time.ToString(TimeFormat), text);
            }
        }

        public static LogPage Read(long after, int limit)
        {
            lock (SyncRoot)
            {
                // 进程重启后序号会从头开始，客户端需要整体重置
                var reset = after > _lastSeq;
                if (reset)
                {
                    after = 0;
                }

                var firstAvailable = Math.Max(1, _lastSeq - Capacity + 1);
                var start = Math.Max(after + 1, firstAvailable);
                if (_lastSeq - start + 1 > limit)
                {
                    start = _lastSeq - limit + 1;
                }

                var lines = new List<LogLine>((int)Math.Max(0, _lastSeq - start + 1));
                for (long seq = start; seq <= _lastSeq; seq++)
                {
                    lines.Add(Lines[seq % Capacity]);
                }

                return new LogPage(lines, _lastSeq, reset);
            }
        }
    }

    internal sealed record LogLine(long Seq, string Time, string Text);

    internal sealed record LogPage(List<LogLine> Lines, long LastSeq, bool Reset);
}
