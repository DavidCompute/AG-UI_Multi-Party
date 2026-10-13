namespace AguiGroupChat.Hub.Messaging;

/// <summary>
/// 群事件观察者（出站集成钩子）：<see cref="GroupHub"/> 每次向群扇出事件时<b>同步</b>调用一次，
/// 供平台之外的系统订阅（如 Webhook 出站推送）。接口定义在 Hub，实现放在 Web（组合根注册），
/// 与 <c>IAttachmentLifecycle</c> 同属「Hub 只定义、Web 落地」的扩展模式。
///
/// 实现约定：<see cref="OnEvent"/> 在实时扇出路径上被调用（每条消息、每个流式增量都会命中），
/// 实现必须<b>立即返回</b>——内部入队 / 后台线程做真正的推送，绝不能在此发起网络 I/O 或阻塞。
/// 该方法抛出的异常会被 <see cref="GroupHub"/> 吞掉，不会影响正常扇出。
/// </summary>
public interface IGroupEventObserver
{
    /// <summary>收到一条群事件。<paramref name="evt"/> 为既有事件对象（形如 <c>TextMessageEndEvent</c>，含 <c>Type</c> 属性）。</summary>
    void OnEvent(string groupId, object evt);
}
