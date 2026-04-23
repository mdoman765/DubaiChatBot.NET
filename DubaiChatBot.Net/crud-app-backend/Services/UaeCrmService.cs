using System.Text.Json;

namespace crud_app_backend.Bot.Services
{
    /// <summary>
    /// Submits complaints, returns and agent requests to:
    /// POST crm.prangroup.com/api/whats-app/sales-support/v1/create-uae-ticket
    /// ApiKey: uH6rJ3QpW9xN2Tz5K8bL (access-token header)
    /// </summary>
    public class UaeCrmService : IUaeCrmService
    {
        private readonly IHttpClientFactory _factory;
        private readonly IConfiguration _config;
        private readonly ILogger<UaeCrmService> _logger;

        public UaeCrmService(
            IHttpClientFactory factory,
            IConfiguration config,
            ILogger<UaeCrmService> logger)
        {
            _factory = factory;
            _config = config;
            _logger = logger;
        }

        public async Task<UaeCrmResult> SubmitAsync(
            UaeCrmRequest req, CancellationToken ct = default)
        {
            // NEW endpoint for UAE chatbot tickets
            var url = _config["Crm:UaeSubmitUrl"]
                   ?? "https://crm.prangroup.com/api/whats-app/sales-support/v1/create-uae-ticket";

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(55));

            try
            {
                // Required fields for this endpoint
                var body = new
                {
                    shopCode = req.ShopCode,
                    whatsappNumber = req.WhatsappNumber,
                    voiceFiles = req.VoiceFiles,
                    images = req.Images,
                    description = req.Description,
                    location = req.Location,
                    ticket_category = "UAE_Chatbot",   // required by this endpoint
                    cartItems = req.CartItems,
                    ticket_type = req.TicketType,   // API requires snake_case
                };

                _logger.LogInformation("[UaeCRM] POST {Url} ticketType={T} shop={S}",
                    url, req.TicketType, req.ShopCode);

                var client = _factory.CreateClient("CrmClient");
                var resp = await client.PostAsJsonAsync(url, body, cts.Token);
                var json = await resp.Content.ReadAsStringAsync(cts.Token);

                // Log full response for debugging
                _logger.LogInformation("[UaeCRM] {Code} response: {Body}",
                    (int)resp.StatusCode,
                    json.Length > 500 ? json[..500] : json);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Check success: status == "success" string
                // Response: {"status":"success","message":"...","data":{"id":101,...}}
                var statusVal = root.TryGetProperty("status", out var sv)
                    ? sv.GetString() : null;
                var isSuccess = statusVal?.ToLower() is "success" or "true" or "1"
                    || resp.IsSuccessStatusCode;

                if (!isSuccess)
                {
                    var errMsg = root.TryGetProperty("message", out var mv)
                        ? mv.GetString() : "Submission failed";
                    _logger.LogWarning("[UaeCRM] Failed: {Msg}", errMsg);
                    return new UaeCrmResult(false, null, errMsg);
                }

                // Extract ticket ID from data.id
                // data is an object: {"id": 101, "shop_code": "...", ...}
                string? ticketId = null;
                if (root.TryGetProperty("data", out var da) &&
                    da.ValueKind == JsonValueKind.Object &&
                    da.TryGetProperty("id", out var idEl) &&
                    idEl.ValueKind != JsonValueKind.Null)
                {
                    ticketId = idEl.ToString();
                }

                _logger.LogInformation("[UaeCRM] Success ticketId={T}", ticketId ?? "null");
                return new UaeCrmResult(true, ticketId, null);
            }
            catch (OperationCanceledException)
            {
                _logger.LogError("[UaeCRM] Timed out — type={T}", req.TicketType);
                return new UaeCrmResult(false, null,
                    "Support system is taking too long. Please try again.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[UaeCRM] Crashed — type={T}", req.TicketType);
                return new UaeCrmResult(false, null, "Could not reach support system.");
            }
        }
    }
}
