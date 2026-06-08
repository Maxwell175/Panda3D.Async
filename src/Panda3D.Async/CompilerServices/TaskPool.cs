using System.Threading;

namespace Panda3D.Async.CompilerServices;

/// <summary>
/// Lock-free singly-linked stack pool of <typeparamref name="T"/>.
/// Used to recycle state-machine runners and single-shot sources so
/// common async patterns don't allocate after warm-up.  Each
/// <typeparamref name="T"/> must expose a <c>NextNode</c> field that
/// TaskPool uses as the intrusive link — callers access via
/// <see cref="ITaskPoolNode{T}"/>.
/// </summary>
public static class TaskPool<T> where T : class, ITaskPoolNode<T> {
    static T? _root;
    static int _count;

    /// <summary>
    /// Upper bound on pool depth.  Excess returns are dropped on the
    /// floor so the pool never grows unboundedly for rarely-used
    /// runners.
    /// </summary>
    public static int MaxPoolSize = 256;

    public static bool TryPop(out T? result) {
        var v = _root;
        while (v is not null) {
            // Replace _root with v.NextNode.  If someone beat us to
            // it, retry.
            var next = v.NextNode;
            if (Interlocked.CompareExchange(ref _root, next, v) == v) {
                v.NextNode = null;
                Interlocked.Decrement(ref _count);
                result = v;
                return true;
            }
            v = _root;
        }
        result = null;
        return false;
    }

    public static bool TryPush(T item) {
        if (Volatile.Read(ref _count) >= MaxPoolSize) return false;
        while (true) {
            var head = _root;
            item.NextNode = head;
            if (Interlocked.CompareExchange(ref _root, item, head) == head) {
                Interlocked.Increment(ref _count);
                return true;
            }
        }
    }
}

/// <summary>
/// Link-field contract for pooled objects.  Allows
/// <see cref="TaskPool{T}"/> to thread them into an intrusive stack
/// without needing a generic wrapper node.
/// </summary>
public interface ITaskPoolNode<T> where T : class {
    T? NextNode { get; set; }
}
