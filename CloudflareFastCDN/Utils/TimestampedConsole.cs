using CloudflareFastCDN.Web;
using System.Text;

namespace CloudflareFastCDN.Utils
{
    internal static class TimestampedConsole
    {
        public const string TimestampFormatPattern = "yyyy-MM-dd HH:mm:ss.fff zzz";

        private static TimeZoneInfo _timeZone = TimeZoneInfo.Local;

        public static string EffectiveTimeZoneId => _timeZone.Id;

        public static string? RequestedTimeZoneId { get; private set; }

        private static bool RequestedTimeZoneApplied { get; set; }

        public static DateTimeOffset Now => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _timeZone);

        public static void Configure(bool isDocker)
        {
            var standardOutput = Console.Out;
            var standardError = Console.Error;

            ResolveTimeZone(isDocker);

            Console.SetOut(new TimestampTextWriter(standardOutput, () => Now, LogBuffer.Append));
            Console.SetError(new TimestampTextWriter(standardError, () => Now, LogBuffer.Append));

            if (isDocker &&
                !string.IsNullOrWhiteSpace(RequestedTimeZoneId) &&
                !RequestedTimeZoneApplied)
            {
                Console.WriteLine($"Docker 环境下无法识别 TZ={RequestedTimeZoneId}，日志时区回退为 {EffectiveTimeZoneId}");
            }
        }

        private static void ResolveTimeZone(bool isDocker)
        {
            RequestedTimeZoneId = isDocker ? Environment.GetEnvironmentVariable("TZ")?.Trim() : null;
            if (string.IsNullOrWhiteSpace(RequestedTimeZoneId))
            {
                _timeZone = TimeZoneInfo.Local;
                RequestedTimeZoneApplied = false;
                return;
            }

            try
            {
                _timeZone = TimeZoneInfo.FindSystemTimeZoneById(RequestedTimeZoneId);
                RequestedTimeZoneApplied = true;
            }
            catch (TimeZoneNotFoundException)
            {
                _timeZone = TimeZoneInfo.Local;
                RequestedTimeZoneApplied = false;
            }
            catch (InvalidTimeZoneException)
            {
                _timeZone = TimeZoneInfo.Local;
                RequestedTimeZoneApplied = false;
            }
        }
    }

    internal sealed class TimestampTextWriter : TextWriter
    {
        private readonly TextWriter _innerWriter;
        private readonly Func<DateTimeOffset> _timestampProvider;
        private readonly Action<DateTimeOffset, string>? _lineSink;
        private readonly StringBuilder _currentLine = new();
        private readonly object _syncRoot = new();
        private DateTimeOffset _currentLineTime;
        private bool _isLineStart = true;

        public TimestampTextWriter(TextWriter innerWriter, Func<DateTimeOffset> timestampProvider, Action<DateTimeOffset, string>? lineSink = null)
        {
            _innerWriter = innerWriter;
            _timestampProvider = timestampProvider;
            _lineSink = lineSink;
        }

        public override Encoding Encoding => _innerWriter.Encoding;

        public override IFormatProvider FormatProvider => _innerWriter.FormatProvider;

        public override void Write(char value)
        {
            lock (_syncRoot)
            {
                WriteCore(value);
            }
        }

        public override void Write(string? value)
        {
            if (value == null)
            {
                return;
            }

            lock (_syncRoot)
            {
                foreach (var character in value)
                {
                    WriteCore(character);
                }
            }
        }

        public override void Write(char[] buffer, int index, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);

            lock (_syncRoot)
            {
                for (int i = index; i < index + count; i++)
                {
                    WriteCore(buffer[i]);
                }
            }
        }

        public override void WriteLine()
        {
            lock (_syncRoot)
            {
                EnsureTimestampPrefix();
                _innerWriter.Write(NewLine);
                _innerWriter.Flush();
                _isLineStart = true;
                CompleteLine();
            }
        }

        public override void WriteLine(string? value)
        {
            lock (_syncRoot)
            {
                if (value != null)
                {
                    foreach (var character in value)
                    {
                        WriteCore(character);
                    }
                }

                EnsureTimestampPrefix();
                _innerWriter.Write(NewLine);
                _innerWriter.Flush();
                _isLineStart = true;
                CompleteLine();
            }
        }

        public override void Flush()
        {
            lock (_syncRoot)
            {
                _innerWriter.Flush();
            }
        }

        private void WriteCore(char value)
        {
            EnsureTimestampPrefix();
            _innerWriter.Write(value);

            if (value == '\n')
            {
                _innerWriter.Flush();
                _isLineStart = true;
                CompleteLine();
            }
            else if (value != '\r')
            {
                _isLineStart = false;
                _currentLine.Append(value);
            }
        }

        private void CompleteLine()
        {
            _lineSink?.Invoke(_currentLineTime, _currentLine.ToString());
            _currentLine.Clear();
        }

        private void EnsureTimestampPrefix()
        {
            if (!_isLineStart)
            {
                return;
            }

            _currentLineTime = _timestampProvider();
            _innerWriter.Write($"[{_currentLineTime.ToString(TimestampedConsole.TimestampFormatPattern)}] ");
            _isLineStart = false;
        }
    }
}
