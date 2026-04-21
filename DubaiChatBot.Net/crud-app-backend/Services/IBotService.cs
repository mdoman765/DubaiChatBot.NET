using System.Text.Json;

namespace crud_app_backend.Bot.Services
{
    public interface IBotService
    {
        /// <summary>
        /// Process one incoming 360dialog webhook payload.
        /// Called in a background Task — must never throw.
        /// </summary>
        Task ProcessAsync(JsonElement body);
    }
}
