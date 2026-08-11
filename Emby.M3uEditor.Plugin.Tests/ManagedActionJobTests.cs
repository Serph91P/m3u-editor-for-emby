using System;
using System.Threading;
using System.Threading.Tasks;
using Emby.M3uEditor.Plugin.Api;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class ManagedActionJobTests
    {
        [Fact]
        public async Task TryStart_AdmitsActionWithoutWaitingForCompletion()
        {
            var completion = new TaskCompletionSource<ManagedActionResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var coordinator = new ManagedActionJobCoordinator(TimeSpan.FromMinutes(1));

            var admitted = coordinator.TryStart("reconcile", null, token => completion.Task);

            Assert.True(admitted.Accepted);
            Assert.Equal("accepted", admitted.State);
            Assert.Equal("running", coordinator.GetStatus().State);

            completion.SetResult(new ManagedActionResult { Success = true, Message = "Completed." });
            await WaitForState(coordinator, "succeeded");
        }

        [Fact]
        public async Task TryStart_WhileRunning_ReturnsSameJobWithoutStartingAnotherMutation()
        {
            var completion = new TaskCompletionSource<ManagedActionResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var coordinator = new ManagedActionJobCoordinator(TimeSpan.FromMinutes(1));
            var invocations = 0;
            Func<CancellationToken, Task<ManagedActionResult>> operation = token =>
            {
                Interlocked.Increment(ref invocations);
                return completion.Task;
            };
            var first = coordinator.TryStart("reconcile", null, operation);
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref invocations) == 1, 1000));

            var duplicate = coordinator.TryStart("rollback", "mapping-2", operation);

            Assert.False(duplicate.Accepted);
            Assert.True(duplicate.Duplicate);
            Assert.Equal(first.JobId, duplicate.JobId);
            Assert.Equal("reconcile", duplicate.Action);
            Assert.Equal(1, Volatile.Read(ref invocations));

            completion.SetResult(new ManagedActionResult { Success = true });
            await WaitForState(coordinator, "succeeded");
        }

        [Fact]
        public async Task TryStart_UnsuccessfulAction_ReportsTerminalFailure()
        {
            var coordinator = new ManagedActionJobCoordinator(TimeSpan.FromMinutes(1));

            coordinator.TryStart("rollback", "mapping-1", token => Task.FromResult(
                new ManagedActionResult { Success = false, Message = "No previous generation." }));

            var failed = await WaitForState(coordinator, "failed");
            Assert.False(failed.Result.Success);
            Assert.Equal("No previous generation.", failed.Result.Message);
        }

        [Fact]
        public async Task TryStart_TimedOutAction_ReportsBoundedTerminalFailure()
        {
            var coordinator = new ManagedActionJobCoordinator(TimeSpan.FromMilliseconds(20));

            coordinator.TryStart("reconcile", null, async token =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return new ManagedActionResult { Success = true };
            });

            var failed = await WaitForState(coordinator, "failed");
            Assert.Contains("timed out", failed.Result.Message);
        }

        [Fact]
        public void ToManagedActionAdmission_AcceptedJob_PreservesNonBlockingContract()
        {
            var response = M3uEditorApi.ToManagedActionAdmission(new ManagedJobStatus
            {
                JobId = "job-1",
                State = "accepted",
                Accepted = true
            });

            Assert.True(response.Success);
            Assert.True(response.Accepted);
            Assert.False(response.Duplicate);
            Assert.Equal("job-1", response.JobId);
            Assert.Equal("accepted", response.State);
        }

        private static async Task<ManagedJobStatus> WaitForState(
            ManagedActionJobCoordinator coordinator,
            string expected)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var status = coordinator.GetStatus();
                if (status.State == expected)
                {
                    return status;
                }

                await Task.Delay(10);
            }

            Assert.Equal(expected, coordinator.GetStatus().State);
            return coordinator.GetStatus();
        }
    }
}
