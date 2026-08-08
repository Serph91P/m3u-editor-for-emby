using System;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.Xtream.Plugin.Api
{
    public class ManagedJobStatus
    {
        public string JobId { get; set; }
        public string Action { get; set; }
        public string Target { get; set; }
        public string State { get; set; }
        public bool Accepted { get; set; }
        public bool Duplicate { get; set; }
        public DateTime? StartedUtc { get; set; }
        public DateTime? CompletedUtc { get; set; }
        public ManagedActionResult Result { get; set; }
    }

    internal sealed class ManagedActionJobCoordinator
    {
        private readonly object _gate = new object();
        private readonly TimeSpan _timeout;
        private ManagedJobStatus _status = new ManagedJobStatus { State = "idle" };

        public ManagedActionJobCoordinator(TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            _timeout = timeout;
        }

        public ManagedJobStatus TryStart(
            string action,
            string target,
            Func<CancellationToken, Task<ManagedActionResult>> operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            ManagedJobStatus admitted;
            lock (_gate)
            {
                if (string.Equals(_status.State, "running", StringComparison.Ordinal))
                {
                    var duplicate = Clone(_status);
                    duplicate.Duplicate = true;
                    return duplicate;
                }

                var jobId = Guid.NewGuid().ToString("N");
                _status = new ManagedJobStatus
                {
                    JobId = jobId,
                    Action = action,
                    Target = target,
                    State = "running",
                    StartedUtc = DateTime.UtcNow
                };
                admitted = Clone(_status);
                admitted.Accepted = true;
                admitted.State = "accepted";
                Task.Run(() => RunAsync(jobId, operation));
            }

            return admitted;
        }

        public ManagedJobStatus GetStatus()
        {
            lock (_gate)
            {
                return Clone(_status);
            }
        }

        private async Task RunAsync(
            string jobId,
            Func<CancellationToken, Task<ManagedActionResult>> operation)
        {
            ManagedActionResult result;
            using (var cancellation = new CancellationTokenSource(_timeout))
            {
                try
                {
                    result = await operation(cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    result = new ManagedActionResult
                    {
                        Success = false,
                        Message = "Managed action timed out or was cancelled."
                    };
                }
                catch (Exception)
                {
                    result = new ManagedActionResult
                    {
                        Success = false,
                        Message = "Managed action failed unexpectedly."
                    };
                }
            }

            lock (_gate)
            {
                if (!string.Equals(_status.JobId, jobId, StringComparison.Ordinal))
                {
                    return;
                }

                _status.State = result != null && result.Success ? "succeeded" : "failed";
                _status.Result = result ?? new ManagedActionResult
                {
                    Success = false,
                    Message = "Managed action returned no result."
                };
                _status.CompletedUtc = DateTime.UtcNow;
            }
        }

        private static ManagedJobStatus Clone(ManagedJobStatus value)
        {
            return new ManagedJobStatus
            {
                JobId = value.JobId,
                Action = value.Action,
                Target = value.Target,
                State = value.State,
                Accepted = value.Accepted,
                Duplicate = value.Duplicate,
                StartedUtc = value.StartedUtc,
                CompletedUtc = value.CompletedUtc,
                Result = value.Result
            };
        }
    }
}
