namespace CloudflareFastCDN.Web
{
    internal enum ManualCommand
    {
        None,
        Revalidate,
        FullSelection
    }

    /// <summary>
    /// Web 管理页面向主循环发送手动指令；主循环在等待期间通过 <see cref="WaitAsync"/> 响应。
    /// </summary>
    internal static class RuntimeControl
    {
        private static readonly object SyncRoot = new();
        private static readonly SemaphoreSlim Signal = new(0, 1);
        private static ManualCommand _pending = ManualCommand.None;

        public static ManualCommand Pending
        {
            get
            {
                lock (SyncRoot)
                {
                    return _pending;
                }
            }
        }

        public static ActionResponse RequestFullSelection()
        {
            lock (SyncRoot)
            {
                var phase = DashboardState.CurrentPhase;
                if (phase == DashboardPhase.Selecting)
                {
                    return new ActionResponse(false, "正在执行全量优选，请等待本轮完成");
                }

                if (_pending == ManualCommand.FullSelection)
                {
                    return new ActionResponse(false, "已有待执行的立即优选请求");
                }

                _pending = ManualCommand.FullSelection;
                SignalLocked();
                Console.WriteLine("收到Web管理页面的立即优选请求");
                return new ActionResponse(true, phase == DashboardPhase.Waiting
                    ? "已开始立即优选"
                    : "已排队，当前任务完成后立即开始优选");
            }
        }

        public static ActionResponse RequestRevalidation()
        {
            lock (SyncRoot)
            {
                var phase = DashboardState.CurrentPhase;
                if (phase is DashboardPhase.Selecting or DashboardPhase.Revalidating)
                {
                    return new ActionResponse(false, "当前正在优选或复检，请稍后再试");
                }

                if (_pending != ManualCommand.None)
                {
                    return new ActionResponse(false, "已有待执行的手动请求");
                }

                _pending = ManualCommand.Revalidate;
                SignalLocked();
                Console.WriteLine("收到Web管理页面的立即复检请求");
                return new ActionResponse(true, phase == DashboardPhase.Waiting
                    ? "已开始复检已分配IP"
                    : "已排队，当前任务完成后立即复检");
            }
        }

        /// <summary>
        /// 等待指定时长，期间收到手动指令则提前返回该指令；超时返回 <see cref="ManualCommand.None"/>。
        /// </summary>
        public static async Task<ManualCommand> WaitAsync(TimeSpan delay)
        {
            var deadline = DateTimeOffset.UtcNow + delay;
            while (true)
            {
                var command = TakePending();
                if (command != ManualCommand.None)
                {
                    return command;
                }

                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    return ManualCommand.None;
                }

                // 信号可能来自已被消费的旧指令，循环重新检查即可
                await Signal.WaitAsync(remaining);
            }
        }

        /// <summary>
        /// 全量优选开始时调用，所有排队的指令都已被本次优选覆盖。
        /// </summary>
        public static void ClearPending()
        {
            lock (SyncRoot)
            {
                _pending = ManualCommand.None;
            }
        }

        /// <summary>
        /// 复检开始时调用，排队中的复检请求由本次复检满足，立即优选请求保留。
        /// </summary>
        public static void ClearPendingRevalidation()
        {
            lock (SyncRoot)
            {
                if (_pending == ManualCommand.Revalidate)
                {
                    _pending = ManualCommand.None;
                }
            }
        }

        private static ManualCommand TakePending()
        {
            lock (SyncRoot)
            {
                var command = _pending;
                _pending = ManualCommand.None;
                return command;
            }
        }

        private static void SignalLocked()
        {
            if (Signal.CurrentCount == 0)
            {
                Signal.Release();
            }
        }
    }

    internal sealed record ActionResponse(bool Accepted, string Message);
}
