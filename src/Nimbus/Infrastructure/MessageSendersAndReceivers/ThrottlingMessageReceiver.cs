using System;
using System.Threading;
using System.Threading.Tasks;
using Nimbus.Configuration.Settings;
using Nimbus.Extensions;
using Nimbus.Infrastructure.Heartbeat.PerformanceCounters;

namespace Nimbus.Infrastructure.MessageSendersAndReceivers
{
    internal abstract class ThrottlingMessageReceiver : INimbusMessageReceiver
    {
        protected readonly ConcurrentHandlerLimitSetting ConcurrentHandlerLimit;
        private readonly ILogger _logger;
        private readonly IGlobalHandlerThrottle _globalHandlerThrottle;
        private readonly SemaphoreSlim _localHandlerThrottle;
        private bool _running;

        private Task _workerTask;
        private readonly SemaphoreSlim _startStopSemaphore = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        protected ThrottlingMessageReceiver(ConcurrentHandlerLimitSetting concurrentHandlerLimit, IGlobalHandlerThrottle globalHandlerThrottle, ILogger logger)
        {
            ConcurrentHandlerLimit = concurrentHandlerLimit;
            _logger = logger;
            _globalHandlerThrottle = globalHandlerThrottle;
            _localHandlerThrottle = new SemaphoreSlim(concurrentHandlerLimit, concurrentHandlerLimit);
        }

        public async Task Start(Func<NimbusMessage, Task> callback)
        {
            await _startStopSemaphore.WaitAsync();

            try
            {
                if (_running) return;
                _running = true;

                await WarmUp();

                _cancellationTokenSource = new CancellationTokenSource();

                _workerTask = Task.Run(() => Worker(callback), _cancellationTokenSource.Token).ConfigureAwaitFalse();
            }
            finally
            {
                _startStopSemaphore.Release();
            }
        }

        public async Task Stop()
        {
            await _startStopSemaphore.WaitAsync();

            try
            {
                if (!_running) return;
                _running = false;

                _cancellationTokenSource?.Cancel();
                _cancellationTokenSource = null;

                try
                {
                    await _workerTask;
                }
                catch (OperationCanceledException)
                {
                    // this will be thrown by the tasks that get cancelled.
                }
            }
            finally
            {
                _startStopSemaphore.Release();
            }
        }

        protected abstract Task WarmUp();

        protected abstract Task<NimbusMessage> Fetch(CancellationToken cancellationToken);

        private async Task Worker(Func<NimbusMessage, Task> callback)
        {
            while (true)
            {
                if (!_running) break;
                if (_cancellationTokenSource.IsCancellationRequested) break;

                try
                {
                    var message = await Fetch(_cancellationTokenSource.Token);

                    if (message == null)
                    {
                        if (!_cancellationTokenSource.IsCancellationRequested)
                        {
                            // if we're shutting down, we're fine if we received a null message - we asked to quit, after all...
                            _logger.Debug($"Call to {nameof(Fetch)} returned null on {{QueueOrTopic}}. Retrying...", ToString());
                        }
                        continue;
                    }

                    _logger.Debug("Waiting for handler semaphores...");
                    await _localHandlerThrottle.WaitAsync(_cancellationTokenSource.Token);
                    await _globalHandlerThrottle.Wait(_cancellationTokenSource.Token);
                    _logger.Debug("Acquired handler semaphores...");

#pragma warning disable 4014
                    Task.Run(async () =>
                                   {
                                       try
                                       {
                                           await callback(message);
                                           GlobalMessageCounters.IncrementReceivedMessageCount(1);
                                       }
                                       finally
                                       {
                                           _globalHandlerThrottle.Release();
                                           _localHandlerThrottle.Release();
                                       }
                                   }).ConfigureAwaitFalse();
#pragma warning restore 4014
                }
                catch (OperationCanceledException)
                {
                    // will be thrown when someone calls .Stop() on us
                    break;
                }
                catch (Exception exc)
                {
                    _logger.Error(exc, "Worker exception in {0} for {1}", GetType().Name, this);
                }
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing) return;

            try
            {
                // ReSharper disable CSharpWarnings::CS4014
#pragma warning disable 4014
                Stop();
#pragma warning restore 4014
                // ReSharper restore CSharpWarnings::CS4014
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}