namespace AutomatedLine.Domain.Tasks;

/// <summary>
/// 天车搬运状态机的步骤。
/// 严格对应架构文档 §8.5：调度器派发时**只判断**路权/预约可用，**真正获取在状态机执行任务时**，
/// 因此第一个实际步骤是 <see cref="AcquireResources"/>（原子 TryAcquire 路权 + TryReserve 预约，拿不到回滚）。
/// 步骤是"机械动作分步"的游标，Worker 每周期只推进一步或几步，绝不阻塞。
/// </summary>
public enum TransportStep
{
    /// <summary>空闲，无任务。</summary>
    Idle = 0,

    /// <summary>
    /// 获取路权 + 设备预约（**真正加锁**，调度器派发时只判断）。
    /// 先 TryAcquire 路权，再 TryReserve 源/目标；任一步失败 → 回滚（释放已占用、任务回 Waiting、天车让出）。
    /// </summary>
    AcquireResources = 15,

    /// <summary>预检查：连接、报警、范围。</summary>
    PreCheck = 1,

    /// <summary>二次确认源设备状态，避免派发后现场已变化。</summary>
    ConfirmSource = 2,

    /// <summary>移动到源设备。</summary>
    MoveToSource = 3,

    /// <summary>取料握手：下发取料请求，等待设备允许。</summary>
    PickHandshake = 4,

    /// <summary>取料确认：等待"天车有料 + 设备无料"等物理信号成立。</summary>
    PickConfirmed = 5,

    /// <summary>取料后回安全高度。</summary>
    RetractZAfterPick = 6,

    /// <summary>移动到目标设备。</summary>
    MoveToTarget = 7,

    /// <summary>放料握手：下发放料请求，等待目标允许。</summary>
    PlaceHandshake = 8,

    /// <summary>放料确认：等待"目标有料 + 天车无料"等物理信号成立。</summary>
    PlaceConfirmed = 9,

    /// <summary>放料后回安全高度。</summary>
    RetractZAfterPlace = 10,

    /// <summary>回到本车安全位 X。</summary>
    ReturnToSafeX = 11,

    /// <summary>释放该任务持有的路权区间。</summary>
    ReleaseRightOfWay = 12,

    /// <summary>释放设备预约。</summary>
    ReleaseReservation = 13,

    /// <summary>任务完成，清空 CurrentTask 回到空闲。</summary>
    Complete = 14
}
