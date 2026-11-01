namespace DeviceGateway.Tests.Support;

/// <summary>
/// 并发测试的执行助手。
///
/// <para>
/// <b>为什么不用 <c>Task.Run</c> + <c>Barrier</c></b>：
/// 线程池注入新线程的速度很慢（每秒才一两个），
/// 几十个任务在 Barrier 上互相等，整个测试会等几十秒才跑完。
/// 用专用线程则能真正同时就位，测试既快又更接近真实并发。
/// </para>
/// </summary>
internal static class ConcurrentRunner
{
    /// <summary>
    /// 让 N 个动作真正同时开始执行，全部完成后返回。
    /// </summary>
    /// <param name="count">并发数。</param>
    /// <param name="action">
    /// 每个线程要执行的动作。参数是该线程的序号。
    /// 所有线程会在 <see cref="Barrier"/> 上等齐，然后一起开始。
    /// </param>
    public static void Run(int count, Action<int> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (count <= 0)
            return;

        // 参与者数是 count + 1：多出来的一个是主线程，
        // 它也参与等待，这样所有线程都能真正同时起跑。
        using var barrier = new Barrier(count + 1);

        var threads = new Thread[count];

        for (var i = 0; i < count; i++)
        {
            var index = i;

            threads[i] = new Thread(() =>
            {
                // 所有线程在这里等齐。
                barrier.SignalAndWait();

                action(index);
            })
            {
                IsBackground = true,
                Name = $"concurrency-{index}"
            };
        }

        foreach (var thread in threads)
        {
            thread.Start();
        }

        // 主线程也参与等待 —— 确保所有工作线程都已就位。
        barrier.SignalAndWait();

        foreach (var thread in threads)
        {
            thread.Join();
        }
    }

    /// <summary>
    /// 让 N 个动作真正同时开始执行，全部完成后返回各自的返回值。
    /// </summary>
    /// <typeparam name="T">返回值的类型。</typeparam>
    /// <param name="count">并发数。</param>
    /// <param name="action">每个线程要执行的动作，参数是线程序号。</param>
    /// <returns>各线程的返回值，按线程序号排列。</returns>
    public static T[] Run<T>(int count, Func<int, T> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (count <= 0)
            return Array.Empty<T>();

        var results = new T[count];

        Run(count, i => { results[i] = action(i); });

        return results;
    }
}
