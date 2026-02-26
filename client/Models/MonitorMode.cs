namespace client.Models
{
    /// <summary>
    /// 应用监测模式
    /// </summary>
    public enum MonitorMode
    {
        /// <summary>
        /// 仅最上层应用（最前面的窗口）
        /// </summary>
        TopMost,

        /// <summary>
        /// 前台可见应用（所有未最小化的可见窗口）
        /// </summary>
        Foreground,

        /// <summary>
        /// 所有应用（所有拥有窗口的进程，包括最小化的）
        /// </summary>
        All
    }
}
