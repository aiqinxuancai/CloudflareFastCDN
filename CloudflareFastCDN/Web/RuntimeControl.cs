namespace CloudflareFastCDN.Web
{
    internal enum ManualCommand
    {
        None,
        Revalidate,
        FullSelection
    }

    internal readonly record struct ManualRequest(ManualCommand Command, string Trigger)
    {
        public static ManualRequest None { get; } = new(ManualCommand.None, string.Empty);
    }

    /// <summary>
    /// Web 管理页面向主循环发送手动指令；主循环在等待期间通过 <see cref="WaitAsync"/> 响应。
    /// </summary>
    internal static class RuntimeControl
    {
        private static readonly object SyncRoot = new();
        private static readonly SemaphoreSlim Signal = new(0, 1);
        private static ManualRequest _pending = ManualRequest.None;

        public static ManualCommand Pending
        {
            get
            {
                lock (SyncRoot)
                {
                    return _pending.Command;
                }
            }
        }

        public static ActionResponse RequestFullSelection()
        {
            lock (SyncRoot)
            {
                var phase = DashboardState.CurrentPhase;
                if (phase == DashboardPhase.NotConfigured)
                {
                    return new ActionResponse(false, "尚未完成DNS配置，请先在设置页面填写DNS服务商凭证和域名");
                }

                if (phase == DashboardPhase.Selecting)
                {
                    return new ActionResponse(false, "正在执行全量优选，请等待本轮完成");
                }

                if (_pending.Command == ManualCommand.FullSelection)
                {
                    return new ActionResponse(false, "已有待执行的立即优选请求");
                }

                SetPendingLocked(new ManualRequest(ManualCommand.FullSelection, "手动触发"));
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
                if (phase == DashboardPhase.NotConfigured)
                {
                    return new ActionResponse(false, "尚未完成DNS配置，请先在设置页面填写DNS服务商凭证和域名");
                }

                if (phase is DashboardPhase.Selecting or DashboardPhase.Revalidating)
                {
                    return new ActionResponse(false, "当前正在优选或复检，请稍后再试");
                }

                if (_pending.Command != ManualCommand.None)
                {
                    return new ActionResponse(false, "已有待执行的手动请求");
                }

                SetPendingLocked(new ManualRequest(ManualCommand.Revalidate, "手动复检"));
                Console.WriteLine("收到Web管理页面的立即复检请求");
                return new ActionResponse(true, phase == DashboardPhase.Waiting
                    ? "已开始复检已分配IP"
                    : "已排队，当前任务完成后立即复检");
            }
        }

        /// <summary>
        /// 由程序内部安排一次全量优选（例如DNS设置变更），正在进行的优选结束后也会再执行一次。
        /// 尚未完成配置时不排队，配置完成后主循环会自行开始。
        /// </summary>
        public static bool QueueFullSelection(string trigger)
        {
            lock (SyncRoot)
            {
                if (DashboardState.CurrentPhase == DashboardPhase.NotConfigured)
                {
                    return false;
                }

                SetPendingLocked(new ManualRequest(ManualCommand.FullSelection, trigger));
                Console.WriteLine($"已安排一次全量优选，原因：{trigger}");
                return true;
            }
        }

        /// <summary>
        /// 等待指定时长，期间收到手动指令则提前返回该指令；超时返回 <see cref="ManualRequest.None"/>。
        /// </summary>
        public static async Task<ManualRequest> WaitAsync(TimeSpan delay)
        {
            var deadline = DateTimeOffset.UtcNow + delay;
            while (true)
            {
                var request = TakePending();
                if (request.Command != ManualCommand.None)
                {
                    return request;
                }

                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    return ManualRequest.None;
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
                _pending = ManualRequest.None;
            }
        }

        /// <summary>
        /// 复检开始时调用，排队中的复检请求由本次复检满足，立即优选请求保留。
        /// </summary>
        public static void ClearPendingRevalidation()
        {
            lock (SyncRoot)
            {
                if (_pending.Command == ManualCommand.Revalidate)
                {
                    _pending = ManualRequest.None;
                }
            }
        }

        private static ManualRequest TakePending()
        {
            lock (SyncRoot)
            {
                var request = _pending;
                _pending = ManualRequest.None;
                return request;
            }
        }

        private static void SetPendingLocked(ManualRequest request)
        {
            _pending = request;
            if (Signal.CurrentCount == 0)
            {
                Signal.Release();
            }
        }
    }

    internal sealed record ActionResponse(bool Accepted, string Message);
}
