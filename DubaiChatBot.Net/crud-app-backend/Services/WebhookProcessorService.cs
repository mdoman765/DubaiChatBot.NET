namespace crud_app_backend.Bot.Services
{
    /// <summary>
    /// Hosted background service — reads from WebhookQueue and processes messages.
    ///
    /// Runs up to 10 messages concurrently (across different users).
    /// Per-user message ordering is already handled by BotStateService SemaphoreSlim,
    /// so concurrent processing is safe — different users never interfere.
    ///
    /// Lifecycle: starts with the app, stops gracefully on shutdown.
    /// </summary>
    public class WebhookProcessorService : BackgroundService
    {
        private readonly WebhookQueue _queue;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<WebhookProcessorService> _logger;

        // Max messages processed concurrently across all users.
        // Per-user ordering is handled by BotStateService SemaphoreSlim separately.
        private const int MaxConcurrency = 10;

        public WebhookProcessorService(
            WebhookQueue queue,
            IServiceScopeFactory scopeFactory,
            ILogger<WebhookProcessorService> logger)
        {
            _queue = queue;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("[WebhookProcessor] Started — max concurrency={N}", MaxConcurrency);

            // Semaphore limits how many messages process simultaneously.
            // Without this, 1000 queued messages would all fire at once.
            var concurrencySemaphore = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);

            try
            {
                await foreach (var body in _queue.Reader.ReadAllAsync(stoppingToken))
                {
                    // Acquire concurrency slot — waits only if 10 are already running.
                    // Use stoppingToken so this unblocks cleanly on app shutdown.
                    await concurrencySemaphore.WaitAsync(stoppingToken);

                    var captured = body;

                    // FIX: do NOT pass stoppingToken to Task.Run.
                    // If the token is cancelled, Task.Run would cancel the task before
                    // it starts — Release() in finally would never run, leaking a slot.
                    // Once a task is started it should always run to completion.
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // Fresh DI scope per message — AppDbContext, BotService, etc.
                            // are scoped services; this scope owns their lifetime.
                            await using var scope = _scopeFactory.CreateAsyncScope();
                            var bot = scope.ServiceProvider.GetRequiredService<IBotService>();
                            await bot.ProcessAsync(captured);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[WebhookProcessor] ProcessAsync crashed");
                        }
                        finally
                        {
                            // FIX: guard against ObjectDisposedException.
                            // On app shutdown, ExecuteAsync exits and disposes the semaphore
                            // while in-flight tasks may still be running. Catch and ignore
                            // the disposal exception — it only happens during shutdown.
                            try { concurrencySemaphore.Release(); }
                            catch (ObjectDisposedException) { /* app shutting down — expected */ }
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
                // stoppingToken was cancelled — normal graceful shutdown
            }
            finally
            {
                // Dispose semaphore after ExecuteAsync loop exits
                concurrencySemaphore.Dispose();
            }

            _logger.LogInformation("[WebhookProcessor] Stopped.");
        }
    }
}
