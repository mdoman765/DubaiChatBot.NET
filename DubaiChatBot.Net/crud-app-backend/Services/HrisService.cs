using System.Text.Json;

namespace crud_app_backend.Bot.Services
{
    /// <summary>
    /// Calls the PRAN HRIS API to verify a staff ID.
    ///   GET {Hris:BaseUrl}/v1/Staff/SDI?staffId={id}
    ///   Auth: Authorization: Basic ... + S_KEYSD header
    /// </summary>
    public class HrisService : IHrisService
    {
        private readonly IHttpClientFactory    _factory;
        private readonly IConfiguration        _config;
        private readonly ILogger<HrisService>  _logger;

        public HrisService(IHttpClientFactory factory, IConfiguration config,
            ILogger<HrisService> logger)
        {
            _factory = factory;
            _config  = config;
            _logger  = logger;
        }

        public async Task<HrisStaffData?> VerifyAsync(string staffId,
            CancellationToken ct = default)
        {
            try
            {
                var baseUrl = _config["Hris:BaseUrl"]
                              ?? "http://hrisapi.prangroup.com:8083";

                var client = _factory.CreateClient("Hris");
                var resp   = await client.GetAsync(
                    $"{baseUrl}/v1/Staff/SDI?staffId={Uri.EscapeDataString(staffId)}", ct);

                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[HRIS] HTTP {Code} for staffId={Id}",
                        (int)resp.StatusCode, staffId);
                    return null;
                }

                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc  = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // { "SUCCESS_CODE": "2000", "DATA": { "NAME": "...", "STATUS": "Active" } }
                if (!root.TryGetProperty("SUCCESS_CODE", out var codeProp) ||
                    codeProp.GetString() != "2000")
                {
                    _logger.LogInformation("[HRIS] staffId={Id} not found", staffId);
                    return null;  // not found
                }

                if (!root.TryGetProperty("DATA", out var data))
                    return null;

                var status = Str(data, "STATUS");

                if (!string.Equals(status, "Active", StringComparison.OrdinalIgnoreCase))
                {
                    // Found but inactive — return object with status so caller can show message
                    return new HrisStaffData { Status = status };
                }

                return new HrisStaffData
                {
                    Id           = Str(data, "ID"),
                    Name         = Str(data, "NAME"),
                    ContactNo    = Str(data, "CONTACT_NO"),
                    Email        = Str(data, "EMAIL"),
                    Designation  = Str(data, "DESIGNATION"),
                    Department   = Str(data, "DEPARTMENT"),
                    GroupName    = Str(data, "GROUP_NAME"),
                    Company      = Str(data, "COMPANY"),
                    LocationName = Str(data, "LOCATION_NAME"),
                    Status       = "Active",
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[HRIS] VerifyAsync error for staffId={Id}", staffId);
                return null;
            }
        }

        private static string Str(JsonElement el, string key) =>
            el.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";
    }
}
