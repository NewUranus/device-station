using DeviceStation.Core.Devices;
using Microsoft.Extensions.Logging;

namespace DeviceStation.Core.Jobs;

/// <summary>按 <see cref="StepPolicy"/> 执行一步：每次尝试单独限时，失败按次数重试。</summary>
internal static class StepRunner
{
    public static async Task RunAsync(JobStep step, DeviceContext ctx, DeviceSession session, CancellationToken deviceCt)
    {
        var policy = step.Policy;
        int maxAttempts = Math.Max(0, policy.MaxRetries) + 1;
        Exception? last = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ctx.Attempt = attempt;
            if (attempt > 1) session.SetAttempt(attempt, $"重试 {attempt - 1}/{policy.MaxRetries}");

            // 每次尝试一个独立的 CTS：既能被“停止本设备”取消，也能被本步超时取消，两者可以区分开。
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(deviceCt);
            if (policy.Timeout is { } timeout) attemptCts.CancelAfter(timeout);

            try
            {
                await step.Step.ExecuteAsync(ctx, attemptCts.Token).ConfigureAwait(false);
                if (attempt > 1)
                    ctx.Logger.LogInformation("步骤 {Step} 第 {Attempt} 次尝试成功", step.Name, attempt);
                return;
            }
            catch (OperationCanceledException) when (deviceCt.IsCancellationRequested)
            {
                throw; // 用户停止：不重试，直接向上传播
            }
            catch (OperationCanceledException) when (attemptCts.IsCancellationRequested)
            {
                last = new TimeoutException($"超时（{policy.Timeout!.Value.TotalMilliseconds:0} ms）");
            }
            catch (Exception ex)
            {
                last = ex;
            }

            if (attempt < maxAttempts)
            {
                ctx.Logger.LogWarning("步骤 {Step} 第 {Attempt}/{Max} 次失败：{Error}，{Delay} ms 后重试",
                    step.Name, attempt, maxAttempts, last.Message, policy.RetryDelay.TotalMilliseconds);
                await Task.Delay(policy.RetryDelay, deviceCt).ConfigureAwait(false);
            }
        }

        throw new StepFailedException(step.Name, maxAttempts, last!);
    }
}
